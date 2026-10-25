using Microsoft.Extensions.Options;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Problems;
using SecondBrain.Core.Providers;
using SecondBrain.Infrastructure.Security;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Operations;

namespace SecondBrain.Server.Http;

public sealed record ProviderTestRequest(string? Name = null);
public sealed record DiagnosticCheck(string Name, bool Passed, string Message);

/// <summary>Administrative diagnostics use the same policy transport and readiness observations as the daemon.</summary>
public static class AdministrationEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        SettingsEndpoints.Map(endpoints);
        BackupEndpoints.Map(endpoints);
        foreach (var prefix in new[] { "", "/v1" })
        {
            endpoints.MapGet(prefix + "/providers", async (IProviderRegistry providers, HttpContext context) =>
            {
                var models = await providers.ListModelsAsync(context.RequestAborted);
                return Results.Ok(providers.ProviderNames.Select(name => new
                {
                    name,
                    endpoint = providers.GetProvider(name).Endpoint,
                    isLocal = providers.GetProvider(name).IsLocal,
                    models = models[name],
                    roles = Roles(providers).Where(binding => binding.Provider.ProviderName == name).Select(binding => new
                    {
                        role = binding.Role.ToString().ToLowerInvariant(), binding.Model.Model,
                        binding.Model.Capabilities, binding.Model.Limits,
                    }),
                }));
            }).WithMetadata(new EndpointPolicy("providers.list"));
            endpoints.MapPost(prefix + "/providers/test", async (ProviderTestRequest request, IProviderRegistry providers, HttpContext context) =>
            {
                if (request.Name is not null && !providers.ProviderNames.Contains(request.Name, StringComparer.Ordinal))
                    return Results.Problem(statusCode: 404, type: ProblemTypes.NotFound, title: "Provider was not found.");
                var results = await providers.TestAsync(context.RequestAborted);
                return Results.Ok(results.Where(result => request.Name is null || providers.GetRole(result.Key).Provider.ProviderName == request.Name)
                    .Select(result => new { role = result.Key.ToString().ToLowerInvariant(), provider = providers.GetRole(result.Key).Provider.ProviderName,
                        result.Value.IsHealthy, result.Value.CheckedAt, result.Value.Detail }).ToArray());
            }).WithMetadata(new EndpointPolicy("providers.test"));
            endpoints.MapGet(prefix + "/diagnostics", DiagnosticsAsync).WithMetadata(new EndpointPolicy("diagnostics.read"));
            endpoints.MapGet(prefix + "/usage", UsageAsync).WithMetadata(new EndpointPolicy("usage.read")).Produces<ModelUsageReport>();
            endpoints.MapPost(prefix + "/maintenance/retention/metadata", RetentionAsync)
                .WithMetadata(new EndpointPolicy("maintenance.retention", StepUp: true)).Produces<MetadataRetentionResult>();
            endpoints.MapPost(prefix + "/maintenance/retention", FullRetentionAsync)
                .WithMetadata(new EndpointPolicy("maintenance.retention", StepUp: true)).Produces<RetentionResult>();
        }
    }

    private static async Task<IResult> UsageAsync(HttpContext context, IAuthRepository auth, IModelUsage usage, TimeProvider clock,
        DateTimeOffset? from, DateTimeOffset? to, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        var end = to ?? clock.GetUtcNow().AddTicks(1);
        var start = from ?? new DateTimeOffset(end.UtcDateTime.Date, TimeSpan.Zero);
        try { return Results.Ok(await usage.ReadAsync(await SecondBrain.Server.Ingestion.KnowledgeEndpoints.AuthorityAsync(context, auth, token), start, end, token)); }
        catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "Choose a usage interval of up to 31 days."); }
    }

    private static async Task<IResult> RetentionAsync(HttpContext context, IAuthRepository auth, IMetadataRetention retention, CancellationToken token)
    {
        var actor = (AuthenticatedCredential)context.Items[typeof(AuthenticatedCredential)]!;
        var credential = await auth.FindAsync(actor.Id, token) ?? throw new AuthorityChangedException();
        return Results.Ok(await retention.RunAsync(new(actor.Id, actor.Generation, actor.AccountEpoch, credential.Name, actor.Kind), cancellationToken: token));
    }

    private static async Task<IResult> FullRetentionAsync(HttpContext context, IAuthRepository auth, IMetadataRetention metadata, IDocumentRetention documents, IBackupOperations backups, IOperationCleanup cleanup, CancellationToken token)
    {
        var actor = (AuthenticatedCredential)context.Items[typeof(AuthenticatedCredential)]!;
        var credential = await auth.FindAsync(actor.Id, token) ?? throw new AuthorityChangedException();
        var authority = new IngestionAuthority(actor.Id, actor.Generation, actor.AccountEpoch, credential.Name, actor.Kind);
        return Results.Ok(new RetentionResult(await metadata.RunAsync(authority, cancellationToken: token), await documents.RunAsync(authority, cancellationToken: token))
        { Backups = await backups.RotateAsync(authority, token), Operations = await cleanup.RunAsync(authority, token) });
    }

    private static async Task<IResult> DiagnosticsAsync(ReadinessService readiness, IProviderRegistry providers,
        IOptionsMonitor<SecondBrainOptions> options, AccessJwksCache access, IKeyRing ring, TimeProvider clock, HttpContext context)
    {
        var checks = (await readiness.CheckAsync(context.RequestAborted)).Select(result =>
            new DiagnosticCheck(result.Key, result.Value.Ready, result.Value.Detail ?? "Dependency is ready.")).ToList();
        foreach (var binding in Roles(providers))
            checks.Add(new("capabilities:" + binding.Role.ToString().ToLowerInvariant(), true,
                $"{binding.Provider.ProviderName}/{binding.Model.Model}; streaming={binding.Model.Capabilities.Streaming}, tools={binding.Model.Capabilities.Tools}, " +
                $"context_tokens={binding.Model.Limits.ContextTokens}, max_output_tokens={binding.Model.Limits.MaxOutputTokens}, " +
                $"embed_dimensions={binding.Model.Limits.EmbedDimensions}, embed_max_input_tokens={binding.Model.Limits.EmbedMaxInputTokens}, embed_batch_max={binding.Model.Limits.EmbedBatchMax}."));
        checks.Add(new("trusted-services", true, "Exact trusted origins are validated and DNS addresses pinned by the privacy policy."));
        if (options.CurrentValue.Server.CloudflareAccess is null)
            checks.Add(new("cloudflare-access.refresh", true, "Cloudflare Access is not configured on this private daemon."));
        else
        {
            await access.EnsureKeysAsync(null, context.RequestAborted);
            var success = access.LastSuccessfulRefresh;
            var refreshed = access.LastRefreshSucceeded == true && success is not null && clock.GetUtcNow() - success < TimeSpan.FromDays(1);
            checks.Add(new("cloudflare-access.refresh", refreshed,
                refreshed ? $"Last successful signing-key refresh: {success:O}." : "Access signing-key refresh failed or has not succeeded recently."));
            checks.Add(new("cloudflare-access.cached-keys", access.CurrentKeys.Count != 0,
                "Cached signing keys remain available for assertion validation after a refresh failure."));
        }
        checks.Add(new("keyring", Ulid.TryParse(ring.ActiveKid, out _), "Active HMAC key and Data Protection provider are loaded."));
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { checks });
    }

    private static IEnumerable<ProviderRoleBinding> Roles(IProviderRegistry providers)
    {
        foreach (var role in new[] { ModelRole.Chat, ModelRole.Enrich, ModelRole.Embed, ModelRole.Rerank })
        {
            ProviderRoleBinding? binding;
            try { binding = providers.GetRole(role); }
            catch (InvalidOperationException) { continue; }
            yield return binding;
        }
    }
}
