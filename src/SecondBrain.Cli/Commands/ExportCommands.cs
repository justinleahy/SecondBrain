using System.Globalization;

namespace SecondBrain.Cli.Commands;

public sealed partial class HttpDaemonCommands
{
    public async Task<CliResult> ExportAsync(string? destination, CancellationToken token)
    {
        if (destination is not null && File.Exists(destination)) throw new CliPreconditionException("The export destination already exists.");
        using var client = await OpenAsync(apiKey: false, token); client.Timeout = TimeSpan.FromHours(1);
        var password = await readPassword("export", token); await LoginAsync(client, password, token);
        try
        {
            await StepUpAsync(client, password, token);
            using var response = await SendAsync(client, HttpMethod.Post, "/v1/maintenance/export", new { }, token, HttpCompletionOption.ResponseHeadersRead);
            var disposition = response.Content.Headers.ContentDisposition;
            var name = (disposition?.FileNameStar ?? disposition?.FileName)?.Trim('"');
            if (name is null || name.Length > 200 || name != Path.GetFileName(name) || name.Contains('\\') || !name.EndsWith(".tar", StringComparison.Ordinal))
                throw new CliPreconditionException("The daemon returned an invalid export name.");
            var expected = response.Content.Headers.ContentLength ?? throw new CliPreconditionException("The daemon returned no export length.");
            var path = Path.GetFullPath(destination is null ? name : Directory.Exists(destination) ? Path.Combine(destination, name) : destination);
            if (Path.Exists(path)) throw new CliPreconditionException("The export destination already exists.");
            var parent = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(parent)) throw new CliPreconditionException("The export destination directory does not exist.");
            var temporary = Path.Combine(parent, ".export-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var output = new FileStream(temporary, options))
                {
                    await using var input = await response.Content.ReadAsStreamAsync(token);
                    var buffer = new byte[65536]; long bytes = 0;
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer, token); if (read == 0) break;
                        bytes = checked(bytes + read); if (bytes > expected) throw new IOException("Export length changed.");
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                    }
                    if (bytes != expected) throw new IOException("Export was incomplete.");
                    output.Flush(true);
                }
                File.Move(temporary, path, false);
            }
            finally { File.Delete(temporary); }
            var partial = response.Headers.TryGetValues("X-SecondBrain-Export-Partial", out var values)
                && long.TryParse(values.Single(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count > 0;
            return new(partial ? CliExitCode.PreconditionFailed : CliExitCode.Ok, partial ? "export-partial" : "ok",
                partial ? "Portable export downloaded with unavailable files; review its manifest." : "Portable export downloaded. It contains private knowledge in plaintext; authentication secrets are excluded.", new { path, bytes = expected, partial });
        }
        finally { await LogoutAsync(client, token); }
    }
}
