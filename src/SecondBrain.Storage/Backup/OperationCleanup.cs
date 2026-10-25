using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Operations;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Durability;

namespace SecondBrain.Storage.Backup;

public sealed partial class OperationCleanup(IStateStore state, IOptions<SecondBrainOptions> options, TimeProvider clock,
    IOptionsMonitor<SecondBrainOptions>? monitor = null) : IOperationCleanup
{
    private SecondBrainOptions Current => monitor?.CurrentValue ?? options.Value;
    public async Task<OperationCleanupResult> RunAsync(IngestionAuthority? actor = null, CancellationToken token = default)
    {
        if (actor is not null) await Authorize(actor, token);
        var root = Path.GetFullPath(Current.DataRoot);
        var workspaces = 0; var logs = 0; var deferred = 0; var more = false;
        var staging = Path.Combine(root, "staging");
        if (Directory.Exists(staging))
        {
            new ManagedMutationFiles(staging, requireSafeRoot: true).ValidateRoot();
            foreach (var directory in Directory.EnumerateDirectories(staging))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var owned = OperationWorkspace.TryClaim(directory, out _);
                    if (owned is null) continue;
                    if (workspaces >= 16) { more = true; break; }
                    if (actor is not null) await Authorize(actor, token);
                    owned.Delete(); workspaces++;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                { deferred++; }
            }
        }
        var directoryPath = Path.Combine(root, "logs");
        if (Directory.Exists(directoryPath))
        {
            new ManagedMutationFiles(directoryPath, requireSafeRoot: true).ValidateRoot();
            var cutoff = clock.GetUtcNow().AddDays(-Current.Retention.DebugLogDays);
            foreach (var path in Directory.EnumerateFiles(directoryPath, "debug-*.jsonl"))
            {
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path);
                if (!DebugName().IsMatch(name) || !DateTimeOffset.TryParseExact(name.Substring(6, 23),
                    "yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var created)
                    || created >= cutoff || File.GetLastWriteTimeUtc(path) >= cutoff.UtcDateTime) continue;
                if (logs >= 64) { more = true; break; }
                try
                {
                    using var parent = new ManagedMutationFiles(root, requireSafeRoot: true).Open(path, "debug-retention");
                    using var read = parent.OpenSnapshotRead();
                    if (!OperationWorkspace.TryLock(read.Stream.SafeFileHandle)) continue;
                    if (actor is not null) await Authorize(actor, token);
                    if (!read.Unchanged) { deferred++; continue; }
                    File.Delete(path); logs++;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                { deferred++; }
            }
        }
        return new(workspaces, logs, deferred, more);
    }
    private async Task Authorize(IngestionAuthority actor, CancellationToken token) =>
        await state.QueueWriteAsync(async (c, t, ct) =>
        {
            await IngestionAuthorityValidator.ValidateAsync(c, t, actor, Scope.Admin, clock.GetUtcNow(),
                stepUpWindow: actor.Kind == "session" ? TimeSpan.FromMinutes(Current.Auth.StepUpMinutes) : null, cancellationToken: ct);
            return 0;
        }, token);

    [GeneratedRegex("^debug-[0-9]{8}T[0-9]{13}Z-[a-f0-9]{32}\\.jsonl$", RegexOptions.CultureInvariant)]
    private static partial Regex DebugName();
}
