namespace SecondBrain.Storage;

/// <summary>Finite store resource budgets, independent of admission limits.</summary>
public sealed record SqliteStoreOptions
{
    public int ReadPoolSize { get; init; } = 8;
    public int WriterQueueCapacity { get; init; } = 128;
    public TimeSpan StatementTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan TransactionTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan ReadLeaseTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public int WalAutoCheckpointPages { get; init; } = 1000;
    public long JournalSizeLimitBytes { get; init; } = 64 * 1024 * 1024;

    internal void Validate()
    {
        if (ReadPoolSize < 1 || WriterQueueCapacity < 1 || WalAutoCheckpointPages < 1 || JournalSizeLimitBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(ReadPoolSize), "Pool, queue, and WAL limits must be finite and positive.");
        foreach (var budget in new[] { StatementTimeout, TransactionTimeout, ReadLeaseTimeout })
            if (budget <= TimeSpan.Zero || budget > TimeSpan.FromHours(1))
                throw new ArgumentOutOfRangeException(nameof(StatementTimeout), "Store timeouts must be between zero and one hour.");
    }
}
