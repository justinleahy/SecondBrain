namespace SecondBrain.Core.Ingestion;

public interface IInputStager
{
    ValueTask<StagedInput> StageAsync(Stream input, string filename, CancellationToken cancellationToken = default);
    ValueTask<StagedInput> OpenSourceAsync(string sourceRoot, string relativePath, CancellationToken cancellationToken = default);
}

/// <summary>Startup recovery for the private staging directory (spec §8 Startup: "cleans stale staging files").</summary>
public interface IStagingRecovery
{
    /// <summary>
    /// Removes staged files a crash left between creation and unlink. Only owned, single-link regular files with
    /// the stager's own names are removed; symlinks are never followed and directories or other entries are kept.
    /// </summary>
    ValueTask<int> CleanStaleAsync(CancellationToken cancellationToken = default);
}

public abstract class StagedInput : IAsyncDisposable
{
    public abstract Stream Content { get; }
    public abstract string Filename { get; }
    public abstract string Sha256 { get; }
    public abstract long Length { get; }
    public abstract ValueTask DisposeAsync();
}
