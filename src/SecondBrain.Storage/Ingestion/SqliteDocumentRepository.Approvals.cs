using System.Text.Json;
using Dapper;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Chat;
using SecondBrain.Core.Ingestion;
using SecondBrain.Storage.Auth;

namespace SecondBrain.Storage.Ingestion;

public sealed partial class SqliteDocumentRepository
{
    public async ValueTask<CaptureResult> ExecuteApprovedAsync(ApprovedDocumentWrite write, IngestionAuthority authority, CancellationToken cancellationToken = default)
    {
        if (files is null) throw new InvalidOperationException("Approved writes require the managed-file journal.");
        var alreadyExecuted = await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await IngestionAuthorityValidator.ValidateAsync(connection, transaction, authority, Scope.Read, DateTimeOffset.UtcNow, sessionOnly: true, cancellationToken: token);
            await IngestionAuthorityValidator.ValidateAsync(connection, transaction, authority, Scope.Write, DateTimeOffset.UtcNow, sessionOnly: true, cancellationToken: token);
            var claim = await connection.QuerySingleOrDefaultAsync<ApprovalClaim>(new CommandDefinition("""
                SELECT p.status Status,p.approved_by Approver,p.execution_id ExecutionId,b.bytes Plan,
                  d.approver_generation Generation,d.approver_epoch Epoch
                FROM pending_operations p JOIN chat_proposal_details d ON d.proposal_id=p.id
                JOIN payloads b ON b.id=p.payload_id AND b.redacted_at IS NULL WHERE p.id=@id
                """, new { id = write.ProposalId }, transaction, cancellationToken: token)) ?? throw new AuthorityChangedException();
            if (claim.Approver != authority.CredentialId || claim.Generation != authority.CredentialGeneration || claim.Epoch != authority.AccountEpoch ||
                claim.ExecutionId != write.ExecutionId || claim.Status is not ("executing" or "executed") ||
                JsonSerializer.Serialize(JsonSerializer.Deserialize<ChatWritePlan>(claim.Plan, ApprovalJson), ApprovalJson) != JsonSerializer.Serialize(write.Plan, ApprovalJson))
                throw new AuthorityChangedException();
            return claim.Status == "executed";
        }, cancellationToken);
        if (alreadyExecuted) return new(await GetAsync(write.Plan.DocumentId, true, cancellationToken) ?? throw new IngestionConflictException("not-found"), null);
        if (write.Plan.Operation == "create")
        {
            if (write.Plan.Destination != files.GetAssistantNotePath(write.Plan.DocumentId)) throw new AuthorityChangedException();
            var request = JsonSerializer.Deserialize<CaptureRequest>(write.Plan.Payload, ApprovalJson) ?? throw new ArgumentException("Invalid capture proposal.");
            await captureGate.WaitAsync(cancellationToken);
            try { return await CaptureCoreAsync(request, authority, "proposal:" + write.ProposalId, cancellationToken, write); }
            finally { captureGate.Release(); }
        }
        if (write.Plan.Operation != "update") throw new ArgumentException("Unsupported approved operation.");
        return await MutateCoreAsync(write.Plan.DocumentId, write.Plan.Revision, "update", write.Plan.Payload, authority, cancellationToken, write);
    }

    private static readonly JsonSerializerOptions ApprovalJson = new(JsonSerializerDefaults.Web);
    private sealed class ApprovalClaim
    {
        public string Status { get; set; } = "";
        public string Approver { get; set; } = "";
        public string ExecutionId { get; set; } = "";
        public byte[] Plan { get; set; } = [];
        public long Generation { get; set; }
        public long Epoch { get; set; }
    }
}
