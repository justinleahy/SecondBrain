using System.Data.Common;
using System.Text;
using System.Text.Json;
using Dapper;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Chat;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Domain;
using SecondBrain.Core.Ingestion;
using SecondBrain.Storage.Auth;

namespace SecondBrain.Storage.Chat;

public sealed partial class SqliteChatRepository
{
    public ValueTask<ChatProposal> ProposeAsync(string turnId, string tool, JsonElement arguments,
        AuthenticatedCredential actor, CancellationToken cancellationToken = default) =>
        ProposeCoreAsync(turnId, tool, arguments, null, actor, cancellationToken);

    public ValueTask<ChatProposal> ProposeForCallAsync(string turnId, string tool, JsonElement arguments, string executionId,
        AuthenticatedCredential actor, CancellationToken cancellationToken = default) =>
        ProposeCoreAsync(turnId, tool, arguments, executionId, actor, cancellationToken);

    private async ValueTask<ChatProposal> ProposeCoreAsync(string turnId, string tool, JsonElement arguments, string? executionId,
        AuthenticatedCredential actor, CancellationToken cancellationToken)
    {
        await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            var turn = await RunningAsync(connection, transaction, turnId, actor, token);
            if (turn.Mode == "ask") throw new AuthorityChangedException();
            await WriteAuthorityAsync(connection, transaction, actor, false, token);
            return 0;
        }, cancellationToken);
        var plan = await PlanAsync(tool, arguments, cancellationToken);
        return await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            var turn = await RunningAsync(connection, transaction, turnId, actor, token);
            if (turn.Mode == "ask") throw new AuthorityChangedException();
            await WriteAuthorityAsync(connection, transaction, actor, false, token);
            if (executionId is not null && await connection.ExecuteScalarAsync<int>(Sql("""
                SELECT COUNT(*) FROM tool_executions WHERE id=@executionId AND turn_id=@turnId AND tool=@tool
                AND status='running' AND args_hash=@hash
                """, new { executionId, turnId, tool, hash = Hash(JsonSerializer.SerializeToUtf8Bytes(arguments, Json)) }, transaction, token)) != 1)
                throw new ChatConflictException("The proposal has no matching active tool invocation.");
            var proposal = await InsertProposalAsync(connection, transaction, turn, tool, plan, actor, token);
            if (executionId is not null)
                await connection.ExecuteAsync(Sql("INSERT INTO chat_proposal_calls(proposal_id,invocation_id) VALUES(@id,@executionId)", new { id = proposal.Id, executionId }, transaction, token));
            return proposal;
        }, cancellationToken);
    }

    public ValueTask<IReadOnlyList<ChatProposal>> ListAsync(string? conversationId, AuthenticatedCredential actor, CancellationToken cancellationToken = default) => ReadAsync<IReadOnlyList<ChatProposal>>(async (connection, transaction, token) =>
    {
        await AuthorizeAsync(connection, transaction, actor, false, token);
        if (conversationId is not null) await OwnConversationAsync(connection, transaction, conversationId, actor, token);
        var rows = await connection.QueryAsync<ProposalRow>(Sql(ProposalSelect + " WHERE t.account_epoch=@epoch AND (@kind='session' OR p.creator_credential_id=@actor) AND (@conversationId IS NULL OR t.conversation_id=@conversationId) ORDER BY CASE WHEN p.status IN ('pending','executing') THEN 0 ELSE 1 END,p.created_at DESC,p.id DESC LIMIT 200", new { conversationId, epoch = actor.AccountEpoch, kind = actor.Kind, actor = actor.Id }, transaction, token));
        return rows.Select(ToProposal).ToArray();
    }, cancellationToken);

    public async ValueTask<ChatProposal> ApproveAsync(string id, AuthenticatedCredential actor, bool automatic = false, CancellationToken cancellationToken = default)
    {
        var claim = await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await WriteAuthorityAsync(connection, transaction, actor, true, token);
            var row = await ProposalAsync(connection, transaction, id, token);
            await OwnProposalAsync(connection, transaction, row, actor, token);
            if (row.Status != "pending") return (Row: row, Execute: false);
            var problem = await ProposalProblemAsync(connection, transaction, row, token);
            if (problem is not null)
            {
                await ResolveProposalAsync(connection, transaction, row, problem, problem, token);
                return (Row: await ProposalAsync(connection, transaction, id, token), Execute: false);
            }
            var plan = ToProposal(row).Plan!;
            if (automatic)
            {
                if (await connection.ExecuteScalarAsync<string>(Sql("SELECT name FROM credentials WHERE id=@id", new { id = actor.Id }, transaction, token)) == "cli")
                    throw new AuthorityChangedException();
                var turn = await TurnAsync(connection, transaction, row.TurnId, token);
                var retrieved = await connection.ExecuteScalarAsync<long>(Sql("SELECT retrieved_content FROM turns WHERE id=@id", new { id = row.TurnId }, transaction, token));
                if (turn.Initiator != actor.Id || turn.Generation != actor.Generation || turn.Epoch != actor.AccountEpoch ||
                    !Policy(turn).AutoApproveNotes || !Configuration.Assistant.AutoApproveWrites || retrieved != 0 ||
                    plan.Operation != "create" && !plan.AppendOnly || plan.Payload.GetProperty("type").GetString() != "note" ||
                    plan.Destination != ManagedFiles.GetAssistantNotePath(plan.DocumentId)) throw new AuthorityChangedException();
            }
            var changed = await connection.ExecuteAsync(Sql("""
                UPDATE pending_operations SET status='executing',approved_by=@actor WHERE id=@id AND status='pending';
                """, new { id, actor = actor.Id }, transaction, token));
            if (changed != 1) throw new ChatConflictException("The proposal is already being resolved.");
            await connection.ExecuteAsync(Sql("""
                UPDATE chat_proposal_details SET approver_generation=@generation,approver_epoch=@epoch WHERE proposal_id=@id;
                UPDATE tool_executions SET status='running' WHERE id=@execution AND status='pending_approval';
                """, new { id, generation = actor.Generation, epoch = actor.AccountEpoch, execution = row.ExecutionId }, transaction, token));
            return (Row: await ProposalAsync(connection, transaction, id, token), Execute: true);
        }, cancellationToken);
        if (!claim.Execute) return ToProposal(claim.Row);
        // Once claimed, browser cancellation cannot abandon an accepted write.
        await ExecuteClaimAsync(claim.Row, actor, CancellationToken.None);
        return await ReadAsync(async (connection, transaction, token) => ToProposal(await ProposalAsync(connection, transaction, id, token)), CancellationToken.None);
    }

    public ValueTask<ChatProposal> DiscardAsync(string id, AuthenticatedCredential actor, CancellationToken cancellationToken = default) => state.QueueWriteAsync(async (connection, transaction, token) =>
    {
        await WriteAuthorityAsync(connection, transaction, actor, true, token);
        var row = await ProposalAsync(connection, transaction, id, token);
        await OwnProposalAsync(connection, transaction, row, actor, token);
        if (row.Status == "pending") await ResolveProposalAsync(connection, transaction, row, "discarded", null, token);
        return ToProposal(await ProposalAsync(connection, transaction, id, token));
    }, cancellationToken);

    public async ValueTask<ChatProposal> EditAsync(string id, JsonElement arguments, AuthenticatedCredential actor, CancellationToken cancellationToken = default)
    {
        var source = await ReadAsync(async (connection, transaction, token) =>
        {
            await WriteAuthorityAsync(connection, transaction, actor, true, token);
            var row = await ProposalAsync(connection, transaction, id, token);
            await OwnProposalAsync(connection, transaction, row, actor, token);
            if (row.Status != "pending") throw new ChatConflictException("Only pending proposals can be edited.");
            return row;
        }, cancellationToken);
        var replacementTool = source.Tool == "create_document" ? source.Tool : "update_document";
        var plan = await PlanAsync(replacementTool, arguments, cancellationToken);
        return await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await WriteAuthorityAsync(connection, transaction, actor, true, token);
            var previous = await ProposalAsync(connection, transaction, id, token);
            if (previous.Status != "pending") throw new ChatConflictException("The proposal was already resolved.");
            var turn = await TurnAsync(connection, transaction, source.TurnId, token);
            var replacement = await InsertProposalAsync(connection, transaction, turn, replacementTool, plan, actor, token, edited: true);
            await ResolveProposalAsync(connection, transaction, previous, "superseded", null, token);
            return replacement;
        }, cancellationToken);
    }

    public async ValueTask<int> RecoverApprovalsAsync(CancellationToken cancellationToken = default)
    {
        var rows = await ReadAsync(async (connection, transaction, token) =>
            (await connection.QueryAsync<ProposalRow>(Sql(ProposalSelect + " WHERE p.status='executing' ORDER BY p.created_at,p.id", null, transaction, token))).ToArray(), cancellationToken);
        foreach (var row in rows)
        {
            var actor = new AuthenticatedCredential(row.Approver!, "session", row.ApproverGeneration!.Value, row.ApproverEpoch!.Value, new HashSet<Scope>(), null);
            await ExecuteClaimAsync(row, actor, cancellationToken);
        }
        return rows.Length;
    }

    private async Task ExecuteClaimAsync(ProposalRow claim, AuthenticatedCredential actor, CancellationToken token)
    {
        var plan = ToProposal(claim).Plan;
        if (plan is null)
        {
            var committed = await ReadAsync((connection, transaction, cancellation) => connection.ExecuteScalarAsync<bool>(Sql("""
                SELECT EXISTS(SELECT 1 FROM chat_proposal_details d JOIN mutations m ON m.id=d.mutation_id
                WHERE d.proposal_id=@id AND m.status='finalized')
                """, new { id = claim.Id }, transaction, cancellation)), token);
            if (committed)
                await state.QueueWriteAsync(async (connection, transaction, cancellation) =>
                {
                    await CompleteRedactedClaimAsync(connection, transaction, claim,
                        JsonSerializer.Deserialize<string[]>(claim.DocumentRefs, Json) ?? [], cancellation);
                    return 0;
                }, token);
            else await FailClaimAsync(claim, "source_purged", token);
            return;
        }
        try
        {
            var problem = await state.QueueWriteAsync(async (connection, transaction, cancellation) =>
            {
                await WriteAuthorityAsync(connection, transaction, actor, true, cancellation);
                return await ProposalProblemAsync(connection, transaction, await ProposalAsync(connection, transaction, claim.Id, cancellation), cancellation);
            }, token);
            if (problem is not null) { await FailClaimAsync(claim, problem, token); return; }
            var authority = new IngestionAuthority(actor.Id, actor.Generation, actor.AccountEpoch, "", actor.Kind);
            await Documents.ExecuteApprovedAsync(new(claim.Id, claim.ExecutionId, plan), authority, token);
            await state.QueueWriteAsync(async (connection, transaction, cancellation) =>
            {
                var row = await ProposalAsync(connection, transaction, claim.Id, cancellation);
                if (row.Status is not ("executing" or "executed")) throw new ChatConflictException("The approved write changed state unexpectedly.");
                var refs = JsonSerializer.Deserialize<string[]>(row.DocumentRefs, Json) ?? [];
                refs = refs.Append(plan.DocumentId).Distinct().ToArray();
                if (!await LiveLineageAsync(connection, transaction, refs, cancellation))
                {
                    // The journal already committed the effect, but a purge won the race
                    // before this callback. Retain that outcome without restoring its data.
                    await CompleteRedactedClaimAsync(connection, transaction, row, refs, cancellation);
                    return 0;
                }
                var result = await PayloadAsync(connection, transaction, new { documentId = plan.DocumentId, revision = plan.Revision + 1 }, refs, "chat-execution", row.TurnId, cancellation);
                await connection.ExecuteAsync(Sql("""
                    UPDATE pending_operations SET status='executed',resolved_at=@now WHERE id=@id;
                    UPDATE tool_executions SET status='committed',result_payload_id=@result,committed_at=@now WHERE id=@execution;
                    UPDATE payloads SET document_refs_json=@refs WHERE id=@payload;
                    UPDATE payloads SET document_refs_json=@refs WHERE id IN
                      (SELECT e.payload_id FROM chat_proposal_calls c JOIN tool_executions e ON e.id=c.invocation_id WHERE c.proposal_id=@id);
                    """, new { id = row.Id, now = Now(), result, execution = row.ExecutionId, payload = row.PayloadId, refs = JsonSerializer.Serialize(refs, Json) }, transaction, cancellation));
                await EventAsync(connection, transaction, row.TurnId, "proposal", new { id = row.Id, status = "executed", documentId = plan.DocumentId }, refs, cancellation);
                return 0;
            }, token);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (error is AuthorityChangedException)
            {
                await FailClaimAsync(claim, "authority_changed", CancellationToken.None);
                return;
            }
            // An existing journal entry may already own file effects. Recovery must inspect/replay
            // its stable identity; converting this into a fresh proposal could duplicate those effects.
            var journalStatus = await ReadAsync((connection, transaction, cancellation) => connection.ExecuteScalarAsync<string?>(Sql("SELECT status FROM mutations WHERE id=@id", new { id = plan.MutationId }, transaction, cancellation)), CancellationToken.None);
            if (journalStatus is "prepared" or "applied" or "finalized") return;
            await FailClaimAsync(claim, error is AuthorityChangedException ? "authority_changed" : "execution_failed", CancellationToken.None);
        }
    }

    private async Task CompleteRedactedClaimAsync(DbConnection connection, DbTransaction transaction, ProposalRow row, string[] refs, CancellationToken token)
    {
        var marker = await PayloadAsync(connection, transaction, new { redacted = true }, refs, "chat-execution", row.TurnId, token);
        await connection.ExecuteAsync(Sql("""
            UPDATE payloads SET bytes=X'',sha256='',redacted_at=@now,document_refs_json=@refs
            WHERE id IN (@payload,@marker) OR id IN
              (SELECT e.payload_id FROM chat_proposal_calls c JOIN tool_executions e ON e.id=c.invocation_id WHERE c.proposal_id=@id);
            UPDATE pending_operations SET status='executed',resolved_at=@now WHERE id=@id;
            UPDATE tool_executions SET status='committed',result_payload_id=@marker,committed_at=@now WHERE id=@execution;
            """, new { id = row.Id, payload = row.PayloadId, marker, execution = row.ExecutionId, now = Now(), refs = JsonSerializer.Serialize(refs, Json) }, transaction, token));
    }

    private ValueTask<int> FailClaimAsync(ProposalRow row, string reason, CancellationToken token) => state.QueueWriteAsync(async (connection, transaction, cancellation) =>
    {
        await ResolveProposalAsync(connection, transaction, row, "failed", reason, cancellation);
        return 0;
    }, token);

    private async Task<ChatWritePlan> PlanAsync(string tool, JsonElement arguments, CancellationToken token)
    {
        if (arguments.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(arguments.GetRawText()) > 65_536) throw new ArgumentException("Invalid proposal arguments.");
        if (tool is not ("create_document" or "update_document" or "tag_document" or "link_documents")) throw new ArgumentException("Unknown write tool.");
        var allowed = new HashSet<string>(["id", "revision", "content", "title", "type", "props", "tags", "append", "targetId"], StringComparer.Ordinal);
        if (arguments.EnumerateObject().Any(property => !allowed.Contains(property.Name))) throw new ArgumentException("Unknown proposal field.");
        var create = tool == "create_document";
        var id = create ? NewId() : arguments.GetProperty("id").GetString() ?? throw new ArgumentException("Document ID is required.");
        var before = create ? null : await Documents.GetAsync(id, true, token) ?? throw new ChatNotFoundException();
        var revision = create ? 0 : arguments.GetProperty("revision").GetInt64();
        if (before is not null && (before.Revision != revision || DocumentStatusNames.IsUnavailable(before.Status))) throw new ChatConflictException("The target document changed or is unavailable.");
        var hasContent = arguments.TryGetProperty("content", out var contentValue);
        var beforeText = before is not null && (!hasContent || tool == "link_documents" || arguments.TryGetProperty("append", out var requestedAppend) && requestedAppend.GetBoolean())
            ? await OriginalTextAsync(before, token) : "";
        var content = hasContent ? contentValue.GetString() ?? "" : beforeText;
        var appendOnly = !create && arguments.TryGetProperty("append", out var append) && append.GetBoolean();
        if (appendOnly)
        {
            if (arguments.EnumerateObject().Any(property => property.Name is not ("id" or "revision" or "content" or "append"))) throw new ArgumentException("An append cannot change metadata.");
            content = beforeText + (beforeText.EndsWith('\n') ? "" : "\n") + content;
        }
        string[] related = [];
        if (tool == "link_documents")
        {
            var targetId = arguments.GetProperty("targetId").GetString() ?? throw new ArgumentException("Link target is required.");
            var target = await Documents.GetAsync(targetId, false, token) ?? throw new ChatNotFoundException();
            if (DocumentStatusNames.IsUnavailable(target.Status)) throw new ChatNotFoundException();
            related = [targetId];
            content = beforeText + (beforeText.EndsWith('\n') ? "" : "\n") + "[[" + targetId + "]]\n";
        }
        var title = arguments.TryGetProperty("title", out var titleValue) ? titleValue.GetString() : before?.Title;
        title ??= content.Split('\n', 2)[0];
        if (title.Length > 512 || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content) || Encoding.UTF8.GetByteCount(content) > 32_768) throw new ArgumentException("A proposal needs a title and content within the note limits.");
        var type = arguments.TryGetProperty("type", out var typeValue) ? typeValue.GetString() ?? "note" : before?.Type ?? "note";
        var props = arguments.TryGetProperty("props", out var propsValue) ? propsValue.Clone() : JsonSerializer.Deserialize<JsonElement>(before?.PropsJson ?? "{}");
        if (!new TypeRegistry().ValidateProperties(type, props).IsValid) throw new ArgumentException("Invalid document properties.");
        var tags = arguments.TryGetProperty("tags", out var tagsValue) ? tagsValue.Deserialize<string[]>(Json) ?? [] : before?.Tags ?? [];
        if (tags.Length > 100 || tags.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Length > 100)) throw new ArgumentException("Invalid tags.");
        var destination = create ? ManagedFiles.GetAssistantNotePath(id) : before!.Path;
        if (destination is null || !string.Equals(destination, ManagedFiles.GetManagedPath(id, Path.GetExtension(destination)), StringComparison.Ordinal) &&
            !string.Equals(destination, ManagedFiles.GetAssistantNotePath(id), StringComparison.Ordinal)) throw new ChatConflictException("This assistant can write only managed notes.");
        if (Path.GetExtension(destination) is not (".md" or ".markdown" or ".txt")) throw new ChatConflictException("This document cannot be edited as a note.");
        var payload = JsonSerializer.SerializeToElement(new { content, title, type, props, tags = tags.Distinct().ToArray() }, Json);
        return new(create ? "create" : "update", id, revision, destination, payload, NewId(), related, appendOnly);
    }

    private async Task<ChatProposal> InsertProposalAsync(DbConnection connection, DbTransaction transaction, TurnRow turn,
        string tool, ChatWritePlan plan, AuthenticatedCredential actor, CancellationToken token, bool edited = false)
    {
        var refs = await ReferencesAsync(connection, transaction, turn.Id, token);
        var prompt = ToTurn(turn).Prompt;
        if (plan.Operation != "create" && !edited && !refs.Contains(plan.DocumentId) && !prompt.Contains(plan.DocumentId, StringComparison.Ordinal))
            throw new ChatConflictException("The target must be surfaced in this turn or named by the user.");
        foreach (var related in plan.RelatedDocumentIds ?? [])
            if (!refs.Contains(related) && !prompt.Contains(related, StringComparison.Ordinal)) throw new ChatConflictException("The link target must be surfaced or named by the user.");
        if (plan.Operation != "create") refs = refs.Append(plan.DocumentId).Distinct().ToArray();
        refs = refs.Concat(plan.RelatedDocumentIds ?? []).Distinct().ToArray();
        var targets = new List<ProposalTarget>();
        foreach (var id in refs)
        {
            var revision = await connection.ExecuteScalarAsync<long?>(Sql("SELECT revision FROM documents WHERE id=@id AND status NOT IN ('deleted','purged','missing','removed_from_source')", new { id }, transaction, token));
            if (revision is null || id == plan.DocumentId && revision != plan.Revision) throw new ChatConflictException("A referenced document changed.");
            if (await connection.ExecuteScalarAsync<long>(Sql("SELECT COUNT(*) FROM chat_sources WHERE turn_id=@turn AND document_id=@id AND revision!=@revision", new { turn = turn.Id, id, revision }, transaction, token)) != 0)
                throw new ChatConflictException("A retrieved source changed before the proposal was prepared.");
            targets.Add(new(id, revision.Value));
        }
        var idValue = NewId(); var execution = NewId();
        var payload = await PayloadAsync(connection, transaction, plan, refs, "chat-proposal", turn.Id, token);
        var hash = Hash(JsonSerializer.SerializeToUtf8Bytes(plan, Json));
        var now = Now(); var expiry = AuthTime.Format(clock.GetUtcNow().AddHours(Configuration.Assistant.PendingTtlHours));
        await connection.ExecuteAsync(Sql("""
            INSERT INTO tool_executions(id,turn_id,tool,payload_id,args_hash,status,initiating_credential_id,credential_generation,account_epoch)
              VALUES(@execution,@turnId,@tool,@payload,@hash,'pending_approval',@actor,@generation,@epoch);
            INSERT INTO pending_operations(id,turn_id,creator_credential_id,tool,tool_schema_version,payload_id,payload_hash,
              targets_json,destination,policy_generation,status,execution_id,created_at,expires_at)
              VALUES(@id,@turnId,@actor,@tool,1,@payload,@hash,@targets,@destination,1,'pending',@execution,@now,@expiry);
            INSERT INTO chat_proposal_details(proposal_id,mutation_id,policy_hash,creator_generation,creator_epoch)
              VALUES(@id,@mutation,@policy,@generation,@epoch);
            """, new { id = idValue, turnId = turn.Id, tool, payload, hash, actor = actor.Id, generation = actor.Generation,
            epoch = actor.AccountEpoch, execution, targets = JsonSerializer.Serialize(targets, Json), destination = plan.Destination,
            now, expiry, mutation = plan.MutationId, policy = PolicyHash() }, transaction, token));
        await EventAsync(connection, transaction, turn.Id, "proposal", new { id = idValue, status = "pending", tool }, refs, token);
        return ToProposal(await ProposalAsync(connection, transaction, idValue, token));
    }

    private async Task<string> OriginalTextAsync(DocumentRecord document, CancellationToken token)
    {
        var path = document.Path ?? throw new ChatConflictException("The document has no managed original.");
        if (path != ManagedFiles.GetAssistantNotePath(document.Id) && path != ManagedFiles.GetManagedPath(document.Id, Path.GetExtension(path)))
            throw new ChatConflictException("This assistant can write only managed notes.");
        await using var stream = await ManagedFiles.OpenReadAsync(path, token);
        var bytes = new byte[32_769];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length), token);
            if (read == 0) break;
            length += read;
        }
        if (length > 32_768) throw new ChatLimitException("This note exceeds the assistant's write limit.");
        var content = bytes.AsSpan(0, length).ToArray();
        if (!string.Equals(Hash(content), document.ContentHash, StringComparison.OrdinalIgnoreCase)) throw new ChatConflictException("The original changed outside SecondBrain.");
        try { return new UTF8Encoding(false, true).GetString(content); }
        catch (DecoderFallbackException) { throw new ChatConflictException("This original is not a UTF-8 note."); }
    }

    private async Task<string?> ProposalProblemAsync(DbConnection connection, DbTransaction transaction, ProposalRow row, CancellationToken token)
    {
        if (row.RedactedAt is not null) return "source_purged";
        if (row.Status == "pending" && AuthTime.Expired(row.ExpiresAt, clock.GetUtcNow())) return "expired";
        if (row.PolicyHash != PolicyHash()) return "policy_changed";
        if (Hash(row.Bytes) != row.PayloadHash) return "payload_changed";
        try
        {
            await IngestionAuthorityValidator.ValidateAsync(connection, transaction,
                new(row.Creator, row.CreatorGeneration, row.CreatorEpoch, "", row.CreatorKind), Scope.Write, clock.GetUtcNow(), cancellationToken: token);
        }
        catch (AuthorityChangedException) { return "creator_changed"; }
        foreach (var target in JsonSerializer.Deserialize<ProposalTarget[]>(row.Targets, Json) ?? [])
        {
            var revision = await connection.ExecuteScalarAsync<long?>(Sql("SELECT revision FROM documents WHERE id=@id AND status NOT IN ('deleted','purged','missing')", new { id = target.DocumentId }, transaction, token));
            if (revision != target.Revision) return "target_changed";
        }
        return null;
    }

    private async Task ResolveProposalAsync(DbConnection connection, DbTransaction transaction, ProposalRow row, string status, string? reason, CancellationToken token)
    {
        await connection.ExecuteAsync(Sql("""
            UPDATE pending_operations SET status=@status,resolved_at=@now WHERE id=@id AND status IN ('pending','executing');
            UPDATE chat_proposal_details SET failure_code=@reason WHERE proposal_id=@id;
            UPDATE tool_executions SET status=@status WHERE id=@execution AND status IN ('running','pending_approval');
            """, new { id = row.Id, status, now = Now(), reason, execution = row.ExecutionId }, transaction, token));
        await EventAsync(connection, transaction, row.TurnId, "proposal", new { id = row.Id, status, reason }, [], token);
    }

    private Task WriteAuthorityAsync(DbConnection connection, DbTransaction transaction, AuthenticatedCredential actor, bool sessionOnly, CancellationToken token) =>
        ValidateWriteAsync(connection, transaction, actor, sessionOnly, token);
    private static async Task OwnProposalAsync(DbConnection connection, DbTransaction transaction, ProposalRow row, AuthenticatedCredential actor, CancellationToken token)
    {
        var turn = await TurnAsync(connection, transaction, row.TurnId, token);
        if (actor.Kind != "session" || turn.Epoch != actor.AccountEpoch) throw new ChatNotFoundException();
    }
    private async Task ValidateWriteAsync(DbConnection connection, DbTransaction transaction, AuthenticatedCredential actor, bool sessionOnly, CancellationToken token)
    {
        await AuthorizeAsync(connection, transaction, actor, false, token);
        await IngestionAuthorityValidator.ValidateAsync(connection, transaction, new(actor.Id, actor.Generation, actor.AccountEpoch, "", actor.Kind), Scope.Write,
            clock.GetUtcNow(), sessionOnly: sessionOnly, cancellationToken: token);
    }

    private SecondBrainOptions Configuration => options?.CurrentValue ?? throw new InvalidOperationException("Assistant configuration is required for proposals.");
    private IDocumentRepository Documents => documents ?? throw new InvalidOperationException("The document repository is required for proposals.");
    private IManagedDocumentFiles ManagedFiles => files ?? throw new InvalidOperationException("Managed files are required for proposals.");
    private string PolicyHash() => Hash(JsonSerializer.SerializeToUtf8Bytes(new { Configuration.DataRoot, Configuration.Assistant, Configuration.Time.Zone }, Json));
    private static async Task<ProposalRow> ProposalAsync(DbConnection connection, DbTransaction transaction, string id, CancellationToken token) =>
        await connection.QuerySingleOrDefaultAsync<ProposalRow>(Sql(ProposalSelect + " WHERE p.id=@id", new { id }, transaction, token)) ?? throw new ChatNotFoundException();
    private static ChatProposal ToProposal(ProposalRow row) => new(row.Id, row.TurnId, row.Tool, row.Status,
        row.RedactedAt is null ? JsonSerializer.Deserialize<ChatWritePlan>(row.Bytes, Json) : null, row.CreatedAt, row.ExpiresAt, row.FailureCode, row.ExecutionId);
    private const string ProposalSelect = """
        SELECT p.id Id,p.turn_id TurnId,p.tool Tool,p.status Status,p.payload_id PayloadId,p.payload_hash PayloadHash,
          p.targets_json Targets,p.execution_id ExecutionId,p.created_at CreatedAt,p.expires_at ExpiresAt,
          p.approved_by Approver,p.creator_credential_id Creator,c.kind CreatorKind,d.creator_generation CreatorGeneration,d.creator_epoch CreatorEpoch,
          d.policy_hash PolicyHash,d.approver_generation ApproverGeneration,d.approver_epoch ApproverEpoch,
          d.failure_code FailureCode,b.bytes Bytes,b.redacted_at RedactedAt,b.document_refs_json DocumentRefs
        FROM pending_operations p JOIN chat_proposal_details d ON d.proposal_id=p.id JOIN payloads b ON b.id=p.payload_id
          JOIN turns t ON t.id=p.turn_id
          JOIN credentials c ON c.id=p.creator_credential_id
        """;
    private sealed record ProposalTarget(string DocumentId, long Revision);
    private sealed class ProposalRow
    {
        public string Id { get; set; } = ""; public string TurnId { get; set; } = ""; public string Tool { get; set; } = "";
        public string Status { get; set; } = ""; public string PayloadId { get; set; } = ""; public string PayloadHash { get; set; } = "";
        public string Targets { get; set; } = "[]"; public string ExecutionId { get; set; } = ""; public string CreatedAt { get; set; } = "";
        public string ExpiresAt { get; set; } = ""; public string? Approver { get; set; } public string PolicyHash { get; set; } = "";
        public long? ApproverGeneration { get; set; } public long? ApproverEpoch { get; set; } public string? FailureCode { get; set; }
        public string Creator { get; set; } = ""; public string CreatorKind { get; set; } = ""; public long CreatorGeneration { get; set; } public long CreatorEpoch { get; set; }
        public byte[] Bytes { get; set; } = []; public string? RedactedAt { get; set; } public string DocumentRefs { get; set; } = "[]";
    }
}
