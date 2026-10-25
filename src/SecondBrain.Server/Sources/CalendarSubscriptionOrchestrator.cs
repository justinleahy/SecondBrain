using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Extraction;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Limits;
using SecondBrain.Core.Sources;
using SecondBrain.Extraction.Contracts;
using SecondBrain.Infrastructure.Configuration;

namespace SecondBrain.Server.Sources;

public sealed class CalendarSubscriptionOrchestrator(ICalendarSubscriptionRepository subscriptions, ICalendarSubscriptionSecrets secrets,
    ICalendarDownloadTransport transport, IExtractorClient extractor, IManagedDocumentFiles files, IPublicationCoordinator publications,
    IOptionsMonitor<SecondBrainOptions> options, TimeProvider clock, IPersistentAdmissionAccounting accounting,
    ILogger<CalendarSubscriptionOrchestrator> logger, ConfiguredCalendarSubscriptions configured, ReloadingConfiguration configuration) : BackgroundService
{
    private readonly SemaphoreSlim wake = new(0, 4);
    private long nextRecovery;
    public void Notify() { try { wake.Release(); } catch (SemaphoreFullException) { } }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await configured.InitializeAsync(cancellationToken);
        configuration.Validating += ValidateConfiguration;
        await base.StartAsync(cancellationToken);
    }

    private void ValidateConfiguration(SecondBrainOptions candidate) => configured.ValidateAsync(candidate).AsTask().GetAwaiter().GetResult();

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        configuration.Validating -= ValidateConfiguration;
        await base.StopAsync(cancellationToken);
    }

    public async Task ProcessAsync(CalendarRefreshWork work, CancellationToken token = default)
    {
        try
        {
            var window = CalendarMaterialization.Window(options.CurrentValue, clock.GetUtcNow());
            if (work.MaterializationOnly)
            {
                if (work.SnapshotPath is null || work.SnapshotHash is null) throw new CalendarSubscriptionException("calendar_snapshot_missing");
                await using var original = await files.OpenReadAsync(work.SnapshotPath, token);
                var parsed = await extractor.ExtractCalendarAsync(original, window, token);
                if (!Valid(parsed)) { await subscriptions.FailAsync(work, "calendar_parse_failed", token: token); return; }
                if (await publications.PublishCalendarSubscriptionAsync(new(work, parsed.Calendar!, work.SnapshotPath, work.SnapshotHash, original.Length, work.ETag, work.Modified), token) != PublicationOutcome.Published)
                    await subscriptions.FailAsync(work, "calendar_publication_failed", token: token);
                await accounting.RefreshAsync(token);
                return;
            }
            var url = secrets.Unprotect(work.ProtectedUrl);
            var downloaded = await transport.DownloadAsync(new(work.SourceId, work.Generation, url, work.ETag, work.Modified), token);
            if (downloaded.Status == "not_modified" && work.SnapshotHash is null)
                downloaded = await transport.DownloadAsync(new(work.SourceId, work.Generation, url), token);
            if (downloaded.Status == "not_modified")
            {
                if (work.SnapshotHash is null) await subscriptions.FailAsync(work, "calendar_snapshot_missing", token: token);
                else await subscriptions.CompleteUnchangedAsync(work, token);
                return;
            }
            if (downloaded.Status != "modified" || downloaded.Snapshot is null)
            {
                await subscriptions.FailAsync(work, downloaded.Error ?? "calendar_transport", downloaded.RetryAfterSeconds, token: token);
                return;
            }
            await using var staged = downloaded.Snapshot;
            var processing = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            { parser = "ical.net:5.2.3:calendar:1", window, chunking = ChunkingProfile.FromOptions(options.CurrentValue.Chunking).GenerationJson }))));
            if (!work.Deep && work.RejectedHash == staged.Sha256 && work.RejectedGeneration == processing)
            { await subscriptions.FailAsync(work, "calendar_rejected_unchanged", token: token); return; }
            var extraction = await extractor.ExtractCalendarAsync(staged.Content, window, token);
            if (!Valid(extraction))
            { await subscriptions.FailAsync(work, "calendar_parse_failed", rejectedHash: staged.Sha256, processingGeneration: processing, token: token); return; }
            string path;
            if (staged.Sha256 == work.SnapshotHash && work.SnapshotPath is not null) path = work.SnapshotPath;
            else
            {
                await publications.CheckAdmissionAsync(work.ContainerId, work.SourceId,
                    new(null, work.LeaseId, null, "", staged.Length, TrustedSourceId: work.SourceId), token);
                var bytes = new byte[checked((int)staged.Length)]; staged.Content.Position = 0;
                await staged.Content.ReadExactlyAsync(bytes, token);
                path = await files.WriteCalendarSnapshotAsync(work.ContainerId, work.LeaseId, bytes, token);
            }
            if (await publications.PublishCalendarSubscriptionAsync(new(work, extraction.Calendar!, path, staged.Sha256, staged.Length, downloaded.ETag, downloaded.Modified), token) != PublicationOutcome.Published)
                await subscriptions.FailAsync(work, "calendar_publication_failed", token: token);
            await accounting.RefreshAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (CryptographicException) { await subscriptions.FailAsync(work, "calendar_secret_invalid", token: token); }
        catch (NotSupportedException) { await subscriptions.FailAsync(work, "calendar_publication_failed", token: token); }
        catch (CalendarSubscriptionException error) { await subscriptions.FailAsync(work, error.Code, token: token); }
        catch (IngestionConflictException error) { await subscriptions.FailAsync(work, error.Code == "capacity" ? "calendar_capacity" : "calendar_publication_failed", token: token); }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException)
        { await subscriptions.FailAsync(work, "calendar_publication_failed", token: token); }
    }

    private static bool Valid(ExtractionResult result)
    {
        if (result.Status != "extracted" || result.PhysicalFormat != "calendar" || result.Calendar is null) return false;
        try { result.Calendar.Validate(); return true; }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or TimeZoneNotFoundException) { return false; }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(Enumerable.Range(0, 4).Select(_ => RunAsync(stoppingToken)));
    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var now = clock.GetUtcNow().UtcTicks; var due = Interlocked.Read(ref nextRecovery);
                if (now >= due && Interlocked.CompareExchange(ref nextRecovery, now + TimeSpan.FromSeconds(30).Ticks, due) == due)
                {
                    await publications.RecoverAsync(token);
                    await subscriptions.RecoverPurgesAsync(token);
                    await files.CleanupCalendarSnapshotsAsync(token);
                }
                var pending = await subscriptions.ClaimDueAsync(1, token);
                if (pending.Count == 0) pending = await subscriptions.ClaimMaterializationAsync(CalendarMaterialization.Window(options.CurrentValue, clock.GetUtcNow()), 1, token);
                if (pending.Count > 0) { await ProcessAsync(pending[0], token); continue; }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or IngestionConflictException or CalendarSubscriptionException)
            { logger.LogWarning("Calendar subscription work remains pending; retry will use its durable state."); }
            try { await wake.WaitAsync(TimeSpan.FromSeconds(2), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }
}
