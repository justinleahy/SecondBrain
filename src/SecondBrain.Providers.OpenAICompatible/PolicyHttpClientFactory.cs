using System.Net;
using System.Net.Sockets;
using SecondBrain.Core.Privacy;
using SecondBrain.Core.Providers;

namespace SecondBrain.Providers.OpenAICompatible;

/// <summary>The only provider HTTP construction service exposed in DI.</summary>
public interface IPolicyHttpClientFactory
{
    HttpClient CreateClient(ModelRole role, IProviderBinding binding, bool discovery = false);
}

/// <summary>Owns exactly one socket handler per daemon; clients add only request-specific policy context.</summary>
public sealed class PolicyHttpClientFactory : IPolicyHttpClientFactory, IDisposable
{
    private readonly SocketsHttpHandler sockets;
    private readonly PrivacyPolicy policy;
    private readonly IDnsResolver resolver;
    private static readonly HttpRequestOptionsKey<RequestPolicyContext> RequestContextKey = new("SecondBrain.ProviderPolicy");

    public PolicyHttpClientFactory(PrivacyPolicy policy, IDnsResolver resolver)
    {
        this.policy = policy;
        this.resolver = resolver;
        sockets = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            // Reload may repin an unchanged origin to new addresses. SocketsHttpHandler has
            // no pool-generation/flush API; zero lifetime ensures an old pinned socket is
            // never reused after a newly approved pin set. One handler still owns all sends.
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = ConnectAsync,
        };
    }

    public HttpClient CreateClient(ModelRole role, IProviderBinding binding, bool discovery = false)
    {
        var handler = new RequestPolicyHandler(policy, resolver, role, binding, discovery)
        {
            InnerHandler = new SharedSocketsHandler(sockets),
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var request = context.InitialRequestMessage;
        if (!request.Options.TryGetValue(RequestContextKey, out var requestContext) || request.RequestUri is null)
            throw new PrivacyPolicyException(SecondBrain.Core.Problems.ProblemTypes.PrivacyPolicy,
                "Provider connection has no policy context.");
        var addresses = await resolver.ResolveAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        policy.VerifyConnectionAddresses(context.DnsEndPoint.Host, context.DnsEndPoint.Port, addresses);
        policy.ValidateRequest(requestContext.Role, requestContext.Binding, request.RequestUri, requestContext.Discovery);
        Exception? lastFailure = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                policy.ValidateRequest(requestContext.Role, requestContext.Binding, request.RequestUri, requestContext.Discovery);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException exception)
            {
                lastFailure = exception;
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw new ProviderRequestException("https://secondbrain.dev/problems/provider-unavailable",
            "Provider connection failed.", retryable: true, innerException: lastFailure);
    }

    public void Dispose() => sockets.Dispose();

    // Disposing a binding-owned client must never dispose the daemon's shared socket pool.
    private sealed class SharedSocketsHandler(HttpMessageHandler handler) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker invoker = new(handler, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => invoker.SendAsync(request, cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing) invoker.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class RequestPolicyHandler(PrivacyPolicy policy, IDnsResolver resolver,
        ModelRole role, IProviderBinding binding, bool discovery) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Provider request has no URI.");
            if (discovery && (request.Method != HttpMethod.Get || binding.Endpoint is null ||
                uri.AbsolutePath != binding.Endpoint.AbsolutePath.TrimEnd('/') + "/models"))
                throw new PrivacyPolicyException(SecondBrain.Core.Problems.ProblemTypes.PrivacyPolicy,
                    "Provider discovery transport cannot send inference requests.");
            request.Options.Set(RequestContextKey, new RequestPolicyContext(role, binding, discovery));
            policy.ValidateRequest(role, binding, uri, discovery);
            var addresses = await resolver.ResolveAsync(uri.IdnHost, cancellationToken).ConfigureAwait(false);
            policy.VerifyResolvedAddresses(uri, addresses);
            // DNS can await; re-evaluate immediately before touching the socket pool to close reload races.
            policy.ValidateRequest(role, binding, uri, discovery);
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ProviderRequestException("https://secondbrain.dev/problems/provider-timeout",
                    "Provider request timed out.", retryable: true, innerException: exception);
            }
            catch (HttpRequestException exception) when (exception is not ProviderRequestException)
            {
                var privacyException = FindPrivacyFailure(exception);
                if (privacyException is not null) throw privacyException;
                throw new ProviderRequestException("https://secondbrain.dev/problems/provider-unavailable",
                    "Provider connection failed.", retryable: true, innerException: exception);
            }
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
            {
                response.Dispose();
                throw new ProviderRequestException("https://secondbrain.dev/problems/provider-redirect",
                    "Provider redirects are refused.", statusCode: (HttpStatusCode)status);
            }
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new ProviderRequestException("https://secondbrain.dev/problems/provider-response",
                    "Provider returned an unsuccessful response.", retryable: status == 429 || status >= 500,
                    statusCode: (HttpStatusCode)status);
            }
            return response;
        }

        private static PrivacyPolicyException? FindPrivacyFailure(Exception exception)
        {
            for (Exception? current = exception; current is not null; current = current.InnerException)
                if (current is PrivacyPolicyException privacy) return privacy;
            return null;
        }
    }

    private sealed record RequestPolicyContext(ModelRole Role, IProviderBinding Binding, bool Discovery);
}
