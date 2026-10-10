using System.Net.Http.Headers;
using System.Text.Json;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Retrieval;

namespace SecondBrain.Cli.Commands;

public sealed partial class HttpDaemonCommands : IKnowledgeCommands
{
    public const long MaximumUploadBytes = 50 * 1024 * 1024;
    public const int MaximumCaptureCharacters = 1_000_000;

    public async Task<CliResult> AddAsync(string path, CaptureRequest metadata, int wait, string? idempotencyKey, TextReader input, CancellationToken cancellationToken)
    {
        if (wait is < 0 or > 30) throw new CliUsageException("--wait must be between 0 and 30 seconds.");
        if (idempotencyKey is not null && (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200))
            throw new CliUsageException("--idempotency-key must contain 1–200 characters.");
        using var client = await OpenAsync(apiKey: true, cancellationToken);
        // The server's maximum bounded wait may take the full thirty seconds after upload.
        client.Timeout = TimeSpan.FromMinutes(3);
        if (idempotencyKey is not null) client.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);
        object body;
        Stream? file = null;
        try
        {
            if (path == "-")
            {
                var text = new System.Text.StringBuilder();
                var buffer = new char[8192];
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
                {
                    if (text.Length + read > MaximumCaptureCharacters) throw new CliUsageException("Standard input exceeds the capture text limit.");
                    text.Append(buffer, 0, read);
                }
                body = metadata with { Content = text.ToString() };
            }
            else
            {
                if (!File.Exists(path)) throw new CliUsageException("The input file does not exist.");
                var info = new FileInfo(path);
                if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.Directory)) throw new CliUsageException("Input must be a regular file.");
                file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (file.Length > MaximumUploadBytes) throw new CliUsageException("Input exceeds the 50 MiB upload limit.");
                var multipart = new MultipartFormDataContent();
                multipart.Add(new StringContent(JsonSerializer.Serialize(metadata, Json), System.Text.Encoding.UTF8, "application/json"), "metadata");
                var content = new StreamContent(file);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                multipart.Add(content, "file", Path.GetFileName(path));
                body = multipart;
            }
            using var response = await SendAsync(client, HttpMethod.Post, "/v1/documents" + (wait > 0 ? "?wait=" + wait : ""), body, cancellationToken);
            var data = await ReadJsonAsync(response, cancellationToken);
            return CliResult.Ok("Capture accepted; projection and job status are included in the result.", data);
        }
        finally { if (file is not null) await file.DisposeAsync(); }
    }

    public Task<CliResult> SearchAsync(SearchRequest request, CancellationToken cancellationToken) =>
        KnowledgePostAsync("/v1/search", request, "Search completed; degraded reasons are included in the result.", cancellationToken);
    public Task<CliResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken) =>
        KnowledgePostAsync("/v1/query", request, "Query completed; continuation and coverage are included in the result.", cancellationToken);

    private async Task<CliResult> KnowledgePostAsync(string path, object body, string message, CancellationToken cancellationToken)
    {
        using var client = await OpenAsync(apiKey: true, cancellationToken);
        using var response = await SendAsync(client, HttpMethod.Post, path, body, cancellationToken);
        return CliResult.Ok(message, await ReadJsonAsync(response, cancellationToken));
    }
}
