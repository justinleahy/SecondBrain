using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Problems;
using SecondBrain.Core.Security;
using SecondBrain.Infrastructure.Security;

namespace SecondBrain.Server.Auth;

/// <summary>One credential listing: its logical operation (shared by the bare and <c>/v1</c> routes) and the rows it selects.</summary>
public sealed class CredentialListing
{
    public static readonly CredentialListing Sessions = new("sessions.list", "session", activeOnly: true);
    public static readonly CredentialListing Keys = new("keys.list", "api_key", activeOnly: false);

    private CredentialListing(string operation, string kind, bool activeOnly)
    {
        Operation = operation;
        Kind = kind;
        ActiveOnly = activeOnly;
        // The normalized query is every input that selects rows; the page size is not part of it.
        QueryHash = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"kind={kind}&active={(activeOnly ? "true" : "false")}")));
    }

    public string Operation { get; }
    public string Kind { get; }
    public bool ActiveOnly { get; }
    public string QueryHash { get; }
}

/// <summary>
/// Keyset pages of credential summaries: a bare JSON array plus an RFC 8288 <c>Link: rel="next"</c>.
/// The cursor is a SEC-32 Data Protection envelope bound to this instance, listing, caller and moment; it never conveys authority.
/// </summary>
public static class CredentialPaging
{
    public const int DefaultPageSize = 100;
    public const int MaxPageSize = 500;
    /// <summary>The subpurpose under <see cref="KeyRingPurposes.Cursor"/>; no other token is protected with it.</summary>
    public const string CursorPurpose = "credentials.list.v1";
    public static readonly TimeSpan CursorLifetime = TimeSpan.FromMinutes(15);
    private const int CursorVersion = 1;
    private const string Sort = "created_at:asc,id:asc";
    // Credential rows are not derived from processed content, so the processing generations they depend on are the empty set.
    private const string ProcessingGeneration = "{}";
    // Issued cursors are about 650 characters around a payload under 450 bytes; the token length is bounded before decryption and the decrypted payload before parsing.
    private const int MaxCursorLength = 2048;
    private const int MaxPayloadBytes = 1024;

    public static async Task<Results<Ok<CredentialSummary[]>, ProblemHttpResult>> ListAsync(HttpContext context, IAuthRepository repository,
        IKeyRing keyRing, TimeProvider clock, CredentialListing listing, string? after, int? limit)
    {
        var size = limit ?? DefaultPageSize;
        if (size is < 1 or > MaxPageSize)
            return TypedResults.Problem(statusCode: 400, type: ProblemTypes.InvalidRequest, title: $"The page limit must be between 1 and {MaxPageSize}.");
        var identity = AuthEndpoints.Identity(context);
        var protector = keyRing.DataProtectionProvider.CreateProtector(KeyRingPurposes.Cursor).CreateProtector(CursorPurpose);
        var now = clock.GetUtcNow();
        string? instance = null;
        CredentialCursor? position = null;
        if (after is not null)
        {
            // Every rejection is the same typed 400, so a caller learns nothing about which binding failed.
            if (!TryOpen(protector, after, out var ticket) || !Applies(ticket, listing, identity, now) ||
                ticket.Instance != (instance = await repository.GetInstanceIdAsync(context.RequestAborted)))
                return TypedResults.Problem(statusCode: 400, type: ProblemTypes.InvalidRequest, title: "The page cursor is invalid or has expired.");
            position = new(ticket.CreatedAt, ticket.Id);
        }
        var page = await repository.ListPageAsync(listing.Kind, listing.ActiveOnly, position, size, context.RequestAborted);
        if (page.Next is not null)
        {
            instance ??= await repository.GetInstanceIdAsync(context.RequestAborted);
            var ticket = new CursorTicket(CursorVersion, instance, listing.Operation, listing.QueryHash, Sort, ProcessingGeneration,
                page.Next.CreatedAt, page.Next.Id, identity.Id, identity.Generation, identity.AccountEpoch,
                now.ToUnixTimeMilliseconds(), (now + CursorLifetime).ToUnixTimeMilliseconds());
            var cursor = WebEncoders.Base64UrlEncode(protector.Protect(JsonSerializer.SerializeToUtf8Bytes(ticket)));
            context.Response.Headers.Link = $"<{context.Request.PathBase}{context.Request.Path}?after={cursor}&limit={size.ToString(CultureInfo.InvariantCulture)}>; rel=\"next\"";
        }
        return TypedResults.Ok(page.Items.Select(CredentialSummary.From).ToArray());
    }

    private static bool TryOpen(IDataProtector protector, string value, out CursorTicket ticket)
    {
        ticket = null!;
        if (value.Length is 0 or > MaxCursorLength) return false;
        byte[] payload;
        try { payload = protector.Unprotect(WebEncoders.Base64UrlDecode(value)); }
        catch (Exception exception) when (exception is FormatException or CryptographicException) { return false; }
        if (payload.Length > MaxPayloadBytes) return false;
        try { ticket = JsonSerializer.Deserialize<CursorTicket>(payload)!; }
        catch (JsonException) { return false; }
        return ticket is { Instance: not null, Operation: not null, Query: not null, Sort: not null, Processing: not null, CreatedAt: not null, Id: not null, Credential: not null };
    }

    /// <summary>The cursor must come from this listing and this credential at its current generation and epoch, and be under 15 minutes old.</summary>
    private static bool Applies(CursorTicket ticket, CredentialListing listing, AuthenticatedCredential identity, DateTimeOffset now)
    {
        var at = now.ToUnixTimeMilliseconds();
        return ticket.Version == CursorVersion && ticket.Operation == listing.Operation && ticket.Query == listing.QueryHash &&
            ticket.Sort == Sort && ticket.Processing == ProcessingGeneration &&
            ticket.Credential == identity.Id && ticket.Generation == identity.Generation && ticket.Epoch == identity.AccountEpoch &&
            ticket.ExpiresAt - ticket.IssuedAt == (long)CursorLifetime.TotalMilliseconds && at >= ticket.IssuedAt && at < ticket.ExpiresAt;
    }

    private sealed record CursorTicket(int Version, string Instance, string Operation, string Query, string Sort, string Processing,
        string CreatedAt, string Id, string Credential, long Generation, long Epoch, long IssuedAt, long ExpiresAt);
}
