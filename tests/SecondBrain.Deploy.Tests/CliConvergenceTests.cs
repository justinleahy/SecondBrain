using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SecondBrain.Cli;
using SecondBrain.Cli.Commands;
using SecondBrain.Cli.Credentials;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Configuration;
using SecondBrain.Infrastructure.Configuration;
using SecondBrain.Infrastructure.Security;
using SecondBrain.Core.Storage;
using SecondBrain.Extractor;
using SecondBrain.MockProvider;
using SecondBrain.Server.Auth;
using SecondBrain.Server.Composition;
using SecondBrain.Server.Http;
using Xunit;
using SecondBrain.Storage.Auth;

namespace SecondBrain.Deploy.Tests;

public sealed class CliConvergenceTests
{
    private const string Password = "isolated-convergence-password";
    private static readonly PasswordParameters TestParameters = new(MemoryKiB: 1024, Iterations: 1);

    [Fact]
    public async Task Cli_InitPersistsCalibratedPolicyAndPrintsAdminKeyOnlyOnce()
    {
        using var root = new CliTempRoot();
        root.CreateRoots();
        var services = LocalServices(root);
        var first = await Run(root.Identities(["init", "--json"]), services);
        Assert.Equal(0, first.Exit);
        var key = first.Json.GetProperty("data").GetProperty("adminKey").GetString()!;
        Assert.StartsWith("sb_", key);
        Assert.Equal(1, Occurrences(first.Output, key));
        Assert.DoesNotContain(Password, first.Output);
        Assert.Empty(first.Error);
        var second = await Run(root.Identities(["init", "--json"]), services);
        Assert.Equal(0, second.Exit);
        Assert.False(second.Json.GetProperty("data").GetProperty("initialized").GetBoolean());
        Assert.DoesNotContain(key, second.Output);
        using var store = new SecondBrain.Storage.SqliteStateStore(root.Data);
        var repository = new AuthRepository(store, TimeProvider.System);
        Assert.Equal(1, await repository.GetEpochAsync());
        Assert.True(new PasswordHasher(TestParameters).Verify((await repository.GetAccountAsync())!.PasswordHash, Password));
        await using var read = await store.OpenReadConnectionAsync();
        var policy = await read.Connection.QuerySingleAsync<string>("SELECT value FROM meta WHERE key='password_parameters'");
        Assert.Equal(TestParameters, JsonSerializer.Deserialize<PasswordParameters>(policy));
        var row = Assert.Single((await repository.ListPageAsync("api_key", activeOnly: false, after: null, limit: 100)).Items);
        Assert.Equal("read,write,infer,admin", row.Scopes);
        Assert.DoesNotContain(key, row.Verifier);
    }

    [Fact]
    public async Task Cli_InitTextAlsoShowsOneTimeAdminKey()
    {
        using var root = new CliTempRoot();
        root.CreateRoots();
        var output = new StringWriter(CultureInfo.InvariantCulture);
        var error = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Equal(0, await Cli.Program.RunAsync(root.Identities(["init"]), LocalServices(root), output, error));
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("sb_", lines[1]);
        Assert.Empty(error.ToString());
        Assert.DoesNotContain(Password, output.ToString());
    }

    [Fact]
    public async Task Cli_ResetPasswordChangesHashPolicyAndEpochTogether()
    {
        using var root = new CliTempRoot();
        root.CreateRoots();
        Assert.Equal(0, (await Run(root.Identities(["init", "--json"]), LocalServices(root))).Exit);
        const string replacement = "replacement-password";
        var reset = await Run(root.Identities(["init", "--reset-password", "--json"]), LocalServices(root, replacement));
        Assert.Equal(0, reset.Exit);
        Assert.Equal(2, reset.Json.GetProperty("data").GetProperty("accountEpoch").GetInt64());
        Assert.DoesNotContain(replacement, reset.Output);
        using var store = new SecondBrain.Storage.SqliteStateStore(root.Data);
        var repository = new AuthRepository(store, TimeProvider.System);
        Assert.Equal(2, await repository.GetEpochAsync());
        var account = (await repository.GetAccountAsync())!;
        Assert.True(new PasswordHasher(TestParameters).Verify(account.PasswordHash, replacement));
        Assert.False(new PasswordHasher(TestParameters).Verify(account.PasswordHash, Password));
        var oldKey = Assert.Single((await repository.ListPageAsync("api_key", activeOnly: false, after: null, limit: 100)).Items);
        Assert.False(await repository.IsCurrentAsync(oldKey.Id, oldKey.Generation, oldKey.AccountEpoch));
    }

