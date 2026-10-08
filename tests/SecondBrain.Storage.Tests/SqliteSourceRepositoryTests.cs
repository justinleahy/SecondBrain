using System.Text.Json;
using SecondBrain.Core.Sources;
using SecondBrain.Storage.Sources;
using Xunit;

namespace SecondBrain.Storage.Tests;

/// <summary>Characterizes the sources SQL relocated from the daemon into Storage.</summary>
public sealed class SqliteSourceRepositoryTests
{
    private static readonly string[] ConfigJsonKeys =
        ["path", "include", "exclude", "recursive", "mode", "enrich", "default_type", "type_map", "watch", "poll_interval_s"];

    [Fact]
    public async Task AddThenListRoundTripsTheSourceAndStoresTheSpecificationConfigKeys()
    {
        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path);
        await stores.Runner.MigrateAsync();
        var repository = new SqliteSourceRepository(stores.State);
        var source = new SourceRecord(
            "01JBSOURCE0000000000000000", "folder", "notes",
            new FolderSourceConfiguration(
                "/srv/notes", ["**/*.md"], ["drafts/**"], Recursive: false, Mode: "import", Enrich: true,
                DefaultType: "note", TypeMap: new Dictionary<string, string> { ["journal/**"] = "journal" },
                Watch: "poll", PollIntervalSeconds: 120),
            "active", null, null, new DateTimeOffset(2026, 10, 8, 12, 30, 0, TimeSpan.Zero));

        await repository.AddAsync(source);

        var listed = Assert.Single(await repository.ListAsync());
        Assert.Equal(source.Id, listed.Id);
        Assert.Equal(source.Kind, listed.Kind);
        Assert.Equal(source.Name, listed.Name);
        Assert.Equal(source.Status, listed.Status);
        Assert.Null(listed.LastScanAt);
        Assert.Null(listed.LastFullScanAt);
        Assert.Equal(source.CreatedAt, listed.CreatedAt);
        Assert.Equal(source.Config.Path, listed.Config.Path);
        Assert.Equal(source.Config.Include, listed.Config.Include);
        Assert.Equal(source.Config.Exclude, listed.Config.Exclude);
        Assert.Equal(source.Config.Recursive, listed.Config.Recursive);
        Assert.Equal(source.Config.Mode, listed.Config.Mode);
        Assert.Equal(source.Config.Enrich, listed.Config.Enrich);
        Assert.Equal(source.Config.DefaultType, listed.Config.DefaultType);
        Assert.Equal(source.Config.TypeMap, listed.Config.TypeMap);
        Assert.Equal(source.Config.Watch, listed.Config.Watch);
        Assert.Equal(source.Config.PollIntervalSeconds, listed.Config.PollIntervalSeconds);

        var stored = (string?)await MigrationTestSql.ScalarAsync(stores.State, $"SELECT config_json FROM sources WHERE id = '{source.Id}';");
        Assert.NotNull(stored);
        using var document = JsonDocument.Parse(stored);
        Assert.Equal(ConfigJsonKeys, document.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
    }
}
