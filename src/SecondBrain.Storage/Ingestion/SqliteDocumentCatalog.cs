using System.Data.Common;
using System.Text.Json;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Retrieval;
using SecondBrain.Storage.Auth;
using static SecondBrain.Storage.Ingestion.SqliteDocumentRepository;

namespace SecondBrain.Storage.Ingestion;

public sealed class SqliteDocumentCatalog(IStateStore state, IPayloadProtection protection, IIndexStore index) : IDocumentCatalog
{
    private const string Purpose = "SecondBrain.M3.CatalogCursor.v1";

    public async ValueTask<PreservedConflictFile?> ConflictFileAsync(string documentId, string mutationId, IngestionAuthority authority, CancellationToken cancellationToken = default)
    {
        await using var lease = await state.OpenReadConnectionAsync(cancellationToken);
        await using var transaction = await lease.Connection.BeginTransactionAsync(cancellationToken);
        await IngestionAuthorityValidator.ValidateAsync(lease.Connection, transaction, authority, Scope.Read, DateTimeOffset.UtcNow, cancellationToken: cancellationToken);
        await using var query = Command(lease.Connection, transaction, $"""
            SELECT json_extract(CAST(result.bytes AS TEXT),'$.ConflictPath')
            FROM mutations m JOIN payloads result ON result.id=m.result_payload_id AND result.redacted_at IS NULL
            JOIN payloads plan ON plan.id=$plan AND plan.redacted_at IS NULL
            JOIN documents d ON d.id=$document AND d.status NOT IN ({DocumentStatusSql.Unavailable})
            WHERE m.id=$mutation AND m.status='conflict' AND json_extract(CAST(plan.bytes AS TEXT),'$.Finalization.DocumentId')=$document
            """, ("$document", documentId), ("$mutation", mutationId), ("$plan", SecondBrain.Storage.Durability.MutationJournal.PlanPayloadId(mutationId)));
        return await query.ExecuteScalarAsync(cancellationToken) is string path ? new(Path.GetFileName(path), path) : null;
    }

    public async ValueTask<IReadOnlyList<SourceDocumentCounts>> SourceCountsAsync(IngestionAuthority authority, CancellationToken cancellationToken = default)
    {
        await using var lease = await state.OpenReadConnectionAsync(cancellationToken);
        await using var transaction = await lease.Connection.BeginTransactionAsync(cancellationToken);
        await IngestionAuthorityValidator.ValidateAsync(lease.Connection, transaction, authority, Scope.Admin, DateTimeOffset.UtcNow, cancellationToken: cancellationToken);
        await using var query = Command(lease.Connection, transaction, $"""
            SELECT s.id,COUNT(d.id),
              COALESCE(SUM(CASE WHEN d.status NOT IN ({DocumentStatusSql.Unavailable}) THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN d.status IN ('queued','processing','pending','indexed_partial') THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN d.status IN ('failed','skipped','needs_ocr') THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN d.status IN ('missing','removed_from_source') THEN 1 ELSE 0 END),0)
            FROM sources s LEFT JOIN documents d ON d.source_id=s.id WHERE s.kind='folder' GROUP BY s.id ORDER BY s.id
            """);
        var results = new List<SourceDocumentCounts>();
        await using var rows = await query.ExecuteReaderAsync(cancellationToken);
        while (await rows.ReadAsync(cancellationToken)) results.Add(new(rows.GetString(0), rows.GetInt64(1), rows.GetInt64(2), rows.GetInt64(3), rows.GetInt64(4), rows.GetInt64(5)));
        return results;
    }

