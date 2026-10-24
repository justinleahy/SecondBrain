using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Chat;
using SecondBrain.Core.Ingestion;
using SecondBrain.Server.Chat;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class SourceNoteReaderTests
{
    [Fact]
    public async Task LoadsTheWholeNoteWithItsTitleAndTags()
    {
        await using var factory = new LaneDWebFactory();
        var actor = await SessionAsync(factory);
        var documents = factory.Services.GetRequiredService<IDocumentRepository>();
        var captured = await documents.CaptureAsync(new("# First section\n\nOpening text.\n\n# Last section\n\nThe final paragraph is here.", "Full note", Tags: ["project"]), Authority(actor));
        var note = await SourceNoteReader.LoadAsync(captured.Document.Id, actor,
            factory.Services.GetRequiredService<IAuthRepository>(), documents);
        Assert.Equal("Full note", note.Title);
        Assert.Contains("Opening text.", note.Text);
        Assert.Contains("The final paragraph is here.", note.Text);
        Assert.Contains("project", note.Tags!);
    }

    [Fact]
    public async Task LoadsTheCurrentRevisionForComparisonWithTheCitation()
    {
        await using var factory = new LaneDWebFactory();
        var actor = await SessionAsync(factory);
        var documents = factory.Services.GetRequiredService<IDocumentRepository>();
        var captured = await documents.CaptureAsync(new("Original", "Note"), Authority(actor));
        await documents.MutateAsync(captured.Document.Id, 1, "update", JsonSerializer.SerializeToElement(new { content = "Updated full note" }), Authority(actor));
        var current = await SourceNoteReader.LoadAsync(captured.Document.Id, actor,
            factory.Services.GetRequiredService<IAuthRepository>(), documents);
        Assert.Equal(2, current.Revision);
        Assert.Contains("Updated full note", current.Text);
    }

    [Fact]
    public async Task StoredReadPermissionWinsOverCallerClaims()
    {
        await using var factory = new LaneDWebFactory();
        var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Write });
        var actor = new AuthenticatedCredential(key.Record.Id, "api_key", key.Record.Generation, key.Record.AccountEpoch, new HashSet<Scope> { Scope.Read, Scope.Write }, null);
        var documents = factory.Services.GetRequiredService<IDocumentRepository>();
        var note = await documents.CaptureAsync(new("Private note", "Note"), Authority(actor));
        await Assert.ThrowsAsync<AuthorityChangedException>(() => SourceNoteReader.LoadAsync(note.Document.Id, actor,
            factory.Services.GetRequiredService<IAuthRepository>(), documents));
    }

    [Fact]
    public async Task RevokedSessionsCannotOpenNotes()
    {
        await using var factory = new LaneDWebFactory();
        var actor = await SessionAsync(factory);
        var auth = factory.Services.GetRequiredService<IAuthRepository>();
        var documents = factory.Services.GetRequiredService<IDocumentRepository>();
        var note = await documents.CaptureAsync(new("Private note", "Note"), Authority(actor));
        await auth.RevokeAsync(actor.Id);
        await Assert.ThrowsAsync<AuthorityChangedException>(() => SourceNoteReader.LoadAsync(note.Document.Id, actor, auth, documents));
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("purge")]
    public async Task UnavailableNotesDoNotExposeRetainedText(string operation)
    {
        await using var factory = new LaneDWebFactory();
        var actor = await SessionAsync(factory);
        var documents = factory.Services.GetRequiredService<IDocumentRepository>();
        var note = await documents.CaptureAsync(new("Removed content", "Note"), Authority(actor));
        await documents.MutateAsync(note.Document.Id, 1, operation, JsonSerializer.SerializeToElement(new { }), Authority(actor));
        await Assert.ThrowsAsync<ChatNotFoundException>(() => SourceNoteReader.LoadAsync(note.Document.Id, actor,
            factory.Services.GetRequiredService<IAuthRepository>(), documents));
    }

    private static IngestionAuthority Authority(AuthenticatedCredential actor) => new(actor.Id, actor.Generation, actor.AccountEpoch, "reader fixture", actor.Kind);

    private static async Task<AuthenticatedCredential> SessionAsync(LaneDWebFactory factory)
    {
        var auth = factory.Services.GetRequiredService<IAuthRepository>();
        var created = factory.Services.GetRequiredService<CredentialFactory>().CreateSession("reader fixture", false, await auth.GetEpochAsync(), 1, 1);
        await auth.AddAsync(created.Record);
        return new(created.Record.Id, "session", created.Record.Generation, created.Record.AccountEpoch, created.Record.GrantedScopes, null);
    }
}