    [Fact]
    public async Task Cli_RotateRevokeAllBumpsDurableEpoch()
    {
        using var root = new CliTempRoot();
        root.CreateRoots();
        var services = LocalServices(root);
        Assert.Equal(0, (await Run(root.Identities(["init", "--json"]), services)).Exit);
        var result = await Run(root.Identities(["maintenance", "rotate-keys", "--revoke-all", "--json"]), services);
        Assert.Equal(0, result.Exit);
        using var store = new SecondBrain.Storage.SqliteStateStore(root.Data);
        Assert.Equal(2, await new AuthRepository(store, TimeProvider.System).GetEpochAsync());
    }

    [Fact]
    public async Task Cli_HttpLoginAndKeysUseDaemonAuthorityAndRetainNoSecretsInListings()
    {
        await using var daemon = await CliDaemon.CreateAsync();
        var login = await daemon.Run(["login", daemon.Origin.ToString(), "--name", "admin-cli", "--scopes", "admin,read", "--json"]);
        Assert.Equal(0, login.Exit);
        var stored = await daemon.Credentials.ReadAsync(daemon.Origin, "admin-cli", default);
        Assert.NotNull(stored);
        Assert.DoesNotContain(stored!, login.Output);
        Assert.DoesNotContain(Password, login.Output + login.Error);
        var create = await daemon.Run(["keys", "create", "--scopes", "read", "--json"]);
        Assert.Equal(0, create.Exit);
        var key = create.Json.GetProperty("data").GetProperty("key").GetString()!;
        var id = create.Json.GetProperty("data").GetProperty("id").GetString()!;
        Assert.Equal(1, Occurrences(create.Output, key));
        var list = await daemon.Run(["keys", "list", "--json"]);
        Assert.Equal(0, list.Exit);
        Assert.Contains(list.Json.GetProperty("data").EnumerateArray(), item => item.GetProperty("id").GetString() == id);
        Assert.DoesNotContain(key, list.Output);
        Assert.DoesNotContain("verifier", list.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, (await daemon.Run(["keys", "revoke", id, "--json"])).Exit);
        Assert.Equal(4, (await daemon.Run(["keys", "revoke", id, "--json"])).Exit);
        Assert.Equal(0, (await daemon.Run(["login", daemon.Origin.ToString(), "--name", "read-cli", "--json"])).Exit);
        Assert.Equal(4, (await daemon.Run(["keys", "list", "--credential-name", "read-cli", "--json"])).Exit);
    }

    [Fact]
    public async Task Cli_ProvidersListAndTestUsePolicyTransport()
    {
        await using var daemon = await CliDaemon.CreateAsync();
        await daemon.LoginAdmin();
        var listed = await daemon.Run(["providers", "list", "--json"]);
        Assert.Equal(0, listed.Exit);
        Assert.Contains("mock-chat", listed.Output);
        Assert.Contains("mock-embed", listed.Output);
        Assert.Equal(0, (await daemon.Run(["providers", "test", "mock", "--json"])).Exit);
        Assert.Equal(4, (await daemon.Run(["providers", "test", "missing", "--json"])).Exit);
        daemon.Mock.Services.GetRequiredService<MockProviderState>().Fault = MockFault.RateLimit;
        Assert.Equal(4, (await daemon.Run(["providers", "test", "--json"])).Exit);
    }

