using System.Security.Cryptography;
using System.Text;
using Dapper;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Storage;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Migrations;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class JournalTests
{
    [Theory]
    [InlineData(CrashPointNames.AfterApply)]
    [InlineData(CrashPointNames.BeforeFinalize)]
    public async Task AppliedIsFinalizedOnce(string crashPoint)
    {
        await using var root = new JournalRoot();
        var old = Encoding.UTF8.GetBytes("original");
        var content = Encoding.UTF8.GetBytes("replacement");
        MutationWriteRequest request;
        await using (var session = await root.OpenAsync())
        {
            await File.WriteAllBytesAsync(root.Destination, old);
            await SeedFinalizationAsync(session.State, Hash(old));
            request = Request(root, content, Hash(old)) with
            {
                Finalization = new MutationFinalization
                {
                    DocumentId = "doc", RevisionBefore = 1, RevisionAfter = 2,
                    PendingOperationId = "proposal", ToolExecutionId = "execution",
                    Idempotency = new MutationIdempotency("request-key", "credential", Hash(content), "{\"mutation_id\":\"mutation\"}")
                },
                DocumentReferences = ["doc"],
                ReplayResult = Encoding.UTF8.GetBytes("{\"document_id\":\"doc\",\"revision\":2}")
            };
            using var crash = new MutationJournal(session.State, root.Path, new ThrowAt(crashPoint));
            await Assert.ThrowsAsync<InjectedJournalCrash>(() => crash.ExecuteAsync(request).AsTask());
            Assert.Equal("applied", await ScalarAsync<string>(session.State, "SELECT status FROM mutations"));
            Assert.Equal(1, await ScalarAsync<long>(session.State, "SELECT revision FROM documents"));
            Assert.Equal("pending", await ScalarAsync<string>(session.State, "SELECT status FROM pending_operations"));
            Assert.Equal("running", await ScalarAsync<string>(session.State, "SELECT status FROM tool_executions"));
            Assert.Equal(0, await ScalarAsync<long>(session.State, "SELECT count(*) FROM idempotency"));
            Assert.Equal(content, await File.ReadAllBytesAsync(root.Destination));
        }

        string firstFinalizedAt;
        await using (var restarted = await root.OpenAsync())
        {
            Assert.Equal(new MutationRecoveryReport(1, 0, 0, 0), await restarted.Journal.RecoverAsync());
            Assert.Equal("finalized", await ScalarAsync<string>(restarted.State, "SELECT status FROM mutations"));
            Assert.Equal(2, await ScalarAsync<long>(restarted.State, "SELECT revision FROM documents"));
            Assert.Equal(Hash(content), await ScalarAsync<string>(restarted.State, "SELECT content_hash FROM documents"));
            Assert.Equal(2, await ScalarAsync<long>(restarted.State, "SELECT count(*) FROM revisions"));
            Assert.Equal("executed", await ScalarAsync<string>(restarted.State, "SELECT status FROM pending_operations"));
            Assert.Equal("committed", await ScalarAsync<string>(restarted.State, "SELECT status FROM tool_executions"));
            Assert.Equal(1, await ScalarAsync<long>(restarted.State, "SELECT count(*) FROM idempotency"));
            Assert.Equal(1, await ScalarAsync<long>(restarted.State, "SELECT count(*) FROM payload_refs WHERE referrer_kind='tool_execution'"));
            Assert.Equal(1, await ScalarAsync<long>(restarted.State, "SELECT count(*) FROM payload_refs WHERE referrer_kind='idempotency'"));
            Assert.Equal(4, await ScalarAsync<long>(restarted.State, "SELECT count(*) FROM payloads"));
            firstFinalizedAt = await ScalarAsync<string>(restarted.State, "SELECT updated_at FROM mutations");
            Assert.Equal("finalized", (await restarted.Journal.ExecuteAsync(request)).Status);
        }

        await using (var restartedAgain = await root.OpenAsync())
        {
            Assert.Equal(new MutationRecoveryReport(0, 0, 0, 0), await restartedAgain.Journal.RecoverAsync());
            Assert.Equal(firstFinalizedAt, await ScalarAsync<string>(restartedAgain.State, "SELECT updated_at FROM mutations"));
            Assert.Equal(2, await ScalarAsync<long>(restartedAgain.State, "SELECT count(*) FROM revisions"));
            Assert.Equal(1, await ScalarAsync<long>(restartedAgain.State, "SELECT count(*) FROM idempotency"));
            Assert.Equal(content, await File.ReadAllBytesAsync(root.Destination));
        }
    }

    [Fact]
    public async Task PreparedTempIsDiscardedAndSecondRestartChangesNothing()
    {
        await using var root = new JournalRoot();
        var original = Encoding.UTF8.GetBytes("original");
        await File.WriteAllBytesAsync(root.Destination, original);
        await using (var session = await root.OpenAsync())
        {
            using var journal = new MutationJournal(session.State, root.Path, new ThrowAt(MutationCrashPointNames.AfterTempFlush));
            await Assert.ThrowsAsync<InjectedJournalCrash>(() => journal.ExecuteAsync(Request(root, "new", Hash(original))).AsTask());
            Assert.Equal("prepared", await ScalarAsync<string>(session.State, "SELECT status FROM mutations"));
            Assert.Single(Directory.GetFiles(root.Vault, ".sb-*.tmp"));
            Assert.Equal(original, await File.ReadAllBytesAsync(root.Destination));
        }
        string discardedAt;
        await using (var restarted = await root.OpenAsync())
        {
            Assert.Equal(new MutationRecoveryReport(0, 1, 0, 0), await restarted.Journal.RecoverAsync());
            Assert.Empty(Directory.GetFiles(root.Vault, ".sb-*.tmp"));
            Assert.Equal("failed", await ScalarAsync<string>(restarted.State, "SELECT status FROM mutations"));
            discardedAt = await ScalarAsync<string>(restarted.State, "SELECT updated_at FROM mutations");
            Assert.Equal(original, await File.ReadAllBytesAsync(root.Destination));
        }
        await using (var restartedAgain = await root.OpenAsync())
        {
            Assert.Equal(new MutationRecoveryReport(0, 0, 0, 0), await restartedAgain.Journal.RecoverAsync());
            Assert.Equal(discardedAt, await ScalarAsync<string>(restartedAgain.State, "SELECT updated_at FROM mutations"));
            Assert.Empty(Directory.GetFiles(root.Vault, ".sb-*.tmp"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenameBeforeAppliedRecordIsReconciledRatherThanDiscarded(bool replaceExisting)
    {
        await using var root = new JournalRoot();
        var old = Encoding.UTF8.GetBytes("old");
        if (replaceExisting) await File.WriteAllBytesAsync(root.Destination, old);
        var request = Request(root, "new", replaceExisting ? Hash(old) : null);
        await using (var session = await root.OpenAsync())
        {
            using var crash = new MutationJournal(session.State, root.Path, new ThrowAt(MutationCrashPointNames.AfterRename));
            await Assert.ThrowsAsync<InjectedJournalCrash>(() => crash.ExecuteAsync(request).AsTask());
            Assert.Equal("prepared", await ScalarAsync<string>(session.State, "SELECT status FROM mutations"));
            Assert.Equal(Hash(Encoding.UTF8.GetBytes("new")), await ScalarAsync<string>(session.State, "SELECT resulting_hash FROM mutations"));
            Assert.Equal("new", await File.ReadAllTextAsync(root.Destination));
        }
        await using (var restarted = await root.OpenAsync())
        {
            Assert.Equal(new MutationRecoveryReport(1, 0, 0, 1), await restarted.Journal.RecoverAsync());
            Assert.Equal("finalized", (await restarted.Journal.ExecuteAsync(request)).Status);
        }
        await using (var restartedAgain = await root.OpenAsync())
        {
            Assert.Equal(new MutationRecoveryReport(0, 0, 0, 0), await restartedAgain.Journal.RecoverAsync());
            Assert.Equal("new", await File.ReadAllTextAsync(root.Destination));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedAtomicExchangeChecksTheDisplacedFile(bool raceAnExternalEdit)
    {
        await using var root = new JournalRoot();
        await File.WriteAllTextAsync(root.Destination, "old");
        await using (var session = await root.OpenAsync())
        {
            using var crash = new MutationJournal(session.State, root.Path, new CallbackCrash(async point =>
            {
                if (point == MutationCrashPointNames.BeforeAtomicRename && raceAnExternalEdit)
                    await File.WriteAllTextAsync(root.Destination, "unseen edit");
                if (point == MutationCrashPointNames.AfterAtomicExchange) throw new InjectedJournalCrash();
            }));
            await Assert.ThrowsAsync<InjectedJournalCrash>(() => crash.ExecuteAsync(Request(root, "new", Hash(Encoding.UTF8.GetBytes("old")))).AsTask());
            Assert.Equal("new", await File.ReadAllTextAsync(root.Destination));
            Assert.Single(Directory.GetFiles(root.Vault, ".sb-*.tmp"));
        }
        await using (var restarted = await root.OpenAsync())
        {
            var report = await restarted.Journal.RecoverAsync();
            if (raceAnExternalEdit)
            {
                Assert.Equal(new MutationRecoveryReport(0, 0, 1, 0), report);
                Assert.Equal("unseen edit", await File.ReadAllTextAsync(root.Destination));
                Assert.Equal("new", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(root.Vault, "*.conflict-*.md"))));
                Assert.Equal("conflict", await ScalarAsync<string>(restarted.State, "SELECT status FROM mutations"));
            }
            else
            {
                Assert.Equal(new MutationRecoveryReport(1, 0, 0, 1), report);
                Assert.Equal("new", await File.ReadAllTextAsync(root.Destination));
            }
            Assert.Empty(Directory.GetFiles(root.Vault, ".sb-*.tmp"));
        }
        await using (var restartedAgain = await root.OpenAsync())
        {
            Assert.Equal(new MutationRecoveryReport(0, 0, 0, 0), await restartedAgain.Journal.RecoverAsync());
            Assert.Equal(raceAnExternalEdit ? "unseen edit" : "new", await File.ReadAllTextAsync(root.Destination));
        }
    }

    [Fact]
    public async Task ConflictKeepsExternalEditAndRecordsItsNewRevision()
    {
        await using var root = new JournalRoot();
        await File.WriteAllTextAsync(root.Destination, "external edit");
        await using var session = await root.OpenAsync();
        var oldHash = Hash(Encoding.UTF8.GetBytes("old"));
        await SeedFinalizationAsync(session.State, oldHash);
        var request = Request(root, "proposal content", oldHash) with
        {
            Finalization = new MutationFinalization { DocumentId = "doc", RevisionBefore = 1, RevisionAfter = 2, PendingOperationId = "proposal" }
        };
        var result = await session.Journal.ExecuteAsync(request);
        Assert.Equal("conflict", result.Status);
        Assert.NotNull(result.ConflictPath);
        Assert.Equal("external edit", await File.ReadAllTextAsync(root.Destination));
        Assert.Equal("proposal content", await File.ReadAllTextAsync(result.ConflictPath));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(result.ConflictPath));
        Assert.Equal("target_changed", await ScalarAsync<string>(session.State, "SELECT status FROM pending_operations"));
        Assert.Equal(2, await ScalarAsync<long>(session.State, "SELECT revision FROM documents"));
        Assert.Equal(Hash(Encoding.UTF8.GetBytes("external edit")), await ScalarAsync<string>(session.State, "SELECT content_hash FROM documents"));
        Assert.Equal("external_edit", await ScalarAsync<string>(session.State, "SELECT cause FROM revisions WHERE revision=2"));
        Assert.Equal(result, await session.Journal.ExecuteAsync(request));
        Assert.Equal(new MutationRecoveryReport(0, 0, 0, 0), await session.Journal.RecoverAsync());
        Assert.Single(Directory.GetFiles(root.Vault, "*.conflict-*.md"));
    }

    [Fact]
    public async Task RecoveryPreservesBothExternalReplacementsAfterInterruptedExchange()
    {
        await using var root = new JournalRoot();
        await File.WriteAllTextAsync(root.Destination, "old");
        await using (var session = await root.OpenAsync())
        {
            using var crash = new MutationJournal(session.State, root.Path, new CallbackCrash(async point =>
            {
                if (point == MutationCrashPointNames.BeforeAtomicRename) await File.WriteAllTextAsync(root.Destination, "first unseen edit");
                if (point == MutationCrashPointNames.AfterAtomicExchange) throw new InjectedJournalCrash();
            }));
            await Assert.ThrowsAsync<InjectedJournalCrash>(() => crash.ExecuteAsync(Request(root, "new", Hash(Encoding.UTF8.GetBytes("old")))).AsTask());
        }
        await File.WriteAllTextAsync(root.Destination, "second external edit");
        await using (var restarted = await root.OpenAsync())
        {
            Assert.Equal(new MutationRecoveryReport(0, 0, 1, 0), await restarted.Journal.RecoverAsync());
            Assert.Equal("second external edit", await File.ReadAllTextAsync(root.Destination));
            Assert.Equal("first unseen edit", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(root.Vault, "*.external"))));
            Assert.Equal("new", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(root.Vault, "*.conflict-*.md"))));
            Assert.Empty(Directory.GetFiles(root.Vault, ".sb-*.tmp"));
        }
        await using (var restartedAgain = await root.OpenAsync())
            Assert.Equal(new MutationRecoveryReport(0, 0, 0, 0), await restartedAgain.Journal.RecoverAsync());
    }

    [Theory]
    [InlineData(MutationCrashPointNames.AfterTempFlush)]
    [InlineData(MutationCrashPointNames.BeforeAtomicRename)]
    public async Task ExternalEditBetweenHashAndRenameIsPreserved(string boundary)
    {
        await using var root = new JournalRoot();
        await File.WriteAllTextAsync(root.Destination, "old");
        await using var session = await root.OpenAsync();
        using var journal = new MutationJournal(session.State, root.Path, new CallbackCrash(async point =>
        {
            if (point == boundary) await File.WriteAllTextAsync(root.Destination, "racing edit");
        }));
        var result = await journal.ExecuteAsync(Request(root, "new", Hash(Encoding.UTF8.GetBytes("old"))));
        Assert.Equal("conflict", result.Status);
        Assert.Equal("racing edit", await File.ReadAllTextAsync(root.Destination));
        Assert.Equal("new", await File.ReadAllTextAsync(result.ConflictPath!));
        Assert.Empty(Directory.GetFiles(root.Vault, ".sb-*.tmp"));
        Assert.Equal(new MutationRecoveryReport(0, 0, 0, 0), await session.Journal.RecoverAsync());
    }

    [Fact]
    public async Task CreateRaceDoesNotOverwriteAFileThatAppearedAfterTheCheck()
    {
        await using var root = new JournalRoot();
        await using var session = await root.OpenAsync();
        using var journal = new MutationJournal(session.State, root.Path, new CallbackCrash(async point =>
        {
            if (point == MutationCrashPointNames.BeforeAtomicRename) await File.WriteAllTextAsync(root.Destination, "external create");
        }));
        var result = await journal.ExecuteAsync(Request(root, "new", null));
        Assert.Equal("conflict", result.Status);
        Assert.Equal("external create", await File.ReadAllTextAsync(root.Destination));
        Assert.Equal("new", await File.ReadAllTextAsync(result.ConflictPath!));
    }

    [Theory]
    [InlineData(CrashPointNames.AfterApply)]
    [InlineData(CrashPointNames.BeforeFinalize)]
    public async Task AppliedHashMismatchBecomesConflictWithoutAcknowledgingARevision(string point)
    {
        await using var root = new JournalRoot();
        await File.WriteAllTextAsync(root.Destination, "old");
        await using (var session = await root.OpenAsync())
        {
            using var crash = new MutationJournal(session.State, root.Path, new ThrowAt(point));
            await Assert.ThrowsAsync<InjectedJournalCrash>(() => crash.ExecuteAsync(Request(root, "new", Hash(Encoding.UTF8.GetBytes("old")))).AsTask());
        }
        await File.WriteAllTextAsync(root.Destination, "external after apply");
        await using (var restarted = await root.OpenAsync())
        {
            Assert.Equal(new MutationRecoveryReport(0, 0, 1, 0), await restarted.Journal.RecoverAsync());
            Assert.Equal("external after apply", await File.ReadAllTextAsync(root.Destination));
            Assert.Equal("conflict", await ScalarAsync<string>(restarted.State, "SELECT status FROM mutations"));
            Assert.Equal("new", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(root.Vault, "*.conflict-*.md"))));
        }
        await using (var restartedAgain = await root.OpenAsync())
            Assert.Equal(new MutationRecoveryReport(0, 0, 0, 0), await restartedAgain.Journal.RecoverAsync());
    }

    [Fact]
    public async Task FinalizationFailureRollsBackAllDatabaseChanges()
    {
        await using var root = new JournalRoot();
        await File.WriteAllTextAsync(root.Destination, "old");
        await using var session = await root.OpenAsync();
        var oldHash = Hash(Encoding.UTF8.GetBytes("old"));
        await SeedFinalizationAsync(session.State, oldHash);
        var request = Request(root, "new", oldHash) with
        {
            Finalization = new MutationFinalization
            {
                DocumentId = "doc", RevisionBefore = 1, RevisionAfter = 2,
                PendingOperationId = "proposal", ToolExecutionId = "missing-execution"
            }
        };
        await session.Journal.PrepareAsync(request);
        await session.Journal.ApplyAsync(request.MutationId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.Journal.FinalizeAsync(request.MutationId).AsTask());
        Assert.Equal("applied", await ScalarAsync<string>(session.State, "SELECT status FROM mutations"));
        Assert.Equal("pending", await ScalarAsync<string>(session.State, "SELECT status FROM pending_operations"));
        Assert.Equal(1, await ScalarAsync<long>(session.State, "SELECT revision FROM documents"));
        Assert.Equal(1, await ScalarAsync<long>(session.State, "SELECT count(*) FROM revisions"));
    }

    [Fact]
    public async Task MutationIdAndDestinationAreReservedExactlyOnce()
    {
        await using var root = new JournalRoot();
        await using var session = await root.OpenAsync();
        var request = Request(root, "new", null);
        var prepared = await session.Journal.PrepareAsync(request);
        Assert.Equal(prepared, await session.Journal.PrepareAsync(request));
        await Assert.ThrowsAsync<MutationReuseException>(() => session.Journal.PrepareAsync(request with { Content = Encoding.UTF8.GetBytes("different") }).AsTask());
        await Assert.ThrowsAsync<MutationReuseException>(() => session.Journal.PrepareAsync(request with { Finalization = new MutationFinalization { PendingOperationId = "another" } }).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.Journal.PrepareAsync(request with { MutationId = "another" }).AsTask());
        Assert.Equal(1, await ScalarAsync<long>(session.State, "SELECT count(*) FROM mutations"));
        Assert.Equal(2, await ScalarAsync<long>(session.State, "SELECT count(*) FROM payloads"));
    }

    [Fact]
    public async Task FinalizedReplayNeverWritesTheFileAgain()
    {
        await using var root = new JournalRoot();
        await using var session = await root.OpenAsync();
        var request = Request(root, "new", null);
        var result = await session.Journal.ExecuteAsync(request);
        Assert.Equal("finalized", result.Status);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(root.Destination));
        await File.WriteAllTextAsync(root.Destination, "a later edit");
        Assert.Equal(result, await session.Journal.ExecuteAsync(request));
        Assert.Equal("a later edit", await File.ReadAllTextAsync(root.Destination));
        Assert.Equal(new MutationRecoveryReport(0, 0, 0, 0), await session.Journal.RecoverAsync());
    }

    [Fact]
    public async Task JournalRejectsSymlinkParentsAndDestinationSymlinks()
    {
        await using var root = new JournalRoot();
        await using var session = await root.OpenAsync();
        var outside = System.IO.Path.Combine(root.Path, "outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(System.IO.Path.Combine(outside, "note.md"), "outside");
        Directory.CreateSymbolicLink(System.IO.Path.Combine(root.Vault, "linked"), outside);
        await Assert.ThrowsAsync<IOException>(() => session.Journal.PrepareAsync(Request(root, "new", null) with { Destination = System.IO.Path.Combine(root.Vault, "linked", "note.md") }).AsTask());
        File.CreateSymbolicLink(root.Destination, System.IO.Path.Combine(outside, "note.md"));
        await session.Journal.PrepareAsync(Request(root, "new", null));
        await Assert.ThrowsAsync<IOException>(() => session.Journal.ApplyAsync("mutation").AsTask());
        Assert.Equal("outside", await File.ReadAllTextAsync(System.IO.Path.Combine(outside, "note.md")));
    }

    [Fact]
    public async Task JournalRejectsDestinationsOutsideTheManagedRoot()
    {
        await using var root = new JournalRoot();
        await using var session = await root.OpenAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => session.Journal.PrepareAsync(Request(root, "new", null) with { Destination = System.IO.Path.Combine(root.Path, "..", "outside.md") }).AsTask());
        Assert.Equal(0, await ScalarAsync<long>(session.State, "SELECT count(*) FROM mutations"));
    }

    private static MutationWriteRequest Request(JournalRoot root, string content, string? expectedHash) => Request(root, Encoding.UTF8.GetBytes(content), expectedHash);
    private static MutationWriteRequest Request(JournalRoot root, byte[] content, string? expectedHash) => new()
    {
        MutationId = "mutation", Kind = "proposal", OperationId = "operation", Destination = root.Destination,
        Content = content, ExpectedHash = expectedHash
    };
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async ValueTask<T> ScalarAsync<T>(IStateStore state, string sql)
    {
        await using var read = await state.OpenReadConnectionAsync();
        return (await read.Connection.ExecuteScalarAsync<T>(sql))!;
    }

    private static async ValueTask SeedFinalizationAsync(IStateStore state, string oldHash) =>
        await state.QueueWriteAsync(async (connection, transaction, ct) =>
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO sources(id,kind,name,created_at) VALUES('source','api','test','2026-10-08T00:00:00Z');
                INSERT INTO documents(id,source_id,type_origin,title,provenance,content_hash,status,created_at,updated_at)
                VALUES('doc','source','door','note','api:test',@oldHash,'indexed','2026-10-08T00:00:00Z','2026-10-08T00:00:00Z');
                INSERT INTO revisions(document_id,revision,cause,content_hash,created_at) VALUES('doc',1,'ingest',@oldHash,'2026-10-08T00:00:00Z');
                INSERT INTO credentials(id,name,kind,verifier,scopes,kid,account_epoch,created_at)
                VALUES('credential','test','session','verifier','read,write,infer,admin','kid',1,'2026-10-08T00:00:00Z');
                INSERT INTO conversations(id,owner_kind,created_at,updated_at) VALUES('conversation','account','2026-10-08T00:00:00Z','2026-10-08T00:00:00Z');
                INSERT INTO turns(id,conversation_id,ordinal,status,initiating_credential_id,credential_generation,account_epoch,allowed_tools_json,started_at)
                VALUES('turn','conversation',1,'running','credential',1,1,'[]','2026-10-08T00:00:00Z');
                INSERT INTO tool_executions(id,turn_id,tool,args_hash,status,initiating_credential_id,credential_generation,account_epoch)
                VALUES('execution','turn','write','hash','running','credential',1,1);
                INSERT INTO payloads(id,sha256,bytes,created_at) VALUES('proposal-payload','hash',X'01','2026-10-08T00:00:00Z');
                INSERT INTO pending_operations(id,turn_id,creator_credential_id,tool,tool_schema_version,payload_id,payload_hash,targets_json,policy_generation,status,execution_id,created_at,expires_at)
                VALUES('proposal','turn','credential','write',1,'proposal-payload','hash','[]',1,'pending','execution','2026-10-08T00:00:00Z','2026-10-09T00:00:00Z');
                """, new { oldHash }, transaction, cancellationToken: ct)));

    private sealed class JournalRoot : IAsyncDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "secondbrain-journal-" + Guid.NewGuid().ToString("N"));
        public string Vault => System.IO.Path.Combine(Path, "vault");
        public string Destination => System.IO.Path.Combine(Vault, "note.md");
        public JournalRoot() => Directory.CreateDirectory(Vault);

        public async Task<JournalSession> OpenAsync()
        {
            var state = new SqliteStateStore(Path);
            var index = new SqliteIndexStore(Path);
            try
            {
                using var migrations = new MigrationRunner(state, index, Path);
                await migrations.MigrateAsync();
                return new JournalSession(state, index, Path);
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
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class JournalSession(SqliteStateStore state, SqliteIndexStore index, string root) : IAsyncDisposable
    {
        public SqliteStateStore State { get; } = state;
        public MutationJournal Journal { get; } = new(state, root);
        public async ValueTask DisposeAsync()
        {
            Journal.Dispose();
            await index.DisposeAsync();
            await State.DisposeAsync();
        }
    }

    private sealed class InjectedJournalCrash : Exception;
    private sealed class ThrowAt(string point) : ICrashPoints
    {
        public ValueTask HitAsync(string actual, CancellationToken cancellationToken = default)
        {
            if (point == actual) throw new InjectedJournalCrash();
            return ValueTask.CompletedTask;
        }
    }
    private sealed class CallbackCrash(Func<string, Task> action) : ICrashPoints
    {
        public async ValueTask HitAsync(string point, CancellationToken cancellationToken = default) => await action(point);
    }
}
