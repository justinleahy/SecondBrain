using System.Net;
using SecondBrain.Core.Security;

namespace SecondBrain.Core.Configuration;

/// <summary>Cross-field validation at startup and before publishing a reload.</summary>
public static class ConfigurationValidator
{
    public static void Validate(SecondBrainOptions options)
    {
        if (!Path.IsPathFullyQualified(options.DataRoot)) Fail("data_root must be absolute.");
        if (options.Extractor is null || !Path.IsPathFullyQualified(options.Extractor.SocketPath)) Fail("extractor.socket_path must be absolute.");
        if (options.Server is null || options.Privacy is null || options.Models is null || options.Providers is null || options.Sources is null || options.Auth is null || options.Limits is null || options.Logging is null)
            Fail("M0 configuration sections must not be null.");
        if (options.Sources.AllowedRoots is null || options.Server.Listeners is null || options.Server.Hosts is null || options.Server.Origins is null || options.Server.TrustedProxies is null || options.Privacy.TrustedServices is null || options.Privacy.ControlPlaneEgress is null || options.Limits.PerCredential is null || options.Limits.Global is null)
            Fail("M0 configuration collections and limit groups must not be null.");
        if (options.Server.Listeners.Count == 0 || options.Server.Hosts.Count == 0 || options.Server.Origins.Count == 0)
            Fail("server.listeners, server.hosts and server.origins must be explicitly configured.");
        foreach (var origin in options.Server.Origins)
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
                uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                origin.EndsWith("/", StringComparison.Ordinal))
                Fail("server.origins entries must be exact credential-free HTTP(S) origins without a path.");
        foreach (var host in options.Server.Hosts)
            if (string.IsNullOrWhiteSpace(host) || host.Contains('*', StringComparison.Ordinal) ||
                !Uri.TryCreate("http://" + host, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 ||
                uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                Fail("server.hosts entries must be explicit hostnames or addresses with an optional port.");
        var data = UnixPath.Canonicalize(options.DataRoot);
        foreach (var root in options.Sources.AllowedRoots)
        {
            if (!Path.IsPathFullyQualified(root)) Fail("Every allowed root must be absolute.");
            if (!Directory.Exists(root)) Fail("Every allowed root must exist.");
            if (IsWithin(UnixPath.Canonicalize(root), data)) Fail("An allowed root must not be under data_root.");
        }
        if (!Path.IsPathFullyQualified(options.Sources.IncomingRoot)) Fail("incoming_root must be absolute.");
        var incoming = UnixPath.Canonicalize(options.Sources.IncomingRoot);
        if (IsWithin(incoming, data) || IsWithin(data, incoming)) Fail("incoming_root and data_root must be separate trees.");
        foreach (var listener in options.Server.Listeners)
        {
            if (listener is null || listener.Port is < 1 or > 65535) Fail("Listener port must be in 1..65535.");
            if (listener.Scheme is not ("http" or "https")) Fail("Listener scheme must be http or https.");
            if (listener.Scheme == "https" && string.IsNullOrWhiteSpace(listener.Certificate)) Fail("HTTPS listeners require a certificate.");
            if (!IPAddress.TryParse(listener.Bind, out var address) || IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address))
                Fail("Listener bind must be an explicit interface address.");
            if (!IsPrivateInterface(address)) Fail("Listeners must bind loopback or a private network interface.");
        }
        foreach (var provider in options.Providers.Values)
        {
            if (provider is null || string.IsNullOrWhiteSpace(provider.Kind)) Fail("Every provider requires an adapter kind.");
            if (provider.Endpoint is not null && (!Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(endpoint.UserInfo)))
                Fail("Provider endpoints must be credential-free HTTP(S) URLs.");
        }
        foreach (var trusted in options.Privacy.TrustedServices)
            if (!Uri.TryCreate(trusted, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https") || endpoint.AbsolutePath != "/" || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
                Fail("trusted_services entries must be exact HTTP(S) scheme/host/port origins.");
        foreach (var binding in new[] { options.Models.Chat, options.Models.Enrich, options.Models.Embed, options.Models.Rerank })
            if (binding is not null) ValidateBinding(binding, options, new HashSet<ModelBindingOptions>(ReferenceEqualityComparer.Instance));
        if (options.Auth.SessionIdleHours <= 0 || options.Auth.SessionAbsoluteDays <= 0 || options.Auth.CliSessionIdleHours <= 0 || options.Auth.CliSessionAbsoluteHours <= 0 || options.Auth.StepUpMinutes <= 0)
            Fail("Authentication lifetimes must be positive.");
    }

    private static void ValidateBinding(ModelBindingOptions binding, SecondBrainOptions options, HashSet<ModelBindingOptions> seen)
    {
        if (!seen.Add(binding)) Fail("Model fallbacks must not be recursive.");
        if (string.IsNullOrWhiteSpace(binding.Provider) || !options.Providers.TryGetValue(binding.Provider, out var provider)) Fail("Every model binding must resolve to a declared provider.");
        else
        {
            if (string.IsNullOrWhiteSpace(binding.Model)) Fail("Every model binding requires a model id.");
            if (options.Privacy.LocalOnly && !IsLocal(provider, options.Privacy)) Fail("local_only refuses a hosted model binding, including fallbacks.");
        }
        if (binding.Limits is null) Fail("Model limits must not be null.");
        if (binding.Dimensions is <= 0 || binding.Limits.EmbedDimensions is <= 0) Fail("Embedding dimensions must be positive.");
        if (binding.Dimensions.HasValue && binding.Limits.EmbedDimensions.HasValue && binding.Dimensions != binding.Limits.EmbedDimensions)
            Fail("Embedding dimensions conflict with limits.embed_dimensions.");
        if (binding.Fallback is not null) ValidateBinding(binding.Fallback, options, seen);
    }

    public static bool IsLocal(ProviderOptions provider, PrivacyOptions privacy)
    {
        if (provider.Kind is "in_process" or "in-process" && provider.Endpoint is null) return true;
        if (!Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out var endpoint)) return false;
        return privacy.TrustedServices.Any(text => Uri.TryCreate(text, UriKind.Absolute, out var trusted) &&
            string.Equals(endpoint.Scheme, trusted.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(endpoint.IdnHost, trusted.IdnHost, StringComparison.OrdinalIgnoreCase) && endpoint.Port == trusted.Port);
    }

    private static bool IsWithin(string path, string root) => path == root || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    public static bool IsPrivateInterface(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4
            ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 100 && bytes[1] is >= 64 and <= 127
            : (bytes[0] & 0xfe) == 0xfc || address.IsIPv6LinkLocal;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string message) => throw new ConfigurationException(message);
}
