using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.ExceptionServices;
using SecondBrain.Core.Auth;
using SecondBrain.Extractor;
using SecondBrain.MockProvider;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Configuration;
using SecondBrain.Infrastructure.Configuration;
using SecondBrain.Infrastructure.Security;
using SecondBrain.Core.Storage;
using SecondBrain.Storage;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SecondBrain.Server.Tests.Support;

/// <summary>Lane D's host with real repositories backed by isolated test infrastructure.</summary>
public sealed class LaneDWebFactory : WebApplicationFactory<global::Program>
{
    public LaneDWebFactory(
        Action<SecondBrainOptions>? configureOptions = null,
        Action<IServiceCollection>? configureServices = null)
    {
        try
        {
            MockProvider = MockProviderApplication.Build(["--urls", "http://127.0.0.1:0"]);
            MockProvider.StartAsync().GetAwaiter().GetResult();
            var endpoint = MockProvider.Urls.Single();
            // Darwin's sockaddr_un path budget is smaller than the default per-user temp path.
            SocketDirectory = Path.Combine("/tmp", "sb-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(SocketDirectory);
            ExtractorSocket = Path.Combine(SocketDirectory, "e.sock");
            ExtractorTask = new ExtractorSocketServer(new ExtractorServerOptions { SocketPath = ExtractorSocket },
                NullLogger<ExtractorSocketServer>.Instance).RunAsync(ExtractorCancellation.Token);
            var options = new SecondBrainOptions
            {
                DataRoot = System.IO.Path.Combine(Store.DirectoryPath, "data"),
                Server = new ServerOptions
                {
                    Listeners = [new ListenerOptions { Bind = "127.0.0.1", Port = 7171 }],
                    Hosts = ["localhost", "private.test", "public.test"],
                    Origins = ["http://localhost", "https://localhost", "http://private.test", "https://private.test", "http://public.test", "https://public.test"],
                },
                Providers = new() { ["mock"] = new ProviderOptions { Kind = "openai_compatible", Endpoint = endpoint + "/v1", Trusted = true } },
                Models = new ModelRolesOptions
                {
                    Chat = new ModelBindingOptions { Provider = "mock", Model = "mock-chat", Capabilities = new() { Tools = true, Streaming = true }, Limits = new() { ContextTokens = 8192, MaxOutputTokens = 1024 } },
                    Enrich = new ModelBindingOptions { Provider = "mock", Model = "mock-chat", Capabilities = new() { Tools = true, Streaming = true }, Limits = new() { ContextTokens = 8192, MaxOutputTokens = 1024 } },
                    Embed = new ModelBindingOptions { Provider = "mock", Model = "mock-embed", Limits = new() { EmbedDimensions = 4, EmbedMaxInputTokens = 8192, EmbedBatchMax = 32 } },
                },
                Privacy = new PrivacyOptions { LocalOnly = true, EgressCanary = false, TrustedServices = [endpoint] },
                Extractor = new ExtractorOptions { SocketPath = ExtractorSocket },
                Sources = new SourcesOptions
                {
                    AllowedRoots = [System.IO.Path.Combine(Store.DirectoryPath, "allowed")],
                    IncomingRoot = System.IO.Path.Combine(Store.DirectoryPath, "incoming"),
                },
                Limits = new LimitsOptions
                {
                    PerCredential = new CredentialLimitsOptions { RequestsPerMinute = 10_000 },
                    Global = new GlobalLimitsOptions { DiskLowWaterGb = 0 },
                },
            };
            Directory.CreateDirectory(options.DataRoot);
            Directory.CreateDirectory(options.Sources.AllowedRoots[0]);
            Directory.CreateDirectory(options.Sources.IncomingRoot);
            configureOptions?.Invoke(options);
            ConfigPath = Path.Combine(Store.DirectoryPath, "config.yaml");
            File.WriteAllText(ConfigPath, new SerializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance)
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull).Build().Serialize(options));
            Configuration = new ReloadingConfiguration(ConfigPath, new YamlConfigurationLoader(environment: _ => null), watch: false);
            Options = new MutableOptionsMonitor<SecondBrainOptions>(Configuration.CurrentValue);
            Configuration.Changed += Options.Update;
            ConfigureServices = configureServices;
        }
        catch (Exception constructionFailure)
        {
            // A failed constructor never reaches the caller's using statement.
            try { DisposeResourcesAsync().GetAwaiter().GetResult(); }
            catch (Exception cleanupFailure) { constructionFailure.Data["FixtureCleanupFailure"] = cleanupFailure; }
            throw;
        }
    }

    public Microsoft.AspNetCore.Builder.WebApplication MockProvider { get; }
    public string ConfigPath { get; }
    public ReloadingConfiguration Configuration { get; }
    public string ExtractorSocket { get; }
    private string SocketDirectory { get; }
    private CancellationTokenSource ExtractorCancellation { get; } = new();
    private Task ExtractorTask { get; }
    private int resourcesDisposed;

    /// <summary>Lane A's real stores, opened under a temporary data root; the account epoch and instance id are seeded as <c>brain init</c> would.</summary>
    public RealStoreAccessor Store { get; } = new();
    public RealStoreAccessor StateStore => Store;
    public TestKeyRing KeyRing { get; } = new();
    // Starts at the real clock so cookies the daemon issues are not already expired for HttpClient; every test advances relative to this.
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
    public MutableOptionsMonitor<SecondBrainOptions> Options { get; }
    public MutableOptionsMonitor<SecondBrainOptions> OptionsMonitor => Options;
    public Action<IServiceCollection>? ConfigureServices { get; set; }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        Store.Attach(host.Services.GetRequiredService<IStateStore>());
        Store.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO meta(key, value) VALUES ('account_epoch', '1'), ('instance_id', $instance) ON CONFLICT(key) DO NOTHING;";
            var instance = command.CreateParameter();
            instance.ParameterName = "$instance";
            instance.Value = Ulid.NewUlid().ToString();
            command.Parameters.Add(instance);
            await command.ExecuteNonQueryAsync(token);
            return 0;
        }).AsTask().GetAwaiter().GetResult();
        return host;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ReloadingConfiguration>();
            services.AddSingleton(Configuration);
            services.RemoveAll<DataRootLock>();
            services.AddSingleton(_ => DataRootLock.Acquire(Options.CurrentValue.DataRoot));
            services.RemoveAll<IKeyRing>();
            services.AddSingleton<IKeyRing>(KeyRing);
            services.RemoveAll<IDataProtectionProvider>();
            services.AddSingleton(KeyRing.DataProtectionProvider);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.RemoveAll<IOptionsMonitor<SecondBrainOptions>>();
            services.AddSingleton<IOptionsMonitor<SecondBrainOptions>>(Options);
            services.RemoveAll<IOptions<SecondBrainOptions>>();
            services.AddSingleton<IOptions<SecondBrainOptions>>(Options);
            services.RemoveAll<PasswordParameters>();
            services.AddSingleton(new PasswordParameters(Version: 1, MemoryKiB: 1024, Iterations: 1, Lanes: 1));
            ConfigureServices?.Invoke(services);
        });
    }

    public HttpClient CreatePrivateClient(bool handleCookies = true) => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("http://private.test"),
        AllowAutoRedirect = false,
        HandleCookies = handleCookies,
    });

    public async Task SeedAccountAsync(string password = "test-password", CancellationToken cancellationToken = default)
    {
        var factory = Services.GetRequiredService<CredentialFactory>();
        var repository = Services.GetRequiredService<IAuthRepository>();
        await repository.SaveAccountAsync(factory.CreateAccount(password), cancellationToken: cancellationToken);
    }

    public async Task<CreatedCredential> CreateKeyAsync(
        IReadOnlySet<Scope> scopes,
        string name = "test-key",
        DateTimeOffset? expiresAt = null,
        CancellationToken cancellationToken = default)
    {
        var factory = Services.GetRequiredService<CredentialFactory>();
        var repository = Services.GetRequiredService<IAuthRepository>();
        var epoch = await repository.GetEpochAsync(cancellationToken);
        var created = factory.CreateApiKey(name, scopes, epoch, expiresAt);
        await repository.AddAsync(created.Record, cancellationToken);
        return created;
    }

    protected override void Dispose(bool disposing)
    {
        try { base.Dispose(disposing); }
        finally
        {
            if (disposing) DisposeResourcesAsync().GetAwaiter().GetResult();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        try { await base.DisposeAsync().ConfigureAwait(false); }
        finally { await DisposeResourcesAsync().ConfigureAwait(false); }
    }

    private async Task DisposeResourcesAsync()
    {
        if (Interlocked.Exchange(ref resourcesDisposed, 1) != 0) return;
        var failures = new List<Exception>();
        // Each resource gets a cleanup attempt even when cancellation or a worker fails.
        await CleanupAsync(() => { ExtractorCancellation.Cancel(); return ValueTask.CompletedTask; }).ConfigureAwait(false);
        if (ExtractorTask is not null)
            await CleanupAsync(() => new ValueTask(ExtractorTask)).ConfigureAwait(false);
        if (MockProvider is not null)
            await CleanupAsync(() => MockProvider.DisposeAsync()).ConfigureAwait(false);
        await CleanupAsync(() => { Configuration?.Dispose(); return ValueTask.CompletedTask; }).ConfigureAwait(false);
        await CleanupAsync(() => { ExtractorCancellation.Dispose(); return ValueTask.CompletedTask; }).ConfigureAwait(false);
        await CleanupAsync(() =>
        {
            if (SocketDirectory is not null && Directory.Exists(SocketDirectory)) Directory.Delete(SocketDirectory, recursive: true);
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
        await CleanupAsync(() => Store.DisposeAsync()).ConfigureAwait(false);
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Test fixture cleanup failed.", failures);

        async ValueTask CleanupAsync(Func<ValueTask> action)
        {
            try { await action().ConfigureAwait(false); }
            catch (Exception failure) { failures.Add(failure); }
        }
    }
}