    [Fact]
    public async Task Cli_SessionsUseBrowserAuthenticationAndDurableEpochRevocation()
    {
        await using var daemon = await CliDaemon.CreateAsync();
        using var browser = daemon.Browser();
        using var response = await browser.PostAsJsonAsync("/auth/login", new { password = Password });
        response.EnsureSuccessStatusCode();
        var browserSession = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        await daemon.LoginAdmin();
        var list = await daemon.Run(["sessions", "list", "--json"]);
        Assert.Equal(0, list.Exit);
        Assert.Contains(list.Json.GetProperty("data").EnumerateArray(), item => item.GetProperty("id").GetString() == browserSession);
        Assert.Equal(0, (await daemon.Run(["sessions", "revoke", browserSession, "--json"])).Exit);
        using var current = await browser.GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, current.StatusCode);
        Assert.Equal(0, (await daemon.Run(["sessions", "revoke-all", "--json"])).Exit);
        Assert.Equal(4, (await daemon.Run(["keys", "list", "--json"])).Exit);
    }

    [Fact]
    public async Task Cli_ListingsTraverseEveryPagePastHistoricalRows()
    {
        await using var daemon = await CliDaemon.CreateAsync();
        using var browser = daemon.Browser();
        using var response = await browser.PostAsJsonAsync("/auth/login", new { password = Password });
        response.EnsureSuccessStatusCode();
        var browserSession = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        await daemon.LoginAdmin();
        await daemon.SeedCredentialsAsync("h", "session", 1100, historical: true);
        await daemon.SeedCredentialsAsync("a", "session", 150, historical: false);
        await daemon.SeedCredentialsAsync("k", "api_key", 1100, historical: true);
        var create = await daemon.Run(["keys", "create", "--scopes", "read", "--json"]);
        var created = create.Json.GetProperty("data").GetProperty("id").GetString()!;

        var keys = await daemon.Run(["keys", "list", "--json"]);
        Assert.Equal(0, keys.Exit);
        var keyIds = keys.Json.GetProperty("data").EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(keyIds.Length, keyIds.Distinct().Count());
        Assert.Contains(created, keyIds);
        Assert.Equal(1100, keyIds.Count(id => id.StartsWith('k')));

        var sessions = await daemon.Run(["sessions", "list", "--json"]);
        Assert.Equal(0, sessions.Exit);
        var sessionIds = sessions.Json.GetProperty("data").EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(Enumerable.Range(1, 150).Select(i => $"a{i:D5}").Append(browserSession), sessionIds);

        Assert.Equal(0, (await daemon.Run(["sessions", "revoke", "a00150", "--json"])).Exit);
        var after = await daemon.Run(["sessions", "list", "--json"]);
        Assert.DoesNotContain(after.Json.GetProperty("data").EnumerateArray(), item => item.GetProperty("id").GetString() == "a00150");
        Assert.Equal(150, after.Json.GetProperty("data").GetArrayLength());
    }

    [Theory]
    [InlineData("<http://attacker.test/keys?after=x>; rel=\"next\"")]
    [InlineData("<//attacker.test/keys?after=x>; rel=\"next\"")]
    [InlineData("<https://daemon.test/keys?after=x>; rel=\"next\"")]
    [InlineData("<http://daemon.test:8080/keys?after=x>; rel=\"next\"")]
    [InlineData("<http://user@daemon.test/keys?after=x>; rel=\"next\"")]
    [InlineData("</auth/sessions?after=x>; rel=\"next\"")]
    [InlineData("</keys?after=a>; rel=\"next\", </keys?after=b>; rel=next")]
    [InlineData("/keys?after=x; rel=\"next\"")]
    public async Task Cli_ListingRefusesNextLinksOutsideTheListing(string link)
    {
        var handler = new PagingHandler(_ => link);
        var failure = await Assert.ThrowsAsync<DaemonCommandException>(() => ((ICredentialCommands)PagedCommands(handler)).ListAsync(default));
        Assert.Equal("listing-link-rejected", failure.Result.Code);
        Assert.Equal(CliExitCode.Error, failure.Result.ExitCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://daemon.test/keys", request.ToString());
    }

    [Fact]
    public async Task Cli_ListingFailsInsteadOfLoopingOrTruncating()
    {
        var looping = new PagingHandler(_ => "</keys?after=same>; rel=\"next\"");
        var loop = await Assert.ThrowsAsync<DaemonCommandException>(() => ((ICredentialCommands)PagedCommands(looping)).ListAsync(default));
        Assert.Equal("listing-incomplete", loop.Result.Code);
        Assert.Equal(2, looping.Requests.Count);
        var restarting = new PagingHandler(_ => "</keys>; rel=next");
        await Assert.ThrowsAsync<DaemonCommandException>(() => ((ICredentialCommands)PagedCommands(restarting)).ListAsync(default));
        Assert.Single(restarting.Requests);

        var endless = new PagingHandler(page => $"</keys?after={page}>; rel=\"next\"");
        var exhausted = await Assert.ThrowsAsync<DaemonCommandException>(() => ((ICredentialCommands)PagedCommands(endless)).ListAsync(default));
        Assert.Equal("listing-incomplete", exhausted.Result.Code);
        Assert.Equal(HttpDaemonCommands.MaxListPages, endless.Requests.Count);
    }

    [Fact]
    public async Task Cli_ListingFollowsSameOriginNextLinksToTheEnd()
    {
        var handler = new PagingHandler(page => page switch
        {
            1 => "</keys?after=1&limit=1>; rel=\"next\"",
            2 => "<http://daemon.test/keys?after=2>; rel = \"next\", </keys?after=0>; rel=\"prev\"",
            _ => "</keys?after=0>; rel=\"prev\"",
        });
        var result = await ((ICredentialCommands)PagedCommands(handler)).ListAsync(default);
        Assert.Equal(CliExitCode.Ok, result.ExitCode);
        Assert.Equal(["k1", "k2", "k3"], ((JsonElement[])result.Data!).Select(item => item.GetProperty("id").GetString()));
        Assert.Equal(["/keys", "/keys?after=1&limit=1", "/keys?after=2"], handler.Requests.Select(uri => uri.PathAndQuery));
    }

    [Theory]
    [InlineData("length")]
    [InlineData("chunked")]
    public async Task Cli_DaemonBoundsLoginBodiesOnKestrel(string framing)
    {
        await using var daemon = await CliDaemon.CreateAsync();
        var oversized = Encoding.UTF8.GetBytes("{\"password\":\"" + new string('a', PasswordRequestMiddleware.MaxBodyBytes) + "\"}");
        Assert.Equal(413, await RawLoginAsync(daemon.Origin, framing, oversized, sendBody: framing == "chunked"));
        Assert.Equal(200, await RawLoginAsync(daemon.Origin, framing, JsonSerializer.SerializeToUtf8Bytes(new { password = Password }), sendBody: true));
    }

    [Fact]
    public async Task Cli_DoctorChecksConvergedDaemonAndReturnsZero()
    {
        await using var daemon = await CliDaemon.CreateAsync();
        await daemon.LoginAdmin();
        var result = await daemon.Run(["doctor", "--json"]);
        Assert.True(result.Exit == 0, result.Output);
        var checks = result.Json.GetProperty("data").EnumerateArray().ToArray();
        foreach (var name in new[] { "stores", "migrations", "lock", "canary", "extractor", "provider:chat", "provider:enrich", "provider:embed", "cloudflare-access.refresh", "trusted-services", "keyring.hmac-integrity" })
            Assert.Contains(checks, item => item.GetProperty("name").GetString() == name && item.GetProperty("passed").GetBoolean());
        Assert.All(checks, check => Assert.True(check.GetProperty("passed").GetBoolean(), check.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Cli_UnreachableDaemonReturnsExitThree()
    {
        using var root = new CliTempRoot();
        var endpoint = new Uri("http://127.0.0.1:" + FreePort());
        var credentials = new FileCredentialStore(Path.Combine(root.Path, "credentials"));
        await credentials.StoreAsync(endpoint, "cli-test", "sb_unreachable.private", default);
        var services = new CliServices { CredentialStore = credentials };
        var result = await Run(["keys", "list", "--url", endpoint.ToString(), "--credential-name", "cli-test", "--json"], services);
        Assert.Equal(3, result.Exit);
        Assert.DoesNotContain("sb_unreachable.private", result.Output + result.Error);
    }

    [Fact]
    public async Task Cli_DoctorUnreachablePreservesChecksAndReturnsExitThree()
    {
        using var root = new CliTempRoot();
        root.CreateRoots();
        var endpoint = new Uri("http://127.0.0.1:" + FreePort());
        var credentials = new FileCredentialStore(Path.Combine(root.Path, "credentials"));
        await credentials.StoreAsync(endpoint, "cli-test", "sb_unreachable.private", default);
        var services = new CliServices { ReadConfiguration = (_, _) => root.Options(), CredentialStore = credentials };
        var result = await Run(root.Identities(["doctor", "--url", endpoint.ToString(), "--credential-name", "cli-test",
            "--extractor-socket", Path.Combine(root.Path, "missing.sock"), "--json"]), services);
        Assert.Equal(3, result.Exit);
        Assert.Equal("daemon-unreachable", result.Json.GetProperty("code").GetString());
        var checks = result.Json.GetProperty("data").EnumerateArray().ToArray();
        Assert.Contains(checks, item => item.GetProperty("name").GetString() == "daemon.unreachable" && !item.GetProperty("passed").GetBoolean());
        Assert.Contains(checks, item => item.GetProperty("name").GetString() == root.Data && item.GetProperty("passed").GetBoolean());
        Assert.DoesNotContain("sb_unreachable.private", result.Output + result.Error);
    }

    private static CliServices LocalServices(CliTempRoot root, string password = Password) => new()
    {
        ReadConfiguration = (_, _) => root.Options(),
        ReadPassword = (_, _) => Task.FromResult(password),
        CalibratePassword = () => TestParameters,
    };
    private static async Task<CommandResult> Run(string[] args, CliServices services)
    {
        var output = new StringWriter(CultureInfo.InvariantCulture);
        var error = new StringWriter(CultureInfo.InvariantCulture);
        var exit = await Cli.Program.RunAsync(args, services, output, error);
        using var document = JsonDocument.Parse(output.ToString());
        return new(exit, document.RootElement.Clone(), output.ToString(), error.ToString());
    }
    private static HttpDaemonCommands PagedCommands(HttpMessageHandler handler)
    {
        var commands = new HttpDaemonCommands(() => new StaticCredentialStore(), (_, _) => Task.FromResult(Password),
            () => new HttpClient(handler, disposeHandler: false));
        commands.Configure(() => throw new InvalidOperationException("The configured origin is used."), "http://daemon.test", "paged");
        return commands;
    }

    /// <summary>A real Kestrel exchange: either a declared over-limit length with no body sent, or the body itself.</summary>
    private static async Task<int> RawLoginAsync(Uri origin, string framing, byte[] body, bool sendBody)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(origin.Host, origin.Port);
        var stream = tcp.GetStream();
        var authority = origin.GetLeftPart(UriPartial.Authority);
        var headers = $"POST /auth/login HTTP/1.1\r\nHost: {origin.Authority}\r\nOrigin: {authority}\r\nContent-Type: application/json\r\nConnection: close\r\n" +
            (framing == "chunked" ? "Transfer-Encoding: chunked\r\n" : $"Content-Length: {body.Length}\r\n") + "\r\n";
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
            if (sendBody && framing == "chunked")
            {
                foreach (var chunk in body.Chunk(4096))
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(chunk.Length.ToString("x", CultureInfo.InvariantCulture) + "\r\n"));
                    await stream.WriteAsync(chunk);
                    await stream.WriteAsync("\r\n"u8.ToArray());
                }
                await stream.WriteAsync("0\r\n\r\n"u8.ToArray());
            }
            else if (sendBody) await stream.WriteAsync(body);
        }
        catch (IOException) { } // The daemon may answer and close before an over-limit body is fully sent.
        using var reader = new StreamReader(stream, Encoding.ASCII);
        var status = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(status);
        return int.Parse(status!.Split(' ')[1], CultureInfo.InvariantCulture);
    }

    private sealed class StaticCredentialStore : ICredentialStore
    {
        public Task StoreAsync(Uri origin, string name, string key, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string?> ReadAsync(Uri origin, string name, CancellationToken cancellationToken) => Task.FromResult<string?>("sb_paged.secret");
    }

    /// <summary>Serves one-item pages and a caller-chosen Link header per page number.</summary>
    private sealed class PagingHandler(Func<int, string?> link) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { new { id = "k" + Requests.Count } }) };
            if (link(Requests.Count) is { } value) response.Headers.TryAddWithoutValidation("Link", value);
            return Task.FromResult(response);
        }
    }

    private static int Occurrences(string text, string value) => (text.Length - text.Replace(value, "", StringComparison.Ordinal).Length) / value.Length;
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    private sealed record CommandResult(int Exit, JsonElement Json, string Output, string Error);

    private sealed class CliDaemon : IAsyncDisposable
    {
        private readonly CliTempRoot root = new();
        private readonly CancellationTokenSource stopExtractor = new();
        private Task? extractor;
        private FileKeyRing? keyRing;
        private DataRootLock? rootLock;
        private ReloadingConfiguration? configuration;
        private WebApplication? daemon;
        private CliServices services = null!;
        private string configPath = null!;
        private string socketPath = null!;
        public Uri Origin { get; private set; } = null!;
        public FileCredentialStore Credentials { get; private set; } = null!;
        public WebApplication Mock { get; private set; } = null!;

        public static async Task<CliDaemon> CreateAsync()
        {
            var fixture = new CliDaemon();
            try { await fixture.Start(); return fixture; }
            catch { await fixture.DisposeAsync(); throw; }
        }
        private async Task Start()
        {
            root.CreateRoots();
            Mock = MockProviderApplication.Build(["--urls", "http://127.0.0.1:0"], builder => builder.Logging.ClearProviders());
            await Mock.StartAsync();
            var mockUrl = Mock.Urls.Single();
            Origin = new Uri("http://127.0.0.1:" + FreePort());
            socketPath = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "sb-cli-" + Guid.NewGuid().ToString("N")[..12] + ".sock");
            configPath = Path.Combine(root.Path, "config.yaml");
            await File.WriteAllTextAsync(configPath, $$"""
                data_root: '{{root.Data}}'
                server:
                  listeners: [{scheme: http, bind: 127.0.0.1, port: {{Origin.Port}}}]
                  hosts: ['127.0.0.1']
                  origins: ['{{Origin.GetLeftPart(UriPartial.Authority)}}']
                sources:
                  incoming_root: '{{root.Incoming}}'
                  allowed_roots: ['{{root.Incoming}}']
                providers:
                  mock: {kind: openai_compatible, endpoint: '{{mockUrl}}/v1'}
                models:
                  chat: {provider: mock, model: mock-chat, capabilities: {tools: true, streaming: true}, limits: {context_tokens: 8192, max_output_tokens: 1024} }
                  enrich: {provider: mock, model: mock-chat, limits: {context_tokens: 8192, max_output_tokens: 1024} }
                  embed: {provider: mock, model: mock-embed, limits: {embed_dimensions: 4, embed_max_input_tokens: 8192, embed_batch_max: 32} }
                privacy:
                  local_only: true
                  trusted_services: ['{{mockUrl}}']
                  egress_canary: false
                extractor:
                  socket_path: '{{socketPath}}'
                limits:
                  per_credential: {requests_per_min: 10000}
                  global: {disk_low_water_gb: 0}
                """);
            Credentials = new FileCredentialStore(Path.Combine(root.Path, "credentials"));
            services = new CliServices
            {
                CredentialStore = Credentials,
                ReadPassword = (_, _) => Task.FromResult(Password),
                CalibratePassword = () => TestParameters,
            };
            var initialized = await Run(["init", "--json"]);
            Assert.True(initialized.Exit == 0, initialized.Output);
            configuration = new ReloadingConfiguration(configPath, new YamlConfigurationLoader(environment: _ => null), watch: false);
            rootLock = DataRootLock.Acquire(root.Data);
            keyRing = services.CreateKeyRing(root.Data);
            var socketServer = new ExtractorSocketServer(new ExtractorServerOptions { SocketPath = socketPath }, NullLogger<ExtractorSocketServer>.Instance);
            extractor = socketServer.RunAsync(stopExtractor.Token);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(global::Program).Assembly.GetName().Name,
                EnvironmentName = "Testing",
            });
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(configuration);
            builder.Services.AddSingleton<ISecretResolver>(configurationLoader());
            builder.Services.AddSingleton<IOptionsMonitor<SecondBrainOptions>>(new SecondBrainOptionsMonitor(configuration));
            builder.Services.AddSingleton<IOptions<SecondBrainOptions>>(Options.Create(configuration.CurrentValue));
            builder.Services.AddSingleton<DataRootLock>(_ => rootLock!);
            builder.Services.AddSingleton<IKeyRing>(keyRing);
            // Production registers these beside root ownership enforcement in AddKeyRing.
            // This same-user isolated fixture supplies its real ring and already held lock directly.
            builder.Services.AddSingleton<IReadinessContributor, LockReadinessContributor>();
            builder.Services.AddSingleton<IReadinessContributor, ExtractorReadinessContributor>();
            builder.Services.AddStorage().AddDomain().AddProviders().AddPrivacy().AddHttpHost().AddAuth().AddLimits();
            daemon = builder.Build();
            daemon.MapGet("/health", () => Microsoft.AspNetCore.Http.Results.Ok());
            await daemon.StartAsync();

            ISecretResolver configurationLoader() => new YamlConfigurationLoader(environment: _ => null).Secrets;
        }
        public Task<CommandResult> Run(string[] args) => CliConvergenceTests.Run(root.Identities(args.Concat([
            "--config", configPath]).Concat(args.Contains("--credential-name", StringComparer.Ordinal) ? [] : new[] { "--credential-name", "admin-cli" }).ToArray()), services);
        public Task SeedCredentialsAsync(string prefix, string kind, int count, bool historical)
        {
            var now = DateTimeOffset.UtcNow;
            // Older than every credential the daemon issues; historical rows are revoked, earlier-epoch or expired.
            return daemon!.Services.GetRequiredService<SecondBrain.Storage.IStateStore>().QueueWriteAsync(async (connection, transaction, token) =>
                await connection.ExecuteAsync(new CommandDefinition("""
                    WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<@count)
                    INSERT INTO credentials (id,name,kind,verifier,scopes,generation,kid,account_epoch,idle_expires_at,absolute_expires_at,created_at,expires_at,revoked_at)
                    SELECT @prefix || printf('%05d', i), 'seeded', @kind, 'AAAA', 'read', 1, 'seed',
                      CASE WHEN @historical AND i % 4 = 1 THEN 0 ELSE 1 END,
                      CASE WHEN @historical AND i % 4 = 2 THEN @past ELSE @future END, @future, @createdAt,
                      CASE WHEN @historical AND i % 4 = 3 THEN @past END, CASE WHEN @historical AND i % 4 = 0 THEN @past END
                    FROM n
                    """, new { count, prefix, kind, historical, createdAt = AuthTime.Format(now.AddDays(-1)), past = AuthTime.Format(now.AddMinutes(-1)), future = AuthTime.Format(now.AddDays(1)) },
                    transaction, cancellationToken: token))).AsTask();
        }
        public async Task LoginAdmin() => Assert.Equal(0, (await Run(["login", Origin.ToString(), "--name", "admin-cli", "--scopes", "admin,read", "--json"])).Exit);
        public HttpClient Browser()
        {
            var browser = new HttpClient(new SocketsHttpHandler { UseCookies = true, AllowAutoRedirect = false, UseProxy = false }) { BaseAddress = Origin };
            browser.DefaultRequestHeaders.Add("Origin", Origin.GetLeftPart(UriPartial.Authority));
            return browser;
        }
        public async ValueTask DisposeAsync()
        {
            if (daemon is not null) { await daemon.StopAsync(); await daemon.DisposeAsync(); }
            stopExtractor.Cancel();
            if (extractor is not null) await extractor;
            stopExtractor.Dispose();
            rootLock?.Dispose();
            keyRing?.Dispose();
            configuration?.Dispose();
            if (Mock is not null) { await Mock.StopAsync(); await Mock.DisposeAsync(); }
            root.Dispose();
        }
    }
}
