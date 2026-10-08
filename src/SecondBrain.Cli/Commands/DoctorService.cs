using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Security;

namespace SecondBrain.Cli.Commands;

public sealed class DoctorService
{
    public async Task<CliResult> CheckAsync(SecondBrainOptions options, uint daemonUid, uint daemonGid, uint syncUid,
        string extractorSocket, IDoctorExtension? extension, CancellationToken cancellationToken)
    {
        var checks = RootSecurityValidator.Validate(options.DataRoot, options.Sources.IncomingRoot, daemonUid, daemonGid, syncUid)
            .Select(check => new DoctorCheck(check.Path, check.Passed, check.Message)).ToList();
        CheckListeners(options, checks);
        CheckKeyRing(options.DataRoot, daemonUid, checks);
        await CheckExtractorAsync(extractorSocket, checks, cancellationToken);
        if (options.Server.CloudflareAccess is { } access)
            checks.Add(new("cloudflare-access.configuration", !string.IsNullOrWhiteSpace(access.PublicHostname) && !string.IsNullOrWhiteSpace(access.Audience) &&
                Uri.TryCreate("https://" + access.TeamDomain + "/cdn-cgi/access/certs", UriKind.Absolute, out _), "Configured Access public host, audience and certificate endpoint."));
        checks.Add(new("models.bindings", options.Models.Chat is not null && options.Models.Enrich is not null && options.Models.Embed is not null,
            "Chat, enrich and embed require explicit provider/model bindings before readiness."));
        if (extension is not null) checks.AddRange(await extension.CheckAsync(options, cancellationToken));
        else checks.Add(new("daemon.integration", false, "Store, provider, Access refresh and egress canary diagnostics require IDoctorExtension from lanes A/B/D."));
        var passed = checks.All(check => check.Passed);
        return new(passed ? CliExitCode.Ok : CliExitCode.PreconditionFailed, passed ? "ok" : "precondition-failed",
            passed ? "All diagnostics passed." : "Diagnostics found failed preconditions.", checks);
    }

