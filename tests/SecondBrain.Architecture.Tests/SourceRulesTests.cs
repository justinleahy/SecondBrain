using System.Text.RegularExpressions;
using SecondBrain.Architecture.Tests.Support;
using Xunit;

namespace SecondBrain.Architecture.Tests;

public sealed partial class SourceRulesTests
{
    private static readonly string[] DomainDirectories = ["src/SecondBrain.Core/Domain", "src/SecondBrain.Core/Authorization"];
    private static readonly string[] DomainNamespaces = ["SecondBrain.Core.Domain", "SecondBrain.Core.Authorization"];

    [Fact]
    [Trait("Rule", "AR-14")]
    public void DomainFilesUseOnlyTheDomainAndTheBaseLibrary()
    {
        var files = DomainDirectories.SelectMany(directory => Repository.SourceFiles(directory)).ToArray();
        Assert.NotEmpty(files);

        var violations = new List<string>();
        foreach (var file in files)
        {
            var lines = Repository.ReadAllLines(file);
            for (var index = 0; index < lines.Count; index++)
            {
                var line = lines[index];
                var location = $"{file}:{index + 1}";

                var usingMatch = UsingDirective().Match(line);
                if (usingMatch.Success)
                {
                    var target = usingMatch.Groups["target"].Value;
                    if (target == "Microsoft" || target.StartsWith("Microsoft.", StringComparison.Ordinal))
                    {
                        violations.Add($"{location}: using {target}");
                    }
                    else if ((target == "SecondBrain" || target.StartsWith("SecondBrain.", StringComparison.Ordinal)) && !IsDomainNamespace(target))
                    {
                        violations.Add($"{location}: using {target}");
                    }
                }

                foreach (Match qualified in CoreQualifiedName().Matches(line))
                {
                    if (!IsDomainNamespace(qualified.Value))
                    {
                        violations.Add($"{location}: {qualified.Value}");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Domain and Authorization must not depend on outer namespaces:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    private static bool IsDomainNamespace(string name) =>
        DomainNamespaces.Any(ns => name == ns || name.StartsWith(ns + ".", StringComparison.Ordinal));

    // using X; using static X; global using X; using Alias = X;
    [GeneratedRegex(@"^\s*(?:global\s+)?using\s+(?:static\s+)?(?:@?\w+\s*=\s*)?(?:global::)?(?<target>[A-Za-z_][\w.]*)\s*;")]
    private static partial Regex UsingDirective();

    [GeneratedRegex(@"\bSecondBrain\.Core\.\w+")]
    private static partial Regex CoreQualifiedName();
}
