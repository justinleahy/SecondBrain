using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SecondBrain.Extractor;
using SecondBrain.Infrastructure.Extraction;
using Xunit;
using SecondBrain.Extraction.Contracts;
using SecondBrain.Extractor.Wire;

namespace SecondBrain.Extractor.Tests;

public sealed class ExtractionParserTests
{
    [Fact]
    public void MarkdownRetainsLosslessTextAndUntrustedIdentityDoesNotBecomeMetadata()
    {
        const string text = "---\ntitle: My note\ntype: note\nid: forged\nsource: forged\nprops:\n  author: Ada\ntags: [coding, notes]\n---\n# Heading\n[[Raw Target]] and [site](https://example.test)\n";
        var result = Parse(Encoding.UTF8.GetBytes(text), ".md");
        Assert.Equal("extracted", result.Status); Assert.Equal(text, result.LosslessText);
        Assert.Equal("My note", result.Title); Assert.Equal("note", result.DeclaredType);
        Assert.Equal(["coding", "notes"], result.Tags); Assert.Equal(["Raw Target", "https://example.test"], result.Links);
        Assert.Equal("Ada", result.Properties!["author"].GetString()); Assert.False(result.Properties.ContainsKey("id"));
    }

    [Fact]
    public void AliasesAreDistinctFromTagsAndDeclaredTimesArePreserved()
    {
        var result = Parse(Encoding.UTF8.GetBytes("---\ntags: [science]\naliases: [Observatory]\ncreated: 2026-01-02T10:00:00-05:00\nupdated: 2026-02-03\npublished_at: 2025-12-01\n---\nbody"), ".md");
        Assert.Equal(["science"], result.Tags); Assert.Equal(["Observatory"], result.Aliases);
        Assert.Equal("2026-01-02T10:00:00-05:00", result.DeclaredCreatedAt); Assert.Equal("2026-02-03", result.DeclaredUpdatedAt);
        Assert.Equal("2025-12-01", result.Properties!["published_at"].GetString());
    }

    [Theory]
    [InlineData("---\na: &anchor [1, 2]\nb: *anchor\n---\nbody", "frontmatter_aliases")]
    [InlineData("---\na: [[[[[[[[[x]]]]]]]]]\n---\nbody", "frontmatter_depth")]
    public void FrontmatterBoundsAreNamed(string text, string bound) => Assert.Equal(bound, Parse(Encoding.UTF8.GetBytes(text), ".md").Bound);

    [Fact]
    public void HtmlPhysicalBytesOverrideMisleadingHintAndActiveContentIsExcluded()
    {
        var result = Parse(Encoding.UTF8.GetBytes("<!doctype html><html><head><title>A title</title></head><body><p>Visible &amp; safe</p><script>secretScript</script><img src='https://unreachable.test/x'></body></html>"), ".pdf");
        Assert.Equal("html", result.PhysicalFormat); Assert.Contains("Visible & safe", result.LosslessText); Assert.DoesNotContain("secretScript", result.LosslessText); Assert.Equal("A title", result.Title);
    }

    [Fact]
    public void DocxUsesDocumentXmlWithoutResolvingRelationships()
    {
        var result = Parse(Docx("<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:p><w:r><w:t>Hello DOCX</w:t></w:r></w:p></w:body></w:document>"), ".txt");
        Assert.Equal("docx", result.PhysicalFormat); Assert.Equal("Hello DOCX\n", result.LosslessText);
    }

    [Fact]
    public void DocxRejectsEntityDeclarations() => Assert.Equal("failed", Parse(Docx("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///etc/passwd'>]><x>&e;</x>"), ".docx").Status);

    [Fact]
    public void DocxCompressionBombRejectsBeforeXmlParsing() => Assert.Equal("compression_ratio", Parse(Docx("<x>" + new string('x', 1000000) + "</x>"), ".docx").Bound);

    [Fact]
    public void MalformedPdfReturnsHonestError() => Assert.Equal("malformed_document", Parse("%PDF-1.7\nbroken"u8.ToArray(), ".txt").Error);

    [Fact]
    public async Task DescriptorClientStreamsMoreThanOneFrameFromRealWorker()
    {
        var socket = Path.Combine(Path.GetTempPath(), "sbext-" + Guid.NewGuid().ToString("N"));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = new ExtractorSocketServer(new() { SocketPath = socket }, NullLogger<ExtractorSocketServer>.Instance);
        var running = server.RunAsync(stop.Token);
        var path = Path.GetTempFileName();
        try
        {
            var text = new string('a', 200000) + "\nfin";
            await File.WriteAllTextAsync(path, text, stop.Token);
            await using var file = File.OpenRead(path); File.Delete(path);
            var result = await new DescriptorExtractorClient(socket).ExtractAsync(file, ".txt", stop.Token);
            Assert.Equal("extracted", result.Status); Assert.Equal(text, result.LosslessText);
        }
        finally { File.Delete(path); stop.Cancel(); await running; }
    }

    [Fact]
    public void PdfTextAndPageOffsetsComeFromPhysicalPdf()
    {
        var result = Parse(Pdf("BT /F1 12 Tf 72 700 Td (Text layer fixture) Tj ET"), ".md");
        Assert.Equal("extracted", result.Status); Assert.Equal("pdf", result.PhysicalFormat);
        Assert.Contains("Text layer fixture", result.LosslessText);
        var page = Assert.Single(result.Pages!);
        Assert.Equal("Text layer fixture", result.LosslessText.Substring(page.Start, page.Length));
    }

    [Fact]
    public void PdfWithoutTextDeclaresNeedsOcr() => Assert.Equal("needs_ocr", Parse(Pdf(""), ".pdf").Status);

    [Fact]
    public void PdfPageEnumerationIsBounded()
    {
        var result = Parse(Pdf("", pages: 2001), ".pdf");
        Assert.Equal("skipped", result.Status); Assert.Equal("pdf_pages", result.Bound);
    }

    [Fact]
    public void PdfFlateInflationIsBoundedBeforeDecodedAllocation()
    {
        using var compressed = new MemoryStream();
        using (var encoding = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) encoding.Write(Encoding.ASCII.GetBytes(new string(' ', 1000000)));
        var result = Parse(Pdf("", encoded: compressed.ToArray()), ".pdf");
        Assert.Equal("skipped", result.Status); Assert.Equal("bound_exceeded", result.Error); Assert.Equal("pdf_decoded_bytes", result.Bound);
    }

    private static byte[] Pdf(string content, int pages = 1, byte[]? encoded = null)
    {
        var fontId = pages + 3; var streamId = pages + 4;
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(3,pages).Select(page => page + " 0 R"))}] /Count {pages} >>" };
        for (var page = 0; page < pages; page++) objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {fontId} 0 R >> >> /Contents {streamId} 0 R >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var stream = encoded is null ? content : Encoding.Latin1.GetString(encoded);
        objects.Add($"<< /Length {stream.Length} {(encoded is null ? "" : "/Filter /FlateDecode")} >>\nstream\n{stream}\nendstream");
        var text = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++) { offsets.Add(text.Length); text.Append($"{i+1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = text.Length; text.Append($"xref\n0 {objects.Count+1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Count+1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(text.ToString());
    }

    private static ExtractionResult Parse(byte[] bytes, string hint)
    {
        var path = Path.GetTempFileName(); File.WriteAllBytes(path, bytes);
        try { using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read); File.Delete(path); return DocumentParser.Extract(handle, hint, CancellationToken.None); }
        finally { File.Delete(path); }
    }
    private static byte[] Docx(string xml)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        { using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open()); writer.Write(xml); }
        return bytes.ToArray();
    }
}
