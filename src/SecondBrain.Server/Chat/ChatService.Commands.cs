using System.Globalization;
using System.Text.Json;
using SecondBrain.Core.Chat;

namespace SecondBrain.Server.Chat;

public sealed partial class ChatService
{
    private async Task<ChatAnswer> ExecuteSlashAsync(ChatSlashCommand command, ChatToolExecutor executor, Work work, CancellationToken token)
    {
        var turn = work.Turn;
        switch (command.Name)
        {
            case "/new": return new("Started a new conversation.", []);
            case "/search":
                var found = await executor.SearchAsync(command.Argument, cancellationToken: token);
                var text = found.Count == 0
                    ? executor.Warnings.Count == 0 ? "No matching passages were found." : "Retrieval was degraded; no passages were returned."
                    : string.Join("\n\n", found.Select(source => $"{source.Title}: {source.Excerpt} [{source.Number}]"));
                var answer = CitationVerifier.Verify(text, found);
                return answer with { Warnings = (answer.Warnings ?? []).Concat(executor.Warnings).Distinct().ToArray() };
            case "/query":
                return await executor.QueryCommandAsync(command.Query!, token);
            case "/recent":
                return await executor.RecentCommandAsync(int.Parse(command.Argument, CultureInfo.InvariantCulture), token);
            case "/pending":
                var pending = (await proposals.ListAsync(turn.ConversationId, work.Actor, token)).Where(proposal => proposal.Status is "pending" or "executing").ToArray();
                return new(pending.Length == 0 ? "No proposals are waiting in this conversation." :
                    string.Join("\n", pending.Select(proposal => $"{proposal.Id} · {proposal.Tool} · {proposal.Status}")), []);
            case "/sources":
                var previous = await PreviousAnswerAsync(work, token);
                if (previous?.Answer is not { Citations.Count: > 0 } prior) return new("The previous answer has no verified sources.", []);
                var originals = prior.Citations.Select(citation => citation.Source).ToArray();
                var sources = await repository.ExposeAsync(turn.Id, originals.Select(source => new ChatSourceRequest(source.DocumentId, source.Revision, source.ChunkId)).ToArray(), work.Actor, token);
                if (sources.Where((source, i) => source.Generation != originals[i].Generation || source.Sha256 != originals[i].Sha256).Any())
                    throw new ChatConflictException("A previous source has changed. Search again to retrieve its current content.");
                await repository.AppendAsync(turn.Id, "sources", JsonSerializer.SerializeToElement(sources), sources.Select(source => source.DocumentId).Distinct().ToArray(), work.Actor, token);
                return CitationVerifier.Verify(string.Join("\n\n", sources.Select(source => $"{source.Title} · revision {source.Revision}\n{source.Excerpt} [{source.Number}]")), sources);
            case "/save":
                var content = command.Argument;
                if (content.Length == 0)
                {
                    var last = await PreviousAnswerAsync(work, token);
                    if (last?.Answer is not { } lastAnswer || last.Redacted || string.IsNullOrWhiteSpace(lastAnswer.Text))
                        throw new ChatConflictException("There is no available answer to save. Use /save followed by the text to keep.");
                    content = lastAnswer.Text;
                    var manifest = await executions.ManifestAsync(last.Id, work.Actor, token);
                    await repository.AppendAsync(turn.Id, "history", JsonSerializer.SerializeToElement(new { savedAnswer = last.Id }),
                        manifest.Documents.Select(document => document.DocumentId).Distinct().ToArray(), work.Actor, token);
                }
                return await executor.SaveCommandAsync(content, token);
            default: throw new ArgumentException("Unknown chat command.");
        }
    }

    private async Task<ChatTurn?> PreviousAnswerAsync(Work work, CancellationToken token) =>
        (await repository.GetConversationAsync(work.Turn.ConversationId, work.Actor, token))
            .LastOrDefault(turn => turn.Ordinal < work.Turn.Ordinal && turn.Answer is not null &&
                !new[] { "/save", "/sources", "/pending", "/new" }.Contains(turn.Prompt.Trim().Split([' ', '\t', '\r', '\n'], 2)[0], StringComparer.Ordinal));
}
