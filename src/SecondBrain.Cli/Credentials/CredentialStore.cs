using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SecondBrain.Cli.Commands;

namespace SecondBrain.Cli.Credentials;

/// <summary>Stores login credentials separately from config; stored material is never returned to command output.</summary>
public interface ICredentialStore
{
    Task StoreAsync(Uri origin, string name, string key, CancellationToken cancellationToken);
    Task<string?> ReadAsync(Uri origin, string name, CancellationToken cancellationToken);
}

/// <summary>macOS Keychain, Linux Secret Service, with a 0600 file fallback when unavailable.</summary>
public sealed class OsCredentialStore(string? fallbackDirectory = null, bool useOsStore = true) : ICredentialStore
{
    private readonly FileCredentialStore _fallback = new(fallbackDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "secondbrain", "credentials"));

    public async Task StoreAsync(Uri origin, string name, string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var account = CredentialAccount(origin, name);
        if (useOsStore && OperatingSystem.IsMacOS())
        {
            // Interactive security input keeps secret bytes out of process command-line arguments.
            var line = $"add-generic-password -U -s secondbrain -a {ShellWord(account)} -w {ShellWord(key)}\nquit\n";
            var result = await ExecuteAsync("/usr/bin/security", ["-i"], line, cancellationToken);
            if (result?.ExitCode == 0)
            {
                // security -i can exit successfully after an individual command failed.
                var confirmation = await ExecuteAsync("/usr/bin/security", ["find-generic-password", "-s", "secondbrain", "-a", account, "-w"], null, cancellationToken);
                if (confirmation is { ExitCode: 0 } stored && string.Equals(stored.Output.TrimEnd('\r', '\n'), key, StringComparison.Ordinal)) return;
            }
        }
        else if (useOsStore && OperatingSystem.IsLinux())
        {
            var result = await ExecuteAsync("secret-tool", ["store", "--label=SecondBrain API credential", "application", "secondbrain", "account", account], key + "\n", cancellationToken);
            if (result?.ExitCode == 0) return;
        }
        await _fallback.StoreAsync(origin, name, key, cancellationToken);
    }

    public async Task<string?> ReadAsync(Uri origin, string name, CancellationToken cancellationToken)
    {
        var account = CredentialAccount(origin, name);
        if (useOsStore && OperatingSystem.IsMacOS())
        {
            var result = await ExecuteAsync("/usr/bin/security", ["find-generic-password", "-s", "secondbrain", "-a", account, "-w"], null, cancellationToken);
            if (result is { ExitCode: 0 } stored) return stored.Output.TrimEnd('\r', '\n');
        }
        else if (useOsStore && OperatingSystem.IsLinux())
        {
            var result = await ExecuteAsync("secret-tool", ["lookup", "application", "secondbrain", "account", account], null, cancellationToken);
            if (result is { ExitCode: 0 } stored && !string.IsNullOrWhiteSpace(stored.Output)) return stored.Output.TrimEnd('\r', '\n');
        }
        return await _fallback.ReadAsync(origin, name, cancellationToken);
    }

    private static string CredentialAccount(Uri origin, string name) => origin.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/" + name;
    private static string ShellWord(string value)
    {
        if (value.Contains('\n') || value.Contains('\r') || value.Contains('\0')) throw new CliUsageException("Credential values must contain no control characters.");
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static async Task<(int ExitCode, string Output)?> ExecuteAsync(string program, string[] arguments, string? input, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var operationToken = timeout.Token;
        var start = new ProcessStartInfo(program) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // Session bus/keychain variables are needed by credential stores; no provider/bootstrap secrets are inherited.
        var allowed = new[] { "PATH", "HOME", "DBUS_SESSION_BUS_ADDRESS", "XDG_RUNTIME_DIR", "DISPLAY" };
        var values = allowed.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        start.Environment.Clear();
        foreach (var (name, value) in values) if (value is not null) start.Environment[name] = value;
        Process? process;
        try { process = Process.Start(start); }
        catch (System.ComponentModel.Win32Exception) { return null; }
        if (process is null) return null;
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync(operationToken);
            var error = process.StandardError.ReadToEndAsync(operationToken);
            try
            {
                if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), operationToken);
                process.StandardInput.Close();
                await process.WaitForExitAsync(operationToken);
                _ = await error;
                return (process.ExitCode, await output);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                if (cancellationToken.IsCancellationRequested) throw;
                return null;
            }
        }
    }
}

public sealed class FileCredentialStore(string directory) : ICredentialStore
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileModeBits = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public async Task StoreAsync(Uri origin, string name, string key, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows()) throw new CliPreconditionException("An OS credential-store adapter is required on Windows; the Unix 0600 fallback is unavailable.");
        EnsureDirectory();
        var path = GetPath(origin, name);
        RejectSymlink(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = FileModeBits, Options = FileOptions.Asynchronous }))
            {
                await JsonSerializer.SerializeAsync(stream, new StoredCredential(origin.GetLeftPart(UriPartial.Authority), name, key), cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<string?> ReadAsync(Uri origin, string name, CancellationToken cancellationToken)
    {
        var path = GetPath(origin, name);
        if (!File.Exists(path)) return null;
        if (OperatingSystem.IsWindows()) throw new CliPreconditionException("Unix credential-file permissions are unavailable on Windows.");
        RejectSymlink(directory);
        RejectSymlink(path);
        if (File.GetUnixFileMode(directory) != DirectoryMode || File.GetUnixFileMode(path) != FileModeBits)
            throw new CliPreconditionException("Credential fallback directory must be 0700 and files must be 0600.");
        await using var stream = File.OpenRead(path);
        return (await JsonSerializer.DeserializeAsync<StoredCredential>(stream, cancellationToken: cancellationToken))?.ApiKey;
    }

    private void EnsureDirectory()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Unix credential-file permissions are unavailable.");
        RejectSymlink(directory);
        Directory.CreateDirectory(directory, DirectoryMode);
        File.SetUnixFileMode(directory, DirectoryMode);
    }

    private string GetPath(Uri origin, string name)
    {
        var identity = Encoding.UTF8.GetBytes(origin.GetLeftPart(UriPartial.Authority) + "\0" + name);
        return Path.Combine(directory, Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant() + ".json");
    }

    private static void RejectSymlink(string path)
    {
        FileSystemInfo item = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (item.LinkTarget is not null) throw new CliPreconditionException("Refusing a symbolic link in the credential store.");
    }
    private sealed record StoredCredential(string Origin, string Name, string ApiKey);
}
