using System.IO.Compression;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokens;

namespace SecondBrain.Extractor;

/// <summary>Preflight Flate inflation with a streaming counter before PdfPig allocates decoded buffers.</summary>
internal sealed class BoundedPdfFilters : ILookupFilterProvider
{
    private long decodedTotal;
    private IReadOnlyList<IFilter> Wrap(IReadOnlyList<IFilter> filters) => filters.Select(filter => (IFilter)new BoundedFilter(filter, this)).ToArray();
    public IReadOnlyList<IFilter> GetFilters(DictionaryToken dictionary) => Wrap(DefaultFilterProvider.Instance.GetFilters(dictionary));
    public IReadOnlyList<IFilter> GetFilters(DictionaryToken dictionary, UglyToad.PdfPig.Tokenization.Scanner.IPdfTokenScanner scanner) => GetFilters(dictionary);
    public IReadOnlyList<IFilter> GetNamedFilters(IReadOnlyList<NameToken> names) => Wrap(DefaultFilterProvider.Instance.GetNamedFilters(names));
    public IReadOnlyList<IFilter> GetAllFilters() => Wrap(DefaultFilterProvider.Instance.GetAllFilters());

    private sealed class BoundedFilter(IFilter inner, BoundedPdfFilters owner) : IFilter
    {
        public bool IsSupported => inner.IsSupported;
        public Memory<byte> Decode(Memory<byte> input, DictionaryToken dictionary, IFilterProvider provider, int index)
        {
            if (inner is FlateFilter)
            {
                using var compressed = new MemoryStream(input.ToArray());
                using var inflated = new ZLibStream(compressed, CompressionMode.Decompress);
                var buffer = new byte[8192]; long total = 0; int read;
                while ((read = inflated.Read(buffer)) != 0)
                {
                    total += read;
                    if (total > DocumentParser.MaximumTextBytes || total > Math.Max(1024, (long)input.Length * 100))
                        throw new PdfBoundException("pdf_decoded_bytes");
                }
            }
            // LZW can expand without a streamed callback; reject rather than allocate an unbounded buffer.
            else if (inner.GetType().Name.Contains("Lzw", StringComparison.OrdinalIgnoreCase)) throw new PdfBoundException("pdf_unsupported_filter");
            var result = inner.Decode(input, dictionary, provider, index);
            if (result.Length > DocumentParser.MaximumTextBytes || result.Length > Math.Max(1024, (long)input.Length * 100))
                throw new PdfBoundException("pdf_decoded_bytes");
            owner.decodedTotal += result.Length;
            if (owner.decodedTotal > DocumentParser.MaximumTextBytes) throw new PdfBoundException("pdf_decoded_bytes");
            return result;
        }
    }
}
internal sealed class PdfBoundException(string bound) : Exception { public string Bound { get; } = bound; }
