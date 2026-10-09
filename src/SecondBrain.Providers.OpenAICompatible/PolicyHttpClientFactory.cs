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
    private readonly IProviderEgressPolicy policy;
    private readonly IDnsResolver resolver;
    private static readonly HttpRequestOptionsKey<RequestPolicyContext> RequestContextKey = new("SecondBrain.ProviderPolicy");
    // The pool may hand a new connection to a queued request other than InitialRequestMessage.
    // Writes run on the writing request's own async flow, so they validate that request.
    private static readonly AsyncLocal<RequestPolicyContext?> WritingRequest = new();

    public PolicyHttpClientFactory(IProviderEgressPolicy policy, IDnsResolver resolver)
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
        if (!request.Options.TryGetValue(RequestContextKey, out var requestContext))
            throw new PrivacyPolicyException(SecondBrain.Core.Problems.ProblemTypes.PrivacyPolicy,
                "Provider connection has no policy context.");
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;
        var addresses = await resolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        policy.VerifyConnectionAddresses(host, port, addresses);
        policy.ValidateRequest(requestContext.Role, requestContext.Binding, requestContext.RequestUri, requestContext.Discovery);
        Exception? lastFailure = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
                // A reload can repin this origin while the connect is in flight. Check the
                // address actually dialed against the current pins before HTTP sees the socket,
                // and again before every write (including a TLS ClientHello).
                var stream = new PolicyCheckedStream(new NetworkStream(socket, ownsSocket: true), policy, host, port, address, requestContext);
                stream.Verify();
                return stream;
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

    private sealed class RequestPolicyHandler(IProviderEgressPolicy policy, IDnsResolver resolver,
        ModelRole role, IProviderBinding binding, bool discovery) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Provider request has no URI.");
            if (discovery && (request.Method != HttpMethod.Get || binding.Endpoint is null ||
                uri.AbsolutePath != binding.Endpoint.AbsolutePath.TrimEnd('/') + "/models"))
                throw new PrivacyPolicyException(SecondBrain.Core.Problems.ProblemTypes.PrivacyPolicy,
                    "Provider discovery transport cannot send inference requests.");
            // The checks below are per connection and per request. HTTP/3 bypasses ConnectCallback and
            // HTTP/2 multiplexing does not fit that request context, so this adapter currently sends exactly HTTP/1.1.
            request.Version = HttpVersion.Version11;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            var requestContext = new RequestPolicyContext(role, binding, uri, discovery);
            request.Options.Set(RequestContextKey, requestContext);
            WritingRequest.Value = requestContext;
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

    private sealed record RequestPolicyContext(ModelRole Role, IProviderBinding Binding, Uri RequestUri, bool Discovery);

    /// <summary>Re-checks the dialed address and the writing request against current policy before each write.</summary>
    private sealed class PolicyCheckedStream(NetworkStream inner, IProviderEgressPolicy policy, string host, int port,
        IPAddress address, RequestPolicyContext initial) : Stream
    {
        private readonly IPAddress[] connected = [address];

        public void Verify()
        {
            policy.VerifyConnectionAddresses(host, port, connected);
            var request = WritingRequest.Value ?? initial;
            if (request.RequestUri.Port != port ||
                !string.Equals(request.RequestUri.IdnHost.Trim('[', ']'), host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase))
                throw new PrivacyPolicyException(SecondBrain.Core.Problems.ProblemTypes.PrivacyPolicy,
                    "Provider request does not match its connection origin.");
            policy.ValidateRequest(request.Role, request.Binding, request.RequestUri, request.Discovery);
        }

        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override bool CanTimeout => inner.CanTimeout;
        public override int ReadTimeout { get => inner.ReadTimeout; set => inner.ReadTimeout = value; }
        public override int WriteTimeout { get => inner.WriteTimeout; set => inner.WriteTimeout = value; }
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override int ReadByte() => inner.ReadByte();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count)
            => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Write(ReadOnlySpan<byte> buffer)
            => WriteAsync(buffer.ToArray().AsMemory()).AsTask().GetAwaiter().GetResult();
        public override void WriteByte(byte value) => Write([value], 0, 1);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                var request = WritingRequest.Value ?? throw new PrivacyPolicyException(
                    SecondBrain.Core.Problems.ProblemTypes.PrivacyPolicy, "Provider write has no request policy context.");
                if (request.RequestUri.Port != port ||
                    !string.Equals(request.RequestUri.IdnHost.Trim('[', ']'), host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase))
                    throw new PrivacyPolicyException(SecondBrain.Core.Problems.ProblemTypes.PrivacyPolicy,
                        "Provider request does not match its connection origin.");
                return policy.AdmitWrite(request.Role, request.Binding, request.RequestUri, address, request.Discovery,
                    () => inner.WriteAsync(buffer, cancellationToken));
            }
            catch (Exception exception) { return ValueTask.FromException(exception); }
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
