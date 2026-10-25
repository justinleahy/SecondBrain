using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Ingestion;

namespace SecondBrain.Core.Sources;

public sealed class CalendarSubscriptionService(IOptionsMonitor<SecondBrainOptions> options, ICalendarSubscriptionSecrets secrets, ICalendarSubscriptionRepository repository)
{
    public ValueTask<CalendarSubscription> CreateAsync(string name, string url, int interval, bool enabled, IngestionAuthority actor, CancellationToken token = default)
    {
        if (name is null) throw new CalendarSubscriptionException("calendar_settings_invalid");
        Validate(name, interval);
        var link = CalendarLink.Parse(url);
        if (!options.CurrentValue.Sources.CalendarAllowedOrigins.Any(value => CalendarLink.ValidateOrigin(value) == link.Origin))
            throw new CalendarSubscriptionException("calendar_origin_not_allowed");
        return repository.CreateAsync(name.Trim(), link.Origin, secrets.Protect(link.Uri.OriginalString), interval, enabled, actor, token);
    }
    public static void Validate(string? name, int? interval)
    {
        if (name is not null && (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Any(char.IsControl)) || interval is < 300)
            throw new CalendarSubscriptionException("calendar_settings_invalid");
    }
}
