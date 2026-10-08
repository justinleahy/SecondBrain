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

    [Fact]
    [Trait("Rule", "AR-05")]
    public void JsonSchemaIsUsedOnlyByTheCoreDomain()
    {
        var files = Repository.SourceFiles("src/SecondBrain.Core");
        Assert.NotEmpty(files);

        var users = new List<string>();
        var violations = new List<string>();
        foreach (var file in files)
        {
            var lines = Repository.ReadAllLines(file);
            for (var index = 0; index < lines.Count; index++)
            {
                var usingMatch = UsingDirective().Match(lines[index]);
                if (!usingMatch.Success)
                {
                    continue;
                }

                var target = usingMatch.Groups["target"].Value;
                if (target != "Json.Schema" && !target.StartsWith("Json.Schema.", StringComparison.Ordinal))
                {
                    continue;
                }

                users.Add(file);
                if (!file.StartsWith("src/SecondBrain.Core/Domain/", StringComparison.Ordinal))
                {
                    violations.Add($"{file}:{index + 1}: using {target}");
                }
            }
        }

        // Guards the rule against a pattern that never matches anything.
        Assert.NotEmpty(users);
        Assert.True(violations.Count == 0,
            $"using Json.Schema is allowed only under src/SecondBrain.Core/Domain:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    [Fact]
    [Trait("Rule", "AR-09")]
    public void CliSourceNeverOpensTheServerLayoutAlias()
    {
        var files = Repository.SourceFiles("src/SecondBrain.Cli");
        Assert.NotEmpty(files);

        var violations = new List<string>();
        foreach (var file in files)
        {
            var lines = Repository.ReadAllLines(file);
            for (var index = 0; index < lines.Count; index++)
            {
                if (ExternAliasDirective().IsMatch(lines[index]))
                {
                    violations.Add($"{file}:{index + 1}: {lines[index].Trim()}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"brain references SecondBrain.Server for layout only; no extern alias is allowed in src/SecondBrain.Cli:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    private static bool IsDomainNamespace(string name) =>
        DomainNamespaces.Any(ns => name == ns || name.StartsWith(ns + ".", StringComparison.Ordinal));

    // using X; using static X; global using X; using Alias = X;
    [GeneratedRegex(@"^\s*(?:global\s+)?using\s+(?:static\s+)?(?:@?\w+\s*=\s*)?(?:global::)?(?<target>[A-Za-z_][\w.]*)\s*;")]
    private static partial Regex UsingDirective();

    [GeneratedRegex(@"^\s*extern\s+alias\b")]
    private static partial Regex ExternAliasDirective();

    [GeneratedRegex(@"\bSecondBrain\.Core\.\w+")]
    private static partial Regex CoreQualifiedName();
}
