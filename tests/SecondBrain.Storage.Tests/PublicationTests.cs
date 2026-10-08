using System.Data.Common;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Storage;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Migrations;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class PublicationTests
{
    [Theory]
    [InlineData(CrashPointNames.AfterStatePublishing, 1)]
    [InlineData(CrashPointNames.AfterIndexCommit, 2)]
    public async Task InterruptedPublicationRecoversAcrossTwoRestarts(string point, long indexedBeforeRecovery)
    {
        await using var root = new PublicationRoot();
        await using (var session = await root.OpenAsync())
        {
            Assert.Equal(PublicationOutcome.Published, await session.Coordinator.PublishAsync(NewDocument()));
            var crashing = new PublicationCoordinator(session.State, session.Index, new ThrowAt(point));
            await Assert.ThrowsAsync<InjectedPublicationCrash>(() => crashing.PublishAsync(NewDocument(1) with { Title = "New title", PropsJson = "{\"x\":2}" }).AsTask());
            Assert.Equal("publishing", await ScalarAsync(session.State, "SELECT publish_state FROM documents WHERE id='doc'"));
            Assert.Equal(1L, await ScalarAsync(session.State, "SELECT indexed_revision FROM documents WHERE id='doc'"));
            Assert.Equal(indexedBeforeRecovery, await ScalarAsync(session.Index, "SELECT revision FROM indexed_documents WHERE document_id='doc'"));
            Assert.Equal(indexedBeforeRecovery == 1 ? "Old title" : "New title", await ScalarAsync(session.Index, "SELECT title FROM indexed_documents WHERE document_id='doc'"));
        }

        await using (var restarted = await root.OpenAsync())
        {
            Assert.Equal(1, await restarted.Coordinator.RecoverAsync());
            Assert.Equal("published", await ScalarAsync(restarted.State, "SELECT publish_state FROM documents WHERE id='doc'"));
            Assert.Equal(2L, await ScalarAsync(restarted.State, "SELECT indexed_revision FROM documents WHERE id='doc'"));
            Assert.Equal("New title", await ScalarAsync(restarted.Index, "SELECT title FROM indexed_documents WHERE document_id='doc'"));
            Assert.Equal("{\"x\":2}", await ScalarAsync(restarted.Index, "SELECT props_json FROM indexed_documents WHERE document_id='doc'"));
            Assert.Equal(2L, await ScalarAsync(restarted.State, "SELECT count(*) FROM revisions"));
        }

        await using (var restartedAgain = await root.OpenAsync())
        {
            Assert.Equal(0, await restartedAgain.Coordinator.RecoverAsync());
            Assert.Equal(2L, await ScalarAsync(restartedAgain.State, "SELECT revision FROM documents WHERE id='doc'"));
            Assert.Equal(2L, await ScalarAsync(restartedAgain.State, "SELECT count(*) FROM revisions"));
            Assert.Equal(1L, await ScalarAsync(restartedAgain.Index, "SELECT count(*) FROM indexed_documents"));
        }
    }

    [Theory]
    [InlineData(CrashPointNames.AfterStatePublishing)]
    [InlineData(CrashPointNames.AfterIndexCommit)]
    public async Task GenerationOnlyCrashRecoversWithoutNewRevision(string point)
    {
        await using var root = new PublicationRoot();
        await using (var session = await root.OpenAsync())
        {
            await session.Coordinator.PublishAsync(NewDocument());
            var crashing = new PublicationCoordinator(session.State, session.Index, new ThrowAt(point));
            await Assert.ThrowsAsync<InjectedPublicationCrash>(() => crashing.ReprocessAsync(
                new PublicationGenerationChange(new PublicationFence("doc", 1, "{\"extractor\":1}"), "{\"extractor\":2}")).AsTask());
            Assert.Equal(1L, await ScalarAsync(session.State, "SELECT revision FROM documents WHERE id='doc'"));
            Assert.Equal(point == CrashPointNames.AfterStatePublishing ? "{\"extractor\":1}" : "{\"extractor\":2}",
                await ScalarAsync(session.Index, "SELECT generation_json FROM indexed_document_generations WHERE document_id='doc'"));
        }
        await using (var restarted = await root.OpenAsync())
        {
            Assert.Equal(1, await restarted.Coordinator.RecoverAsync());
            Assert.Equal("{\"extractor\":2}", await ScalarAsync(restarted.Index, "SELECT generation_json FROM indexed_document_generations WHERE document_id='doc'"));
        }
        await using (var restartedAgain = await root.OpenAsync())
        {
            Assert.Equal(0, await restartedAgain.Coordinator.RecoverAsync());
            Assert.Equal(1L, await ScalarAsync(restartedAgain.State, "SELECT count(*) FROM revisions"));
        }
    }

    [Theory]
    [InlineData("deleted", "delete")]
    [InlineData("purged", "purge")]
    public async Task TombstoneRemovesIndexAndLateJobCannotResurrectIt(string status, string cause)
    {
        await using var root = new PublicationRoot();
        await using var session = await root.OpenAsync();
        await session.Coordinator.PublishAsync(NewDocument());
        Assert.Equal(PublicationOutcome.Published, await session.Coordinator.PublishAsync(NewDocument(1) with { Status = status, Cause = cause }));
        Assert.Equal(0L, await ScalarAsync(session.Index, "SELECT count(*) FROM indexed_documents"));
        Assert.Equal(2L, await ScalarAsync(session.State, "SELECT indexed_revision FROM documents WHERE id='doc'"));
        Assert.Equal(cause, await ScalarAsync(session.State, "SELECT cause FROM revisions WHERE revision=2"));
        Assert.Equal(PublicationOutcome.Superseded, await session.Coordinator.PublishCurrentAsync(new PublicationFence("doc", 1, "{\"extractor\":1}")));
        Assert.Equal(0L, await ScalarAsync(session.Index, "SELECT count(*) FROM indexed_documents"));
        Assert.Equal(0, await session.Coordinator.RecoverAsync());
    }

    [Theory]
    [InlineData(CrashPointNames.AfterStatePublishing)]
    [InlineData(CrashPointNames.AfterIndexCommit)]
    public async Task DeletionCrashRecoversAcrossTwoRestarts(string point)
    {
        await using var root = new PublicationRoot();
        await using (var session = await root.OpenAsync())
        {
            await session.Coordinator.PublishAsync(NewDocument());
            var crashing = new PublicationCoordinator(session.State, session.Index, new ThrowAt(point));
            await Assert.ThrowsAsync<InjectedPublicationCrash>(() => crashing.PublishAsync(NewDocument(1) with { Status = "deleted", Cause = "delete" }).AsTask());
            Assert.Equal(point == CrashPointNames.AfterStatePublishing ? 1L : 0L, await ScalarAsync(session.Index, "SELECT count(*) FROM indexed_documents"));
        }
        await using (var restarted = await root.OpenAsync())
        {
            Assert.Equal(1, await restarted.Coordinator.RecoverAsync());
            Assert.Equal(0L, await ScalarAsync(restarted.Index, "SELECT count(*) FROM indexed_documents"));
        }
        await using (var restartedAgain = await root.OpenAsync())
        {
            Assert.Equal(0, await restartedAgain.Coordinator.RecoverAsync());
            Assert.Equal("published", await ScalarAsync(restartedAgain.State, "SELECT publish_state FROM documents WHERE id='doc'"));
            Assert.Equal(0L, await ScalarAsync(restartedAgain.Index, "SELECT count(*) FROM indexed_documents"));
        }
    }

    [Fact]
    public async Task RestoreAdvancesRevisionAndRetainsCreationTime()
    {
        await using var root = new PublicationRoot();
        await using var session = await root.OpenAsync();
        var created = DateTimeOffset.Parse("2025-01-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        await session.Coordinator.PublishAsync(NewDocument() with { CreatedAt = created });
        await session.Coordinator.PublishAsync(NewDocument(1) with { Status = "deleted", Cause = "delete" });
        Assert.Equal(PublicationOutcome.Published, await session.Coordinator.PublishAsync(NewDocument(2) with { Cause = "restore" }));
        Assert.Equal(3L, await ScalarAsync(session.Index, "SELECT revision FROM indexed_documents WHERE document_id='doc'"));
        Assert.Equal(created.ToString("O", System.Globalization.CultureInfo.InvariantCulture), await ScalarAsync(session.State, "SELECT created_at FROM documents WHERE id='doc'"));
        Assert.Equal(DBNull.Value, await ScalarAsync(session.State, "SELECT deleted_at FROM documents WHERE id='doc'"));
    }

    [Fact]
    public async Task StaleGenerationFailsEvenWhenRevisionIsUnchanged()
    {
        await using var root = new PublicationRoot();
        await using var session = await root.OpenAsync();
        await session.Coordinator.PublishAsync(NewDocument());
        await session.Coordinator.ReprocessAsync(new PublicationGenerationChange(new PublicationFence("doc", 1, "{\"extractor\":1}"), "{\"extractor\":2}"));
        Assert.Equal(PublicationOutcome.Superseded, await session.Coordinator.PublishCurrentAsync(new PublicationFence("doc", 1, "{\"extractor\":1}")));
        Assert.Equal(PublicationOutcome.Superseded, await session.Coordinator.ReprocessAsync(new PublicationGenerationChange(
            new PublicationFence("doc", 1, "{\"extractor\":1}"), "{\"extractor\":3}")));
        Assert.Equal("{\"extractor\":2}", await ScalarAsync(session.Index, "SELECT generation_json FROM indexed_document_generations"));
        Assert.Equal(1L, await ScalarAsync(session.State, "SELECT count(*) FROM revisions"));
    }

    [Theory]
    [InlineData("UPDATE documents SET revision=2 WHERE id='doc'")]
    [InlineData("UPDATE publication_fences SET generation_json='{\"extractor\":2}' WHERE document_id='doc'")]
    public async Task FenceIsRecheckedInsideIndexTransaction(string supersedeSql)
    {
        await using var root = new PublicationRoot();
        await using var session = await root.OpenAsync();
        await session.Coordinator.PublishAsync(NewDocument());
        var racedIndex = new BeforeIndexWrite(session.Index, async () => await WriteAsync(session.State, supersedeSql));
        var coordinator = new PublicationCoordinator(session.State, racedIndex);
        Assert.Equal(PublicationOutcome.Superseded, await coordinator.PublishCurrentAsync(new PublicationFence("doc", 1, "{\"extractor\":1}")));
        Assert.Equal(1L, await ScalarAsync(session.Index, "SELECT revision FROM indexed_documents"));
        Assert.Equal("{\"extractor\":1}", await ScalarAsync(session.Index, "SELECT generation_json FROM indexed_document_generations"));
        Assert.Equal("publishing", await ScalarAsync(session.State, "SELECT publish_state FROM documents"));
    }

    [Fact]
    public async Task IndexFailureRollsBackReplacementAndRecoveryPublishesIt()
    {
        await using var root = new PublicationRoot();
        await using var session = await root.OpenAsync();
        await session.Coordinator.PublishAsync(NewDocument());
        var coordinator = new PublicationCoordinator(session.State, new FailIndexCommit(session.Index));
        await Assert.ThrowsAsync<InjectedPublicationCrash>(() => coordinator.PublishAsync(NewDocument(1) with { Title = "Replacement" }).AsTask());
        Assert.Equal("Old title", await ScalarAsync(session.Index, "SELECT title FROM indexed_documents"));
        Assert.Equal(1L, await ScalarAsync(session.Index, "SELECT revision FROM indexed_documents"));
        Assert.Equal("publishing", await ScalarAsync(session.State, "SELECT publish_state FROM documents"));
        Assert.Equal(1, await session.Coordinator.RecoverAsync());
        Assert.Equal("Replacement", await ScalarAsync(session.Index, "SELECT title FROM indexed_documents"));
        Assert.Equal(0, await session.Coordinator.RecoverAsync());
    }

    [Fact]
    public async Task PublicationLockSpansBothStoresAndIsPerDocument()
    {
        await using var root = new PublicationRoot();
        await using var session = await root.OpenAsync();
        var boundary = new PauseAfterState();
        var firstCoordinator = new PublicationCoordinator(session.State, session.Index, boundary);
        var first = firstCoordinator.PublishAsync(NewDocument()).AsTask();
        await boundary.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = session.Coordinator.PublishAsync(NewDocument() with { Title = "Racing title" }).AsTask();
        Assert.False(second.IsCompleted);
        Assert.Equal(PublicationOutcome.Published, await session.Coordinator.PublishAsync(NewDocument() with { DocumentId = "other" }));
        Assert.Equal("publishing", await ScalarAsync(session.State, "SELECT publish_state FROM documents WHERE id='doc'"));
        boundary.Release.TrySetResult();
        Assert.Equal(PublicationOutcome.Published, await first);
        Assert.Equal(PublicationOutcome.Superseded, await second);
        Assert.Equal("Old title", await ScalarAsync(session.Index, "SELECT title FROM indexed_documents WHERE document_id='doc'"));
        Assert.Equal(2L, await ScalarAsync(session.State, "SELECT count(*) FROM revisions"));
    }

    [Fact]
    public async Task EquivalentGenerationObjectsShareTheSameFence()
    {
        await using var root = new PublicationRoot();
        await using var session = await root.OpenAsync();
        await session.Coordinator.PublishAsync(NewDocument() with { GenerationJson = "{\"normalizer\":1,\"extractor\":2}" });
        Assert.Equal(PublicationOutcome.Published, await session.Coordinator.PublishCurrentAsync(new PublicationFence("doc", 1, "{ \"extractor\" : 2, \"normalizer\" : 1 }")));
        Assert.Equal("{\"extractor\":2,\"normalizer\":1}", await ScalarAsync(session.Index, "SELECT generation_json FROM indexed_document_generations"));
        Assert.Equal(1L, await ScalarAsync(session.State, "SELECT count(*) FROM revisions"));
    }

    [Fact]
    public async Task MetadataAndOriginalPropertiesArePreservedInTheirRespectiveStores()
    {
        await using var root = new PublicationRoot();
        await using var session = await root.OpenAsync();
        await session.Coordinator.PublishAsync(NewDocument() with
        {
            Type = "article", TypeOrigin = "override", PropsJson = "{\"ends_at\":\"2026-10-08T15:00:00Z\"}",
            PropsOriginalJson = "{\"unrecognized\":\"preserved\"}", OccurredAt = "2026-10-08T14:00:00Z", OccurredPrecision = "datetime"
        });
        Assert.Equal("{\"unrecognized\":\"preserved\"}", await ScalarAsync(session.State, "SELECT props_original_json FROM documents"));
        Assert.Equal("article", await ScalarAsync(session.Index, "SELECT type FROM indexed_documents"));
        Assert.Equal("2026-10-08T15:00:00Z", await ScalarAsync(session.Index, "SELECT ends_at FROM indexed_documents"));
        Assert.Equal("2026-10-08T14:00:00Z", await ScalarAsync(session.Index, "SELECT occurred_at FROM indexed_documents"));
    }

    [Fact]
    public async Task JournalRevisionIsPublishedByStartupRecovery()
    {
        await using var root = new PublicationRoot();
        var before = System.Text.Encoding.UTF8.GetBytes("before");
        var after = System.Text.Encoding.UTF8.GetBytes("after");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(before)).ToLowerInvariant();
        var vault = Path.Combine(root.DataRoot, "vault");
        Directory.CreateDirectory(vault);
        var destination = Path.Combine(vault, "note.md");
        await File.WriteAllBytesAsync(destination, before);
        await using (var session = await root.OpenAsync())
        {
            await session.Coordinator.PublishAsync(NewDocument() with { ContentHash = hash });
            using var journal = new MutationJournal(session.State, root.DataRoot);
            var mutation = await journal.ExecuteAsync(new MutationWriteRequest
            {
                MutationId = "journal-publication", Kind = "write_back", OperationId = "operation", Destination = destination,
                Content = after, ExpectedHash = hash,
                Finalization = new MutationFinalization { DocumentId = "doc", RevisionBefore = 1, RevisionAfter = 2 }
            });
            Assert.Equal("finalized", mutation.Status);
            Assert.Equal("publishing", await ScalarAsync(session.State, "SELECT publish_state FROM documents"));
            Assert.Equal(1L, await ScalarAsync(session.Index, "SELECT revision FROM indexed_documents"));
        }
        await using (var restarted = await root.OpenAsync())
        {
            Assert.Equal(1, await restarted.Coordinator.RecoverAsync());
            Assert.Equal(2L, await ScalarAsync(restarted.Index, "SELECT revision FROM indexed_documents"));
            Assert.Equal("{\"extractor\":1}", await ScalarAsync(restarted.Index, "SELECT generation_json FROM indexed_document_generations"));
            Assert.Equal(PublicationOutcome.Superseded, await restarted.Coordinator.PublishCurrentAsync(new PublicationFence("doc", 1, "{\"extractor\":1}")));
        }
        await using (var restartedAgain = await root.OpenAsync())
        {
            Assert.Equal(0, await restartedAgain.Coordinator.RecoverAsync());
            Assert.Equal("finalized", await ScalarAsync(restartedAgain.State, "SELECT status FROM mutations"));
            Assert.Equal(2L, await ScalarAsync(restartedAgain.State, "SELECT count(*) FROM revisions"));
            Assert.Equal(after, await File.ReadAllBytesAsync(destination));
        }
    }

    private static DocumentPublication NewDocument(long expectedRevision = 0) => new(
        "doc", "source", expectedRevision, "{\"extractor\":1}", "edit", "Old title", "hash", "api:test");

    private static async Task<object?> ScalarAsync(IStoreHandle store, string sql)
    {
        await using var lease = await store.OpenReadConnectionAsync();
        await using var command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async ValueTask WriteAsync(IStateStore state, string sql)
    {
        await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            return await command.ExecuteNonQueryAsync(token);
        });
    }

    private sealed class PublicationRoot : IAsyncDisposable
    {
        private readonly string dataRoot = Path.Combine(Path.GetTempPath(), "secondbrain-publication-" + Guid.NewGuid().ToString("N"));
        public string DataRoot => dataRoot;

        public async Task<PublicationSession> OpenAsync()
        {
            var state = new SqliteStateStore(dataRoot);
            var index = new SqliteIndexStore(dataRoot);
            try
            {
                using var migration = new MigrationRunner(state, index, dataRoot);
                await migration.MigrateAsync();
                await WriteAsync(state, "INSERT OR IGNORE INTO sources(id,kind,name,created_at) VALUES('source','api','test','2026-10-08T00:00:00Z')");
                return new PublicationSession(state, index);
            }
            catch
            {
                await index.DisposeAsync();
                await state.DisposeAsync();
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PublicationSession(SqliteStateStore state, SqliteIndexStore index) : IAsyncDisposable
    {
        public SqliteStateStore State { get; } = state;
        public SqliteIndexStore Index { get; } = index;
        public PublicationCoordinator Coordinator { get; } = new(state, index);

        public async ValueTask DisposeAsync()
        {
            await Index.DisposeAsync();
            await State.DisposeAsync();
        }
    }

    private sealed class InjectedPublicationCrash : Exception;

    private sealed class ThrowAt(string point) : ICrashPoints
    {
        public ValueTask HitAsync(string actual, CancellationToken cancellationToken = default)
        {
            if (actual == point)
            {
                throw new InjectedPublicationCrash();
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PauseAfterState : ICrashPoints
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask HitAsync(string point, CancellationToken cancellationToken = default)
        {
            if (point == CrashPointNames.AfterStatePublishing)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
        }
    }

    private sealed class BeforeIndexWrite(IIndexStore inner, Func<ValueTask> before) : IIndexStore
    {
        public ValueTask<IReadConnectionLease> OpenReadConnectionAsync(CancellationToken cancellationToken = default) => inner.OpenReadConnectionAsync(cancellationToken);
        public ValueTask<TResult> QueueWriteAsync<TResult>(Func<DbConnection, DbTransaction, CancellationToken, ValueTask<TResult>> write, CancellationToken cancellationToken = default) =>
            inner.QueueWriteAsync(async (connection, transaction, token) =>
            {
                await before();
                return await write(connection, transaction, token);
            }, cancellationToken);
    }

    private sealed class FailIndexCommit(IIndexStore inner) : IIndexStore
    {
        public ValueTask<IReadConnectionLease> OpenReadConnectionAsync(CancellationToken cancellationToken = default) => inner.OpenReadConnectionAsync(cancellationToken);
        public ValueTask<TResult> QueueWriteAsync<TResult>(Func<DbConnection, DbTransaction, CancellationToken, ValueTask<TResult>> write, CancellationToken cancellationToken = default) =>
            inner.QueueWriteAsync<TResult>(async (connection, transaction, token) =>
            {
                await write(connection, transaction, token);
                throw new InjectedPublicationCrash();
            }, cancellationToken);
    }
}
