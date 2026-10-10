using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Win32.SafeHandles;
using SecondBrain.Extraction.Contracts;
using SecondBrain.Extractor.Wire;
using UglyToad.PdfPig;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace SecondBrain.Extractor;

/// <summary>All untrusted format interpretation stays in the sandbox.</summary>
public static class DocumentParser
{
    public const int MaximumInputBytes = 50 * 1024 * 1024;
    public const int MaximumTextBytes = 10 * 1024 * 1024;
    public const int MaximumPages = 2000;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static ExtractionResult Extract(SafeFileHandle descriptor, string? hint, CancellationToken cancellationToken)
    {
        UnixDescriptorTransport.RequireReadOnlyRegularFile(descriptor);
        hint = hint?.ToLowerInvariant();
        var length = RandomAccess.GetLength(descriptor);
        if (length > MaximumInputBytes) return Failed("input_bytes");
        var bytes = new byte[checked((int)length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = RandomAccess.Read(descriptor, bytes.AsSpan(offset), offset);
            if (read == 0) return Failed("input_changed");
            offset += read;
        }
        try
        {
            if (bytes.AsSpan().StartsWith("%PDF-"u8)) return Pdf(bytes, cancellationToken);
            if (bytes.AsSpan().StartsWith("PK\x03\x04"u8)) return Docx(bytes, cancellationToken);
            var text = Utf8.GetString(bytes).TrimStart('\ufeff');
            if (text.Contains('\0')) return new("skipped", "unknown", "application/octet-stream", "", Error: "unsupported_format");
            if (Regex.IsMatch(text.AsSpan(0, Math.Min(text.Length, 4096)).ToString(), @"^\s*<(?:!doctype\s+html|html|head|body|article|main|p|div|h[1-6]|table|section)(?:\s|>)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
                return Html(text, cancellationToken);
            var markdown = hint is ".md" or ".markdown" or "md" or "markdown" || text.StartsWith("---\n", StringComparison.Ordinal) || text.StartsWith("---\r\n", StringComparison.Ordinal);
            return Text(text, markdown);
        }
        catch (PdfBoundException exception) { return Failed(exception.Bound); }
        catch (BoundException exception) { return Failed(exception.Bound); }
        catch (Exception exception) when (exception is not OperationCanceledException && exception is not OutOfMemoryException)
        { return new("failed", "unknown", "application/octet-stream", "", Error: "malformed_document"); }
    }

    private static ExtractionResult Failed(string bound) => bound == "input_changed"
        ? new("failed", "unknown", "application/octet-stream", "", Error: "input_changed")
        : new("skipped", "unknown", "application/octet-stream", "", Error: "bound_exceeded", Bound: bound);
    private static void CheckText(string text) { if (Utf8.GetByteCount(text) > MaximumTextBytes) throw new BoundException("decoded_bytes"); }

    private static ExtractionResult Text(string text, bool markdown)
    {
        CheckText(text);
        var props = new Dictionary<string, JsonElement>();
        string? title = null, type = null, created = null, updated = null;
        var tags = new List<string>(); var aliases = new List<string>();
        if (markdown && (text.StartsWith("---\n", StringComparison.Ordinal) || text.StartsWith("---\r\n", StringComparison.Ordinal)))
        {
            var start = text.IndexOf('\n') + 1;
            var end = text.IndexOf("\n---", start, StringComparison.Ordinal);
            if (end < 0 || end - start > 65536) throw new BoundException("frontmatter_bytes");
            var yaml = text[start..end];
            if (Utf8.GetByteCount(yaml) > 65536) throw new BoundException("frontmatter_bytes");
            var parser = new Parser(new StringReader(yaml));
            int nodes = 0, depth = 0;
            while (parser.MoveNext())
            {
                if (++nodes > 1000) throw new BoundException("frontmatter_nodes");
                if (parser.Current is AnchorAlias) throw new BoundException("frontmatter_aliases");
                if (parser.Current is MappingStart or SequenceStart && ++depth > 8) throw new BoundException("frontmatter_depth");
                if (parser.Current is MappingEnd or SequenceEnd) depth--;
            }
            var stream = new YamlStream(); stream.Load(new StringReader(yaml));
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode map) throw new InvalidDataException();
            foreach (var pair in map.Children)
            {
                var key = ((YamlScalarNode)pair.Key).Value;
                if (key == "title") title = Scalar(pair.Value);
                else if (key == "type") type = Scalar(pair.Value);
                else if (key is "created" or "created_at") created = Scalar(pair.Value);
                else if (key is "updated" or "updated_at") updated = Scalar(pair.Value);
                else if (key is "published_at" or "author" or "url") props[key] = JsonSerializer.SerializeToElement(ConvertYaml(pair.Value));
                else if (key == "tags" || key == "aliases")
                {
                    var values = pair.Value is YamlSequenceNode sequence ? sequence.Children.Select(Scalar) : [Scalar(pair.Value)];
                    foreach (var value in values) { if (tags.Count + aliases.Count >= 200) throw new BoundException("frontmatter_tags"); if (value is not null) (key == "tags" ? tags : aliases).Add(value); }
                }
                else if (key == "props" && pair.Value is YamlMappingNode properties)
                    foreach (var property in properties.Children) props[((YamlScalarNode)property.Key).Value ?? ""] = JsonSerializer.SerializeToElement(ConvertYaml(property.Value));
            }
        }
        var links = new List<string>();
        if (markdown)
            foreach (Match match in Regex.Matches(text, @"\[\[([^\]\r\n]+)\]\]|\[[^\]\r\n]*\]\(([^)\r\n]+)\)", RegexOptions.None, TimeSpan.FromSeconds(1)))
            { if (links.Count >= 200) throw new BoundException("links"); links.Add(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value); }
        return new("extracted", markdown ? "markdown" : "text", markdown ? "text/markdown" : "text/plain", text, title, type, props, tags, links, Aliases: aliases, DeclaredCreatedAt: created, DeclaredUpdatedAt: updated);
    }
    private static string? Scalar(YamlNode node) => (node as YamlScalarNode)?.Value;
    private static object? ConvertYaml(YamlNode node) => node switch
    {
        YamlScalarNode scalar => scalar.Value,
        YamlSequenceNode sequence => sequence.Children.Select(ConvertYaml).ToArray(),
        YamlMappingNode mapping => mapping.Children.ToDictionary(pair => Scalar(pair.Key) ?? "", pair => ConvertYaml(pair.Value)),
        _ => throw new InvalidDataException()
    };

    private static ExtractionResult Pdf(byte[] bytes, CancellationToken token)
    {
        using var pdf = PdfDocument.Open(bytes, new ParsingOptions { UseLenientParsing = false, FilterProvider = new BoundedPdfFilters() });
        if (pdf.NumberOfPages > MaximumPages) throw new BoundException("pdf_pages");
        var text = new StringBuilder(); var pages = new List<ExtractionPage>(); var hasText = false;
        for (var page = 1; page <= pdf.NumberOfPages; page++)
        {
            token.ThrowIfCancellationRequested();
            var content = pdf.GetPage(page).Text;
            hasText |= !string.IsNullOrWhiteSpace(content);
            text.Append($"\n<!-- page {page} -->\n"); var start = text.Length;
            text.Append(content); text.Append('\n');
            pages.Add(new(page, start, content.Length)); CheckText(text.ToString());
        }
        return new(hasText ? "extracted" : "needs_ocr", "pdf", "application/pdf", text.ToString(), pdf.Information.Title, Pages: pages);
    }

    private static ExtractionResult Html(string source, CancellationToken token)
    {
        CheckText(source);
        if (source.Count(character => character == '<') > 50000) throw new BoundException("html_nodes");
        using var document = new HtmlParser(new HtmlParserOptions { IsScripting = false }).ParseDocument(source);
        var text = new StringBuilder(); var stack = new Stack<(INode Node, int Depth)>();
        stack.Push((document.Body ?? (INode)document.DocumentElement, 0)); var count = 0;
        while (stack.TryPop(out var item))
        {
            token.ThrowIfCancellationRequested();
            if (++count > 100000) throw new BoundException("html_nodes");
            if (item.Depth > 64) throw new BoundException("html_depth");
            if (item.Node is IElement element && element.LocalName is "script" or "style" or "iframe" or "object" or "template" or "nav") continue;
            if (item.Node.NodeType == NodeType.Text) text.Append(item.Node.TextContent);
            if (item.Node is IElement block && block.LocalName is "p" or "div" or "h1" or "h2" or "h3" or "li" or "br" or "tr") text.Append('\n');
            foreach (var child in item.Node.ChildNodes.Reverse()) stack.Push((child, item.Depth + 1));
        }
        var output = text.ToString(); CheckText(output);
        var links = document.QuerySelectorAll("a[href]").Select(a => a.GetAttribute("href")!).Take(201).ToArray();
        if (links.Length > 200) throw new BoundException("links");
        return new("extracted", "html", "text/html", output, document.Title, Links: links);
    }

    private static ExtractionResult Docx(byte[] bytes, CancellationToken token)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (archive.Entries.Count > 1000) throw new BoundException("archive_members");
        long decoded = 0;
        foreach (var entry in archive.Entries)
        {
            decoded += entry.Length;
            if (decoded > MaximumTextBytes) throw new BoundException("decoded_bytes");
            if (entry.Length > Math.Max(1024, entry.CompressedLength * 100)) throw new BoundException("compression_ratio");
        }
        var document = archive.GetEntry("word/document.xml") ?? throw new InvalidDataException();
        using var input = document.Open();
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumTextBytes });
        var output = new StringBuilder(); int nodes = 0;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (++nodes > 100000) throw new BoundException("xml_nodes");
            if (reader.Depth > 64) throw new BoundException("xml_depth");
            if (reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.SignificantWhitespace) output.Append(reader.Value);
            if (reader.LocalName == "tab" && reader.NodeType == XmlNodeType.Element) output.Append('\t');
            if ((reader.LocalName is "p" or "tr") && reader.NodeType == XmlNodeType.EndElement || reader.LocalName == "br" && reader.NodeType == XmlNodeType.Element) output.Append('\n');
            if (output.Length > MaximumTextBytes) throw new BoundException("decoded_bytes");
        }
        var text = output.ToString(); CheckText(text);
        return new("extracted", "docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", text);
    }
    private sealed class BoundException(string bound) : Exception { public string Bound { get; } = bound; }
}
