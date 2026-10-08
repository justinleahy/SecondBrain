namespace SecondBrain.Core.Configuration;

/// <summary>Host file-system queries that configuration validation needs.</summary>
public interface IHostPathInspector
{
    string Canonicalize(string path);   // realpath semantics
    bool DirectoryExists(string path);
}
