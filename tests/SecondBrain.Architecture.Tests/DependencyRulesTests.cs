using SecondBrain.Architecture.Tests.Support;
using Xunit;

namespace SecondBrain.Architecture.Tests;

public sealed class DependencyRulesTests
{
    private static readonly string[] VendorSdkAssemblies = ["OpenAI", "Microsoft.Extensions.AI.OpenAI"];

    /// <summary>AR-06 (network): the socket and DNS types whose use belongs to Infrastructure.</summary>
    private static readonly string[] CoreForbiddenNetworkTypes =
    [
        "System.Net.Dns",
        "System.Net.Sockets.Socket",
        "System.Net.Sockets.TcpClient",
        "System.Net.Sockets.UdpClient",
        "System.Net.Sockets.NetworkStream",
        "System.Net.Sockets.UnixDomainSocketEndPoint",
    ];

    private static readonly string[] CompositionAssemblyPrefixes =
        ["Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.Hosting"];

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
    [InlineData(Layers.InfrastructureName, new[] { Layers.CoreName })]
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
    [Trait("Rule", "AR-06")]
    public void CoreReferencesNoSocketOrDnsType()
    {
        var facts = AssemblyFacts.For(Layers.Core);
        var violations = facts.TypeReferences
            .Intersect(CoreForbiddenNetworkTypes, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(violations.Length == 0,
            $"SecondBrain.Core references network types that belong in SecondBrain.Infrastructure: {string.Join(", ", violations)}");
    }

    [Fact]
    [Trait("Rule", "AR-06")]
    public void InfrastructureIsWhereTheSocketAndDnsTypesAreReferenced()
    {
        // Guards the rule above against reading type references that never contain these names at all.
        var facts = AssemblyFacts.For(Layers.Infrastructure);
        Assert.Contains("System.Net.Dns", facts.TypeReferences);
        Assert.Contains("System.Net.Sockets.TcpClient", facts.TypeReferences);
        Assert.Contains("System.Net.Sockets.UnixDomainSocketEndPoint", facts.TypeReferences);
    }

    [Fact]
    [Trait("Rule", "AR-16")]
    public void InfrastructureReferencesNoCompositionAssembly()
    {
        var facts = AssemblyFacts.For(Layers.Infrastructure);
        Assert.Equal(Layers.InfrastructureName, facts.Name);
        var violations = facts.AssemblyReferences
            .Where(reference => CompositionAssemblyPrefixes.Any(prefix => reference.StartsWith(prefix, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(violations.Length == 0,
            $"SecondBrain.Infrastructure references composition assemblies; composition stays in the hosts: {string.Join(", ", violations)}");
    }

    [Theory]
    [Trait("Rule", "AR-08")]
    [InlineData("src/SecondBrain.Extractor/SecondBrain.Extractor.csproj")]
    [InlineData("tests/SecondBrain.MockProvider/SecondBrain.MockProvider.csproj")]
    public void SandboxAndMockProjectsHaveNoProjectReferences(string projectPath)
    {
        var project = Repository.Project(projectPath);
        Assert.Empty(project.ProjectReferences);
    }

    [Theory]
    [Trait("Rule", "AR-08")]
    [InlineData(Layers.ExtractorName)]
    [InlineData(Layers.MockProviderName)]
    public void SandboxAndMockAssembliesReferenceNoSecondBrainAssembly(string assemblyName)
    {
        var facts = AssemblyFacts.For(Layers.ByName(assemblyName));
        Assert.Equal(assemblyName, facts.Name);
        Assert.Empty(facts.SecondBrainReferences);
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
