using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Storage;

namespace SecondBrain.Storage.Durability;

/// <summary>M0 metadata publication and restart recovery following spec §8's three durable steps.</summary>
public sealed class PublicationCoordinator : IPublicationCoordinator
{
    private static readonly ConditionalWeakTable<IStateStore, DocumentLocks> SharedLocks = new();
    private readonly IStateStore state;
    private readonly IIndexStore index;
    private readonly ICrashPoints crashPoints;
    private readonly DocumentLocks locks;

    /// <summary>Creates a coordinator over migrated stores. Register one coordinator per data root.</summary>
    public PublicationCoordinator(IStateStore state, IIndexStore index, ICrashPoints? crashPoints = null)
    {
        this.state = state;
        this.index = index;
        this.crashPoints = crashPoints ?? new NoOpCrashPoints();
        locks = SharedLocks.GetValue(state, static _ => new DocumentLocks());
    }

    // Journal finalization uses the same lock before changing a document's revision. This
    // prevents a filesystem mutation from invalidating an in-flight index publication.
    internal static ValueTask<IDisposable> AcquireDocumentLockAsync(IStateStore state, string documentId, CancellationToken cancellationToken = default) =>
        SharedLocks.GetValue(state, static _ => new DocumentLocks()).AcquireAsync(documentId, cancellationToken);

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
        var revision = checked(publication.ExpectedRevision + 1);
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var accepted = await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
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

            await WriteGenerationAsync(connection, transaction, publication.DocumentId, generation, token);
            return true;
        }, cancellationToken);

        if (!accepted)
        {
            return PublicationOutcome.Superseded;
        }
        await crashPoints.HitAsync(CrashPointNames.AfterStatePublishing, cancellationToken);
        return await CompleteAsync(new PublicationFence(publication.DocumentId, revision, generation), cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<PublicationOutcome> PublishCurrentAsync(PublicationFence fence, CancellationToken cancellationToken = default)
    {
        fence = NormalizeFence(fence);
        using var documentLock = await locks.AcquireAsync(fence.DocumentId, cancellationToken);
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
        var accepted = await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            if (!await FenceMatchesAsync(connection, transaction, expected, token))
            {
                return false;
            }
            await WriteGenerationAsync(connection, transaction, expected.DocumentId, generation, token);
            await using var mark = Command(connection, transaction, "UPDATE documents SET publish_state='publishing' WHERE id=$id", ("$id", expected.DocumentId));
            await mark.ExecuteNonQueryAsync(token);
            return true;
        }, cancellationToken);
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
        var committed = await index.QueueWriteAsync(async (connection, transaction, token) =>
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
            // Retain a generation marker even for a tombstone, so its removal is fenced too.
            await using var generation = Command(connection, transaction,
                """
                INSERT INTO indexed_document_generations(document_id,generation_json) VALUES($id,$generation)
                ON CONFLICT(document_id) DO UPDATE SET generation_json=excluded.generation_json
                """, ("$id", fence.DocumentId), ("$generation", fence.GenerationJson));
            await generation.ExecuteNonQueryAsync(token);
            return true;
        }, cancellationToken);

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
                "UPDATE documents SET indexed_revision=$revision,publish_state='published' WHERE id=$id",
                ("$revision", fence.Revision), ("$id", fence.DocumentId));
            await finalize.ExecuteNonQueryAsync(token);
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
              d.occurred_precision,d.status,d.updated_at,d.publish_state
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
            reader.IsDBNull(6) ? null : reader.GetString(6), endsAt, reader.GetString(7), reader.GetString(8), reader.GetString(9));
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

    private static bool IsRemoved(string status) => status is "deleted" or "purged";

    private sealed record Snapshot(PublicationFence Fence, string Type, string Title, string PropsJson,
        string? OccurredAt, string? OccurredPrecision, string? EndsAt, string Status, string UpdatedAt, string PublishState);

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
