using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Extraction;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Limits;
using SecondBrain.Core.Retrieval;
using SecondBrain.Server.Http;
using SecondBrain.Server.Auth;

namespace SecondBrain.Server.Ingestion;

public static class KnowledgeEndpoints
{
    public const long MaximumUploadBytes = 50 * 1024 * 1024;
    public const int MaximumJsonBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 16, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        foreach (var prefix in new[] { "", "/v1" })
        {
            endpoints.MapPost(prefix + "/documents", CaptureAsync)
                .Accepts<CaptureRequest>("application/json", "multipart/form-data")
                .AddOpenApiOperationTransformer((operation, _, _) =>
                {
                    operation.RequestBody!.Content!["multipart/form-data"] = new OpenApiMediaType
                    {
                        Schema = new OpenApiSchema
                        {
                            Type = JsonSchemaType.Object,
                            Required = new HashSet<string> { "file" },
                            Properties = new Dictionary<string, IOpenApiSchema>
                            {
                                ["file"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" },
                                ["metadata"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "JSON capture metadata: title, type, props, tags and source_url; content is ignored." }
                            }
                        },
                        Encoding = new Dictionary<string, OpenApiEncoding> { ["metadata"] = new() { ContentType = "application/json" } }
                    };
                    return Task.CompletedTask;
                })
                .WithMetadata(new EndpointPolicy("documents.create"), new AdmissionPolicy(AdmissionResource.Upload, QueuedJobs: 1, AdmittedBytes: MaximumUploadBytes + 65536, Ingestion: true))
                .Produces<CaptureResult>(201).Produces<CaptureResult>(200).Produces<CaptureResult>(202).ProducesProblem(400).ProducesProblem(413).ProducesProblem(422);
            endpoints.MapGet(prefix + "/documents/{id}", ReadAsync).WithMetadata(new EndpointPolicy("documents.read")).Produces<DocumentRecord>().ProducesProblem(404);
            endpoints.MapGet(prefix + "/files/{id}", DownloadAsync).WithMetadata(new EndpointPolicy("documents.read")).Produces(200, contentType: "application/octet-stream").ProducesProblem(404);
            endpoints.MapGet(prefix + "/jobs", JobsAsync).WithMetadata(new EndpointPolicy("jobs.read")).Produces<IReadOnlyList<ProcessingJob>>();
            endpoints.MapGet(prefix + "/jobs/{id}", JobAsync).WithMetadata(new EndpointPolicy("jobs.read")).Produces<ProcessingJob>().ProducesProblem(404);
            endpoints.MapPost(prefix + "/jobs/{id}/retry", RetryJobAsync).WithMetadata(new EndpointPolicy("jobs.retry")).Produces<ProcessingJob>().ProducesProblem(409);
            endpoints.MapPost(prefix + "/search", SearchAsync).Accepts<SearchRequest>("application/json").WithMetadata(new EndpointPolicy("search"), new AdmissionPolicy(AdmissionResource.Search)).Produces<SearchResponse>().ProducesProblem(400);
            endpoints.MapPost(prefix + "/query", QueryAsync).Accepts<QueryRequest>("application/json").WithMetadata(new EndpointPolicy("query"), new AdmissionPolicy(AdmissionResource.Search)).Produces<QueryPage>().ProducesProblem(400);
            endpoints.MapPatch(prefix + "/documents/{id}", MutateAsync).WithMetadata(new EndpointPolicy("documents.mutate")).Produces<CaptureResult>().Produces<PendingOperationResponse>(202).ProducesProblem(412);
            endpoints.MapDelete(prefix + "/documents/{id}", DeleteAsync).WithMetadata(new EndpointPolicy("documents.mutate")).Produces<CaptureResult>().Produces<PendingOperationResponse>(202).ProducesProblem(412);
            endpoints.MapPost(prefix + "/documents/{id}/restore", RestoreAsync).WithMetadata(new EndpointPolicy("documents.mutate", SessionOnly: true)).Produces<CaptureResult>();
            endpoints.MapPost(prefix + "/documents/{id}/purge", PurgeAsync).WithMetadata(new EndpointPolicy("documents.purge", SessionOnly: true, StepUp: true)).Produces<CaptureResult>();
            endpoints.MapPost(prefix + "/maintenance/rebuild", RebuildAsync).WithMetadata(new EndpointPolicy("maintenance.rebuild", StepUp: true)).Produces<object>();
        }
    }

    private static async Task<IngestionAuthority> AuthorityAsync(HttpContext context, IAuthRepository auth, CancellationToken token)
    {
        var identity = (AuthenticatedCredential)context.Items[typeof(AuthenticatedCredential)]!;
        if (!await auth.IsCurrentAsync(identity.Id, identity.Generation, identity.AccountEpoch, token)) throw new AuthorityChangedException();
        var row = await auth.FindAsync(identity.Id, token) ?? throw new AuthorityChangedException();
        return new(identity.Id, identity.Generation, identity.AccountEpoch, row.Name, identity.Kind);
    }

