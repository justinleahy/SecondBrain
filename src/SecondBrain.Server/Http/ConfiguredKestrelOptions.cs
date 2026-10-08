using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;

namespace SecondBrain.Server.Http;

/// <summary>Only explicit private addresses can become daemon listeners.</summary>
public sealed class ConfiguredKestrelOptions(IOptionsMonitor<SecondBrainOptions> options) : IConfigureOptions<KestrelServerOptions>
{
    public void Configure(KestrelServerOptions kestrel)
    {
        kestrel.Limits.MaxRequestBodySize = 100L * 1024 * 1024;
        if (options.CurrentValue.Server.Listeners.Count == 0)
            throw new OptionsValidationException(nameof(SecondBrainOptions), typeof(SecondBrainOptions), ["At least one explicit private listener is required."]);
        foreach (var listener in options.CurrentValue.Server.Listeners)
        {
            if (!IPAddress.TryParse(listener.Bind, out var address) || !IsPrivateAddress(address))
                throw new OptionsValidationException(nameof(SecondBrainOptions), typeof(SecondBrainOptions),
                    ["Listeners must bind an explicit loopback or private-network IP address."]);
            if (listener.Port is <= 0 or > 65535 || listener.Scheme is not ("http" or "https"))
                throw new OptionsValidationException(nameof(SecondBrainOptions), typeof(SecondBrainOptions), ["Invalid HTTP listener."]);
            kestrel.Listen(address, listener.Port, endpoint =>
            {
                if (listener.Scheme != "https") return;
                if (string.IsNullOrWhiteSpace(listener.Certificate) || !File.Exists(listener.Certificate))
                    throw new OptionsValidationException(nameof(SecondBrainOptions), typeof(SecondBrainOptions),
                        ["HTTPS requires an existing certificate file."]);
                var extension = Path.GetExtension(listener.Certificate);
                var keyFile = Path.ChangeExtension(listener.Certificate, ".key");
                var certificate = extension.Equals(".pfx", StringComparison.OrdinalIgnoreCase) ||
                                  extension.Equals(".p12", StringComparison.OrdinalIgnoreCase)
                    ? X509CertificateLoader.LoadPkcs12FromFile(listener.Certificate, password: null)
                    : X509Certificate2.CreateFromPemFile(listener.Certificate,
                        File.Exists(keyFile) ? keyFile : listener.Certificate);
                endpoint.UseHttps(certificate);
            });
        }
    }

    public static bool IsPrivateAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4
            ? bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
              (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
            : (bytes[0] & 0xfe) == 0xfc;
    }
}
