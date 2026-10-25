using System.Formats.Tar;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SecondBrain.Core.Operations;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Sources;
using SecondBrain.Server.Auth;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class ExportApiTests
{
    [Theory]
    [InlineData("")]
    [InlineData("/v1")]
    public async Task ExportIsConsistentCompleteAndExcludesAuthenticationAndCalendarSecrets(string prefix)
    {
        await using var factory = new LaneDWebFactory(); await factory.SeedAccountAsync();
        using var client = factory.CreatePrivateClient(); client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        using var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest("test-password")); login.EnsureSuccessStatusCode(); await Csrf(client);
        using var denied = await client.PostAsJsonAsync(prefix + "/maintenance/export", new { }); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var step = await client.PostAsJsonAsync("/auth/step-up", new LoginRequest("test-password")); step.EnsureSuccessStatusCode(); await Csrf(client);
        using var capture = await client.PostAsJsonAsync("/documents", new CaptureRequest("Export this complete document", "Export note")); capture.EnsureSuccessStatusCode();
        var document = (await capture.Content.ReadFromJsonAsync<CaptureResult>())!.Document;
        using var trash = new HttpRequestMessage(HttpMethod.Delete, "/documents/" + document.Id); trash.Headers.TryAddWithoutValidation("If-Match", "\"" + document.Revision + "\"");
        using var trashed = await client.SendAsync(trash); trashed.EnsureSuccessStatusCode();
        await factory.Store.QueueWriteAsync(async (c, t, ct) =>
        {
            await c.ExecuteAsync(new CommandDefinition("""
                INSERT INTO sources(id,kind,name,config_json,status,created_at) VALUES('export-calendar','calendar_subscription','Calendar','{}','active','2026-01-01');
                INSERT INTO calendar_subscriptions(source_id,container_id,origin,protected_url,poll_interval_s,etag,last_modified)
                VALUES('export-calendar','export-calendar-container','https://calendar.example.invalid','PRIVATE-CALENDAR-ENVELOPE',3600,'PRIVATE-ETAG','PRIVATE-LAST-MODIFIED');
                INSERT INTO cli_pairings(id,code,name,poll_verifier,source,secure,created_at,expires_at,status)
                VALUES('export-pair','PRIVATE-PAIRING-CODE','pair','PRIVATE-POLL-VERIFIER','test',0,'2026-01-01','2027-01-01','pending');
                """, transaction: t, cancellationToken: ct)); return 0;
        });
        using var response = await client.PostAsJsonAsync(prefix + "/maintenance/export", new { }); response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl?.NoStore); Assert.Equal("application/x-tar", response.Content.Headers.ContentType!.MediaType);
        var archive = await response.Content.ReadAsByteArrayAsync(); var text = Encoding.UTF8.GetString(archive);
        foreach (var secret in new[] { "PRIVATE-CALENDAR-ENVELOPE", "PRIVATE-ETAG", "PRIVATE-LAST-MODIFIED", "PRIVATE-PAIRING-CODE", "PRIVATE-POLL-VERIFIER" }) Assert.DoesNotContain(secret, text);
        var entries = await Read(archive);
        Assert.DoesNotContain(entries.Keys, name => name.Contains("keyring", StringComparison.Ordinal) || name.EndsWith("state.db", StringComparison.Ordinal));
        Assert.False(entries.ContainsKey("data/account.jsonl")); Assert.False(entries.ContainsKey("data/credentials.jsonl"));
        Assert.Contains(document.Id, Encoding.UTF8.GetString(entries["data/documents.jsonl"]));
        Assert.Contains(entries, entry => entry.Key.StartsWith("files/trash/", StringComparison.Ordinal) && Encoding.UTF8.GetString(entry.Value).Contains("Export this complete document", StringComparison.Ordinal));
        var metadata = JsonDocument.Parse(entries["export.json"]); Assert.Equal(1, metadata.RootElement.GetProperty("version").GetInt32());
        var manifest = Encoding.UTF8.GetString(entries["manifest.jsonl"]).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            foreach (var row in manifest)
            {
                var item = row.RootElement; var name = item.GetProperty("path").GetString()!;
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(entries[name])), item.GetProperty("sha256").GetString());
                Assert.Equal(entries[name].LongLength, item.GetProperty("bytes").GetInt64());
            }
            Assert.Equal(entries.Count - 1, manifest.Length);
        }
        finally { foreach (var item in manifest) item.Dispose(); metadata.Dispose(); }
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(factory.Options.CurrentValue.DataRoot, "staging"), "export-*"));
    }

    [Fact]
    public async Task PortableNamesCannotEscapeOrCollideWhenExtractedOnAnotherPlatform()
    {
        await using var factory = new LaneDWebFactory();
        var source = Directory.CreateDirectory(Path.Combine(factory.Options.CurrentValue.Sources.AllowedRoots[0], "portable-names")).FullName;
        var names = new[] { "..\\outside.txt", "CON", "trailing.", "Upper.txt", "upper.txt", new string('x', 210) };
        foreach (var name in names) await File.WriteAllTextAsync(Path.Combine(source, name), name);
        await factory.Services.GetRequiredService<ISourceRepository>().AddAsync(new("portable-export", "folder", "Portable",
            new(source, ["**"], [], true, "index", false, null, new Dictionary<string, string>(), "poll", 3600), "disabled", null, null, DateTimeOffset.UtcNow));
        using var client = factory.CreatePrivateClient(false); var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Admin });
        client.DefaultRequestHeaders.Authorization = new("Bearer", key.Plaintext);
        using var response = await client.PostAsJsonAsync("/maintenance/export", new { }); response.EnsureSuccessStatusCode();
        var entries = await Read(await response.Content.ReadAsByteArrayAsync());
        var files = entries.Keys.Where(name => name.StartsWith("files/external/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(names.Length, files.Length); Assert.Equal(files.Length, files.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(files, name => { Assert.DoesNotContain('\\', name); Assert.DoesNotContain(':', name); Assert.All(name.Split('/'), part => { Assert.NotEqual("..", part); Assert.False(part.EndsWith('.')); }); });
        var original = Encoding.UTF8.GetString(entries["manifest.jsonl"]).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var exported = original.Where(row => row.RootElement.TryGetProperty("source_root", out var root) && root.ValueKind == JsonValueKind.String && root.GetString()!.StartsWith("external/", StringComparison.Ordinal))
                .Select(row => row.RootElement.GetProperty("source_path").GetString()).ToArray();
            Assert.Equal(names.Order(StringComparer.Ordinal), exported.Order(StringComparer.Ordinal));
        }
        finally { foreach (var row in original) row.Dispose(); }
    }

    [Fact]
    public async Task ALinkedParentCannotRedirectAnExportToPrivateFiles()
    {
        await using var factory = new LaneDWebFactory(configureServices: services => services.Replace(ServiceDescriptor.Singleton<IBackupKeyProvider>(new Keys())));
        var outside = Directory.CreateDirectory(Path.Combine(factory.Options.CurrentValue.DataRoot, "private-tree", "leaf")).FullName;
        await File.WriteAllTextAsync(Path.Combine(outside, "private.txt"), "PRIVATE-ANCESTOR-REDIRECTION");
        var link = Path.Combine(factory.Options.CurrentValue.Sources.AllowedRoots[0], "linked-parent");
        Directory.CreateSymbolicLink(link, Path.GetDirectoryName(outside)!);
        await factory.Services.GetRequiredService<ISourceRepository>().AddAsync(new("linked-export", "folder", "Linked",
            new(Path.Combine(link, "leaf"), ["**"], [], true, "index", false, null, new Dictionary<string, string>(), "poll", 3600), "disabled", null, null, DateTimeOffset.UtcNow));
        using var client = factory.CreatePrivateClient(false); var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Admin });
        client.DefaultRequestHeaders.Authorization = new("Bearer", key.Plaintext);
        using var response = await client.PostAsJsonAsync("/maintenance/export", new { });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("PRIVATE-ANCESTOR-REDIRECTION", await response.Content.ReadAsStringAsync());
        using var backup = await client.PostAsJsonAsync("/maintenance/backup", new { });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, backup.StatusCode);
    }

    [Fact]
    public async Task UnreadableSourceEntriesProduceALabelledPartialExportWithoutFollowingLinks()
    {
        await using var factory = new LaneDWebFactory();
        var source = Directory.CreateDirectory(Path.Combine(factory.Options.CurrentValue.Sources.AllowedRoots[0], "export-source")).FullName;
        await File.WriteAllTextAsync(Path.Combine(source, "included.txt"), "Included source text");
        var outside = Path.Combine(factory.Options.CurrentValue.DataRoot, "outside.txt"); await File.WriteAllTextAsync(outside, "Do not export through this link");
        File.CreateSymbolicLink(Path.Combine(source, "linked.txt"), outside);
        await factory.Services.GetRequiredService<ISourceRepository>().AddAsync(new("external-export", "folder", "External",
            new(source, ["**"], [], true, "index", false, null, new Dictionary<string, string>(), "poll", 3600), "disabled", null, null, DateTimeOffset.UtcNow));
        using var client = factory.CreatePrivateClient(false); var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Admin });
        client.DefaultRequestHeaders.Authorization = new("Bearer", key.Plaintext);
        using var response = await client.PostAsJsonAsync("/maintenance/export", new { }); response.EnsureSuccessStatusCode();
        Assert.Equal("1", response.Headers.GetValues("X-SecondBrain-Export-Partial").Single());
        Assert.Contains(".partial.tar", response.Content.Headers.ContentDisposition!.FileNameStar);
        var bytes = await response.Content.ReadAsByteArrayAsync(); Assert.DoesNotContain("Do not export through this link", Encoding.UTF8.GetString(bytes));
        var entries = await Read(bytes);
        Assert.Contains(entries, entry => entry.Key.EndsWith("/f-included.txt", StringComparison.Ordinal) && Encoding.UTF8.GetString(entry.Value) == "Included source text");
        var manifest = Encoding.UTF8.GetString(entries["manifest.jsonl"]); Assert.Contains("linked_entry", manifest);
    }

    [Fact]
    public async Task UnreviewedSchemaCannotLeakNewColumnsAndReaderCannotExport()
    {
        await using var factory = new LaneDWebFactory();
        var reader = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read }); using var client = factory.CreatePrivateClient(false);
        client.DefaultRequestHeaders.Authorization = new("Bearer", reader.Plaintext);
        using var denied = await client.PostAsJsonAsync("/maintenance/export", new { }); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var admin = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Admin }); client.DefaultRequestHeaders.Authorization = new("Bearer", admin.Plaintext);
        await factory.Store.QueueWriteAsync(async (c, t, ct) => { await c.ExecuteAsync(new CommandDefinition("ALTER TABLE documents ADD COLUMN unreviewed_secret TEXT", transaction: t, cancellationToken: ct)); return 0; });
        using var rejected = await client.PostAsJsonAsync("/maintenance/export", new { }); Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        Assert.DoesNotContain("unreviewed_secret", await rejected.Content.ReadAsStringAsync());
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(factory.Options.CurrentValue.DataRoot, "staging"), "export-*"));
    }

    private static async Task<Dictionary<string, byte[]>> Read(byte[] archive)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await using var reader = new TarReader(new MemoryStream(archive));
        while (await reader.GetNextEntryAsync() is { } entry)
        {
            Assert.Equal(TarEntryType.RegularFile, entry.EntryType);
            using var output = new MemoryStream(); if (entry.DataStream is not null) await entry.DataStream.CopyToAsync(output); result.Add(entry.Name, output.ToArray());
        }
        return result;
    }
    private static async Task Csrf(HttpClient client)
    { var csrf = (await client.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!; client.DefaultRequestHeaders.Remove(csrf.HeaderName); client.DefaultRequestHeaders.Add(csrf.HeaderName, csrf.Token); }
    private sealed class Keys : IBackupKeyProvider
    { public ValueTask<BackupKey> ReadAsync(CancellationToken token = default) => ValueTask.FromResult(new BackupKey(new byte[32])); }
}
