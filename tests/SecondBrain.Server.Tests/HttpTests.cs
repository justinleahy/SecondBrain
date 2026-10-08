using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using Microsoft.AspNetCore.DataProtection;
using SecondBrain.Core.Security;
using Microsoft.AspNetCore.Routing;
using SecondBrain.Core.Problems;
using SecondBrain.Server.Http;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class HttpTests
{
    [Fact]
    public async Task HostRejected()
    {
        await using var factory = new LaneDWebFactory();
        using var client = factory.CreatePrivateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Host = "untrusted.test";
        using var response = await client.SendAsync(request);
        await AssertProblem(response, HttpStatusCode.BadRequest, ProblemTypes.HostRejected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://attacker.test")]
    [InlineData("http://private.test/")]
    [InlineData("HTTP://private.test")]
    public async Task OriginRejected(string? origin)
    {
        await using var factory = new LaneDWebFactory();
        using var client = factory.CreatePrivateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
        { Content = JsonContent.Create(new { password = "irrelevant" }) };
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        using var response = await client.SendAsync(request);
        await AssertProblem(response, HttpStatusCode.Forbidden, ProblemTypes.OriginRejected);
        Assert.Equal(0, factory.KeyRing.HmacCalls);
    }

    [Theory]
    [InlineData("/auth/login")]
    [InlineData("/v1/auth/login")]
    public async Task LoginOriginCannotBeBypassedWithBearerHeader(string path)
    {
        await using var factory = new LaneDWebFactory();
        using var client = factory.CreatePrivateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        { Content = JsonContent.Create(new { password = "irrelevant" }) };
        request.Headers.Authorization = new("Bearer", "random-invalid-credential");
        using var response = await client.SendAsync(request);
        await AssertProblem(response, HttpStatusCode.Forbidden, ProblemTypes.OriginRejected);
        Assert.Equal(0, factory.KeyRing.HmacCalls);
    }

    [Fact]
    public async Task ForwardedIgnoredFromUntrusted()
    {
        await using var factory = new LaneDWebFactory(options => options.Server.TrustedProxies = ["127.0.0.1"]);
        _ = factory.CreatePrivateClient();
        var response = await factory.Server.SendAsync(context =>
        {
            context.Request.Method = "GET";
            context.Request.Path = "/health";
            context.Request.Host = new("private.test");
            context.Request.Scheme = "http";
            context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
            context.Request.Headers["X-Forwarded-Host"] = "attacker.test";
            context.Request.Headers["X-Forwarded-Proto"] = "https";
            context.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
        });
        Assert.Equal(200, response.Response.StatusCode);
        Assert.Equal("private.test", response.Request.Host.Host);
        Assert.Equal("http", response.Request.Scheme);
        Assert.Equal(IPAddress.Parse("203.0.113.9"), response.Connection.RemoteIpAddress);
    }

    [Fact]
    public async Task ForwardedHonoredOnlyForConfiguredProxy()
    {
        await using var factory = new LaneDWebFactory(options => options.Server.TrustedProxies = ["127.0.0.1"]);
        _ = factory.CreatePrivateClient();
        var response = await factory.Server.SendAsync(context =>
        {
            context.Request.Method = "GET";
            context.Request.Path = "/health";
            context.Request.Host = new("localhost");
            context.Request.Scheme = "http";
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.Request.Headers["X-Forwarded-Host"] = "private.test";
            context.Request.Headers["X-Forwarded-Proto"] = "https";
            context.Request.Headers["X-Forwarded-For"] = "10.0.0.2";
        });
        Assert.Equal(200, response.Response.StatusCode);
        Assert.Equal("private.test", response.Request.Host.Host);
        Assert.Equal("https", response.Request.Scheme);
        Assert.Equal(IPAddress.Parse("10.0.0.2"), response.Connection.RemoteIpAddress);
    }

    [Theory]
    [InlineData("stores")]
    [InlineData("migrations")]
    [InlineData("lock")]
    [InlineData("canary")]
    [InlineData("extractor")]
    [InlineData("provider:chat")]
    [InlineData("provider:enrich")]
    [InlineData("provider:embed")]
    public async Task ReadyReflectsEachContributor(string failed)
    {
        var names = new[] { "stores", "migrations", "lock", "canary", "extractor", "provider:chat", "provider:enrich", "provider:embed" };
        var contributors = names.Select(name => new FakeContributor(name)).ToArray();
        await using var factory = new LaneDWebFactory(configureServices: services =>
        {
            foreach (var contributor in contributors) services.AddSingleton<IReadinessContributor>(contributor);
        });
        foreach (var contributor in contributors) contributor.CheckedAt = factory.Clock.GetUtcNow();
        using var client = factory.CreatePrivateClient();
        using var green = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, green.StatusCode);
        contributors.Single(contributor => contributor.Name == failed).Ready = false;
        using var red = await client.GetAsync("/ready");
        var body = await AssertProblem(red, HttpStatusCode.ServiceUnavailable, ProblemTypes.Capacity);
        Assert.False(body.GetProperty("components").GetProperty(failed).GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task ReadyRequiresFreshProviderObservation()
    {
        var contributors = new[] { "stores", "migrations", "lock", "canary", "extractor", "provider:chat", "provider:enrich", "provider:embed" }
            .Select(name => new FakeContributor(name)).ToArray();
        await using var factory = new LaneDWebFactory(configureServices: services =>
        {
            foreach (var contributor in contributors) services.AddSingleton<IReadinessContributor>(contributor);
        });
        foreach (var contributor in contributors) contributor.CheckedAt = factory.Clock.GetUtcNow();
        factory.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        using var client = factory.CreatePrivateClient();
        using var response = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task ReadyFailsClosedWhenDependenciesAreMissing()
    {
        await using var factory = new LaneDWebFactory();
        using var client = factory.CreatePrivateClient();
        using var response = await client.GetAsync("/ready");
        await AssertProblem(response, HttpStatusCode.ServiceUnavailable, ProblemTypes.Capacity);
    }

    [Fact]
    public async Task OpenApiContainsEndpointRequestSchemas()
    {
        await using var factory = new LaneDWebFactory();
        using var client = factory.CreatePrivateClient();
        using var response = await client.GetAsync("/v1/openapi.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(document.GetProperty("paths").TryGetProperty("/auth/login", out var login));
        Assert.True(login.GetProperty("post").TryGetProperty("requestBody", out _));
        Assert.True(document.GetProperty("components").GetProperty("schemas").TryGetProperty("LoginRequest", out _));
    }

    [Fact]
    public async Task BlazorFrameworkAssetIsMounted()
    {
        await using var factory = new LaneDWebFactory();
        using var client = factory.CreatePrivateClient();
        using var response = await client.GetAsync("/_framework/blazor.web.js");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        var endpointNames = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>().Where(endpoint => endpoint.RoutePattern.RawText?.Contains("_framework", StringComparison.Ordinal) == true)
            .Select(endpoint => endpoint.RoutePattern.RawText);
        Assert.True(content.Contains("Blazor", StringComparison.Ordinal), response + " endpoints=" + string.Join(",", endpointNames));
    }

    [Fact]
    public async Task StaticAssetsApplyHostPolicy()
    {
        await using var factory = new LaneDWebFactory();
        using var client = factory.CreatePrivateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/_framework/blazor.web.js");
        request.Headers.Host = "attacker.test";
        using var response = await client.SendAsync(request);
        await AssertProblem(response, HttpStatusCode.BadRequest, ProblemTypes.HostRejected);
    }

    [Fact]
    public void FrameworkProtectionUsesKeyRingLazilyAndPurposeSeparation()
    {
        var ring = new TestKeyRing();
        var opened = false;
        var services = new ServiceCollection();
        services.AddSingleton<IKeyRing>(_ => { opened = true; return ring; });
        services.AddSingleton<IDataProtectionProvider, KeyRingDataProtectionProvider>();
        using var container = services.BuildServiceProvider();
        var protector = container.GetRequiredService<IDataProtectionProvider>().CreateProtector("framework-antiforgery-purpose");
        Assert.False(opened);
        byte[] plaintext = [1, 2, 3];
        var encrypted = protector.Protect(plaintext);
        Assert.True(opened);
        Assert.Equal(plaintext, ring.DataProtectionProvider.CreateProtector("secondbrain.antiforgery")
            .CreateProtector("framework-antiforgery-purpose").Unprotect(encrypted));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("100.64.0.2", true)]
    [InlineData("10.8.0.5", true)]
    [InlineData("fd12::1", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::", false)]
    [InlineData("1.1.1.1", false)]
    public void ListenersRejectPublicAndWildcardAddresses(string address, bool expected) =>
        Assert.Equal(expected, ConfiguredKestrelOptions.IsPrivateAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("0.0.0.0", "http", 7171)]
    [InlineData("1.1.1.1", "http", 7171)]
    [InlineData("127.0.0.1", "ftp", 7171)]
    [InlineData("127.0.0.1", "http", 0)]
    [InlineData("127.0.0.1", "https", 7443)]
    public async Task InvalidListenerConfigurationIsRefused(string bind, string scheme, int port)
    {
        await using var factory = new LaneDWebFactory(options => options.Server.Listeners =
            [new ListenerOptions { Bind = bind, Scheme = scheme, Port = port }]);
        Assert.Throws<OptionsValidationException>(() => new ConfiguredKestrelOptions(factory.Options).Configure(new KestrelServerOptions()));
    }

    private static async Task<JsonElement> AssertProblem(HttpResponseMessage response, HttpStatusCode status, string type)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(type, body.GetProperty("type").GetString());
        return body;
    }

    private sealed class FakeContributor(string name) : IReadinessContributor
    {
        public string Name { get; } = name;
        public bool Ready { get; set; } = true;
        public DateTimeOffset? CheckedAt { get; set; }
        public ValueTask<ReadinessStatus> CheckAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ReadinessStatus(Ready, CheckedAt: CheckedAt));
    }
}
