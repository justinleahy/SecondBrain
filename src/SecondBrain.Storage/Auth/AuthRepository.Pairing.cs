using Dapper;
using SecondBrain.Core.Auth;

namespace SecondBrain.Storage.Auth;

public sealed partial class AuthRepository
{
    public async Task CreatePairingAsync(CliPairingRequest request, CancellationToken token = default) =>
        _ = await store.QueueWriteAsync(async (connection, transaction, cancellation) =>
        {
            var now = AuthTime.Format(clock.GetUtcNow());
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM cli_pairings WHERE expires_at<=@now", new { now }, transaction, cancellationToken: cancellation));
            var counts = await connection.QuerySingleAsync<PairingCounts>(new CommandDefinition(
                "SELECT COUNT(*) Total,COALESCE(SUM(CASE WHEN source=@source THEN 1 ELSE 0 END),0) Local FROM cli_pairings",
                new { source = request.Source }, transaction, cancellationToken: cancellation));
            if (counts.Total >= 128 || counts.Local >= 8) throw new CliPairingException("Too many pairing requests; try again after existing requests expire.");
            return await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO cli_pairings(id,code,name,poll_verifier,source,secure,created_at,expires_at,status)
                VALUES(@Id,@Code,@Name,@PollVerifier,@Source,@Secure,@CreatedAt,@ExpiresAt,'pending')
                """, request, transaction, cancellationToken: cancellation));
        }, token);

    public async Task<CliPairingView?> FindPairingAsync(string code, CancellationToken token = default)
    {
        await using var lease = await store.OpenReadConnectionAsync(token);
        return await lease.Connection.QuerySingleOrDefaultAsync<CliPairingView>(new CommandDefinition(
            "SELECT code Code,name Name,expires_at ExpiresAt,status Status FROM cli_pairings WHERE code=@code AND expires_at>@now",
            new { code, now = AuthTime.Format(clock.GetUtcNow()) }, cancellationToken: token));
    }

    public async Task ApprovePairingAsync(string code, AuthenticatedCredential actor, long passwordVerifiedEpoch, CancellationToken token = default) =>
        _ = await store.QueueWriteAsync(async (connection, transaction, cancellation) =>
        {
            await ValidateActorAsync(connection, transaction, actor, sessionOnly: true, admin: true, stepUp: false, TimeSpan.Zero, cancellation);
            // A paired terminal cannot grant another terminal interactive authority.
            var name = await connection.QuerySingleAsync<string>(new CommandDefinition("SELECT name FROM credentials WHERE id=@Id", new { actor.Id }, transaction, cancellationToken: cancellation));
            if (name == "cli" || passwordVerifiedEpoch != actor.AccountEpoch) throw new AuthorityChangedException();
            var changed = await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE cli_pairings SET status='approved',approver_id=@Id,approver_generation=@Generation,approver_epoch=@AccountEpoch,approved_at=@now
                WHERE code=@code AND status='pending' AND expires_at>@now
                """, new { code, actor.Id, actor.Generation, actor.AccountEpoch, now = AuthTime.Format(clock.GetUtcNow()) }, transaction, cancellationToken: cancellation));
            if (changed != 1) throw new CliPairingException("The pairing request is expired or has already been resolved.");
            return true;
        }, token);

    public async Task<bool> ClaimPairingAsync(string id, string pollVerifier, CredentialRecord credential, bool secureRequest, CancellationToken token = default) =>
        await store.QueueWriteAsync(async (connection, transaction, cancellation) =>
        {
            var row = await connection.QuerySingleOrDefaultAsync<PairingRow>(new CommandDefinition("""
                SELECT status Status,secure Secure,expires_at ExpiresAt,approved_at ApprovedAt,
                    approver_id ApproverId,approver_generation ApproverGeneration,approver_epoch ApproverEpoch
                FROM cli_pairings WHERE id=@id AND poll_verifier=@pollVerifier AND expires_at>@now
                """, new { id, pollVerifier, now = AuthTime.Format(clock.GetUtcNow()) }, transaction, cancellationToken: cancellation));
            if (row is null || row.Status == "claimed") throw new CliPairingException("The pairing request is invalid, expired, or already claimed.");
            if (row.Secure && !secureRequest) throw new AuthorityChangedException();
            if (row.Status == "pending") return false;
            var approver = await connection.QuerySingleOrDefaultAsync<CredentialRecord>(new CommandDefinition($"SELECT {Columns} FROM credentials WHERE id=@Id", new { Id = row.ApproverId }, transaction, cancellationToken: cancellation));
            if (approver is null) throw new AuthorityChangedException();
            var actor = new AuthenticatedCredential(approver.Id, "session", row.ApproverGeneration, row.ApproverEpoch, approver.GrantedScopes, row.ApprovedAt);
            await ValidateActorAsync(connection, transaction, actor, sessionOnly: true, admin: true, stepUp: false, TimeSpan.Zero, cancellation);
            if (credential.AccountEpoch != row.ApproverEpoch || credential.Kind != "session" || credential.Name != "cli" ||
                AuthTime.Parse(credential.AbsoluteExpiresAt!) > clock.GetUtcNow().AddHours(8)) throw new AuthorityChangedException();
            credential.SteppedUpAt = row.ApprovedAt;
            // Bind bearer use to the origin's transport security, independent of client input.
            credential.Device = System.Text.Json.JsonSerializer.Serialize(new SessionDevice("Paired CLI", row.Secure));
            await connection.ExecuteAsync(new CommandDefinition(Insert, credential, transaction, cancellationToken: cancellation));
            await connection.ExecuteAsync(new CommandDefinition("UPDATE cli_pairings SET status='claimed' WHERE id=@id", new { id }, transaction, cancellationToken: cancellation));
            return true;
        }, token);

    private sealed class PairingCounts { public int Total { get; set; } public int Local { get; set; } }
    private sealed class PairingRow
    {
        public string Status { get; set; } = "";
        public bool Secure { get; set; }
        public string ExpiresAt { get; set; } = "";
        public string? ApprovedAt { get; set; }
        public string? ApproverId { get; set; }
        public long ApproverGeneration { get; set; }
        public long ApproverEpoch { get; set; }
    }
}
