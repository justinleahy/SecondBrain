using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Sources;
using SecondBrain.Infrastructure.FileSystem;
using SecondBrain.Server.Sources;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class SourcesTests
{
    [Fact]
    public async Task RegistersCanonicalFolderAndSchedulesNothing()
    {
        await using var factory = new LaneDWebFactory();
        var path = Directory.CreateDirectory(Path.Combine(factory.Options.CurrentValue.Sources.AllowedRoots[0], "inbox")).FullName;
        using var client = await AdminClientAsync(factory);
        using var response = await client.PostAsJsonAsync("/sources", new CreateSourceRequest
        {
            Path = path, Name = "inbox", Include = ["**/*.md"], Exclude = ["**/.git/**"], Mode = "import", Watch = "poll", PollIntervalSeconds = 20, DefaultType = "note",
            TypeMap = new Dictionary<string, string> { ["**/*.txt"] = "note" },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var source = await response.Content.ReadFromJsonAsync<SourceRecord>();
        Assert.NotNull(source);
        Assert.True(Ulid.TryParse(source.Id, out _));
        Assert.EndsWith(Path.DirectorySeparatorChar + "inbox", source.Config.Path, StringComparison.Ordinal);
        Assert.True(Directory.Exists(source.Config.Path));
        Assert.Equal("folder", source.Kind);
        Assert.Equal("import", source.Config.Mode);
        Assert.Equal("note", source.Config.DefaultType);
        Assert.Null(source.LastScanAt);
        Assert.Null(source.LastFullScanAt);
        Assert.Equal(factory.Clock.GetUtcNow(), source.CreatedAt);
        var listed = await client.GetFromJsonAsync<SourceRecord[]>("/v1/sources");
        Assert.Single(listed!);
        Assert.Equal(source.Id, listed![0].Id);
        Assert.Equal("note", listed[0].Config.TypeMap["**/*.txt"]);

        await using var lease = await factory.Store.OpenReadConnectionAsync();
        Assert.Equal(1, await lease.Connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM sources WHERE kind='folder' AND status='active'"));
        Assert.Contains("\"default_type\":\"note\"", await lease.Connection.QuerySingleAsync<string>("SELECT config_json FROM sources"), StringComparison.Ordinal);
        Assert.Equal(0, await lease.Connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM usage"));
        Assert.Equal(0, await lease.Connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM jobs"));
    }

    [Fact]
    public async Task RejectsOutsideRoots()
    {
        await using var factory = new LaneDWebFactory();
        var outside = Directory.CreateDirectory(Path.Combine(factory.Store.DirectoryPath, "outside")).FullName;
        using var client = await AdminClientAsync(factory);
        using var response = await client.PostAsJsonAsync("/sources", new { path = outside });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("source-path-rejected", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(await factory.Services.GetRequiredService<ISourceRepository>().ListAsync());
    }

    [Fact]
    public async Task DeniesDataRoot()
    {
        await using var factory = new LaneDWebFactory();
        // Deliberately bypass the validated loader to prove request-time defense in depth.
        factory.Options.CurrentValue.Sources.AllowedRoots.Add(factory.Options.CurrentValue.DataRoot);
        using var client = await AdminClientAsync(factory);
        using var response = await client.PostAsJsonAsync("/v1/sources", new { path = factory.Options.CurrentValue.DataRoot });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await factory.Services.GetRequiredService<ISourceRepository>().ListAsync());
    }

    [Fact]
    public async Task AncestorOfDataRootIsDenied()
    {
        await using var factory = new LaneDWebFactory();
        var parent = Path.GetDirectoryName(factory.Options.CurrentValue.DataRoot)!;
        factory.Options.CurrentValue.Sources.AllowedRoots.Add(parent);
        Assert.False(new SourcePathValidator(factory.Options).Validate(parent).Accepted);
    }

    [Fact]
    public async Task SymlinkOutsideRootsIsDenied()
    {
        await using var factory = new LaneDWebFactory();
        var outside = Directory.CreateDirectory(Path.Combine(factory.Store.DirectoryPath, "outside")).FullName;
        var link = Path.Combine(factory.Options.CurrentValue.Sources.AllowedRoots[0], "escape");
        Directory.CreateSymbolicLink(link, outside);
        using var client = await AdminClientAsync(factory);
        using var response = await client.PostAsJsonAsync("/sources", new { path = link });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await factory.Services.GetRequiredService<ISourceRepository>().ListAsync());
    }

    [Fact]
    public async Task SymlinkToDataRootIsDenied()
    {
        await using var factory = new LaneDWebFactory();
        var link = Path.Combine(factory.Options.CurrentValue.Sources.AllowedRoots[0], "private");
        Directory.CreateSymbolicLink(link, factory.Options.CurrentValue.DataRoot);
        using var client = await AdminClientAsync(factory);
        using var response = await client.PostAsJsonAsync("/sources", new { path = link });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/proc")]
    [InlineData("/sys")]
    [InlineData("/dev")]
    [InlineData("/etc/secondbrain")]
    [InlineData("/var/log")]
    [InlineData("/private/var/log")]
    [InlineData("/etc")]
    [InlineData("/")]
    public async Task ProtectedDirectoriesAreDeniedEvenWhenAllowed(string path)
    {
        await using var factory = new LaneDWebFactory();
        // A production YAML load rejects nonexistent/protected roots before this point.
        factory.Options.CurrentValue.Sources.AllowedRoots.Add(path);
        var validator = new SourcePathValidator(factory.Options);
        Assert.False(validator.Validate(path).Accepted);
        Assert.Empty(await factory.Services.GetRequiredService<ISourceRepository>().ListAsync());
    }

    [Fact]
    public async Task SymlinkedParentAndNestedTargetAreResolved()
    {
        await using var factory = new LaneDWebFactory();
        var allowed = factory.Options.CurrentValue.Sources.AllowedRoots[0];
        var outside = Directory.CreateDirectory(Path.Combine(factory.Store.DirectoryPath, "outside")).FullName;
        Directory.CreateDirectory(Path.Combine(outside, "child"));
        var alias = Path.Combine(factory.Store.DirectoryPath, "alias");
        Directory.CreateSymbolicLink(alias, outside);
        var escaped = Path.Combine(allowed, "escape");
        Directory.CreateSymbolicLink(escaped, Path.Combine(alias, "child"));
        Assert.False(new SourcePathValidator(factory.Options).Validate(escaped).Accepted);
        Assert.False(new SourcePathValidator(factory.Options).Validate(Path.Combine(escaped, "..")).Accepted);
    }

    [Fact]
    public async Task PhysicalDataRootIsDeniedWhenConfigurationUsesAlias()
    {
        await using var factory = new LaneDWebFactory();
        var actual = factory.Options.CurrentValue.DataRoot;
        var alias = Path.Combine(factory.Store.DirectoryPath, "data-alias");
        Directory.CreateSymbolicLink(alias, actual);
        factory.Options.CurrentValue.DataRoot = alias;
        factory.Options.CurrentValue.Sources.AllowedRoots.Add(actual);
        Assert.False(new SourcePathValidator(factory.Options).Validate(actual).Accepted);
    }

    [Fact]
    public async Task PrefixSiblingAndNonexistentPathAreDenied()
    {
        await using var factory = new LaneDWebFactory();
        var allowed = factory.Options.CurrentValue.Sources.AllowedRoots[0];
        var sibling = Directory.CreateDirectory(allowed + "-sibling").FullName;
        var validator = new SourcePathValidator(factory.Options);
        Assert.False(validator.Validate(sibling).Accepted);
        Assert.False(validator.Validate(Path.Combine(allowed, "does-not-exist")).Accepted);
        Assert.False(validator.Validate("relative/path").Accepted);
    }

    [Fact]
    public async Task AlternateCaseCannotBypassProtectedRoot()
    {
        await using var factory = new LaneDWebFactory(options => options.Sources.AllowedRoots.Add(Path.GetDirectoryName(options.DataRoot)!));
        var actual = factory.Options.CurrentValue.DataRoot;
        var alternate = actual.ToUpperInvariant();
        var validator = new SourcePathValidator(factory.Options);
        Assert.False(validator.Validate(alternate).Accepted);
        // A volume that treats the alternate spelling as a distinct, nonexistent path also denies it.
        Assert.True(Directory.Exists(actual));
    }

    [Fact]
    public async Task SymlinkToAllowedDirectoryStoresPhysicalPath()
    {
        await using var factory = new LaneDWebFactory();
        var allowed = factory.Options.CurrentValue.Sources.AllowedRoots[0];
        var actual = Directory.CreateDirectory(Path.Combine(allowed, "real")).FullName;
        await File.WriteAllTextAsync(Path.Combine(actual, "proof.txt"), "same directory");
        var alias = Path.Combine(allowed, "alias");
        Directory.CreateSymbolicLink(alias, actual);
        using var client = await AdminClientAsync(factory);
        using var response = await client.PostAsJsonAsync("/sources", new { path = alias });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var canonical = (await response.Content.ReadFromJsonAsync<SourceRecord>())!.Config.Path;
        Assert.EndsWith(Path.DirectorySeparatorChar + "real", canonical, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.DirectorySeparatorChar + "alias", canonical, StringComparison.Ordinal);
        Assert.Equal("same directory", await File.ReadAllTextAsync(Path.Combine(canonical, "proof.txt")));
    }

    [Theory]
    [InlineData("mode", "copy")]
    [InlineData("watch", "continuous")]
    [InlineData("name", "")]
    public async Task InvalidSourceSchemasAreRejected(string field, string value)
    {
        await using var factory = new LaneDWebFactory();
        using var client = await AdminClientAsync(factory);
        using var response = await client.PostAsJsonAsync("/sources", new Dictionary<string, object>
        {
            ["path"] = factory.Options.CurrentValue.Sources.AllowedRoots[0], [field] = value,
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await factory.Services.GetRequiredService<ISourceRepository>().ListAsync());
    }

    [Fact]
    public async Task ReadKeyCannotRegisterOrListSources()
    {
        await using var factory = new LaneDWebFactory();
        using var client = await AdminClientAsync(factory, Scope.Read);
        using var create = await client.PostAsJsonAsync("/sources", new { path = factory.Options.CurrentValue.Sources.AllowedRoots[0] });
        using var list = await client.GetAsync("/sources");
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Empty(await factory.Services.GetRequiredService<ISourceRepository>().ListAsync());
    }

    private static async Task<HttpClient> AdminClientAsync(LaneDWebFactory factory, Scope scope = Scope.Admin)
    {
        var repository = factory.Services.GetRequiredService<IAuthRepository>();
        var created = factory.Services.GetRequiredService<CredentialFactory>().CreateApiKey("source-test", new HashSet<Scope> { scope }, await repository.GetEpochAsync());
        await repository.AddAsync(created.Record);
        var client = factory.CreatePrivateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", created.Plaintext);
        return client;
    }
}
