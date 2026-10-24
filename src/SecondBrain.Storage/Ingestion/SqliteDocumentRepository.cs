using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SecondBrain.Core.Domain;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Ingestion;
using SecondBrain.Storage.Durability;
using SecondBrain.Extraction.Contracts;

namespace SecondBrain.Storage.Ingestion;

public sealed partial class SqliteDocumentRepository(IStateStore state, IIndexStore index, IPublicationCoordinator coordinator, IManagedDocumentFiles? files = null, Microsoft.Extensions.Options.IOptions<SecondBrain.Core.Configuration.SecondBrainOptions>? options = null) : IDocumentRepository
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IStateStore, SemaphoreSlim> CaptureGates = new();
    private readonly SemaphoreSlim captureGate = CaptureGates.GetValue(state, static _ => new SemaphoreSlim(1, 1));
    private const string Generation = "{\"chunker\":1,\"extractor\":1,\"normalization\":1}";
    public async ValueTask<CaptureResult> CaptureAsync(CaptureRequest request, IngestionAuthority authority, string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        await captureGate.WaitAsync(cancellationToken);
        try { return await CaptureCoreAsync(request, authority, idempotencyKey, cancellationToken); }
        finally { captureGate.Release(); }
    }

    private async ValueTask<CaptureResult> CaptureCoreAsync(CaptureRequest request, IngestionAuthority authority, string? idempotencyKey, CancellationToken cancellationToken, SecondBrain.Core.Chat.ApprovedDocumentWrite? approved = null)
    {
        Validate(request);
        if (authority.Door is not ("api" or "mcp" or "ui")) throw new ArgumentException("Invalid capture surface.");
        var requestHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
        if (authority.Door != "api") requestHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { request, authority.Door })));
        if (idempotencyKey is { Length: > 200 }) throw new ArgumentException("Idempotency key exceeds bound.");
        var id = approved?.Plan.DocumentId ?? Ulid.NewUlid().ToString();
        authority = await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            var row = await SecondBrain.Storage.Auth.IngestionAuthorityValidator.ValidateAsync(connection, transaction, authority,
                SecondBrain.Core.Authorization.Scope.Write, DateTimeOffset.UtcNow, cancellationToken: token);
            return authority with { CredentialName = row.Name };
        }, cancellationToken);
        string? replay = null;
        await using (var lease = await state.OpenReadConnectionAsync(cancellationToken))
        await using (var read = Command(lease.Connection, null, "SELECT payload_hash,response_json FROM idempotency WHERE key=$key AND credential_id=$actor", ("$key", idempotencyKey), ("$actor", authority.CredentialId)))
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
            if (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetString(0) != requestHash) throw new IngestionConflictException("idempotency-key-reuse");
                replay = reader.GetString(1);
            }
        if (replay is not null)
        {
            var previous = await GetAsync(replay, true, cancellationToken);
            if (previous is null) throw new IngestionConflictException("capture-pending");
            return new CaptureResult(previous, (await JobsAsync(null, cancellationToken)).FirstOrDefault(job => job.DocumentId == replay));
        }
        await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await using var source = Command(connection, transaction,
                "INSERT OR IGNORE INTO sources(id,kind,name,created_at) VALUES($source,$kind,$name,$now)",
                ("$source", authority.Door + "-captures"), ("$kind", authority.Door), ("$name", authority.Door.ToUpperInvariant() + " captures"), ("$now", Now()));
            return await source.ExecuteNonQueryAsync(token);
        }, cancellationToken);
        var jobId = approved?.ExecutionId ?? Ulid.NewUlid().ToString();
        var title = request.Title ?? FirstLine(request.Content);
        var managedPath = approved is null ? files?.GetManagedPath(id, ".md") : files?.GetAssistantNotePath(id);
        var projection = TextProcessor.Process(id, title, request.Content, request.Tags, managedPath);
        var props = request.Props?.GetRawText() ?? "{}";
        using var timeProperties = JsonDocument.Parse(props);
        var basis = new TypeRegistry().GetProcessingType(request.Type ?? "note").Metadata.OccurredBasis;
        var declared = basis is not null && timeProperties.RootElement.TryGetProperty(basis, out var timeValue) && timeValue.ValueKind == JsonValueKind.String ? timeValue.GetString() : null;
        var occurred = basis is null ? null : SourceTime.Parse(declared, basis, TimeZoneInfo.FindSystemTimeZoneById(options?.Value.Time.Zone ?? TimeZoneInfo.Local.Id));
        var publication = new DocumentPublication(id, authority.Door + "-captures", 0, Generation, "capture", title,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.Content))), authority.Door + ":" + authority.CredentialName)
        { Type = request.Type ?? "note", TypeOrigin = request.Type is null ? "format-default" : "door", PropsJson = props,
            PropsOriginalJson = props, Mime = "text/plain", SourceUrl = request.SourceUrl, Projection = projection,
            OccurredAt = occurred?.At, OccurredOffset = occurred?.Offset, OccurredBasis = occurred?.Basis, OccurredPrecision = occurred?.Precision,
            SizeBytes = Encoding.UTF8.GetByteCount(request.Content), Status = "indexed_partial",
            Admission = new PublicationAdmission(authority, jobId, idempotencyKey, requestHash, Encoding.UTF8.GetByteCount(request.Content), ApprovedProposalId: approved?.ProposalId) };
        // ING-4: capacity admission precedes the vault write, so a rejected capture leaves no original behind.
        await coordinator.CheckAdmissionAsync(id, publication.SourceId, publication.Admission, cancellationToken);
        if (files is not null)
        {
            if (approved is null) await files.WriteAsync(id, Encoding.UTF8.GetBytes(request.Content), ".md", cancellationToken: cancellationToken);
            else await files.WriteRevisionAsync(id, Encoding.UTF8.GetBytes(request.Content), ".md", null,
                new MutationFinalization { MutationIdentity = approved.Plan.MutationId, AssistantNote = true }, cancellationToken);
        }
        await PublishFreshAsync(publication, managedPath, cancellationToken, preserveOnFailure: approved is not null);
        return new CaptureResult((await GetAsync(id, true, cancellationToken))!, new ProcessingJob(jobId, id, 1, "queued", 0));
    }

    // Admission is re-checked atomically at publication; a concurrent admission that wins the remaining
    // capacity must not strand the original just written for a document that will never exist.
    private async ValueTask PublishFreshAsync(DocumentPublication publication, string? managedPath, CancellationToken cancellationToken, bool preserveOnFailure = false)
    {
        PublicationOutcome outcome;
        try { outcome = await coordinator.PublishAsync(publication, cancellationToken); }
        catch
        {
            if (managedPath is not null && !preserveOnFailure) await files!.DiscardUnpublishedAsync(managedPath, CancellationToken.None);
            throw;
        }
        if (outcome == PublicationOutcome.Published) return;
        if (managedPath is not null && !preserveOnFailure) await files!.DiscardUnpublishedAsync(managedPath, CancellationToken.None);
        throw new IngestionConflictException("revision-mismatch");
    }

    public async ValueTask<DocumentRecord?> GetAsync(string id, bool includeText = false, CancellationToken cancellationToken = default)
    {
        await using var lease = await state.OpenReadConnectionAsync(cancellationToken);
        await using var transaction = await lease.Connection.BeginTransactionAsync(cancellationToken);
        await using var read = Command(lease.Connection, transaction,
            "SELECT id,revision,type,title,status,content_hash,props_json,provenance,updated_at FROM documents WHERE id=$id", ("$id", id));
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var record = new DocumentRecord(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8));
        if (!includeText) return record;
        await reader.DisposeAsync();
        await using var projectionRead = Command(lease.Connection, transaction,
            "SELECT p.bytes FROM document_projections d JOIN payloads p ON p.id=d.payload_id WHERE d.document_id=$id AND p.redacted_at IS NULL", ("$id", id));
        if (await projectionRead.ExecuteScalarAsync(cancellationToken) is byte[] bytes)
        {
            var projection = JsonSerializer.Deserialize<TextProjection>(bytes)!;
            record = record with { Text = projection.LosslessText, Chunks = projection.Chunks, Tags = projection.Tags, Path = projection.Path };
        }
        return record;
    }

    public async ValueTask<IReadOnlyList<ProcessingJob>> JobsAsync(string? status = null, CancellationToken cancellationToken = default)
    {
        var results = new List<ProcessingJob>();
        await using var lease = await state.OpenReadConnectionAsync(cancellationToken);
        await using var read = Command(lease.Connection, null, "SELECT id,document_id,revision,status,attempts,error,kind FROM jobs WHERE ($status IS NULL OR status=$status) ORDER BY created_at,id LIMIT 200", ("$status", status));
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) results.Add(new ProcessingJob(reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1), reader.IsDBNull(2) ? 0 : reader.GetInt64(2), reader.GetString(3), reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6)));
        return results;
    }

    public async ValueTask<ProcessingJob?> JobAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var lease = await state.OpenReadConnectionAsync(cancellationToken);
        await using var read = Command(lease.Connection, null, "SELECT id,document_id,revision,status,attempts,error,kind FROM jobs WHERE id=$id", ("$id", id));
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new ProcessingJob(reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1), reader.IsDBNull(2) ? 0 : reader.GetInt64(2), reader.GetString(3), reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6)) : null;
    }
    public async ValueTask<ProcessingJob> RetryJobAsync(string id, IngestionAuthority authority, CancellationToken cancellationToken = default)
    {
        await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await SecondBrain.Storage.Auth.IngestionAuthorityValidator.ValidateAsync(connection, transaction, authority, SecondBrain.Core.Authorization.Scope.Write, DateTimeOffset.UtcNow, cancellationToken: token);
            string? error; string status; string kind; string document;
            await using (var read = Command(connection, transaction, "SELECT j.status,j.error,j.kind,j.document_id FROM jobs j JOIN documents d ON d.id=j.document_id JOIN publication_fences f ON f.document_id=d.id WHERE j.id=$id AND j.revision=d.revision AND j.generation_json=f.generation_json AND d.status NOT IN ('deleted','purged')", ("$id", id)))
            await using (var reader = await read.ExecuteReaderAsync(token))
            {
                if (!await reader.ReadAsync(token)) throw new IngestionConflictException("job-superseded");
                status = reader.GetString(0); error = reader.IsDBNull(1) ? null : reader.GetString(1); kind = reader.GetString(2); document = reader.GetString(3);
            }
            if (status != "failed") throw new IngestionConflictException("job-not-failed");
            // A staging rejection (ING-7) is deterministic; only a changed source entry retries it.
            if (kind == "stage" || kind == "extract" && error is not ("extraction-unavailable" or "extraction-timeout" or "provider-unavailable")) throw new IngestionConflictException("deterministic-failure");
            await using var update = Command(connection, transaction, "UPDATE jobs SET status='queued',attempts=0,run_after=NULL,lease_until=NULL,error=NULL,updated_at=$now WHERE id=$id", ("$id", id), ("$now", Now())); await update.ExecuteNonQueryAsync(token);
            await using var doc = Command(connection, transaction, "UPDATE documents SET status=$status,error=NULL WHERE id=$id", ("$id", document), ("$status", kind == "extract" ? "queued" : "indexed_partial")); return await doc.ExecuteNonQueryAsync(token);
        }, cancellationToken);
        return (await JobAsync(id, cancellationToken))!;
    }

    public async ValueTask<string> ProposeAsync(string id, long revision, string operation, JsonElement payload, IngestionAuthority authority, CancellationToken cancellationToken = default)
    {
        var pending = Ulid.NewUlid().ToString();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await SecondBrain.Storage.Auth.IngestionAuthorityValidator.ValidateAsync(connection, transaction, authority,
                SecondBrain.Core.Authorization.Scope.Write, DateTimeOffset.UtcNow, cancellationToken: token);
            await using var current = Command(connection, transaction, "SELECT revision FROM documents WHERE id=$id", ("$id", id));
            if (Convert.ToInt64(await current.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) != revision) throw new IngestionConflictException("revision-mismatch");
            await using var body = Command(connection, transaction, "INSERT INTO payloads(id,sha256,bytes,document_refs_json,created_at) VALUES($pending,$hash,$bytes,$refs,$now)",
                ("$pending", pending), ("$hash", hash), ("$bytes", bytes), ("$refs", JsonSerializer.Serialize(new[] { id })), ("$now", Now()));
            await body.ExecuteNonQueryAsync(token);
            await using var proposal = Command(connection, transaction,
                "INSERT INTO pending_operations(id,creator_credential_id,tool,tool_schema_version,payload_id,payload_hash,targets_json,policy_generation,status,created_at,expires_at) VALUES($pending,$actor,$tool,1,$pending,$hash,$targets,1,'pending',$now,$expiry)",
                ("$pending", pending), ("$actor", authority.CredentialId), ("$tool", operation), ("$hash", hash),
                ("$targets", JsonSerializer.Serialize(new[] { new { document_id = id, revision } })), ("$now", Now()),
                ("$expiry", DateTimeOffset.UtcNow.AddDays(1).ToString("O", CultureInfo.InvariantCulture)));
            await proposal.ExecuteNonQueryAsync(token);
            await using var reference = Command(connection, transaction, "INSERT INTO payload_refs(payload_id,referrer_kind,referrer_id) VALUES($pending,'pending-operation',$pending)", ("$pending", pending));
            await reference.ExecuteNonQueryAsync(token);
            return pending;
        }, cancellationToken);
    }

    public async ValueTask<CaptureResult> AdmitFileAsync(byte[] bytes, CaptureRequest metadata, string fileName, IngestionAuthority authority, string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        if (files is null) throw new InvalidOperationException("Managed file adapter is not registered.");
        if (bytes.LongLength > 50L * 1024 * 1024 || fileName.Length > 1024) throw new ArgumentException("Input bound exceeded.");
        Validate(metadata with { Content = "" });
        await captureGate.WaitAsync(cancellationToken);
        try
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var requestHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hash + JsonSerializer.Serialize(new { metadata, fileName }))));
            var actor = await state.QueueWriteAsync(async (connection, transaction, token) =>
                await SecondBrain.Storage.Auth.IngestionAuthorityValidator.ValidateAsync(connection, transaction, authority,
                    SecondBrain.Core.Authorization.Scope.Write, DateTimeOffset.UtcNow, cancellationToken: token), cancellationToken);
            authority = authority with { CredentialName = actor.Name };
            if (!string.IsNullOrEmpty(idempotencyKey))
            {
                await using var lease = await state.OpenReadConnectionAsync(cancellationToken);
                await using var read = Command(lease.Connection, null, "SELECT payload_hash,response_json FROM idempotency WHERE key=$key AND credential_id=$actor", ("$key", idempotencyKey), ("$actor", actor.Id));
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    if (reader.GetString(0) != requestHash) throw new IngestionConflictException("idempotency-key-reuse");
                    var existing = await GetAsync(reader.GetString(1), true, cancellationToken) ?? throw new IngestionConflictException("capture-pending");
                    return new CaptureResult(existing, (await JobsAsync(null, cancellationToken)).FirstOrDefault(job => job.DocumentId == existing.Id), "live", "pending");
                }
            }
            var id = Ulid.NewUlid().ToString(); var job = Ulid.NewUlid().ToString();
            var dot = fileName.LastIndexOf('.');
            var extension = dot >= 0 && fileName.Length - dot <= 16 && fileName[(dot + 1)..].All(char.IsAsciiLetterOrDigit) ? fileName[dot..] : ".bin";
            var path = files.GetManagedPath(id, extension);
            await state.QueueWriteAsync(async (connection, transaction, token) =>
            {
                await using var source = Command(connection, transaction, "INSERT OR IGNORE INTO sources(id,kind,name,created_at) VALUES('api-captures','api','API captures',$now)", ("$now", Now()));
                return await source.ExecuteNonQueryAsync(token);
            }, cancellationToken);
            var title = metadata.Title ?? fileName;
            var props = metadata.Props?.GetRawText() ?? "{}";
            var publication = new DocumentPublication(id, "api-captures", 0, Generation, "capture-file", title, hash, "api:" + actor.Name)
            {
                Type = metadata.Type ?? "file", TypeOrigin = metadata.Type is null ? "format-default" : "door", PropsJson = props, PropsOriginalJson = props, SizeBytes = bytes.LongLength,
                Status = "queued", Projection = TextProcessor.Process(id, title, "", metadata.Tags, path),
                Admission = new PublicationAdmission(authority, job, idempotencyKey, requestHash, bytes.LongLength, "extract",
                    JsonSerializer.Serialize(new { path, file_name = fileName, metadata, admitted_bytes = bytes.LongLength }))
            };
            await coordinator.CheckAdmissionAsync(id, publication.SourceId, publication.Admission, cancellationToken);
            await files.WriteAsync(id, bytes, extension, cancellationToken: cancellationToken);
            await PublishFreshAsync(publication, path, cancellationToken);
            return new CaptureResult((await GetAsync(id, true, cancellationToken))!, new ProcessingJob(job, id, 1, "queued", 0), "live", "pending");
        }
        finally { captureGate.Release(); }
    }

    public async ValueTask<IReadOnlyList<FileProcessingWork>> PendingClaimCleanupAsync(int limit = 16, CancellationToken cancellationToken = default)
    {
        var claims = new List<FileProcessingWork>();
        await using var lease = await state.OpenReadConnectionAsync(cancellationToken);
        await using var read = Command(lease.Connection, null,
            "SELECT id,document_id,revision,generation_json,payload_json FROM jobs WHERE kind='extract' AND status='completed' AND json_extract(payload_json,'$.claim_root') IS NOT NULL AND COALESCE(json_extract(payload_json,'$.claim_cleaned'),0)=0 ORDER BY created_at,id LIMIT $limit", ("$limit", Math.Clamp(limit, 1, 32)));
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            using var payload = JsonDocument.Parse(reader.GetString(4)); var root = payload.RootElement;
            claims.Add(new FileProcessingWork(reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(0), root.GetProperty("path").GetString()!, root.GetProperty("file_name").GetString()!, root.GetProperty("claim_root").GetString(), root.GetProperty("claim_path").GetString(), root.GetProperty("claim_original_name").GetString()));
        }
        return claims;
    }
    public async ValueTask FinalizeClaimAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await using var update = Command(connection, transaction, "UPDATE jobs SET payload_json=json_set(payload_json,'$.claim_cleaned',1),updated_at=$now WHERE id=$id AND kind='extract' AND status='completed'", ("$id", jobId), ("$now", Now()));
            return await update.ExecuteNonQueryAsync(token);
        }, cancellationToken);
    }
    public async ValueTask FailFileAsync(string jobId, string error, bool deterministic, CancellationToken cancellationToken = default)
    {
        if (error.Length > 1024) error = error[..1024];
        await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            int attempts; string documentId;
            await using (var read = Command(connection, transaction, "SELECT attempts,document_id FROM jobs WHERE id=$id AND kind='extract' AND status='running'", ("$id", jobId)))
            await using (var reader = await read.ExecuteReaderAsync(token))
            { if (!await reader.ReadAsync(token)) return 0; attempts = reader.GetInt32(0); documentId = reader.GetString(1); }
            var terminal = deterministic || attempts >= 3;
            await using var update = Command(connection, transaction, "UPDATE jobs SET status=$status,error=$error,lease_until=NULL,run_after=$after,updated_at=$now WHERE id=$id AND status='running'", ("$id", jobId), ("$status", terminal ? "failed" : "queued"), ("$error", error), ("$after", DateTimeOffset.UtcNow.AddSeconds(Math.Pow(2, attempts)).ToString("O", CultureInfo.InvariantCulture)), ("$now", Now())); await update.ExecuteNonQueryAsync(token);
            await using var document = Command(connection, transaction, "UPDATE documents SET status=$status,error=$error WHERE id=$id AND status NOT IN ('deleted','purged')", ("$id", documentId), ("$status", terminal ? "failed" : "queued"), ("$error", error)); return await document.ExecuteNonQueryAsync(token);
        }, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<FileProcessingWork>> PendingFilesAsync(int limit = 16, CancellationToken cancellationToken = default)
    {
        return await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            var jobs = new List<FileProcessingWork>();
            await using (var expired = Command(connection, transaction, "UPDATE jobs SET status='queued',lease_until=NULL WHERE kind='extract' AND status='running' AND lease_until<$now", ("$now", Now()))) await expired.ExecuteNonQueryAsync(token);
            await using (var read = Command(connection, transaction,
                "SELECT j.id,j.document_id,j.revision,j.generation_json,j.payload_json FROM jobs j JOIN documents d ON d.id=j.document_id JOIN publication_fences f ON f.document_id=d.id WHERE j.kind='extract' AND j.status='queued' AND j.revision=d.revision AND j.generation_json=f.generation_json AND d.status NOT IN ('deleted','purged') AND (j.run_after IS NULL OR j.run_after<=$now) ORDER BY j.created_at,j.id LIMIT $limit", ("$now", Now()), ("$limit", Math.Clamp(limit, 1, 32))))
            await using (var reader = await read.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token))
                {
                    using var payload = JsonDocument.Parse(reader.GetString(4));
                    jobs.Add(new FileProcessingWork(reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(0), payload.RootElement.GetProperty("path").GetString()!, payload.RootElement.GetProperty("file_name").GetString()!,
                        payload.RootElement.TryGetProperty("claim_root", out var root) ? root.GetString() : null,
                        payload.RootElement.TryGetProperty("claim_path", out var path) ? path.GetString() : null,
                        payload.RootElement.TryGetProperty("claim_original_name", out var original) ? original.GetString() : null));
                }
            foreach (var job in jobs)
            {
                await using var lease = Command(connection, transaction, "UPDATE jobs SET status='running',attempts=attempts+1,lease_until=$lease,updated_at=$now WHERE id=$id AND status='queued'", ("$id", job.JobId), ("$lease", DateTimeOffset.UtcNow.AddMinutes(3).ToString("O", CultureInfo.InvariantCulture)), ("$now", Now()));
                await lease.ExecuteNonQueryAsync(token);
            }
            return (IReadOnlyList<FileProcessingWork>)jobs;
        }, cancellationToken);
    }

    public async ValueTask RecoverFileLeasesAsync(CancellationToken cancellationToken = default)
    {
        await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await using var recover = Command(connection, transaction,
                "UPDATE jobs SET status='queued',lease_until=NULL,updated_at=$now WHERE kind='extract' AND status='running'", ("$now", Now()));
            return await recover.ExecuteNonQueryAsync(token);
        }, cancellationToken);
    }

    private async Task<string> ExtractionCacheDiscriminatorAsync(string documentId, long revision, CancellationToken cancellationToken)
    {
        await using var lease = await state.OpenReadConnectionAsync(cancellationToken);
        await using var read = Command(lease.Connection, null, "SELECT json_extract(payload_json,'$.file_name') FROM jobs WHERE document_id=$id AND revision=$revision AND kind='extract' LIMIT 1", ("$id", documentId), ("$revision", revision));
        var name = await read.ExecuteScalarAsync(cancellationToken) as string ?? "";
        var hint = Path.GetExtension(name).ToLowerInvariant() switch { ".md" or ".markdown" => "markdown", ".txt" or ".text" => "text", ".html" or ".htm" => "html", ".pdf" => "pdf", ".docx" => "docx", _ => "unknown" };
        return "m1-bounded:" + hint;
    }
    public async ValueTask<ExtractionResult?> CachedExtractionAsync(string documentId, CancellationToken cancellationToken = default)
    {
        var document = await GetAsync(documentId, false, cancellationToken);
        if (document is null || DocumentStatusNames.IsUnavailable(document.Status)) return null;
        var discriminator = await ExtractionCacheDiscriminatorAsync(documentId, document.Revision, cancellationToken);
        string? payloadId;
        await using (var indexLease = await index.OpenReadConnectionAsync(cancellationToken))
        await using (var cached = Command(indexLease.Connection, null, "SELECT path FROM extraction_cache WHERE content_hash=$hash AND extractor=$extractor AND extractor_version=1 AND type=$type AND schema_version=1", ("$hash", document.ContentHash), ("$type", document.Type), ("$extractor", discriminator)))
            payloadId = await cached.ExecuteScalarAsync(cancellationToken) as string;
        if (payloadId is null) return null;
        await using var lease = await state.OpenReadConnectionAsync(cancellationToken);
        await using var body = Command(lease.Connection, null, "SELECT bytes FROM payloads WHERE id=$id AND redacted_at IS NULL", ("$id", payloadId));
        return await body.ExecuteScalarAsync(cancellationToken) is byte[] bytes ? JsonSerializer.Deserialize<ExtractionResult>(bytes) : null;
    }
    public async ValueTask<CaptureResult> CompleteFileAsync(string documentId, long revision, ExtractionResult extraction, CancellationToken cancellationToken = default)
    {
        var document = await GetAsync(documentId, true, cancellationToken) ?? throw new IngestionConflictException("not-found");
        if (document.Revision != revision || DocumentStatusNames.IsUnavailable(document.Status)) throw new IngestionConflictException("revision-mismatch");
        string typeOrigin = "format-default"; string? sourceDefault = null; string? mappedType = null;
        await using (var metadataLease = await state.OpenReadConnectionAsync(cancellationToken))
        await using (var metadataRead = Command(metadataLease.Connection, null, "SELECT d.type_origin,s.config_json,(SELECT path FROM occurrences WHERE document_id=d.id LIMIT 1) FROM documents d JOIN sources s ON s.id=d.source_id WHERE d.id=$id", ("$id", documentId)))
        await using (var metadataReader = await metadataRead.ExecuteReaderAsync(cancellationToken))
            if (await metadataReader.ReadAsync(cancellationToken))
            {
                typeOrigin = metadataReader.GetString(0);
                if (!metadataReader.IsDBNull(1) && !metadataReader.IsDBNull(2))
                {
                    var source = JsonSerializer.Deserialize<SecondBrain.Core.Sources.FolderSourceConfiguration>(metadataReader.GetString(1));
                    sourceDefault = source?.DefaultType;
                    if (source is not null)
                        mappedType = source.TypeMap.FirstOrDefault(pair => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pair.Key, metadataReader.GetString(2), false)).Value;
                }
            }
        var type = typeOrigin is "door" or "user" ? document.Type : extraction.DeclaredType ?? mappedType ?? sourceDefault ??
            (extraction.PhysicalFormat is "pdf" or "html" ? "article" : extraction.PhysicalFormat is "markdown" or "md" or "text" or "txt" ? "note" : "file");
        if (typeOrigin is not ("door" or "user")) typeOrigin = extraction.DeclaredType is not null ? "frontmatter" : mappedType is not null ? "type-map" : sourceDefault is not null ? "source" : "format-default";
        if (type is not ("note" or "article" or "file")) type = "file";
        CaptureRequest? doorMetadata = null;
        await using (var admissionLease = await state.OpenReadConnectionAsync(cancellationToken))
        await using (var admissionRead = Command(admissionLease.Connection, null, "SELECT json_extract(payload_json,'$.metadata') FROM jobs WHERE document_id=$id AND revision=$revision AND kind='extract' LIMIT 1", ("$id", documentId), ("$revision", revision)))
            if (await admissionRead.ExecuteScalarAsync(cancellationToken) is string metadataJson) doorMetadata = JsonSerializer.Deserialize<CaptureRequest>(metadataJson);
        var overrides = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        await using (var overrideLease = await state.OpenReadConnectionAsync(cancellationToken))
        await using (var overrideRead = Command(overrideLease.Connection, null, "SELECT kind,value_json FROM overrides WHERE document_id=$id AND key=''", ("$id", documentId)))
        await using (var overrideReader = await overrideRead.ExecuteReaderAsync(cancellationToken))
            while (await overrideReader.ReadAsync(cancellationToken))
                overrides[overrideReader.GetString(0)] = JsonSerializer.Deserialize<JsonElement>(overrideReader.GetString(1));
        if (overrides.TryGetValue("type", out var typeOverride) && typeOverride.ValueKind == JsonValueKind.String)
        { type = typeOverride.GetString()!; typeOrigin = "user"; }
        var tags = (doorMetadata?.Tags ?? extraction.Tags ?? document.Tags ?? []).ToArray();
        if (overrides.TryGetValue("tags", out var tagOverride) && tagOverride.ValueKind == JsonValueKind.Array)
            tags = tagOverride.EnumerateArray().Select(tag => tag.GetString()!).Distinct(StringComparer.Ordinal).ToArray();
        var title = overrides.TryGetValue("title", out var titleOverride) && titleOverride.ValueKind == JsonValueKind.String ? titleOverride.GetString()! : doorMetadata?.Title ?? extraction.Title ?? document.Title;
        TextProjection projection;
        try { projection = TextProcessor.Process(documentId, title, extraction.LosslessText, tags, document.Path); }
        catch (IngestionConflictException error) when (error.Code == "chunk-count-bound")
        {
            extraction = extraction with { Status = "skipped", Bound = "chunk-count-bound", LosslessText = "" };
            projection = TextProcessor.Process(documentId, title, "", document.Tags, document.Path);
        }
        if (extraction.Pages is { Count: > 0 } pages)
            projection = projection with { Chunks = projection.Chunks.Select(chunk => chunk with
            {
                Page = pages.FirstOrDefault(page => chunk.EndOffset > page.Start && chunk.StartOffset < (long)page.Start + page.Length)?.Number
            }).ToArray() };
        var props = doorMetadata?.Props?.GetRawText() ?? (extraction.Properties is null ? document.PropsJson : JsonSerializer.Serialize(extraction.Properties));
        var registry = new TypeRegistry(); string effective;
        try
        {
            using var parsed = JsonDocument.Parse(props);
            var authored = parsed.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
            if (overrides.TryGetValue("props", out var propertyOverrides) && propertyOverrides.ValueKind == JsonValueKind.Object)
                foreach (var property in propertyOverrides.EnumerateObject()) authored[property.Name] = property.Value.Clone();
            effective = registry.DeriveEffectiveProperties(type, JsonSerializer.SerializeToElement(authored)).GetRawText();
        }
        catch (PropertyValidationException)
        {
            extraction = extraction with { Status = "failed", Error = "invalid-properties" };
            effective = "{}";
        }
        var sourceZone = TimeZoneInfo.FindSystemTimeZoneById(options?.Value.Time.Zone ?? TimeZoneInfo.Local.Id);
        using var effectiveTimeProps = JsonDocument.Parse(effective);
        var occurredBasis = registry.GetProcessingType(type).Metadata.OccurredBasis;
        var occurredValue = occurredBasis == "created" ? extraction.DeclaredCreatedAt : occurredBasis is not null && effectiveTimeProps.RootElement.TryGetProperty(occurredBasis, out var declaredTime) && declaredTime.ValueKind == JsonValueKind.String ? declaredTime.GetString() : null;
        var occurred = occurredBasis is null ? null : SourceTime.Parse(occurredValue, occurredBasis, sourceZone);
        projection = projection with { Aliases = extraction.Aliases?.Where(alias => !string.IsNullOrWhiteSpace(alias) && alias.Length <= 256).Take(200).Distinct(StringComparer.Ordinal).ToArray(), SourceCreatedAt = SourceTime.Parse(extraction.DeclaredCreatedAt, "created", sourceZone) is not null ? extraction.DeclaredCreatedAt : null, SourceUpdatedAt = SourceTime.Parse(extraction.DeclaredUpdatedAt, "updated", sourceZone) is not null ? extraction.DeclaredUpdatedAt : null, Links = extraction.Links?.Where(link => !string.IsNullOrWhiteSpace(link) && link.Length <= 4096 && !link.Any(char.IsControl)).Take(200).Distinct(StringComparer.Ordinal).ToArray() };
        var cacheDiscriminator = await ExtractionCacheDiscriminatorAsync(documentId, revision, cancellationToken);
        var cachePayloadId = Ulid.NewUlid().ToString();
        var cacheBytes = JsonSerializer.SerializeToUtf8Bytes(extraction);
        var completedNow = false;
        using (await PublicationCoordinator.AcquirePublicationGateAsync(state, cancellationToken))
        {
        await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await using var pendingExtraction = Command(connection, transaction, "SELECT 1 FROM jobs WHERE document_id=$id AND revision=$revision AND kind='extract' AND status IN ('queued','running') LIMIT 1", ("$id", documentId), ("$revision", revision));
            if (await pendingExtraction.ExecuteScalarAsync(token) is null) return 0;
            await using var fence = Command(connection, transaction, $"SELECT d.revision FROM documents d JOIN publication_fences f ON f.document_id=d.id WHERE d.id=$id AND d.status NOT IN ({DocumentStatusSql.Unavailable}) AND f.generation_json=$generation", ("$id", documentId), ("$generation", Generation));
            if (Convert.ToInt64(await fence.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) != revision) throw new IngestionConflictException("revision-mismatch");
            completedNow = true;
            await using var cacheBody = Command(connection, transaction, "INSERT INTO payloads(id,sha256,bytes,document_refs_json,created_at) VALUES($payload,$hash,$bytes,$refs,$now)", ("$payload", cachePayloadId), ("$hash", Convert.ToHexStringLower(SHA256.HashData(cacheBytes))), ("$bytes", cacheBytes), ("$refs", JsonSerializer.Serialize(new[] { documentId })), ("$now", Now())); await cacheBody.ExecuteNonQueryAsync(token);
            await using var cacheReference = Command(connection, transaction, "INSERT INTO payload_refs(payload_id,referrer_kind,referrer_id) VALUES($payload,'extraction-cache',$id)", ("$payload", cachePayloadId), ("$id", documentId)); await cacheReference.ExecuteNonQueryAsync(token);
            var payloadId = Ulid.NewUlid().ToString(); var bytes = JsonSerializer.SerializeToUtf8Bytes(projection);
            await using var body = Command(connection, transaction, "INSERT INTO payloads(id,sha256,bytes,document_refs_json,created_at) VALUES($payload,$hash,$bytes,$refs,$now)", ("$payload", payloadId), ("$hash", Convert.ToHexStringLower(SHA256.HashData(bytes))), ("$bytes", bytes), ("$refs", JsonSerializer.Serialize(new[] { documentId })), ("$now", Now())); await body.ExecuteNonQueryAsync(token);
            await using var reference = Command(connection, transaction, "INSERT INTO payload_refs(payload_id,referrer_kind,referrer_id) VALUES($payload,'document-projection',$id)", ("$payload", payloadId), ("$id", documentId)); await reference.ExecuteNonQueryAsync(token);
            await using var updateProjection = Command(connection, transaction, "UPDATE document_projections SET payload_id=$payload WHERE document_id=$id AND revision=$revision", ("$payload", payloadId), ("$id", documentId), ("$revision", revision)); await updateProjection.ExecuteNonQueryAsync(token);
            var status = extraction.Status is "failed" or "skipped" or "needs_ocr" ? extraction.Status : "indexed_partial";
            await using var update = Command(connection, transaction, "UPDATE documents SET type=$type,type_origin=$origin,title=$title,mime=$mime,props_json=$props,props_original_json=$original,occurred_at=$occurred,occurred_offset=$offset,occurred_basis=$basis,occurred_precision=$precision,status=$status,error=$error,publish_state='publishing' WHERE id=$id AND revision=$revision", ("$type", type), ("$origin", typeOrigin), ("$title", title), ("$mime", extraction.MimeType), ("$props", effective), ("$original", props), ("$occurred", occurred?.At), ("$offset", occurred?.Offset), ("$basis", occurred?.Basis), ("$precision", occurred?.Precision), ("$status", status), ("$error", extraction.Bound ?? extraction.Error), ("$id", documentId), ("$revision", revision)); await update.ExecuteNonQueryAsync(token);
            await using var complete = Command(connection, transaction, "UPDATE jobs SET status=$status,error=$error,lease_until=NULL,updated_at=$now WHERE document_id=$id AND revision=$revision AND kind='extract'", ("$status", extraction.Status is "failed" or "skipped" ? "failed" : "completed"), ("$error", extraction.Bound ?? extraction.Error), ("$now", Now()), ("$id", documentId), ("$revision", revision)); await complete.ExecuteNonQueryAsync(token);
            if (status == "indexed_partial")
            {
                await using var embed = Command(connection, transaction, "INSERT INTO jobs(id,kind,document_id,revision,generation_json,initiating_credential_id,credential_generation,account_epoch,status,created_at,updated_at) SELECT $job,'embed',document_id,revision,generation_json,initiating_credential_id,credential_generation,account_epoch,'queued',$now,$now FROM jobs WHERE document_id=$id AND revision=$revision AND kind='extract' LIMIT 1", ("$job", Ulid.NewUlid().ToString()), ("$now", Now()), ("$id", documentId), ("$revision", revision)); await embed.ExecuteNonQueryAsync(token);
            }
            return 0;
        }, cancellationToken);
        }
        if (completedNow)
            await index.QueueWriteAsync(async (connection, transaction, token) =>
            {
                await using var cache = Command(connection, transaction, "INSERT INTO extraction_cache(content_hash,extractor,extractor_version,type,schema_version,path,created_at) VALUES($hash,$extractor,1,$type,1,$payload,$now) ON CONFLICT(content_hash,extractor,extractor_version,type,schema_version) DO UPDATE SET path=excluded.path,created_at=excluded.created_at", ("$hash", document.ContentHash), ("$type", document.Type), ("$payload", cachePayloadId), ("$extractor", cacheDiscriminator), ("$now", Now())); return await cache.ExecuteNonQueryAsync(token);
            }, cancellationToken);
        await coordinator.PublishCurrentAsync(new PublicationFence(documentId, revision, Generation), cancellationToken);
        var job = (await JobsAsync(null, cancellationToken)).FirstOrDefault(item => item.DocumentId == documentId);
        return new CaptureResult((await GetAsync(documentId, true, cancellationToken))!, job);
    }

    public async ValueTask<CaptureResult> CaptureExtractedAsync(CaptureRequest metadata, ExtractionResult extraction, string fileName, IngestionAuthority authority, string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        var type = metadata.Type ?? extraction.DeclaredType ?? (extraction.PhysicalFormat is "pdf" or "html" ? "article" : extraction.PhysicalFormat is "markdown" or "text" ? "note" : "file");
        JsonElement? props = metadata.Props;
        if (props is null && extraction.Properties is not null) props = JsonSerializer.SerializeToElement(extraction.Properties);
        var result = await CaptureAsync(metadata with { Content = extraction.LosslessText, Title = metadata.Title ?? extraction.Title ?? fileName,
            Type = type, Props = props, Tags = metadata.Tags ?? extraction.Tags?.ToArray() }, authority, idempotencyKey, cancellationToken);
        if (extraction.Status is "failed" or "skipped" or "needs_ocr")
        {
            await state.QueueWriteAsync(async (connection, transaction, token) =>
            {
                await using var update = Command(connection, transaction,
                    "UPDATE documents SET status=$status,error=$error,mime=$mime WHERE id=$id", ("$id", result.Document.Id),
                    ("$status", extraction.Status), ("$error", extraction.Bound ?? extraction.Error), ("$mime", extraction.MimeType));
                await update.ExecuteNonQueryAsync(token);
                await using var jobs = Command(connection, transaction,
                    "UPDATE jobs SET status='failed',error=$error WHERE document_id=$id", ("$id", result.Document.Id), ("$error", extraction.Bound ?? extraction.Error));
                return await jobs.ExecuteNonQueryAsync(token);
            }, cancellationToken);
            await coordinator.PublishCurrentAsync(new PublicationFence(result.Document.Id, result.Document.Revision, Generation), cancellationToken);
            result = result with { Document = (await GetAsync(result.Document.Id, true, cancellationToken))!, Job = result.Job is null ? null : result.Job with { Status = "failed", Error = extraction.Bound ?? extraction.Error, Stage = "extract" } };
        }
        return result;
    }

    public ValueTask<CaptureResult> MutateAsync(string id, long revision, string operation, JsonElement payload, IngestionAuthority authority, CancellationToken cancellationToken = default)
        => MutateCoreAsync(id, revision, operation, payload, authority, cancellationToken);

    private async ValueTask<CaptureResult> MutateCoreAsync(string id, long revision, string operation, JsonElement payload, IngestionAuthority authority, CancellationToken cancellationToken, SecondBrain.Core.Chat.ApprovedDocumentWrite? approved = null)
    {
        // A tags array is the requested tag set. Only a superset of the current tags (pure additions) is additive
        // metadata that a key executes directly; omitting any current tag is a removal and needs a proposal (ING-12).
        var additive = operation is "patch" or "update" && payload.ValueKind == JsonValueKind.Object && payload.EnumerateObject().All(property => property.Name == "tags")
            && payload.TryGetProperty("tags", out var requestedTags) && requestedTags.ValueKind == JsonValueKind.Array
            && await GetAsync(id, true, cancellationToken) is { } current
            && (current.Tags ?? []).All(tag => requestedTags.EnumerateArray().Any(requested => requested.ValueKind == JsonValueKind.String && requested.GetString() == tag));
        if (authority.Kind == "api_key" && !additive)
        {
            var pending = await ProposeAsync(id, revision, operation, payload, authority, cancellationToken);
            var target = await GetAsync(id, false, cancellationToken) ?? throw new IngestionConflictException("not-found");
            return new CaptureResult(target with { Status = "proposed" }, new ProcessingJob(pending, id, revision, "pending", 0, Stage: "proposal"));
        }
        var document = await GetAsync(id, true, cancellationToken) ?? throw new IngestionConflictException("not-found");
        if (document.Revision != revision) throw new IngestionConflictException("revision-mismatch");
        if (document.Status == "purged") throw new IngestionConflictException("document-purged");
        // An unavailable document is not editable, so an edit can never return its retained projection.
        if (operation is not ("delete" or "restore" or "purge") && DocumentStatusNames.IsUnavailable(document.Status)) throw new IngestionConflictException("not-found");
        if (operation == "delete" && document.Status == "deleted" || operation == "restore" && document.Status != "deleted") return new(document, null);
        TextProjection? durableProjection = null;
        await using (var projectionLease = await state.OpenReadConnectionAsync(cancellationToken))
        await using (var projectionRead = Command(projectionLease.Connection, null, "SELECT p.bytes FROM document_projections d JOIN payloads p ON p.id=d.payload_id WHERE d.document_id=$id AND p.redacted_at IS NULL", ("$id", id)))
        {
            if (await projectionRead.ExecuteScalarAsync(cancellationToken) is byte[] snapshot) durableProjection = JsonSerializer.Deserialize<TextProjection>(snapshot);
        }
        var priorPath = document.Path ?? durableProjection?.Path;
        var replacesContent = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("content", out _);
        var status = operation switch { "delete" => "deleted", "purge" => "purged", "restore" => "indexed_partial", _ => replacesContent ? "indexed_partial" : document.Status };
        var title = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("title", out var t) ? t.GetString()! : document.Title;
        var content = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("content", out var c) ? c.GetString()! : document.Text ?? durableProjection?.LosslessText ?? "";
        var type = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("type", out var ty) ? ty.GetString()! : document.Type;
        var props = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("props", out var p) ? p.GetRawText() : document.PropsJson;
        var tags = document.Tags ?? durableProjection?.Tags ?? [];
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("tags", out var requested)) tags = requested.EnumerateArray().Select(tag => tag.GetString()!).Distinct(StringComparer.Ordinal).ToArray();
        using var parsedProps = JsonDocument.Parse(props);
        Validate(new CaptureRequest(content, title, type, parsedProps.RootElement, tags));
        var publication = new DocumentPublication(id, "api-captures", revision, Generation, operation, title,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))), document.Provenance)
        { Type = type, PropsJson = props, PropsOriginalJson = props, Status = status,
            Admission = new PublicationAdmission(authority, Ulid.NewUlid().ToString(), null, "", Encoding.UTF8.GetByteCount(content)),
            Projection = TextProcessor.Process(id, title, content, tags, document.Path) };
        if (operation is "patch" or "update" or "delete" or "restore" or "purge" && files is not null)
        {
            var hasContent = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("content", out _);
            var extension = priorPath is null ? ".md" : Path.GetExtension(priorPath);
            if (string.IsNullOrEmpty(extension)) extension = ".md";
            if (hasContent && extension is not (".md" or ".markdown" or ".txt")) throw new IngestionConflictException("binary-edit-not-supported");
            var bytes = Encoding.UTF8.GetBytes(content);
            string? expectedHash = null;
            if (priorPath is not null)
            {
                try
                {
                    await using var original = await files.OpenReadAsync(priorPath, cancellationToken);
                    if (!hasContent) { using var copied = new MemoryStream(); await original.CopyToAsync(copied, cancellationToken); bytes = copied.ToArray(); }
                    expectedHash = document.ContentHash;
                }
                catch (InvalidDataException exception) when (exception.Message == "managed_path") { extension = ".md"; priorPath = null; }
            }
            var typeOrigin = "user"; var originalProps = document.PropsJson; string? mime = null, sourceUrl = null;
            await using (var metadataLease = await state.OpenReadConnectionAsync(cancellationToken))
            await using (var metadataRead = Command(metadataLease.Connection, null, "SELECT type_origin,props_original_json,mime,source_url FROM documents WHERE id=$id", ("$id", id)))
            await using (var metadataReader = await metadataRead.ExecuteReaderAsync(cancellationToken))
            {
                if (await metadataReader.ReadAsync(cancellationToken))
                {
                    if (!payload.TryGetProperty("type", out _)) typeOrigin = metadataReader.GetString(0);
                    originalProps = metadataReader.GetString(1);
                    mime = metadataReader.IsDBNull(2) ? null : metadataReader.GetString(2);
                    sourceUrl = metadataReader.IsDBNull(3) ? null : metadataReader.GetString(3);
                }
            }
            var lifecycle = operation is "delete" or "restore" or "purge";
            var assistantNote = priorPath == files.GetAssistantNotePath(id);
            var targetPath = lifecycle ? files.GetLifecyclePath(id, extension, operation) : assistantNote ? files.GetAssistantNotePath(id) : files.GetManagedPath(id, extension);
            var projectionId = "mutation-projection:" + (approved?.ExecutionId ?? Ulid.NewUlid().ToString());
            var plannedProjection = operation == "purge" ? TextProcessor.Process(id, "[purged]", "", []) : publication.Projection! with
            {
                Aliases = durableProjection?.Aliases, SourceCreatedAt = durableProjection?.SourceCreatedAt,
                SourceUpdatedAt = durableProjection?.SourceUpdatedAt,
                Links = approved?.Plan.RelatedDocumentIds is { Length: > 0 } related
                    ? (durableProjection?.Links ?? []).Concat(related).Distinct(StringComparer.Ordinal).ToArray()
                    : hasContent ? null : durableProjection?.Links
            };
            var projectionBytes = JsonSerializer.SerializeToUtf8Bytes(plannedProjection with { Path = targetPath });
            await state.QueueWriteAsync(async (connection, transaction, token) =>
            {
                await SecondBrain.Storage.Auth.IngestionAuthorityValidator.ValidateAsync(connection, transaction, authority,
                    operation == "purge" ? SecondBrain.Core.Authorization.Scope.Admin : SecondBrain.Core.Authorization.Scope.Write, DateTimeOffset.UtcNow, sessionOnly: !additive, cancellationToken: token);
                await using var fence = Command(connection, transaction, "SELECT COUNT(*) FROM documents WHERE id=$id AND revision=$revision AND status!='purged'", ("$id", id), ("$revision", revision));
                if ((long)(await fence.ExecuteScalarAsync(token))! != 1) throw new IngestionConflictException("revision-mismatch");
                await using var body = Command(connection, transaction,
                    "INSERT OR IGNORE INTO payloads(id,sha256,bytes,document_refs_json,created_at) VALUES($payload,$hash,$bytes,$refs,$now)",
                    ("$payload", projectionId), ("$hash", Convert.ToHexStringLower(SHA256.HashData(projectionBytes))), ("$bytes", projectionBytes), ("$refs", JsonSerializer.Serialize(new[] { id })), ("$now", Now()));
                await body.ExecuteNonQueryAsync(token);
                await using var verify = Command(connection, transaction, "SELECT sha256 FROM payloads WHERE id=$payload AND redacted_at IS NULL", ("$payload", projectionId));
                if (await verify.ExecuteScalarAsync(token) as string != Convert.ToHexStringLower(SHA256.HashData(projectionBytes))) throw new IngestionConflictException("projection-changed");
                return 0;
            }, cancellationToken);
            var overrideValues = new Dictionary<string,string>(StringComparer.Ordinal);
            foreach (var kind in new[] { "type", "props", "tags", "title" })
                if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(kind, out var winning)) overrideValues[kind] = kind == "tags" ? JsonSerializer.Serialize(tags) : winning.GetRawText();
            var updateOccurred = operation == "purge" || payload.ValueKind == JsonValueKind.Object && (payload.TryGetProperty("props", out _) || payload.TryGetProperty("type", out _));
            var registry = new TypeRegistry();
            var effectiveProps = registry.DeriveEffectiveProperties(type, parsedProps.RootElement).GetRawText();
            var sourceZone = TimeZoneInfo.FindSystemTimeZoneById(options?.Value.Time.Zone ?? TimeZoneInfo.Local.Id);
            var occurrenceBasis = registry.GetProcessingType(type).Metadata.OccurredBasis;
            using var timeProps = JsonDocument.Parse(effectiveProps);
            var occurrenceValue = occurrenceBasis == "created" ? durableProjection?.SourceCreatedAt : occurrenceBasis is not null && timeProps.RootElement.TryGetProperty(occurrenceBasis, out var declaredTime) && declaredTime.ValueKind == JsonValueKind.String ? declaredTime.GetString() : null;
            var occurred = operation == "purge" || occurrenceBasis is null ? null : SourceTime.Parse(occurrenceValue, occurrenceBasis, sourceZone);
            var finalization = new MutationFinalization
            {
                MutationIdentity = approved?.Plan.MutationId, AssistantNote = assistantNote,
                PendingOperationId = approved?.ProposalId, ToolExecutionId = approved?.ExecutionId,
                DocumentId = id, RevisionBefore = revision, RevisionAfter = revision + 1, RevisionCause = operation, GenerationJson = Generation,
                SuppressDocument = operation is "delete" or "purge", ClearSuppression = operation == "restore", PurgeDocumentLineage = operation == "purge",
                DocumentMetadata = operation == "purge" ? new("[purged]", type, typeOrigin, "{}", "{}", status, projectionId, UpdateOccurred: true) : new(title, type, typeOrigin, effectiveProps, originalProps, status, projectionId, mime, sourceUrl, overrideValues, updateOccurred, occurred?.At, occurred?.Offset, occurred?.Basis, occurred?.Precision)
            };
            if (lifecycle) await files.ApplyLifecycleAsync(id, priorPath, extension, operation, expectedHash, bytes, finalization, cancellationToken);
            else await files.WriteRevisionAsync(id, bytes, extension, expectedHash, finalization, cancellationToken);
            if (await coordinator.PublishCurrentAsync(new(id, revision + 1, Generation), cancellationToken) != PublicationOutcome.Published) throw new IngestionConflictException("revision-mismatch");
        }
        else if (await coordinator.PublishAsync(publication, cancellationToken) != PublicationOutcome.Published) throw new IngestionConflictException("revision-mismatch");
        if (operation is "delete" or "restore" or "purge" && files is null)
        {
            await state.QueueWriteAsync(async (connection, transaction, token) =>
            {
                await using var change = Command(connection, transaction, operation == "restore"
                    ? "DELETE FROM suppressions WHERE scope='document' AND key=$id"
                    : "INSERT OR IGNORE INTO suppressions(id,scope,source_id,key,reason,created_at) VALUES($sid,'document','api-captures',$id,'app-delete',$now)",
                    ("$id", id), ("$sid", Ulid.NewUlid().ToString()), ("$now", Now()));
                await change.ExecuteNonQueryAsync(token);
                if (operation == "purge")
                {
                    await using var mark = Command(connection, transaction, "INSERT INTO meta(key,value) VALUES($key,$hash) ON CONFLICT(key) DO NOTHING", ("$key", PurgeCleanup.MarkerPrefix + id), ("$hash", document.ContentHash));
                    await mark.ExecuteNonQueryAsync(token);
                    await using var overrides = Command(connection, transaction, "DELETE FROM overrides WHERE document_id=$id", ("$id", id));
                    await overrides.ExecuteNonQueryAsync(token);
                    await using var redact = Command(connection, transaction,
                        "UPDATE payloads SET bytes=X'',sha256='',redacted_at=$now WHERE EXISTS(SELECT 1 FROM json_each(document_refs_json) WHERE value=$id)", ("$id", id), ("$now", Now()));
                    await redact.ExecuteNonQueryAsync(token);
                    await using var scrub = Command(connection, transaction,
                        "UPDATE documents SET title='[purged]',props_json='{}',props_original_json='{}',content_hash='',source_url=NULL,claimed_client=NULL,error=NULL WHERE id=$id", ("$id", id));
                    await scrub.ExecuteNonQueryAsync(token);
                    await using var cancel = Command(connection, transaction,
                        "UPDATE jobs SET status='cancelled',payload_json='{}',error=NULL WHERE document_id=$id", ("$id", id));
                    await cancel.ExecuteNonQueryAsync(token);
                }
                return 0;
            }, cancellationToken);
        }
        // The purge is durable and its marker makes this replayable after a crash; a disconnecting caller cannot cancel it.
        if (operation == "purge") await PurgeCleanup.CompleteAsync(state, index, id, CancellationToken.None);
        return new CaptureResult((await GetAsync(id, true, cancellationToken))!, null);
    }

    public async ValueTask<int> RebuildAsync(CancellationToken cancellationToken = default)
    {
        await index.QueueWriteAsync(async (connection, transaction, token) =>
        {
            // Ensure a physically deleted disposable index has its M1 tables before replay.
            await using var schema = Command(connection, transaction, SecondBrain.Storage.Migrations.M0Schema.IndexDdl + SecondBrain.Storage.Migrations.M1Schema.IndexDdl);
            return await schema.ExecuteNonQueryAsync(token);
        }, cancellationToken);
        var fences = new List<PublicationFence>();
        await using (var lease = await state.OpenReadConnectionAsync(cancellationToken))
        await using (var read = Command(lease.Connection, null, "SELECT d.id,d.revision,f.generation_json FROM documents d JOIN publication_fences f ON f.document_id=d.id ORDER BY d.id"))
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) fences.Add(new PublicationFence(reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
        var count = 0;
        foreach (var fence in fences) if (await coordinator.PublishCurrentAsync(fence, cancellationToken) == PublicationOutcome.Published) count++;
        await SqliteFtsMaintenance.ConsolidateAsync(index, cancellationToken);
        return count;
    }

    private static void Validate(CaptureRequest request)
    {
        if (request.Content is null || Encoding.UTF8.GetByteCount(request.Content) > 50 * 1024 * 1024 || request.Title?.Length > 1024 || request.Tags?.Length > 200 || request.Tags?.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Length > 256) == true)
            throw new ArgumentException("Capture exceeds input bounds.");
        var type = request.Type ?? "note";
        if (type is not ("note" or "article" or "file")) throw new ArgumentException("Unsupported M1 type.");
        using var empty = JsonDocument.Parse("{}");
        if (!new TypeRegistry().ValidateProperties(type, request.Props ?? empty.RootElement).IsValid) throw new ArgumentException("Invalid properties.");
    }
    private static string FirstLine(string content) => content.Split('\n', 2)[0].Trim() is { Length: > 0 } title ? title[..Math.Min(title.Length, 120)] : "Untitled";
    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    internal static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) { var p = command.CreateParameter(); p.ParameterName = name; p.Value = value ?? DBNull.Value; command.Parameters.Add(p); }
        return command;
    }
}
