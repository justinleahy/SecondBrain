using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Ingestion;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class DocumentMetadataTests
{
    [Fact]
    public async Task PdfPagesMatchCurrentTextAfterClassificationAndMetadataOnlyRevision()
    {
        await using var factory = new LaneDWebFactory();
        var auth = factory.Services.GetRequiredService<IAuthRepository>();
        var session = factory.Services.GetRequiredService<CredentialFactory>().CreateSession("PDF metadata", false, await auth.GetEpochAsync(), 1, 1);
        await auth.AddAsync(session.Record);
        var actor = new IngestionAuthority(session.Record.Id, session.Record.Generation, session.Record.AccountEpoch, "PDF metadata", "session");
        var documents = factory.Services.GetRequiredService<IDocumentRepository>();
        var pdf = await documents.AdmitFileAsync(PdfFixture.TwoPages(), new("", "Page fixture"), "pages.pdf", actor);
        var processing = factory.Services.GetRequiredService<SecondBrain.Server.Ingestion.FileProcessingOrchestrator>();
        processing.Notify();
        pdf = await processing.WaitAsync(pdf, 15, CancellationToken.None);
        Assert.Equal("article", pdf.Document.Type);
        var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read });
        using var client = factory.CreatePrivateClient(false);
        client.DefaultRequestHeaders.Authorization = new("Bearer", key.Plaintext);
        foreach (var revision in new long[] { 1, 2 })
        {
            var current = (await documents.GetAsync(pdf.Document.Id, true))!;
            var details = (await client.GetFromJsonAsync<DocumentDetails>("/documents/" + pdf.Document.Id + "/metadata?revision=" + revision))!;
            Assert.Equal(2, details.Pages.Count);
            Assert.Contains("amber", current.Text!.Substring(details.Pages[0].Start, details.Pages[0].Length));
            Assert.Contains("sapphire", current.Text.Substring(details.Pages[1].Start, details.Pages[1].Length));
            if (revision == 1) await documents.MutateAsync(pdf.Document.Id, 1, "update", JsonSerializer.SerializeToElement(new { title = "Renamed PDF", type = "file" }), actor);
        }
    }

    [Fact]
    public async Task MetadataTracksAliasesLinksOriginsAndRevisionHistoryWithoutServingOldText()
    {
        await using var factory = new LaneDWebFactory();
        var auth = factory.Services.GetRequiredService<IAuthRepository>();
        var session = factory.Services.GetRequiredService<CredentialFactory>().CreateSession("metadata fixture", false, await auth.GetEpochAsync(), 1, 1);
        await auth.AddAsync(session.Record);
        var actor = new IngestionAuthority(session.Record.Id, session.Record.Generation, session.Record.AccountEpoch, "metadata fixture", "session");
        var documents = factory.Services.GetRequiredService<IDocumentRepository>();
        var target = await documents.CaptureAsync(new("Target text", "Link target"), actor);
        var note = await documents.CaptureAsync(new("Original [[" + target.Document.Id + "]]", "Metadata note", Tags: ["initial"], SourceUrl: "https://example.test/original", Aliases: ["First alias"]), actor);
        var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read });
        using var client = factory.CreatePrivateClient(false);
        client.DefaultRequestHeaders.Authorization = new("Bearer", key.Plaintext);
        var metadata = (await client.GetFromJsonAsync<DocumentDetails>("/documents/" + note.Document.Id + "/metadata?revision=1"))!;
        Assert.Equal("First alias", Assert.Single(metadata.Aliases));
        Assert.Equal("capture", Assert.Single(metadata.Tags).Origin);
        Assert.Equal(target.Document.Id, Assert.Single(metadata.Links).DocumentId);
        await Assert.ThrowsAsync<ArgumentException>(() => documents.MutateAsync(note.Document.Id, 1, "update", JsonSerializer.SerializeToElement(new { aliases = "invalid" }), actor).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => documents.MutateAsync(note.Document.Id, 1, "update", JsonSerializer.SerializeToElement(new { source_url = 123 }), actor).AsTask());
        await documents.MutateAsync(note.Document.Id, 1, "update", JsonSerializer.SerializeToElement(new
        { content = "Updated [[" + target.Document.Id + "]]", tags = new[] { "updated" }, aliases = new[] { "Second alias" }, source_url = "https://example.test/updated" }), actor);
        metadata = (await client.GetFromJsonAsync<DocumentDetails>("/documents/" + note.Document.Id + "/metadata?revision=2"))!;
        Assert.Equal("Second alias", Assert.Single(metadata.Aliases));
        Assert.Equal("user", Assert.Single(metadata.Tags).Origin);
        Assert.Equal("https://example.test/updated", metadata.SourceUrl);
        var first = (await client.GetFromJsonAsync<CatalogPage<DocumentRevisionEntry>>("/documents/" + note.Document.Id + "/revisions?limit=1"))!;
        Assert.Equal(1, Assert.Single(first.Items).Revision); Assert.NotNull(first.NextCursor);
        var second = (await client.GetFromJsonAsync<CatalogPage<DocumentRevisionEntry>>("/documents/" + note.Document.Id + "/revisions?cursor=" + Uri.EscapeDataString(first.NextCursor)))!;
        Assert.Equal(2, Assert.Single(second.Items).Revision);
        using var wrongDocument = await client.GetAsync("/documents/" + target.Document.Id + "/revisions?cursor=" + Uri.EscapeDataString(first.NextCursor));
        Assert.Equal(HttpStatusCode.BadRequest, wrongDocument.StatusCode);
        using var stale = await client.GetAsync("/documents/" + note.Document.Id + "/metadata?revision=1");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        await documents.MutateAsync(note.Document.Id, 2, "delete", JsonSerializer.SerializeToElement(new { }), actor);
        using var unavailable = await client.GetAsync("/documents/" + note.Document.Id + "/metadata");
        using var unavailableHistory = await client.GetAsync("/documents/" + note.Document.Id + "/revisions");
        Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode); Assert.Equal(HttpStatusCode.NotFound, unavailableHistory.StatusCode);
    }

    [Fact]
    public async Task CompletionReturnsLiteralPrefixesAndHidesDeletedDocuments()
    {
        await using var factory = new LaneDWebFactory();
        var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read, Scope.Write });
        var auth = factory.Services.GetRequiredService<IAuthRepository>();
        var session = factory.Services.GetRequiredService<CredentialFactory>().CreateSession("completions", false, await auth.GetEpochAsync(), 1, 1);
        await auth.AddAsync(session.Record);
        var actor = new IngestionAuthority(session.Record.Id, session.Record.Generation, session.Record.AccountEpoch, "completions", "session");
        var documents = factory.Services.GetRequiredService<IDocumentRepository>();
        var note = await documents.CaptureAsync(new("content", "A%literal title", Tags: ["alpha", "a%literal"]), actor);
        var article = await documents.CaptureAsync(new("article", "Authored", "article", JsonSerializer.SerializeToElement(new { author = "Maya Chen" })), actor);
        using var client = factory.CreatePrivateClient(false);
        client.DefaultRequestHeaders.Authorization = new("Bearer", key.Plaintext);
        var links = (await client.GetFromJsonAsync<CatalogCompletion[]>("/browse/complete?kind=link&q=A%25"))!;
        Assert.Equal(note.Document.Id, Assert.Single(links).Value);
        var tags = (await client.GetFromJsonAsync<CatalogCompletion[]>("/browse/complete?kind=tag&q=a%25"))!;
        Assert.Equal("a%literal", Assert.Single(tags).Value);
        var people = (await client.GetFromJsonAsync<CatalogCompletion[]>("/browse/complete?kind=person&q=Maya"))!;
        Assert.Equal("Maya Chen", Assert.Single(people).Label);
        await documents.MutateAsync(note.Document.Id, 1, "delete", JsonSerializer.SerializeToElement(new { }), actor);
        await documents.MutateAsync(article.Document.Id, 1, "delete", JsonSerializer.SerializeToElement(new { }), actor);
        Assert.Empty((await client.GetFromJsonAsync<CatalogCompletion[]>("/browse/complete?kind=link&q=A%25"))!);
        Assert.Empty((await client.GetFromJsonAsync<CatalogCompletion[]>("/browse/complete?kind=tag&q=a%25"))!);
        Assert.Empty((await client.GetFromJsonAsync<CatalogCompletion[]>("/browse/complete?kind=person&q=Maya"))!);
        using var invalid = await client.GetAsync("/browse/complete?kind=invalid");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task StyleNonceIsPerResponseAndDoesNotPermitInlineScripts()
    {
        await using var factory = new LaneDWebFactory();
        using var client = factory.CreatePrivateClient();
        using var first = await client.GetAsync("/login");
        using var second = await client.GetAsync("/login");
        var policy = string.Join(";", first.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("script-src 'self';", policy);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", policy);
        Assert.Contains("style-src-attr 'unsafe-inline'", policy);
        var text = await first.Content.ReadAsStringAsync();
        var nonce = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(text, "name=\"secondbrain-style-nonce\" content=\"([^\"]+)\"").Groups[1].Value);
        Assert.NotEmpty(nonce); Assert.Contains("'nonce-" + nonce + "'", policy);
        Assert.NotEqual(policy, string.Join(";", second.Headers.GetValues("Content-Security-Policy")));
    }
}