    private static void CheckListeners(SecondBrainOptions options, List<DoctorCheck> checks)
    {
        var localAddresses = NetworkInterface.GetAllNetworkInterfaces().SelectMany(item => item.GetIPProperties().UnicastAddresses).Select(item => item.Address).ToHashSet();
        IPEndPoint[]? active = null;
        try { active = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(); }
        catch (NetworkInformationException) { checks.Add(new("listeners.actual-exposure", false, "Cannot inspect active TCP listeners on this host.")); }
        if (options.Server.Listeners.Count == 0) checks.Add(new("listeners", false, "No listeners configured."));
        foreach (var listener in options.Server.Listeners)
        {
            var parsed = IPAddress.TryParse(listener.Bind, out var address);
            var assigned = parsed && (IPAddress.IsLoopback(address!) || localAddresses.Contains(address!));
            checks.Add(new($"listener.{listener.Bind}:{listener.Port}", assigned && IsPrivate(address!),
                assigned && IsPrivate(address!) ? "Listener binds an assigned private or loopback interface." : "Listener is public, wildcard, or not assigned to a local interface."));
            if (listener.Scheme == "https")
                checks.Add(new($"listener.{listener.Port}.certificate", listener.Certificate is not null && listener.Certificate != "tailscale" && File.Exists(listener.Certificate),
                    listener.Certificate == "tailscale" ? "Resolve the Tailscale certificate selector to an installed certificate file before serving HTTPS." : "HTTPS certificate file must exist."));
            if (active is not null)
                checks.Add(new($"listener.{listener.Port}.actual-exposure", active.Where(item => item.Port == listener.Port).All(item => IsPrivate(item.Address)),
                    "Inspect all currently active binds for this configured port; public or wildcard listeners are refused."));
        }
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4) return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
        return (bytes[0] & 0xfe) == 0xfc || address.IsIPv6LinkLocal;
    }

    private static void CheckKeyRing(string dataRoot, uint daemonUid, List<DoctorCheck> checks)
    {
        var path = Path.Combine(dataRoot, "keyring");
        if (!Directory.Exists(path)) { checks.Add(new("keyring", false, "Key ring directory does not exist; run brain init.")); return; }
        if (OperatingSystem.IsWindows()) { checks.Add(new("keyring", false, "Unix key-ring modes cannot be checked on Windows.")); return; }
        var directory = new DirectoryInfo(path);
        var directoryValid = directory.LinkTarget is null && File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute) &&
            RootSecurityValidator.Ownership(path).Uid == daemonUid;
        checks.Add(new("keyring.directory", directoryValid, "Key ring must be daemon-owned, 0700, and not a symbolic link."));
        if (!directoryValid) return;
        var files = Directory.EnumerateFiles(path).ToArray();
        checks.Add(new("keyring.keys", files.Any(file => Path.GetFileName(file).StartsWith("key-", StringComparison.Ordinal) && file.EndsWith(".xml", StringComparison.Ordinal)) &&
            File.Exists(Path.Combine(path, "hmac.json")), "Data Protection and HMAC key material must both exist."));
        var manifest = Path.Combine(path, "hmac.json");
        if (File.Exists(manifest) && new FileInfo(manifest).LinkTarget is null &&
            File.GetUnixFileMode(manifest) == (UnixFileMode.UserRead | UnixFileMode.UserWrite) && RootSecurityValidator.Ownership(manifest).Uid == daemonUid)
        {
            var valid = false;
            byte[]? bytes = null;
            try
            {
                if (new FileInfo(manifest).Length > 1024 * 1024) throw new InvalidDataException();
                bytes = File.ReadAllBytes(manifest);
                using var document = JsonDocument.Parse(bytes);
                var root = document.RootElement;
                if (root.TryGetProperty("ActiveKid", out var active) && active.ValueKind == JsonValueKind.String &&
                    root.TryGetProperty("Keys", out var keys) && keys.ValueKind == JsonValueKind.Object)
                {
                    var activeKid = active.GetString();
                    valid = activeKid is not null && Ulid.TryParse(activeKid, out _) && keys.TryGetProperty(activeKid, out _) && keys.EnumerateObject().All(key =>
                    {
                        if (!Ulid.TryParse(key.Name, out _) || key.Value.ValueKind != JsonValueKind.String || !key.Value.TryGetBytesFromBase64(out var material)) return false;
                        var correctLength = material.Length == 32;
                        System.Security.Cryptography.CryptographicOperations.ZeroMemory(material);
                        return correctLength;
                    });
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { }
            finally { if (bytes is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
            checks.Add(new("keyring.hmac-integrity", valid, valid ? "HMAC active kid and retained keys are valid." : "HMAC key-ring manifest is invalid."));
        }
        foreach (var file in files)
            checks.Add(new("keyring.file." + Path.GetFileName(file), new FileInfo(file).LinkTarget is null &&
                File.GetUnixFileMode(file) == (UnixFileMode.UserRead | UnixFileMode.UserWrite) && RootSecurityValidator.Ownership(file).Uid == daemonUid,
                "Key material must be daemon-owned, 0600, and not a symbolic link."));
    }

    private static async Task CheckExtractorAsync(string path, List<DoctorCheck> checks, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), timeout.Token);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, id = "doctor-1", operation = "ping" });
            var header = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.Length);
            await stream.WriteAsync(header, timeout.Token);
            await stream.WriteAsync(payload, timeout.Token);
            await stream.ReadExactlyAsync(header, timeout.Token);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length is 0 or > 65536) throw new IOException("Extractor returned an invalid frame size.");
            var response = new byte[length];
            await stream.ReadExactlyAsync(response, timeout.Token);
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            var ok = root.TryGetProperty("version", out var version) && version.GetInt32() == 1 && root.TryGetProperty("id", out var id) && id.GetString() == "doctor-1" &&
                root.TryGetProperty("ok", out var success) && success.ValueKind == JsonValueKind.True && root.TryGetProperty("result", out var result) && result.GetString() == "pong";
            checks.Add(new("extractor.socket", ok, ok ? "Extractor answered ping." : "Extractor response did not match the framing contract."));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { checks.Add(new("extractor.socket", false, "Extractor ping timed out.")); }
        catch (Exception ex) when (ex is SocketException or IOException or JsonException or InvalidOperationException or PlatformNotSupportedException)
        { checks.Add(new("extractor.socket", false, "Extractor socket ping failed: " + ex.Message)); }
    }
}
