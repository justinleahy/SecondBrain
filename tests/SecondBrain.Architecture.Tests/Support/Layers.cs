using System.Reflection;

namespace SecondBrain.Architecture.Tests.Support;

/// <summary>
/// One anchor type per SecondBrain assembly.
/// </summary>
internal static class Layers
{
    public const string CoreName = "SecondBrain.Core";
    public const string StorageName = "SecondBrain.Storage";
    public const string ProvidersOpenAICompatibleName = "SecondBrain.Providers.OpenAICompatible";
    public const string ServerName = "SecondBrain.Server";
    public const string CliName = "brain";
    public const string ExtractorName = "SecondBrain.Extractor";
    public const string MockProviderName = "SecondBrain.MockProvider";
    public const string InfrastructureName = "SecondBrain.Infrastructure";

    public static Assembly Core => typeof(global::SecondBrain.Core.Problems.ProblemTypes).Assembly;

    public static Assembly Infrastructure => typeof(global::SecondBrain.Infrastructure.Network.SystemDnsResolver).Assembly;

    public static Assembly Storage => typeof(global::SecondBrain.Storage.StorageServiceCollectionExtensions).Assembly;

    public static Assembly ProvidersOpenAICompatible => typeof(global::SecondBrain.Providers.OpenAICompatible.ModelCatalog).Assembly;

    public static Assembly Server => typeof(global::Program).Assembly;

    public static Assembly Cli => typeof(global::SecondBrain.Cli.Program).Assembly;

    public static Assembly Extractor => typeof(global::SecondBrain.Extractor.Program).Assembly;

    public static Assembly MockProvider => typeof(global::SecondBrain.MockProvider.Program).Assembly;

    /// <summary>Every production assembly built from <c>src/</c>.</summary>
    public static IReadOnlyList<Assembly> SourceAssemblies =>
        [Core, Infrastructure, Storage, ProvidersOpenAICompatible, Server, Cli, Extractor];

    /// <summary>Every assembly that ships: the <c>src/</c> assemblies plus the mock provider fixture host.</summary>
    public static IReadOnlyList<Assembly> ShippedAssemblies => [.. SourceAssemblies, MockProvider];

    public static Assembly ByName(string name) =>
        ShippedAssemblies.Single(assembly => assembly.GetName().Name == name);

    public static bool IsSecondBrainAssembly(string assemblyName) =>
        assemblyName == CliName || assemblyName.StartsWith("SecondBrain.", StringComparison.Ordinal);
}
