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

    /// <summary>AR-06 (watcher and signal): configuration reload hooks whose use belongs to Infrastructure.</summary>
    private static readonly string[] CoreForbiddenReloadTypes =
    [
        "System.IO.FileSystemWatcher",
        "System.Runtime.InteropServices.PosixSignalRegistration",
    ];

    /// <summary>AR-06 (I/O, interop and environment): host access whose use belongs to Infrastructure.</summary>
    private static readonly string[] CoreForbiddenHostTypes =
    [
        "System.IO.File",
        "System.IO.Directory",
        "System.IO.FileInfo",
        "System.IO.DirectoryInfo",
        "System.IO.FileSystemInfo",
        "System.IO.FileStream",
        "System.IO.DriveInfo",
        "System.Runtime.InteropServices.Marshal",
        "System.Runtime.InteropServices.NativeLibrary",
        "System.Runtime.InteropServices.SafeHandle",
        "System.Environment",
        "System.Diagnostics.Process",
    ];

    /// <summary>
    /// AR-06 (environment): the one <c>System.Environment</c> member Core may reach. The C# compiler emits it in
    /// every iterator (<c>yield return</c>) to check thread affinity, so it is not host or environment access.
    /// </summary>
    private const string CompilerIteratorEnvironmentMember = "System.Environment::get_CurrentManagedThreadId";

    /// <summary>AR-06 (interop): every type in this namespace is forbidden in Core.</summary>
    private const string SafeHandlesNamespacePrefix = "Microsoft.Win32.SafeHandles.";

    /// <summary>AR-07: the assemblies that may declare P/Invoke methods.</summary>
    private static readonly string[] PinvokeAllowedAssemblies =
        [Layers.InfrastructureName, Layers.StorageName, Layers.ExtractorName, Layers.CliName];

    /// <summary>AR-05: the only YamlDotNet type Core may reference.</summary>
    private static readonly string[] CoreAllowedYamlTypes = ["YamlDotNet.Serialization.YamlMemberAttribute"];

    private static readonly string[] CompositionAssemblyPrefixes =
        ["Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.Hosting"];

    /// <summary>AR-09: the only SecondBrain assemblies brain may compile against.</summary>
    private static readonly string[] CliAllowedSecondBrainAssemblies =
        [Layers.CoreName, Layers.InfrastructureName, Layers.StorageName];

    /// <summary>AR-09: the alias that keeps the CLI's Server reference layout-only.</summary>
    private const string ServerLayoutAlias = "ServerLayout";

    /// <summary>AR-12 and AR-12a: the SQL assemblies whose use belongs to Storage.</summary>
    private static readonly string[] SqlAssemblyNames = ["Dapper", "Microsoft.Data.Sqlite"];
    private const string SqlitePclAssemblyPrefix = "SQLitePCLRaw.";

    /// <summary>AR-03b: the exact <c>ProjectReference</c> set of every <c>src</c> project (§1.2), by referenced project name.</summary>
    public static TheoryData<string, string[]> SourceProjectReferences => new()
    {
        { "src/SecondBrain.Core/SecondBrain.Core.csproj", [] },
        { "src/SecondBrain.Infrastructure/SecondBrain.Infrastructure.csproj", [Layers.CoreName] },
        { "src/SecondBrain.Storage/SecondBrain.Storage.csproj", [Layers.CoreName] },
        { "src/SecondBrain.Providers.OpenAICompatible/SecondBrain.Providers.OpenAICompatible.csproj", [Layers.CoreName] },
        { "src/SecondBrain.Server/SecondBrain.Server.csproj", [Layers.CoreName, Layers.InfrastructureName, Layers.StorageName, Layers.ProvidersOpenAICompatibleName] },
        { "src/SecondBrain.Cli/SecondBrain.Cli.csproj", [Layers.CoreName, Layers.InfrastructureName, Layers.StorageName, Layers.ServerName] },
        { "src/SecondBrain.Extractor/SecondBrain.Extractor.csproj", [] },
    };

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

    [Theory]
    [Trait("Rule", "AR-03")]
    [MemberData(nameof(SourceProjectReferences))]
    public void SourceProjectReferencesMatchTheDependencyTableExactly(string projectPath, string[] expected)
    {
        var actual = Repository.Project(projectPath).ProjectReferences
            .Select(reference => reference.ProjectName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var wanted = expected.Order(StringComparer.Ordinal).ToArray();
        Assert.True(actual.SequenceEqual(wanted, StringComparer.Ordinal),
            $"{projectPath} references [{string.Join(", ", actual)}]; §1.2 allows exactly [{string.Join(", ", wanted)}].");
    }

    [Fact]
    [Trait("Rule", "AR-03")]
    public void EverySourceProjectHasAnExactProjectReferenceRule()
    {
        // A new src project must be added to the AR-03b table rather than escape it.
        var covered = SourceProjectReferences.Select(row => (string)row[0]).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(covered, Repository.SourceProjects());
    }

    [Fact]
    [Trait("Rule", "AR-04")]
    public void CoreReferencesNoAspNetCoreAssembly()
    {
        var facts = AssemblyFacts.For(Layers.Core);
        var violations = facts.AssemblyReferences
            .Where(reference => reference.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(violations.Length == 0,
            $"SecondBrain.Core references ASP.NET Core assemblies: {string.Join(", ", violations)}");
    }

    [Fact]
    [Trait("Rule", "AR-05")]
    public void CoreReferencesOnlyTheYamlMemberAttributeFromYamlDotNet()
    {
        var facts = AssemblyFacts.For(Layers.Core);
        var yamlTypes = facts.TypeReferences
            .Where(type => type.StartsWith("YamlDotNet.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(yamlTypes.SequenceEqual(CoreAllowedYamlTypes, StringComparer.Ordinal),
            $"SecondBrain.Core may reference only {string.Join(", ", CoreAllowedYamlTypes)} from YamlDotNet; found: {string.Join(", ", yamlTypes)}");
    }

    [Fact]
    [Trait("Rule", "AR-06")]
    public void CoreReferencesNoFileWatcherOrSignalType()
    {
        var facts = AssemblyFacts.For(Layers.Core);
        var violations = facts.TypeReferences
            .Intersect(CoreForbiddenReloadTypes, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(violations.Length == 0,
            $"SecondBrain.Core references reload types that belong in SecondBrain.Infrastructure: {string.Join(", ", violations)}");
    }

    [Fact]
    [Trait("Rule", "AR-06")]
    public void InfrastructureIsWhereTheFileWatcherAndSignalTypesAreReferenced()
    {
        // Guards the rule above against reading type references that never contain these names at all.
        var facts = AssemblyFacts.For(Layers.Infrastructure);
        foreach (var type in CoreForbiddenReloadTypes)
        {
            Assert.Contains(type, facts.TypeReferences);
        }
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
    [Trait("Rule", "AR-06")]
    public void CoreReferencesNoFileSystemInteropOrEnvironmentType()
    {
        var facts = AssemblyFacts.For(Layers.Core);
        var violations = facts.TypeReferences
            .Where(type => CoreForbiddenHostTypes.Contains(type, StringComparer.Ordinal) ||
                           type.StartsWith(SafeHandlesNamespacePrefix, StringComparison.Ordinal))
            .Where(type => type != "System.Environment" || !OnlyCompilerIteratorEnvironmentMembers(facts))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(violations.Length == 0,
            $"SecondBrain.Core references host access types that belong in SecondBrain.Infrastructure: {string.Join(", ", violations)}");
    }

    [Fact]
    [Trait("Rule", "AR-06")]
    public void CoreReachesSystemEnvironmentOnlyThroughCompilerGeneratedIterators()
    {
        var facts = AssemblyFacts.For(Layers.Core);
        var environmentMembers = facts.MemberReferences
            .Where(member => member.StartsWith("System.Environment::", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(environmentMembers.All(member => member == CompilerIteratorEnvironmentMember),
            $"SecondBrain.Core reads the process environment, which belongs in SecondBrain.Infrastructure: {string.Join(", ", environmentMembers)}");
        // Guards the member-level check against reading metadata that never contains member references at all.
        Assert.Contains("System.Threading.Monitor::Enter", AssemblyFacts.For(Layers.Infrastructure).MemberReferences);
    }

    [Fact]
    [Trait("Rule", "AR-06")]
    public void InfrastructureIsWhereTheFileSystemAndInteropTypesAreReferenced()
    {
        // Guards the rule above against reading type references that never contain these names at all.
        var facts = AssemblyFacts.For(Layers.Infrastructure);
        Assert.Contains("System.IO.File", facts.TypeReferences);
        Assert.Contains("System.IO.Directory", facts.TypeReferences);
        Assert.Contains("System.Runtime.InteropServices.Marshal", facts.TypeReferences);
        Assert.Contains(facts.TypeReferences, type => type.StartsWith(SafeHandlesNamespacePrefix, StringComparison.Ordinal));
    }

    private static bool OnlyCompilerIteratorEnvironmentMembers(AssemblyFacts facts) =>
        facts.MemberReferences
            .Where(member => member.StartsWith("System.Environment::", StringComparison.Ordinal))
            .All(member => member == CompilerIteratorEnvironmentMember);

    [Fact]
    [Trait("Rule", "AR-07")]
    public void PinvokeMethodsExistOnlyInTheAllowedAssemblies()
    {
        var violations = Layers.ShippedAssemblies
            .Select(AssemblyFacts.For)
            .Where(facts => !PinvokeAllowedAssemblies.Contains(facts.Name, StringComparer.Ordinal))
            .SelectMany(facts => facts.PinvokeMethods.Select(method => $"{facts.Name}: {method}"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(violations.Length == 0,
            $"P/Invoke methods are declared outside {string.Join(", ", PinvokeAllowedAssemblies)}: {string.Join("; ", violations)}");
    }

    [Fact]
    [Trait("Rule", "AR-07")]
    public void InfrastructureIsWhereTheUnixPinvokeMethodsAreDeclared()
    {
        // Guards the rule above against reading metadata that never contains P/Invoke methods at all.
        var facts = AssemblyFacts.For(Layers.Infrastructure);
        Assert.Contains(facts.PinvokeMethods, method => method.StartsWith("SecondBrain.Infrastructure.Security.UnixPath::", StringComparison.Ordinal));
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
    [Trait("Rule", "AR-09")]
    public void CliCompilesOnlyAgainstCoreInfrastructureAndStorage()
    {
        var facts = AssemblyFacts.For(Layers.Cli);
        Assert.Equal(Layers.CliName, facts.Name);
        // Guards the rule against reading metadata that never resolves a type into a SecondBrain assembly at all.
        Assert.Contains(Layers.CoreName, facts.UsedAssemblyReferences);
        Assert.Contains(Layers.StorageName, facts.UsedAssemblyReferences);

        var used = facts.UsedAssemblyReferences.Where(Layers.IsSecondBrainAssembly)
            .Except(CliAllowedSecondBrainAssemblies, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.True(used.Length == 0,
            $"brain compiles against types in {string.Join(", ", used)}; allowed: {string.Join(", ", CliAllowedSecondBrainAssemblies)}.");

        // The ServerLayout alias makes the compiler emit an otherwise unused SecondBrain.Server assembly reference so the
        // portable PDB can record the alias. No type resolves into it (checked above); any other SecondBrain reference fails.
        var referenced = facts.SecondBrainReferences
            .Except([.. CliAllowedSecondBrainAssemblies, Layers.ServerName], StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.True(referenced.Length == 0,
            $"brain references {string.Join(", ", referenced)}; allowed: {string.Join(", ", CliAllowedSecondBrainAssemblies)} (plus the layout-only SecondBrain.Server alias record).");
    }

    [Fact]
    [Trait("Rule", "AR-09")]
    public void CliServerReferenceIsLayoutOnly()
    {
        var project = Repository.Project("src/SecondBrain.Cli/SecondBrain.Cli.csproj");
        var server = Assert.Single(project.ProjectReferences, reference => reference.ProjectName == Layers.ServerName);
        Assert.Equal(ServerLayoutAlias, server.Aliases);
    }

    [Fact]
    [Trait("Rule", "AR-12")]
    public void CliReferencesNoSqlAssembly()
    {
        var facts = AssemblyFacts.For(Layers.Cli);
        Assert.Equal(Layers.CliName, facts.Name);
        var violations = facts.AssemblyReferences
            .Where(reference => SqlAssemblyNames.Contains(reference, StringComparer.Ordinal) ||
                                reference.StartsWith(SqlitePclAssemblyPrefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(violations.Length == 0,
            $"brain references SQL assemblies; SQL belongs in SecondBrain.Storage: {string.Join(", ", violations)}");
    }

    [Fact]
    [Trait("Rule", "AR-12")]
    public void OnlyStorageReferencesSqlAssemblies()
    {
        var violations = Layers.ShippedAssemblies
            .Select(AssemblyFacts.For)
            .Where(facts => facts.Name != Layers.StorageName)
            .SelectMany(facts => facts.AssemblyReferences
                .Where(reference => SqlAssemblyNames.Contains(reference, StringComparer.Ordinal) ||
                                    reference.StartsWith(SqlitePclAssemblyPrefix, StringComparison.Ordinal))
                .Select(reference => $"{facts.Name} -> {reference}"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(violations.Length == 0,
            $"SQL assemblies are referenced outside SecondBrain.Storage: {string.Join("; ", violations)}");
    }

    [Fact]
    [Trait("Rule", "AR-12")]
    public void StorageIsWhereTheSqlAssembliesAreReferenced()
    {
        // Guards the rule above against reading metadata that never contains the SQL assemblies at all.
        var facts = AssemblyFacts.For(Layers.Storage);
        Assert.Contains("Dapper", facts.AssemblyReferences);
        Assert.Contains("Microsoft.Data.Sqlite", facts.AssemblyReferences);
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

    [Fact]
    [Trait("Rule", "AR-11")]
    public void OnlyInfrastructureReferencesArgon2Assemblies()
    {
        var violations = Layers.ShippedAssemblies
            .Select(AssemblyFacts.For)
            .Where(facts => facts.Name != Layers.InfrastructureName)
            .SelectMany(facts => facts.AssemblyReferences
                .Where(reference => reference.StartsWith("Isopoh.", StringComparison.Ordinal))
                .Select(reference => $"{facts.Name} -> {reference}"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(violations.Length == 0,
            $"Isopoh.* assemblies are referenced outside SecondBrain.Infrastructure: {string.Join("; ", violations)}");
    }

    [Fact]
    [Trait("Rule", "AR-11")]
    public void InfrastructureIsWhereArgon2IsReferenced()
    {
        // Guards the rule above against reading metadata that never contains the Argon2 assembly at all.
        var facts = AssemblyFacts.For(Layers.Infrastructure);
        Assert.Contains(facts.AssemblyReferences, reference => reference.StartsWith("Isopoh.", StringComparison.Ordinal));
    }
}
