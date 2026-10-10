using SecondBrain.Core.Limits;

namespace SecondBrain.Storage.Ingestion;

/// <summary>Restores committed corpus and queue accounting before HTTP admission and after lifecycle changes.</summary>
/// <remarks>
/// Aggregates are read from a snapshot reader, never the serialized state writer. Concurrent callers coalesce:
/// each waits for a read that starts after its call, and at most one read runs with one more queued behind it.
/// </remarks>
public sealed class PersistentAdmissionAccounting(IStateStore state, IAdmissionController? controller = null) : IPersistentAdmissionAccounting
{
    private readonly object gate = new();
    private TaskCompletionSource<AdmissionSnapshot>? queued;
    private bool running;
    private static long version;

    public async ValueTask<AdmissionSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<AdmissionSnapshot> flight;
        var start = false;
        lock (gate)
        {
            // A queued read has not started yet, so it observes every commit that preceded this call.
            flight = queued ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!running) running = start = true;
        }
        if (start) _ = Task.Run(PumpAsync, CancellationToken.None);
        return await flight.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            TaskCompletionSource<AdmissionSnapshot> flight;
            lock (gate)
            {
                if (queued is null) { running = false; return; }
                flight = queued; queued = null;
            }
            try { flight.SetResult(await ReadAsync().ConfigureAwait(false)); }
            catch (Exception exception) { flight.SetException(exception); }
        }
    }

    private async ValueTask<AdmissionSnapshot> ReadAsync()
    {
        // Reads run one at a time, so a later sequence always carries a snapshot at least as new.
        var sequence = Interlocked.Increment(ref version);
        await using var lease = await state.OpenReadConnectionAsync(CancellationToken.None).ConfigureAwait(false);
        await using var read = SqliteDocumentRepository.Command(lease.Connection, null,
            "SELECT (SELECT COUNT(*) FROM jobs WHERE status IN ('queued','running')),(SELECT COALESCE(SUM(size_bytes),0) FROM documents WHERE status NOT IN ('deleted','purged'))");
        await using var reader = await read.ExecuteReaderAsync(CancellationToken.None).ConfigureAwait(false);
        await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        var accounting = (Jobs: reader.GetInt32(0), Bytes: reader.GetInt64(1));
        controller?.RestorePersistent(sequence, accounting.Jobs, accounting.Bytes);
        return new AdmissionSnapshot(accounting.Jobs, accounting.Bytes, 0);
    }
}
