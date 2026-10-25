using System.Globalization;
using System.Text.Json;

namespace SecondBrain.Extraction.Contracts;

public sealed record CalendarWindow(string From, string To, string TimeZone, int MaxOccurrences = 100000)
{
    public (DateTimeOffset From, DateTimeOffset To, TimeZoneInfo Zone) Validate()
    {
        if (MaxOccurrences is < 1 or > 100000 || From is null || To is null || TimeZone is null || From.Length > 64 || To.Length > 64 || TimeZone.Length is < 1 or > 128 ||
            !DateTimeOffset.TryParse(From, CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) ||
            !DateTimeOffset.TryParse(To, CultureInfo.InvariantCulture, DateTimeStyles.None, out var to) ||
            to <= from || to - from > TimeSpan.FromDays(366 * 20)) throw new ArgumentException("Invalid calendar coverage window.");
        return (from.ToUniversalTime(), to.ToUniversalTime(), TimeZoneInfo.FindSystemTimeZoneById(TimeZone));
    }
}

/// <summary>One complete container result; members are reconciled only after the entire result is validated.</summary>
public sealed record CalendarSnapshot(CalendarWindow Window, IReadOnlyList<CalendarMember> Members, IReadOnlyList<CalendarInstance> Instances)
{
    public void Validate()
    {
        if (Window is null) throw new InvalidDataException("calendar_window");
        var (from, to, _) = Window.Validate();
        if (Members is null || Instances is null || Members.Count > 10000 || Instances.Count > Window.MaxOccurrences) throw new InvalidDataException("calendar_bounds");
        var keys = new HashSet<(string, string)>();
        long bytes = 0;
        foreach (var member in Members)
        {
            if (member is null || string.IsNullOrWhiteSpace(member.Uid) || member.Uid.Length > 2048 || member.Uid.Any(char.IsControl) ||
                member.RecurrenceId is null || member.RecurrenceId.Length > 128 || member.RecurrenceId.Any(char.IsControl) || member.Title is null || member.Title.Length > 4096 || member.Text is null || member.Properties is null ||
                !keys.Add((member.Uid, member.RecurrenceId))) throw new InvalidDataException("calendar_member");
            var metadataLength = JsonSerializer.SerializeToUtf8Bytes(member.Properties).Length;
            bytes += System.Text.Encoding.UTF8.GetByteCount(member.Text) + metadataLength;
            if (bytes > 20 * 1024 * 1024 || metadataLength > 131072) throw new InvalidDataException("calendar_bounds");
            if (!member.Properties.TryGetValue("uid", out var uid) || uid.ValueKind != JsonValueKind.String || uid.GetString() != member.Uid ||
                !member.Properties.TryGetValue("starts_at", out var startsAt) || startsAt.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParse(startsAt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
                member.Properties.TryGetValue("recurrence_id", out var recurrence) && (recurrence.ValueKind != JsonValueKind.String || recurrence.GetString() != member.RecurrenceId))
                throw new InvalidDataException("calendar_member_properties");
            if (member.People?.Count > 1001 || member.People?.Any(person => person is null || person.Role is not ("attendee" or "organizer") ||
                string.IsNullOrWhiteSpace(person.Identifier) || person.Identifier.Length > 2048 || person.Identifier.Any(char.IsControl) ||
                person.DisplayName is null || person.DisplayName.Length > 2048 || person.DisplayName.Any(char.IsControl)) == true)
                throw new InvalidDataException("calendar_people");
        }
        var instances = new HashSet<(string, string, DateTimeOffset)>();
        foreach (var instance in Instances)
        {
            if (instance is null || !keys.Contains((instance.Uid, instance.RecurrenceId)) || instance.Status is not ("confirmed" or "tentative" or "cancelled") ||
                !DateTimeOffset.TryParse(instance.StartsAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
                !DateTimeOffset.TryParse(instance.EndsAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) || end < start ||
                start >= to || end <= from || !instances.Add((instance.Uid, instance.RecurrenceId, start))) throw new InvalidDataException("calendar_instance");
        }
    }
}
public sealed record CalendarMember(string Uid, string RecurrenceId, string Title, string Text, IReadOnlyDictionary<string, JsonElement> Properties,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CalendarPerson>? People = null);
public sealed record CalendarPerson(string Role, string Identifier, string DisplayName);
public sealed record CalendarInstance(string Uid, string RecurrenceId, string StartsAt, string EndsAt, string Status);
