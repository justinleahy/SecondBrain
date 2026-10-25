using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Operations;
using SecondBrain.Server.Auth;
using SecondBrain.Storage.Backup;
using Xunit;

namespace SecondBrain.Deploy.Tests;

public sealed partial class CliConvergenceTests
{
    [Fact]
    [Trait("Category", "Qualification")]
    public async Task M4BackupUnderSustainedHttpEditsRestoresAndRestartsTheFullDaemon()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("SECONDBRAIN_M4_DAEMON_BACKUP"));
        var output = Environment.GetEnvironmentVariable("SECONDBRAIN_M4_DAEMON_BACKUP_OUTPUT")!;
        Directory.CreateDirectory(output);
        await using var daemon = await CliDaemon.CreateAsync(forBackup: true);
        using var browser = daemon.Browser(); await SignIn(browser);
        async Task<DocumentRecord> Capture(string text, string title)
        {
            using var response = await browser.PostAsJsonAsync("/v1/documents", new CaptureRequest(text, title));
            response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<CaptureResult>())!.Document;
        }
        var note = await Capture("checkpoint0000 sustained backup note", "Sustained edits");
        var trash = await Capture("Retained trash during sustained edits", "Sustained backup trash");
        using (var request = new HttpRequestMessage(HttpMethod.Delete, "/v1/documents/" + trash.Id))
        {
            request.Headers.TryAddWithoutValidation("If-Match", "\"" + trash.Revision + "\"");
            using var response = await browser.SendAsync(request); response.EnsureSuccessStatusCode();
        }
        // Enough real managed content to exercise streaming while writes continue.
        for (var i = 0; i < 16; i++) await Capture(new string('x', 128 * 1024) + " synthetic backup padding " + i, "Backup padding " + i);
        await daemon.LoginAdmin();
        var oldKey = await daemon.Credentials.ReadAsync(daemon.Origin, "admin-cli", CancellationToken.None);
        Assert.NotNull(oldKey);
        var written = 0; var revision = note.Revision;
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        async Task EditContinuously()
        {
            while (!stop.IsCancellationRequested)
            {
                var next = Volatile.Read(ref written) + 1;
                using var request = new HttpRequestMessage(HttpMethod.Patch, "/v1/documents/" + note.Id)
                {
                    Content = JsonContent.Create(new { content = "checkpoint" + next.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + " sustained backup note " + new string('y', 4096) })
                };
                request.Headers.TryAddWithoutValidation("If-Match", "\"" + revision + "\"");
                using var response = await browser.SendAsync(request); response.EnsureSuccessStatusCode();
                revision = (await response.Content.ReadFromJsonAsync<CaptureResult>())!.Document.Revision;
                Interlocked.Increment(ref written);
            }
        }
        var writer = EditContinuously();
        var archives = new List<(BackupCreated Created, string Path, int ConcurrentEdits)>();
        try
        {
            while (Volatile.Read(ref written) < 4) { Assert.False(writer.IsCompleted); await Task.Delay(10); }
            for (var i = 0; i < 2; i++)
            {
                var before = Volatile.Read(ref written);
                using var response = await browser.PostAsync("/v1/maintenance/backup", null);
                response.EnsureSuccessStatusCode();
                var made = (await response.Content.ReadFromJsonAsync<BackupCreated>())!;
                Assert.False(made.Archive.Partial); Assert.Equal(0, made.PartialFiles);
                var concurrent = Volatile.Read(ref written) - before; Assert.True(concurrent > 0);
                var path = Path.Combine(daemon.Workspace, "qualification-" + i + ".sbk");
                await File.WriteAllBytesAsync(path, await browser.GetByteArrayAsync("/v1/maintenance/backups/" + made.Archive.Name));
                archives.Add((made, path, concurrent));
            }
        }
        finally { stop.Cancel(); await writer; }
        var key = await File.ReadAllBytesAsync(Path.Combine(daemon.Workspace, "backup.key"));
        var staging = Directory.CreateDirectory(Path.Combine(daemon.Workspace, "verify")).FullName;
        var snapshots = new List<object>(); long capturedRevision = 0; string? capturedText = null;
        foreach (var archive in archives)
        {
            await using var input = File.OpenRead(archive.Path);
            await using var verified = await BackupPackage.VerifyAndStageAsync(input, key, staging);
            Assert.Equal(0, verified.Summary.Partial);
            var entries = new List<BackupPackageEntry>(); await foreach (var entry in verified.FilesAsync()) entries.Add(entry);
            var noteEntry = Assert.Single(entries, entry => entry.DocumentId == note.Id);
            Assert.NotNull(noteEntry.Revision);
            await using var state = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(verified.ContentPath, "state", "state.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await state.OpenAsync(); Assert.Equal("ok", await state.ExecuteScalarAsync<string>("PRAGMA integrity_check"));
            capturedRevision = await state.ExecuteScalarAsync<long>("SELECT revision FROM documents WHERE id=@id", new { id = note.Id });
            Assert.Equal(capturedRevision, noteEntry.Revision);
            Assert.Equal(noteEntry.Sha256, await state.ExecuteScalarAsync<string>("SELECT content_hash FROM documents WHERE id=@id", new { id = note.Id }));
            Assert.Equal("deleted", await state.ExecuteScalarAsync<string>("SELECT status FROM documents WHERE id=@id", new { id = trash.Id }));
            Assert.Contains(entries, entry => entry.Root == "trash" && entry.DocumentId == trash.Id);
            var projection = await state.ExecuteScalarAsync<byte[]>("SELECT p.bytes FROM document_projections d JOIN payloads p ON p.id=d.payload_id WHERE d.document_id=@id", new { id = note.Id });
            capturedText = JsonSerializer.Deserialize<TextProjection>(projection)!.LosslessText;
            foreach (var entry in entries.Where(entry => entry.DocumentId is not null))
            {
                var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(verified.ContentPath, entry.Root, entry.Path))));
                Assert.Equal(entry.Sha256, hash);
                Assert.Equal(hash, await state.ExecuteScalarAsync<string>("SELECT content_hash FROM documents WHERE id=@id", new { id = entry.DocumentId }));
            }
            snapshots.Add(new { archive.Created.Files, verified.Summary.Complete, archive.ConcurrentEdits, capturedRevision, manifestVerified = true, sqliteIntegrity = "ok", trashIncluded = true });
        }
        Assert.True(revision > capturedRevision);
        await daemon.StopHost();
        var restored = await daemon.Run(["maintenance", "restore", archives[^1].Path, "--json"]);
        Assert.True(restored.Exit == 0, restored.Output + restored.Error);
        await daemon.StartHost();
        using (var stale = daemon.Browser())
        {
            stale.DefaultRequestHeaders.Authorization = new("Bearer", oldKey);
            using var rejected = await stale.GetAsync("/v1/documents/" + note.Id);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }
        using var recovered = daemon.Browser(); await SignIn(recovered);
        var document = (await recovered.GetFromJsonAsync<DocumentRecord>("/v1/documents/" + note.Id + "?include=text"))!;
        Assert.Equal(capturedRevision, document.Revision); Assert.Equal(capturedText, document.Text);
        var recoveredTrash = (await recovered.GetFromJsonAsync<CatalogPage<TrashedDocument>>("/v1/trash"))!;
        var retained = Assert.Single(recoveredTrash.Items, item => item.Id == trash.Id);
        using (var restoreTrash = new HttpRequestMessage(HttpMethod.Post, "/v1/documents/" + trash.Id + "/restore"))
        {
            restoreTrash.Headers.TryAddWithoutValidation("If-Match", "\"" + retained.Revision + "\"");
            using var response = await recovered.SendAsync(restoreTrash); response.EnsureSuccessStatusCode();
        }
        var untrashed = (await recovered.GetFromJsonAsync<DocumentRecord>("/v1/documents/" + trash.Id + "?include=text"))!;
        Assert.Equal("Retained trash during sustained edits", untrashed.Text);
        using var query = await recovered.PostAsJsonAsync("/v1/search", new { query = capturedText!.Split(' ')[0], mode = "keyword" }); query.EnsureSuccessStatusCode();
        var results = await query.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(results.GetProperty("results").EnumerateArray(), hit => hit.GetProperty("document_id").GetString() == note.Id);
        await File.WriteAllTextAsync(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(new
        {
            syntheticData = true, controlledLocalProvider = true, realExtractorProtocol = true, fullDaemonComposition = true,
            sameUserFixture = true, hardenedDeploymentQualified = false, productionChanged = false,
            totalHttpEdits = written, archives = snapshots, offlineCliRestore = true, daemonRestarted = true,
            capturedRevisionRestored = true, rebuiltSearchVerified = true, oldCredentialRejected = true, trashRestoredAfterRestart = true,
            limitations = new[] { "Disposable same-user Kestrel host, not systemd/Compose isolation.", "Small synthetic corpus; mounted roots and large-corpus resource limits remain separate gates.", "No external calendar service or cloud model used." }
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n");

        static async Task SignIn(HttpClient client)
        {
            foreach (var path in new[] { "/auth/login", "/auth/step-up" })
            {
                var csrf = (await client.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!;
                client.DefaultRequestHeaders.Remove(csrf.HeaderName); client.DefaultRequestHeaders.Add(csrf.HeaderName, csrf.Token);
                using var response = await client.PostAsJsonAsync(path, new LoginRequest(Password)); response.EnsureSuccessStatusCode();
            }
            var final = (await client.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!;
            client.DefaultRequestHeaders.Remove(final.HeaderName); client.DefaultRequestHeaders.Add(final.HeaderName, final.Token);
        }
    }
}
