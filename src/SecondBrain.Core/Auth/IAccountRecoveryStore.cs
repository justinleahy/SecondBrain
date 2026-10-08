namespace SecondBrain.Core.Auth;

/// <summary>Local bootstrap and recovery persistence used by brain while it holds the data-root lock.</summary>
public interface IAccountRecoveryStore
{
    /// <summary>Returns the recorded initial admin credential id, else the oldest admin API key id, else <see langword="null"/>.</summary>
    Task<string?> FindInitialAdminCredentialIdAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the password and the recorded password policy and advances the account epoch in one transaction,
    /// returning the new epoch. Throws <see cref="AccountNotInitializedException"/> when no account exists.
    /// </summary>
    Task<long> ResetPasswordAsync(AccountRecord account, string passwordParametersJson, CancellationToken cancellationToken = default);
}

/// <summary>Thrown inside the recovery transaction when there is no account to update, so the transaction rolls back.</summary>
public sealed class AccountNotInitializedException() : InvalidOperationException("The account is not initialized.");
