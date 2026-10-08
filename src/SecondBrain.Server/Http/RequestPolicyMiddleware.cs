using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Problems;

namespace SecondBrain.Server.Http;

/// <summary>Marks an anonymous browser endpoint whose unsafe requests always require an Origin.</summary>
public sealed class BrowserFacingRequest;

/// <summary>Processes one trusted proxy hop; never trusts the ambient proxy defaults.</summary>
public sealed class TrustedForwardedHeadersMiddleware(
    RequestDelegate next, IOptionsMonitor<SecondBrainOptions> options, ILoggerFactory loggerFactory)
{
    public Task InvokeAsync(HttpContext context)
    {
        var server = options.CurrentValue.Server;
        var forwarded = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
            ForwardLimit = 1
        };
        forwarded.KnownProxies.Clear();
        forwarded.KnownIPNetworks.Clear();
        foreach (var proxy in server.TrustedProxies)
            if (IPAddress.TryParse(proxy, out var address)) forwarded.KnownProxies.Add(address);
        // An empty known-proxy collection means "trust everyone" to the framework.
        // Explicitly skip processing unless the socket peer is a configured proxy.
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || !forwarded.KnownProxies.Any(proxy =>
                proxy.Equals(remote) || proxy.MapToIPv6().Equals(remote.MapToIPv6())))
            return next(context);
        return new ForwardedHeadersMiddleware(next, loggerFactory, Options.Create(forwarded)).Invoke(context);
    }
}

/// <summary>Allowlisted Host and exact browser Origin checks after trusted forwarding.</summary>
public sealed class RequestPolicyMiddleware(RequestDelegate next, IOptionsMonitor<SecondBrainOptions> options)
{
    private static readonly string[] LocalHosts = ["localhost", "127.0.0.1", "[::1]"];

    public async Task InvokeAsync(HttpContext context)
    {
        var server = options.CurrentValue.Server;
        IEnumerable<string> hosts = server.Hosts.Count == 0 ? LocalHosts : server.Hosts;
        if (!context.Request.Host.HasValue || !hosts.Any(host => HostMatches(host, context.Request.Host)))
        {
            await ProblemResponses.WriteAsync(context, 400, ProblemTypes.HostRejected, "Host rejected");
            return;
        }

        var origins = context.Request.Headers.Origin;
        var unsafeMethod = !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) &&
            !HttpMethods.IsOptions(context.Request.Method) && !HttpMethods.IsTrace(context.Request.Method);
        var bearer = context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
        var endpoint = context.GetEndpoint();
        var browserFacing = endpoint?.Metadata.GetMetadata<BrowserFacingRequest>() is not null ||
            endpoint?.Metadata.GetMetadata<EndpointPolicy>()?.BrowserFacing == true || !bearer;
        if ((origins.Count > 0 && (origins.Count != 1 || !server.Origins.Contains(origins[0]!, StringComparer.Ordinal))) ||
            (unsafeMethod && browserFacing && origins.Count == 0))
        {
            await ProblemResponses.WriteAsync(context, 403, ProblemTypes.OriginRejected, "Origin rejected");
            return;
        }

        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
        context.Response.Headers["Referrer-Policy"] = "same-origin";
        await next(context);
    }

    private static bool HostMatches(string configured, HostString host)
    {
        if (string.IsNullOrWhiteSpace(configured) || configured.Contains('*', StringComparison.Ordinal)) return false;
        var allowed = new HostString(configured);
        return string.Equals(allowed.Host, host.Host, StringComparison.OrdinalIgnoreCase) &&
            (allowed.Port is null || allowed.Port == host.Port);
    }
}
