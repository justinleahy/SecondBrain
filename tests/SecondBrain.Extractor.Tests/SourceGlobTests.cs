using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using SecondBrain.Core.Extraction;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Sources;
using SecondBrain.Extraction.Contracts;
using SecondBrain.Infrastructure.Ingestion;
using Xunit;

namespace SecondBrain.Extractor.Tests;

public sealed class SourceGlobTests
{
    public static TheoryData<string, string> Hostile => new()
    {
        { string.Concat(Enumerable.Repeat("*a", 8)) + "b", new string('a', 128) },
        { string.Concat(Enumerable.Repeat("*a", 12)) + "b", new string('a', 256) },
        { string.Concat(Enumerable.Repeat("**a", 12)) + "b", string.Join('/', Enumerable.Repeat("aaaaaaaa", 32)) },
        { string.Concat(Enumerable.Repeat("?*", 200)) + "!", new string('x', 1024) },
        { string.Concat(Enumerable.Repeat("**/a", 100)) + "/b", string.Join('/', Enumerable.Repeat("a", 512)) },
        { string.Concat(Enumerable.Repeat("*a", 512)), new string('a', 4096) + "b" },
    };

    [Theory]
    [MemberData(nameof(Hostile))]
    public void HostilePatternsMatchInBoundedTimeWithoutTimeouts(string pattern, string path)
    {
        var elapsed = Stopwatch.StartNew();
        Assert.False(SourceGlob.IsMatch(path, pattern));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2), $"Glob took {elapsed.Elapsed}.");
    }

    [Fact]
    public void TheFormerRegexTranslationTimesOutOnTheReportedShape()
    {
        // The reproduction for the replaced matcher: this exception used to escape the hosted source worker.
        var pattern = string.Concat(Enumerable.Repeat("*a", 12)) + "b";
        Assert.Throws<RegexMatchTimeoutException>(() => Legacy(new string('a', 256), pattern));
    }

    public static TheoryData<string, string> Ordinary
    {
        get
        {
            string[] patterns = ["*", "*.md", "**/*.md", "notes/**", "**/drafts/**", "a?c.txt", "**", "**/", "docs/*/index.md", "***/x",
                "a**b", "[x].md", "(a)+.md", "**/**/z", ".claimed/**", "a/**/b/*.md", "*/", "x*y*z", "?", "dir/", "**/*", "*.{md,txt}"];
            string[] paths = ["note.md", "notes/note.md", "notes/deep/note.md", "abc.txt", "a/c.txt", "docs/x/index.md", "docs/x/y/index.md",
                "x", "q/x", "q/r/x", "ab", "a/b", "a/x/b", "[x].md", "(a)+.md", "z", "p/z", "p/q/z", ".claimed/123--file", "a/b/n.md",
                "a/q/r/b/n.md", "dir/", "dir/file", "drafts/a", "y/drafts/a", "xyz", "x/y/z", "x-y-z", "a", "", "note.{md,txt}", "note.txt"];
            var data = new TheoryData<string, string>();
            foreach (var pattern in patterns) foreach (var path in paths) data.Add(pattern, path);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Ordinary))]
    public void OrdinaryPatternsKeepTheFormerSemantics(string pattern, string path) =>
        Assert.Equal(Legacy(path, pattern), SourceGlob.IsMatch(path, pattern));

    [Fact]
    public void OverlongPatternIsRejectedAsInvalidData() =>
        Assert.Equal("source_pattern", Assert.Throws<InvalidDataException>(() => SourceGlob.Compile(new string('*', 1025))).Message);

    [Fact]
    public async Task HostileBrainignoreDoesNotFailTheScanAndOrdinaryRulesStillApply()
    {
        using var fixture = new Fixture();
        var hostilePath = string.Join('/', Enumerable.Repeat("aaaaaaaa", 32));
        foreach (var name in new[] { "keep.md", "drop.tmp", "build/out.md", "build/keep.md", "nested/drop.tmp", hostilePath })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(fixture.Root, name))!);
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, name), "bytes");
        }
        var rules = string.Join('\n', "# comment", string.Concat(Enumerable.Repeat("**a", 12)) + "b", "*.tmp", "/build/", "!build/keep.md");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, ".brainignore"), rules);
        using var stager = new InputStager(fixture.Stage);
        var files = new FolderSourceFiles(stager, new TextExtractor());
        var configuration = new FolderSourceConfiguration(fixture.Root, [], [], true, "index", false, null, new Dictionary<string, string>(), "poll", 10);
        foreach (var deep in new[] { false, true })
        {
            var entries = new List<SourceFileEntry>();
            await foreach (var entry in files.ScanAsync(configuration, deep)) entries.Add(entry);
            Assert.Equal([hostilePath, "build/keep.md", "keep.md"], entries.Select(entry => entry.RelativePath).Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task PolicyInvalidEntriesAreReportedPerEntryRatherThanFailingTheDeepScan()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "good.md"), "good bytes");
        await using (var oversized = File.Create(Path.Combine(fixture.Root, "huge.md"))) oversized.SetLength(InputStager.MaximumInputBytes + 1);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "linked.md"), "linked bytes");
        Assert.Equal(0, Link(Path.Combine(fixture.Root, "linked.md"), Path.Combine(fixture.Stage + "-link")));
        try
        {
            using var stager = new InputStager(fixture.Stage);
            var files = new FolderSourceFiles(stager);
            var configuration = new FolderSourceConfiguration(fixture.Root, ["*.md"], [], false, "index", false, null, new Dictionary<string, string>(), "poll", 10);
            var entries = new List<SourceFileEntry>();
            await foreach (var entry in files.ScanAsync(configuration, true)) entries.Add(entry);
            Assert.NotNull(entries.Single(entry => entry.RelativePath == "good.md").Sha256);
            // Each stays a present entry naming its deterministic rejection, distinct from a transient unreadable one.
            Assert.Equal((false, "input_bytes"), entries.Single(entry => entry.RelativePath == "huge.md") is var huge ? (huge.Unreadable, huge.Rejection) : default);
            Assert.Equal((false, "source_multiple_links"), entries.Single(entry => entry.RelativePath == "linked.md") is var linked ? (linked.Unreadable, linked.Rejection) : default);
        }
        finally { File.Delete(fixture.Stage + "-link"); }
    }

    private static bool Legacy(string path, string pattern)
    {
        var expression = "^" + Regex.Escape(pattern).Replace(@"\*\*/", "(?:.*/)?", StringComparison.Ordinal).Replace(@"\*\*", ".*", StringComparison.Ordinal).Replace(@"\*", "[^/]*", StringComparison.Ordinal).Replace(@"\?", "[^/]", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(path, expression, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    private sealed class TextExtractor : IExtractorClient
    {
        public async ValueTask<ExtractionResult> ExtractAsync(Stream stagedInput, string formatHint, CancellationToken cancellationToken = default)
        {
            using var reader = new StreamReader(stagedInput, Encoding.UTF8);
            return new("extracted", "text", "text/plain", await reader.ReadToEndAsync(cancellationToken));
        }
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)] private static extern int Link(string existing, string link);
    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "sbglob-" + Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(directory, "root"); public string Stage => Path.Combine(directory, "stage");
        public Fixture() { Directory.CreateDirectory(Root); }
        public void Dispose() => Directory.Delete(directory, true);
    }
}
