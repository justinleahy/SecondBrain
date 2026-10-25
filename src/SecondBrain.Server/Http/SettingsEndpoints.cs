using System.Text.Json;
using System.Text.Json.Serialization;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Privacy;
using SecondBrain.Core.Providers;
using SecondBrain.Infrastructure.Configuration;
using SecondBrain.Server.Auth;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SecondBrain.Server.Http;

public sealed record ProviderSetting(string Name, string Kind, string? Endpoint, bool Trusted, bool HasCredential, bool IsLocal);
public sealed record RoleSetting(string Role, string Provider, string Model, ModelCapabilities Capabilities, ModelLimits Limits);
public sealed record EditablePrivacy(bool LocalOnly, List<string> TrustedServices);
public sealed record ProviderChange(string Kind, string? Endpoint, bool Trusted, string? CredentialReference = null, bool ClearCredential = false);
public sealed record SettingsChange(string Version, string Section, JsonElement Value, string? ProviderName = null);
public sealed record BackupRetentionSetting(int Keep);
public sealed record SettingsSnapshot(string Version, bool CanEdit, IReadOnlyList<ProviderSetting> Providers, IReadOnlyList<RoleSetting> Roles, ModelRolesOptions Models,
    EditablePrivacy Privacy, LimitsOptions Limits, TimeOptions Time, AssistantOptions Assistant, BudgetOptions Budgets,
    IngestOptions Ingest, ChunkingOptions Chunking, RetentionOptions Retention, PrivacyReadiness Egress)
{
    public BackupRetentionSetting Backup { get; init; } = new(14);
    public ClassificationOptions Classification { get; init; } = new();
    public EnrichmentOptions Enrichment { get; init; } = new();
}

