using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Chat;
using SecondBrain.Server.Components.Pages;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class ApprovalCardTests
{
    [Theory]
    [InlineData("update", "Suggested update")]
    [InlineData("create", "Suggested note")]
    public async Task ReviewRendersMarkdownWithoutChangingTheProposedPayload(string operation, string heading)
    {
        const string content = """
            # Current status

            - **M2** is `deployed`.

            | Milestone | Status |
            | --- | --- |
            | M3 | Next |

            ```sh
            brain ask 'status'
            ```

            <script>alert(1)</script> ![track](https://example.com/pixel)
            """;
        var payload = JsonSerializer.SerializeToElement(new { title = "SecondBrain status", content });
        var plan = new ChatWritePlan(operation, "document", operation == "create" ? 0 : 1, "/vault/status.md", payload, "mutation");
        var proposal = new ChatProposal("proposal", "turn", "update_document", "pending", plan, "2026-10-24", "2026-10-27");
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IChatProposalRepository, NoMutations>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(ApprovalCard.Proposal)] = proposal,
                [nameof(ApprovalCard.Actor)] = new AuthenticatedCredential("actor", "session", 1, 1, new HashSet<Scope> { Scope.Read, Scope.Write }, null)
            });
            return (await renderer.RenderComponentAsync<ApprovalCard>(parameters)).ToHtmlString();
        });

        Assert.Contains(heading, html);
        Assert.Contains("<h3>Current status</h3>", html);
        Assert.Contains("<li><strong>M2</strong> is <code>deployed</code>.</li>", html);
        Assert.Contains("<table><thead>", html);
        Assert.Contains("<td>M3</td><td>Next</td>", html);
        Assert.Contains("<pre><code>brain ask", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("Approve change", html);
        Assert.Contains("Discard", html);
        Assert.Equal(content, plan.Payload.GetProperty("content").GetString());
    }

    private sealed class NoMutations : IChatProposalRepository
    {
        public ValueTask<ChatProposal> ProposeAsync(string turnId, string tool, JsonElement arguments, AuthenticatedCredential actor, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public ValueTask<IReadOnlyList<ChatProposal>> ListAsync(string? conversationId, AuthenticatedCredential actor, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public ValueTask<ChatProposal> ApproveAsync(string id, AuthenticatedCredential actor, bool automatic = false, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public ValueTask<ChatProposal> DiscardAsync(string id, AuthenticatedCredential actor, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public ValueTask<ChatProposal> EditAsync(string id, JsonElement arguments, AuthenticatedCredential actor, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public ValueTask<int> RecoverApprovalsAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
