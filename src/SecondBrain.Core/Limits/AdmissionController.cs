using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;

namespace SecondBrain.Core.Limits;

/// <summary>Token buckets and concurrency permits share a lock with global capacity admission.</summary>
public sealed class AdmissionController(IOptionsMonitor<SecondBrainOptions> options, TimeProvider timeProvider, IDiskCapacity disk)
    : IAdmissionController
{
    private readonly object _sync = new();
    private readonly Dictionary<string, CredentialBucket> _credentials = new(StringComparer.Ordinal);
    private int _jobs;
    private long _bytes;
    private int _active;
    private int _calls;

    public AdmissionSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return new(_jobs, _bytes, _active);
            }
        }
    }

    public AdmissionDecision TryReserve(string credentialId, AdmissionPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialId);
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.QueuedJobs < 0 || policy.AdmittedBytes < 0 || !Enum.IsDefined(policy.Resource))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        var config = options.CurrentValue;
        if (policy.Ingestion && config.Limits.Global.DiskLowWaterGb > 0)
        {
            var free = disk.AvailableBytes(config.DataRoot);
            if (free is null || free < ToBytes(config.Limits.Global.DiskLowWaterGb))
            {
                return Reject(503, 30, "Ingestion is paused because free disk space is below the low-water mark.");
            }
        }

        lock (_sync)
        {
            var now = timeProvider.GetTimestamp();
            var limit = Math.Max(0, config.Limits.PerCredential.RequestsPerMinute);
            if (!_credentials.TryGetValue(credentialId, out var bucket))
            {
                bucket = new CredentialBucket(limit, now);
                _credentials.Add(credentialId, bucket);
            }

            var elapsed = Math.Max(0, timeProvider.GetElapsedTime(bucket.LastRefill, now).TotalSeconds);
            bucket.Tokens = Math.Min(limit, bucket.Tokens + (elapsed * limit / 60d));
            bucket.LastRefill = now;
            if (bucket.Tokens < 1)
            {
                var retry = limit == 0 ? 60 : (int)Math.Ceiling((1 - bucket.Tokens) * 60 / limit);
                return Reject(429, Math.Max(1, retry), "The credential request rate is exhausted.");
            }

            var resource = (int)policy.Resource;
            if (policy.Resource != AdmissionResource.None && bucket.Permits[resource] >= ConcurrencyLimit(config.Limits.PerCredential, policy.Resource))
            {
                return Reject(429, 1, "The credential concurrency limit is exhausted.");
            }

            var maxJobs = Math.Max(0, config.Limits.Global.QueuedJobs);
            var maxBytes = ToBytes(config.Limits.Global.AdmittedGb);
            if (policy.QueuedJobs > maxJobs - _jobs || policy.AdmittedBytes > maxBytes - _bytes)
            {
                return Reject(503, 5, "The instance capacity cannot admit this work.");
            }

            bucket.Tokens--;
            bucket.Active++;
            bucket.Permits[resource]++;
            _jobs += policy.QueuedJobs;
            _bytes += policy.AdmittedBytes;
            _active++;
            if (++_calls % 256 == 0)
            {
                foreach (var stale in _credentials.Where(entry => entry.Value.Active == 0 && timeProvider.GetElapsedTime(entry.Value.LastRefill, now) > TimeSpan.FromHours(1)).Select(entry => entry.Key).ToArray())
                {
                    _credentials.Remove(stale);
                }
            }

            return new(new Reservation(this, bucket, policy), 0, 0, null);
        }
    }

    private void Release(CredentialBucket bucket, AdmissionPolicy policy)
    {
        lock (_sync)
        {
            bucket.Permits[(int)policy.Resource]--;
            bucket.Active--;
            _jobs -= policy.QueuedJobs;
            _bytes -= policy.AdmittedBytes;
            _active--;
        }
    }

    private static AdmissionDecision Reject(int status, int retry, string detail) => new(null, status, retry, detail);

    private static int ConcurrencyLimit(CredentialLimitsOptions config, AdmissionResource resource) => Math.Max(0, resource switch
    {
        AdmissionResource.Upload => config.ConcurrentUploads,
        AdmissionResource.Search => config.ConcurrentSearches,
        AdmissionResource.Turn => config.ConcurrentTurns,
        AdmissionResource.Stream => config.SseStreams,
        AdmissionResource.Circuit => config.Circuits,
        _ => int.MaxValue,
    });

    private static long ToBytes(decimal gigabytes) => (long)Math.Clamp(gigabytes * 1_000_000_000m, 0, long.MaxValue);

    private sealed class CredentialBucket(double tokens, long timestamp)
    {
        public double Tokens { get; set; } = tokens;
        public long LastRefill { get; set; } = timestamp;
        public int Active { get; set; }
        public int[] Permits { get; } = new int[Enum.GetValues<AdmissionResource>().Length];
    }

    private sealed class Reservation(AdmissionController owner, CredentialBucket bucket, AdmissionPolicy policy) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(bucket, policy);
            }
        }
    }
}
