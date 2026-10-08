using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Security;
using SecondBrain.Core.Storage;
using SecondBrain.Server.Auth;

namespace SecondBrain.Server.Tests.Support;

/// <summary>Lane D's host with real repositories backed by isolated test infrastructure.</summary>
public sealed class LaneDWebFactory : WebApplicationFactory<global::Program>
{
    public LaneDWebFactory(
        Action<SecondBrainOptions>? configureOptions = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var options = new SecondBrainOptions
        {
            DataRoot = System.IO.Path.Combine(Store.DirectoryPath, "data"),
            Server = new ServerOptions
            {
                Hosts = ["localhost", "private.test", "public.test"],
                Origins = ["http://localhost", "https://localhost", "http://private.test", "https://private.test", "http://public.test", "https://public.test"],
            },
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
        Options = new MutableOptionsMonitor<SecondBrainOptions>(options);
        ConfigureServices = configureServices;
    }

    /// <summary>Lane A's real stores, opened under a temporary data root; the account epoch is seeded as <c>brain init</c> would.</summary>
    public RealStoreAccessor Store { get; } = new();
    public RealStoreAccessor StateStore => Store;
    public TestKeyRing KeyRing { get; } = new();
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero));
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
            command.CommandText = "INSERT INTO meta(key, value) VALUES ('account_epoch', '1') ON CONFLICT(key) DO NOTHING;";
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
        base.Dispose(disposing);
        if (disposing)
        {
            Store.Dispose();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await Store.DisposeAsync();
    }
}
