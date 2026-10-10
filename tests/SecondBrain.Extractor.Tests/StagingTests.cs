using System.Runtime.InteropServices;
using System.Text;
using SecondBrain.Infrastructure.Ingestion;
using Xunit;
using SecondBrain.Core.Sources;
using SecondBrain.Core.Ingestion;

namespace SecondBrain.Extractor.Tests;

public sealed class StagingTests
{
    [Fact]
    public async Task StageIsUnlinkedReadOnlyAndImmutableAfterSourceChanges()
    {
        using var fixture = new Fixture(); var source = Path.Combine(fixture.Root, "note.md");
        await File.WriteAllTextAsync(source, "Original bytes");
        using var stager = new InputStager(fixture.Stage);
        await using var stage = await stager.OpenSourceAsync(fixture.Root, "note.md");
        await File.WriteAllTextAsync(source, "Replaced bytes");
        Assert.Empty(Directory.GetFiles(fixture.Stage)); Assert.False(stage.Content.CanWrite);
        using var reader = new StreamReader(stage.Content); Assert.Equal("Original bytes", await reader.ReadToEndAsync());
    }
    [Fact]
    public async Task SymlinkAndParentTraversalAreRejected()
    {
        using var fixture = new Fixture(); File.CreateSymbolicLink(Path.Combine(fixture.Root, "escape"), "/etc/passwd");
        using var stager = new InputStager(fixture.Stage);
        await Assert.ThrowsAsync<IOException>(async () => await stager.OpenSourceAsync(fixture.Root, "escape"));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await stager.OpenSourceAsync(fixture.Root, "../escape"));
    }
    [Fact]
    public async Task HardLinkIsRejected()
    {
        using var fixture = new Fixture(); var original = Path.Combine(fixture.Root, "one"); await File.WriteAllTextAsync(original, "protected");
        Assert.Equal(0, Link(original, Path.Combine(fixture.Root, "two")));
        using var stager = new InputStager(fixture.Stage);
        var error = await Assert.ThrowsAsync<InvalidDataException>(async () => await stager.OpenSourceAsync(fixture.Root, "two"));
        Assert.Equal("source_multiple_links", error.Message);
    }
    [Fact]
    public async Task ScansNameEachPolicyRejectionWithTheStagersOwnStatEvidence()
    {
        using var fixture = new Fixture();
        await using (var oversized = File.Create(Path.Combine(fixture.Root, "huge.md"))) oversized.SetLength(InputStager.MaximumInputBytes + 1);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "one.md"), "protected");
        Assert.Equal(0, Link(Path.Combine(fixture.Root, "one.md"), Path.Combine(fixture.Root, "two.md")));
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "fine.md"), "fine");
        using var stager = new InputStager(fixture.Stage);
        var files = new FolderSourceFiles(stager);
        var configuration = new FolderSourceConfiguration(fixture.Root, ["**/*.md"], [], true, "index", false, null, new Dictionary<string,string>(), "poll", 10);
        foreach (var deep in new[] { false, true })
        {
            var entries = new List<SourceFileEntry>();
            await foreach (var entry in files.ScanAsync(configuration, deep)) entries.Add(entry);
            // A rejection is a present entry with a named reason, never an unreadable or absent one.
            Assert.All(entries, entry => Assert.False(entry.Unreadable));
            Assert.Equal("input_bytes", entries.Single(entry => entry.RelativePath == "huge.md").Rejection);
            Assert.Equal("source_multiple_links", entries.Single(entry => entry.RelativePath == "one.md").Rejection);
            Assert.Equal("source_multiple_links", entries.Single(entry => entry.RelativePath == "two.md").Rejection);
            Assert.Null(entries.Single(entry => entry.RelativePath == "fine.md").Rejection);
            Assert.Equal(deep, entries.Single(entry => entry.RelativePath == "fine.md").Sha256 is not null);
            foreach (var rejected in entries.Where(entry => entry.Rejection is not null))
            {
                Assert.Null(rejected.Sha256);
                var error = await Assert.ThrowsAsync<InvalidDataException>(async () => await stager.OpenSourceAsync(fixture.Root, rejected.RelativePath));
                Assert.Equal(rejected.Rejection, error.Message);
                Assert.True(SourceEntryPolicy.TryGetEvidence(error, out var length, out var modified));
                Assert.Equal((rejected.Length, rejected.ModifiedAt), (length, modified));
            }
        }
        Assert.Empty(Directory.GetFiles(fixture.Stage));
    }
    [Fact]
    public void RejectionReasonsAreBoundedCodes()
    {
        Assert.Equal("input_bytes", SourceEntryPolicy.ReasonOf("input_bytes"));
        Assert.Equal("source_rejected", SourceEntryPolicy.ReasonOf("Input bound exceeded for /home/user/secret.md"));
        Assert.Equal("source_rejected", SourceEntryPolicy.ReasonOf(new string('a', 65)));
        Assert.False(SourceEntryPolicy.TryGetEvidence(new InvalidDataException("input_bytes"), out _, out _));
    }
    [Fact]
    public async Task RetainedRootDoesNotFollowReplacedRootPath()
    {
        using var fixture = new Fixture(); await File.WriteAllTextAsync(Path.Combine(fixture.Root, "item"), "safe");
        using var stager = new InputStager(fixture.Stage);
        await using (var first = await stager.OpenSourceAsync(fixture.Root, "item")) { }
        var moved = fixture.Root + "-moved"; Directory.Move(fixture.Root, moved); Directory.CreateSymbolicLink(fixture.Root, "/etc");
        try
        {
            await using var second = await stager.OpenSourceAsync(fixture.Root, "item");
            using var reader = new StreamReader(second.Content); Assert.Equal("safe", await reader.ReadToEndAsync());
        }
        finally { Directory.Delete(fixture.Root); Directory.Move(moved, fixture.Root); }
    }
    [Fact]
    public async Task OversizedStreamCleansPartialStage()
    {
        using var fixture = new Fixture(); using var stager = new InputStager(fixture.Stage);
        using var input = new MemoryStream(new byte[InputStager.MaximumInputBytes + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await stager.StageAsync(input, "large.txt"));
        Assert.Empty(Directory.GetFiles(fixture.Stage));
    }
    [Fact]
    public async Task StartupSweepRemovesOnlyOwnedRegularStagedFiles()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture(); Directory.CreateDirectory(fixture.Stage);
        string Name() => Guid.NewGuid().ToString("N");
        var stale = Path.Combine(fixture.Stage, Name()); await File.WriteAllTextAsync(stale, "orphaned stage");
        File.SetUnixFileMode(stale, UnixFileMode.UserRead);
        var outside = Path.Combine(fixture.Root, "outside.txt"); await File.WriteAllTextAsync(outside, "not ours");
        var link = Path.Combine(fixture.Stage, Name()); File.CreateSymbolicLink(link, outside);
        var directory = Path.Combine(fixture.Stage, Name()); Directory.CreateDirectory(directory);
        var foreign = Path.Combine(fixture.Stage, "operator-notes.txt"); await File.WriteAllTextAsync(foreign, "keep");
        var linked = Path.Combine(fixture.Stage, Name()); Assert.Equal(0, Link(outside, linked));
        using var stager = new InputStager(fixture.Stage);
        Assert.Equal(1, await stager.CleanStaleAsync());
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(link) || new FileInfo(link).LinkTarget is not null); Assert.Equal("not ours", await File.ReadAllTextAsync(outside));
        Assert.True(Directory.Exists(directory)); Assert.True(File.Exists(foreign)); Assert.True(File.Exists(linked));
        Assert.Equal(0, await stager.CleanStaleAsync());
        using var missing = new InputStager(Path.Combine(fixture.Root, "never-created"));
        Assert.Equal(0, await missing.CleanStaleAsync());
    }

    [Fact]
    public async Task DirectoryIsNotAdmittedAsFile()
    {
        using var fixture = new Fixture(); Directory.CreateDirectory(Path.Combine(fixture.Root, "folder"));
        using var stager = new InputStager(fixture.Stage);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await stager.OpenSourceAsync(fixture.Root, "folder"));
    }
    [Fact]
    public async Task FolderDeepScanHashesOnlyMatchingFilesAndSkipsSymlinkDirectories()
    {
        using var fixture = new Fixture(); Directory.CreateDirectory(Path.Combine(fixture.Root, "nested"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "nested", "note.md"), "scan bytes");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "skip.txt"), "ignored");
        Directory.CreateSymbolicLink(Path.Combine(fixture.Root, "outside"), "/etc");
        using var stager = new InputStager(fixture.Stage);
        var files = new FolderSourceFiles(stager);
        var configuration = new FolderSourceConfiguration(fixture.Root, ["**/*.md"], [], true, "index", false, null, new Dictionary<string,string>(), "poll", 10);
        var entries = new List<SourceFileEntry>();
        await foreach (var entry in files.ScanAsync(configuration, true)) entries.Add(entry);
        var item = Assert.Single(entries); Assert.Equal("nested/note.md", item.RelativePath); Assert.NotNull(item.Sha256);
    }

    [Fact]
    public async Task UnreadableEntriesAreReportedRatherThanOmitted()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root") return; // root bypasses mode bits, so nothing is unreadable.
        using var fixture = new Fixture(); Directory.CreateDirectory(Path.Combine(fixture.Root, "locked"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "readable.md"), "readable");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "secret.md"), "secret");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "locked", "inner.md"), "inner");
        File.SetUnixFileMode(Path.Combine(fixture.Root, "secret.md"), UnixFileMode.None);
        File.SetUnixFileMode(Path.Combine(fixture.Root, "locked"), UnixFileMode.None);
        try
        {
            using var stager = new InputStager(fixture.Stage);
            var files = new FolderSourceFiles(stager);
            var configuration = new FolderSourceConfiguration(fixture.Root, ["**/*.md"], [], true, "index", false, null, new Dictionary<string,string>(), "poll", 10);
            foreach (var deep in new[] { false, true })
            {
                var entries = new List<SourceFileEntry>();
                await foreach (var entry in files.ScanAsync(configuration, deep)) entries.Add(entry);
                Assert.Equal(["locked", "readable.md", "secret.md"], entries.Select(entry => entry.RelativePath).Order(StringComparer.Ordinal));
                Assert.False(entries.Single(entry => entry.RelativePath == "readable.md").Unreadable);
                Assert.True(entries.Single(entry => entry.RelativePath == "secret.md").Unreadable);
                Assert.True(entries.Single(entry => entry.RelativePath == "locked").Unreadable);
            }
        }
        finally
        {
            File.SetUnixFileMode(Path.Combine(fixture.Root, "locked"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(Path.Combine(fixture.Root, "secret.md"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task InboxClaimSurvivesFailureAndNewOriginalPathIsSeparate()
    {
        using var fixture = new Fixture(); using var stager = new InputStager(fixture.Stage); var claims = new InboxClaims(stager);
        var inbox = await claims.EnsureAsync(fixture.Root); await File.WriteAllTextAsync(Path.Combine(inbox, "input.txt"), "claimed bytes");
        var claim = await claims.ClaimAsync(inbox, "input.txt");
        Assert.False(File.Exists(Path.Combine(inbox, "input.txt")));
        await File.WriteAllTextAsync(Path.Combine(inbox, "input.txt"), "new bytes");
        await using (var staged = await stager.OpenSourceAsync(inbox, claim.RelativeClaimPath))
        { using var reader = new StreamReader(staged.Content); Assert.Equal("claimed bytes", await reader.ReadToEndAsync()); }
        var resumed = new List<ClaimedInput>(); await foreach (var item in claims.EnumerateAsync(inbox)) resumed.Add(item);
        Assert.Equal(claim, Assert.Single(resumed)); await claims.CompleteAsync(inbox, claim); await claims.CompleteAsync(inbox, claim);
        Assert.Equal("new bytes", await File.ReadAllTextAsync(Path.Combine(inbox, "input.txt")));
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)] private static extern int Link(string existing, string link);
    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "sbstage-" + Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(directory, "root"); public string Stage => Path.Combine(directory, "stage");
        public Fixture() { Directory.CreateDirectory(Root); }
        public void Dispose() => Directory.Delete(directory, true);
    }
}
