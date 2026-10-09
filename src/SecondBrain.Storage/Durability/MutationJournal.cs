using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Storage;

namespace SecondBrain.Storage.Durability;

/// <summary>Persistent prepare/apply/finalize for managed-file replacement, with replayable finalization.</summary>
public sealed class MutationJournal : IMutationJournal, IDisposable
{
    private const int MaximumPayloadBytes = 64 * 1024 * 1024;
    private readonly IStateStore _state;
    private readonly ManagedMutationFiles _files;
    private readonly ICrashPoints _crashPoints;
    // One daemon owns a data root; serialize filesystem work independently of the database writer.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MutationJournal(IStateStore state, string dataRoot, ICrashPoints? crashPoints = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _files = new ManagedMutationFiles(dataRoot);
        _crashPoints = crashPoints ?? new NoOpCrashPoints();
    }

    public async ValueTask<MutationRecord> PrepareAsync(MutationWriteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MutationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OperationId);
        ArgumentNullException.ThrowIfNull(request.Content);
        ArgumentNullException.ThrowIfNull(request.DocumentReferences);
        if (request.MutationId.Length > 256 || request.OperationId.Length > 256 || request.DocumentReferences.Count > 1024)
            throw new ArgumentException("Mutation identifiers or lineage exceed their bounds.", nameof(request));
        if (request.Kind is not ("proposal" or "import" or "trash" or "restore" or "purge" or "write_back"))
            throw new ArgumentException("Unknown mutation kind.", nameof(request));
        if (request.Content.Length > MaximumPayloadBytes || request.ReplayResult?.Length > MaximumPayloadBytes)
            throw new ArgumentException("Mutation payload exceeds 64 MiB.", nameof(request));
        var destination = _files.NormalizeDestination(request.Destination);
        // Opening the retained parent also rejects symlink traversal before the reservation is admitted.
        using (var parent = _files.Open(destination, request.MutationId)) { }
        var expectedHash = NormalizeHash(request.ExpectedHash);
        ValidateFinalization(request.Finalization);
        var finalization = request.Finalization.GenerationJson is null ? request.Finalization :
            request.Finalization with { GenerationJson = PublicationCoordinator.NormalizeGeneration(request.Finalization.GenerationJson) };
        var content = request.Content.ToArray();
        var result = request.ReplayResult?.ToArray();
        var hash = Hash(content);
        var now = Timestamp();
        var plan = new DurablePlan(1, finalization, request.DocumentReferences.ToArray(), result is null ? null : Hash(result), DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture));

        if (JsonSerializer.SerializeToUtf8Bytes(plan).Length > 64 * 1024) throw new ArgumentException("Mutation finalization metadata exceeds 64 KiB.", nameof(request));

        return await _state.QueueWriteAsync(async (connection, transaction, ct) =>
        {
            var existing = await LoadAsync(connection, transaction, request.MutationId, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                var saved = ParsePlan(existing);
                if (existing.Kind != request.Kind || existing.OperationId != request.OperationId || existing.Destination != destination ||
                    existing.ExpectedHash != expectedHash || existing.PayloadHash != hash || saved.Finalization != plan.Finalization ||
                    !saved.DocumentReferences.SequenceEqual(plan.DocumentReferences, StringComparer.Ordinal) ||
                    saved.ReplayResultHash != plan.ReplayResultHash)
                    throw new MutationReuseException(request.MutationId);
                return Record(existing);
            }
            var reserved = await connection.ExecuteScalarAsync<long>(Command(
                "SELECT count(*) FROM mutations WHERE destination=@destination AND status IN ('prepared','applied');",
                new { destination }, transaction, ct)).ConfigureAwait(false);
            if (reserved != 0) throw new InvalidOperationException("Mutation destination is already reserved.");
            if (plan.Finalization.DocumentId is { } documentId)
            {
                // The observed revision and the document reservation are one decision: publication cannot advance
                // the revision while this mutation is active, so apply and finalization never act on a stale plan.
                if (!await RevisionIsCurrentAsync(connection, transaction, plan.Finalization, ct).ConfigureAwait(false))
                    throw new MutationRevisionException(documentId);
                var holder = await connection.ExecuteScalarAsync<string?>(Command("SELECT value FROM meta WHERE key=@key;",
                    new { key = DocumentReservationKey(documentId) }, transaction, ct)).ConfigureAwait(false);
                if (holder is not null) throw new InvalidOperationException("Mutation document already has an active mutation.");
                await connection.ExecuteAsync(Command("INSERT INTO meta(key,value) VALUES(@key,@id);",
                    new { key = DocumentReservationKey(documentId), id = request.MutationId }, transaction, ct)).ConfigureAwait(false);
            }
            if (plan.Finalization.DocumentId is { } fenceId)
            {
                var expectedGeneration = await connection.ExecuteScalarAsync<string?>(Command("SELECT generation_json FROM publication_fences WHERE document_id=@id;", new { id = fenceId }, transaction, ct)).ConfigureAwait(false);
                plan = plan with { ExpectedGenerationJson = expectedGeneration is null ? null : PublicationCoordinator.NormalizeGeneration(expectedGeneration), HasExpectedGeneration = true };
            }
            var planBytes = JsonSerializer.SerializeToUtf8Bytes(plan);
            if (planBytes.Length > 64 * 1024) throw new ArgumentException("Mutation finalization metadata exceeds 64 KiB.", nameof(request));
            var contentId = PayloadId(request.MutationId, "content");
            var planId = PayloadId(request.MutationId, "plan");
            var resultId = result is null ? null : PayloadId(request.MutationId, "result");
            await InsertPayloadAsync(connection, transaction, contentId, content, plan.DocumentReferences, request.MutationId, now, ct).ConfigureAwait(false);
            await InsertPayloadAsync(connection, transaction, planId, planBytes, plan.DocumentReferences, request.MutationId, now, ct).ConfigureAwait(false);
            if (result is not null)
                await InsertPayloadAsync(connection, transaction, resultId!, result, plan.DocumentReferences, request.MutationId, now, ct).ConfigureAwait(false);
            await connection.ExecuteAsync(Command("""
                INSERT INTO mutations(id,kind,operation_id,destination,expected_hash,payload_id,revision_before,revision_after,status,result_payload_id,created_at,updated_at)
                VALUES(@id,@kind,@operation,@destination,@expectedHash,@contentId,@before,@after,'prepared',@resultId,@now,@now);
                """, new
                {
                    id = request.MutationId, kind = request.Kind, operation = request.OperationId, destination,
                    expectedHash, contentId, before = plan.Finalization.RevisionBefore, after = plan.Finalization.RevisionAfter, resultId, now
                }, transaction, ct)).ConfigureAwait(false);
            return Record((await LoadAsync(connection, transaction, request.MutationId, ct).ConfigureAwait(false))!);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<MutationRecord> ApplyAsync(string mutationId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ApplyLockedAsync(mutationId, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async ValueTask<MutationRecord> ApplyLockedAsync(string mutationId, CancellationToken cancellationToken)
    {
        var entry = await ReadAsync(mutationId, cancellationToken).ConfigureAwait(false);
        if (entry.Status != "prepared") return Record(entry);
        var plan = ParsePlan(entry);
        // Lock order is _gate, then the document lock. It is held across every filesystem effect.
        using var documentLock = await AcquireDocumentLockAsync(plan, cancellationToken).ConfigureAwait(false);
        using var file = _files.Open(entry.Destination, entry.Id);
        var conflictName = file.ConflictName(plan.ConflictTimestamp, entry.Id);
        // A row prepared before document reservations existed may observe a superseded revision; never write it.
        if (!await PlanIsCurrentAsync(plan, cancellationToken).ConfigureAwait(false))
            return await MarkConflictAsync(entry, plan, file, await file.ObserveDestinationAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        if (entry.ResultingHash is not null && await file.HashDestinationAsync(cancellationToken).ConfigureAwait(false) == entry.PayloadHash)
        {
            if (!await file.ReconcileRenameAsync(entry.ExpectedHash, entry.PayloadHash, conflictName + ".external", cancellationToken).ConfigureAwait(false))
                return await MarkConflictAsync(entry, plan, file, await file.ObserveDestinationAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            return await MarkAppliedAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        var currentHash = await file.HashDestinationAsync(cancellationToken).ConfigureAwait(false);
        if ((await file.ObserveDestinationAsync(cancellationToken).ConfigureAwait(false)).Kind == ManagedMutationFiles.DestinationLease.EntryKind.Nonregular || currentHash != entry.ExpectedHash)
            return await MarkConflictAsync(entry, plan, file, await file.ObserveDestinationAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        await file.WriteTempAsync(entry.Content, cancellationToken).ConfigureAwait(false);
        await _crashPoints.HitAsync(MutationCrashPointNames.AfterTempFlush, cancellationToken).ConfigureAwait(false);
        // Persist apply intent before rename. A prepared row with this hash can reconcile a rename-before-record crash.
        await _state.QueueWriteAsync(async (connection, transaction, ct) =>
            await connection.ExecuteAsync(Command("UPDATE mutations SET resulting_hash=@hash,updated_at=@now WHERE id=@id AND status='prepared';",
                new { id = entry.Id, hash = entry.PayloadHash, now = Timestamp() }, transaction, ct)).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        if (!await file.CommitAsync(entry.ExpectedHash, entry.PayloadHash, conflictName + ".external", _crashPoints.HitAsync, cancellationToken).ConfigureAwait(false))
            return await MarkConflictAsync(entry, plan, file, await file.ObserveDestinationAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        await _crashPoints.HitAsync(MutationCrashPointNames.AfterRename, cancellationToken).ConfigureAwait(false);
        var applied = await MarkAppliedAsync(entry, cancellationToken).ConfigureAwait(false);
        await _crashPoints.HitAsync(CrashPointNames.AfterApply, cancellationToken).ConfigureAwait(false);
        return applied;
    }

    private async ValueTask<MutationRecord> MarkAppliedAsync(DbMutation entry, CancellationToken cancellationToken) =>
        await _state.QueueWriteAsync(async (connection, transaction, ct) =>
        {
            await connection.ExecuteAsync(Command("UPDATE mutations SET status='applied',resulting_hash=@hash,updated_at=@now WHERE id=@id AND status='prepared';",
                new { id = entry.Id, hash = entry.PayloadHash, now = Timestamp() }, transaction, ct)).ConfigureAwait(false);
            return Record((await LoadAsync(connection, transaction, entry.Id, ct).ConfigureAwait(false))!);
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<MutationRecord> FinalizeAsync(string mutationId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await FinalizeLockedAsync(mutationId, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async ValueTask<MutationRecord> FinalizeLockedAsync(string mutationId, CancellationToken cancellationToken)
    {
        var entry = await ReadAsync(mutationId, cancellationToken).ConfigureAwait(false);
        if (entry.Status is "finalized" or "failed" or "conflict") return Record(entry);
        if (entry.Status != "applied") throw new InvalidOperationException("A mutation must be applied before finalization.");
        var durable = ParsePlan(entry);
        using var documentLock = await AcquireDocumentLockAsync(durable, cancellationToken).ConfigureAwait(false);
        using var file = _files.Open(entry.Destination, entry.Id);
        var currentHash = await file.HashDestinationAsync(cancellationToken).ConfigureAwait(false);
        if (currentHash != entry.ResultingHash)
            return await MarkConflictAsync(entry, durable, file, await file.ObserveDestinationAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        await _crashPoints.HitAsync(CrashPointNames.BeforeFinalize, cancellationToken).ConfigureAwait(false);
        var finalized = await _state.QueueWriteAsync<MutationRecord?>(async (connection, transaction, ct) =>
        {
            var fresh = (await LoadAsync(connection, transaction, mutationId, ct).ConfigureAwait(false))!;
            if (fresh.Status != "applied") return Record(fresh);
            var plan = ParsePlan(fresh);
            // Only rows applied before document reservations existed can reach this; resolve them as conflicts below.
            if (!await PlanIsCurrentAsync(connection, transaction, plan, ct).ConfigureAwait(false)) return null;
            var now = Timestamp();
            await ApplyFinalizationAsync(connection, transaction, fresh, plan.HasExpectedGeneration ? plan.Finalization : plan.Finalization with { GenerationJson = null }, now, ct).ConfigureAwait(false);
            await connection.ExecuteAsync(Command("UPDATE mutations SET status='finalized',updated_at=@now WHERE id=@id AND status='applied';",
                new { id = mutationId, now }, transaction, ct)).ConfigureAwait(false);
            await ReleaseDocumentReservationAsync(connection, transaction, plan, mutationId, ct).ConfigureAwait(false);
            return Record((await LoadAsync(connection, transaction, mutationId, ct).ConfigureAwait(false))!);
        }, cancellationToken).ConfigureAwait(false);
        return finalized ?? await MarkConflictAsync(entry, durable, file, await file.ObserveDestinationAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<MutationRecord> ExecuteAsync(MutationWriteRequest request, CancellationToken cancellationToken = default)
    {
        var entry = await PrepareAsync(request, cancellationToken).ConfigureAwait(false);
        if (entry.Status == "prepared") entry = await ApplyAsync(entry.Id, cancellationToken).ConfigureAwait(false);
        return entry.Status == "applied" ? await FinalizeAsync(entry.Id, cancellationToken).ConfigureAwait(false) : entry;
    }

    public async ValueTask<MutationRecoveryReport> RecoverAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var finalized = 0;
            var discarded = 0;
            var conflicts = 0;
            var reconciled = 0;
            while (true)
            {
                string[] ids;
                await using (var read = await _state.OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false))
                    ids = (await read.Connection.QueryAsync<string>(Command("SELECT id FROM mutations WHERE status IN ('prepared','applied') ORDER BY created_at,id LIMIT 128;", null, null, cancellationToken)).ConfigureAwait(false)).ToArray();
                if (ids.Length == 0) break;
                foreach (var id in ids)
                {
                    var entry = await ReadAsync(id, cancellationToken).ConfigureAwait(false);
                    if (entry.Status == "prepared")
                    {
                        switch (await RecoverPreparedAsync(entry, cancellationToken).ConfigureAwait(false))
                        {
                            case PreparedRecovery.Conflict:
                                conflicts++;
                                continue;
                            case PreparedRecovery.Discarded:
                                discarded++;
                                continue;
                            default:
                                reconciled++;
                                break;
                        }
                    }
                    // The prepared row's document lock is released before finalization reacquires it.
                    var result = await FinalizeLockedAsync(id, cancellationToken).ConfigureAwait(false);
                    if (result.Status == "finalized") finalized++;
                    if (result.Status == "conflict") conflicts++;
                }
            }
            return new MutationRecoveryReport(finalized, discarded, conflicts, reconciled);
        }
        finally { _gate.Release(); }
    }

    private async ValueTask<PreparedRecovery> RecoverPreparedAsync(DbMutation entry, CancellationToken cancellationToken)
    {
        var plan = ParsePlan(entry);
        using var documentLock = await AcquireDocumentLockAsync(plan, cancellationToken).ConfigureAwait(false);
        using var file = _files.Open(entry.Destination, entry.Id);
        var currentHash = await file.HashDestinationAsync(cancellationToken).ConfigureAwait(false);
        if (!await PlanIsCurrentAsync(plan, cancellationToken).ConfigureAwait(false))
        {
            await MarkConflictAsync(entry, plan, file, await file.ObserveDestinationAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            return PreparedRecovery.Conflict;
        }
        if (entry.ResultingHash is not null && currentHash == entry.ResultingHash)
        {
            if (!await file.ReconcileRenameAsync(entry.ExpectedHash, entry.PayloadHash, file.ConflictName(plan.ConflictTimestamp, entry.Id) + ".external", cancellationToken).ConfigureAwait(false))
            {
                await MarkConflictAsync(entry, plan, file, await file.ObserveDestinationAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                return PreparedRecovery.Conflict;
            }
            await MarkAppliedAsync(entry, cancellationToken).ConfigureAwait(false);
            return PreparedRecovery.Reconciled;
        }
        if (entry.ResultingHash is not null && currentHash != entry.ExpectedHash)
        {
            await MarkConflictAsync(entry, plan, file, await file.ObserveDestinationAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            return PreparedRecovery.Conflict;
        }
        file.DeleteTemp();
        await _state.QueueWriteAsync(async (connection, transaction, ct) =>
        {
            var changed = await connection.ExecuteAsync(Command("UPDATE mutations SET status='failed',updated_at=@now WHERE id=@id AND status='prepared';",
                new { id = entry.Id, now = Timestamp() }, transaction, ct)).ConfigureAwait(false);
            await ReleaseDocumentReservationAsync(connection, transaction, plan, entry.Id, ct).ConfigureAwait(false);
            return changed;
        }, cancellationToken).ConfigureAwait(false);
        return PreparedRecovery.Discarded;
    }

    /// <summary>Records a conflict. The caller holds the plan's document lock; the lock is not reentrant.</summary>
    private async ValueTask<MutationRecord> MarkConflictAsync(DbMutation entry, DurablePlan plan, ManagedMutationFiles.DestinationLease file, ManagedMutationFiles.DestinationLease.Observation external, CancellationToken cancellationToken)
    {
        if (entry.ResultingHash is not null)
        {
            await file.ReconcileRenameAsync(entry.ExpectedHash, entry.PayloadHash, file.ConflictName(plan.ConflictTimestamp, entry.Id) + ".external", cancellationToken).ConfigureAwait(false);
            external = await file.ObserveDestinationAsync(cancellationToken).ConfigureAwait(false);
        }
        var conflictPath = await file.PreserveConflictAsync(file.ConflictName(plan.ConflictTimestamp, entry.Id), entry.Content, cancellationToken).ConfigureAwait(false);
        var externalHash = external.Hash;
        var resultBytes = JsonSerializer.SerializeToUtf8Bytes(new ConflictResult(conflictPath, externalHash, external.Kind.ToString()));
        return await _state.QueueWriteAsync(async (connection, transaction, ct) =>
        {
            var fresh = (await LoadAsync(connection, transaction, entry.Id, ct).ConfigureAwait(false))!;
            if (fresh.Status is "conflict" or "finalized" or "failed") return Record(fresh);
            var now = Timestamp();
            var resultId = PayloadId(entry.Id, "conflict");
            await InsertPayloadAsync(connection, transaction, resultId, resultBytes, plan.DocumentReferences, entry.Id, now, ct).ConfigureAwait(false);
            if (plan.Finalization.PendingOperationId is not null)
                await connection.ExecuteAsync(Command("UPDATE pending_operations SET status='target_changed',resolved_at=@now WHERE id=@id AND status='pending';",
                    new { id = plan.Finalization.PendingOperationId, now }, transaction, ct)).ConfigureAwait(false);
            if (plan.Finalization.DocumentId is not null && externalHash is not null)
            {
                var document = await connection.QuerySingleOrDefaultAsync<DbDocument>(Command("SELECT revision AS Revision,content_hash AS ContentHash FROM documents WHERE id=@id;",
                    new { id = plan.Finalization.DocumentId }, transaction, ct)).ConfigureAwait(false);
                // A superseded plan (rows written before document reservations) records the on-disk bytes on top of
                // the newer revision, keeping its metadata, and only when the authoritative hash actually differs.
                var planWasCurrent = plan.HasExpectedGeneration && await PlanIsCurrentAsync(connection, transaction, plan, ct).ConfigureAwait(false);
                if (document is not null && ((planWasCurrent && document.Revision == plan.Finalization.RevisionBefore) || document.ContentHash != externalHash))
                {
                    var count = await connection.ExecuteAsync(Command("UPDATE documents SET content_hash=@externalHash,revision=revision+1,publish_state='publishing',updated_at=@now WHERE id=@id AND revision=@current;",
                        new { id = plan.Finalization.DocumentId, current = document.Revision, externalHash, now }, transaction, ct)).ConfigureAwait(false);
                    if (count != 0)
                    {
                        await connection.ExecuteAsync(Command("INSERT INTO revisions(document_id,revision,cause,content_hash,created_at) VALUES(@id,@revision,'external_edit',@externalHash,@now);",
                            new { id = plan.Finalization.DocumentId, revision = document.Revision + 1, externalHash, now }, transaction, ct)).ConfigureAwait(false);
                        // A superseded plan's generation is older than the current fence; never roll that back.
                        var fence = planWasCurrent ? plan.Finalization : plan.Finalization with { GenerationJson = null };
                        await EnsurePublicationFenceAsync(connection, transaction, fence, ct).ConfigureAwait(false);
                    }
                }
            }
            await connection.ExecuteAsync(Command("UPDATE mutations SET status='conflict',result_payload_id=@resultId,updated_at=@now WHERE id=@id AND status IN ('prepared','applied');",
                new { id = entry.Id, resultId, now }, transaction, ct)).ConfigureAwait(false);
            await ReleaseDocumentReservationAsync(connection, transaction, plan, entry.Id, ct).ConfigureAwait(false);
            return Record((await LoadAsync(connection, transaction, entry.Id, ct).ConfigureAwait(false))!);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The durable per-document fence shared with publication; its value is the active mutation id.</summary>
    internal static string DocumentReservationKey(string documentId) => "mutation_document_reservation:" + documentId;

    private static async ValueTask ReleaseDocumentReservationAsync(DbConnection connection, DbTransaction transaction, DurablePlan plan, string mutationId, CancellationToken ct)
    {
        if (plan.Finalization.DocumentId is null) return;
        await connection.ExecuteAsync(Command("DELETE FROM meta WHERE key=@key AND value=@id;",
            new { key = DocumentReservationKey(plan.Finalization.DocumentId), id = mutationId }, transaction, ct)).ConfigureAwait(false);
    }

    private async ValueTask<IDisposable?> AcquireDocumentLockAsync(DurablePlan plan, CancellationToken cancellationToken) =>
        plan.Finalization.DocumentId is null ? null :
            await PublicationCoordinator.AcquireDocumentLockAsync(_state, plan.Finalization.DocumentId, cancellationToken).ConfigureAwait(false);

    private async ValueTask<bool> PlanIsCurrentAsync(DurablePlan plan, CancellationToken cancellationToken)
    {
        if (plan.Finalization.DocumentId is null) return true;
        await using var read = await _state.OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await PlanIsCurrentAsync(read.Connection, null, plan, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> PlanIsCurrentAsync(DbConnection connection, DbTransaction? transaction, DurablePlan plan, CancellationToken ct)
    {
        if (!await RevisionIsCurrentAsync(connection, transaction, plan.Finalization, ct).ConfigureAwait(false)) return false;
        if (!plan.HasExpectedGeneration || plan.Finalization.DocumentId is null) return true;
        var current = await connection.ExecuteScalarAsync<string?>(Command("SELECT generation_json FROM publication_fences WHERE document_id=@id;", new { id = plan.Finalization.DocumentId }, transaction, ct)).ConfigureAwait(false);
        return (current is null ? null : PublicationCoordinator.NormalizeGeneration(current)) ==
            (plan.ExpectedGenerationJson is null ? null : PublicationCoordinator.NormalizeGeneration(plan.ExpectedGenerationJson));
    }

    private static async ValueTask<bool> RevisionIsCurrentAsync(DbConnection connection, DbTransaction? transaction, MutationFinalization finalize, CancellationToken ct) =>
        finalize.DocumentId is null || await connection.ExecuteScalarAsync<long?>(Command("SELECT revision FROM documents WHERE id=@id;",
            new { id = finalize.DocumentId }, transaction, ct)).ConfigureAwait(false) == finalize.RevisionBefore;

    private static async ValueTask ApplyFinalizationAsync(DbConnection connection, DbTransaction transaction, DbMutation entry, MutationFinalization finalize, string now, CancellationToken ct)
    {
        if (finalize.DocumentId is not null)
        {
            var count = await connection.ExecuteAsync(Command("""
                UPDATE documents SET revision=@after,content_hash=@hash,size_bytes=@size,publish_state='publishing',updated_at=@now
                WHERE id=@id AND revision=@before;
                """, new { id = finalize.DocumentId, before = finalize.RevisionBefore, after = finalize.RevisionAfter, hash = entry.ResultingHash, size = entry.Content.Length, now }, transaction, ct)).ConfigureAwait(false);
            if (count != 1) throw new MutationRevisionException(finalize.DocumentId);
            await connection.ExecuteAsync(Command("INSERT INTO revisions(document_id,revision,cause,content_hash,created_at) VALUES(@id,@revision,@cause,@hash,@now);",
                new { id = finalize.DocumentId, revision = finalize.RevisionAfter, cause = finalize.RevisionCause, hash = entry.ResultingHash, now }, transaction, ct)).ConfigureAwait(false);
            await EnsurePublicationFenceAsync(connection, transaction, finalize, ct).ConfigureAwait(false);
        }
        if (finalize.PendingOperationId is not null)
        {
            var count = await connection.ExecuteAsync(Command("UPDATE pending_operations SET status='executed',resolved_at=@now WHERE id=@id AND status='pending';",
                new { id = finalize.PendingOperationId, now }, transaction, ct)).ConfigureAwait(false);
            if (count != 1) throw new InvalidOperationException("Proposal is missing or already resolved.");
        }
        if (finalize.ToolExecutionId is not null)
        {
            var count = await connection.ExecuteAsync(Command("UPDATE tool_executions SET status='committed',result_payload_id=@resultId,committed_at=@now WHERE id=@id AND status IN ('running','pending_approval');",
                new { id = finalize.ToolExecutionId, resultId = entry.ResultPayloadId, now }, transaction, ct)).ConfigureAwait(false);
            if (count != 1) throw new InvalidOperationException("Tool execution is missing or already committed.");
            if (entry.ResultPayloadId is not null)
                await InsertReferenceAsync(connection, transaction, entry.ResultPayloadId, "tool_execution", finalize.ToolExecutionId, ct).ConfigureAwait(false);
        }
        if (finalize.Idempotency is { } replay)
        {
            var existing = await connection.QuerySingleOrDefaultAsync<DbIdempotency>(Command("SELECT payload_hash AS PayloadHash,response_json AS ResponseJson FROM idempotency WHERE key=@Key AND credential_id=@CredentialId;", replay, transaction, ct)).ConfigureAwait(false);
            if (existing is not null && (existing.PayloadHash != replay.PayloadHash || existing.ResponseJson != replay.ResponseJson))
                throw new InvalidOperationException("Idempotency key was reused with different inputs.");
            if (existing is null)
                await connection.ExecuteAsync(Command("INSERT INTO idempotency(key,credential_id,payload_hash,response_json,created_at) VALUES(@Key,@CredentialId,@PayloadHash,@ResponseJson,@now);",
                    new { replay.Key, replay.CredentialId, replay.PayloadHash, replay.ResponseJson, now }, transaction, ct)).ConfigureAwait(false);
            if (entry.ResultPayloadId is not null)
                await InsertReferenceAsync(connection, transaction, entry.ResultPayloadId, "idempotency", JsonSerializer.Serialize(new[] { replay.Key, replay.CredentialId }), ct).ConfigureAwait(false);
        }
    }

    private static async ValueTask EnsurePublicationFenceAsync(DbConnection connection, DbTransaction transaction, MutationFinalization finalize, CancellationToken ct)
    {
        var generation = finalize.GenerationJson ?? await connection.ExecuteScalarAsync<string?>(Command("SELECT value FROM meta WHERE key='generations_json';", null, transaction, ct)).ConfigureAwait(false) ?? "{}";
        generation = PublicationCoordinator.NormalizeGeneration(generation);
        var sql = finalize.GenerationJson is null
            ? "INSERT INTO publication_fences(document_id,generation_json) VALUES(@id,@generation) ON CONFLICT(document_id) DO NOTHING;"
            : "INSERT INTO publication_fences(document_id,generation_json) VALUES(@id,@generation) ON CONFLICT(document_id) DO UPDATE SET generation_json=excluded.generation_json;";
        await connection.ExecuteAsync(Command(sql, new { id = finalize.DocumentId, generation }, transaction, ct)).ConfigureAwait(false);
    }

    private async ValueTask<DbMutation> ReadAsync(string mutationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutationId);
        await using var read = await _state.OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadAsync(read.Connection, null, mutationId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Mutation '{mutationId}' does not exist.");
    }

    private static Task<DbMutation?> LoadAsync(DbConnection connection, DbTransaction? transaction, string id, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<DbMutation>(Command("""
            SELECT m.id AS Id,m.kind AS Kind,m.operation_id AS OperationId,m.destination AS Destination,
              m.expected_hash AS ExpectedHash,m.resulting_hash AS ResultingHash,m.status AS Status,
              m.revision_before AS RevisionBefore,m.revision_after AS RevisionAfter,m.result_payload_id AS ResultPayloadId,
              p.bytes AS Content,p.sha256 AS PayloadHash,f.bytes AS PlanBytes,r.bytes AS ResultBytes
            FROM mutations m JOIN payloads p ON p.id=m.payload_id JOIN payloads f ON f.id=@planId
            LEFT JOIN payloads r ON r.id=m.result_payload_id WHERE m.id=@id;
            """, new { id, planId = PayloadId(id, "plan") }, transaction, ct));

    private static async ValueTask InsertPayloadAsync(DbConnection connection, DbTransaction transaction, string id, byte[] bytes, IReadOnlyList<string> refs, string mutationId, string now, CancellationToken ct)
    {
        await connection.ExecuteAsync(Command("INSERT INTO payloads(id,sha256,bytes,document_refs_json,created_at) VALUES(@id,@sha256,@bytes,@refs,@now);",
            new { id, sha256 = Hash(bytes), bytes, refs = JsonSerializer.Serialize(refs), now }, transaction, ct)).ConfigureAwait(false);
        await InsertReferenceAsync(connection, transaction, id, "mutation", mutationId, ct).ConfigureAwait(false);
    }

    private static async ValueTask InsertReferenceAsync(DbConnection connection, DbTransaction transaction, string payload, string kind, string referrer, CancellationToken ct) =>
        await connection.ExecuteAsync(Command("INSERT OR IGNORE INTO payload_refs(payload_id,referrer_kind,referrer_id) VALUES(@payload,@kind,@referrer);",
            new { payload, kind, referrer }, transaction, ct)).ConfigureAwait(false);

    private static CommandDefinition Command(string sql, object? parameters, DbTransaction? transaction, CancellationToken ct) => new(sql, parameters, transaction, cancellationToken: ct);
    private static string Timestamp() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string PayloadId(string mutationId, string suffix) => $"sb_mutation_{Hash(Encoding.UTF8.GetBytes(mutationId))}_{suffix}";
    private static DurablePlan ParsePlan(DbMutation entry)
    {
        var plan = JsonSerializer.Deserialize<DurablePlan>(entry.PlanBytes) ?? throw new InvalidDataException("Mutation finalization plan is missing.");
        if (plan.Version != 1 || Hash(entry.Content) != entry.PayloadHash) throw new InvalidDataException("Mutation payload or finalization plan is invalid.");
        return plan;
    }
    private static MutationRecord Record(DbMutation entry) => new(entry.Id, entry.Kind, entry.OperationId, entry.Destination, entry.ExpectedHash,
        entry.ResultingHash, entry.Status, entry.RevisionBefore, entry.RevisionAfter, entry.ResultPayloadId,
        entry.Status == "conflict" && entry.ResultBytes is not null ? JsonSerializer.Deserialize<ConflictResult>(entry.ResultBytes)?.ConflictPath : null);

    private static string? NormalizeHash(string? hash)
    {
        if (hash is null) return null;
        if (hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c))) throw new ArgumentException("Expected hash must be SHA-256 hex.", nameof(hash));
        return hash.ToLowerInvariant();
    }

    private static void ValidateFinalization(MutationFinalization finalize)
    {
        ArgumentNullException.ThrowIfNull(finalize);
        if (finalize.DocumentId is null)
        {
            if (finalize.RevisionBefore is not null || finalize.RevisionAfter is not null) throw new ArgumentException("Revisions require a document id.", nameof(finalize));
        }
        else if (finalize.RevisionBefore is null || finalize.RevisionBefore < 1 || finalize.RevisionBefore == long.MaxValue || finalize.RevisionAfter != finalize.RevisionBefore + 1)
            throw new ArgumentException("Document mutation must advance the observed revision by one.", nameof(finalize));
        ArgumentException.ThrowIfNullOrWhiteSpace(finalize.RevisionCause);
        if (finalize.GenerationJson is not null)
        {
            using var generation = JsonDocument.Parse(finalize.GenerationJson);
            if (generation.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Generation must be a JSON object.", nameof(finalize));
        }
        if (finalize.Idempotency is { } replay)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(replay.Key);
            ArgumentException.ThrowIfNullOrWhiteSpace(replay.CredentialId);
            NormalizeHash(replay.PayloadHash);
            using var json = JsonDocument.Parse(replay.ResponseJson);
        }
    }

    public void Dispose() => _gate.Dispose();

    private sealed record DurablePlan(int Version, MutationFinalization Finalization, string[] DocumentReferences, string? ReplayResultHash, string ConflictTimestamp, string? ExpectedGenerationJson = null, bool HasExpectedGeneration = false);
    private sealed record ConflictResult(string ConflictPath, string? ExternalHash, string? ExternalKind = null);
    private enum PreparedRecovery { Reconciled, Conflict, Discarded }
    private sealed class DbDocument
    {
        public long Revision { get; init; }
        public string? ContentHash { get; init; }
    }
    private sealed class DbIdempotency
    {
        public string PayloadHash { get; init; } = "";
        public string ResponseJson { get; init; } = "";
    }
    private sealed class DbMutation
    {
        public string Id { get; init; } = "";
        public string Kind { get; init; } = "";
        public string OperationId { get; init; } = "";
        public string Destination { get; init; } = "";
        public string? ExpectedHash { get; init; }
        public string? ResultingHash { get; init; }
        public string Status { get; init; } = "";
        public long? RevisionBefore { get; init; }
        public long? RevisionAfter { get; init; }
        public string? ResultPayloadId { get; init; }
        public byte[] Content { get; init; } = [];
        public string PayloadHash { get; init; } = "";
        public byte[] PlanBytes { get; init; } = [];
        public byte[]? ResultBytes { get; init; }
    }
}