    public ValueTask<CatalogPage<CatalogJob>> JobsAsync(IngestionAuthority authority, string? status = null, string? source = null,
        int limit = 50, string? cursor = null, CancellationToken cancellationToken = default)
    {
        if (status is not (null or "queued" or "running" or "failed" or "completed" or "cancelled") || source?.Length > 128)
            throw new ArgumentException("Invalid job filter.");
        var operation = "jobs:" + JsonSerializer.Serialize(new { status, source });
        return ReadAsync(operation, Scope.Read, authority, limit, cursor, $"""
            SELECT j.id,j.document_id,j.revision,d.title,
              COALESCE(json_extract(j.payload_json,'$.claim_original_name'),json_extract(j.payload_json,'$.file_name'),
                (SELECT o.path FROM occurrences o WHERE o.document_id=d.id ORDER BY o.path LIMIT 1)),
              d.source_id,s.name,j.status,j.attempts,j.error,j.kind,
              CASE WHEN j.revision=d.revision AND j.generation_json=f.generation_json THEN 1 ELSE 0 END
            FROM jobs j JOIN documents d ON d.id=j.document_id JOIN sources s ON s.id=d.source_id
            LEFT JOIN publication_fences f ON f.document_id=d.id
            WHERE d.status NOT IN ({DocumentStatusSql.Unavailable})
              AND ($after='' OR j.id<$after) AND ($status IS NULL OR j.status=$status) AND ($source IS NULL OR d.source_id=$source)
            ORDER BY j.id DESC LIMIT $limit
            """, reader =>
            {
                var reason = JobRetryPolicy.Rejection(reader.GetString(10), reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetString(7), reader.GetInt64(11) != 0);
                return new CatalogJob(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetInt32(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetString(10), reason is null, reason);
            }, item => item.Id, cancellationToken, parameters: [("$status", status), ("$source", source)]);
    }

    public async ValueTask<DocumentDetails> DetailsAsync(string id, IngestionAuthority authority, long? revision = null, CancellationToken cancellationToken = default)
    {
        await using var lease = await state.OpenReadConnectionAsync(cancellationToken);
        await using var transaction = await lease.Connection.BeginTransactionAsync(cancellationToken);
        await IngestionAuthorityValidator.ValidateAsync(lease.Connection, transaction, authority, Scope.Read, DateTimeOffset.UtcNow, cancellationToken: cancellationToken);
        await using var query = Command(lease.Connection, transaction, $"""
            SELECT d.revision,d.status,d.source_id,s.name,s.kind,d.type_origin,d.mime,d.source_url,d.created_at,d.updated_at,
              d.occurred_at,d.occurred_basis,d.occurred_precision,d.props_original_json,
              COALESCE(json_extract(CAST(p.bytes AS TEXT),'$.Aliases'),'[]'),
              COALESCE(json_extract(CAST(p.bytes AS TEXT),'$.Tags'),'[]'),
              COALESCE(json_extract(CAST(p.bytes AS TEXT),'$.Links'),'[]'),
              EXISTS(SELECT 1 FROM overrides o WHERE o.document_id=d.id AND o.kind='tags'),d.type_confidence,
              json_extract(CAST(p.bytes AS TEXT),'$.Enrichment')
            FROM documents d JOIN sources s ON s.id=d.source_id
            LEFT JOIN document_projections dp ON dp.document_id=d.id
            LEFT JOIN payloads p ON p.id=dp.payload_id AND p.redacted_at IS NULL
            WHERE d.id=$id AND NOT EXISTS(SELECT 1 FROM documents parent WHERE parent.id=d.container_document_id AND parent.status IN ({DocumentStatusSql.Unavailable}))
            """, ("$id", id));
        DocumentDetails result;
        string[] targets;
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken) || DocumentStatusNames.IsUnavailable(reader.GetString(1))) throw new IngestionConflictException("not-found");
            if (revision is not null && revision != reader.GetInt64(0)) throw new IngestionConflictException("revision-mismatch");
            string? Optional(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
            var origin = reader.GetInt64(17) != 0 ? "user" : reader.GetString(4) == "folder" ? "frontmatter" : "capture";
            var enrichment = reader.IsDBNull(19) ? null : JsonSerializer.Deserialize<DocumentEnrichment>(reader.GetString(19));
            var tags = (JsonSerializer.Deserialize<string[]>(reader.GetString(15)) ?? []).Select(tag => new DocumentTag(tag, origin)).ToArray();
            if (enrichment is not null && reader.GetInt64(17) == 0) tags = tags.Concat(enrichment.Tags.Select(t => new DocumentTag(t, "llm"))).Distinct().ToArray();
            targets = JsonSerializer.Deserialize<string[]>(reader.GetString(16)) ?? [];
            result = new(reader.GetInt64(0), reader.GetString(2), reader.GetString(3), reader.GetString(5), Optional(6), Optional(7),
                reader.GetString(8), reader.GetString(9), Optional(10), Optional(11), Optional(12), reader.GetString(13),
                JsonSerializer.Deserialize<string[]>(reader.GetString(14)) ?? [], tags, [])
                { TypeConfidence = reader.IsDBNull(18) ? null : reader.GetDouble(18), Enrichment = enrichment };
        }
        var links = new List<DocumentLink>();
        foreach (var target in targets.Take(200))
        {
            await using var lookup = Command(lease.Connection, transaction,
                $"SELECT id,title FROM documents WHERE status NOT IN ({DocumentStatusSql.Unavailable}) AND (id=$target OR title=$target) ORDER BY CASE WHEN id=$target THEN 0 ELSE 1 END,id LIMIT 1", ("$target", target));
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            links.Add(await reader.ReadAsync(cancellationToken) ? new(target, reader.GetString(0), reader.GetString(1)) : new(target, null, null));
        }
        CalendarDocumentInfo? calendar = null;
        await using (var parent = Command(lease.Connection, transaction, "SELECT container_document_id FROM documents WHERE id=$id", ("$id", id)))
        {
            if (await parent.ExecuteScalarAsync(cancellationToken) is string container)
            {
                using var gate = await SecondBrain.Storage.Retrieval.VectorSnapshotRegistry.For(index).EnterAsync(cancellationToken);
                await using var calendarLease = await index.OpenReadConnectionAsync(cancellationToken);
                string? from = null, to = null; long generation = 0;
                await using (var coverage = Command(calendarLease.Connection, null, "SELECT covered_from,covered_to,materialization_generation FROM indexed_calendar_coverage WHERE container_id=$container", ("$container", container)))
                await using (var range = await coverage.ExecuteReaderAsync(cancellationToken))
                    if (await range.ReadAsync(cancellationToken)) { from = range.GetString(0); to = range.GetString(1); generation = range.GetInt64(2); }
                if (from is not null && to is not null)
                {
                    var occurrences = new List<SecondBrain.Extraction.Contracts.CalendarInstance>();
                    await using (var eventQuery = Command(calendarLease.Connection, null, "SELECT e.recurrence_id,e.starts_at,e.ends_at,e.status,json_extract(d.props_json,'$.uid') FROM event_occurrences e JOIN indexed_documents d ON d.document_id=e.document_id WHERE e.document_id=$id ORDER BY e.starts_at,e.instance_id LIMIT 21", ("$id", id)))
                    await using (var events = await eventQuery.ExecuteReaderAsync(cancellationToken))
                        while (await events.ReadAsync(cancellationToken)) occurrences.Add(new(events.GetString(4), events.GetString(0), events.GetString(1), events.GetString(2), events.GetString(3)));
                    await using var duplicates = Command(calendarLease.Connection, null, "SELECT COUNT(DISTINCT other.container_id) FROM indexed_calendar_members self JOIN indexed_documents a ON a.document_id=self.document_id JOIN indexed_documents b ON json_extract(b.props_json,'$.uid')=json_extract(a.props_json,'$.uid') JOIN indexed_calendar_members other ON other.document_id=b.document_id JOIN indexed_documents root ON root.document_id=other.container_id WHERE self.document_id=$id AND other.container_id<>self.container_id", ("$id", id));
                    var count = Convert.ToInt32(await duplicates.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
                    calendar = new(container, from, to, generation, count, occurrences.Take(20).ToArray(), occurrences.Count > 20);
                }
            }
        }
        return result with { Links = links, Calendar = calendar };
    }

