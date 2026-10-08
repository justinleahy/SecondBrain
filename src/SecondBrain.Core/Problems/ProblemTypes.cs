namespace SecondBrain.Core.Problems;

/// <summary>Stable RFC 9457 problem type URIs; see spec §7.3 and M0 build plan §5.</summary>
public static class ProblemTypes
{
    private const string Prefix = "https://secondbrain.dev/problems/";

    /// <summary>A document revision precondition failed (spec §7.3).</summary>
    public const string RevisionMismatch = Prefix + "revision-mismatch";

    /// <summary>An idempotency key was reused with a different payload (spec §7.1, ING-2).</summary>
    public const string IdempotencyKeyReuse = Prefix + "idempotency-key-reuse";

    /// <summary>A per-credential reservation or rate limit was exceeded (spec §15.8).</summary>
    public const string LimitExceeded = Prefix + "limit-exceeded";

    /// <summary>A global capacity reservation could not be admitted (spec §15.8).</summary>
    public const string Capacity = Prefix + "capacity";

    /// <summary>A provider call violated the current privacy policy (spec §15.6, SEC-16).</summary>
    public const string PrivacyPolicy = Prefix + "privacy-policy";

    /// <summary>The host egress backstop is unverified under local_only (spec §15.6, SEC-17).</summary>
    public const string EgressUnverified = Prefix + "egress-unverified";

    /// <summary>A credential lacks a required scope (spec §15.4).</summary>
    public const string ScopeDenied = Prefix + "scope-denied";

    /// <summary>A required browser origin was absent or not allowlisted (spec §15.2, SEC-4).</summary>
    public const string OriginRejected = Prefix + "origin-rejected";

    /// <summary>The request host was not allowlisted (spec §15.2, SEC-4).</summary>
    public const string HostRejected = Prefix + "host-rejected";

    /// <summary>A public-origin request lacks a valid Access assertion (spec §15.2, SEC-3).</summary>
    public const string AccessRequired = Prefix + "access-required";
}
