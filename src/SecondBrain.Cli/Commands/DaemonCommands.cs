using System.Diagnostics;

namespace SecondBrain.Cli.Commands;

/// <summary>Runs the installed daemon; the daemon composition owns the lock for its entire lifetime.</summary>
public sealed class InstalledDaemonCommands : IDaemonCommands
{
    public async Task<int> ServeAsync(DaemonLaunchOptions options, CancellationToken cancellationToken)
    {
        var configured = Environment.GetEnvironmentVariable("SECONDBRAIN_SERVER_PATH");
        var candidates = new[] { configured, Path.Combine(AppContext.BaseDirectory, "SecondBrain.Server"),
            Path.Combine(AppContext.BaseDirectory, "SecondBrain.Server.dll"), Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "server", "SecondBrain.Server.dll")) };
        var executable = candidates.FirstOrDefault(path => path is not null && File.Exists(path));
        if (executable is null) throw new CliPreconditionException("The daemon binary is missing; install SecondBrain.Server beside brain or set SECONDBRAIN_SERVER_PATH.");
        var start = new ProcessStartInfo(executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : executable) { RedirectStandardError = true, RedirectStandardOutput = true };
        if (executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(executable);
        start.Environment["SECONDBRAIN_CONFIG"] = options.ConfigPath;
        start.Environment["SECONDBRAIN_DATA_ROOT"] = options.DataRoot;
        start.Environment["SECONDBRAIN_SYNC_UID"] = options.SyncUid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (options.SecretsDirectory is not null) start.Environment["SECONDBRAIN_SECRETS_DIRECTORY"] = options.SecretsDirectory;
        using var process = Process.Start(start) ?? throw new IOException("Cannot launch the daemon.");
        var lockFailure = false;
        var ownershipFailure = false;
        string? failureType = null;
        var stdout = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
                await options.Diagnostics.WriteLineAsync(line);
        }, cancellationToken);
        var stderr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
            {
                lockFailure |= line.Contains("DataRootLockedException", StringComparison.Ordinal) || line.Contains("data root is already locked", StringComparison.OrdinalIgnoreCase);
                ownershipFailure |= line.Contains("Root mode is incorrect", StringComparison.Ordinal) || line.Contains("Root ownership is incorrect", StringComparison.Ordinal) ||
                    line.Contains("Key ring must be daemon-owned", StringComparison.Ordinal) || line.Contains("must run as a non-root", StringComparison.OrdinalIgnoreCase);
                if (line.StartsWith("Unhandled exception. ", StringComparison.Ordinal)) failureType = line[21..].Split(':', 2)[0];
            }
        }, cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0)
        {
            if (lockFailure) throw new CliPreconditionException("The data root is already locked by another SecondBrain process.");
            if (ownershipFailure) throw new CliPreconditionException("Daemon ownership, permissions or non-root identity validation failed; run brain doctor.");
            await options.Diagnostics.WriteLineAsync(failureType is null ? $"Daemon exited with status {process.ExitCode}; inspect the service logs." : $"Daemon startup failed ({failureType}); inspect the service logs.");
        }
        return process.ExitCode is >= 0 and <= 4 ? process.ExitCode : (int)CliExitCode.Error;
    }
}
