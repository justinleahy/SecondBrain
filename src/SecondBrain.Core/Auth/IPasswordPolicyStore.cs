namespace SecondBrain.Core.Auth;

/// <summary>Reads the deployment host's password policy recorded atomically by brain init.</summary>
public interface IPasswordPolicyStore
{
    /// <summary>Returns the stored <c>password_parameters</c> JSON, or <see langword="null"/> when none is recorded.</summary>
    Task<string?> ReadPasswordParametersJsonAsync(CancellationToken cancellationToken = default);
}
