using SecondBrain.Architecture.Tests.Support;
using Xunit;

namespace SecondBrain.Architecture.Tests;

public sealed class DependencyRulesTests
{
    private static readonly string[] VendorSdkAssemblies = ["OpenAI", "Microsoft.Extensions.AI.OpenAI"];

    [Fact]
    [Trait("Rule", "AR-01")]
    public void CoreProjectHasNoProjectReferences()
    {
        var project = Repository.Project("src/SecondBrain.Core/SecondBrain.Core.csproj");
        Assert.Empty(project.ProjectReferences);
    }

    [Fact]
    [Trait("Rule", "AR-01")]
    public void CoreAssemblyReferencesNoSecondBrainAssembly()
    {
        var facts = AssemblyFacts.For(Layers.Core);
        Assert.Equal(Layers.CoreName, facts.Name);
        Assert.Empty(facts.SecondBrainReferences);
    }

    [Theory]
    [Trait("Rule", "AR-03")]
    [InlineData(Layers.StorageName, new[] { Layers.CoreName })]
    [InlineData(Layers.ProvidersOpenAICompatibleName, new[] { Layers.CoreName })]
    [InlineData(Layers.ServerName, new[] { Layers.CoreName, Layers.InfrastructureName, Layers.StorageName, Layers.ProvidersOpenAICompatibleName })]
    public void SecondBrainAssemblyReferencesStayInsideTheAllowSet(string assemblyName, string[] allowed)
    {
        var facts = AssemblyFacts.For(Layers.ByName(assemblyName));
        Assert.Contains(Layers.CoreName, facts.SecondBrainReferences);
        var disallowed = facts.SecondBrainReferences.Except(allowed, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.True(disallowed.Length == 0,
            $"{assemblyName} references {string.Join(", ", disallowed)}; allowed: {string.Join(", ", allowed)}.");
    }

    [Fact]
    [Trait("Rule", "AR-10")]
    public void OnlyProviderAssembliesReferenceVendorSdkAssemblies()
    {
        var violations = Layers.ShippedAssemblies
            .Select(AssemblyFacts.For)
            .Where(facts => !facts.Name.StartsWith("SecondBrain.Providers.", StringComparison.Ordinal))
            .SelectMany(facts => facts.AssemblyReferences
                .Intersect(VendorSdkAssemblies, StringComparer.Ordinal)
                .Select(reference => $"{facts.Name} -> {reference}"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(violations.Length == 0,
            $"Vendor SDK assemblies are referenced outside SecondBrain.Providers.* (spec §2.8): {string.Join("; ", violations)}");
    }

    [Fact]
    [Trait("Rule", "AR-10")]
    public void ProviderAdapterIsWhereTheVendorSdkIsReferenced()
    {
        // Guards the rule above against reading metadata that never contains the vendor SDK at all.
        var facts = AssemblyFacts.For(Layers.ProvidersOpenAICompatible);
        Assert.Contains("OpenAI", facts.AssemblyReferences);
    }

    [Fact]
    [Trait("Rule", "AR-10")]
    public void OnlyProviderProjectsReferenceVendorSdkPackages()
    {
        var projects = Repository.SourceProjects();
        Assert.Contains("src/SecondBrain.Providers.OpenAICompatible/SecondBrain.Providers.OpenAICompatible.csproj", projects);

        var violations = projects
            .Where(path => !path.StartsWith("src/SecondBrain.Providers.", StringComparison.Ordinal))
            .Select(Repository.Project)
            .SelectMany(project => project.PackageReferences
                .Where(package => VendorSdkAssemblies.Contains(package, StringComparer.OrdinalIgnoreCase))
                .Select(package => $"{project.RelativePath} -> {package}"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(violations.Length == 0,
            $"Vendor SDK packages are referenced outside src/SecondBrain.Providers.* (spec §2.8): {string.Join("; ", violations)}");
    }
}
