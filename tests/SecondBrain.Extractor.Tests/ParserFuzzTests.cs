using System.IO.Compression;
using System.Text;
using SecondBrain.Extraction.Contracts;
using SecondBrain.Extractor;
using Xunit;
using Xunit.Abstractions;

namespace SecondBrain.Extractor.Tests;

/// <summary>Reproducible byte-level mutation corpus at the untrusted parser boundary.</summary>
public sealed class ParserFuzzTests(ITestOutputHelper output)
{
    [Theory]
    [Trait("Category", "Fuzz")]
    [InlineData(".md")]
    [InlineData(".txt")]
    [InlineData(".html")]
    [InlineData(".vtt")]
    [InlineData(".srt")]
    [InlineData(".ics")]
    [InlineData(".pdf")]
    [InlineData(".docx")]
    public void MutatedBytesNeverEscapeTheBoundedResultContract(string hint)
    {
        var seed = int.TryParse(Environment.GetEnvironmentVariable("SECONDBRAIN_FUZZ_SEED"), out var supplied) ? supplied : 0x4d3401;
        var random = new Random(seed);
        output.WriteLine($"Parser corpus: {hint}; seed={seed}; cases=257");
        var original = Seed(hint);
        var path = Path.GetTempFileName();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            for (var iteration = 0; iteration <= 256; iteration++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var bytes = original.ToList();
                if (iteration > 0)
                {
                    for (var change = 0; change < 1 + iteration % 12; change++)
                    {
                        var index = random.Next(bytes.Count + 1);
                        switch (random.Next(4))
                        {
                            case 0: if (index < bytes.Count) bytes[index] = (byte)random.Next(256); break;
                            case 1: bytes.Insert(index, (byte)random.Next(256)); break;
                            case 2: if (index < bytes.Count) bytes.RemoveAt(index); break;
                            case 3: if (index < bytes.Count) bytes.RemoveRange(index, bytes.Count - index); break;
                        }
                    }
                }
                File.WriteAllBytes(path, bytes.ToArray());
                using var descriptor = File.OpenHandle(path, FileMode.Open, FileAccess.Read);
                var window = hint == ".ics" ? new CalendarWindow("2026-10-01T00:00:00Z", "2026-11-01T00:00:00Z", "UTC") : null;
                var result = DocumentParser.Extract(descriptor, hint, deadline.Token, window);
                Assert.Contains(result.Status, new[] { "extracted", "skipped", "failed", "needs_ocr" });
                Assert.True(Encoding.UTF8.GetByteCount(result.LosslessText) <= DocumentParser.MaximumTextBytes);
                if (result.Segments is { } segments) Assert.True(TranscriptSegments.IsValid(result.LosslessText, segments));
                if (result.Calendar is { } calendar) calendar.Validate();
                foreach (var page in result.Pages ?? [])
                {
                    Assert.True(page.Start >= 0 && page.Length >= 0 && page.Start <= result.LosslessText.Length - page.Length);
                }
            }
        }
        finally { File.Delete(path); }
    }

    private static byte[] Seed(string hint)
    {
        if (hint == ".docx")
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            using (var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open()))
                writer.Write("<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:p><w:r><w:t>Bounded fixture</w:t></w:r></w:p></w:body></w:document>");
            return stream.ToArray();
        }
        return Encoding.UTF8.GetBytes(hint switch
        {
            ".md" => "---\ntitle: Fixture\nprops: {author: Example}\ntags: [one, two]\n---\n# Fixture\nUnicode α😀 and [[Target]].",
            ".txt" => "Bounded text with Unicode α😀\nSecond line.",
            ".html" => "<!doctype html><html><head><title>Fixture</title></head><body><p>Text &amp; α😀</p><script>inert()</script></body></html>",
            ".vtt" => "WEBVTT\n\n00:00.000 --> 00:01.000\n<v Example>Unicode α😀</v>\n\n00:01.000 --> 00:02.000\n<v Other>Reply</v>\n",
            ".srt" => "1\n00:00:00,000 --> 00:00:01,000\nUnicode α😀\n\n2\n00:00:01,000 --> 00:00:02,000\nReply\n",
            ".ics" => "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:fixture\r\nDTSTAMP:20261001T000000Z\r\nDTSTART:20261002T120000Z\r\nDTEND:20261002T130000Z\r\nSUMMARY:Fixture\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n",
            ".pdf" => "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n",
            _ => throw new ArgumentException("Unknown fixture format.")
        });
    }
}
