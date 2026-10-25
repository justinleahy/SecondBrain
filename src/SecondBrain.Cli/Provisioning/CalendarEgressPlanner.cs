using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using SecondBrain.Cli.Commands;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Privacy;
using SecondBrain.Core.Sources;

namespace SecondBrain.Cli.Provisioning;

/// <summary>Prepares host policy from origin permissions only; it never reads subscription links or grants provider trust.</summary>
public sealed class CalendarEgressPlanner(Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> dns = resolve ?? ((host, token) => Dns.GetHostAddressesAsync(host, token));
    public async Task<CalendarEgressPlan> BuildAsync(SecondBrainOptions options, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var permitted = options.Sources.CalendarAllowedOrigins;
            if (permitted is null || permitted.Count > 128) throw new CliPreconditionException("At most 128 exact calendar origins are supported.");
            var origins = permitted.Select(CalendarLink.ValidateOrigin).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            // An empty source permission must remove previous generated allowances without depending on DNS.
            if (origins.Length == 0) return new([], [], []);
            var (canaryHost, _) = EgressCanary.ParseTarget(options.Privacy.CanaryTarget);
            var canary = await ResolveAsync(canaryHost, deadline.Token);
            var destinations = new List<CalendarEgressDestination>();
            foreach (var origin in origins)
            {
                var uri = new Uri(origin);
                foreach (var address in await ResolveAsync(uri.IdnHost.Trim('[', ']'), deadline.Token))
                {
                    if (canary.Contains(address)) throw new CliPreconditionException("A calendar origin overlaps the egress canary. Select a separate canary destination before provisioning.");
                    destinations.Add(new(origin, address.ToString(), uri.Port));
                }
            }
            if (destinations.Count > 1024) throw new CliPreconditionException("Calendar DNS results exceed the supported policy size.");
            return new(origins, destinations.OrderBy(item => item.Address, StringComparer.Ordinal).ThenBy(item => item.Port).ToArray(),
                canary.Select(item => item.ToString()).Order(StringComparer.Ordinal).ToArray());
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new CliPreconditionException("Calendar network policy resolution timed out."); }
        catch (Exception error) when (error is SocketException or ArgumentException or CalendarSubscriptionException or PrivacyPolicyException)
        { throw new CliPreconditionException("Calendar network policy could not be prepared. Check the exact HTTPS origins, public DNS records and canary setting."); }
    }

    private async Task<IPAddress[]> ResolveAsync(string host, CancellationToken token)
    {
        var values = IPAddress.TryParse(host, out var literal) ? [literal] : await dns(host, token).WaitAsync(token);
        var addresses = values.Select(address => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).Distinct().ToArray();
        if (addresses.Length == 0 || addresses.Length > 128 || addresses.Any(address => !CalendarLink.IsPublicAddress(address)))
            throw new CliPreconditionException("Calendar and canary DNS records must contain only public unicast addresses.");
        return addresses;
    }

    public static async Task WriteAsync(CalendarEgressPlan plan, string directory, CancellationToken token = default)
    {
        var path = Path.GetFullPath(directory);
        if (new DirectoryInfo(path).LinkTarget is not null) throw new CliPreconditionException("The policy output directory must not be a symbolic link.");
        if (!Directory.Exists(path))
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
            else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        foreach (var (name, text) in new[] { ("40-calendar-egress.conf", plan.SystemdDropIn), ("calendar-egress.env", plan.ComposeEnvironment),
                     ("calendar-egress.json", JsonSerializer.Serialize(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + "\n") })
        {
            var destination = Path.Combine(path, name);
            if (new FileInfo(destination).LinkTarget is not null) throw new CliPreconditionException("A policy output file must not be a symbolic link.");
            var temporary = Path.Combine(path, ".calendar-policy-" + Guid.NewGuid().ToString("N"));
            try
            {
                var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var output = new FileStream(temporary, fileOptions))
                {
                    await output.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text), token);
                    await output.FlushAsync(token); output.Flush(flushToDisk: true);
                }
                File.Move(temporary, destination, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}

public sealed record CalendarEgressDestination(string Origin, string Address, int Port);
public sealed record CalendarEgressPlan(IReadOnlyList<string> Origins, IReadOnlyList<CalendarEgressDestination> Destinations, IReadOnlyList<string> CanaryAddresses)
{
    public string SystemdDropIn => "# Generated from sources.calendar_allowed_origins. Does not grant model-provider trust.\n[Service]\n" +
        string.Concat(Destinations.Select(item => item.Address).Distinct(StringComparer.Ordinal).Select(address => "IPAddressAllow=" + address + "\n"));
    public string ComposeEnvironment => "# Calendar downloads by the daemon UID only; independent of local_only and provider trust.\nSECONDBRAIN_CALENDAR_ALLOWLIST=" +
        string.Join(' ', Destinations.Select(item => (item.Address.Contains(':') ? "[" + item.Address + "]" : item.Address) + ":" + item.Port).Distinct(StringComparer.Ordinal)) +
        "\nSECONDBRAIN_CALENDAR_CANARY_ADDRESSES=" + string.Join(' ', CanaryAddresses) + "\n";
}
