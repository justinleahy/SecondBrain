using System.Runtime.InteropServices;
using System.Text.Json;

namespace SecondBrain.Core.Configuration;

/// <summary>Atomic validated snapshots with debounced file watching and POSIX SIGHUP.</summary>
public sealed class ReloadingConfiguration : IDisposable
{
    private readonly object gate = new();
    private readonly YamlConfigurationLoader loader;
    private readonly string path;
    private readonly Action<string>? diagnostic;
    private readonly FileSystemWatcher? watcher;
    private readonly Timer? debounce;
    private readonly PosixSignalRegistration? signal;
    private SecondBrainOptions current;
    private string restartMetadata;
    private bool disposed;
    private long generation;

    public ReloadingConfiguration(string path, YamlConfigurationLoader? loader = null, Action<string>? diagnostic = null, bool watch = true)
    {
        this.path = Path.GetFullPath(path);
        this.loader = loader ?? new YamlConfigurationLoader();
        this.diagnostic = diagnostic;
        var initial = this.loader.LoadSnapshot(this.path);
        current = initial.Options;
        restartMetadata = initial.RestartMetadata;
        if (!watch) return;
        debounce = new Timer(_ => TryReload(), null, Timeout.Infinite, Timeout.Infinite);
        watcher = new FileSystemWatcher(Path.GetDirectoryName(this.path)!, Path.GetFileName(this.path))
        { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
        watcher.Changed += Schedule;
        watcher.Created += Schedule;
        watcher.Deleted += Schedule;
        watcher.Renamed += Schedule;
        watcher.Error += (_, _) => diagnostic?.Invoke("Configuration file watcher failed; SIGHUP or explicit reload remains available.");
        watcher.EnableRaisingEvents = true;
        if (!OperatingSystem.IsWindows())
            signal = PosixSignalRegistration.Create(PosixSignal.SIGHUP, context => { context.Cancel = true; Schedule(null, null!); });
    }

    public SecondBrainOptions CurrentValue => Volatile.Read(ref current);
    public event Action<SecondBrainOptions>? Changed;

    public bool TryReload()
    {
        SecondBrainOptions next;
        lock (gate)
        {
            if (disposed) return false;
            try
            {
                var snapshot = loader.LoadSnapshot(path);
                next = snapshot.Options;
                if (RestartFingerprint(next) != RestartFingerprint(current) || snapshot.RestartMetadata != restartMetadata)
                    throw new ConfigurationException("Listeners, allowed roots, data root, Access and sandbox settings require a restart.");
                Volatile.Write(ref current, next);
                generation++;
            }
            catch (Exception error) when (error is ConfigurationException or IOException or UnauthorizedAccessException)
            {
                diagnostic?.Invoke(error is ConfigurationException ? $"Configuration reload rejected: {error.Message}" : "Configuration reload rejected: file unavailable.");
                return false;
            }
            // Serialize publication and delivery. A reentrant subscriber can reload; stop
            // delivering the superseded outer generation so no lane observes an older value last.
            var publishedGeneration = generation;
            var handlers = Changed;
            if (handlers is not null)
                foreach (Action<SecondBrainOptions> handler in handlers.GetInvocationList())
                {
                    if (publishedGeneration != generation || disposed) break;
                    try { handler(next); }
                    catch (Exception) { diagnostic?.Invoke("A configuration change subscriber failed after a valid reload."); }
                }
            return true;
        }
    }

    private static string RestartFingerprint(SecondBrainOptions options) => JsonSerializer.Serialize(new
    { options.DataRoot, options.Server.Listeners, options.Server.Hosts, options.Server.Origins, options.Server.TrustedProxies, options.Server.CloudflareAccess, options.Sources });

    private void Schedule(object? sender, FileSystemEventArgs args)
    {
        lock (gate) { if (!disposed) debounce?.Change(150, Timeout.Infinite); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            watcher?.Dispose();
            debounce?.Dispose();
            signal?.Dispose();
        }
    }
}
