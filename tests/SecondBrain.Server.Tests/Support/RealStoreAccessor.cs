using System.Data.Common;
using SecondBrain.Core.Storage;
using SecondBrain.Storage;

namespace SecondBrain.Server.Tests.Support;

/// <summary>
/// Hands tests the real lane A state store once the host has started, while owning the temporary
/// directory that the data root, allowed roots, and incoming root live under.
/// </summary>
public sealed class RealStoreAccessor : IDisposable, IAsyncDisposable
{
    private IStateStore? _store;

    public RealStoreAccessor()
    {
        DirectoryPath = Directory.CreateTempSubdirectory("secondbrain-lane-d-").FullName;
    }

    public string DirectoryPath { get; }

    private IStateStore Store => _store ?? throw new InvalidOperationException("The host has not started; access a client or Services first.");

    internal void Attach(IStateStore store) => _store = store;

    public ValueTask<IReadConnectionLease> OpenReadConnectionAsync(CancellationToken cancellationToken = default)
        => Store.OpenReadConnectionAsync(cancellationToken);

    public ValueTask<TResult> QueueWriteAsync<TResult>(
        Func<DbConnection, DbTransaction, CancellationToken, ValueTask<TResult>> write,
        CancellationToken cancellationToken = default)
        => Store.QueueWriteAsync(write, cancellationToken);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort; SQLite may still hold a handle briefly on dispose ordering.
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
