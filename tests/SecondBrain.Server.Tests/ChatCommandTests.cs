using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Chat;
using SecondBrain.Core.Ingestion;
using SecondBrain.MockProvider;
using SecondBrain.Server.Chat;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed partial class ChatWorkflowTests
{
    [Fact]
    public async Task SlashSearchQueryRecentSourcesAndNewUseApplicationServicesWithoutTheModel()
    {
        await using var factory = BufferedFactory();
        var actor = await CreateActorAsync(factory);
        var documents = factory.Services.GetRequiredService<IDocumentRepository>();
        var note = (await documents.CaptureAsync(new("Orchard apples are red.", "Orchard", Tags: ["chosen"]), new(actor.Id, actor.Generation, actor.AccountEpoch, "browser", "session"))).Document;
        var calls = 0;
        factory.MockProvider.Services.GetRequiredService<MockProviderState>().ChatResponse = context =>
        {
            Interlocked.Increment(ref calls);
            return TextResponseAsync(context, "Unexpected model call");
        };
        var service = factory.Services.GetRequiredService<ChatService>();
        var repository = factory.Services.GetRequiredService<IChatRepository>();
        var search = await service.StartAsync(new("/search orchard"), actor);
        var searched = await FinishAsync(repository, search.Turn.Id, actor);
        Assert.Equal("complete", searched.Status);
        Assert.Equal(note.Id, Assert.Single(searched.Answer!.Citations).Source.DocumentId);
        var sources = await service.StartAsync(new("/sources", searched.ConversationId), actor);
        Assert.Equal(note.Id, Assert.Single((await FinishAsync(repository, sources.Turn.Id, actor)).Answer!.Citations).Source.DocumentId);
        foreach (var prompt in new[] { "/query note {\"tags\":[\"chosen\"]}", "/recent 2" })
        {
            var command = await service.StartAsync(new(prompt, searched.ConversationId), actor);
            var completed = await FinishAsync(repository, command.Turn.Id, actor);
            Assert.Equal("complete", completed.Status);
            Assert.Contains(note.Id, completed.Answer!.Text);
            Assert.Contains(await factory.Services.GetRequiredService<IChatExecutionRepository>().ActivityAsync(completed.Id, actor), item => item.Status == "committed");
        }
        var fresh = await service.StartAsync(new("/new", searched.ConversationId), actor);
        Assert.NotEqual(searched.ConversationId, fresh.Turn.ConversationId);
        Assert.Equal("complete", (await FinishAsync(repository, fresh.Turn.Id, actor)).Status);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task SavePreviousSourcedAnswerRetainsLineageAndRequiresApprovalEvenWhenAutoApproveIsEnabled()
    {
        await using var factory = new Support.LaneDWebFactory(options =>
        {
            options.Models.Chat!.Capabilities = options.Models.Chat.Capabilities with { Streaming = false };
            options.Models.Chat.Limits = options.Models.Chat.Limits with { ContextTokens = 32768 };
            options.Assistant.AutoApproveWrites = true;
        });
        var actor = await CreateActorAsync(factory);
        var documents = factory.Services.GetRequiredService<IDocumentRepository>();
        var source = (await documents.CaptureAsync(new("Orchard apples are red.", "Orchard"), new(actor.Id, actor.Generation, actor.AccountEpoch, "browser", "session"))).Document;
        var service = factory.Services.GetRequiredService<ChatService>();
        var repository = factory.Services.GetRequiredService<IChatRepository>();
        var search = await service.StartAsync(new("/search orchard"), actor);
        await FinishAsync(repository, search.Turn.Id, actor);
        var save = await service.StartAsync(new("/save", search.Turn.ConversationId), actor);
        var saved = await FinishAsync(repository, save.Turn.Id, actor);
        Assert.Equal("complete", saved.Status);
        var proposal = Assert.Single(await factory.Services.GetRequiredService<IChatProposalRepository>().ListAsync(search.Turn.ConversationId, actor));
        Assert.Equal("pending", proposal.Status);
        Assert.False(File.Exists(proposal.Plan!.Destination));
        Assert.Contains((await factory.Services.GetRequiredService<IChatExecutionRepository>().ManifestAsync(saved.Id, actor)).Documents, document => document.DocumentId == source.Id);
        await documents.MutateAsync(source.Id, 1, "purge", JsonSerializer.SerializeToElement(new { }), new(actor.Id, actor.Generation, actor.AccountEpoch, "browser", "session"));
        Assert.Null(Assert.Single(await factory.Services.GetRequiredService<IChatProposalRepository>().ListAsync(search.Turn.ConversationId, actor)).Plan);
    }

    [Fact]
    public async Task ExplicitSaveWaitsForApprovalAndReadOnlyAskRejectsIt()
    {
        await using var factory = BufferedFactory();
        var actor = await CreateActorAsync(factory);
        var service = factory.Services.GetRequiredService<ChatService>();
        var repository = factory.Services.GetRequiredService<IChatRepository>();
        var proposed = await service.StartAsync(new("/save Remember the new plan"), actor);
        Assert.Equal("complete", (await FinishAsync(repository, proposed.Turn.Id, actor)).Status);
        var pending = await service.StartAsync(new("/pending", proposed.Turn.ConversationId), actor);
        Assert.Contains("create_document", (await FinishAsync(repository, pending.Turn.Id, actor)).Answer!.Text);
        var denied = await service.StartAsync(new("/save Must not write", Mode: "ask"), actor);
        Assert.Equal("partial", (await FinishAsync(repository, denied.Turn.Id, actor)).Status);
        var proposal = Assert.Single(await factory.Services.GetRequiredService<IChatProposalRepository>().ListAsync(null, actor));
        Assert.Equal("pending", proposal.Status);
        Assert.False(File.Exists(proposal.Plan!.Destination));
    }
}
