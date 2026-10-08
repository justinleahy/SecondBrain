using System.Reflection;
using SecondBrain.Core.Configuration;
using Xunit;
using YamlDotNet.Serialization;

namespace SecondBrain.Core.Tests.Configuration;

/// <summary>Pins every Appendix B YAML key declared through <see cref="YamlMemberAttribute"/> in Core.</summary>
public sealed class YamlKeyContractTests
{
    [Fact]
    public void YamlAliasesMatchGolden()
    {
        var lines = new List<string>();
        foreach (var type in typeof(SecondBrainOptions).Assembly.GetExportedTypes())
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (property.GetCustomAttribute<YamlMemberAttribute>() is { } member)
                    lines.Add($"{type.Name}.{property.Name}={member.Alias}");
        lines.Sort(StringComparer.Ordinal);

        var path = Path.Combine(AppContext.BaseDirectory, "Golden", "yaml-aliases.txt");
        var expected = File.ReadAllText(path).ReplaceLineEndings("\n");
        var actual = string.Concat(lines.Select(line => line + "\n"));
        if (string.Equals(expected, actual, StringComparison.Ordinal)) return;
        File.WriteAllText(path + ".actual", actual);
        Assert.Fail($"Golden/yaml-aliases.txt differs from the current output, which was written to {path}.actual.");
    }
}
