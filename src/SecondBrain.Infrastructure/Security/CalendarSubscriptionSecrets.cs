using Microsoft.AspNetCore.DataProtection;
using SecondBrain.Core.Security;
using SecondBrain.Core.Sources;

namespace SecondBrain.Infrastructure.Security;

public sealed class CalendarSubscriptionSecrets(IKeyRing ring) : ICalendarSubscriptionSecrets
{
    private const string Purpose = "secondbrain.calendar-subscription-url.v1";
    public string Protect(string url) => ring.DataProtectionProvider.CreateProtector(Purpose).Protect(url);
    public string Unprotect(string envelope) => ring.DataProtectionProvider.CreateProtector(Purpose).Unprotect(envelope);
}
