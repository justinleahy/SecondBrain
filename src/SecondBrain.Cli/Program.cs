using System.CommandLine;
using SecondBrain.Cli.Commands;
using SecondBrain.Cli.Provisioning;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Security;

namespace SecondBrain.Cli;

public static class Program
{
    public static Task<int> Main(string[] args) => RunAsync(args);

    /// <summary>Runs brain with injectable lane adapters and isolated output for tests.</summary>
    public static async Task<int> RunAsync(string[] args, CliServices? services = null, TextWriter? output = null, TextWriter? error = null,
        CancellationToken cancellationToken = default)
    {
        services ??= new CliServices();
        output ??= Console.Out;
        error ??= Console.Error;
        var json = new Option<bool>("--json") { Description = "Write a structured JSON result.", Recursive = true };
        var config = new Option<string>("--config") { Description = "Configuration file.", Recursive = true,
            DefaultValueFactory = _ => Environment.GetEnvironmentVariable("SECONDBRAIN_CONFIG") ?? "/etc/secondbrain/config.yaml" };
        var secrets = new Option<string?>("--secrets-dir") { Description = "Read-only external secrets directory.", Recursive = true,
            DefaultValueFactory = _ => Environment.GetEnvironmentVariable("SECONDBRAIN_SECRETS_DIRECTORY") ?? Environment.GetEnvironmentVariable("SECONDBRAIN_SECRETS_DIR") };
        var dataRoot = new Option<string?>("--data-root") { Description = "Override data_root.", Recursive = true,
            DefaultValueFactory = _ => Environment.GetEnvironmentVariable("SECONDBRAIN_DATA_ROOT") };
        var daemonUid = new Option<uint>("--daemon-uid") { Recursive = true, DefaultValueFactory = _ => Identity("SECONDBRAIN_DAEMON_UID", RootSecurityValidator.CurrentUid() == 0 ? 1654 : RootSecurityValidator.CurrentUid()) };
        var daemonGid = new Option<uint>("--daemon-gid") { Recursive = true, DefaultValueFactory = _ => Identity("SECONDBRAIN_DAEMON_GID", RootSecurityValidator.CurrentGid() == 0 ? 1654 : RootSecurityValidator.CurrentGid()) };
        var syncUid = new Option<uint>("--sync-uid") { Recursive = true, DefaultValueFactory = _ => Identity("SECONDBRAIN_SYNC_UID", 1656) };
        var root = new RootCommand("SecondBrain administration CLI.");
        foreach (var option in new Option[] { json, config, secrets, dataRoot, daemonUid, daemonGid, syncUid }) root.Options.Add(option);

        SecondBrainOptions Load(ParseResult parse, string? positionalRoot = null)
        {
            var options = services.ReadConfiguration(parse.GetValue(config)!, parse.GetValue(secrets));
            var selectedRoot = positionalRoot ?? parse.GetValue(dataRoot);
            if (selectedRoot is not null) options.DataRoot = selectedRoot;
            ConfigurationValidator.Validate(options);
            return options;
        }
        void Bind(Command command, Func<ParseResult, CancellationToken, Task<CliResult>> handler) => command.SetAction(async (parse, token) =>
        {
            CliResult result;
            try { result = await handler(parse, token); }
            catch (CliUsageException ex) { result = new(CliExitCode.Usage, "usage", ex.Message); }
            catch (DaemonUnreachableException ex) { result = new(CliExitCode.DaemonUnreachable, "daemon-unreachable", ex.Message); }
            catch (HttpRequestException) { result = new(CliExitCode.DaemonUnreachable, "daemon-unreachable", "The daemon could not be reached."); }
            catch (Exception ex) when (ex is CliPreconditionException or ConfigurationException or DataRootLockedException or UnauthorizedAccessException)
            { result = new(CliExitCode.PreconditionFailed, "precondition-failed", ex.Message); }
            catch (OperationCanceledException) { result = new(CliExitCode.Error, "cancelled", "Command cancelled."); }
            catch (Exception) { result = new(CliExitCode.Error, "error", "Command failed; inspect the installation and daemon logs."); }
            return await CliOutput.WriteAsync(result, parse.GetValue(json), output, error);
        });

        var init = new Command("init", "Provision a host or initialize the stores and account.");
        var initialRoot = new Argument<string?>("data-root") { Arity = ArgumentArity.ZeroOrOne };
        init.Arguments.Add(initialRoot);
        var provision = new Option<bool>("--provision");
        var reset = new Option<bool>("--reset-password");
        var dryRun = new Option<bool>("--dry-run");
        var skipUsers = new Option<bool>("--skip-users") { Description = "Use numeric identities already provisioned in a container image." };
        var incoming = new Option<string>("--incoming-root") { DefaultValueFactory = _ => "/srv/secondbrain-incoming" };
        var configDir = new Option<string>("--config-dir") { DefaultValueFactory = _ => "/etc/secondbrain" };
        var runtimeDir = new Option<string>("--runtime-dir") { DefaultValueFactory = _ => "/run/secondbrain" };
        var unitsDir = new Option<string>("--units-dir") { DefaultValueFactory = _ => "/etc/systemd/system" };
        var tmpfilesDir = new Option<string>("--tmpfiles-dir") { DefaultValueFactory = _ => "/etc/tmpfiles.d" };
        var composeDir = new Option<string>("--compose-dir") { DefaultValueFactory = _ => "/opt/secondbrain" };
        var templates = new Option<string>("--templates") { DefaultValueFactory = _ => Path.Combine(AppContext.BaseDirectory, "deploy") };
        var deployment = new Option<string>("--deployment") { DefaultValueFactory = _ => "systemd" };
        var extractorUid = new Option<uint>("--extractor-uid") { DefaultValueFactory = _ => 1655 };
        foreach (var option in new Option[] { provision, reset, dryRun, skipUsers, incoming, configDir, runtimeDir, unitsDir, tmpfilesDir, composeDir, templates, deployment, extractorUid }) init.Options.Add(option);
        Bind(init, async (parse, token) =>
        {
            if (parse.GetValue(provision) && parse.GetValue(reset)) throw new CliUsageException("--provision and --reset-password are mutually exclusive.");
            if (!parse.GetValue(provision) && (parse.GetValue(dryRun) || parse.GetValue(skipUsers))) throw new CliUsageException("--dry-run and --skip-users require --provision.");
            if (parse.GetValue(initialRoot) is not null && parse.GetValue(dataRoot) is not null) throw new CliUsageException("Choose the data-root argument or --data-root, not both.");
            if (parse.GetValue(provision))
            {
                var options = new ProvisionOptions
                {
                    DataRoot = parse.GetValue(initialRoot) ?? parse.GetValue(dataRoot) ?? "/srv/secondbrain", IncomingRoot = parse.GetValue(incoming)!,
                    ConfigDirectory = parse.GetValue(configDir)!, RuntimeDirectory = parse.GetValue(runtimeDir)!, UnitsDirectory = parse.GetValue(unitsDir)!,
                    TmpFilesDirectory = parse.GetValue(tmpfilesDir)!,
                    ComposeDirectory = parse.GetValue(composeDir)!, TemplatesDirectory = parse.GetValue(templates)!, Deployment = parse.GetValue(deployment)!,
                    DaemonUid = parse.GetValue(daemonUid), DaemonGid = parse.GetValue(daemonGid), SyncUid = parse.GetValue(syncUid), ExtractorUid = parse.GetValue(extractorUid),
                    DryRun = parse.GetValue(dryRun), SkipUsers = parse.GetValue(skipUsers)
                };
                return CliResult.Ok(options.DryRun ? "Provisioning dry-run prepared." : "Host provisioned.", services.Provisioning.Provision(options));
            }
            var loaded = Load(parse, parse.GetValue(initialRoot));
            RootSecurityValidator.ValidateOrThrow(loaded.DataRoot, loaded.Sources.IncomingRoot, parse.GetValue(daemonUid), parse.GetValue(daemonGid), parse.GetValue(syncUid));
            using var rootLock = DataRootLock.Acquire(loaded.DataRoot);
            using var ring = services.CreateKeyRing(loaded.DataRoot);
            return parse.GetValue(reset) ? await services.Initialization.ResetPasswordAsync(loaded, token) : await services.Initialization.InitializeAsync(loaded, token);
        });
        root.Subcommands.Add(init);

        var login = new Command("login", "Store a daemon credential in the OS credential store.");
        var url = new Argument<string>("url");
        var name = new Option<string>("--name") { DefaultValueFactory = _ => Environment.MachineName };
        var loginScopes = new Option<string>("--scopes") { DefaultValueFactory = _ => "read" };
        login.Arguments.Add(url); login.Options.Add(name); login.Options.Add(loginScopes);
        Bind(login, async (parse, token) =>
        {
            if (!Uri.TryCreate(parse.GetValue(url), UriKind.Absolute, out var origin) || origin.Scheme is not ("http" or "https") ||
                !string.IsNullOrEmpty(origin.UserInfo) || origin.AbsolutePath != "/" || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
                throw new CliUsageException("Login URL must be an HTTP(S) origin without credentials, a path, query or fragment.");
            var scopes = ParseScopes(parse.GetValue(loginScopes)!);
            var credentialName = parse.GetValue(name)!;
            if (string.IsNullOrWhiteSpace(credentialName)) throw new CliUsageException("--name cannot be empty.");
            var credential = await services.Login.AcquireAsync(origin, credentialName, scopes, token);
            await services.CredentialStore.StoreAsync(origin, credentialName, credential.ApiKey, token);
            return CliResult.Ok("Login credential stored.", new { origin = origin.GetLeftPart(UriPartial.Authority), name = credentialName, scopes, credentialId = credential.CredentialId });
        });
        root.Subcommands.Add(login);

        var doctor = new Command("doctor", "Validate local installation and daemon preconditions.");
        var socket = new Option<string>("--extractor-socket") { DefaultValueFactory = _ => Environment.GetEnvironmentVariable("SECONDBRAIN_EXTRACTOR_SOCKET") ?? "/run/secondbrain/extractor.sock" };
        doctor.Options.Add(socket);
        Bind(doctor, (parse, token) => new DoctorService().CheckAsync(Load(parse), parse.GetValue(daemonUid), parse.GetValue(daemonGid), parse.GetValue(syncUid),
            parse.GetValue(socket)!, services.DoctorExtension, token));
        root.Subcommands.Add(doctor);

        var keys = new Command("keys", "Manage API keys.");
        var create = new Command("create");
        var scopesOption = new Option<string>("--scopes") { Required = true };
        create.Options.Add(scopesOption);
        Bind(create, (parse, token) => services.Credentials.CreateAsync(ParseScopes(parse.GetValue(scopesOption)!), token));
        var keyList = new Command("list"); Bind(keyList, (_, token) => services.Credentials.ListAsync(token));
        var keyRevoke = new Command("revoke"); var keyId = new Argument<string>("id"); keyRevoke.Arguments.Add(keyId);
        Bind(keyRevoke, (parse, token) => services.Credentials.RevokeAsync(parse.GetValue(keyId)!, token));
        keys.Subcommands.Add(create); keys.Subcommands.Add(keyList); keys.Subcommands.Add(keyRevoke); root.Subcommands.Add(keys);

        var providers = new Command("providers", "Inspect configured provider bindings.");
        var providerList = new Command("list"); Bind(providerList, (_, token) => services.Providers.ListAsync(token));
        var providerTest = new Command("test"); var providerName = new Argument<string?>("name") { Arity = ArgumentArity.ZeroOrOne }; providerTest.Arguments.Add(providerName);
        Bind(providerTest, (parse, token) => services.Providers.TestAsync(parse.GetValue(providerName), token));
        providers.Subcommands.Add(providerList); providers.Subcommands.Add(providerTest); root.Subcommands.Add(providers);

        var sessions = new Command("sessions", "Manage browser and CLI sessions.");
        var sessionList = new Command("list"); Bind(sessionList, (_, token) => services.Sessions.ListAsync(token));
        var sessionRevoke = new Command("revoke"); var sessionId = new Argument<string>("id"); sessionRevoke.Arguments.Add(sessionId);
        Bind(sessionRevoke, (parse, token) => services.Sessions.RevokeAsync(parse.GetValue(sessionId)!, token));
        var revokeAll = new Command("revoke-all"); Bind(revokeAll, (_, token) => services.Sessions.RevokeAllAsync(token));
        sessions.Subcommands.Add(sessionList); sessions.Subcommands.Add(sessionRevoke); sessions.Subcommands.Add(revokeAll); root.Subcommands.Add(sessions);

        var maintenance = new Command("maintenance", "Maintain the local key ring.");
        var rotate = new Command("rotate-keys"); var revokeEpoch = new Option<bool>("--revoke-all"); rotate.Options.Add(revokeEpoch);
        Bind(rotate, async (parse, token) =>
        {
            if (parse.GetValue(revokeEpoch) && services.EpochRevoker is null) throw new CliPreconditionException("--revoke-all requires the lane A account epoch adapter before any key is rotated.");
            var loaded = Load(parse);
            RootSecurityValidator.ValidateOrThrow(loaded.DataRoot, loaded.Sources.IncomingRoot, parse.GetValue(daemonUid), parse.GetValue(daemonGid), parse.GetValue(syncUid));
            using var rootLock = DataRootLock.Acquire(loaded.DataRoot);
            using var ring = services.CreateKeyRing(loaded.DataRoot);
            var kid = await ring.RotateAsync(parse.GetValue(revokeEpoch), services.EpochRevoker, token);
            return CliResult.Ok("Key ring rotated.", new { kid, epochRevoked = parse.GetValue(revokeEpoch) });
        });
        maintenance.Subcommands.Add(rotate); root.Subcommands.Add(maintenance);

        var serve = new Command("serve", "Run the installed daemon.");
        Bind(serve, async (parse, token) =>
        {
            var loaded = Load(parse);
            RootSecurityValidator.ValidateOrThrow(loaded.DataRoot, loaded.Sources.IncomingRoot, parse.GetValue(daemonUid), parse.GetValue(daemonGid), parse.GetValue(syncUid));
            // Classify an existing instance before launching; the child retains its own lock for life.
            using (DataRootLock.Acquire(loaded.DataRoot)) { }
            var exit = await services.Daemon.ServeAsync(new DaemonLaunchOptions(parse.GetValue(config)!, loaded.DataRoot, parse.GetValue(secrets), parse.GetValue(syncUid), error), token);
            return new((CliExitCode)exit, exit == 0 ? "ok" : "daemon-stopped", exit == 0 ? "Daemon stopped." : "Daemon failed to start or stopped with an error.");
        });
        root.Subcommands.Add(serve);
        var parsed = root.Parse(args);
        if (parsed.Errors.Count > 0)
            return await CliOutput.WriteAsync(new(CliExitCode.Usage, "usage", string.Join(" ", parsed.Errors.Select(item => item.Message))), args.Contains("--json", StringComparer.Ordinal), output, error);
        return await parsed.InvokeAsync(new InvocationConfiguration { Output = output, Error = error, EnableDefaultExceptionHandler = false }, cancellationToken);
    }

    private static IReadOnlyList<string> ParseScopes(string value)
    {
        var scopes = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
        if (scopes.Length == 0 || scopes.Any(scope => scope is not ("read" or "write" or "infer" or "admin"))) throw new CliUsageException("Scopes must be a comma-separated list of read, write, infer, admin.");
        return scopes;
    }
    private static uint Identity(string variable, uint fallback) => uint.TryParse(Environment.GetEnvironmentVariable(variable), out var value) ? value : fallback;
}
