using SecondBrain.Core.Configuration;
using SecondBrain.Infrastructure.Security;

namespace SecondBrain.Infrastructure.FileSystem;

/// <summary>Host path queries backed by realpath and the local file system.</summary>
public sealed class UnixHostPathInspector : IHostPathInspector
{
    public static UnixHostPathInspector Instance { get; } = new();

    public string Canonicalize(string path) => UnixPath.Canonicalize(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);
}
