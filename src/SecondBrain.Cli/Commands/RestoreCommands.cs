using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Limits;
using SecondBrain.Core.Retrieval;
using SecondBrain.Core.Storage;
using SecondBrain.Infrastructure.FileSystem;
using SecondBrain.Infrastructure.Security;
using SecondBrain.Storage;
using SecondBrain.Storage.Backup;

namespace SecondBrain.Cli.Commands;

public interface IRestoreCommands
{
    Task<CliResult> RunAsync(SecondBrainOptions options, RuntimeLocations locations, string? archive,
        bool rollback, IReadOnlyDictionary<string, string> externalMappings, CancellationToken token);
}

/// <summary>Offline restore uses OS access and the separately held backup key; no old session is reused.</summary>
public sealed class LocalRestoreCommands(Func<string, FileKeyRing> openKeyRing) : IRestoreCommands
{
    public async Task<CliResult> RunAsync(SecondBrainOptions options, RuntimeLocations locations, string? archive,
        bool rollback, IReadOnlyDictionary<string, string> externalMappings, CancellationToken token)
    {
        using var lease = DataRootLock.Acquire(options.DataRoot, allowPendingRestore: true);
        var disk = new DiskCapacity();
        var installer = new BackupInstaller(disk, checked((long)(options.Limits.Global.DiskLowWaterGb * 1_000_000_000m)));
        try
        {
            if (rollback)
                return CliResult.Ok("Previous files restored. Prepared restore files were retained for review.", await installer.RollbackAsync(lease, token));
            if (archive is null) throw new CliUsageException("Provide an archive, or use --rollback to recover an interrupted restore.");
            using var key = await new FileBackupKeyProvider(new ConstantOptions(options)).ReadAsync(token);
            var reserve = checked((long)(options.Limits.Global.DiskLowWaterGb * 1_000_000_000m));
            var available = disk.AvailableBytes(options.DataRoot);
            var archiveBytes = new FileInfo(archive).Length;
            if (available is null || available < reserve || (available - reserve) / 2 < archiveBytes + 1024 * 1024)
                throw new CliPreconditionException("There is not enough free space to verify the restore archive safely.");
            var limits = new BackupRestoreLimits(Math.Min(64L * 1024 * 1024 * 1024, (available.Value - reserve) / 2));
            var staging = Path.Combine(options.DataRoot, "staging");
            if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            Directory.CreateDirectory(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await using var source = File.OpenRead(Path.GetFullPath(archive));
            await using var verified = await BackupPackage.VerifyAndStageAsync(source, key.Bytes, staging, limits, token);
            var keyring = Path.Combine(verified.ContentPath, "keyring");
            if (!File.Exists(Path.Combine(keyring, "hmac.json")) || !Directory.EnumerateFiles(keyring, "*.xml").Any())
                throw new CliPreconditionException("The backup does not contain a recoverable key ring.");
            var roots = RestorePreparation.MapRoots(verified.Metadata, options.DataRoot, externalMappings, options.Sources.AllowedRoots);
            foreach (var root in roots.Where(root => root.Name.StartsWith("external", StringComparison.Ordinal)))
            {
                if (Within(options.Sources.IncomingRoot, root.Destination))
                    throw new CliPreconditionException("Map the external source to a folder beneath an allowed root, without replacing the configured incoming root.");
                foreach (var protectedPath in new[] { locations.ConfigDirectory, locations.SecretsDirectory, options.Backup.KeyFile })
                    if (Within(root.Destination, protectedPath) || Within(protectedPath, root.Destination))
                        throw new CliPreconditionException("An external restore root overlaps configuration or secrets.");
            }
            var result = await installer.InstallAsync(verified, lease, externalMappings, options.Sources.AllowedRoots,
                ct => RebuildAsync(options, disk, ct), token);
            return CliResult.Ok("Backup restored and index rebuilt. Old credentials are invalid. Sign in again, rotate API and provider keys, and review retained previous files.", result);
        }
        catch (Core.Operations.BackupException) { throw new CliPreconditionException("The backup key is unavailable or invalid."); }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            throw new CliPreconditionException(Path.Exists(Path.Combine(options.DataRoot, Core.Operations.RestoreState.PendingFileName))
                ? "Restore did not finish. The daemon is blocked from starting; run brain maintenance restore --rollback. Previous files have been retained."
                : "Restore could not be prepared. Check the archive, backup key, destination roots and free space. Existing files were not replaced.");
        }
    }

    private async Task<int> RebuildAsync(SecondBrainOptions options, IDiskCapacity disk, CancellationToken token)
    {
        using var ring = openKeyRing(options.DataRoot);
        var services = new ServiceCollection(); services.AddLogging();
        services.AddSingleton<IOptions<SecondBrainOptions>>(Options.Create(options));
        services.AddSingleton<IOptionsMonitor<SecondBrainOptions>>(new ConstantOptions(options));
        services.AddSingleton(disk);
        services.AddSingleton<IPayloadProtection>(new KeyRingPayloadProtector(ring));
        services.AddSecondBrainStorage();
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IMigrationRunner>().MigrateAsync(token);
        await provider.GetRequiredService<IPublicationCoordinator>().RecoverAsync(token);
        return await provider.GetRequiredService<IDocumentRepository>().RebuildAsync(token);
    }
    private static bool Within(string path, string root)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return path == root || path.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
    private sealed class ConstantOptions(SecondBrainOptions options) : IOptionsMonitor<SecondBrainOptions>
    {
        public SecondBrainOptions CurrentValue => options;
        public SecondBrainOptions Get(string? name) => options;
        public IDisposable? OnChange(Action<SecondBrainOptions, string?> listener) => null;
    }
}
