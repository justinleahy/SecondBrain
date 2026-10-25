using System.Runtime.InteropServices;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using SecondBrain.Core.Configuration;
using SecondBrain.Infrastructure.Security;

namespace SecondBrain.Infrastructure.Configuration;

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
    private string appliedHash;

    public ReloadingConfiguration(string path, YamlConfigurationLoader? loader = null, Action<string>? diagnostic = null, bool watch = true)
    {
        this.path = Path.GetFullPath(path);
        this.loader = loader ?? new YamlConfigurationLoader();
        this.diagnostic = diagnostic;
        var initialText = ReadText();
        var initial = this.loader.LoadTextSnapshot(initialText);
        appliedHash = Hash(initialText);
        current = initial.Options;
        restartMetadata = initial.RestartMetadata;
        if (!watch) return;
        debounce = new Timer(_ => TryReload(), null, Timeout.Infinite, Timeout.Infinite);
        var watchedPath = new FileInfo(this.path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? this.path;
        watcher = new FileSystemWatcher(Path.GetDirectoryName(watchedPath)!, Path.GetFileName(watchedPath))
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
    /// <summary>Collaborating model/privacy validators run before an options snapshot is published.</summary>
    public event Action<SecondBrainOptions>? Validating;

    public ConfigurationEditSnapshot ReadForEdit()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (Hash(ReadText()) != appliedHash && !TryReload())
                throw new ConfigurationException("The configuration file changed and could not be reloaded. Correct it before editing settings.");
            return new(appliedHash, current);
        }
    }

    /// <summary>Validates and atomically replaces the source configuration before publishing it.</summary>
    public ConfigurationEditSnapshot UpdateForEdit(string expectedHash, Func<string, string> rewrite, Func<bool> authorized)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var original = ReadText();
            if (Hash(original) != expectedHash) throw new ConfigurationEditConflictException();
            var replacement = rewrite(original);
            if (Encoding.UTF8.GetByteCount(replacement) > 1_048_576) throw new ConfigurationException("Configuration exceeds the supported size.");
            var candidate = loader.LoadTextSnapshot(replacement);
            ValidateCandidate(candidate);
            if (!authorized()) throw new ConfigurationEditAuthorityException();
            if (Hash(ReadText()) != expectedHash) throw new ConfigurationEditConflictException();
            var target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
            var temporary = Path.Combine(Path.GetDirectoryName(target)!, ".secondbrain-settings-" + Guid.NewGuid().ToString("N"));
            try
            {
                var bytes = Encoding.UTF8.GetBytes(replacement);
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(target));
                    output.Write(bytes); output.Flush(flushToDisk: true);
                }
                if (Hash(ReadText()) != expectedHash) throw new ConfigurationEditConflictException();
                if (!authorized()) throw new ConfigurationEditAuthorityException();
                File.Move(temporary, target, overwrite: true);
                UnixSecurity.SyncDirectory(Path.GetDirectoryName(target)!);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            Publish(candidate.Options, Hash(replacement));
            return new(appliedHash, current);
        }
    }

    public bool TryReload()
    {
        SecondBrainOptions next;
        lock (gate)
        {
            if (disposed) return false;
            try
            {
                var text = ReadText();
                var snapshot = loader.LoadTextSnapshot(text);
                next = snapshot.Options;
                ValidateCandidate(snapshot);
                appliedHash = Hash(text);
            }
            catch (Exception error) when (error is ConfigurationException or IOException or UnauthorizedAccessException)
            {
                diagnostic?.Invoke(error is ConfigurationException ? $"Configuration reload rejected: {error.Message}" : "Configuration reload rejected: file unavailable.");
                return false;
            }
            Publish(next, appliedHash);
            return true;
        }
    }

    private string ReadText()
    {
        try { return File.ReadAllText(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new ConfigurationException("Configuration file cannot be read."); }
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private void ValidateCandidate(ConfigurationSnapshot snapshot)
    {
        if (RestartFingerprint(snapshot.Options) != RestartFingerprint(current) || snapshot.RestartMetadata != restartMetadata)
            throw new ConfigurationException("Listeners, allowed roots, data root, Access and sandbox settings require a restart.");
        try { Validating?.Invoke(snapshot.Options); }
        catch (Exception) { throw new ConfigurationException("Model or privacy validation rejected the reload candidate."); }
    }
    private void Publish(SecondBrainOptions next, string hash)
    {
            Volatile.Write(ref current, next);
            appliedHash = hash;
            generation++;
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
    }

    private static string RestartFingerprint(SecondBrainOptions options) => JsonSerializer.Serialize(new
    { options.DataRoot, options.Backup.KeyFile, options.Backup.ExternalRoots, options.Server.Listeners, options.Server.Hosts, options.Server.Origins, options.Server.TrustedProxies, options.Server.CloudflareAccess, options.Sources, options.Extractor });

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

public sealed record ConfigurationEditSnapshot(string Version, SecondBrainOptions Options);
public sealed class ConfigurationEditConflictException : Exception;
public sealed class ConfigurationEditAuthorityException : Exception;