public static class SettingsEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly ISerializer Yaml = new SerializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull).Build();

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        foreach (var prefix in new[] { "", "/v1" })
        {
            endpoints.MapGet(prefix + "/configuration", ReadAsync).WithMetadata(new EndpointPolicy("settings.read")).Produces<SettingsSnapshot>();
            endpoints.MapPut(prefix + "/configuration", WriteAsync).WithMetadata(new EndpointPolicy("settings.write", SessionOnly: true, StepUp: true))
                .Produces<SettingsSnapshot>().ProducesProblem(400).ProducesProblem(409).ProducesProblem(412);
        }
    }
    private static IResult ReadAsync(ReloadingConfiguration configuration, IPrivacyReadiness privacy, IProviderRegistry providers, TimeProvider clock, HttpContext context) => Guard(context, () =>
        Results.Ok(Snapshot(configuration.ReadForEdit(), privacy, providers,
            context.Items[typeof(AuthenticatedCredential)] is AuthenticatedCredential { Kind: "session" } identity && RequestAuthorizationMiddleware.HasFreshStepUp(identity, configuration.CurrentValue, clock.GetUtcNow()))));

    private static IResult WriteAsync(SettingsChange request, ReloadingConfiguration configuration, IPrivacyReadiness privacy, IProviderRegistry providers,
        IAuthRepository auth, TimeProvider clock, HttpContext context) => Guard(context, () =>
    {
        if (request.Version?.Length != 64) throw new ArgumentException("A settings revision is required.");
        var captured = (AuthenticatedCredential)context.Items[typeof(AuthenticatedCredential)]!;
        bool Authorized()
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            var current = auth.FindAsync(captured.Id, context.RequestAborted).GetAwaiter().GetResult();
            return current is not null && auth.IsCurrentAsync(captured.Id, captured.Generation, captured.AccountEpoch, context.RequestAborted).GetAwaiter().GetResult()
                && current.Kind == "session" && current.GrantedScopes.Contains(Scope.Admin)
                && current.SteppedUpAt is not null && DateTimeOffset.TryParse(current.SteppedUpAt, out var stepped)
                && stepped <= clock.GetUtcNow() && clock.GetUtcNow() - stepped <= TimeSpan.FromMinutes(configuration.CurrentValue.Auth.StepUpMinutes);
        }
        var result = configuration.UpdateForEdit(request.Version, original => Rewrite(original, request), Authorized);
        return Results.Ok(Snapshot(result, privacy, providers, true));
    });

    private static SettingsSnapshot Snapshot(ConfigurationEditSnapshot snapshot, IPrivacyReadiness privacy, IProviderRegistry providers, bool canEdit)
    {
        var value = snapshot.Options;
        var roles = new List<RoleSetting>();
        foreach (var role in Enum.GetValues<ModelRole>())
        {
            try { var binding = providers.GetRole(role); roles.Add(new(role.ToString().ToLowerInvariant(), binding.Provider.ProviderName, binding.Model.Model, binding.Model.Capabilities, binding.Model.Limits)); }
            catch (InvalidOperationException) { }
        }
        return new(snapshot.Version, canEdit, value.Providers.Select(pair => new ProviderSetting(pair.Key, pair.Value.Kind, pair.Value.Endpoint,
            pair.Value.Trusted, pair.Value.ApiKeyReference is not null, ConfigurationValidator.IsLocal(pair.Value, value.Privacy))).ToArray(), roles, value.Models,
            new(value.Privacy.LocalOnly, value.Privacy.TrustedServices), value.Limits, value.Time, value.Assistant, value.Budgets, value.Ingest, value.Chunking, value.Retention, privacy.GetReadiness())
        { Backup = new(value.Backup.Keep), Classification = value.Classification, Enrichment = value.Enrichment };
    }

    public static string Rewrite(string original, SettingsChange change)
    {
        var stream = new YamlStream(); stream.Load(new StringReader(original));
        var root = (YamlMappingNode)stream.Documents.Single().RootNode;
        switch (change.Section)
        {
            case "privacy":
                var privacy = Value<EditablePrivacy>(change.Value);
                if (privacy.TrustedServices is null || privacy.TrustedServices.Count > 100) throw new ArgumentException("Invalid trusted services.");
                Merge(root, "privacy", Node(privacy)); break;
            case "limits":
                var limits = Value<LimitsOptions>(change.Value); ValidateLimits(limits); Set(root, "limits", Node(limits)); break;
            case "time":
                var time = Value<TimeOptions>(change.Value);
                try { _ = TimeZoneInfo.FindSystemTimeZoneById(time.Zone); }
                catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new ArgumentException("Unknown time zone."); }
                if (!Enum.TryParse<DayOfWeek>(time.WeekStart, true, out var day) || !Enum.IsDefined(day)) throw new ArgumentException("Unknown first day of the week.");
                Set(root, "time", Node(time)); break;
            case "models": Set(root, "models", Node(Value<ModelRolesOptions>(change.Value))); break;
            case "assistant": Set(root, "assistant", Node(Value<AssistantOptions>(change.Value))); break;
            case "budgets":
                var budget = Value<BudgetOptions>(change.Value);
                if (budget.DailyTokens < 0) throw new ArgumentException("Daily token budget cannot be negative.");
                Set(root, "budgets", Node(budget)); break;
            case "classification":
                var classification = Value<ClassificationOptions>(change.Value); classification.Validate();
                Set(root, "classification", Node(classification)); break;
            case "enrichment":
                var enrichment = Value<EnrichmentOptions>(change.Value); enrichment.Validate(); Set(root, "enrichment", Node(enrichment)); break;
            case "ingest": Merge(root, "ingest", Node(Value<IngestOptions>(change.Value))); break;
            case "chunking": Set(root, "chunking", Node(Value<ChunkingOptions>(change.Value))); break;
            case "retention":
                var retention = Value<RetentionOptions>(change.Value); retention.Validate(); Set(root, "retention", Node(retention)); break;
            case "backup":
                var backup = Value<BackupRetentionSetting>(change.Value);
                if (backup.Keep is < 1 or > 1000) throw new ArgumentException("Backup retention must be between 1 and 1000 archives.");
                Merge(root, "backup", Node(backup)); break;
            case "provider":
                var provider = Value<ProviderChange>(change.Value);
                var entries = Mapping(root, "providers");
                var name = ProviderName(change.ProviderName);
                var entry = Mapping(entries, name);
                entry.Children.Remove(new YamlScalarNode("adapter")); entry.Children.Remove(new YamlScalarNode("base_url"));
                Set(entry, "kind", new YamlScalarNode(provider.Kind)); Set(entry, "trusted", new YamlScalarNode(provider.Trusted ? "true" : "false"));
                if (provider.Endpoint is null) entry.Children.Remove(new YamlScalarNode("endpoint")); else Set(entry, "endpoint", new YamlScalarNode(provider.Endpoint));
                if (provider.ClearCredential) entry.Children.Remove(new YamlScalarNode("api_key"));
                else if (provider.CredentialReference is not null) Set(entry, "api_key", new YamlScalarNode(provider.CredentialReference));
                break;
            case "remove_provider": Mapping(root, "providers").Children.Remove(new YamlScalarNode(ProviderName(change.ProviderName))); break;
            default: throw new ArgumentException("This settings section cannot be edited.");
        }
        using var writer = new StringWriter(); stream.Save(writer, false); return writer.ToString();
    }
    private static string ProviderName(string? value) => value is not null && value.Length is > 0 and <= 100 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
        ? value : throw new ArgumentException("Provider names must contain letters, numbers, dots, underscores or hyphens.");
    private static T Value<T>(JsonElement value) => value.Deserialize<T>(Json) ?? throw new ArgumentException("A settings value is required.");
    private static YamlNode Node(object value)
    { var stream = new YamlStream(); stream.Load(new StringReader(Yaml.Serialize(value))); return stream.Documents[0].RootNode; }
    private static YamlMappingNode Mapping(YamlMappingNode parent, string key)
    { if (parent.Children.TryGetValue(new YamlScalarNode(key), out var value)) return value as YamlMappingNode ?? throw new ArgumentException("Invalid settings mapping."); var map = new YamlMappingNode(); Set(parent, key, map); return map; }
    private static void Set(YamlMappingNode parent, string key, YamlNode value) => parent.Children[new YamlScalarNode(key)] = value;
    private static void Merge(YamlMappingNode parent, string key, YamlNode value)
    { var target = Mapping(parent, key); foreach (var pair in ((YamlMappingNode)value).Children) target.Children[pair.Key] = pair.Value; }
    private static void ValidateLimits(LimitsOptions value)
    {
        if (value.PerCredential is not { } credential || value.Global is not { } global || credential.RequestsPerMinute < 1 ||
            credential.ConcurrentUploads < 1 || credential.ConcurrentSearches < 1 || credential.ConcurrentTurns < 1 || credential.SseStreams < 1 || credential.Circuits < 1 ||
            credential.ModelTokensPerHour < 0 || global.FilesPerSource < 1 || global.Files < 1 || global.AdmittedGb <= 0 || global.QueuedJobs < 1 ||
            global.CalendarOccurrences < 1 || global.DiskLowWaterGb < 0) throw new ArgumentException("Limits must be positive; token budgets and the disk low-water threshold may be zero.");
    }
    private static IResult Guard(HttpContext context, Func<IResult> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        try { return action(); }
        catch (ConfigurationEditConflictException) { return Results.Problem(statusCode: 412, title: "Settings changed. Reload the latest values before saving again."); }
        catch (ConfigurationEditAuthorityException) { return Results.Problem(statusCode: 403, title: "Confirm your password again before changing settings."); }
        catch (Exception exception) when (exception is ConfigurationException or ArgumentException or JsonException or YamlDotNet.Core.YamlException)
        { return Results.Problem(statusCode: 400, title: exception is ConfigurationException or ArgumentException ? exception.Message : "Invalid settings value."); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return Results.Problem(statusCode: 503, title: "The configuration file could not be saved. Check its write permissions and reload settings before retrying."); }
    }
}
