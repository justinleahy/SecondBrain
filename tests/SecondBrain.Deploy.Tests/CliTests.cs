using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using SecondBrain.Cli;
using SecondBrain.Cli.Commands;
using SecondBrain.Cli.Credentials;
using SecondBrain.Cli.Provisioning;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Security;
using SecondBrain.Extractor;
using Xunit;

namespace SecondBrain.Deploy.Tests;

public sealed class CliTests
{
    [Theory]
    [InlineData("keys", "create", "--scopes", "read")]
    [InlineData("keys", "list")]
    [InlineData("keys", "revoke", "id")]
    [InlineData("providers", "list")]
    [InlineData("providers", "test")]
    [InlineData("providers", "test", "local")]
    [InlineData("sessions", "list")]
    [InlineData("sessions", "revoke", "id")]
    [InlineData("sessions", "revoke-all")]
    [InlineData("login", "http://127.0.0.1:7171")]
    public async Task Cli_UnboundCommandsReturnJsonPrecondition(params string[] arguments)
    {
        var (exit, json, error) = await Run(arguments.Concat(["--json"]).ToArray());
        Assert.Equal(4, exit);
        Assert.Equal(4, json.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Empty(error);
        Assert.Contains("convergence", json.RootElement.GetProperty("message").GetString());
        json.Dispose();
    }

    [Theory]
    [InlineData("missing-command")]
    [InlineData("keys", "create")]
    [InlineData("keys", "create", "--scopes", "read,root")]
    [InlineData("sessions", "revoke")]
    [InlineData("init", "--provision", "--reset-password")]
    [InlineData("init", "--dry-run")]
    [InlineData("login", "https://name:secret@brain.example.com")]
    [InlineData("login", "https://brain.example.com/path")]
    public async Task Cli_UsageFailuresHaveExitTwoAndJson(params string[] arguments)
    {
        var (exit, json, error) = await Run(arguments.Concat(["--json"]).ToArray());
        Assert.Equal(2, exit);
        Assert.Equal("usage", json.RootElement.GetProperty("code").GetString());
        Assert.Empty(error);
        json.Dispose();
    }

    [Fact]
    public async Task Cli_DryRunCreatesNothingAndReportsAllOverrides()
    {
        using var temporary = new CliTempRoot();
        var target = Path.Combine(temporary.Path, "uncreated");
        var (exit, json, _) = await Run(["init", "--provision", "--dry-run", "--deployment", "none", "--data-root", Path.Combine(target, "data"),
            "--incoming-root", Path.Combine(target, "incoming"), "--config-dir", Path.Combine(target, "etc"), "--runtime-dir", Path.Combine(target, "run"), "--json"]);
        Assert.Equal(0, exit);
        Assert.False(Directory.Exists(target));
        Assert.True(json.RootElement.GetProperty("data").GetProperty("dryRun").GetBoolean());
        Assert.Contains(Path.Combine(target, "data"), json.RootElement.GetRawText());
        json.Dispose();
    }

    [Fact]
    public async Task Cli_LoginStoresCredentialWithoutPrintingIt()
    {
        using var temporary = new CliTempRoot();
        var origin = new Uri("https://brain.example.com");
        var store = new FileCredentialStore(Path.Combine(temporary.Path, "credentials"));
        var services = new CliServices { Login = new LoginStub(), CredentialStore = store };
        var (exit, json, error) = await Run(["login", origin.ToString(), "--name", "laptop", "--json"], services);
        Assert.Equal(0, exit);
        Assert.DoesNotContain(LoginStub.Secret, json.RootElement.GetRawText());
        Assert.DoesNotContain(LoginStub.Secret, error);
        Assert.Equal(LoginStub.Secret, await store.ReadAsync(origin, "laptop", default));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Directory.GetFiles(Path.Combine(temporary.Path, "credentials")).Single()));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.Combine(temporary.Path, "credentials")));
        }
        json.Dispose();
    }

    [Theory]
    [InlineData(3)]
    [InlineData(1)]
    public async Task Cli_UsesDaemonUnreachableAndErrorExitCodes(int expected)
    {
        var services = new CliServices { Providers = new ProviderFailureStub(expected) };
        var (exit, json, _) = await Run(["providers", "list", "--json"], services);
        Assert.Equal(expected, exit);
        Assert.Equal(expected, json.RootElement.GetProperty("exitCode").GetInt32());
        Assert.DoesNotContain("sensitive", json.RootElement.GetRawText());
        json.Dispose();
    }

    [Fact]
    public async Task Cli_InitializesKeyRingWhileHoldingDataRootLock()
    {
        using var temporary = new CliTempRoot();
        temporary.CreateRoots();
        var services = new CliServices { ReadConfiguration = (_, _) => temporary.Options(), Initialization = new InitializationStub() };
        var (exit, json, _) = await Run(temporary.Identities(["init", "--json"]), services);
        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(temporary.Data, "keyring", "hmac.json")));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(temporary.Data, "keyring"), "key-*.xml"));
        Assert.DoesNotContain("secret", json.RootElement.GetRawText());
        json.Dispose();
    }

    [Fact]
    public async Task Cli_RotationRetainsPreviousKidsAndRevokeAllRequiresBinding()
    {
        using var temporary = new CliTempRoot();
        temporary.CreateRoots();
        var services = new CliServices { ReadConfiguration = (_, _) => temporary.Options(), Initialization = new InitializationStub() };
        var initialized = await Run(temporary.Identities(["init", "--json"]), services);
        initialized.Json.Dispose();
        var manifest = Path.Combine(temporary.Data, "keyring", "hmac.json");
        var before = File.ReadAllText(manifest);
        var denied = await Run(temporary.Identities(["maintenance", "rotate-keys", "--revoke-all", "--json"]), services);
        Assert.Equal(4, denied.Exit);
        Assert.Equal(before, File.ReadAllText(manifest));
        denied.Json.Dispose();
        var rotated = await Run(temporary.Identities(["maintenance", "rotate-keys", "--json"]), services);
        Assert.Equal(0, rotated.Exit);
        using var previous = JsonDocument.Parse(before);
        using var current = JsonDocument.Parse(File.ReadAllText(manifest));
        Assert.Equal(2, current.RootElement.GetProperty("Keys").EnumerateObject().Count());
        Assert.True(current.RootElement.GetProperty("Keys").TryGetProperty(previous.RootElement.GetProperty("ActiveKid").GetString()!, out _));
        rotated.Json.Dispose();
    }

    [Fact]
    public async Task Cli_SecondProcessCannotServeLockedRootAndReturnsFour()
    {
        using var temporary = new CliTempRoot();
        temporary.CreateRoots();
        var config = Path.Combine(temporary.Path, "config.yaml");
        await File.WriteAllTextAsync(config, $"data_root: '{temporary.Data}'\nsources:\n  incoming_root: '{temporary.Incoming}'\n  allowed_roots: ['{temporary.Incoming}']\n");
        using var rootLock = DataRootLock.Acquire(temporary.Data);
        var executable = typeof(Cli.Program).Assembly.Location;
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(executable);
        foreach (var value in temporary.Identities(["serve", "--config", config, "--json"])) info.ArgumentList.Add(value);
        using var process = Process.Start(info)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);
        Assert.Equal(4, process.ExitCode);
        Assert.Empty(error);
        using var result = JsonDocument.Parse(output);
        Assert.Contains("already locked", result.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Cli_DoctorReportsWrongModesAndUnavailableExtractor()
    {
        using var temporary = new CliTempRoot();
        temporary.CreateRoots();
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary.Data, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead);
        var services = new CliServices { ReadConfiguration = (_, _) => temporary.Options() };
        var (exit, json, _) = await Run(temporary.Identities(["doctor", "--extractor-socket", Path.Combine(temporary.Path, "missing.sock"), "--json"]), services);
        Assert.Equal(4, exit);
        var checks = json.RootElement.GetProperty("data").EnumerateArray().ToArray();
        Assert.Contains(checks, item => item.GetProperty("name").GetString() == temporary.Data && !item.GetProperty("passed").GetBoolean());
        Assert.Contains(checks, item => item.GetProperty("name").GetString() == "extractor.socket" && !item.GetProperty("passed").GetBoolean());
        json.Dispose();
    }

    [Fact]
    public async Task Cli_FileCredentialStoreRejectsReadableSecret()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temporary = new CliTempRoot();
        var store = new FileCredentialStore(Path.Combine(temporary.Path, "credentials"));
        var origin = new Uri("http://127.0.0.1:7171");
        await store.StoreAsync(origin, "local", LoginStub.Secret, default);
        File.SetUnixFileMode(Directory.GetFiles(Path.Combine(temporary.Path, "credentials")).Single(), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        await Assert.ThrowsAsync<CliPreconditionException>(() => store.ReadAsync(origin, "local", default));
    }

    [Fact]
    public async Task Cli_RevokeAllRotationCallsAccountEpochHook()
    {
        using var temporary = new CliTempRoot();
        temporary.CreateRoots();
        var epoch = new EpochStub();
        var services = new CliServices { ReadConfiguration = (_, _) => temporary.Options(), EpochRevoker = epoch };
        var result = await Run(temporary.Identities(["maintenance", "rotate-keys", "--revoke-all", "--json"]), services);
        Assert.Equal(0, result.Exit);
        Assert.Equal(1, epoch.Calls);
        Assert.True(result.Json.RootElement.GetProperty("data").GetProperty("epochRevoked").GetBoolean());
        result.Json.Dispose();
    }

    [Fact]
    public async Task Cli_ServePropagatesProcessOptionsWithoutMutatingEnvironment()
    {
        using var temporary = new CliTempRoot();
        temporary.CreateRoots();
        var daemon = new DaemonStub();
        var services = new CliServices { ReadConfiguration = (_, _) => temporary.Options(), Daemon = daemon };
        var previous = Environment.GetEnvironmentVariable("SECONDBRAIN_DATA_ROOT");
        var result = await Run(temporary.Identities(["serve", "--config", "/custom/config.yaml", "--data-root", temporary.Data,
            "--secrets-dir", Path.Combine(temporary.Path, "secrets"), "--json"]), services);
        Assert.Equal(0, result.Exit);
        Assert.Equal("/custom/config.yaml", daemon.Options!.ConfigPath);
        Assert.Equal(temporary.Data, daemon.Options.DataRoot);
        Assert.Equal(Path.Combine(temporary.Path, "secrets"), daemon.Options.SecretsDirectory);
        Assert.Equal(RootSecurityValidator.CurrentUid(), daemon.Options.SyncUid);
        Assert.Equal(previous, Environment.GetEnvironmentVariable("SECONDBRAIN_DATA_ROOT"));
        result.Json.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cli_DoctorPingsExtractorAndChecksHmacIntegrity(bool corruptHmac)
    {
        if (OperatingSystem.IsWindows()) return;
        using var temporary = new CliTempRoot();
        temporary.CreateRoots();
        var services = new CliServices { ReadConfiguration = (_, _) => ReadyOptions(temporary), DoctorExtension = new DoctorStub() };
        using (services.CreateKeyRing(temporary.Data)) { }
        if (corruptHmac) await File.WriteAllTextAsync(Path.Combine(temporary.Data, "keyring", "hmac.json"), "{\"ActiveKid\":\"private-key-material\",\"Keys\":{}}");
        var socketPath = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "brain-doctor-" + Guid.NewGuid().ToString("N")[..12] + ".sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var answer = Task.Run(async () =>
        {
            using var client = await listener.AcceptAsync(timeout.Token);
            ExtractorSocketServer.HandleConnection(client, timeout.Token);
        }, timeout.Token);
        try
        {
            var result = await Run(temporary.Identities(["doctor", "--extractor-socket", socketPath, "--json"]), services);
            await answer;
            Assert.Equal(corruptHmac ? 4 : 0, result.Exit);
            var checks = result.Json.RootElement.GetProperty("data").EnumerateArray().ToArray();
            Assert.Contains(checks, item => item.GetProperty("name").GetString() == "extractor.socket" && item.GetProperty("passed").GetBoolean());
            Assert.Contains(checks, item => item.GetProperty("name").GetString() == "keyring.hmac-integrity" && item.GetProperty("passed").GetBoolean() == !corruptHmac);
            Assert.DoesNotContain("private-key-material", result.Json.RootElement.GetRawText());
            result.Json.Dispose();
        }
        finally { File.Delete(socketPath); }
    }

    private static SecondBrainOptions ReadyOptions(CliTempRoot temporary)
    {
        var options = temporary.Options();
        options.Server.Listeners = [new ListenerOptions { Bind = "127.0.0.1", Port = 7171 }];
        options.Providers.Add("local", new ProviderOptions { Kind = "in_process" });
        options.Models.Chat = new ModelBindingOptions { Provider = "local", Model = "chat" };
        options.Models.Enrich = new ModelBindingOptions { Provider = "local", Model = "enrich" };
        options.Models.Embed = new ModelBindingOptions { Provider = "local", Model = "embed" };
        return options;
    }

    private static async Task<(int Exit, JsonDocument Json, string Error)> Run(string[] arguments, CliServices? services = null)
    {
        var output = new StringWriter(CultureInfo.InvariantCulture);
        var error = new StringWriter(CultureInfo.InvariantCulture);
        var exit = await Cli.Program.RunAsync(arguments, services, output, error);
        return (exit, JsonDocument.Parse(output.ToString()), error.ToString());
    }

    private sealed class LoginStub : ILoginCommands
    {
        public const string Secret = "sb_test.an-api-secret-that-must-not-be-printed";
        public Task<LoginCredential> AcquireAsync(Uri origin, string name, IReadOnlyList<string> scopes, CancellationToken cancellationToken)
        { Assert.Equal(["read"], scopes); return Task.FromResult(new LoginCredential(Secret, "test")); }
    }
    private sealed class ProviderFailureStub(int expected) : IProviderCommands
    {
        public Task<CliResult> ListAsync(CancellationToken cancellationToken) => expected == 3 ? throw new HttpRequestException("sensitive") : throw new InvalidOperationException("sensitive");
        public Task<CliResult> TestAsync(string? name, CancellationToken cancellationToken) => ListAsync(cancellationToken);
    }
    private sealed class InitializationStub : IInitializationCommands
    {
        public Task<CliResult> InitializeAsync(SecondBrainOptions options, CancellationToken cancellationToken)
        { Assert.Throws<DataRootLockedException>(() => DataRootLock.Acquire(options.DataRoot)); return Task.FromResult(CliResult.Ok("Initialized.")); }
        public Task<CliResult> ResetPasswordAsync(SecondBrainOptions options, CancellationToken cancellationToken) => InitializeAsync(options, cancellationToken);
    }
    private sealed class EpochStub : IAccountEpochRevoker
    {
        public int Calls { get; private set; }
        public ValueTask BumpEpochAsync(CancellationToken cancellationToken = default) { Calls++; return ValueTask.CompletedTask; }
    }
    private sealed class DaemonStub : IDaemonCommands
    {
        public DaemonLaunchOptions? Options { get; private set; }
        public Task<int> ServeAsync(DaemonLaunchOptions options, CancellationToken cancellationToken) { Options = options; return Task.FromResult(0); }
    }
    private sealed class DoctorStub : IDoctorExtension
    {
        public Task<IReadOnlyList<DoctorCheck>> CheckAsync(SecondBrainOptions options, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DoctorCheck>>([new("daemon.integration", true, "Daemon diagnostics passed.")]);
    }
}

internal sealed class CliTempRoot : IDisposable
{
    public string Path { get; } = UnixPath.Canonicalize(Directory.CreateTempSubdirectory("secondbrain-cli-").FullName);
    public string Data => System.IO.Path.Combine(Path, "data");
    public string Incoming => System.IO.Path.Combine(Path, "incoming");
    public void CreateRoots()
    {
        Directory.CreateDirectory(Data); Directory.CreateDirectory(Incoming);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Data, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(Incoming, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        }
    }
    public SecondBrainOptions Options() => new() { DataRoot = Data, Sources = new() { IncomingRoot = Incoming, AllowedRoots = [Incoming] } };
    public string[] Identities(string[] arguments) => arguments.Concat(["--daemon-uid", RootSecurityValidator.CurrentUid().ToString(CultureInfo.InvariantCulture),
        "--daemon-gid", RootSecurityValidator.CurrentGid().ToString(CultureInfo.InvariantCulture), "--sync-uid", RootSecurityValidator.CurrentUid().ToString(CultureInfo.InvariantCulture)]).ToArray();
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
}
