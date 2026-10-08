namespace SecondBrain.Core.Privacy;

/// <summary>A redacted provider-data egress refusal suitable for problem+json.</summary>
public sealed class PrivacyPolicyException : Exception
{
    public PrivacyPolicyException(string problemType, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ProblemType = problemType;
    }

    /// <summary>The stable problem type URI for privacy-policy or egress-unverified.</summary>
    public string ProblemType { get; }
}