    public ValueTask<CatalogPage<DocumentRevisionEntry>> RevisionsAsync(string id, IngestionAuthority authority, int limit = 50,
        string? cursor = null, CancellationToken cancellationToken = default) => ReadAsync("revisions:" + id, Scope.Read, authority, limit, cursor,
        "SELECT revision,cause,created_at FROM revisions WHERE document_id=$document AND revision>CAST($after AS INTEGER) ORDER BY revision LIMIT $limit",
        reader => new DocumentRevisionEntry(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)),
        item => item.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken, id);

    public async ValueTask<IReadOnlyList<CatalogCompletion>> CompleteAsync(string kind, string prefix, IngestionAuthority authority,
        int limit = 20, CancellationToken cancellationToken = default)
    {
        if (kind is not ("link" or "tag" or "person") || prefix.Length > 256 || limit is < 1 or > 50)
            throw new ArgumentException("Invalid completion request.");
        await using var stateLease = await state.OpenReadConnectionAsync(cancellationToken);
        await using var stateTransaction = await stateLease.Connection.BeginTransactionAsync(cancellationToken);
        await IngestionAuthorityValidator.ValidateAsync(stateLease.Connection, stateTransaction, authority, Scope.Read, DateTimeOffset.UtcNow, cancellationToken: cancellationToken);
        var pattern = prefix.Replace("!", "!!", StringComparison.Ordinal).Replace("%", "!%", StringComparison.Ordinal).Replace("_", "!_", StringComparison.Ordinal) + "%";
        var results = new List<CatalogCompletion>();
        if (kind == "link")
        {
            await using var query = Command(stateLease.Connection, stateTransaction,
                $"SELECT title,id,type FROM documents WHERE status NOT IN ({DocumentStatusSql.Unavailable}) AND title LIKE $q ESCAPE '!' ORDER BY title,id LIMIT $limit",
                ("$q", pattern), ("$limit", limit));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) results.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            return results;
        }
        using var gate = await SecondBrain.Storage.Retrieval.VectorSnapshotRegistry.For(index).EnterAsync(cancellationToken);
        await using var indexLease = await index.OpenReadConnectionAsync(cancellationToken);
        var sql = kind == "tag"
            ? $"SELECT t.tag,t.tag,CAST(COUNT(*) AS TEXT) FROM effective_tags t JOIN indexed_documents d ON d.document_id=t.document_id WHERE d.status NOT IN ({DocumentStatusSql.Unavailable}) AND t.tag LIKE $q ESCAPE '!' GROUP BY t.tag ORDER BY t.tag LIMIT $limit"
            : $"SELECT COALESCE(p.display_name,p.identifier),p.identifier,p.role FROM people p JOIN indexed_documents d ON d.document_id=p.document_id WHERE d.status NOT IN ({DocumentStatusSql.Unavailable}) AND (p.identifier LIKE $q ESCAPE '!' OR p.display_name LIKE $q ESCAPE '!') GROUP BY p.identifier,p.role ORDER BY p.identifier,p.role LIMIT $limit";
        await using var read = Command(indexLease.Connection, null, sql, ("$q", pattern), ("$limit", limit));
        await using var rows = await read.ExecuteReaderAsync(cancellationToken);
        while (await rows.ReadAsync(cancellationToken)) results.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2)));
        return results;
    }

    public ValueTask<CatalogPage<CatalogSource>> SourcesAsync(IngestionAuthority authority, int limit = 200,
        string? cursor = null, CancellationToken cancellationToken = default) => ReadAsync("sources", Scope.Read, authority, limit, cursor,
        "SELECT id,name,status FROM sources WHERE id>$after ORDER BY id LIMIT $limit",
        reader => new CatalogSource(reader.GetString(0), reader.GetString(1), reader.GetString(2)), item => item.Id, cancellationToken);

    public ValueTask<CatalogPage<TrashedDocument>> TrashAsync(IngestionAuthority authority, int limit = 50,
        string? cursor = null, CancellationToken cancellationToken = default) => ReadAsync("trash", Scope.Read, authority, limit, cursor,
        "SELECT d.id,d.revision,d.title,d.type,d.deleted_at FROM documents d WHERE d.status='deleted' AND d.id>$after AND NOT EXISTS(SELECT 1 FROM documents p WHERE p.id=d.container_document_id AND p.status IN ('deleted','purged','missing','removed_from_source')) ORDER BY d.id LIMIT $limit",
        reader => new TrashedDocument(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)),
        item => item.Id, cancellationToken);

    public ValueTask<CatalogPage<SuppressionRecord>> SuppressionsAsync(IngestionAuthority authority, int limit = 50,
        string? cursor = null, CancellationToken cancellationToken = default) => ReadAsync("suppressions", Scope.Write, authority, limit, cursor,
        """
        SELECT s.id,s.scope,src.name,s.key,s.reason,s.created_at,d.status
        FROM suppressions s LEFT JOIN sources src ON src.id=s.source_id
        LEFT JOIN documents d ON s.scope='document' AND d.id=s.key
        WHERE s.id>$after ORDER BY s.id LIMIT $limit
        """, reader => new SuppressionRecord(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)),
        item => item.Id, cancellationToken);

    public async ValueTask<bool> LiftSuppressionAsync(string id, IngestionAuthority authority, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256) throw new ArgumentException("Invalid suppression identifier.");
        return await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await IngestionAuthorityValidator.ValidateAsync(connection, transaction, authority, Scope.Write, DateTimeOffset.UtcNow,
                sessionOnly: true, cancellationToken: token);
            await using var command = Command(connection, transaction, "DELETE FROM suppressions WHERE id=$id", ("$id", id));
            return await command.ExecuteNonQueryAsync(token) != 0;
        }, cancellationToken);
    }

    private async ValueTask<CatalogPage<T>> ReadAsync<T>(string operation, Scope scope, IngestionAuthority authority,
        int limit, string? cursor, string sql, Func<DbDataReader, T> read, Func<T, string> id, CancellationToken token, string? documentId = null,
        IReadOnlyList<(string Name, object? Value)>? parameters = null)
    {
        if (limit is < 1 or > 200) throw new ArgumentException("Limit must be between 1 and 200.");
        await using var lease = await state.OpenReadConnectionAsync(token);
        await using var transaction = await lease.Connection.BeginTransactionAsync(token);
        await IngestionAuthorityValidator.ValidateAsync(lease.Connection, transaction, authority, scope, DateTimeOffset.UtcNow, cancellationToken: token);
        if (documentId is not null)
        {
            await using var document = Command(lease.Connection, transaction, "SELECT status FROM documents WHERE id=$document", ("$document", documentId));
            if (await document.ExecuteScalarAsync(token) is not string status || DocumentStatusNames.IsUnavailable(status)) throw new IngestionConflictException("not-found");
        }
        await using var instanceCommand = Command(lease.Connection, transaction, "SELECT value FROM meta WHERE key='instance_id'");
        var instance = (string?)await instanceCommand.ExecuteScalarAsync(token) ?? throw new InvalidOperationException("Instance identity is missing.");
        var after = "";
        if (cursor is not null)
        {
            try
            {
                if (cursor.Length > 4096) throw new ArgumentException();
                var saved = JsonSerializer.Deserialize<CatalogCursor>(protection.Unprotect(Purpose, cursor));
                if (saved is null || saved.Version != 1 || saved.Operation != operation || saved.Instance != instance ||
                    saved.Credential != authority.CredentialId || saved.Generation != authority.CredentialGeneration || saved.Epoch != authority.AccountEpoch ||
                    saved.IssuedAt > DateTimeOffset.UtcNow || saved.ExpiresAt <= DateTimeOffset.UtcNow || saved.After.Length > 256)
                    throw new ArgumentException();
                after = saved.After;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { throw new ArgumentException("The catalog cursor is invalid or expired.", exception); }
        }
        var items = new List<T>();
        await using (var command = Command(lease.Connection, transaction, sql,
            new (string Name, object? Value)[] { ("$after", after), ("$limit", limit + 1), ("$document", documentId) }.Concat(parameters ?? []).ToArray()))
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) items.Add(read(reader));
        string? next = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            var now = DateTimeOffset.UtcNow;
            next = protection.Protect(Purpose, JsonSerializer.Serialize(new CatalogCursor(1, operation, instance, authority.CredentialId,
                authority.CredentialGeneration, authority.AccountEpoch, id(items[^1]), now, now.AddMinutes(15))));
        }
        return new(items, next);
    }

    private sealed record CatalogCursor(int Version, string Operation, string Instance, string Credential, long Generation, long Epoch,
        string After, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt);
}
