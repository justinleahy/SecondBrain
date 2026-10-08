using Xunit;

namespace SecondBrain.Server.Tests.Support;

/// <summary>Compares characterization output with a golden file copied beside the test assembly.</summary>
internal static class GoldenFile
{
    public static void AssertMatches(string name, string actual)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Golden", name);
        var expected = File.ReadAllText(path).ReplaceLineEndings("\n");
        if (string.Equals(expected, actual, StringComparison.Ordinal)) return;
        var actualPath = path + ".actual";
        File.WriteAllText(actualPath, actual);
        Assert.Fail($"Golden/{name} differs from the current output, which was written to {actualPath}.");
    }
}
