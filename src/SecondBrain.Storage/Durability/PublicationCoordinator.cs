using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Storage;
using SecondBrain.Core.Ingestion;
using System.Security.Cryptography;

namespace SecondBrain.Storage.Durability;

/// <summary>M0 metadata publication and restart recovery following spec §8's three durable steps.</summary>
public sealed class PublicationCoordinator : IPublicationCoordinator
{
    private static readonly ConditionalWeakTable<IStateStore, DocumentLocks> SharedLocks = new();
    private static readonly ConditionalWeakTable<IStateStore, SemaphoreSlim> AdmissionGates = new();
    private readonly IStateStore state;
    private readonly IIndexStore index;
    private readonly ICrashPoints crashPoints;
    private readonly DocumentLocks locks;
    private readonly SecondBrain.Core.Configuration.GlobalLimitsOptions limits;

    /// <summary>Creates a coordinator over migrated stores. Register one coordinator per data root.</summary>
    public PublicationCoordinator(IStateStore state, IIndexStore index, ICrashPoints? crashPoints = null, Microsoft.Extensions.Options.IOptions<SecondBrain.Core.Configuration.SecondBrainOptions>? configuration = null)
    {
        this.state = state;
        this.index = index;
        limits = configuration?.Value.Limits.Global ?? new SecondBrain.Core.Configuration.GlobalLimitsOptions();
        this.crashPoints = crashPoints ?? new NoOpCrashPoints();
        locks = SharedLocks.GetValue(state, static _ => new DocumentLocks());
    }

    // Journal finalization uses the same lock before changing a document's revision. This
    // prevents a filesystem mutation from invalidating an in-flight index publication.
    internal static ValueTask<IDisposable> AcquireDocumentLockAsync(IStateStore state, string documentId, CancellationToken cancellationToken = default) =>
        SharedLocks.GetValue(state, static _ => new DocumentLocks()).AcquireAsync(documentId, cancellationToken);

