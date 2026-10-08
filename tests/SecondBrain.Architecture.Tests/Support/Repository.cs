using System.Xml.Linq;

namespace SecondBrain.Architecture.Tests.Support;

internal sealed record ProjectReferenceItem(string Include, string? Aliases)
{
    /// <summary>The referenced project's file name without extension, for example <c>SecondBrain.Core</c>.</summary>
    public string ProjectName => Path.GetFileNameWithoutExtension(Include.Replace('\\', '/'));
}

internal sealed record ProjectFile(string RelativePath, IReadOnlyList<ProjectReferenceItem> ProjectReferences, IReadOnlyList<string> PackageReferences);

/// <summary>
/// Source-tree access for the architecture rules.
/// </summary>
internal static class Repository
{
    private static readonly Lazy<string> RootPath = new(FindRoot);

    /// <summary>The directory containing <c>spec.md</c> and <c>deploy/</c>, the same rule as <c>DeploymentArtifactsTests</c>.</summary>
    public static string Root => RootPath.Value;

    public static string Combine(string relativePath) =>
        Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public static string RelativePath(string fullPath) =>
        Path.GetRelativePath(Root, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>Every <c>*.cs</c> file under <paramref name="relativeDirectory"/>, excluding <c>bin</c> and <c>obj</c>, as repo-relative paths with <c>/</c> separators.</summary>
    public static IReadOnlyList<string> SourceFiles(string relativeDirectory = "src")
    {
        var directory = Combine(relativeDirectory);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Select(RelativePath)
            .Where(path => !path.Split('/').Any(segment => segment is "bin" or "obj"))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Every <c>src/*/*.csproj</c>, as repo-relative paths.</summary>
    public static IReadOnlyList<string> SourceProjects() =>
        Directory.EnumerateDirectories(Combine("src"))
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly))
            .Select(RelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();

    public static string ReadAllText(string relativePath) => File.ReadAllText(Combine(relativePath));

    public static IReadOnlyList<string> ReadAllLines(string relativePath) => File.ReadAllLines(Combine(relativePath));

    public static ProjectFile Project(string relativePath)
    {
        var document = XDocument.Load(Combine(relativePath));
        var projectReferences = document.Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => new ProjectReferenceItem(
                (string?)element.Attribute("Include") ?? string.Empty,
                (string?)element.Attribute("Aliases") ?? (string?)element.Elements().FirstOrDefault(child => child.Name.LocalName == "Aliases")))
            .ToArray();
        var packageReferences = document.Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .Select(element => (string?)element.Attribute("Include") ?? string.Empty)
            .ToArray();
        return new ProjectFile(relativePath, projectReferences, packageReferences);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "spec.md")) &&
                Directory.Exists(Path.Combine(directory.FullName, "deploy")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find the repository root beside spec.md.");
    }
}
