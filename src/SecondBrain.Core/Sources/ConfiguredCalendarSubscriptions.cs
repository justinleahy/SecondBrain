using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;

namespace SecondBrain.Core.Sources;

public sealed class ConfiguredCalendarSubscriptions(IOptionsMonitor<SecondBrainOptions> options, ISecretResolver resolver,
    ICalendarSubscriptionSecrets protection, ICalendarSubscriptionRepository repository)
{
    public async ValueTask InitializeAsync(CancellationToken token = default)
    {
        var pending = await PrepareAsync(options.CurrentValue.Sources, token);
        try { await repository.ReconcileConfiguredAsync(pending, token); }
        catch (Exception error) when (error is CalendarSubscriptionException or Ingestion.IngestionConflictException)
        { throw new ConfigurationException("Configured calendars conflict with the durable source registry."); }
    }

    public async ValueTask ValidateAsync(SecondBrainOptions candidate, CancellationToken token = default) =>
        _ = await PrepareAsync(candidate.Sources, token);

    private async ValueTask<IReadOnlyList<ConfiguredCalendarRegistration>> PrepareAsync(SourcesOptions settings, CancellationToken token)
    {
        var prior = (await repository.ListConfiguredAsync(token)).ToDictionary(item => item.Id, StringComparer.Ordinal);
        var pending = new List<ConfiguredCalendarRegistration>();
        try
        {
            // Validate and compare every link before the atomic registry reconciliation.
            foreach (var entry in settings.CalendarSubscriptions)
            {
                var link = CalendarLink.Parse(resolver.Resolve(entry.UrlReference));
                if (!settings.CalendarAllowedOrigins.Any(origin => CalendarLink.ValidateOrigin(origin) == link.Origin))
                    throw new ConfigurationException("A configured calendar origin is not allowed.");
                prior.TryGetValue(entry.Id, out var previous);
                if (previous is not null && CalendarLink.Parse(protection.Unprotect(previous.ProtectedUrl)).Uri.OriginalString != link.Uri.OriginalString)
                    throw new ConfigurationException("A configured calendar URL changed. Use a new subscription id for a different link.");
                pending.Add(new(entry.Id, entry.Name.Trim(), link.Origin, entry.PollIntervalSeconds, entry.Enabled,
                    previous?.ProtectedUrl ?? protection.Protect(link.Uri.OriginalString), previous?.ProtectedUrl));
            }
        }
        catch (Exception error) when (error is not OperationCanceledException && error is not ConfigurationException)
        { throw new ConfigurationException("A configured calendar link could not be resolved or verified."); }
        return pending;
    }
}
