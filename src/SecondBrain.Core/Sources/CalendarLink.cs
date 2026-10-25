using System.Net;
using System.Net.Sockets;

namespace SecondBrain.Core.Sources;

/// <summary>A bearer link. Its string representation deliberately contains only its origin.</summary>
public sealed class CalendarLink
{
    private CalendarLink(Uri uri) { Uri = uri; Origin = OriginOf(uri); }
    [System.Text.Json.Serialization.JsonIgnore] public Uri Uri { get; }
    public string Origin { get; }
    public override string ToString() => Origin;

    public static CalendarLink Parse(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 8192 || value.Any(char.IsControl) || value != value.Trim() || value.Contains('\\'))
            throw new CalendarSubscriptionException("calendar_url_invalid");
        var translated = value.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase) ? "https://" + value[9..] : value;
        if (!System.Uri.TryCreate(translated, UriKind.Absolute, out var uri) || uri.Scheme != System.Uri.UriSchemeHttps ||
            uri.Host.Length == 0 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.IdnHost.EndsWith('.') ||
            System.Uri.CheckHostName(uri.IdnHost) == UriHostNameType.Unknown)
            throw new CalendarSubscriptionException("calendar_url_invalid");
        return new(uri);
    }

    public static string ValidateOrigin(string value)
    {
        var link = Parse(value);
        if (link.Uri.AbsolutePath != "/" || link.Uri.Query.Length != 0 || value.EndsWith('/') || value.StartsWith("webcal:", StringComparison.OrdinalIgnoreCase))
            throw new CalendarSubscriptionException("calendar_origin_invalid");
        return link.Origin;
    }
    private static string OriginOf(Uri uri) => new UriBuilder(System.Uri.UriSchemeHttps, uri.IdnHost, uri.IsDefaultPort ? -1 : uri.Port).Uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();

    /// <summary>Conservative public-unicast policy, including mapped IPv4 and special-use IPv6 ranges.</summary>
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224 || b[0] == 100 && b[1] is >= 64 and <= 127 ||
                b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] is >= 16 and <= 31 ||
                b[0] == 192 && (b[1] == 168 || b[1] == 0 && b[2] is 0 or 2 || b[1] == 88 && b[2] == 99) ||
                b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100) || b[0] == 203 && b[1] == 0 && b[2] == 113);
        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId == 0 && (b[0] & 0xe0) == 0x20 &&
            !(b[0] == 0x20 && b[1] == 0x01 && (b[2] < 2 || b[2] == 0x0d && b[3] == 0xb8)) &&
            !(b[0] == 0x20 && b[1] == 0x02) && !(b[0] == 0x3f && b[1] == 0xff && (b[2] & 0xf0) == 0);
    }
}

public sealed class CalendarSubscriptionException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
