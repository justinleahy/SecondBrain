using System.Net.Sockets;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Problems;

namespace SecondBrain.Core.Privacy;

/// <summary>The replaceable TCP operation, allowing deterministic timeout tests.</summary>
public interface ICanaryConnector
{
    ValueTask ConnectAsync(string host, int port, CancellationToken cancellationToken);
}

/// <summary>Connects without sending application data, proxies or HTTP redirects.</summary>
public sealed class TcpCanaryConnector : ICanaryConnector
{
    public async ValueTask ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Startup and hourly host egress checks (SEC-17). A reachable public target proves the
/// host backstop is absent. A failed connection, including timeout, restores blocked state.
/// </summary>
public sealed class EgressCanary
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly PrivacyPolicy _policy;
    private readonly TimeProvider _timeProvider;
    private readonly ICanaryConnector _connector;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public EgressCanary(
        PrivacyPolicy policy,
        IOptionsMonitor<SecondBrainOptions> options,
        TimeProvider? timeProvider = null,
        ICanaryConnector? connector = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(options);
        _policy = policy;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _connector = connector ?? new TcpCanaryConnector();
    }

    /// <summary>Runs one bounded check and publishes its result; caller cancellation publishes nothing.</summary>
    public async ValueTask<CanaryState> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configuration = _policy.CaptureCanaryConfiguration();
            if (!configuration.Enabled)
            {
                return _policy.CanaryState;
            }

            var targetText = configuration.Target;
            var (host, port) = ParseTarget(targetText);
            using var deadline = new CancellationTokenSource(Timeout, _timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            var result = CanaryState.Blocked;
            try
            {
                await _connector.ConnectAsync(host, port, linked.Token).AsTask()
                    .WaitAsync(Timeout, _timeProvider, cancellationToken).ConfigureAwait(false);
                result = CanaryState.Reachable;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A timeout is a blocked canary, as required by SEC-17.
            }
            catch (Exception exception) when (exception is SocketException or TimeoutException or IOException)
            {
                // Firewall refusal, routing failure and timeout all mean the target is blocked.
            }

            cancellationToken.ThrowIfCancellationRequested();
            _policy.RecordCanaryResult(configuration, result);
            return result;
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>Hosted-service entry point: one immediate check, then TimeProvider-driven hourly checks.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await RunOnceAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(Interval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Requires an explicit host:port, including brackets around an IPv6 literal.</summary>
    public static (string Host, int Port) ParseTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target) || target.Contains('/', StringComparison.Ordinal) ||
            !Uri.TryCreate($"tcp://{target}", UriKind.Absolute, out var uri) ||
            uri.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(uri.Host) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            throw new PrivacyPolicyException(ProblemTypes.PrivacyPolicy, "The egress canary target must be an explicit host:port.");
        }

        return (uri.IdnHost.Trim('[', ']'), uri.Port);
    }
}
