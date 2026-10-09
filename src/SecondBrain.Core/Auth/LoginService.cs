using System.Text;

namespace SecondBrain.Core.Auth;

public sealed record LoginDecision(bool Accepted, int RetryAfterSeconds = 0, long? AccountEpoch = null);

/// <summary>One bounded Argon2 verification at a time, with durable per-source cooldown and fixed lock windows.</summary>
public sealed class LoginService(IAuthRepository repository, IPasswordHasher hasher, CredentialFactory factory, TimeProvider clock) : IDisposable
{
    /// <summary>Upper bound on the replayed tail; throttling keeps a real post-success tail far below it.</summary>
    public const int AttemptHistoryLimit = 1000;
    private readonly SemaphoreSlim passwordSlot = new(1, 1);
    public async Task<LoginDecision> VerifyAsync(string password, string source, CancellationToken cancellationToken = default)
    {
        if (Encoding.UTF8.GetByteCount(password) > 1024) return new(false);
        if (!await passwordSlot.WaitAsync(0, cancellationToken)) return new(false, 1);
        try
        {
            var now = clock.GetUtcNow();
            // A success resets every counter, so the tail from the latest success replays the whole window.
            var attempts = await repository.LoginAttemptsAsync(source, now.AddHours(-1), AttemptHistoryLimit, cancellationToken);
            var failures = 0;
            DateTimeOffset? lastFailure = null;
            DateTimeOffset? lockedUntil = null;
            foreach (var attempt in attempts)
            {
                var at = AuthTime.Parse(attempt.At);
                if (lockedUntil is not null && at >= lockedUntil) { failures = 0; lastFailure = null; lockedUntil = null; }
                if (attempt.Success) { failures = 0; lastFailure = null; lockedUntil = null; continue; }
                failures++; lastFailure = at;
                if (failures == 5) lockedUntil = at.AddMinutes(15);
            }
            // A full tail may have lost its start and cannot be replayed; fail closed with a fixed lock from the newest failure.
            if (attempts.Count >= AttemptHistoryLimit && attempts.LastOrDefault(attempt => !attempt.Success) is { } newest)
                lockedUntil = AuthTime.Parse(newest.At).AddMinutes(15);
            // Rejected probes never add attempts, so a single source cannot slide its own lock window.
            if (lockedUntil > now) return new(false, (int)Math.Ceiling((lockedUntil.Value - now).TotalSeconds));
            if (lockedUntil is null && lastFailure is not null)
            {
                var allowedAt = lastFailure.Value.AddSeconds(Math.Min(60, 1 << Math.Min(failures - 1, 6)));
                if (allowedAt > now) return new(false, (int)Math.Ceiling((allowedAt - now).TotalSeconds));
            }
            var verifiedEpoch = await repository.GetEpochAsync(cancellationToken);
            var account = await repository.GetAccountAsync(cancellationToken);
            var success = account is not null && hasher.Verify(account.PasswordHash, password);
            await repository.RecordLoginAsync(source, now, success, cancellationToken);
            if (success && hasher.NeedsRehash(account!))
                success = await repository.RehashAsync(account!, factory.CreateAccount(password), verifiedEpoch, cancellationToken);
            if (success && await repository.GetEpochAsync(cancellationToken) != verifiedEpoch) success = false;
            return new(success, AccountEpoch: success ? verifiedEpoch : null);
        }
        finally { passwordSlot.Release(); }
    }
    public void Dispose() => passwordSlot.Dispose();
}