    private static async Task<IResult> CaptureAsync(HttpContext context, IDocumentRepository documents, IAuthRepository auth,
        IInputStager stager, EmbeddingOrchestrator embeddings, FileProcessingOrchestrator files, CancellationToken token)
    {
        try
        {
            var wait = 0;
            if (context.Request.Query.TryGetValue("wait", out var value) && (!int.TryParse(value, out wait) || wait is < 0 or > 30)) return Invalid("invalid-wait");
            if (context.Request.Headers["Idempotency-Key"].Count > 1) return Invalid("invalid-idempotency-key");
            var key = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
            if (key is not null && (string.IsNullOrWhiteSpace(key) || key.Length > 200)) return Invalid("invalid-idempotency-key");
            var authority = await AuthorityAsync(context, auth, token);
            CaptureResult result;
            if (context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true)
            {
                var request = await ReadJsonAsync<CaptureRequest>(context.Request.Body, MaximumJsonBytes, token);
                result = await documents.CaptureAsync(request, authority, key, token);
            }
            else if (context.Request.ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) == true)
            {
                result = await UploadAsync(context, documents, stager, authority, key, token);
                result = await files.WaitAsync(result, wait, token);
                context.Response.Headers.ETag = "\"" + result.Document.Revision + "\"";
                return Results.Json(Served(result), statusCode: wait == 0 ? 201 : result.Document.Status is "indexed" or "indexed_partial" ? 200 : 202);
            }
            else return Results.Problem(statusCode: 415, type: ProblemResponses.TypeForStatus(415), title: "Use JSON capture or multipart file upload.");
            result = await embeddings.TryCompleteAsync(result, token);
            context.Response.Headers.ETag = "\"" + result.Document.Revision + "\"";
            return Results.Json(Served(result), statusCode: wait == 0 ? 201 : result.Document.Status is "indexed" or "indexed_partial" ? 200 : 202);
        }
        catch (IngestionConflictException ex) { return Failure(ex.Code, ex.Code == "idempotency-key-reuse" ? 422 : ex.Code == "capacity" ? 503 : 409); }
        catch (InvalidDataException) { return Failure("input-limit", 413); }
        catch (JsonException) { return Invalid("invalid-json"); }
        catch (FormatException) { return Invalid("invalid-multipart"); }
        catch (ArgumentException ex) { return Invalid(ex.Message); }
    }

    private static async Task<CaptureResult> UploadAsync(HttpContext context, IDocumentRepository documents,
        IInputStager stager, IngestionAuthority authority, string? key, CancellationToken token)
    {
        var mediaType = MediaTypeHeaderValue.Parse(context.Request.ContentType!);
        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 128) throw new ArgumentException("invalid-multipart-boundary");
        var reader = new MultipartReader(boundary, context.Request.Body) { BodyLengthLimit = MaximumUploadBytes, HeadersCountLimit = 16, HeadersLengthLimit = 8192 };
        StagedInput? staged = null;
        CaptureRequest metadata = new("");
        var seenMetadata = false;
        string? fileName = null;
        try
        {
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(token)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)) throw new ArgumentException("invalid-multipart-disposition");
                var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
                if (name == "metadata" && !seenMetadata)
                {
                    metadata = await ReadJsonAsync<CaptureRequest>(section.Body, 65536, token);
                    seenMetadata = true;
                }
                else if (name == "file" && fileName is null && disposition.IsFileDisposition())
                {
                    fileName = Path.GetFileName(HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value);
                    if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255) throw new ArgumentException("invalid-file-name");
                    staged = await stager.StageAsync(section.Body, fileName, token);
                }
                else throw new ArgumentException("invalid-multipart-parts");
            }
            if (fileName is null) throw new ArgumentException("file-required");
            staged!.Content.Position = 0;
            // Admission persists the original and a fenced job before any sandbox work begins.
            var bytes = new byte[checked((int)staged.Length)];
            await staged.Content.ReadExactlyAsync(bytes, token);
            return await documents.AdmitFileAsync(bytes, metadata, fileName, authority, key, token);
        }
        finally { if (staged is not null) await staged.DisposeAsync(); }
    }

    private static async Task<IResult> ReadAsync(string id, HttpContext context, IDocumentRepository documents, CancellationToken token)
    {
        var includes = context.Request.Query["include"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (includes.Any(value => value is not ("text" or "chunks"))) return Invalid("unsupported-include");
        var text = includes.Length > 0;
        // Like downloads, retained projections of an unavailable document are never served; the status check
        // precedes loading them and is repeated on the loaded record in case the status changed in between.
        var result = await documents.GetAsync(id, false, token);
        if (result is null || text && DocumentStatusNames.IsUnavailable(result.Status)) return Results.NotFound();
        if (text && ((result = await documents.GetAsync(id, true, token)) is null || DocumentStatusNames.IsUnavailable(result.Status))) return Results.NotFound();
        context.Response.Headers.ETag = "\"" + result.Revision + "\"";
        return Results.Ok(result);
    }
    private static async Task<IResult> DownloadAsync(string id, HttpContext context, IDocumentRepository documents, IManagedDocumentFiles files, CancellationToken token)
    {
        var document = await documents.GetAsync(id, true, token);
        if (document?.Path is null || DocumentStatusNames.IsUnavailable(document.Status)) return Results.NotFound();
        try
        {
            var stream = await files.OpenReadAsync(document.Path, token);
            context.Response.Headers.ETag = "\"" + document.Revision + "\"";
            context.Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
            return Results.File(stream, "application/octet-stream", Path.GetFileName(document.Path));
        }
        catch (IOException) { return Results.NotFound(); }
    }

    private static async Task<IResult> JobsAsync(string? status, IDocumentRepository documents, CancellationToken token) => Results.Ok(await documents.JobsAsync(status, token));
    private static async Task<IResult> JobAsync(string id, IDocumentRepository documents, CancellationToken token)
    {
        var job = await documents.JobAsync(id, token);
        return job is null ? Results.NotFound() : Results.Ok(job);
    }
    private static async Task<IResult> RetryJobAsync(string id, HttpContext context, IDocumentRepository documents, IAuthRepository auth, FileProcessingOrchestrator files, CancellationToken token)
    {
        try
        {
            var result = await documents.RetryJobAsync(id, await AuthorityAsync(context, auth, token), token);
            files.Notify();
            return Results.Ok(result);
        }
        catch (IngestionConflictException ex) { return Failure(ex.Code, ex.Code == "not-found" ? 404 : ex.Code == "capacity" ? 503 : 409); }
    }
    private static async Task<IResult> RebuildAsync(IDocumentRepository documents, CancellationToken token) => Results.Ok(new { documents = await documents.RebuildAsync(token) });

    private static async Task<IResult> SearchAsync(HttpContext context, IRetrievalRepository retrieval, EmbeddingProcessor embeddings, SearchReranker reranker, IAuthRepository auth, CancellationToken token)
    {
        try
        {
            var request = await ReadJsonAsync<SearchRequest>(context.Request.Body, 65536, token);
            if (string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > 8192 || request.Mode is not ("keyword" or "semantic" or "hybrid") || request.Limit is < 1 or > 200 || request.Rerank is not ("none" or "llm" or "provider")) return Invalid("invalid-search");
            RetrievalFilterValidation.Validate(request.Filters);
            await AuthorityAsync(context, auth, token);
            (float[]? Vector, string? SpaceId) vector = (null, null);
            if (request.Mode != "keyword")
            {
                try { vector = await embeddings.QueryAsync(request.Query, token); }
                catch (Exception ex) when (EmbeddingOrchestrator.IsProviderFailure(ex)) { }
            }
            await AuthorityAsync(context, auth, token);
            var fallback = request.Mode != "keyword" && vector.Vector is null;
            var retrievalRequest = request with { Mode = fallback ? "keyword" : request.Mode, Limit = request.Rerank == "none" ? request.Limit : Math.Max(20, request.Limit) };
            var response = await retrieval.SearchAsync(retrievalRequest, vector.Vector, vector.SpaceId, token);
            response = await reranker.ApplyAsync(request, response, token);
            await AuthorityAsync(context, auth, token);
            await retrieval.RecordTraceAsync(request, response, token);
            return Results.Ok(fallback ? response with { Degraded = response.Degraded.Concat(new[] { "embed-unavailable" }).Distinct(StringComparer.Ordinal).ToArray() } : response);
        }
        catch (ArgumentException ex) { return Invalid(ex.Message); }
        catch (JsonException) { return Invalid("invalid-json"); }
        catch (InvalidDataException) { return Failure("input-limit", 413); }
    }
    private static async Task<IResult> QueryAsync(HttpContext context, IRetrievalRepository retrieval, IAuthRepository auth, CancellationToken token)
    {
        try
        {
            var request = await ReadJsonAsync<QueryRequest>(context.Request.Body, 65536, token);
            RetrievalFilterValidation.Validate(new RetrievalFilters(request.Type, request.Tags, request.Source, request.PathPrefix, request.Mime, request.Occurred, request.Updated, request.Predicates, request.People));
            return Results.Ok(await retrieval.QueryAsync(request, await AuthorityAsync(context, auth, token), token));
        }
        catch (ArgumentException ex) { return Invalid(ex.Message); }
        catch (JsonException) { return Invalid("invalid-json"); }
        catch (InvalidDataException) { return Failure("input-limit", 413); }
    }
    private static Task<IResult> MutateAsync(string id, HttpContext context, IDocumentRepository documents, IAuthRepository auth, CancellationToken token) => MutationAsync(id, "update", context, documents, auth, token);
    private static Task<IResult> DeleteAsync(string id, HttpContext context, IDocumentRepository documents, IAuthRepository auth, IOptionsMonitor<SecondBrainOptions> options, TimeProvider clock, CancellationToken token)
    {
        var purge = context.Request.Query["purge"].ToString();
        if (purge is not ("" or "0" or "1" or "false" or "true")) return Task.FromResult(Invalid("invalid-purge"));
        if (purge is "1" or "true")
        {
            var identity = context.Items[typeof(AuthenticatedCredential)] as AuthenticatedCredential;
            if (identity is null || identity.Kind != "session" || !identity.Scopes.Contains(SecondBrain.Core.Authorization.Scope.Admin) || !RequestAuthorizationMiddleware.HasFreshStepUp(identity, options.CurrentValue, clock.GetUtcNow()))
                return Task.FromResult(Failure("scope-denied", 403));
            return MutationAsync(id, "purge", context, documents, auth, token);
        }
        return MutationAsync(id, "delete", context, documents, auth, token);
    }
    private static Task<IResult> RestoreAsync(string id, HttpContext context, IDocumentRepository documents, IAuthRepository auth, CancellationToken token) => MutationAsync(id, "restore", context, documents, auth, token);
    private static Task<IResult> PurgeAsync(string id, HttpContext context, IDocumentRepository documents, IAuthRepository auth, CancellationToken token) => MutationAsync(id, "purge", context, documents, auth, token);
    private static async Task<IResult> MutationAsync(string id, string operation, HttpContext context, IDocumentRepository documents, IAuthRepository auth, CancellationToken token)
    {
        if (!long.TryParse(context.Request.Headers.IfMatch.ToString().Trim('"'), out var revision) || revision < 1) return Failure("revision-required", 400);
        try
        {
            var payload = operation is "delete" or "restore" or "purge" ? JsonSerializer.SerializeToElement(new { }) : await ReadJsonAsync<JsonElement>(context.Request.Body, MaximumJsonBytes, token);
            var result = await documents.MutateAsync(id, revision, operation, payload, await AuthorityAsync(context, auth, token), token);
            if (result.Document.Status == "proposed") return Results.Json(new PendingOperationResponse(result.Job!.Id), statusCode: 202);
            context.Response.Headers.ETag = "\"" + result.Document.Revision + "\"";
            return Results.Ok(Served(result));
        }
        catch (IngestionConflictException ex)
        {
            if (ex.Code == "revision-mismatch")
            {
                var current = await documents.GetAsync(id, cancellationToken: token);
                return Results.Problem(statusCode: 412, type: "https://secondbrain.dev/problems/revision-mismatch", title: ex.Code,
                    extensions: new Dictionary<string, object?> { ["current_revision"] = current?.Revision });
            }
            return Failure(ex.Code, ex.Code == "not-found" ? 404 : ex.Code == "capacity" ? 503 : 409);
        }
        catch (ArgumentException ex) { return Invalid(ex.Message); }
        catch (JsonException) { return Invalid("invalid-json"); }
        catch (InvalidDataException) { return Failure("input-limit", 413); }
    }
    // Responses carry the same projections a read would: none for a document that is no longer served.
    private static CaptureResult Served(CaptureResult result) => DocumentStatusNames.IsUnavailable(result.Document.Status)
        ? result with { Document = result.Document with { Path = null, Text = null, Chunks = null, Tags = null } }
        : result;
    private static IResult Invalid(string code) => Failure(code, 400);
    private static IResult Failure(string code, int status) => Results.Problem(statusCode: status, type: "https://secondbrain.dev/problems/" + Uri.EscapeDataString(code), title: code);
    private static async Task<T> ReadJsonAsync<T>(Stream source, int maximumBytes, CancellationToken token)
    {
        using var bounded = new MemoryStream();
        await CopyBoundedAsync(source, bounded, maximumBytes, token);
        bounded.Position = 0;
        return await JsonSerializer.DeserializeAsync<T>(bounded, Json, token) ?? throw new JsonException("Missing request.");
    }
    private static async Task CopyBoundedAsync(Stream source, Stream destination, long maximumBytes, CancellationToken token)
    {
        var buffer = new byte[65536];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, token)) != 0)
        {
            total += read;
            if (total > maximumBytes) throw new InvalidDataException("input-limit");
            await destination.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }
}

public sealed record PendingOperationResponse([property: System.Text.Json.Serialization.JsonPropertyName("pending_id")] string PendingId);
