using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Sources;
using SecondBrain.Server.Ingestion;

namespace SecondBrain.Server.Sources;

/// <summary>Resumable scans use descriptor-backed adapters and persistent reconciliation state.</summary>
public sealed class FolderProcessingOrchestrator(ISourceRepository sources, IFolderSourceFiles files,
    IFolderDocumentRepository documents, IInputStager stager, FileProcessingOrchestrator processing,
    TimeProvider clock, IInboxClaims claims, IOptionsMonitor<SecondBrainOptions> options, ILogger<FolderProcessingOrchestrator> logger) : BackgroundService
{
    private readonly SemaphoreSlim scanGate = new(1, 1);
    private readonly object eventGate = new();
    private readonly Dictionary<(string Source, string Path), DateTimeOffset> dirty = new();
    private readonly Dictionary<string, Task> watchers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> nextScan = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> nextDeep = new(StringComparer.Ordinal);
    private readonly HashSet<(string Source, string Path, string Reason)> rejected = new();

    public async Task<int> ScanAsync(SourceRecord source, bool deep, CancellationToken token, IReadOnlySet<string>? changedPaths = null)
    {
        if (source.Status != "active") return 0;
        await scanGate.WaitAsync(token);
        try
        {
            if (source.Id == "inbox") return await ScanInboxAsync(source, token);
            var admitted = await DrainPlanAsync(source, token);
            await documents.CompleteScanAsync(source.Id, token);
            var entries = new List<SourceFileEntry>();
            await foreach (var entry in files.ScanAsync(source.Config, deep, token)) entries.Add(entry);
            var candidates = await documents.PlanScanAsync(source, entries, deep, token);
            // A possible move needs hashes for every live occurrence, including unchanged siblings.
            var hashes = entries;
            if (!deep && candidates.Any(candidate => candidate.DocumentId is null))
            {
                hashes = [];
                await foreach (var entry in files.ScanAsync(source.Config, true, token)) hashes.Add(entry);
            }
            var counts = hashes.Where(entry => entry.Sha256 is not null).GroupBy(entry => entry.Sha256!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var hashesByPath = hashes.ToDictionary(entry => entry.RelativePath, entry => entry.Sha256, StringComparer.Ordinal);
            await documents.PersistPlanAsync(source, candidates
                .Where(candidate => changedPaths is null || changedPaths.Contains(candidate.Entry.RelativePath))
                .Select(candidate =>
                {
                    var hash = hashesByPath.GetValueOrDefault(candidate.Entry.RelativePath);
                    // Persist the exact hash whose uniqueness was counted, including the
                    // deep hash pass used to qualify a possible shallow-scan move.
                    return new FolderScanCandidate(candidate with { Entry = candidate.Entry with { Sha256 = hash } },
                        hash is not null && counts.TryGetValue(hash, out var occurrences) ? Math.Max(1, occurrences) : 1);
                }).ToArray(), token);
            admitted += await DrainPlanAsync(source, token);
            await documents.CompleteScanAsync(source.Id, token);
            processing.Notify();
            return admitted;
        }
        finally { scanGate.Release(); }
    }

    private async Task<int> DrainPlanAsync(SourceRecord source, CancellationToken token)
    {
        var admitted = 0;
        while (true)
        {
            var pending = await documents.PendingPlanAsync(source, 256, token);
            if (pending.Count == 0) return admitted;
            foreach (var work in pending)
            {
                var candidate = work.Candidate;
                StagedInput? input = null;
                try { input = await stager.OpenSourceAsync(source.Config.Path, candidate.Entry.RelativePath, token); }
                catch (IOException) { logger.LogWarning("A source entry could not be staged; a later scan will retry it."); }
                catch (InvalidDataException exception) { await RejectEntryAsync(source, candidate.Entry, exception, token); }
                if (input is not null)
                    try
                    {
                        await using (input)
                        {
                            var bytes = new byte[checked((int)input.Length)];
                            await input.Content.ReadExactlyAsync(bytes, token);
                            if (await documents.AdmitSourceAsync(source, candidate, bytes, input.Sha256, work.HashOccurrences, token) is not null) admitted++;
                        }
                    }
                    catch (IOException) { logger.LogWarning("A source entry could not be staged; a later scan will retry it."); }
                    catch (InvalidDataException) { logger.LogWarning("A source entry could not be admitted; a later scan will retry it."); }
                    catch (IngestionConflictException) { logger.LogInformation("A source entry changed during reconciliation; a later scan will retry it."); }
                await documents.CompleteCandidateAsync(source.Id, candidate.Entry.RelativePath, token);
                processing.Notify();
            }
        }
    }

    private async Task<int> ScanInboxAsync(SourceRecord source, CancellationToken token)
    {
        // Claim originals first; rewritten original names become distinct entries on the next scan.
        await foreach (var entry in files.ScanAsync(source.Config, false, token))
        {
            if (entry.Unreadable) continue;
            // Policy-invalid entries (oversized, linked, nonregular) stay unclaimed and visible in the inbox.
            if (entry.Rejection is not null) { await RejectEntryAsync(source, entry, new InvalidDataException(entry.Rejection), token); continue; }
            try { await claims.ClaimAsync(source.Config.Path, entry.RelativePath, token); }
            catch (IOException) { logger.LogWarning("An inbox entry could not be claimed; it remains available for retry."); }
            catch (InvalidDataException exception) { await RejectEntryAsync(source, entry, exception, token); }
        }
        var entries = new List<SourceFileEntry>();
        await foreach (var claim in claims.EnumerateAsync(source.Config.Path, token))
        {
            try
            {
                await using var input = await stager.OpenSourceAsync(source.Config.Path, claim.RelativeClaimPath, token);
                entries.Add(new SourceFileEntry(claim.RelativeClaimPath, input.Length, DateTimeOffset.UnixEpoch, input.Sha256));
            }
            catch (IOException) { logger.LogWarning("An inbox claim could not be staged; it remains visible for retry."); }
            catch (InvalidDataException exception) { await RejectEntryAsync(source, new SourceFileEntry(claim.RelativeClaimPath, 0, DateTimeOffset.UnixEpoch, null), exception, token); }
        }
        var candidates = await documents.PlanScanAsync(source, entries, true, token);
        var admitted = 0;
        foreach (var candidate in candidates)
        {
            // Each claim succeeds or stays visibly claimed on its own; one entry never stops the others.
            try
            {
                await using var input = await stager.OpenSourceAsync(source.Config.Path, candidate.Entry.RelativePath, token);
                var bytes = new byte[checked((int)input.Length)];
                await input.Content.ReadExactlyAsync(bytes, token);
                if (await documents.AdmitSourceAsync(source, candidate, bytes, input.Sha256, 0, token) is not null) admitted++;
            }
            catch (IOException) { logger.LogWarning("An inbox claim could not be staged; it remains visible for retry."); }
            catch (InvalidDataException exception) { await RejectEntryAsync(source, candidate.Entry, exception, token); }
            catch (IngestionConflictException) { logger.LogInformation("An inbox claim changed during admission; it remains visible for retry."); }
        }
        processing.Notify();
        return admitted;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var inboxPath = await claims.EnsureAsync(options.CurrentValue.Sources.IncomingRoot, stoppingToken);
        await documents.EnsureInboxAsync(inboxPath, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var nextWakeAt = DateTimeOffset.MaxValue;
            try
            {
                foreach (var source in await sources.ListAsync(stoppingToken))
                {
                    if (source.Status != "active" || source.Kind is not ("folder" or "inbox")) continue;
                    var now = clock.GetUtcNow();
                    if (source.Config.Watch == "events" && !watchers.ContainsKey(source.Id)) watchers.Add(source.Id, WatchAsync(source, stoppingToken));
                    HashSet<string> ready;
                    lock (eventGate)
                    {
                        ready = dirty.Where(entry => entry.Key.Source == source.Id && now - entry.Value >= TimeSpan.FromMilliseconds(1500))
                            .Select(entry => entry.Key.Path).ToHashSet(StringComparer.Ordinal);
                        foreach (var path in ready) dirty.Remove((source.Id, path));
                    }
                    var changed = ready.Count > 0;
                    var due = !nextScan.TryGetValue(source.Id, out var shallowAt) || shallowAt <= now;
                    var deep = nextDeep.TryGetValue(source.Id, out var deepAt) && deepAt <= now;
                    if (changed || due || deep)
                    {
                        try { await ScanAsync(source, deep, stoppingToken, changed && !due && !deep && !ready.Contains("") && !ready.Any(path => Path.GetFileName(path) == ".brainignore") ? ready : null); }
                        // Source-controlled content (ignore rules, entry counts, policy rejections) fails only its own scan.
                        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or InvalidDataException or IngestionConflictException)
                        { logger.LogWarning("Source {Source} scan could not complete ({Reason}); it will be retried at the next interval.", source.Id, ex is InvalidDataException or IngestionConflictException ? ex.Message : ex.GetType().Name); }
                        nextScan[source.Id] = now.AddSeconds(source.Config.Watch == "poll" ? source.Config.PollIntervalSeconds : 600);
                        nextDeep[source.Id] = deep || !nextDeep.ContainsKey(source.Id) ? DeepRescanSchedule.Next(now, options.CurrentValue.Ingest.DeepRescanAt, TimeZoneInfo.TryFindSystemTimeZoneById(options.CurrentValue.Time.Zone, out var zone) ? zone : TimeZoneInfo.Local) : nextDeep[source.Id];
                    }
                    // Only active sources contribute deadlines; removed sources must not create a busy loop.
                    if (nextScan[source.Id] < nextWakeAt) nextWakeAt = nextScan[source.Id];
                    if (nextDeep[source.Id] < nextWakeAt) nextWakeAt = nextDeep[source.Id];
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            // Keep the event/source-registration backstop, but do not sleep past a pending poll.
            var delay = nextWakeAt - clock.GetUtcNow();
            if (delay > TimeSpan.FromMilliseconds(250)) delay = TimeSpan.FromMilliseconds(250);
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            await Task.Delay(delay, clock, stoppingToken);
        }
        await Task.WhenAll(watchers.Values);
    }

    private async Task RejectEntryAsync(SourceRecord source, SourceFileEntry entry, InvalidDataException exception, CancellationToken token)
    {
        // The entry is left in place and not imported; its named outcome is durable and visible through jobs (ING-6/7).
        var reason = SourceEntryPolicy.ReasonOf(exception);
        var rejection = SourceEntryPolicy.TryGetEvidence(exception, out var length, out var modified)
            ? new SourceEntryRejection(entry.RelativePath, reason, length, modified)
            : new SourceEntryRejection(entry.RelativePath, reason, entry.Length, entry.ModifiedAt);
        string? failure = null;
        try { if (!await documents.RecordRejectionAsync(source, rejection, token)) return; }
        catch (IngestionConflictException conflict) { failure = conflict.Code; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException) { failure = ex.GetType().Name; }
        // A rejection that cannot be recorded (capacity, disabled source) is retried by later scans; report it once per process.
        lock (rejected)
        {
            if (rejected.Count >= 10000) rejected.Clear();
            if (failure is not null && !rejected.Add((source.Id, entry.RelativePath, reason))) return;
        }
        if (failure is null) logger.LogInformation("Source {Source} entry was rejected by staging policy ({Reason}); it is left in place and recorded.", source.Id, reason);
        else logger.LogWarning("Source {Source} entry was rejected by staging policy ({Reason}) but could not be recorded ({Failure}); a later scan will retry it.", source.Id, reason, failure);
    }

    private async Task WatchAsync(SourceRecord source, CancellationToken token)
    {
        try
        {
            await foreach (var path in files.WatchAsync(source.Config, token))
                lock (eventGate)
                {
                    if (dirty.Count >= 100000)
                    {
                        dirty.Clear();
                        dirty[(source.Id, "")] = clock.GetUtcNow();
                    }
                    else dirty[(source.Id, path)] = clock.GetUtcNow();
                }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException) { logger.LogWarning("Source events unavailable; periodic scans continue."); }
    }
}
