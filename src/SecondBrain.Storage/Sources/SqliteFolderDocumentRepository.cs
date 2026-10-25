using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Sources;
using SecondBrain.Storage.Ingestion;

namespace SecondBrain.Storage.Sources;

public sealed partial class SqliteFolderDocumentRepository(IStateStore state, IPublicationCoordinator publication,
    IDocumentRepository documents, IManagedDocumentFiles? files = null,
    Microsoft.Extensions.Options.IOptions<SecondBrain.Core.Configuration.SecondBrainOptions>? options = null,
    Microsoft.Extensions.Options.IOptionsMonitor<SecondBrain.Core.Configuration.SecondBrainOptions>? optionsMonitor = null) : IFolderDocumentRepository
{
    private const string Generation = "{\"chunker\":1,\"extractor\":1,\"normalization\":1}";
    private static string ConfigHash(SourceRecord source) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(source.Config))));
    public async ValueTask PersistPlanAsync(SourceRecord source, IReadOnlyList<FolderScanCandidate> candidates, CancellationToken cancellationToken = default)
    {
        if (candidates.Count > 500_000) throw new IngestionConflictException("source-entry-bound");
        await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await using var active = Command(connection, transaction, "SELECT 1 FROM sources WHERE id=$id AND status='active'", ("$id", source.Id));
            if (await active.ExecuteScalarAsync(token) is null) throw new IngestionConflictException("source-disabled");
            await using var clear = Command(connection, transaction, "DELETE FROM folder_scan_candidates WHERE source_id=$source", ("$source", source.Id)); await clear.ExecuteNonQueryAsync(token);
            var ordinal = 0; var hash = ConfigHash(source);
            foreach (var candidate in candidates)
            {
                await using var write = Command(connection, transaction, "INSERT INTO folder_scan_candidates(source_id,path,config_hash,candidate_json,ordinal,created_at) VALUES($source,$path,$hash,$candidate,$ordinal,$now)", ("$source", source.Id), ("$path", candidate.Candidate.Entry.RelativePath), ("$hash", hash), ("$candidate", JsonSerializer.Serialize(candidate)), ("$ordinal", ordinal++), ("$now", Now())); await write.ExecuteNonQueryAsync(token);
            }
            await using var ready = Command(connection, transaction, "UPDATE meta SET value=json_set(value,'$.Ready',json('true')) WHERE key=$key", ("$key", "folder-reconciliation:" + source.Id));
            await ready.ExecuteNonQueryAsync(token);
            return 0;
        }, cancellationToken);
    }
    public async ValueTask CompleteScanAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        var expired = new List<PublicationFence>();
        using (await SecondBrain.Storage.Durability.PublicationCoordinator.AcquirePublicationGateAsync(state, cancellationToken))
        await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await using var read = Command(connection, transaction, "SELECT value FROM meta WHERE key=$key AND NOT EXISTS(SELECT 1 FROM folder_scan_candidates WHERE source_id=$source)", ("$key", "folder-reconciliation:" + sourceId), ("$source", sourceId));
            if (await read.ExecuteScalarAsync(token) is not string json || JsonSerializer.Deserialize<Reconciliation>(json) is not { Ready: true } scan) return 0;
            foreach (var fence in scan.Absences)
            {
                await using var expire = Command(connection, transaction, "UPDATE documents SET status='removed_from_source',publish_state='publishing',updated_at=$now WHERE id=$id AND revision=$revision AND status='missing' AND source_id=$source AND EXISTS(SELECT 1 FROM sources WHERE id=$source AND status='active')", ("$id", fence.Id), ("$revision", fence.Revision), ("$source", sourceId), ("$now", Now()));
                if (await expire.ExecuteNonQueryAsync(token) == 1) expired.Add(new(fence.Id, fence.Revision, await ReadGenerationAsync(connection, transaction, fence.Id, token)));
            }
            await using var clear = Command(connection, transaction, "DELETE FROM meta WHERE key=$key", ("$key", "folder-reconciliation:" + sourceId));
            return await clear.ExecuteNonQueryAsync(token);
        }, cancellationToken);
        foreach (var fence in expired) await publication.PublishCurrentAsync(fence, cancellationToken);
    }
    public async ValueTask<IReadOnlyList<FolderScanCandidate>> PendingPlanAsync(SourceRecord source, int limit = 256, CancellationToken cancellationToken = default)
    {
        var candidates = new List<FolderScanCandidate>();
        await using var lease = await state.OpenReadConnectionAsync(cancellationToken);
        await using var read = Command(lease.Connection, null, "SELECT c.candidate_json FROM folder_scan_candidates c JOIN sources s ON s.id=c.source_id WHERE c.source_id=$source AND s.status='active' AND c.config_hash=$hash ORDER BY c.ordinal LIMIT $limit", ("$source", source.Id), ("$hash", ConfigHash(source)), ("$limit", Math.Clamp(limit, 1, 1024)));
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) candidates.Add(JsonSerializer.Deserialize<FolderScanCandidate>(reader.GetString(0))!);
        return candidates;
    }
    public async ValueTask CompleteCandidateAsync(string sourceId, string relativePath, CancellationToken cancellationToken = default)
    {
        await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await using var clear = Command(connection, transaction, "DELETE FROM folder_scan_candidates WHERE source_id=$source AND path=$path", ("$source", sourceId), ("$path", relativePath)); return await clear.ExecuteNonQueryAsync(token);
        }, cancellationToken);
    }
    public async ValueTask SetStatusAsync(string sourceId, string status, IngestionAuthority authority, CancellationToken cancellationToken = default)
    {
        if (status is not ("active" or "disabled" or "removed")) throw new ArgumentException("Unknown source status.", nameof(status));
        var fences = new List<PublicationFence>();
        using (await SecondBrain.Storage.Durability.PublicationCoordinator.AcquirePublicationGateAsync(state, cancellationToken))
        await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await SecondBrain.Storage.Auth.IngestionAuthorityValidator.ValidateAsync(connection, transaction, authority,
                SecondBrain.Core.Authorization.Scope.Admin, DateTimeOffset.UtcNow, sessionOnly: true, stepUpWindow: TimeSpan.FromMinutes(optionsMonitor?.CurrentValue.Auth.StepUpMinutes ?? options?.Value.Auth.StepUpMinutes ?? 10), cancellationToken: token);
            await using var change = Command(connection, transaction, "UPDATE sources SET status=$status WHERE id=$id AND kind='folder' AND status<>'removed'", ("$status", status), ("$id", sourceId));
            if (await change.ExecuteNonQueryAsync(token) != 1) throw new IngestionConflictException("not-found");
            if (status != "active")
            {
                await using var clearScan = Command(connection, transaction, "DELETE FROM folder_scan_candidates WHERE source_id=$source", ("$source", sourceId)); await clearScan.ExecuteNonQueryAsync(token);
            }
            if (status == "removed")
            {
                await using (var read = Command(connection, transaction, "SELECT d.id,d.revision,f.generation_json FROM documents d JOIN publication_fences f ON f.document_id=d.id WHERE d.source_id=$source AND d.status NOT IN ('deleted','purged')", ("$source", sourceId)))
                await using (var reader = await read.ExecuteReaderAsync(token))
                    while (await reader.ReadAsync(token)) fences.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
                await using var mark = Command(connection, transaction, "UPDATE documents SET status='removed_from_source',publish_state='publishing',updated_at=CASE WHEN status='removed_from_source' THEN updated_at ELSE $now END WHERE source_id=$source AND status NOT IN ('deleted','purged')", ("$source", sourceId), ("$now", Now())); await mark.ExecuteNonQueryAsync(token);
                await using var cancel = Command(connection, transaction, "UPDATE jobs SET status='cancelled',lease_until=NULL,updated_at=$now WHERE document_id IN (SELECT id FROM documents WHERE source_id=$source) AND status IN ('queued','running')", ("$source", sourceId), ("$now", Now())); await cancel.ExecuteNonQueryAsync(token);
            }
            return 0;
        }, cancellationToken);
        foreach (var fence in fences) await publication.PublishCurrentAsync(fence, cancellationToken);
    }
    public async ValueTask<SourceRecord> EnsureInboxAsync(string inboxPath, CancellationToken cancellationToken = default)
    {
        var configuration = new FolderSourceConfiguration(inboxPath, ["*"], [".claimed/**"], false, "import", true, null,
            new Dictionary<string, string>(), "poll", 20);
        var now = DateTimeOffset.UtcNow;
        await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await using var insert = Command(connection, transaction,
                "INSERT INTO sources(id,kind,name,config_json,status,created_at) VALUES('inbox','folder','inbox',$config,'active',$now) ON CONFLICT(id) DO UPDATE SET config_json=excluded.config_json",
                ("$config", JsonSerializer.Serialize(configuration)), ("$now", now.ToString("O", CultureInfo.InvariantCulture)));
            return await insert.ExecuteNonQueryAsync(token);
        }, cancellationToken);
        return new SourceRecord("inbox", "folder", "inbox", configuration, "active", null, null, now);
    }

    public async ValueTask<IReadOnlyList<FolderImportCandidate>> PlanScanAsync(SourceRecord source, IReadOnlyList<SourceFileEntry> entries, bool deep, CancellationToken cancellationToken = default)
    {
        if (entries.Count > 500000 || entries.Select(entry => entry.RelativePath).Distinct(StringComparer.Ordinal).Count() != entries.Count) throw new ArgumentException("Invalid bounded scan.");
        var existing = new Dictionary<string, Existing>(StringComparer.Ordinal);
        await using (var lease = await state.OpenReadConnectionAsync(cancellationToken))
        await using (var read = Command(lease.Connection, null,
            "SELECT o.path,o.document_id,d.revision,CASE WHEN d.content_hash='' THEN '' ELSE COALESCE(o.source_content_hash,d.content_hash) END,o.size_bytes,o.file_mtime,d.status,j.error,j.payload_json,j.generation_json FROM occurrences o JOIN documents d ON d.id=o.document_id LEFT JOIN jobs j ON j.document_id=o.document_id AND j.kind='stage' WHERE o.source_id=$source AND o.member_key=''", ("$source", source.Id)))
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) existing.TryAdd(reader.GetString(0), new Existing(reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reader.IsDBNull(4) ? 0 : reader.GetInt64(4), reader.IsDBNull(5) ? "" : reader.GetString(5), reader.GetString(6),
                reader.IsDBNull(7) ? null : StageRejection.Read(reader.GetString(7), reader.GetString(8), reader.GetString(9))));
        var candidates = new List<FolderImportCandidate>();
        var live = entries.Select(entry => entry.RelativePath).ToHashSet(StringComparer.Ordinal);
        // U17: an entry that exists but cannot be read is neither imported nor absent, nor is anything beneath it.
        var unreadable = entries.Where(entry => entry.Unreadable).Select(entry => entry.RelativePath).ToArray();
        bool Present(string path) => live.Contains(path) || unreadable.Any(prefix => path.StartsWith(prefix + "/", StringComparison.Ordinal));
        foreach (var entry in entries)
        {
            if (entry.Unreadable) continue;
            existing.TryGetValue(entry.RelativePath, out var old);
            // ING-7: a recorded deterministic rejection is not staged again until its entry or the generation changes;
            // a metadata-only document that went missing is re-recorded once its rejected entry is present again.
            if (old?.Rejected is { } recorded && recorded.Matches(entry) && !(old.Hash.Length == 0 && old.Status is DocumentStatusNames.Missing or DocumentStatusNames.RemovedFromSource)) continue;
            if (old is null) { candidates.Add(new FolderImportCandidate(entry, null, 0)); continue; }
            if (old.Status is "deleted" or "purged") continue;
            var changed = entry.Sha256 is not null ? entry.Sha256 != old.Hash : entry.Length != old.Length || entry.ModifiedAt.ToString("O", CultureInfo.InvariantCulture) != old.Modified;
            // A changed rejection is re-judged, and a metadata-only document is retried once its entry passes policy.
            if (changed || old.Status is DocumentStatusNames.Missing or DocumentStatusNames.RemovedFromSource || old.Rejected is not null || old.Hash.Length == 0 && entry.Rejection is null)
                candidates.Add(new FolderImportCandidate(entry, old.Id, old.Revision));
        }
        var missing = (source.Config.Mode == "import" ? Enumerable.Empty<KeyValuePair<string, Existing>>() : existing.AsEnumerable()).Where(pair => !Present(pair.Key) && pair.Value.Status is not (DocumentStatusNames.Deleted or DocumentStatusNames.Purged or DocumentStatusNames.RemovedFromSource)).ToArray();
        using var scanGate = await SecondBrain.Storage.Durability.PublicationCoordinator.AcquirePublicationGateAsync(state, cancellationToken);
        await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await using (var active = Command(connection, transaction, "SELECT 1 FROM sources WHERE id=$id AND status='active'", ("$id", source.Id)))
                if (await active.ExecuteScalarAsync(token) is null) throw new IngestionConflictException("source-disabled");
            foreach (var pair in missing)
            {
                if (pair.Value.Status == DocumentStatusNames.Missing) continue;
                await using var absence = Command(connection, transaction,
                    "INSERT INTO absences(document_id,source_id,path,since) VALUES($id,$source,$path,$now) ON CONFLICT(document_id,source_id,path,member_key) DO UPDATE SET since=excluded.since,cleared_at=NULL", ("$id", pair.Value.Id), ("$source", source.Id), ("$path", pair.Key), ("$now", Now())); await absence.ExecuteNonQueryAsync(token);
                await using var update = Command(connection, transaction, $"UPDATE documents SET status='{DocumentStatusNames.Missing}',publish_state='publishing' WHERE id=$id AND revision=$revision AND status NOT IN ('deleted','purged')", ("$id", pair.Value.Id), ("$revision", pair.Value.Revision)); await update.ExecuteNonQueryAsync(token);
            }
            var reconciliation = new Reconciliation(false, missing.Where(pair => pair.Value.Status == DocumentStatusNames.Missing)
                .Select(pair => new AbsenceFence(pair.Value.Id, pair.Value.Revision)).Distinct().ToArray());
            await using var remember = Command(connection, transaction, "INSERT INTO meta(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                ("$key", "folder-reconciliation:" + source.Id), ("$value", JsonSerializer.Serialize(reconciliation)));
            await remember.ExecuteNonQueryAsync(token);
            await using var scanned = Command(connection, transaction, "UPDATE sources SET last_scan_at=$now,last_full_scan_at=CASE WHEN $deep=1 THEN $now ELSE last_full_scan_at END WHERE id=$source", ("$now", Now()), ("$deep", deep ? 1 : 0), ("$source", source.Id));
            return await scanned.ExecuteNonQueryAsync(token);
        }, cancellationToken);
        scanGate.Dispose();
        foreach (var pair in missing) await publication.PublishCurrentAsync(new PublicationFence(pair.Value.Id, pair.Value.Revision, await ReadGenerationAsync(pair.Value.Id, cancellationToken)), cancellationToken);
        return candidates;
    }
    public async ValueTask<bool> RecordRejectionAsync(SourceRecord source, SourceEntryRejection rejection, CancellationToken cancellationToken = default)
    {
        using var sourceGate = await AcquireSourceGateAsync(source.Id, cancellationToken);
        await EnsureActiveAsync(source.Id, cancellationToken);
        if (rejection.RelativePath.Length is < 1 or > 4096) throw new ArgumentException("Invalid rejected entry path.", nameof(rejection));
        var reason = SourceEntryPolicy.ReasonOf(rejection.Reason);
        var modified = rejection.ModifiedAt.ToString("O", CultureInfo.InvariantCulture);
        var generation = SecondBrain.Storage.Durability.PublicationCoordinator.NormalizeGeneration(Generation);
        string? id = null; long revision = 0; var readable = false; string? title = null; string? type = null;
        await using (var lease = await state.OpenReadConnectionAsync(cancellationToken))
        {
            await using (var read = Command(lease.Connection, null,
                "SELECT d.id,d.revision,d.content_hash,d.status,d.title,d.type,j.error,j.payload_json,j.generation_json FROM occurrences o JOIN documents d ON d.id=o.document_id LEFT JOIN jobs j ON j.document_id=d.id AND j.kind='stage' WHERE o.source_id=$source AND o.path=$path AND o.member_key=''", ("$source", source.Id), ("$path", rejection.RelativePath)))
            await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
                if (await reader.ReadAsync(cancellationToken))
                {
                    if (reader.GetString(3) is DocumentStatusNames.Deleted or DocumentStatusNames.Purged) return false;
                    // Unchanged evidence: repeat scans and restarts neither restage nor duplicate the outcome.
                    if (!reader.IsDBNull(6) && (reader.GetString(2).Length != 0 || reader.GetString(3) is not (DocumentStatusNames.Missing or DocumentStatusNames.RemovedFromSource))
                        && StageRejection.Read(reader.GetString(6), reader.GetString(7), reader.GetString(8)) is var recorded && recorded.Reason == reason && recorded.Length == rejection.Length && recorded.Modified == modified && recorded.Generation == generation) return false;
                    id = reader.GetString(0); revision = reader.GetInt64(1); readable = reader.GetString(2).Length != 0; title = reader.GetString(4); type = reader.GetString(5);
                }
            await using var suppressed = Command(lease.Connection, null,
                "SELECT 1 FROM suppressions WHERE (scope='document' AND key=$document) OR (source_id=$source AND key=$path)", ("$document", id), ("$source", source.Id), ("$path", rejection.RelativePath));
            if (await suppressed.ExecuteScalarAsync(cancellationToken) is not null) return false;
        }
        var job = Ulid.NewUlid().ToString();
        var payload = JsonSerializer.Serialize(new { source_id = source.Id, path = rejection.RelativePath, reason, size_bytes = rejection.Length, modified_at = modified });
        if (readable)
        {
            // A readable document keeps serving its last admitted content; the rejection is recorded against it.
            return await state.QueueWriteAsync(async (connection, transaction, token) =>
            {
                await using var current = Command(connection, transaction, "SELECT 1 FROM documents d WHERE d.id=$id AND d.revision=$revision AND d.content_hash<>'' AND d.status NOT IN ('deleted','purged') AND EXISTS(SELECT 1 FROM sources WHERE id=$source AND status='active')", ("$id", id), ("$revision", revision), ("$source", source.Id));
                if (await current.ExecuteScalarAsync(token) is null) return false;
                await using var clear = Command(connection, transaction, "DELETE FROM jobs WHERE document_id=$id AND kind='stage'", ("$id", id)); await clear.ExecuteNonQueryAsync(token);
                await using var write = Command(connection, transaction,
                    "INSERT INTO jobs(id,kind,document_id,revision,generation_json,account_epoch,status,error,payload_json,created_at,updated_at) VALUES($job,'stage',$id,$revision,$generation,0,'failed',$error,$payload,$now,$now)",
                    ("$job", job), ("$id", id), ("$revision", revision), ("$generation", generation), ("$error", reason), ("$payload", payload), ("$now", Now()));
                await write.ExecuteNonQueryAsync(token);
                return true;
            }, cancellationToken);
        }
        // ING-6/7: an entry with no readable document is kept as a metadata-only one, findable by title and path.
        // Nothing was read, so it carries no content hash, admits no bytes and has no managed original.
        id ??= Ulid.NewUlid().ToString();
        var name = rejection.RelativePath.Split('/')[^1];
        title ??= source.Id == "inbox" && rejection.RelativePath.StartsWith(".claimed/", StringComparison.Ordinal) ? name.Split("--", 2)[^1] : name;
        var metadataOnly = new DocumentPublication(id, source.Id, revision, Generation, "source-reject", title, "", "folder:" + source.Name)
        {
            Type = type ?? source.Config.DefaultType ?? "file", TypeOrigin = "source", Status = reason == "input_bytes" ? "skipped" : "failed", Error = reason,
            SizeBytes = 0, Projection = TextProcessor.Process(id, title, ""),
            Admission = new PublicationAdmission(null, job, null, "", 0, "stage", payload, source.Id,
                new PublicationOccurrence(source.Id, rejection.RelativePath, rejection.ModifiedAt, rejection.Length), "failed", reason)
        };
        await publication.CheckAdmissionAsync(id, source.Id, metadataOnly.Admission, cancellationToken);
        return await publication.PublishAsync(metadataOnly, cancellationToken) == PublicationOutcome.Published;
    }
    public async ValueTask<CaptureResult?> AdmitSourceAsync(SourceRecord source, FolderImportCandidate candidate, byte[] bytes, string sha256, int hashOccurrences = 1, CancellationToken cancellationToken = default)
    {
        using var sourceGate = await AcquireSourceGateAsync(source.Id, cancellationToken);
        await EnsureActiveAsync(source.Id, cancellationToken);
        var profile = ChunkingProfile.FromOptions((optionsMonitor?.CurrentValue ?? options?.Value)?.Chunking);
        var Generation = profile.GenerationJson;
        if (files is null) throw new InvalidOperationException("Managed files not registered.");
        var entry = candidate.Entry;
        string? id = candidate.DocumentId; var revision = candidate.Revision; var settled = false; var excluded = false;
        await using (var lease = await state.OpenReadConnectionAsync(cancellationToken))
        {
            if (id is null)
            {
                await using var existing = Command(lease.Connection, null, "SELECT d.id,d.revision,COALESCE(o.source_content_hash,d.content_hash),d.status FROM occurrences o JOIN documents d ON d.id=o.document_id WHERE o.source_id=$source AND o.path=$path AND o.member_key=''", ("$source", source.Id), ("$path", entry.RelativePath));
                await using var existingReader = await existing.ExecuteReaderAsync(cancellationToken);
                if (await existingReader.ReadAsync(cancellationToken))
                {
                    id = existingReader.GetString(0); revision = existingReader.GetInt64(1);
                    settled = existingReader.GetString(2) == sha256 && !DocumentStatusNames.IsUnavailable(existingReader.GetString(3));
                }
                else if (source.Id == "inbox")
                {
                    // A valid replacement of a rejected inbox drop continues that drop's metadata-only document.
                    await using var rejected = Command(lease.Connection, null, "SELECT d.id,d.revision FROM occurrences o JOIN documents d ON d.id=o.document_id WHERE o.source_id=$source AND o.path=$path AND o.member_key='' AND d.content_hash='' AND d.status NOT IN ('deleted','purged')", ("$source", source.Id), ("$path", entry.RelativePath.Split('/')[^1].Split("--", 2)[^1]));
                    await using var rejectedReader = await rejected.ExecuteReaderAsync(cancellationToken);
                    if (await rejectedReader.ReadAsync(cancellationToken)) { id = rejectedReader.GetString(0); revision = rejectedReader.GetInt64(1); }
                }
            }
            await using var suppressed = Command(lease.Connection, null,
                "SELECT 1 FROM suppressions WHERE (scope='document' AND key=$document) OR (source_id=$source AND key=$path)", ("$document", id), ("$source", source.Id), ("$path", entry.RelativePath));
            excluded = !settled && await suppressed.ExecuteScalarAsync(cancellationToken) is not null;
            // Uniqueness belongs to the enumerated hash, not to different bytes read
            // later. Still admit a rewritten file, but do not guess a move identity.
            if (!settled && !excluded && id is null && hashOccurrences == 1 && entry.Sha256 == sha256)
            {
                var moves = new List<(string Id, long Revision)>();
                // U9: the reconciliation window is the open absence itself. It lasts while the old occurrence is
                // still `missing` and uncleared (FLD-6), independent of how long a checkpointed plan takes to drain.
                await using var move = Command(lease.Connection, null,
                    $"SELECT DISTINCT d.id,d.revision FROM documents d JOIN absences a ON a.document_id=d.id WHERE a.source_id=$source AND a.cleared_at IS NULL AND d.status='{DocumentStatusNames.Missing}' AND EXISTS(SELECT 1 FROM occurrences o WHERE o.document_id=d.id AND o.source_id=$source AND COALESCE(o.source_content_hash,d.content_hash)=$hash)", ("$source", source.Id), ("$hash", sha256));
                await using var reader = await move.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) moves.Add((reader.GetString(0), reader.GetInt64(1)));
                if (moves.Count == 1) { id = moves[0].Id; revision = moves[0].Revision; }
            }
        }
        if (settled || excluded)
        {
            // These bytes passed staging policy, which ends any recorded rejection even though nothing is published.
            await ClearRejectionAsync(id!, cancellationToken);
            return settled ? new CaptureResult((await documents.GetAsync(id!, true, cancellationToken))!, null) : null;
        }
        id ??= Ulid.NewUlid().ToString();
        var old = revision == 0 ? null : await documents.GetAsync(id, true, cancellationToken);
        string? sourceHash = null;
        var samePath = false;
        if (old is not null)
        {
            await using var sourceLease = await state.OpenReadConnectionAsync(cancellationToken);
            await using var sourceRead = Command(sourceLease.Connection, null,
                "SELECT COALESCE(source_content_hash,$fallback),path FROM occurrences WHERE document_id=$id AND source_id=$source AND member_key='' ORDER BY last_seen_at DESC LIMIT 1",
                ("$id", id), ("$source", source.Id), ("$fallback", old.ContentHash));
            await using var observed = await sourceRead.ExecuteReaderAsync(cancellationToken);
            if (await observed.ReadAsync(cancellationToken)) { sourceHash = observed.GetString(0); samePath = observed.GetString(1) == entry.RelativePath; }
            sourceHash ??= old.ContentHash;
        }
        if (old is not null && samePath && sourceHash == sha256 && !DocumentStatusNames.IsUnavailable(old.Status))
        {
            await ClearRejectionAsync(id, cancellationToken);
            await state.QueueWriteAsync(async (connection, transaction, token) =>
            {
                await using var observed = Command(connection, transaction, "UPDATE occurrences SET file_mtime=$mtime,size_bytes=$length,last_seen_at=$now WHERE document_id=$id AND source_id=$source AND path=$path",
                    ("$mtime", entry.ModifiedAt.ToString("O", CultureInfo.InvariantCulture)), ("$length", bytes.LongLength), ("$now", Now()), ("$id", id), ("$source", source.Id), ("$path", entry.RelativePath));
                return await observed.ExecuteNonQueryAsync(token);
            }, cancellationToken);
            return new CaptureResult(old, (await documents.JobsAsync(null, cancellationToken)).FirstOrDefault(job => job.DocumentId == id));
        }
        // A metadata-only revision has no managed original, so the first admitted bytes create one.
        var metadataOnly = old is not null && old.ContentHash.Length == 0;
        var dot = entry.RelativePath.LastIndexOf('.'); var extension = dot >= 0 && entry.RelativePath.Length - dot <= 16 && entry.RelativePath[(dot + 1)..].All(char.IsAsciiLetterOrDigit) ? entry.RelativePath[dot..] : ".bin";
        var managed = files.GetManagedPath(id, extension);
        var title = old?.Title ?? entry.RelativePath.Split('/')[^1];
        var type = old?.Type ?? source.Config.DefaultType ?? "file";
        TextProjection? retainedProjection = null;
        long? retainedSize = null;
        DocumentPublication? retainedMetadata = null;
        if (old is not null)
        {
            await using var lease = await state.OpenReadConnectionAsync(cancellationToken);
            await using (var read = Command(lease.Connection, null, "SELECT p.bytes FROM document_projections d JOIN payloads p ON p.id=d.payload_id WHERE d.document_id=$id AND p.redacted_at IS NULL", ("$id", id)))
                if (await read.ExecuteScalarAsync(cancellationToken) is byte[] snapshot) retainedProjection = JsonSerializer.Deserialize<TextProjection>(snapshot);
            await using var metadata = Command(lease.Connection, null, "SELECT type_origin,props_original_json,mime,source_url,claimed_client,occurred_at,occurred_offset,occurred_basis,occurred_precision,natural_key,type_confidence,schema_version,size_bytes FROM documents WHERE id=$id", ("$id", id));
            await using var reader = await metadata.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                retainedSize = reader.GetInt64(12);
                retainedMetadata = new DocumentPublication(id, source.Id, revision, Generation, "source-edit", title, sha256, old.Provenance)
            {
                TypeOrigin = reader.GetString(0), PropsOriginalJson = reader.GetString(1), Mime = Optional(2), SourceUrl = Optional(3), ClaimedClient = Optional(4),
                OccurredAt = Optional(5), OccurredOffset = Optional(6), OccurredBasis = Optional(7), OccurredPrecision = Optional(8), NaturalKey = Optional(9),
                TypeConfidence = reader.IsDBNull(10) ? null : reader.GetDouble(10), SchemaVersion = reader.GetInt32(11)
                };
            }
            string? Optional(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }
        var same = sourceHash == sha256 && retainedProjection is not null;
        if (same)
        {
            // A move keeps its existing projection generation. The chunking worker
            // can subsequently migrate it without losing cached PDF page metadata.
            Generation = await ReadGenerationAsync(id, cancellationToken);
            if (retainedMetadata is not null) retainedMetadata = retainedMetadata with { GenerationJson = Generation };
        }
        var projection = same ? retainedProjection! with { Path = managed } : profile.Process(id, title, "", old?.Tags, managed);
        var job = Ulid.NewUlid().ToString();
        var admitted = (retainedMetadata ?? new DocumentPublication(id, source.Id, revision, Generation, "source-admit", title, sha256, "folder:" + source.Name) { TypeOrigin = "source" }) with
        {
            Type = type, ContentHash = same ? old!.ContentHash : sha256, PropsJson = old?.PropsJson ?? "{}", Status = same ? "indexed_partial" : "queued", Projection = projection,
            SizeBytes = same ? retainedSize ?? bytes.LongLength : bytes.LongLength,
            Admission = new PublicationAdmission(null, job, null, sha256, bytes.LongLength, same ? "embed" : "extract", JsonSerializer.Serialize(new { path = managed, file_name = entry.RelativePath, admitted_bytes = bytes.LongLength, claim_root = source.Id == "inbox" ? source.Config.Path : null, claim_path = source.Id == "inbox" ? entry.RelativePath : null, claim_original_name = source.Id == "inbox" ? entry.RelativePath.Split("--", 2)[^1] : null }), source.Id, new PublicationOccurrence(source.Id, entry.RelativePath, entry.ModifiedAt, bytes.LongLength))
        };
        // ING-4: admission precedes the vault write; a fresh original is discarded if publication still rejects it.
        await publication.CheckAdmissionAsync(id, source.Id, admitted.Admission, cancellationToken);
        if (!same || old!.Path != managed)
        {
            var storedBytes = bytes;
            if (same && old!.ContentHash != sha256 && old.Path is not null)
            {
                await using var original = await files.OpenReadAsync(old.Path, cancellationToken);
                using var copy = new MemoryStream(); await original.CopyToAsync(copy, cancellationToken); storedBytes = copy.ToArray();
                if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(storedBytes)) != old.ContentHash) throw new IngestionConflictException("file-changed");
            }
            await files.WriteAsync(id, storedBytes, extension, metadataOnly || old?.Path != managed ? null : old?.ContentHash, cancellationToken);
        }
        PublicationOutcome outcome;
        try { outcome = await publication.PublishAsync(admitted, cancellationToken); }
        catch
        {
            if (old is null || metadataOnly) await files.DiscardUnpublishedAsync(managed, CancellationToken.None);
            throw;
        }
        if (outcome != PublicationOutcome.Published)
        {
            if (old is null || metadataOnly) await files.DiscardUnpublishedAsync(managed, CancellationToken.None);
            return null;
        }
        return new CaptureResult((await documents.GetAsync(id, true, cancellationToken))!, new ProcessingJob(job, id, revision + 1, "queued", 0));
    }
    private async ValueTask ClearRejectionAsync(string? id, CancellationToken cancellationToken)
    {
        if (id is null) return;
        await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await using var clear = Command(connection, transaction, "DELETE FROM jobs WHERE document_id=$id AND kind='stage'", ("$id", id)); return await clear.ExecuteNonQueryAsync(token);
        }, cancellationToken);
    }
    private async ValueTask<string> ReadGenerationAsync(string id, CancellationToken token)
    {
        await using var lease = await state.OpenReadConnectionAsync(token);
        return await ReadGenerationAsync(lease.Connection, null, id, token);
    }
    private static async ValueTask<string> ReadGenerationAsync(DbConnection connection, DbTransaction? transaction, string id, CancellationToken token)
    {
        await using var read = Command(connection, transaction, "SELECT generation_json FROM publication_fences WHERE document_id=$id", ("$id", id));
        return await read.ExecuteScalarAsync(token) as string ?? Generation;
    }
    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    private static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object? Value)[] parameters) => SqliteDocumentRepository.Command(connection, transaction, sql, parameters);
    private sealed record AbsenceFence(string Id, long Revision);
    private sealed record Reconciliation(bool Ready, AbsenceFence[] Absences);
    private sealed record Existing(string Id, long Revision, string Hash, long Length, string Modified, string Status, StageRejection? Rejected);
    private sealed record StageRejection(string Reason, long Length, string Modified, string Generation)
    {
        public static StageRejection Read(string reason, string payload, string generation)
        {
            using var parsed = JsonDocument.Parse(payload);
            var root = parsed.RootElement;
            return new(reason, root.TryGetProperty("size_bytes", out var length) && length.TryGetInt64(out var value) ? value : -1,
                root.TryGetProperty("modified_at", out var modified) ? modified.GetString() ?? "" : "", generation);
        }
        public bool Matches(SourceFileEntry entry) => entry.Rejection == Reason && entry.Length == Length
            && entry.ModifiedAt.ToString("O", CultureInfo.InvariantCulture) == Modified && Generation == SecondBrain.Storage.Durability.PublicationCoordinator.NormalizeGeneration(SqliteFolderDocumentRepository.Generation);
    }
}
