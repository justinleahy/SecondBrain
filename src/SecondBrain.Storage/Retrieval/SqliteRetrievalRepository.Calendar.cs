using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Retrieval;
using SecondBrain.Core.Sources;

namespace SecondBrain.Storage.Retrieval;

public sealed partial class SqliteRetrievalRepository
{
    private async ValueTask<QueryPage> QueryCalendarAsync(QueryRequest request, IngestionAuthority authority, CancellationToken token)
    {
        var mixed = request.Type is null;
        if (!mixed && request.Sort == "title") throw new ArgumentException("Calendar occurrences are sorted by start time.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request with { Cursor = null, Limit = 0 })));
        var registry = VectorSnapshotRegistry.For(index);
        using var gate = await registry.EnterAsync(token);
        await using var snapshot = await index.OpenReadConnectionAsync(token);
        await using var transaction = await snapshot.Connection.BeginTransactionAsync(token);
        var generation = await registry.CorpusGenerationAsync(snapshot.Connection, transaction, token);
        var parameters = new List<(string Name, object? Value)>();
        var filters = new RetrievalFilters(request.Type, request.Tags, request.Source, request.PathPrefix, request.Mime, null, request.Updated, request.Predicates, request.People);
        var occurred = mixed ? "COALESCE(e.starts_at,d.occurred_at)" : "e.starts_at";
        var identity = mixed ? "COALESCE(e.instance_id,d.document_id)" : "e.instance_id";
        var sort = mixed && request.Sort != "occurred_at" ? "COALESCE(d." + request.Sort + ",'')" : occurred;
        var where = BuildFilters(filters, parameters).Replace("d.occurred_at", occurred, StringComparison.Ordinal);
        if (mixed) where += " AND (d.type<>'calendar_event' OR e.instance_id IS NOT NULL)";
        else where += " AND e.instance_id IS NOT NULL";
        if (!request.IncludeCancelled) where += " AND (e.status IS NULL OR e.status<>'cancelled')";
        if (request.Occurred?.From is { } from)
        { where += mixed ? " AND ((d.type='calendar_event' AND e.ends_at>$from) OR (d.type<>'calendar_event' AND d.occurred_at>=$from))" : " AND e.ends_at>$from"; parameters.Add(("$from", NormalizeTime(from))); }
        if (request.Occurred?.To is { } to) { where += " AND " + occurred + "<$to"; parameters.Add(("$to", NormalizeTime(to))); }
        var instance = await InstanceAsync(token);
        const string operation = "calendar-query";
        if (request.Cursor is not null)
        {
            if (request.Cursor.Length > 4096) throw new ArgumentException("Invalid cursor.");
            Cursor cursor;
            try { cursor = JsonSerializer.Deserialize<Cursor>(protection.Unprotect(CursorPurpose, request.Cursor))!; }
            catch (Exception error) when (error is not OperationCanceledException) { throw new ArgumentException("Invalid cursor.", error); }
            if (cursor is null || cursor.Instance != instance || cursor.Operation != operation || cursor.QueryHash != hash || cursor.CredentialId != authority.CredentialId || cursor.CredentialGeneration != authority.CredentialGeneration || cursor.AccountEpoch != authority.AccountEpoch || cursor.Generation != generation || cursor.ExpiresAt <= DateTimeOffset.UtcNow || cursor.IssuedAt > DateTimeOffset.UtcNow)
                throw new ArgumentException("Invalid cursor.");
            var comparison = request.Direction == "asc" ? ">" : "<";
            where += " AND (" + sort + comparison + "$position OR (" + sort + "=$position AND " + identity + comparison + "$instance))";
            parameters.Add(("$position", cursor.Position)); parameters.Add(("$instance", cursor.Id));
        }
        parameters.Add(("$limit", request.Limit + 1));
        var results = new List<DocumentRecord>(); var positions = new List<string>(); var identities = new List<string>();
        await using (var query = Command(snapshot.Connection, transaction,
            "SELECT d.document_id,d.revision,d.title,d.props_json,d.updated_at,e.instance_id,e.recurrence_id,e.starts_at,e.ends_at,COALESCE(e.status,d.status),d.type," + occurred + "," + sort + "," + identity + " FROM indexed_documents d LEFT JOIN event_occurrences e ON d.type='calendar_event' AND d.document_id=e.document_id WHERE " + where + " ORDER BY " + sort + " " + request.Direction + "," + identity + " " + request.Direction + " LIMIT $limit", parameters.ToArray()))
        await using (var reader = await query.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                string? Optional(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
                results.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(10), reader.GetString(2), reader.GetString(9), "", reader.GetString(3), "", reader.GetString(4))
                { InstanceId = Optional(5), RecurrenceId = Optional(6), StartsAt = Optional(7), EndsAt = Optional(8), OccurredAt = Optional(11) });
                positions.Add(Optional(12) ?? ""); identities.Add(reader.GetString(13));
            }
        var truncated = results.Count > request.Limit;
        if (truncated) { results.RemoveAt(results.Count - 1); positions.RemoveAt(positions.Count - 1); identities.RemoveAt(identities.Count - 1); }
        string? next = null;
        if (truncated)
        {
            var now = DateTimeOffset.UtcNow;
            next = protection.Protect(CursorPurpose, JsonSerializer.Serialize(new Cursor(instance, operation, hash, authority.CredentialId, authority.CredentialGeneration, authority.AccountEpoch, generation, positions[^1], identities[^1], now, now.AddMinutes(15))));
        }
        var coverage = new List<CalendarCoverageSource>();
        await using (var query = Command(snapshot.Connection, transaction,
            "SELECT c.container_id,c.source_id,c.covered_from,c.covered_to,c.materialization_generation FROM indexed_calendar_coverage c JOIN indexed_documents root ON root.document_id=c.container_id WHERE ($source IS NULL OR c.source_id=$source) ORDER BY c.container_id", ("$source", request.Source)))
        await using (var reader = await query.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) coverage.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4)));
        var freshness = new List<CalendarSubscriptionFreshness>();
        if (state is not null)
        {
            await using var sourceLease = await state.OpenReadConnectionAsync(token);
            await using var sourceQuery = Command(sourceLease.Connection, null,
                "SELECT c.source_id,s.status,c.error_code,c.last_success_at,c.last_attempt_at,c.poll_interval_s FROM calendar_subscriptions c JOIN sources s ON s.id=c.source_id WHERE s.status IN ('active','disabled') AND ($source IS NULL OR c.source_id=$source) ORDER BY c.source_id", ("$source", request.Source));
            await using var sources = await sourceQuery.ExecuteReaderAsync(token);
            while (await sources.ReadAsync(token))
            {
                DateTimeOffset? Time(int column) => sources.IsDBNull(column) ? null : DateTimeOffset.Parse(sources.GetString(column), CultureInfo.InvariantCulture);
                var success = Time(3); var error = sources.IsDBNull(2) ? null : sources.GetString(2);
                var status = CalendarFreshness.State(sources.GetString(1), error, success, sources.GetInt32(5), clock?.GetUtcNow() ?? DateTimeOffset.UtcNow);
                freshness.Add(new(sources.GetString(0), status, status != "current", success, Time(4), error));
            }
        }
        var complete = coverage.Count != 0 && request.Occurred?.From is { } startBound && request.Occurred?.To is { } endBound && coverage.All(item =>
            DateTimeOffset.Parse(item.From, CultureInfo.InvariantCulture) <= DateTimeOffset.Parse(startBound, CultureInfo.InvariantCulture) &&
            DateTimeOffset.Parse(item.To, CultureInfo.InvariantCulture) >= DateTimeOffset.Parse(endBound, CultureInfo.InvariantCulture)) && freshness.All(item => item.LastSuccessAt is not null);
        return new(results, next, truncated, mixed && coverage.Count == 0 && freshness.Count == 0 ? null : new CalendarCoverage(complete ? "complete" : "partial", coverage)
            { Subscriptions = freshness.Count == 0 ? null : freshness });
    }
}
