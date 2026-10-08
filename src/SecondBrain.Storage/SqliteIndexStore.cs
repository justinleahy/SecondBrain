using SecondBrain.Core.Storage;

namespace SecondBrain.Storage;

/// <summary>The disposable index/index.db store, with NORMAL synchronous WAL commits.</summary>
public sealed class SqliteIndexStore(string dataRoot, SqliteStoreOptions? options = null)
    : SqliteStore(dataRoot, state: false, options), IIndexStore;
