using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SecondBrain.Cli.Commands;

namespace SecondBrain.Cli.Provisioning;

public sealed partial class LinuxProvisionPlatform : IProvisionPlatform
{
    public bool IsRoot => !OperatingSystem.IsWindows() && GetEffectiveUid() == 0;

    public void EnsureGroup(string name, uint gid)
    {
        RequireLinux();
        ValidateName(name);
        var lookup = Execute("getent", ["group", name], allowedFailure: true);
        if (lookup.ExitCode == 0)
        {
            if (!uint.TryParse(lookup.Output.Split(':')[2], out var existing) || existing != gid)
                throw new CliPreconditionException($"Existing group {name} has a different gid; explicitly select its gid.");
            return;
        }
        Execute("groupadd", ["--system", "--gid", gid.ToString(System.Globalization.CultureInfo.InvariantCulture), name]);
    }

    public void EnsureUser(string name, uint uid, string group)
    {
        RequireLinux();
        ValidateName(name);
        ValidateName(group);
        var lookup = Execute("getent", ["passwd", name], allowedFailure: true);
        if (lookup.ExitCode == 0)
        {
            var fields = lookup.Output.Split(':');
            var groupEntry = Execute("getent", ["group", group]).Output.Split(':');
            if (fields.Length < 4 || groupEntry.Length < 3 || !uint.TryParse(fields[2], out var existing) || existing != uid ||
                !string.Equals(fields[3], groupEntry[2], StringComparison.Ordinal))
                throw new CliPreconditionException($"Existing user {name} has a different uid or primary group; provision the configured identity before continuing.");
            return;
        }
        Execute("useradd", ["--system", "--uid", uid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--gid", group, "--no-create-home", "--home-dir", "/nonexistent", "--shell", "/usr/sbin/nologin", name]);
    }

    public void SetOwner(string path, uint uid, uint gid)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Unix ownership requires Linux or macOS.");
        if (Chown(path, uid, gid) != 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot set ownership of {path}.");
    }

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux()) throw new CliPreconditionException("Service user creation requires Linux; use --skip-users only for already provisioned numeric identities.");
    }

    private static void ValidateName(string value)
    {
        if (!UnixName().IsMatch(value)) throw new CliUsageException("Invalid Unix account name.");
    }

    private static (int ExitCode, string Output) Execute(string file, string[] args, bool allowedFailure = false)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment.Clear();
        info.Environment["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin";
        using var process = Process.Start(info) ?? throw new IOException($"Cannot start {file}.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (!allowedFailure && process.ExitCode != 0) throw new CliPreconditionException($"{file} failed: {stderr.Trim()}");
        return (process.ExitCode, stdout.Trim());
    }

    [GeneratedRegex("^[a-z_][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex UnixName();
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUid();
    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int Chown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint uid, uint gid);
}
