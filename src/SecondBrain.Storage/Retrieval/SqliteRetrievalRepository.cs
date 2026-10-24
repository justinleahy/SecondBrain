using System.Data.Common;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Retrieval;
using SecondBrain.Storage.Ingestion;

namespace SecondBrain.Storage.Retrieval;

/// <summary>Retrieval uses only a single committed index snapshot, never live state metadata.</summary>
public sealed partial class SqliteRetrievalRepository(IIndexStore index, IPayloadProtection protection, IOptionsMonitor<SecondBrainOptions>? options = null, IStateStore? state = null) : IRetrievalRepository
{
    private const string CursorPurpose = "SecondBrain.M1.QueryCursor.v1";
    private const string QueryOperation = "query";
    public async ValueTask<SearchResponse> SearchAsync(SearchRequest request, float[]? queryVector = null, string? spaceId = null, CancellationToken cancellationToken = default)
    {
        RetrievalFilterValidation.Validate(request.Filters);
        var started = Stopwatch.GetTimestamp();
        var traceEnabled = options?.CurrentValue.Logging.RetrievalTrace == true;
        double gateAndSnapshot = 0, lexicalRanking = 0, lexicalMaterialization = 0,
            vectorAcquisition = 0, semanticFiltering = 0, semanticScoring = 0,
            semanticMaterialization = 0, fusion = 0;
        var scoringPartitions = 0; var exactChunksScored = 0; var lexicalRankingPasses = 0;
        var lexicalContributionHit = false;
        var lexicalState = BoundedLexicalState.For(index);
        long PhaseStart() => traceEnabled ? Stopwatch.GetTimestamp() : 0;
        double PhaseElapsed(long phaseStart) => traceEnabled ? Stopwatch.GetElapsedTime(phaseStart).TotalMilliseconds : 0;
        if (string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > 8192 || request.Limit is < 1 or > 200 || request.Mode is not ("keyword" or "semantic" or "hybrid") || request.Rerank is not ("none" or "llm" or "provider")) throw new ArgumentException("Invalid search request.");
        var vectorRegistry = VectorSnapshotRegistry.For(index);
        using var consistentGeneration = await vectorRegistry.EnterAsync(cancellationToken);
        await using var snapshot = await index.OpenReadConnectionAsync(cancellationToken);
        await using var transaction = await snapshot.Connection.BeginTransactionAsync(cancellationToken);
        gateAndSnapshot = PhaseElapsed(started);
        var parameters = new List<(string Name, object? Value)>();
        var filter = BuildFilters(request.Filters ?? new RetrievalFilters(), parameters);
        var degraded = new List<string>();
        var lexical = new List<Candidate>();
        if (request.Mode != "semantic")
        {
            var rankingStarted = PhaseStart();
            var query = SanitizeFts(request.Query);
            if (query.Length > 0)
            {
                var marker = Guid.NewGuid().ToString("N");
                var start = "[" + marker + ":start]"; var end = "[" + marker + ":end]";
                var lexicalParameters = parameters.Concat(new[] { ("$query", (object?)query), ("$start", (object?)start), ("$end", (object?)end) }).ToArray();
                var ids = new List<string>();
                var rowids = new List<long>();
                // A bounded SQL top-k sorter avoids sorting/spilling every lexical match.
                // Grow the ranked prefix until it contains fifty diverse chunks; every filter
                // remains inside the query and ties retain the canonical chunk-id order.
                var allowFill = request.Mode == "keyword" || queryVector is null || spaceId is null || vectorRegistry.IsCurrent(spaceId, queryVector.Length);
                var fillRoom = TimeSpan.FromSeconds(10) - Stopwatch.GetElapsedTime(started);
                var derived = await lexicalState.TryRankAsync(snapshot.Connection, transaction, vectorRegistry.Generation,
                    query, filter, parameters.ToArray(), allowFill, fillRoom, cancellationToken);
                if (derived is not null)
                {
                    lexicalContributionHit = true;
                    foreach (var hit in derived) { ids.Add(hit.Id); rowids.Add(hit.Rowid); }
                }
                else
                {
                    var prefix = 256;
                    while (true)
                    {
                        lexicalRankingPasses++;
                        ids.Clear(); rowids.Clear(); var scanned = 0;
                        await using var read = Command(snapshot.Connection, transaction,
                            "SELECT c.id,c.document_id,c.rowid FROM chunks c JOIN indexed_documents d ON d.document_id=c.document_id JOIN chunks_fts ON chunks_fts.rowid=c.rowid WHERE chunks_fts MATCH $query AND " + filter + " ORDER BY bm25(chunks_fts),c.id LIMIT $prefix", lexicalParameters.Append(("$prefix", (object?)prefix)).ToArray());
                        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                        var counts = new Dictionary<string, int>();
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            scanned++;
                            var document = reader.GetString(1);
                            counts.TryGetValue(document, out var count);
                            if (count >= 3) continue;
                            counts[document] = count + 1;
                            ids.Add(reader.GetString(0));
                            rowids.Add(reader.GetInt64(2));
                            if (ids.Count == 50) break;
                        }
                        if (ids.Count == 50 || scanned < prefix) break;
                        prefix = checked(prefix * 4);
                    }
                }
                lexicalRanking = PhaseElapsed(rankingStarted);
                var materializationStarted = PhaseStart();
                var offsetMaps = new Dictionary<string, int[]>(StringComparer.Ordinal);
                var materialized = new Dictionary<string, (Candidate Candidate, string Marked, string SearchText, int Start, int End)>(StringComparer.Ordinal);
                if (ids.Count > 0)
                {
                    var selected = rowids.Select((rowid, index) => (Name: "$selected" + index, Value: (object?)rowid)).ToArray();
                    // A monotonic MATCH cursor initializes the posting lists once. Unary + keeps
                    // selected IDs out of the virtual-table equality seek, which would reopen
                    // the cursor for every selected hit on a segmented FTS index.
                    await using var read = Command(snapshot.Connection, transaction,
                        Select.Replace(" FROM chunks c", ",highlight(chunks_fts,0,$start,$end),f.text,c.start_offset,c.end_offset FROM chunks_fts CROSS JOIN chunks c ON c.rowid=chunks_fts.rowid", StringComparison.Ordinal) +
                        " JOIN chunks_fts_source f ON f.rowid=c.rowid WHERE chunks_fts MATCH $query AND chunks_fts.rowid>=$low AND chunks_fts.rowid<=$high AND +chunks_fts.rowid IN (" +
                        string.Join(',', selected.Select(parameter => parameter.Name)) + ") ORDER BY chunks_fts.rowid",
                        lexicalParameters.Concat(selected).Concat(new[] { ("$low", (object?)rowids.Min()), ("$high", (object?)rowids.Max()) }).ToArray());
                    await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        var candidate = ReadCandidate(reader);
                        materialized.Add(candidate.ChunkId, (candidate, reader.GetString(14), reader.GetString(15), reader.GetInt32(16), reader.GetInt32(17)));
                    }
                }
                foreach (var id in ids)
                {
                    if (materialized.TryGetValue(id, out var hit))
                    {
                        var candidate = hit.Candidate;
                        var marked = hit.Marked; var searchText = hit.SearchText;
                        var chunkStart = hit.Start; var chunkEnd = hit.End;
                        // FTS matched the normalized search slice; highlights are reported against the lossless chunk text.
                        if (!offsetMaps.TryGetValue(candidate.DocumentId, out var offsetMap))
                        {
                            await using var map = Command(snapshot.Connection, transaction, "SELECT offset_map FROM document_text WHERE document_id=$document", ("$document", candidate.DocumentId));
                            offsetMap = await map.ExecuteScalarAsync(cancellationToken) is byte[] bytes ? JsonSerializer.Deserialize<int[]>(bytes) ?? [] : [];
                            offsetMaps[candidate.DocumentId] = offsetMap;
                        }
                        var ranges = offsetMap.Length == 0 ? [] : MatchRanges(searchText, marked, start, end)
                            .Select(range => TextProcessor.ToLosslessRange(offsetMap, chunkStart, chunkEnd, range.Start, range.End))
                            .Where(range => range.End > range.Start).Select(range => new HighlightRange(range.Start, range.End)).ToArray();
                        lexical.Add(candidate with { Highlights = ranges });
                    }
                }
                lexicalMaterialization = PhaseElapsed(materializationStarted);
            }
            else lexicalRanking = PhaseElapsed(rankingStarted);
        }
        var semantic = new List<Candidate>();
        if (request.Mode != "keyword")
        {
            if (queryVector is null || spaceId is null) degraded.Add("embed-unavailable");
            else
            {
                var acquisitionStarted = PhaseStart();
                var normalized = Normalize(queryVector);
                using var vectors = await vectorRegistry.AcquireAsync(snapshot.Connection, transaction, spaceId, normalized.Length, cancellationToken);
                vectorAcquisition = PhaseElapsed(acquisitionStarted);
                var filteringStarted = PhaseStart();
                var cap = options?.CurrentValue.Retrieval.MaxExactCandidates ?? 1_000_000;
                var documents = new List<(string Id, IReadOnlyList<string> Chunks, int Count)>();
                var count = 0;
                await using (var read = Command(snapshot.Connection, transaction,
                    "SELECT d.document_id FROM indexed_documents d WHERE " + filter + " ORDER BY d.updated_at DESC,d.document_id", parameters.ToArray()))
                await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        var documentId = reader.GetString(0);
                        if (vectorRegistry.HasMissingVectors(documentId) && !degraded.Contains("vector-pending")) degraded.Add("vector-pending");
                        var chunks = vectorRegistry.ChunkIds(documentId);
                        var available = Math.Min(chunks.Count, Math.Max(0, cap - count));
                        if (available < chunks.Count && !degraded.Contains("exact-cap")) degraded.Add("exact-cap");
                        count += available;
                        if (available != 0) documents.Add((documentId, chunks, available));
                    }
                }
                semanticFiltering = PhaseElapsed(filteringStarted);
                var scoringStarted = PhaseStart();
                exactChunksScored = count;
                // Each partition retains its exact diverse top fifty. A discarded candidate
                // cannot enter the global top fifty: fifty better candidates already exist in
                // its partition. SQL filtering and the cap prefix finish before parallel work.
                var partitions = count >= 100_000 ? Math.Min(Environment.ProcessorCount, 8) : 1;
                scoringPartitions = partitions;
                var ranked = new VectorTopCandidates[partitions];
                Parallel.For(0, partitions, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = partitions }, partition =>
                {
                    var top = new VectorTopCandidates();
                    for (var documentIndex = partition * documents.Count / partitions;
                         documentIndex < (partition + 1) * documents.Count / partitions; documentIndex++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var document = documents[documentIndex];
                        for (var chunkIndex = 0; chunkIndex < document.Count; chunkIndex++)
                        {
                            var id = document.Chunks[chunkIndex];
                            if (!vectors.TryGetVector(id, out var vector)) throw new InvalidDataException("The vector snapshot is missing a committed chunk.");
                            top.Add(id, document.Id, Dot(normalized, vector.Span));
                        }
                    }
                    ranked[partition] = top;
                });
                var combined = new VectorTopCandidates();
                foreach (var partition in ranked)
                    foreach (var candidate in partition.Selected) combined.Add(candidate.ChunkId, candidate.DocumentId, candidate.Score);
                var selected = combined.Selected;
                semanticScoring = PhaseElapsed(scoringStarted);
                var materializationStarted = PhaseStart();
                // Only the final fifty candidates materialize text and metadata.
                foreach (var candidate in selected)
                {
                    await using var read = Command(snapshot.Connection, transaction, Select + " WHERE c.id=$id", ("$id", candidate.ChunkId));
                    await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                    if (await reader.ReadAsync(cancellationToken)) semantic.Add(ReadCandidate(reader) with { Score = candidate.Score });
                }
                semanticMaterialization = PhaseElapsed(materializationStarted);
            }
        }
        var fusionStarted = PhaseStart();
        var fused = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        AddLeg(lexical, fused); AddLeg(semantic, fused);
        // Reranking happens after retrieval; SearchReranker reports "rerank" only when it actually fails.
        var hits = fused.Values.Select(candidate => request.RecencyBoost ? candidate with { Score = candidate.Score * (1 + .1 * Math.Exp(-Math.Max(0, (DateTimeOffset.UtcNow - DateTimeOffset.Parse(candidate.UpdatedAt, CultureInfo.InvariantCulture)).TotalDays) / 30)) } : candidate)
            .OrderByDescending(candidate => candidate.Score).ThenBy(candidate => candidate.ChunkId, StringComparer.Ordinal).Take(request.Limit)
            .Select(candidate => new SearchHit(candidate.ChunkId, candidate.DocumentId, candidate.Revision, candidate.Type, candidate.Title,
                candidate.Path, candidate.Context, candidate.Text, candidate.Highlights ?? [], candidate.Score,
                candidate.OccurredAt, candidate.UpdatedAt, candidate.Tags, candidate.Page)).ToArray();
        fusion = PhaseElapsed(fusionStarted);
        return new SearchResponse(hits, degraded.Distinct(StringComparer.Ordinal).ToArray())
        {
            Trace = traceEnabled ? new(
                lexical.Select(c => new TraceCandidate(c.ChunkId, c.DocumentId)).ToArray(),
                semantic.Select(c => new TraceCandidate(c.ChunkId, c.DocumentId)).ToArray(),
                fused.Values.OrderByDescending(c => c.Score).ThenBy(c => c.ChunkId, StringComparer.Ordinal).Select(c => new TraceCandidate(c.ChunkId, c.DocumentId)).ToArray(),
                Stopwatch.GetElapsedTime(started).TotalMilliseconds)
                {
                    Phases = new(gateAndSnapshot, lexicalRanking, lexicalMaterialization, vectorAcquisition,
                        semanticFiltering, semanticScoring, semanticMaterialization, fusion),
                    Runtime = new(Environment.ProcessorCount, Vector.IsHardwareAccelerated, Vector<float>.Count,
                        scoringPartitions, exactChunksScored, lexicalRankingPasses, lexical.Count, semantic.Count)
                    { LexicalContributionHit = lexicalContributionHit, LexicalDerivedBytes = lexicalState.AccountedBytes, LexicalPeakBytes = lexicalState.PeakBytes, LexicalPrimitiveLoads = lexicalState.PrimitiveLoads },
                } : null,
        };
    }

    public async ValueTask<IReadOnlyList<ContextPassage>> ExpandAsync(IReadOnlyList<SearchHit> selected, int tokenBudget, CancellationToken cancellationToken = default)
    {
        if (selected.Count > 200 || tokenBudget is < 0 or > 100000) throw new ArgumentException("Invalid context bounds.");
        await using var snapshot = await index.OpenReadConnectionAsync(cancellationToken);
        await using var transaction = await snapshot.Connection.BeginTransactionAsync(cancellationToken);
        var seeds = new List<ContextPassage>(); var neighbors = new List<ContextPassage>();
        foreach (var hit in selected)
        {
            // The anchor must still be live at the requested revision in this same snapshot.
            await using var read = Command(snapshot.Connection, transaction,
                $"SELECT c.id,c.document_id,c.revision,c.ordinal,c.context,c.text,c.page,c.token_count FROM chunks anchor JOIN chunks c ON c.document_id=anchor.document_id AND c.ordinal BETWEEN anchor.ordinal-1 AND anchor.ordinal+1 JOIN indexed_documents d ON d.document_id=c.document_id WHERE anchor.id=$chunk AND anchor.document_id=$document AND anchor.revision=$revision AND c.revision=$revision AND d.status NOT IN ({DocumentStatusSql.Unavailable}) ORDER BY c.ordinal",
                ("$chunk", hit.ChunkId), ("$document", hit.DocumentId), ("$revision", hit.Revision));
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var passage = new ContextPassage(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt32(3), reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetInt32(6), reader.GetInt32(7));
                if (passage.ChunkId == hit.ChunkId) seeds.Add(passage); else neighbors.Add(passage);
            }
        }
        var included = new HashSet<string>(StringComparer.Ordinal); var results = new List<ContextPassage>();
        foreach (var passage in seeds.Concat(neighbors))
        {
            if (included.Contains(passage.ChunkId) || passage.Tokens > tokenBudget) continue;
            included.Add(passage.ChunkId); results.Add(passage); tokenBudget -= passage.Tokens;
        }
        return results;
    }

    public async ValueTask RecordTraceAsync(SearchRequest request, SearchResponse response, CancellationToken cancellationToken = default)
    {
        if (options?.CurrentValue.Logging.RetrievalTrace != true || response.Trace is null) return;
        // No query text or passage bodies are persisted. Document lineage supports purge.
        var query = JsonSerializer.Serialize(new { query_sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.Query))), request.Mode, request.Rerank, request.Limit });
        var trace = JsonSerializer.Serialize(new { response.Trace.Lexical, response.Trace.Semantic, response.Trace.Fused,
            final = response.Results.Select(hit => new TraceCandidate(hit.ChunkId, hit.DocumentId)), response.Trace.ElapsedMilliseconds,
            response.Trace.Phases, response.Trace.Runtime, response.Degraded });
        await index.QueueWriteAsync(async (connection, transaction, token) =>
        {
            // A deletion may have committed after scoring; never resurrect its trace lineage.
            foreach (var document in response.Trace.Fused.Select(candidate => candidate.DocumentId).Distinct(StringComparer.Ordinal))
            {
                await using var live = Command(connection, transaction, $"SELECT 1 FROM indexed_documents WHERE document_id=$id AND status NOT IN ({DocumentStatusSql.Unavailable})", ("$id", document));
                if (await live.ExecuteScalarAsync(token) is null) return false;
            }
            await using var insert = Command(connection, transaction, "INSERT INTO retrieval_traces(at,query_json,trace_json) VALUES($at,$query,$trace)",
                ("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)), ("$query", query), ("$trace", trace));
            await insert.ExecuteNonQueryAsync(token);
            await using var trim = Command(connection, transaction, "DELETE FROM retrieval_traces WHERE id NOT IN(SELECT id FROM retrieval_traces ORDER BY id DESC LIMIT 200)");
            await trim.ExecuteNonQueryAsync(token); return true;
        }, cancellationToken);
    }

    public async ValueTask<QueryPage> QueryAsync(QueryRequest request, IngestionAuthority authority, CancellationToken cancellationToken = default)
    {
        if (request.Limit is < 1 or > 200 || request.Sort is not ("updated_at" or "occurred_at" or "title") || request.Direction is not ("asc" or "desc")) throw new ArgumentException("Invalid query bounds or sort.");
        RetrievalFilterValidation.Validate(new RetrievalFilters(request.Type, request.Tags, request.Source, request.PathPrefix, request.Mime, request.Occurred, request.Updated, request.Predicates, request.People));
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request with { Cursor = null, Limit = 0 })));
        var registry = VectorSnapshotRegistry.For(index);
        using var consistentGeneration = await registry.EnterAsync(cancellationToken);
        await using var snapshot = await index.OpenReadConnectionAsync(cancellationToken);
        await using var transaction = await snapshot.Connection.BeginTransactionAsync(cancellationToken);
        var generation = await registry.CorpusGenerationAsync(snapshot.Connection, transaction, cancellationToken);
        var parameters = new List<(string Name, object? Value)>();
        var filter = BuildFilters(new RetrievalFilters(request.Type, request.Tags, request.Source, request.PathPrefix, request.Mime, request.Occurred, request.Updated, request.Predicates, request.People), parameters);
        var sort = "COALESCE(d." + request.Sort + ",'')";
        var instance = await InstanceAsync(cancellationToken);
        if (request.Cursor is not null)
        {
            if (request.Cursor.Length > 4096) throw new ArgumentException("Invalid cursor.");
            Cursor cursor;
            try { cursor = JsonSerializer.Deserialize<Cursor>(protection.Unprotect(CursorPurpose, request.Cursor))!; }
            catch (Exception error) when (error is not OperationCanceledException) { throw new ArgumentException("Invalid cursor.", error); }
            if (cursor is null || cursor.Instance != instance || cursor.Operation != QueryOperation || cursor.QueryHash != hash || cursor.CredentialId != authority.CredentialId || cursor.CredentialGeneration != authority.CredentialGeneration || cursor.AccountEpoch != authority.AccountEpoch || cursor.Generation != generation || cursor.ExpiresAt <= DateTimeOffset.UtcNow || cursor.IssuedAt > DateTimeOffset.UtcNow) throw new ArgumentException("Invalid cursor.");
            var comparison = request.Direction == "asc" ? ">" : "<";
            filter += " AND (" + sort + comparison + "$position OR (" + sort + "=$position AND d.document_id" + comparison + "$id))";
            parameters.Add(("$position", cursor.Position)); parameters.Add(("$id", cursor.Id));
        }
        parameters.Add(("$limit", request.Limit + 1));
        var results = new List<DocumentRecord>();
        var positions = new List<string>();
        await using (var read = Command(snapshot.Connection, transaction,
            "SELECT d.document_id,d.revision,d.type,d.title,d.status,d.props_json,d.updated_at," + sort + " FROM indexed_documents d WHERE " + filter + " ORDER BY " + sort + " " + request.Direction + ",d.document_id " + request.Direction + " LIMIT $limit", parameters.ToArray()))
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(new DocumentRecord(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), "", reader.GetString(5), "", reader.GetString(6)));
                positions.Add(reader.GetString(7));
            }
        var truncated = results.Count > request.Limit;
        if (truncated) { results.RemoveAt(results.Count - 1); positions.RemoveAt(positions.Count - 1); }
        string? next = null;
        if (truncated)
        {
            var now = DateTimeOffset.UtcNow;
            next = protection.Protect(CursorPurpose, JsonSerializer.Serialize(new Cursor(instance, QueryOperation, hash, authority.CredentialId,
                authority.CredentialGeneration, authority.AccountEpoch, generation, positions[^1], results[^1].Id, now, now.AddMinutes(15))));
        }
        return new QueryPage(results, next, truncated);
    }

    private static string BuildFilters(RetrievalFilters filters, List<(string Name, object? Value)> parameters)
    {
        if (filters.Type is not null && filters.Type is not ("note" or "article" or "file")) throw new ArgumentException("Unsupported M1 type.");
        var clauses = new List<string> { $"d.status NOT IN ({DocumentStatusSql.Unavailable})" };
        void Add(string expression, object? value) { var name = "$f" + parameters.Count; clauses.Add(expression.Replace("?", name, StringComparison.Ordinal)); parameters.Add((name, value)); }
        if (filters.Type is not null) Add("d.type=?", filters.Type);
        if (filters.Source is not null) Add("EXISTS(SELECT 1 FROM indexed_document_sources s WHERE s.document_id=d.document_id AND s.source_id=?)", filters.Source);
        if (filters.PathPrefix is not null) Add("EXISTS(SELECT 1 FROM indexed_document_sources s WHERE s.document_id=d.document_id AND substr(s.path,1,length(?))=?)", filters.PathPrefix);
        if (filters.Mime is not null) Add("EXISTS(SELECT 1 FROM indexed_document_sources s WHERE s.document_id=d.document_id AND s.mime=?)", filters.Mime);
        foreach (var tag in filters.Tags ?? []) Add("EXISTS(SELECT 1 FROM effective_tags t WHERE t.document_id=d.document_id AND t.tag=?)", tag);
        foreach (var person in filters.People ?? [])
        {
            var value = "$f" + parameters.Count; parameters.Add((value, person.Value.ToLowerInvariant()));
            var sql = "EXISTS(SELECT 1 FROM people p WHERE p.document_id=d.document_id AND p.identifier=" + value;
            if (person.Role is not null) { var role = "$f" + parameters.Count; parameters.Add((role, person.Role)); sql += " AND p.role=" + role; }
            clauses.Add(sql + ")");
        }
        foreach (var (window, column) in new[] { (filters.Occurred, "occurred_at"), (filters.Updated, "updated_at") })
        {
            if (window?.From is not null) Add("d." + column + ">=?", NormalizeTime(window.From));
            if (window?.To is not null) Add("d." + column + "<?", NormalizeTime(window.To));
        }
        foreach (var predicate in filters.Predicates ?? [])
        {
            if (BaseFields.TryGetValue(predicate.Field, out var column))
            {
                if (predicate.Op == "exists") { clauses.Add(column + " IS NOT NULL"); continue; }
                if (predicate.Op == "in")
                {
                    if (predicate.Value.ValueKind != JsonValueKind.Array || predicate.Value.GetArrayLength() > 200) throw new ArgumentException("Invalid predicate.");
                    var names = new List<string>();
                    foreach (var item in predicate.Value.EnumerateArray()) { var name = "$f" + parameters.Count; parameters.Add((name, item.ValueKind == JsonValueKind.Number ? item.GetDouble() : Scalar(item))); names.Add(name); }
                    clauses.Add(names.Count == 0 ? "0" : column + " IN (" + string.Join(',', names) + ")"); continue;
                }
                var comparison = predicate.Op switch { "eq" => "=", "neq" => "<>", "lt" => "<", "lte" => "<=", "gt" => ">", "gte" => ">=", _ => throw new ArgumentException("Invalid predicate operator.") };
                Add(column + comparison + "?", predicate.Value.ValueKind == JsonValueKind.Number ? predicate.Value.GetDouble() : Scalar(predicate.Value));
                continue;
            }
            if (!IndexedFields.Contains(predicate.Field)) throw new ArgumentException("unindexed-property");
            var fieldParameter = "$f" + parameters.Count; parameters.Add((fieldParameter, predicate.Field));
            var sql = "EXISTS(SELECT 1 FROM indexed_props p WHERE p.document_id=d.document_id AND p.field=" + fieldParameter;
            if (predicate.Op == "exists") { clauses.Add(sql + ")"); continue; }
            if (predicate.Op == "in")
            {
                if (predicate.Value.ValueKind != JsonValueKind.Array || predicate.Value.GetArrayLength() > 200) throw new ArgumentException("Invalid predicate.");
                var names = new List<string>();
                foreach (var value in predicate.Value.EnumerateArray()) { var name = "$f" + parameters.Count; parameters.Add((name, Scalar(value))); names.Add(name); }
                clauses.Add(names.Count == 0 ? "0" : sql + " AND p.value_text IN (" + string.Join(',', names) + "))"); continue;
            }
            var op = predicate.Op switch { "eq" => "=", "neq" => "<>", "lt" => "<", "lte" => "<=", "gt" => ">", "gte" => ">=", _ => throw new ArgumentException("Invalid predicate operator.") };
            var valueParameter = "$f" + parameters.Count;
            var number = predicate.Value.ValueKind == JsonValueKind.Number;
            parameters.Add((valueParameter, number ? predicate.Value.GetDouble() : Scalar(predicate.Value)));
            clauses.Add(sql + " AND p." + (number ? "value_num" : "value_text") + op + valueParameter + ")");
        }
        return string.Join(" AND ", clauses);
    }
    private static readonly Dictionary<string, string> BaseFields = new(StringComparer.Ordinal) { ["id"] = "d.document_id", ["document_id"] = "d.document_id", ["title"] = "d.title", ["type"] = "d.type", ["status"] = "d.status", ["revision"] = "d.revision", ["occurred_at"] = "d.occurred_at", ["updated_at"] = "d.updated_at" };
    private static readonly HashSet<string> IndexedFields = new(new SecondBrain.Core.Domain.TypeRegistry().Types.Where(type => type.Key is "note" or "article" or "file").SelectMany(type => type.Metadata.IndexedProperties).Select(name => "props." + name), StringComparer.Ordinal);
    private static string NormalizeTime(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static string Scalar(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
    private const string Select = "SELECT c.id,c.document_id,c.revision,d.type,d.title,(SELECT path FROM indexed_document_sources WHERE document_id=d.document_id),c.context,c.text,d.occurred_at,d.updated_at,c.page,(SELECT group_concat(tag,char(31)) FROM effective_tags WHERE document_id=d.document_id),c.ordinal,c.rowid FROM chunks c JOIN indexed_documents d ON d.document_id=c.document_id";
    private static Candidate ReadCandidate(DbDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetInt32(10), reader.IsDBNull(11) ? [] : reader.GetString(11).Split((char)31));
    private static double Dot(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        var sum = Vector<float>.Zero; var position = 0;
        if (Vector.IsHardwareAccelerated)
            for (; position <= left.Length - Vector<float>.Count; position += Vector<float>.Count)
                sum += new Vector<float>(left.Slice(position)) * new Vector<float>(right.Slice(position));
        double result = Vector.Sum(sum);
        for (; position < left.Length; position++) result += (double)left[position] * right[position];
        return result;
    }
    private sealed class VectorTopCandidates
    {
        public SortedSet<VectorCandidate> Selected { get; } = new(VectorCandidateComparer.Instance);
        private readonly Dictionary<string, List<VectorCandidate>> perDocument = new(StringComparer.Ordinal);

        public void Add(string id, string documentId, double score)
        {
            if (!double.IsFinite(score)) return;
            if (Selected.Count == 50)
            {
                var worst = Selected.Max!;
                if (score < worst.Score || score == worst.Score && StringComparer.Ordinal.Compare(id, worst.ChunkId) >= 0) return;
            }
            var candidate = new VectorCandidate(id, documentId, score);
            if (perDocument.TryGetValue(documentId, out var existing) && existing.Count == 3)
            {
                var worst = existing.MaxBy(item => item, VectorCandidateComparer.Instance)!;
                if (VectorCandidateComparer.Instance.Compare(candidate, worst) >= 0) return;
                Remove(worst);
            }
            if (Selected.Count == 50) Remove(Selected.Max!);
            Selected.Add(candidate);
            if (!perDocument.TryGetValue(documentId, out var group)) perDocument[documentId] = group = [];
            group.Add(candidate);
        }
        private void Remove(VectorCandidate candidate)
        {
            Selected.Remove(candidate);
            var group = perDocument[candidate.DocumentId]; group.Remove(candidate);
            if (group.Count == 0) perDocument.Remove(candidate.DocumentId);
        }
    }
    private sealed record VectorCandidate(string ChunkId, string DocumentId, double Score);
    private sealed class VectorCandidateComparer : IComparer<VectorCandidate>
    {
        public static VectorCandidateComparer Instance { get; } = new();
        public int Compare(VectorCandidate? left, VectorCandidate? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1; if (right is null) return 1;
            var score = right.Score.CompareTo(left.Score);
            return score != 0 ? score : StringComparer.Ordinal.Compare(left.ChunkId, right.ChunkId);
        }
    }
    private static void AddLeg(IReadOnlyList<Candidate> leg, Dictionary<string, Candidate> fused)
    {
        for (var i = 0; i < leg.Count; i++) { var candidate = leg[i]; var score = 1d / (60 + i + 1); if (fused.TryGetValue(candidate.ChunkId, out var existing)) fused[candidate.ChunkId] = existing with { Score = existing.Score + score }; else fused[candidate.ChunkId] = candidate with { Score = score }; }
    }
    internal static string SanitizeFts(string query)
    {
        var terms = new List<string>();
        foreach (Match match in Regex.Matches(query, "\\\"[^\\\"]*\\\"|[\\p{L}\\p{N}_]+\\*?"))
        {
            var words = Regex.Matches(match.Value, "[\\p{L}\\p{N}_]+").Select(word => word.Value).ToArray();
            if (words.Length != 0) terms.Add("\"" + string.Join(' ', words) + "\"" + (match.Value.EndsWith('*') ? "*" : ""));
        }
        return string.Join(" OR ", terms);
    }
    private static HighlightRange[] MatchRanges(string text, string marked, string start, string end)
    {
        var plain = new StringBuilder(); var ranges = new List<HighlightRange>(); var position = 0;
        while (position < marked.Length)
        {
            var opening = marked.IndexOf(start, position, StringComparison.Ordinal);
            if (opening < 0) { plain.Append(marked.AsSpan(position)); break; }
            plain.Append(marked.AsSpan(position, opening - position));
            var closing = marked.IndexOf(end, opening + start.Length, StringComparison.Ordinal);
            if (closing < 0) return [];
            var from = plain.Length;
            plain.Append(marked.AsSpan(opening + start.Length, closing - opening - start.Length));
            if (ranges.Count < 200) ranges.Add(new(from, plain.Length));
            position = closing + end.Length;
        }
        return plain.ToString() == text ? ranges.ToArray() : [];
    }
    private static float[] Normalize(float[] vector)
    {
        if (vector.Length == 0 || vector.Any(value => !float.IsFinite(value))) throw new ArgumentException("Invalid query vector.");
        var magnitude = Math.Sqrt(vector.Sum(value => (double)value * value)); if (magnitude == 0) throw new ArgumentException("Zero vector.");
        return vector.Select(value => (float)(value / magnitude)).ToArray();
    }
    private static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object? Value)[] parameters) => SqliteDocumentRepository.Command(connection, transaction, sql, parameters);
    private sealed record Candidate(string ChunkId, string DocumentId, long Revision, string Type, string Title, string? Path, string Context, string Text, string? OccurredAt, string UpdatedAt, int? Page, string[] Tags, double Score = 0, HighlightRange[]? Highlights = null);
    // A cursor is bound to this data root's meta.instance_id; without a state store every cursor shares the empty instance.
    private async ValueTask<string> InstanceAsync(CancellationToken cancellationToken)
    {
        if (state is null) return "";
        await using var lease = await state.OpenReadConnectionAsync(cancellationToken);
        await using var read = Command(lease.Connection, null, "SELECT value FROM meta WHERE key='instance_id'");
        return await read.ExecuteScalarAsync(cancellationToken) as string ?? "";
    }
    private sealed record Cursor(string Instance, string Operation, string QueryHash, string CredentialId, long CredentialGeneration, long AccountEpoch, string Generation, string Position, string Id, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt);
}
