using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Problems;
using SecondBrain.Server.Auth;
using SecondBrain.Server.Limits;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class LimitsTests
{
    [Fact]
    public async Task PerCredentialRate()
    {
        await using var factory = new LaneDWebFactory(options => options.Limits.PerCredential.RequestsPerMinute = 2);
        using var client = factory.CreatePrivateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", await CreateKeyAsync(factory, "limited"));
        using var first = await client.GetAsync("/sources");
        using var second = await client.GetAsync("/sources");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using var rejected = await client.PostAsJsonAsync("/sources", new { path = factory.Options.CurrentValue.Sources.AllowedRoots[0] });
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.Contains(ProblemTypes.LimitExceeded, await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.NotNull(rejected.Headers.RetryAfter);
        Assert.Equal(new AdmissionSnapshot(0, 0, 0), factory.Services.GetRequiredService<IAdmissionController>().Snapshot);
        await AssertNothingQueuedAsync(factory);

        client.DefaultRequestHeaders.Authorization = new("Bearer", await CreateKeyAsync(factory, "other"));
        using var other = await client.GetAsync("/sources");
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        factory.Clock.Advance(TimeSpan.FromSeconds(30));
        client.DefaultRequestHeaders.Authorization = new("Bearer", await CreateKeyAsync(factory, "fresh"));
        using var fresh = await client.GetAsync("/sources");
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [Fact]
    public async Task GlobalCapacity503()
    {
        await using var factory = new LaneDWebFactory(
            options => options.Limits.Global.QueuedJobs = 0,
            services =>
            {
                services.RemoveAll<IAdmissionController>();
                services.AddSingleton<AdmissionController>();
                services.AddSingleton<IAdmissionController>(provider => new PolicyAdmissionController(
                    provider.GetRequiredService<AdmissionController>(), new(AdmissionResource.Upload, QueuedJobs: 1, AdmittedBytes: 4096, Ingestion: true)));
            });
        using var client = factory.CreatePrivateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", await CreateKeyAsync(factory, "capacity"));
        using var rejected = await client.PostAsJsonAsync("/sources", new { path = factory.Options.CurrentValue.Sources.AllowedRoots[0] });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        Assert.Contains(ProblemTypes.Capacity, await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.NotNull(rejected.Headers.RetryAfter);
        Assert.Equal(new AdmissionSnapshot(0, 0, 0), factory.Services.GetRequiredService<IAdmissionController>().Snapshot);
        await AssertNothingQueuedAsync(factory);
    }

    [Fact]
    public void TokenBucketRefillsAndCredentialsAreIndependent()
    {
        var config = Configuration();
        config.Limits.PerCredential.RequestsPerMinute = 2;
        var clock = new FakeTimeProvider();
        var controller = Controller(config, clock);
        using var first = controller.TryReserve("one", new()).Reservation;
        using var second = controller.TryReserve("one", new()).Reservation;
        Assert.Equal(429, controller.TryReserve("one", new()).StatusCode);
        using var other = controller.TryReserve("two", new()).Reservation;
        Assert.NotNull(other);
        clock.Advance(TimeSpan.FromSeconds(29));
        var waiting = controller.TryReserve("one", new());
        Assert.Equal(429, waiting.StatusCode);
        Assert.Equal(1, waiting.RetryAfterSeconds);
        clock.Advance(TimeSpan.FromSeconds(1));
        using var replenished = controller.TryReserve("one", new()).Reservation;
        Assert.NotNull(replenished);
    }

    [Theory]
    [InlineData(AdmissionResource.Upload)]
    [InlineData(AdmissionResource.Search)]
    [InlineData(AdmissionResource.Turn)]
    [InlineData(AdmissionResource.Stream)]
    [InlineData(AdmissionResource.Circuit)]
    public void ConcurrentResourcePermitsAreReleased(AdmissionResource resource)
    {
        var config = Configuration();
        config.Limits.PerCredential.ConcurrentUploads = 1;
        config.Limits.PerCredential.ConcurrentSearches = 1;
        config.Limits.PerCredential.ConcurrentTurns = 1;
        config.Limits.PerCredential.SseStreams = 1;
        config.Limits.PerCredential.Circuits = 1;
        var controller = Controller(config);
        var first = controller.TryReserve("one", new(resource, QueuedJobs: 1, AdmittedBytes: 10));
        Assert.True(first.Accepted);
        Assert.Equal(429, controller.TryReserve("one", new(resource)).StatusCode);
        using var independent = controller.TryReserve("two", new(resource)).Reservation;
        Assert.NotNull(independent);
        first.Reservation!.Dispose();
        first.Reservation.Dispose();
        Assert.Equal(new AdmissionSnapshot(0, 0, 1), controller.Snapshot);
        using var reused = controller.TryReserve("one", new(resource)).Reservation;
        Assert.NotNull(reused);
    }

    [Fact]
    public void CapacityReservationIsAtomicAndRejectionDoesNotSpendRate()
    {
        var config = Configuration();
        config.Limits.PerCredential.RequestsPerMinute = 1;
        config.Limits.Global.QueuedJobs = 1;
        config.Limits.Global.AdmittedGb = 0.000000100m;
        var controller = Controller(config);
        var first = controller.TryReserve("one", new(QueuedJobs: 1, AdmittedBytes: 99));
        Assert.Equal(new AdmissionSnapshot(1, 99, 1), controller.Snapshot);
        Assert.Equal(503, controller.TryReserve("two", new(QueuedJobs: 1, AdmittedBytes: 1)).StatusCode);
        Assert.Equal(503, controller.TryReserve("two", new(AdmittedBytes: 2)).StatusCode);
        Assert.Equal(new AdmissionSnapshot(1, 99, 1), controller.Snapshot);
        first.Reservation!.Dispose();
        using var second = controller.TryReserve("two", new(QueuedJobs: 1, AdmittedBytes: 100)).Reservation;
        Assert.NotNull(second);
    }

    [Fact]
    public async Task ParallelAdmissionCannotOvercommitCapacity()
    {
        var config = Configuration();
        config.Limits.Global.QueuedJobs = 7;
        var controller = Controller(config);
        var decisions = await Task.WhenAll(Enumerable.Range(0, 64).Select(number => Task.Run(() => controller.TryReserve(number.ToString(System.Globalization.CultureInfo.InvariantCulture), new(QueuedJobs: 1)))));
        Assert.Equal(7, decisions.Count(decision => decision.Accepted));
        Assert.Equal(new AdmissionSnapshot(7, 0, 7), controller.Snapshot);
        foreach (var decision in decisions)
        {
            decision.Reservation?.Dispose();
        }

        Assert.Equal(new AdmissionSnapshot(0, 0, 0), controller.Snapshot);
    }

    [Theory]
    [InlineData(999_999_999L)]
    [InlineData(null)]
    public void LowDiskPausesIngestionAndAllowsSearch(long? freeBytes)
    {
        var config = Configuration();
        config.Limits.Global.DiskLowWaterGb = 1;
        var controller = new AdmissionController(new MutableOptionsMonitor<SecondBrainOptions>(config), new FakeTimeProvider(), new FixedDisk(freeBytes));
        Assert.Equal(503, controller.TryReserve("one", new(AdmissionResource.Upload, Ingestion: true)).StatusCode);
        Assert.Equal(new AdmissionSnapshot(0, 0, 0), controller.Snapshot);
        using var search = controller.TryReserve("one", new(AdmissionResource.Search)).Reservation;
        Assert.NotNull(search);
    }

    [Fact]
    public void ReloadedLimitsApplyToNewAdmissions()
    {
        var config = Configuration();
        var monitor = new MutableOptionsMonitor<SecondBrainOptions>(config);
        var controller = new AdmissionController(monitor, new FakeTimeProvider(), new FixedDisk(long.MaxValue));
        using var first = controller.TryReserve("one", new(AdmissionResource.Upload, QueuedJobs: 1)).Reservation;
        config.Limits.PerCredential.ConcurrentUploads = 1;
        config.Limits.Global.QueuedJobs = 1;
        monitor.NotifyChanged();
        Assert.Equal(429, controller.TryReserve("one", new(AdmissionResource.Upload)).StatusCode);
        Assert.Equal(503, controller.TryReserve("two", new(QueuedJobs: 1)).StatusCode);
    }

    [Fact]
    public async Task HandlerFailureReleasesReservation()
    {
        var controller = Controller(Configuration());
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("credential_id", "one")], "test"));
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new AdmissionPolicy(AdmissionResource.Upload, 1, 10)), "probe"));
        var middleware = new AdmissionMiddleware(_ => throw new InvalidOperationException("handler failed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context, controller));
        Assert.Equal(new AdmissionSnapshot(0, 0, 0), controller.Snapshot);
        Assert.False(context.Items.ContainsKey(AdmissionMiddleware.ReservationItem));
    }

    [Fact]
    public void UnknownAndNegativeResourceReservationsAreRejected()
    {
        var controller = Controller(Configuration());
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.TryReserve("one", new(QueuedJobs: -1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.TryReserve("one", new(AdmittedBytes: -1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.TryReserve("one", new((AdmissionResource)999)));
        Assert.Equal(new AdmissionSnapshot(0, 0, 0), controller.Snapshot);
    }

    private static SecondBrainOptions Configuration()
    {
        var config = new SecondBrainOptions();
        config.Limits.PerCredential.RequestsPerMinute = 10_000;
        config.Limits.Global.DiskLowWaterGb = 0;
        return config;
    }

    private static AdmissionController Controller(SecondBrainOptions config, FakeTimeProvider? clock = null) =>
        new(new MutableOptionsMonitor<SecondBrainOptions>(config), clock ?? new(), new FixedDisk(long.MaxValue));

    private static async Task<string> CreateKeyAsync(LaneDWebFactory factory, string name)
    {
        var repository = factory.Services.GetRequiredService<IAuthRepository>();
        var created = factory.Services.GetRequiredService<CredentialFactory>().CreateApiKey(name, new HashSet<Scope> { Scope.Admin }, await repository.GetEpochAsync());
        await repository.AddAsync(created.Record);
        return created.Plaintext;
    }

    private static async Task AssertNothingQueuedAsync(LaneDWebFactory factory)
    {
        await using var lease = await factory.Store.OpenReadConnectionAsync();
        Assert.Equal(0, await lease.Connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM sources"));
        Assert.Equal(0, await lease.Connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM usage"));
        var exists = await lease.Connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='jobs'");
        var jobs = exists == 0 ? 0 : await lease.Connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM jobs");
        Assert.Equal(0, jobs);
    }

    private sealed class FixedDisk(long? bytes) : IDiskCapacity
    {
        public long? AvailableBytes(string path) => bytes;
    }

    private sealed class PolicyAdmissionController(IAdmissionController actual, AdmissionPolicy policy) : IAdmissionController
    {
        public AdmissionSnapshot Snapshot => actual.Snapshot;
        public AdmissionDecision TryReserve(string credentialId, AdmissionPolicy ignored) => actual.TryReserve(credentialId, policy);
    }
}
