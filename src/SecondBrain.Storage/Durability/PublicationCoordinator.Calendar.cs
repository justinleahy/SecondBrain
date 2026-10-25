using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using SecondBrain.Core.Domain;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Ingestion;
using SecondBrain.Extraction.Contracts;
using SecondBrain.Core.Sources;

namespace SecondBrain.Storage.Durability;

public sealed partial class PublicationCoordinator
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IStateStore, SemaphoreSlim> CalendarGates = new();

    public ValueTask<PublicationOutcome> PublishCalendarAsync(PublicationFence container, CalendarSnapshot calendar, CancellationToken cancellationToken = default) =>
        PublishCalendarCoreAsync(container, calendar, null, cancellationToken);

    public ValueTask<PublicationOutcome> PublishCalendarSubscriptionAsync(CalendarSubscriptionPublication publication, CancellationToken cancellationToken = default) =>
        PublishCalendarCoreAsync(new(publication.Work.ContainerId, 0, new ChunkingProfile().GenerationJson), publication.Calendar, publication, cancellationToken);

    private async ValueTask<PublicationOutcome> PublishCalendarCoreAsync(PublicationFence container, CalendarSnapshot calendar, CalendarSubscriptionPublication? subscription, CancellationToken cancellationToken)
    {
        calendar.Validate();
        var window = calendar.Window.Validate();
        calendar = calendar with
        {
            Window = new(window.From.ToString("O", CultureInfo.InvariantCulture), window.To.ToString("O", CultureInfo.InvariantCulture), window.Zone.Id, calendar.Window.MaxOccurrences),
            Instances = calendar.Instances.Select(item => item with
            {
                StartsAt = DateTimeOffset.Parse(item.StartsAt, CultureInfo.InvariantCulture).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                EndsAt = DateTimeOffset.Parse(item.EndsAt, CultureInfo.InvariantCulture).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            }).ToArray()
        };
        var gate = CalendarGates.GetValue(state, static _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        var held = new List<IDisposable>();
        try
        {
            held.Add(await locks.AcquireAsync(container.DocumentId, cancellationToken));
            var existing = await CalendarMembersAsync(container.DocumentId, cancellationToken);
            var identities = existing.ToDictionary(item => item.MemberKey, item => item.Id, StringComparer.Ordinal);
            foreach (var member in calendar.Members)
                identities.TryAdd(CalendarKey(member.Uid, member.RecurrenceId), Ulid.NewUlid().ToString());
            foreach (var id in identities.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                held.Add(await locks.AcquireAsync(id, cancellationToken));
            using var publicationGate = await AcquirePublicationGateAsync(state, cancellationToken);
            if (await CalendarBatchPendingAsync(container.DocumentId, cancellationToken))
                return await CompleteCalendarAsync(container.DocumentId, cancellationToken);
            var staged = await StageCalendarAsync(container, calendar, identities, existing, subscription, cancellationToken);
            if (!staged) return PublicationOutcome.Superseded;
            await crashPoints.HitAsync(CrashPointNames.AfterStatePublishing, cancellationToken);
            return await CompleteCalendarAsync(container.DocumentId, cancellationToken);
        }
        finally
        {
            foreach (var item in held.AsEnumerable().Reverse()) item.Dispose();
            gate.Release();
        }
    }

    private async ValueTask<bool> StageCalendarAsync(PublicationFence container, CalendarSnapshot calendar,
        Dictionary<string, string> identities, IReadOnlyList<CalendarMemberState> existing, CalendarSubscriptionPublication? subscription, CancellationToken token)
    {
        return await state.QueueWriteAsync(async (connection, transaction, ct) =>
        {
            if (await connection.ExecuteScalarAsync<long>(Sql("""
                SELECT COUNT(*) FROM meta WHERE key='mutation_document_reservation:'||@id OR key IN
                  (SELECT 'mutation_document_reservation:'||document_id FROM calendar_members WHERE container_id=@id)
                """, new { id = container.DocumentId }, transaction, ct)) != 0) return false;
            if (subscription is not null)
            {
                var prepared = await PrepareSubscriptionRootAsync(connection, transaction, subscription, ct);
                if (prepared is null) return false;
                container = prepared;
            }
            else if (!await FenceMatchesAsync(connection, transaction, container, ct)) return false;
            var root = await connection.QuerySingleAsync<CalendarRoot>(Sql("""
                SELECT d.source_id SourceId,d.provenance Provenance,d.status Status,p.bytes Bytes
                FROM documents d JOIN document_projections dp ON dp.document_id=d.id JOIN payloads p ON p.id=dp.payload_id
                JOIN sources s ON s.id=d.source_id
                WHERE d.id=@id AND p.redacted_at IS NULL AND (s.kind NOT IN ('folder','calendar_subscription') OR s.status='active' OR @localOnly=1 AND s.kind='calendar_subscription' AND s.status='disabled')
                """, new { id = container.DocumentId, localOnly = subscription?.Work.MaterializationOnly == true ? 1 : 0 }, transaction, ct));
            if (DocumentStatusNames.IsUnavailable(root.Status)) return false;
            var rootProjection = JsonSerializer.Deserialize<TextProjection>(root.Bytes) ?? throw new InvalidDataException("calendar_projection");
            var occurrencePath = subscription is null ? rootProjection.Path ?? container.DocumentId : container.DocumentId;
            var previous = await connection.QuerySingleOrDefaultAsync<CalendarBatch>(Sql("SELECT * FROM calendar_batches WHERE container_id=@id", new { id = container.DocumentId }, transaction, ct));
            var epoch = await connection.ExecuteScalarAsync<long>(Sql("SELECT COALESCE(MAX(materialization_generation),0)+1 FROM calendar_batches", null, transaction, ct));
            var profile = optionsMonitor is null ? ChunkingProfile.FromGeneration(container.GenerationJson) : ChunkingProfile.FromOptions(optionsMonitor.CurrentValue.Chunking);
            var generationFields = JsonSerializer.Deserialize<SortedDictionary<string, JsonElement>>(profile.ApplyToGeneration(container.GenerationJson))!;
            generationFields["calendar"] = JsonSerializer.SerializeToElement(1);
            generationFields["calendar_materialization"] = JsonSerializer.SerializeToElement(epoch);
            var generation = JsonSerializer.Serialize(generationFields);
            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var currentByKey = existing.ToDictionary(item => item.MemberKey, StringComparer.Ordinal);
            var sourceHashes = calendar.Members.ToDictionary(member => CalendarKey(member.Uid, member.RecurrenceId), member =>
                Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(member))), StringComparer.Ordinal);
            if (previous is not null && previous.phase == "published" && previous.container_revision == container.Revision && previous.covered_from == calendar.Window.From && previous.covered_to == calendar.Window.To && previous.time_zone == calendar.Window.TimeZone && ChunkingProfile.FromGeneration(previous.generation_json) == profile &&
                sourceHashes.All(pair => currentByKey.TryGetValue(pair.Key, out var prior) && prior.SourceHash == pair.Value && prior.Status != "removed_from_source") &&
                existing.All(item => sourceHashes.ContainsKey(item.MemberKey) || DocumentStatusNames.IsUnavailable(item.Status)))
            {
                await connection.ExecuteAsync(Sql("UPDATE jobs SET status='completed',lease_until=NULL,updated_at=@now WHERE document_id=@id AND revision=@revision AND kind='extract' AND status IN ('queued','running')", new { id = container.DocumentId, revision = container.Revision, now }, transaction, ct));
                if (subscription is not null) await FinishSubscriptionAsync(connection, transaction, container.DocumentId, ct);
                return true;
            }
            await connection.ExecuteAsync(Sql("""
                INSERT INTO calendar_batches(container_id,container_revision,generation_json,phase,source_id,covered_from,covered_to,time_zone,materialization_generation)
                VALUES(@id,@revision,@generation,'publishing',@source,@from,@to,@zone,@epoch)
                ON CONFLICT(container_id) DO UPDATE SET container_revision=excluded.container_revision,generation_json=excluded.generation_json,phase='publishing',source_id=excluded.source_id,
                  covered_from=excluded.covered_from,covered_to=excluded.covered_to,time_zone=excluded.time_zone,materialization_generation=excluded.materialization_generation;
                DELETE FROM calendar_batch_members WHERE container_id=@id;
                UPDATE documents SET status='processing',mime='text/calendar',type='file',error=NULL,publish_state='publishing' WHERE id=@id;
                """, new { id = container.DocumentId, revision = container.Revision, generation, source = root.SourceId, from = calendar.Window.From, to = calendar.Window.To, zone = calendar.Window.TimeZone, epoch }, transaction, ct));
            await SaveCalendarProjectionAsync(connection, transaction, container.DocumentId, container.Revision, generation,
                rootProjection with { LosslessText = "", SearchText = "", OffsetMap = [0], Chunks = [], CalendarInstances = null, CalendarGeneration = null }, now, ct);
            await CalendarMarkerAsync(connection, transaction, container.DocumentId, container.DocumentId, container.Revision, generation, ct);

            var registry = new TypeRegistry();
            foreach (var member in calendar.Members)
            {
                var key = CalendarKey(member.Uid, member.RecurrenceId); var id = identities[key];
                currentByKey.TryGetValue(key, out var prior);
                // Purge and user deletion are retained tombstones, never imported again implicitly.
                if (prior?.Status is "purged" or "deleted")
                {
                    await connection.ExecuteAsync(Sql("UPDATE calendar_members SET source_hash=@hash WHERE document_id=@id; UPDATE absences SET cleared_at=@now WHERE document_id=@id AND cleared_at IS NULL;", new { id, hash = sourceHashes[key], now }, transaction, ct));
                    continue;
                }
                var changed = prior is null || prior.SourceHash != sourceHashes[key] || prior.Status == "removed_from_source";
                var revision = prior is null ? 1 : prior.Revision + (changed ? 1 : 0);
                var decisions = (await connection.QueryAsync<CalendarOverride>(Sql("SELECT kind Kind,value_json Value FROM overrides WHERE document_id=@id AND key=''", new { id }, transaction, ct)))
                    .ToDictionary(item => item.Kind, item => JsonSerializer.Deserialize<JsonElement>(item.Value), StringComparer.Ordinal);
                var type = decisions.TryGetValue("type", out var chosenType) ? chosenType.GetString()! : "calendar_event";
                var title = decisions.TryGetValue("title", out var chosenTitle) ? chosenTitle.GetString()! : member.Title;
                var original = JsonSerializer.Serialize(member.Properties);
                var authored = member.Properties.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
                if (decisions.TryGetValue("props", out var propsOverride))
                    foreach (var property in propsOverride.EnumerateObject()) authored[property.Name] = property.Value.Clone();
                if (member.RecurrenceId.Length != 0 && identities.TryGetValue(CalendarKey(member.Uid, ""), out var master)) authored["master_id"] = JsonSerializer.SerializeToElement(master);
                var props = registry.DeriveEffectiveProperties(type, JsonSerializer.SerializeToElement(authored));
                var tags = decisions.TryGetValue("tags", out var chosenTags) ? chosenTags.Deserialize<string[]>() ?? [] : [];
                var projection = profile.Process(id, title, member.Text, tags, rootProjection.Path);
                var occurrences = type == "calendar_event" ? calendar.Instances.Where(item => item.Uid == member.Uid && item.RecurrenceId == member.RecurrenceId).ToArray() : [];
                projection = projection with { CalendarInstances = occurrences, CalendarGeneration = epoch, People = type == "calendar_event" ? member.People : null,
                    Aliases = decisions.TryGetValue("aliases", out var chosenAliases) ? chosenAliases.Deserialize<string[]>() : null };
                var allDay = props.TryGetProperty("all_day", out var day) && day.ValueKind == JsonValueKind.True;
                var occurred = props.TryGetProperty("starts_at", out var start) ? start.GetString() : null;
                await connection.ExecuteAsync(Sql("""
                    INSERT INTO documents(id,source_id,type,type_origin,props_json,props_original_json,title,mime,provenance,occurred_at,occurred_basis,occurred_precision,natural_key,content_hash,size_bytes,revision,publish_state,status,created_at,updated_at,container_document_id)
                    VALUES(@id,@source,@type,@origin,@props,@original,@title,'text/calendar',@provenance,@occurred,'starts_at',@precision,@natural,@hash,@size,@revision,'publishing','indexed_partial',@now,@now,@container)
                    ON CONFLICT(id) DO UPDATE SET type=excluded.type,type_origin=excluded.type_origin,props_json=excluded.props_json,props_original_json=excluded.props_original_json,title=excluded.title,
                      occurred_at=excluded.occurred_at,occurred_basis=excluded.occurred_basis,occurred_precision=excluded.occurred_precision,content_hash=excluded.content_hash,size_bytes=excluded.size_bytes,
                      revision=excluded.revision,publish_state='publishing',status='indexed_partial',error=NULL,updated_at=CASE WHEN @changed THEN @now ELSE documents.updated_at END;
                    INSERT INTO calendar_members(container_id,member_key,document_id,source_hash) VALUES(@container,@key,@id,@sourceHash)
                    ON CONFLICT(container_id,member_key) DO UPDATE SET source_hash=excluded.source_hash;
                    INSERT INTO occurrences(document_id,source_id,path,container_document_id,member_key,last_seen_at) VALUES(@id,@source,@path,@container,@key,@now)
                    ON CONFLICT(source_id,path,member_key) DO UPDATE SET last_seen_at=excluded.last_seen_at;
                    UPDATE absences SET cleared_at=@now WHERE document_id=@id AND cleared_at IS NULL;
                    UPDATE jobs SET status='cancelled',lease_until=NULL,updated_at=@now WHERE document_id=@id AND status IN ('queued','running');
                    """, new { id, source = root.SourceId, type, origin = decisions.ContainsKey("type") ? "user" : "format", props = props.GetRawText(), original, title,
                    provenance = root.Provenance, occurred, precision = allDay ? "date" : "datetime", natural = "calendar_event:" + key,
                    hash = changed ? sourceHashes[key] : prior!.ContentHash, sourceHash = sourceHashes[key], size = 0, revision, now, container = container.DocumentId, key, path = occurrencePath, changed }, transaction, ct));
                if (changed)
                    await connection.ExecuteAsync(Sql("INSERT INTO revisions(document_id,revision,cause,content_hash,created_at) VALUES(@id,@revision,@cause,@hash,@now)", new { id, revision, cause = prior is null ? "ingest" : "import", hash = sourceHashes[key], now }, transaction, ct));
                await SaveCalendarProjectionAsync(connection, transaction, id, revision, generation, projection, now, ct);
                await CalendarMarkerAsync(connection, transaction, container.DocumentId, id, revision, generation, ct);
                await connection.ExecuteAsync(Sql("INSERT INTO jobs(id,kind,document_id,revision,generation_json,account_epoch,status,created_at,updated_at) VALUES(@job,'embed',@id,@revision,@generation,COALESCE((SELECT CAST(value AS INTEGER) FROM meta WHERE key='account_epoch'),0),'queued',@now,@now)", new { job = Ulid.NewUlid().ToString(), id, revision, generation, now }, transaction, ct));
            }
            foreach (var prior in existing.Where(item => !sourceHashes.ContainsKey(item.MemberKey) && !DocumentStatusNames.IsUnavailable(item.Status)))
            {
                var revision = prior.Revision + 1;
                await connection.ExecuteAsync(Sql("""
                    UPDATE documents SET revision=@revision,status='removed_from_source',publish_state='publishing',updated_at=@now WHERE id=@id;
                    INSERT INTO revisions(document_id,revision,cause,content_hash,created_at) SELECT id,@revision,'source-absence',content_hash,@now FROM documents WHERE id=@id;
                    INSERT INTO absences(document_id,source_id,path,member_key,since) VALUES(@id,@source,@path,@key,@now)
                    ON CONFLICT(document_id,source_id,path,member_key) DO UPDATE SET since=excluded.since,cleared_at=NULL;
                    UPDATE document_projections SET revision=@revision,generation_json=@generation WHERE document_id=@id;
                    UPDATE jobs SET status='cancelled',lease_until=NULL,updated_at=@now WHERE document_id=@id AND status IN ('queued','running');
                    """, new { id = prior.Id, revision, now, source = root.SourceId, path = occurrencePath, key = prior.MemberKey, generation }, transaction, ct));
                await WriteGenerationAsync(connection, transaction, prior.Id, generation, ct);
                await CalendarMarkerAsync(connection, transaction, container.DocumentId, prior.Id, revision, generation, ct);
            }
            var limits = optionsMonitor?.CurrentValue.Limits.Global ?? initialLimits;
            var counts = await connection.QuerySingleAsync<CalendarCounts>(Sql("SELECT COUNT(*) Files,COALESCE(SUM(size_bytes),0) Bytes,(SELECT COUNT(*) FROM jobs WHERE status IN ('queued','running')) Jobs,(SELECT COUNT(*) FROM documents WHERE source_id=@source AND status NOT IN ('deleted','purged')) SourceFiles FROM documents WHERE status NOT IN ('deleted','purged')", new { source = root.SourceId }, transaction, ct));
            if (counts.Files > limits.Files || counts.SourceFiles > limits.FilesPerSource || counts.Jobs > limits.QueuedJobs || counts.Bytes > limits.AdmittedGb * 1_000_000_000m) throw new IngestionConflictException("capacity");
            return true;
        }, token);
    }

    private async ValueTask<PublicationOutcome> CompleteCalendarAsync(string containerId, CancellationToken token)
    {
        CalendarBatch? batch; List<PublicationFence> fences; string? lifecycle;
        await using (var lease = await state.OpenReadConnectionAsync(token))
        {
            batch = await lease.Connection.QuerySingleOrDefaultAsync<CalendarBatch>(Sql("SELECT * FROM calendar_batches WHERE container_id=@id", new { id = containerId }, null, token));
            if (batch is null) return PublicationOutcome.Superseded;
            if (batch.phase == "published") return PublicationOutcome.Published;
            lifecycle = await lease.Connection.ExecuteScalarAsync<string?>(Sql("SELECT operation FROM calendar_lifecycle_batches WHERE container_id=@id", new { id = containerId }, null, token));
            var sourceStatus = await lease.Connection.ExecuteScalarAsync<string?>(Sql("SELECT status FROM sources WHERE id=@id AND kind IN ('folder','calendar_subscription')", new { id = batch.source_id }, null, token));
            var localOnly = await lease.Connection.ExecuteScalarAsync<long>(Sql("SELECT COUNT(*) FROM calendar_subscription_publications WHERE container_id=@id AND materialization_only=1", new { id = containerId }, null, token)) != 0;
            if (lifecycle is null && sourceStatus is not (null or "active" or "removed") && !(sourceStatus == "disabled" && localOnly)) return PublicationOutcome.Superseded;
            fences = (await lease.Connection.QueryAsync<CalendarFence>(Sql("SELECT document_id Id,revision Revision,generation_json Generation FROM calendar_batch_members WHERE container_id=@id ORDER BY document_id", new { id = containerId }, null, token)))
                .Select(item => new PublicationFence(item.Id, item.Revision, item.Generation)).ToList();
        }
        var registry = SecondBrain.Storage.Retrieval.VectorSnapshotRegistry.For(index);
        var statuses = new Dictionary<string, string?>(StringComparer.Ordinal);
        using (await registry.EnterAsync(token))
        {
            await index.QueueWriteAsync(async (connection, transaction, ct) =>
            {
                foreach (var fence in fences)
                {
                    var snapshot = await ReadSnapshotAsync(fence.DocumentId, ct);
                    if (snapshot is null || snapshot.Fence != fence) throw new IngestionConflictException("calendar-fence");
                    if (fence.DocumentId == containerId && snapshot.Status == "processing") snapshot = snapshot with { Status = "indexed" };
                    if (IsRemoved(snapshot.Status))
                        await connection.ExecuteAsync(Sql("DELETE FROM indexed_documents WHERE document_id=@id", new { id = fence.DocumentId }, transaction, ct));
                    else
                        await connection.ExecuteAsync(Sql("""
                            INSERT INTO indexed_documents(document_id,revision,type,title,props_json,occurred_at,occurred_precision,ends_at,status,updated_at)
                            VALUES(@id,@revision,@type,@title,@props,@occurred,@precision,@ends,@status,@updated)
                            ON CONFLICT(document_id) DO UPDATE SET revision=excluded.revision,type=excluded.type,title=excluded.title,props_json=excluded.props_json,
                              occurred_at=excluded.occurred_at,occurred_precision=excluded.occurred_precision,ends_at=excluded.ends_at,status=excluded.status,updated_at=excluded.updated_at
                            """, new { id = fence.DocumentId, revision = fence.Revision, type = snapshot.Type, title = snapshot.Title, props = snapshot.PropsJson,
                            occurred = snapshot.OccurredAt, precision = snapshot.OccurredPrecision, ends = snapshot.EndsAt, status = snapshot.Status, updated = snapshot.UpdatedAt }, transaction, ct));
                    await ReplaceProjectionsAsync(connection, transaction, snapshot, ct);
                    await connection.ExecuteAsync(Sql("INSERT INTO indexed_document_generations(document_id,generation_json) VALUES(@id,@generation) ON CONFLICT(document_id) DO UPDATE SET generation_json=excluded.generation_json", new { id = fence.DocumentId, generation = fence.GenerationJson }, transaction, ct));
                    statuses[fence.DocumentId] = await connection.ExecuteScalarAsync<string?>(Sql("SELECT status FROM indexed_documents WHERE document_id=@id", new { id = fence.DocumentId }, transaction, ct));
                }
                if (lifecycle is null or "restore") await connection.ExecuteAsync(Sql("""
                    INSERT INTO indexed_calendar_coverage(container_id,source_id,covered_from,covered_to,time_zone,materialization_generation)
                    VALUES(@id,@source,@from,@to,@zone,@epoch) ON CONFLICT(container_id) DO UPDATE SET source_id=excluded.source_id,covered_from=excluded.covered_from,
                      covered_to=excluded.covered_to,time_zone=excluded.time_zone,materialization_generation=excluded.materialization_generation
                    """, new { id = containerId, source = batch.source_id, from = batch.covered_from, to = batch.covered_to, zone = batch.time_zone, epoch = batch.materialization_generation }, transaction, ct));
                else await connection.ExecuteAsync(Sql("DELETE FROM indexed_calendar_coverage WHERE container_id=@id", new { id = containerId }, transaction, ct));
                return 0;
            }, token);
            foreach (var fence in fences) registry.Committed(fence.DocumentId);
        }
        await crashPoints.HitAsync(CrashPointNames.AfterIndexCommit, token);
        await state.QueueWriteAsync(async (connection, transaction, ct) =>
        {
            foreach (var fence in fences)
            {
                if (!await FenceMatchesAsync(connection, transaction, fence, ct)) throw new IngestionConflictException("calendar-fence");
                await connection.ExecuteAsync(Sql("UPDATE documents SET indexed_revision=revision,publish_state='published',status=CASE WHEN @status IS NULL THEN status ELSE @status END WHERE id=@id", new { id = fence.DocumentId, status = statuses[fence.DocumentId] }, transaction, ct));
                if (statuses[fence.DocumentId] == "indexed")
                    await connection.ExecuteAsync(Sql("UPDATE jobs SET status='completed',lease_until=NULL WHERE document_id=@id AND revision=@revision AND kind='embed' AND status IN ('queued','running')", new { id = fence.DocumentId, revision = fence.Revision }, transaction, ct));
            }
            await connection.ExecuteAsync(Sql("""
                INSERT INTO calendar_coverage(container_id,source_id,covered_from,covered_to,time_zone,materialization_generation)
                SELECT @id,@source,@from,@to,@zone,@epoch WHERE @keepCoverage=1
                ON CONFLICT(container_id) DO UPDATE SET covered_from=excluded.covered_from,covered_to=excluded.covered_to,time_zone=excluded.time_zone,materialization_generation=excluded.materialization_generation;
                UPDATE calendar_batches SET phase='published' WHERE container_id=@id;
                DELETE FROM calendar_batch_members WHERE container_id=@id;
                DELETE FROM calendar_lifecycle_batches WHERE container_id=@id;
                UPDATE jobs SET status='completed',lease_until=NULL,updated_at=@now WHERE document_id=@id AND revision=@revision AND kind='extract' AND status IN ('queued','running');
                """, new { id = containerId, source = batch.source_id, from = batch.covered_from, to = batch.covered_to, zone = batch.time_zone, epoch = batch.materialization_generation, revision = batch.container_revision, now = DateTimeOffset.UtcNow.ToString("O"), keepCoverage = lifecycle == "purge" ? 0 : 1 }, transaction, ct));
            if (lifecycle == "purge") await connection.ExecuteAsync(Sql("DELETE FROM calendar_coverage WHERE container_id=@id", new { id = containerId }, transaction, ct));
            await FinishSubscriptionAsync(connection, transaction, containerId, ct);
            return 0;
        }, token);
        if (lifecycle == "purge")
            foreach (var fence in fences) await SecondBrain.Storage.Ingestion.PurgeCleanup.CompleteAsync(state, index, fence.DocumentId, token);
        return PublicationOutcome.Published;
    }

    public async ValueTask<PublicationOutcome> RecoverCalendarAsync(string id, CancellationToken token = default)
    {
        var held = new List<IDisposable>();
        try
        {
            held.Add(await locks.AcquireAsync(id, token));
            foreach (var member in await CalendarMembersAsync(id, token)) held.Add(await locks.AcquireAsync(member.Id, token));
            using var gate = await AcquirePublicationGateAsync(state, token);
            return await CompleteCalendarAsync(id, token);
        }
        finally { foreach (var item in held.AsEnumerable().Reverse()) item.Dispose(); }
    }

    private async ValueTask<int> RecoverCalendarsAsync(CancellationToken token)
    {
        string[] ids;
        await using (var lease = await state.OpenReadConnectionAsync(token))
            ids = (await lease.Connection.QueryAsync<string>(Sql("SELECT container_id FROM calendar_batches WHERE phase='publishing' ORDER BY container_id", null, null, token))).ToArray();
        var result = 0;
        foreach (var id in ids)
            if (await RecoverCalendarAsync(id, token) == PublicationOutcome.Published) result++;
        return result;
    }

    private async ValueTask<IReadOnlyList<CalendarMemberState>> CalendarMembersAsync(string id, CancellationToken token)
    {
        await using var lease = await state.OpenReadConnectionAsync(token);
        return (await lease.Connection.QueryAsync<CalendarMemberState>(Sql("SELECT m.member_key MemberKey,m.document_id Id,m.source_hash SourceHash,d.content_hash ContentHash,d.revision Revision,d.status Status FROM calendar_members m JOIN documents d ON d.id=m.document_id WHERE m.container_id=@id ORDER BY m.document_id", new { id }, null, token))).ToArray();
    }
    private async ValueTask<bool> CalendarBatchPendingAsync(string id, CancellationToken token)
    {
        await using var lease = await state.OpenReadConnectionAsync(token);
        return await lease.Connection.ExecuteScalarAsync<long>(Sql("SELECT COUNT(*) FROM calendar_batches WHERE container_id=@id AND phase='publishing'", new { id }, null, token)) != 0;
    }
    private static async ValueTask<bool> CalendarOwnsAsync(DbConnection connection, DbTransaction transaction, string id, CancellationToken token) =>
        await connection.ExecuteScalarAsync<long>(Sql("""
            SELECT CASE WHEN EXISTS(SELECT 1 FROM calendar_batch_members m JOIN calendar_batches b ON b.container_id=m.container_id WHERE m.document_id=@id AND b.phase='publishing')
              OR EXISTS(SELECT 1 FROM documents d JOIN meta m ON m.key='mutation_document_reservation:'||d.container_document_id WHERE d.id=@id)
              OR EXISTS(SELECT 1 FROM documents d JOIN calendar_source_purges p ON p.source_id=d.source_id WHERE d.id=@id AND p.completed_at IS NULL) THEN 1 ELSE 0 END
            """, new { id }, transaction, token)) != 0;

    private async ValueTask<bool> HasCalendarLifecycleAsync(string id, CancellationToken token)
    {
        await using var lease = await state.OpenReadConnectionAsync(token);
        return await lease.Connection.ExecuteScalarAsync<long>(Sql("SELECT COUNT(*) FROM calendar_lifecycle_batches WHERE container_id=@id", new { id }, null, token)) != 0;
    }
    private async ValueTask<PublicationOutcome> CompleteCalendarWithRootHeldAsync(string id, CancellationToken token)
    {
        var held = new List<IDisposable>();
        try
        {
            foreach (var member in await CalendarMembersAsync(id, token)) held.Add(await locks.AcquireAsync(member.Id, token));
            using var gate = await AcquirePublicationGateAsync(state, token);
            return await CompleteCalendarAsync(id, token);
        }
        finally { foreach (var item in held.AsEnumerable().Reverse()) item.Dispose(); }
    }
    private static async Task SaveCalendarProjectionAsync(DbConnection connection, DbTransaction transaction, string id, long revision, string generation, TextProjection projection, string now, CancellationToken token)
    {
        var payload = Ulid.NewUlid().ToString(); var bytes = JsonSerializer.SerializeToUtf8Bytes(projection);
        await connection.ExecuteAsync(Sql("""
            INSERT INTO payloads(id,sha256,bytes,document_refs_json,created_at) VALUES(@payload,@hash,@bytes,@refs,@now);
            INSERT INTO payload_refs(payload_id,referrer_kind,referrer_id) VALUES(@payload,'document-projection',@id);
            INSERT INTO document_projections(document_id,revision,generation_json,payload_id) VALUES(@id,@revision,@generation,@payload)
            ON CONFLICT(document_id) DO UPDATE SET revision=excluded.revision,generation_json=excluded.generation_json,payload_id=excluded.payload_id;
            """, new { id, revision, generation, payload, bytes, hash = Convert.ToHexStringLower(SHA256.HashData(bytes)), refs = JsonSerializer.Serialize(new[] { id }), now }, transaction, token));
        await WriteGenerationAsync(connection, transaction, id, generation, token);
    }
    private static Task CalendarMarkerAsync(DbConnection connection, DbTransaction transaction, string container, string id, long revision, string generation, CancellationToken token) =>
        connection.ExecuteAsync(Sql("INSERT INTO calendar_batch_members(container_id,document_id,revision,generation_json) VALUES(@container,@id,@revision,@generation)", new { container, id, revision, generation }, transaction, token));
    private static CommandDefinition Sql(string sql, object? value, DbTransaction? transaction, CancellationToken token) => new(sql, value, transaction, cancellationToken: token);
    private static string CalendarKey(string uid, string recurrence) => JsonSerializer.Serialize(new[] { uid, recurrence });
    private static async Task ApplyCalendarMemberMutationAsync(DbConnection connection, DbTransaction transaction, DocumentPublication publication, string now, CancellationToken token)
    {
        var id = publication.DocumentId;
        foreach (var item in publication.Overrides ?? new Dictionary<string, string>())
            await connection.ExecuteAsync(Sql("INSERT INTO overrides(document_id,kind,key,value_json,created_at) VALUES(@id,@kind,'',@value,@now) ON CONFLICT(document_id,kind,key) DO UPDATE SET value_json=excluded.value_json,created_at=excluded.created_at",
                new { id, kind = item.Key, value = item.Value, now }, transaction, token));
        if (publication.Cause is "delete" or "purge" && publication.Expiry?.Status != "removed_from_source")
            await connection.ExecuteAsync(Sql("INSERT OR IGNORE INTO suppressions(id,scope,source_id,key,reason,created_at) VALUES(@sid,'document',@source,@id,'app-delete',@now)",
                new { id, sid = Ulid.NewUlid().ToString(), source = publication.SourceId, now }, transaction, token));
        else if (publication.Cause == "restore")
            await connection.ExecuteAsync(Sql("DELETE FROM suppressions WHERE scope='document' AND key=@id", new { id }, transaction, token));
        if (publication.Cause == "purge")
            await connection.ExecuteAsync(Sql("""
                UPDATE payloads SET bytes=X'',sha256='',redacted_at=@now WHERE EXISTS(SELECT 1 FROM json_each(document_refs_json) WHERE value=@id);
                UPDATE documents SET title='[purged]',props_json='{}',props_original_json='{}',content_hash='',source_url=NULL,claimed_client=NULL,error=NULL WHERE id=@id;
                UPDATE jobs SET status='cancelled',payload_json='{}',error=NULL WHERE document_id=@id;
                DELETE FROM overrides WHERE document_id=@id;
                """, new { id, now }, transaction, token));
    }
    private sealed class CalendarRoot { public string SourceId { get; set; } = ""; public string Provenance { get; set; } = ""; public string Status { get; set; } = ""; public byte[] Bytes { get; set; } = []; }
    private sealed class CalendarMemberState { public string MemberKey { get; set; } = ""; public string Id { get; set; } = ""; public string SourceHash { get; set; } = ""; public string ContentHash { get; set; } = ""; public long Revision { get; set; } public string Status { get; set; } = ""; }
    private sealed class CalendarOverride { public string Kind { get; set; } = ""; public string Value { get; set; } = ""; }
    private sealed class CalendarFence { public string Id { get; set; } = ""; public long Revision { get; set; } public string Generation { get; set; } = ""; }
    private sealed class CalendarCounts { public long Files { get; set; } public long Bytes { get; set; } public long Jobs { get; set; } public long SourceFiles { get; set; } }
    private sealed class CalendarBatch
    {
        public long container_revision { get; set; } public string generation_json { get; set; } = ""; public string phase { get; set; } = "";
        public string source_id { get; set; } = ""; public string covered_from { get; set; } = ""; public string covered_to { get; set; } = "";
        public string time_zone { get; set; } = ""; public long materialization_generation { get; set; }
    }
}
