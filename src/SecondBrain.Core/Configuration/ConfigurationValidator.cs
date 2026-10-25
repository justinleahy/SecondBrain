using System.Net;

namespace SecondBrain.Core.Configuration;

/// <summary>Cross-field validation at startup and before publishing a reload.</summary>
public static class ConfigurationValidator
{
    public static void Validate(SecondBrainOptions options, IHostPathInspector paths)
    {
        if (!Path.IsPathFullyQualified(options.DataRoot)) Fail("data_root must be absolute.");
        if (options.Enrichment is null) Fail("enrichment must not be null.");
        options.Enrichment.Validate();
        if (options.Classification is null) Fail("classification must not be null.");
        options.Classification.Validate();
        if (options.Retention is null) Fail("retention must not be null.");
        options.Retention.Validate();
        if (options.Backup is null || options.Backup.Keep is < 1 or > 1000 || options.Backup.SegmentMib != 1 || !options.Backup.IncludeTrash
            || !Path.IsPathFullyQualified(options.Backup.KeyFile) || options.Backup.ExternalRoots is null || options.Backup.ExternalRoots.Count > 128)
            Fail("Backup requires keep in 1..1000, one-MiB segments, trash included, an absolute key_file and at most 128 external roots.");
        if (IsWithin(paths.Canonicalize(options.Backup.KeyFile), paths.Canonicalize(options.DataRoot)))
            Fail("The backup key must be outside data_root.");
        if (options.Calendar is null || options.Calendar.PastMonths is < 0 or > 120 || options.Calendar.FutureMonths is < 1 or > 120)
            Fail("calendar.past_months must be in 0..120 and future_months in 1..120.");
        if (options.Assistant is null || options.Assistant.MaxToolCallsPerTurn is < 1 or > 32 ||
            options.Assistant.MaxReadDocuments is < 1 or > 100 || options.Assistant.MaxReadBytes is < 1 or > 1_000_000 ||
            options.Assistant.PendingTtlHours is < 1 or > 720) Fail("assistant limits are outside their supported bounds.");
        if (options.Retrieval is null || options.Retrieval.MaxExactCandidates is < 1 or > 10_000_000) Fail("retrieval.max_exact_candidates must be in 1..10000000.");
        if (options.Ingest is null || options.Ingest.SyncEmbedDeadlineMs is < 0 or > 30000) Fail("ingest.sync_embed_deadline_ms must be in 0..30000.");
        if (options.Chunking is null || options.Chunking.MaxTokens is < 32 or > 8192 || options.Chunking.TargetTokens < 16 ||
            options.Chunking.TargetTokens > options.Chunking.MaxTokens || options.Chunking.OverlapTokens < 0 || options.Chunking.OverlapTokens >= options.Chunking.TargetTokens)
            Fail("chunking requires max_tokens in 32..8192, target_tokens in 16..max_tokens, and overlap_tokens in 0..target_tokens-1.");
        if (!TimeOnly.TryParseExact(options.Ingest.DeepRescanAt, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
            Fail("ingest.deep_rescan_at must be a 24-hour HH:mm time.");
        if (options.Extractor is null || !Path.IsPathFullyQualified(options.Extractor.SocketPath)) Fail("extractor.socket_path must be absolute.");
        if (options.Server is null || options.Privacy is null || options.Models is null || options.Providers is null || options.Sources is null || options.Auth is null || options.Limits is null || options.Logging is null)
            Fail("M0 configuration sections must not be null.");
        if (options.Sources.AllowedRoots is null || options.Server.Listeners is null || options.Server.Hosts is null || options.Server.Origins is null || options.Server.TrustedProxies is null || options.Privacy.TrustedServices is null || options.Privacy.ControlPlaneEgress is null || options.Limits.PerCredential is null || options.Limits.Global is null)
            Fail("M0 configuration collections and limit groups must not be null.");
        if (options.Limits.Global.CalendarOccurrences is < 1 or > 100000) Fail("limits.global.calendar_occurrences must be in 1..100000.");
        if (options.Sources.CalendarAllowedOrigins is null || options.Sources.CalendarAllowedOrigins.Count > 128)
            Fail("sources.calendar_allowed_origins must contain at most 128 exact HTTPS origins.");
        foreach (var origin in options.Sources.CalendarAllowedOrigins)
        {
            try { _ = Sources.CalendarLink.ValidateOrigin(origin); }
            catch (Sources.CalendarSubscriptionException) { Fail("sources.calendar_allowed_origins requires exact HTTPS origins without credentials, paths or queries."); }
        }
        if (options.Sources.CalendarSubscriptions is null || options.Sources.CalendarSubscriptions.Count > 128)
            Fail("sources.calendar_subscriptions must contain at most 128 entries.");
        var calendarIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var calendar in options.Sources.CalendarSubscriptions)
        {
            if (calendar is null || string.IsNullOrWhiteSpace(calendar.Name) || !CalendarSubscriptionOptions.IsId(calendar.Id) || !calendarIds.Add(calendar.Id) ||
                !CalendarSubscriptionOptions.IsReference(calendar.UrlReference))
                Fail("Calendar subscriptions require distinct bounded ids and a single ${NAME} URL reference.");
            try { Sources.CalendarSubscriptionService.Validate(calendar.Name, calendar.PollIntervalSeconds); }
            catch (Sources.CalendarSubscriptionException) { Fail("Calendar subscriptions require a bounded name and a refresh interval of at least 300 seconds."); }
        }
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
        foreach (var root in options.Backup.ExternalRoots)
            if (!Path.IsPathFullyQualified(root) || !paths.DirectoryExists(root)
                || !options.Sources.AllowedRoots.Any(allowed => IsWithin(paths.Canonicalize(root), paths.Canonicalize(allowed))))
                Fail("Backup external roots must exist below configured allowed source roots.");
        var data = paths.Canonicalize(options.DataRoot);
        foreach (var root in options.Sources.AllowedRoots)
        {
            if (!Path.IsPathFullyQualified(root)) Fail("Every allowed root must be absolute.");
            if (!paths.DirectoryExists(root)) Fail("Every allowed root must exist.");
            if (IsWithin(paths.Canonicalize(root), data)) Fail("An allowed root must not be under data_root.");
        }
        if (!Path.IsPathFullyQualified(options.Sources.IncomingRoot)) Fail("incoming_root must be absolute.");
        var incoming = paths.Canonicalize(options.Sources.IncomingRoot);
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
        if (binding.Capabilities is null) Fail("Model capabilities must not be null.");
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