    internal static async ValueTask<IDisposable> AcquirePublicationGateAsync(IStateStore state, CancellationToken cancellationToken = default)
    {
        var semaphore = AdmissionGates.GetValue(state, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        return new PublicationGateLease(semaphore);
    }
    private sealed class PublicationGateLease(SemaphoreSlim semaphore) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) semaphore.Release(); }
    }

    /// <inheritdoc />
    public async ValueTask<PublicationOutcome> PublishAsync(DocumentPublication publication, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        ArgumentException.ThrowIfNullOrWhiteSpace(publication.DocumentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(publication.SourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(publication.Cause);
        ArgumentOutOfRangeException.ThrowIfNegative(publication.ExpectedRevision);
        ArgumentOutOfRangeException.ThrowIfLessThan(publication.SchemaVersion, 1);
        var generation = NormalizeGeneration(publication.GenerationJson);
        using var effectiveProps = JsonDocument.Parse(publication.PropsJson);
        using var originalProps = JsonDocument.Parse(publication.PropsOriginalJson);
        if (effectiveProps.RootElement.ValueKind != JsonValueKind.Object || originalProps.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Document properties must be JSON objects.", nameof(publication));
        }

        using var documentLock = await locks.AcquireAsync(publication.DocumentId, cancellationToken);
        using var publicationGate = await AcquirePublicationGateAsync(state, cancellationToken);
        var revision = checked(publication.ExpectedRevision + 1);
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var accepted = await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            if (publication.Admission is { Authority: { } actor } initiating)
                await SecondBrain.Storage.Auth.IngestionAuthorityValidator.ValidateAsync(connection, transaction, actor,
                    publication.Cause == "purge" ? SecondBrain.Core.Authorization.Scope.Admin : SecondBrain.Core.Authorization.Scope.Write,
                    DateTimeOffset.UtcNow, sessionOnly: publication.Cause is "purge" or "restore", stepUpWindow: publication.Cause == "purge" ? TimeSpan.FromMinutes(10) : null, cancellationToken: token);
            else if (publication.Admission is { TrustedSourceId: { } source })
            {
                await using var trusted = Command(connection, transaction, "SELECT 1 FROM sources WHERE id=$id AND kind='folder' AND status='active'", ("$id", source));
                if (await trusted.ExecuteScalarAsync(token) is null || source != publication.SourceId) return false;
            }
            if (publication.Admission is { } capacity)
                await EnsureCapacityAsync(connection, transaction, publication.DocumentId, publication.SourceId, capacity, token);
                        // A prepared or applied journal mutation owns the next revision until it is finalized or conflicted.
            await using (var reservation = Command(connection, transaction,
                "SELECT 1 FROM meta WHERE key = $key", ("$key", MutationJournal.DocumentReservationKey(publication.DocumentId))))
            {
                if (await reservation.ExecuteScalarAsync(token) is not null)
                {
                    return false;
                }
            }

            await using (var lookup = Command(connection, transaction,
                "SELECT revision FROM documents WHERE id = $id", ("$id", publication.DocumentId)))
            {
                var current = await lookup.ExecuteScalarAsync(token);
                if (current is null ? publication.ExpectedRevision != 0 : Convert.ToInt64(current, CultureInfo.InvariantCulture) != publication.ExpectedRevision)
                {
                    return false;
                }
            }

            await using (var write = Command(connection, transaction,
                """
                INSERT INTO documents (
                  id, source_id, type, type_origin, type_confidence, schema_version, props_json,
                  props_original_json, title, mime, source_url, provenance, claimed_client,
                  occurred_at, occurred_offset, occurred_basis, occurred_precision, natural_key,
                  content_hash, size_bytes, revision, publish_state, status, error, created_at,
                  updated_at, deleted_at)
                VALUES ($id, $source, $type, $origin, $confidence, $schema, $props, $original,
                  $title, $mime, $url, $provenance, $client, $occurred, $offset, $basis,
                  $precision, $natural, $hash, $size, $revision, 'publishing', $status, $error,
                  $created, $now, $deleted)
                ON CONFLICT(id) DO UPDATE SET
                  source_id=excluded.source_id, type=excluded.type, type_origin=excluded.type_origin,
                  type_confidence=excluded.type_confidence, schema_version=excluded.schema_version,
                  props_json=excluded.props_json, props_original_json=excluded.props_original_json,
                  title=excluded.title, mime=excluded.mime, source_url=excluded.source_url,
                  provenance=excluded.provenance, claimed_client=excluded.claimed_client,
                  occurred_at=excluded.occurred_at, occurred_offset=excluded.occurred_offset,
                  occurred_basis=excluded.occurred_basis, occurred_precision=excluded.occurred_precision,
                  natural_key=excluded.natural_key, content_hash=excluded.content_hash,
                  size_bytes=excluded.size_bytes, revision=excluded.revision, publish_state='publishing',
                  status=excluded.status, error=excluded.error, updated_at=excluded.updated_at,
                  deleted_at=CASE WHEN excluded.deleted_at IS NULL THEN NULL
                    ELSE COALESCE(documents.deleted_at, excluded.deleted_at) END
                """,
                ("$id", publication.DocumentId), ("$source", publication.SourceId), ("$type", publication.Type),
                ("$origin", publication.TypeOrigin), ("$confidence", publication.TypeConfidence), ("$schema", publication.SchemaVersion),
                ("$props", publication.PropsJson), ("$original", publication.PropsOriginalJson), ("$title", publication.Title),
                ("$mime", publication.Mime), ("$url", publication.SourceUrl), ("$provenance", publication.Provenance),
                ("$client", publication.ClaimedClient), ("$occurred", publication.OccurredAt), ("$offset", publication.OccurredOffset),
                ("$basis", publication.OccurredBasis), ("$precision", publication.OccurredPrecision), ("$natural", publication.NaturalKey),
                ("$hash", publication.ContentHash), ("$size", publication.SizeBytes), ("$revision", revision),
                ("$status", publication.Status), ("$error", publication.Error),
                ("$created", publication.CreatedAt?.ToString("O", CultureInfo.InvariantCulture) ?? now), ("$now", now),
                ("$deleted", IsRemoved(publication.Status) ? now : null)))
            {
                await write.ExecuteNonQueryAsync(token);
            }

            await using (var record = Command(connection, transaction,
                "INSERT INTO revisions(document_id, revision, cause, content_hash, created_at) VALUES ($id,$revision,$cause,$hash,$now)",
                ("$id", publication.DocumentId), ("$revision", revision), ("$cause", publication.Cause), ("$hash", publication.ContentHash), ("$now", now)))
            {
                await record.ExecuteNonQueryAsync(token);
            }

            await using (var staleJobs = Command(connection, transaction,
                "UPDATE jobs SET status='cancelled',updated_at=$now WHERE document_id=$id AND revision<$revision AND status IN ('queued','running')", ("$id", publication.DocumentId), ("$revision", revision), ("$now", now)))
                await staleJobs.ExecuteNonQueryAsync(token);
            if (publication.Admission is { } admission)
            {
                if (admission.Occurrence is not null)
                {
                    // A source occurrence's publication supersedes any recorded staging rejection of it.
                    await using var clearRejection = Command(connection, transaction, "DELETE FROM jobs WHERE document_id=$id AND kind='stage'", ("$id", publication.DocumentId));
                    await clearRejection.ExecuteNonQueryAsync(token);
                }
                await using var job = Command(connection, transaction,
                    "INSERT INTO jobs(id,kind,document_id,revision,generation_json,initiating_credential_id,credential_generation,account_epoch,status,error,payload_json,created_at,updated_at) VALUES($job,$kind,$id,$revision,$generation,$actor,$credential,$epoch,$status,$error,$payload,$now,$now)",
                    ("$job", admission.JobId), ("$kind", admission.Kind), ("$id", publication.DocumentId), ("$revision", revision), ("$generation", generation),
                    ("$actor", admission.Authority?.CredentialId), ("$credential", admission.Authority?.CredentialGeneration),
                    ("$epoch", admission.Authority?.AccountEpoch ?? 0), ("$status", admission.JobStatus), ("$error", admission.JobError),
                    ("$payload", admission.PayloadJson ?? JsonSerializer.Serialize(new { admitted_bytes = admission.AdmittedBytes })), ("$now", now));
                await job.ExecuteNonQueryAsync(token);
                if (admission.Occurrence is { } occurrence)
                {
                    await using var clearOld = Command(connection, transaction, "DELETE FROM occurrences WHERE source_id=$source AND document_id=$id AND path<>$path", ("$source", occurrence.SourceId), ("$id", publication.DocumentId), ("$path", occurrence.RelativePath)); await clearOld.ExecuteNonQueryAsync(token);
                    await using var writeOccurrence = Command(connection, transaction,
                        "INSERT INTO occurrences(document_id,source_id,path,file_mtime,size_bytes,last_seen_at) VALUES($id,$source,$path,$mtime,$length,$now) ON CONFLICT(source_id,path,member_key) DO UPDATE SET document_id=excluded.document_id,file_mtime=excluded.file_mtime,size_bytes=excluded.size_bytes,last_seen_at=excluded.last_seen_at", ("$id", publication.DocumentId), ("$source", occurrence.SourceId), ("$path", occurrence.RelativePath), ("$mtime", occurrence.ModifiedAt.ToString("O", CultureInfo.InvariantCulture)), ("$length", occurrence.Length), ("$now", now)); await writeOccurrence.ExecuteNonQueryAsync(token);
                    await using var clearAbsence = Command(connection, transaction, "UPDATE absences SET cleared_at=$now WHERE document_id=$id AND source_id=$source AND cleared_at IS NULL", ("$id", publication.DocumentId), ("$source", occurrence.SourceId), ("$now", now)); await clearAbsence.ExecuteNonQueryAsync(token);
                }
                if (!string.IsNullOrEmpty(admission.IdempotencyKey))
                {
                    await using var replay = Command(connection, transaction,
                        "INSERT INTO idempotency(key,credential_id,payload_hash,response_json,created_at) VALUES($key,$actor,$hash,$response,$now)",
                        ("$key", admission.IdempotencyKey), ("$actor", admission.Authority?.CredentialId),
                        ("$hash", admission.RequestHash), ("$response", publication.DocumentId), ("$now", now));
                    await replay.ExecuteNonQueryAsync(token);
                }
            }
            if (publication.Projection is { } projection)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(projection);
                var payloadId = Ulid.NewUlid().ToString();
                await using var body = Command(connection, transaction,
                    "INSERT INTO payloads(id,sha256,bytes,document_refs_json,created_at) VALUES($payload,$hash,$bytes,$refs,$now)",
                    ("$payload", payloadId), ("$hash", Convert.ToHexStringLower(SHA256.HashData(bytes))),
                    ("$bytes", bytes), ("$refs", JsonSerializer.Serialize(new[] { publication.DocumentId })), ("$now", now));
                await body.ExecuteNonQueryAsync(token);
                await using var pointer = Command(connection, transaction,
                    "INSERT INTO document_projections(document_id,revision,generation_json,payload_id) VALUES($id,$revision,$generation,$payload) ON CONFLICT(document_id) DO UPDATE SET revision=excluded.revision,generation_json=excluded.generation_json,payload_id=excluded.payload_id",
                    ("$id", publication.DocumentId), ("$revision", revision), ("$generation", generation), ("$payload", payloadId));
                await pointer.ExecuteNonQueryAsync(token);
                await using var reference = Command(connection, transaction,
                    "INSERT INTO payload_refs(payload_id,referrer_kind,referrer_id) VALUES($payload,'document-projection',$id)",
                    ("$payload", payloadId), ("$id", publication.DocumentId));
                await reference.ExecuteNonQueryAsync(token);
            }
            await WriteGenerationAsync(connection, transaction, publication.DocumentId, generation, token);
            return true;
        }, cancellationToken);

        publicationGate.Dispose();
        if (!accepted)
        {
            return PublicationOutcome.Superseded;
        }
        await crashPoints.HitAsync(CrashPointNames.AfterStatePublishing, cancellationToken);
        return await CompleteAsync(new PublicationFence(publication.DocumentId, revision, generation), cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask CheckAdmissionAsync(string documentId, string sourceId, PublicationAdmission admission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admission);
        await using var lease = await state.OpenReadConnectionAsync(cancellationToken);
        await EnsureCapacityAsync(lease.Connection, null, documentId, sourceId, admission, cancellationToken);
    }

    private async ValueTask EnsureCapacityAsync(DbConnection connection, DbTransaction? transaction, string documentId, string sourceId, PublicationAdmission admission, CancellationToken token)
    {
        await using var accounting = Command(connection, transaction,
            "SELECT (SELECT COUNT(*) FROM jobs WHERE status IN ('queued','running')),(SELECT COALESCE(SUM(size_bytes),0) FROM documents WHERE status NOT IN ('deleted','purged') AND id<>$id),(SELECT COUNT(*) FROM documents WHERE status NOT IN ('deleted','purged') AND id<>$id),(SELECT COUNT(*) FROM documents WHERE source_id=$source AND status NOT IN ('deleted','purged') AND id<>$id)", ("$id", documentId), ("$source", sourceId));
        await using var reader = await accounting.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        var maxBytes = (long)Math.Clamp(limits.AdmittedGb * 1_000_000_000m, 0, long.MaxValue);
        if (reader.GetInt64(0) >= limits.QueuedJobs || admission.AdmittedBytes > maxBytes - reader.GetInt64(1)
            || reader.GetInt64(2) >= limits.Files || reader.GetInt64(3) >= limits.FilesPerSource)
            throw new IngestionConflictException("capacity");
    }

    /// <inheritdoc />
    public async ValueTask<PublicationOutcome> PublishCurrentAsync(PublicationFence fence, CancellationToken cancellationToken = default)
    {
        fence = NormalizeFence(fence);
        using var documentLock = await locks.AcquireAsync(fence.DocumentId, cancellationToken);
        using var publicationGate = await AcquirePublicationGateAsync(state, cancellationToken);
        var accepted = await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            if (!await FenceMatchesAsync(connection, transaction, fence, token))
            {
                return false;
            }
            await using var mark = Command(connection, transaction, "UPDATE documents SET publish_state='publishing' WHERE id=$id", ("$id", fence.DocumentId));
            await mark.ExecuteNonQueryAsync(token);
            return true;
        }, cancellationToken);
        publicationGate.Dispose();
        if (!accepted)
        {
            return PublicationOutcome.Superseded;
        }
        await crashPoints.HitAsync(CrashPointNames.AfterStatePublishing, cancellationToken);
        return await CompleteAsync(fence, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<PublicationOutcome> ReprocessAsync(PublicationGenerationChange change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var expected = NormalizeFence(change.Expected);
        var generation = NormalizeGeneration(change.GenerationJson);
        using var documentLock = await locks.AcquireAsync(expected.DocumentId, cancellationToken);
        using var publicationGate = await AcquirePublicationGateAsync(state, cancellationToken);
        var accepted = await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await using var reservation = Command(connection, transaction, "SELECT 1 FROM meta WHERE key=$key", ("$key", MutationJournal.DocumentReservationKey(expected.DocumentId)));
            if (await reservation.ExecuteScalarAsync(token) is not null) return false;
            if (!await FenceMatchesAsync(connection, transaction, expected, token))
            {
                return false;
            }
            await WriteGenerationAsync(connection, transaction, expected.DocumentId, generation, token);
            await using var mark = Command(connection, transaction, "UPDATE documents SET publish_state='publishing' WHERE id=$id", ("$id", expected.DocumentId));
            await mark.ExecuteNonQueryAsync(token);
            return true;
        }, cancellationToken);
        publicationGate.Dispose();
        if (!accepted)
        {
            return PublicationOutcome.Superseded;
        }
        await crashPoints.HitAsync(CrashPointNames.AfterStatePublishing, cancellationToken);
        return await CompleteAsync(expected with { GenerationJson = generation }, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<int> RecoverAsync(CancellationToken cancellationToken = default)
    {
        var completed = 0;
        var afterId = string.Empty;
        while (true)
        {
            var documentIds = new List<string>(128);
            // Release each bounded read snapshot before attempting publication writes.
            await using (var lease = await state.OpenReadConnectionAsync(cancellationToken))
            await using (var lookup = Command(lease.Connection, null,
                "SELECT id FROM documents WHERE publish_state='publishing' AND id>$after ORDER BY id LIMIT 128", ("$after", afterId)))
            await using (var reader = await lookup.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    documentIds.Add(reader.GetString(0));
                }
            }
            if (documentIds.Count == 0)
            {
                return completed;
            }
            foreach (var id in documentIds)
            {
                using var documentLock = await locks.AcquireAsync(id, cancellationToken);
                var snapshot = await ReadSnapshotAsync(id, cancellationToken);
                if (snapshot is { PublishState: "publishing" } && await CompleteAsync(snapshot.Fence, cancellationToken) == PublicationOutcome.Published)
                {
                    completed++;
                }
            }
            afterId = documentIds[^1];
        }
    }

    private async ValueTask<PublicationOutcome> CompleteAsync(PublicationFence fence, CancellationToken cancellationToken)
    {
        var vectorRegistry = SecondBrain.Storage.Retrieval.VectorSnapshotRegistry.For(index);
        bool committed;
        string? projectedStatus = null;
        using (await vectorRegistry.EnterAsync(cancellationToken))
        {
        committed = await index.QueueWriteAsync(async (connection, transaction, token) =>
        {
            // This state read is deliberately INSIDE the index transaction. The document lock
            // stays held until step three, so every coordinator mutation respects this fence.
            var snapshot = await ReadSnapshotAsync(fence.DocumentId, token);
            if (snapshot is null || snapshot.Fence != fence)
            {
                return false;
            }

            if (IsRemoved(snapshot.Status))
            {
                await using var remove = Command(connection, transaction, "DELETE FROM indexed_documents WHERE document_id=$id", ("$id", fence.DocumentId));
                await remove.ExecuteNonQueryAsync(token);
            }
            else
            {
                await using var replace = Command(connection, transaction,
                    """
                    INSERT INTO indexed_documents(document_id,revision,type,title,props_json,occurred_at,occurred_precision,ends_at,status,updated_at)
                    VALUES($id,$revision,$type,$title,$props,$occurred,$precision,$ends,$status,$updated)
                    ON CONFLICT(document_id) DO UPDATE SET revision=excluded.revision,type=excluded.type,
                      title=excluded.title,props_json=excluded.props_json,occurred_at=excluded.occurred_at,
                      occurred_precision=excluded.occurred_precision,ends_at=excluded.ends_at,
                      status=excluded.status,updated_at=excluded.updated_at
                    """,
                    ("$id", fence.DocumentId), ("$revision", fence.Revision), ("$type", snapshot.Type), ("$title", snapshot.Title),
                    ("$props", snapshot.PropsJson), ("$occurred", snapshot.OccurredAt), ("$precision", snapshot.OccurredPrecision),
                    ("$ends", snapshot.EndsAt), ("$status", snapshot.Status), ("$updated", snapshot.UpdatedAt));
                await replace.ExecuteNonQueryAsync(token);
            }
            await ReplaceProjectionsAsync(connection, transaction, snapshot, token);
            await using (var status = Command(connection, transaction, "SELECT status FROM indexed_documents WHERE document_id=$id", ("$id", fence.DocumentId)))
                projectedStatus = await status.ExecuteScalarAsync(token) as string;
            // Retain a generation marker even for a tombstone, so its removal is fenced too.
            await using var generation = Command(connection, transaction,
                """
                INSERT INTO indexed_document_generations(document_id,generation_json) VALUES($id,$generation)
                ON CONFLICT(document_id) DO UPDATE SET generation_json=excluded.generation_json
                """, ("$id", fence.DocumentId), ("$generation", fence.GenerationJson));
            await generation.ExecuteNonQueryAsync(token);
            return true;
        }, cancellationToken);
        if (committed) vectorRegistry.Committed(fence.DocumentId);
        }

        if (!committed)
        {
            return PublicationOutcome.Superseded;
        }
        await crashPoints.HitAsync(CrashPointNames.AfterIndexCommit, cancellationToken);

        var finalized = await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            if (!await FenceMatchesAsync(connection, transaction, fence, token))
            {
                return false;
            }
            await using var finalize = Command(connection, transaction,
                "UPDATE documents SET indexed_revision=$revision,publish_state='published',status=CASE WHEN status IN ('indexed','indexed_partial') AND $status IN ('indexed','indexed_partial') THEN $status ELSE status END WHERE id=$id",
                ("$revision", fence.Revision), ("$id", fence.DocumentId), ("$status", projectedStatus));
            await finalize.ExecuteNonQueryAsync(token);
            if (projectedStatus == "indexed")
            {
                await using var jobs = Command(connection, transaction,
                    "UPDATE jobs SET status='completed',lease_until=NULL,updated_at=$now WHERE document_id=$id AND revision=$revision AND generation_json=$generation AND kind='embed' AND status IN ('queued','running')",
                    ("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)), ("$id", fence.DocumentId), ("$revision", fence.Revision), ("$generation", fence.GenerationJson));
                await jobs.ExecuteNonQueryAsync(token);
            }
            return true;
        }, cancellationToken);
        return finalized ? PublicationOutcome.Published : PublicationOutcome.Superseded;
    }

    private async ValueTask<Snapshot?> ReadSnapshotAsync(string documentId, CancellationToken cancellationToken)
    {
        await using var lease = await state.OpenReadConnectionAsync(cancellationToken);
        await using var lookup = Command(lease.Connection, null,
            """
            SELECT d.revision,f.generation_json,d.type,d.title,d.props_json,d.occurred_at,
              d.occurred_precision,d.status,d.updated_at,d.publish_state,d.source_id,d.mime
            FROM documents d JOIN publication_fences f ON f.document_id=d.id WHERE d.id=$id
            """, ("$id", documentId));
        await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        var props = reader.GetString(4);
        using var parsed = JsonDocument.Parse(props);
        var endsAt = parsed.RootElement.TryGetProperty("ends_at", out var ends) && ends.ValueKind == JsonValueKind.String ? ends.GetString() : null;
        return new Snapshot(new PublicationFence(documentId, reader.GetInt64(0), NormalizeGeneration(reader.GetString(1))),
            reader.GetString(2), reader.GetString(3), props, reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6), endsAt, reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11));
    }

    private async ValueTask ReplaceProjectionsAsync(DbConnection connection, DbTransaction transaction, Snapshot snapshot, CancellationToken token)
    {
        var id = snapshot.Fence.DocumentId;
        await using (var removeFts = Command(connection, transaction,
            "INSERT INTO chunks_fts(chunks_fts,rowid,text,context,title) SELECT 'delete',f.rowid,f.text,f.context,f.title FROM chunks_fts_source f JOIN chunks c ON c.rowid=f.rowid WHERE c.document_id=$id", ("$id", id)))
            await removeFts.ExecuteNonQueryAsync(token);
        await using (var removeSource = Command(connection, transaction,
            "DELETE FROM chunks_fts_source WHERE rowid IN (SELECT rowid FROM chunks WHERE document_id=$id)", ("$id", id)))
            await removeSource.ExecuteNonQueryAsync(token);
        foreach (var table in new[] { "chunks", "document_text", "effective_tags", "tag_assertions", "indexed_props", "people", "indexed_document_sources" })
        {
            await using var remove = Command(connection, transaction, "DELETE FROM " + table + " WHERE document_id=$id", ("$id", id));
            await remove.ExecuteNonQueryAsync(token);
        }
        await using (var removeLinks = Command(connection, transaction, "DELETE FROM links WHERE from_document_id=$id", ("$id", id)))
            await removeLinks.ExecuteNonQueryAsync(token);
        if (IsRemoved(snapshot.Status)) return;
        TextProjection? projection = null;
        await using (var lease = await state.OpenReadConnectionAsync(token))
        await using (var read = Command(lease.Connection, null,
            "SELECT p.bytes FROM document_projections d JOIN payloads p ON p.id=d.payload_id WHERE d.document_id=$id AND p.redacted_at IS NULL", ("$id", id)))
        {
            if (await read.ExecuteScalarAsync(token) is byte[] bytes) projection = JsonSerializer.Deserialize<TextProjection>(bytes);
        }
        // Index-in-place sources expose the external occurrence to retrieval. The durable
        // projection still points at the managed original used for downloads and replay.
        var indexedPath = projection?.Path;
        await using (var lease = await state.OpenReadConnectionAsync(token))
        await using (var occurrence = Command(lease.Connection, null,
            "SELECT json_extract(s.config_json,'$.path'),o.path FROM occurrences o JOIN sources s ON s.id=o.source_id WHERE o.document_id=$id AND o.source_id=$source AND o.member_key='' AND s.kind='folder' AND json_extract(s.config_json,'$.mode')='index' ORDER BY o.path LIMIT 1", ("$id", id), ("$source", snapshot.SourceId)))
        await using (var reader = await occurrence.ExecuteReaderAsync(token))
            if (await reader.ReadAsync(token)) indexedPath = Path.GetFullPath(Path.Combine(reader.GetString(0), reader.GetString(1)));
        await using (var source = Command(connection, transaction,
            "INSERT INTO indexed_document_sources(document_id,source_id,path,mime) VALUES($id,$source,$path,$mime)",
            ("$id", id), ("$source", snapshot.SourceId), ("$path", indexedPath), ("$mime", snapshot.Mime)))
            await source.ExecuteNonQueryAsync(token);
        if (projection is null) return;
        await using (var text = Command(connection, transaction,
            "INSERT INTO document_text(document_id,revision,generation_json,lossless_text,search_text,offset_map,indexed_at) VALUES($id,$revision,$generation,$lossless,$search,$map,$now)",
            ("$id", id), ("$revision", snapshot.Fence.Revision), ("$generation", snapshot.Fence.GenerationJson),
            ("$lossless", projection.LosslessText), ("$search", projection.SearchText),
            ("$map", JsonSerializer.SerializeToUtf8Bytes(projection.OffsetMap)), ("$now", snapshot.UpdatedAt)))
            await text.ExecuteNonQueryAsync(token);
        foreach (var chunk in projection.Chunks)
        {
            await using var insert = Command(connection, transaction,
                "INSERT INTO chunks(id,document_id,revision,ordinal,context,text,start_offset,end_offset,page,token_count,input_hash) VALUES($chunk,$id,$revision,$ordinal,$context,$text,$start,$end,$page,$tokens,$hash) RETURNING rowid",
                ("$chunk", chunk.Id), ("$id", id), ("$revision", snapshot.Fence.Revision), ("$ordinal", chunk.Ordinal),
                ("$context", chunk.Context), ("$text", chunk.Text), ("$start", chunk.StartOffset), ("$end", chunk.EndOffset),
                ("$page", chunk.Page), ("$tokens", chunk.TokenCount), ("$hash", chunk.InputHash));
            var row = await insert.ExecuteScalarAsync(token);
            // FTS tokenizes the normalized search text; chunks.text stays the lossless slice for citations, and
            // retrieval maps highlight ranges back to it through document_text.offset_map.
            var searchText = TextProcessor.SearchSlice(projection.SearchText, projection.OffsetMap, chunk.StartOffset, chunk.EndOffset);
            await using var source = Command(connection, transaction,
                "INSERT INTO chunks_fts_source(rowid,text,context,title) VALUES($row,$text,$context,$title)",
                ("$row", row), ("$text", searchText), ("$context", chunk.Context), ("$title", snapshot.Title));
            await source.ExecuteNonQueryAsync(token);
            await using var fts = Command(connection, transaction,
                "INSERT INTO chunks_fts(rowid,text,context,title) VALUES($row,$text,$context,$title)",
                ("$row", row), ("$text", searchText), ("$context", chunk.Context), ("$title", snapshot.Title));
            await fts.ExecuteNonQueryAsync(token);
        }
        // Rebuild and generation-only replay retain the exact cached embedding input. Restore
        // vectors in the same fenced publication instead of scheduling provider work for them.
        // Attribute shared cache inputs to this document before exposing any restored vector,
        // so a later purge can reclaim its lineage without removing another owner's cache.
        await using (var owners = Command(connection, transaction,
            "INSERT OR IGNORE INTO embedding_cache_owners(input_hash,document_id) SELECT DISTINCT c.input_hash,c.document_id FROM chunks c WHERE c.document_id=$id AND EXISTS(SELECT 1 FROM embedding_cache e WHERE e.input_hash=c.input_hash)", ("$id", id)))
            await owners.ExecuteNonQueryAsync(token);
        await using (var restore = Command(connection, transaction,
            "INSERT INTO chunk_vectors(space_id,chunk_rowid,vector) SELECT e.space_id,c.rowid,e.vector FROM chunks c JOIN embedding_cache e ON e.input_hash=c.input_hash WHERE c.document_id=$id", ("$id", id)))
            await restore.ExecuteNonQueryAsync(token);
        string? activeSpace;
        await using (var lease = await state.OpenReadConnectionAsync(token))
        await using (var active = Command(lease.Connection, null, "SELECT id FROM embedding_spaces WHERE status='active' ORDER BY activated_at DESC LIMIT 1"))
            activeSpace = await active.ExecuteScalarAsync(token) as string;
        if (activeSpace is not null && snapshot.Status is "indexed" or "indexed_partial")
        {
            await using var coverage = Command(connection, transaction,
                "UPDATE indexed_documents SET status=CASE WHEN EXISTS(SELECT 1 FROM chunks c WHERE c.document_id=$id AND NOT EXISTS(SELECT 1 FROM chunk_vectors v WHERE v.chunk_rowid=c.rowid AND v.space_id=$space)) THEN 'indexed_partial' ELSE 'indexed' END WHERE document_id=$id",
                ("$id", id), ("$space", activeSpace));
            await coverage.ExecuteNonQueryAsync(token);
        }
        foreach (var tag in projection.Tags)
        {
            await using var insert = Command(connection, transaction, "INSERT OR IGNORE INTO effective_tags(document_id,tag) VALUES($id,$tag)", ("$id", id), ("$tag", tag));
            await insert.ExecuteNonQueryAsync(token);
        }
        foreach (var link in projection.Links ?? [])
        {
            var kind = Uri.TryCreate(link, UriKind.Absolute, out _) ? "markdown" : "wikilink";
            await using var insert = Command(connection, transaction,
                "INSERT OR IGNORE INTO links(from_document_id,to_document_id,target_text,kind) VALUES($id,(SELECT document_id FROM indexed_documents WHERE title=$target ORDER BY document_id LIMIT 1),$target,$kind)",
                ("$id", id), ("$target", link), ("$kind", kind));
            await insert.ExecuteNonQueryAsync(token);
        }
        using var props = JsonDocument.Parse(snapshot.PropsJson);
        var typeMetadata = new SecondBrain.Core.Domain.TypeRegistry().GetProcessingType(snapshot.Type).Metadata;
        foreach (var property in props.RootElement.EnumerateObject().Where(property => typeMetadata.IndexedProperties.Contains(property.Name)))
        {
            await using var insert = Command(connection, transaction,
                "INSERT INTO indexed_props(document_id,field,value_text,value_num) VALUES($id,$field,$text,$num)",
                ("$id", id), ("$field", "props." + property.Name),
                ("$text", property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.GetRawText()),
                ("$num", property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var number) ? number : null));
            await insert.ExecuteNonQueryAsync(token);
        }
        foreach (var role in typeMetadata.PeopleRoles)
        {
            if (!props.RootElement.TryGetProperty(role.Key, out var value)) continue;
            var values = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : new[] { value };
            foreach (var person in values)
            {
                var name = person.ValueKind == JsonValueKind.String ? person.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                await using var insert = Command(connection, transaction,
                    "INSERT OR IGNORE INTO people(document_id,role,identifier,display_name) VALUES($id,$role,$identifier,$name)",
                    ("$id", id), ("$role", role.Value), ("$identifier", name.ToLowerInvariant()), ("$name", name));
                await insert.ExecuteNonQueryAsync(token);
            }
        }
    }

    private static async ValueTask<bool> FenceMatchesAsync(DbConnection connection, DbTransaction transaction, PublicationFence fence, CancellationToken cancellationToken)
    {
        await using var lookup = Command(connection, transaction,
            "SELECT d.revision,f.generation_json FROM documents d JOIN publication_fences f ON f.document_id=d.id WHERE d.id=$id",
            ("$id", fence.DocumentId));
        await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) && reader.GetInt64(0) == fence.Revision && NormalizeGeneration(reader.GetString(1)) == fence.GenerationJson;
    }

    private static async ValueTask WriteGenerationAsync(DbConnection connection, DbTransaction transaction, string id, string generation, CancellationToken cancellationToken)
    {
        await using var write = Command(connection, transaction,
            """
            INSERT INTO publication_fences(document_id,generation_json) VALUES($id,$generation)
            ON CONFLICT(document_id) DO UPDATE SET generation_json=excluded.generation_json
            """, ("$id", id), ("$generation", generation));
        await write.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    private static PublicationFence NormalizeFence(PublicationFence fence)
    {
        ArgumentNullException.ThrowIfNull(fence);
        ArgumentException.ThrowIfNullOrWhiteSpace(fence.DocumentId);
        ArgumentOutOfRangeException.ThrowIfLessThan(fence.Revision, 1L);
        return fence with { GenerationJson = NormalizeGeneration(fence.GenerationJson) };
    }

    internal static string NormalizeGeneration(string generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generation);
        using var document = JsonDocument.Parse(generation);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Processing generations must be a JSON object.", nameof(generation));
        }
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            WriteCanonical(writer, document.RootElement);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            string? previous = null;
            foreach (var property in value.EnumerateObject().OrderBy(static p => p.Name, StringComparer.Ordinal))
            {
                if (property.Name == previous)
                {
                    throw new ArgumentException("Processing generations cannot contain duplicate property names.");
                }
                previous = property.Name;
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray())
            {
                WriteCanonical(writer, item);
            }
            writer.WriteEndArray();
        }
        else
        {
            value.WriteTo(writer);
        }
    }

    private static bool IsRemoved(string status) => DocumentStatusNames.IsUnavailable(status);

    private sealed record Snapshot(PublicationFence Fence, string Type, string Title, string PropsJson,
        string? OccurredAt, string? OccurredPrecision, string? EndsAt, string Status, string UpdatedAt, string PublishState, string SourceId, string? Mime);

    private sealed class DocumentLocks
    {
        private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

        public async ValueTask<IDisposable> AcquireAsync(string id, CancellationToken cancellationToken)
        {
            Entry entry;
            lock (entries)
            {
                if (!entries.TryGetValue(id, out entry!))
                {
                    entry = new Entry();
                    entries.Add(id, entry);
                }
                entry.Users++;
            }
            try
            {
                await entry.Semaphore.WaitAsync(cancellationToken);
                return new Releaser(this, id, entry);
            }
            catch
            {
                Release(id, entry, acquired: false);
                throw;
            }
        }

        private void Release(string id, Entry entry, bool acquired)
        {
            lock (entries)
            {
                if (acquired)
                {
                    entry.Semaphore.Release();
                }
                if (--entry.Users == 0)
                {
                    entries.Remove(id);
                    entry.Semaphore.Dispose();
                }
            }
        }

        private sealed class Entry
        {
            public SemaphoreSlim Semaphore { get; } = new(1, 1);
            public int Users { get; set; }
        }

        private sealed class Releaser(DocumentLocks owner, string id, Entry entry) : IDisposable
        {
            private int disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0)
                {
                    owner.Release(id, entry, acquired: true);
                }
            }
        }
    }
}
