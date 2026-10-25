using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using SecondBrain.Core.Sources;

namespace SecondBrain.Infrastructure.Network;

/// <summary>Stream decoders can return a prefix at EOF. A completed snapshot also requires its compression envelope.</summary>
internal sealed class CalendarResponseIntegrity(string? encoding)
{
    private readonly byte[] tail = new byte[8];
    private long encodedBytes, decodedBytes;
    private uint crc = uint.MaxValue, adlerA = 1, adlerB;
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(value =>
    {
        var item = (uint)value;
        for (var i = 0; i < 8; i++) item = (item & 1) == 0 ? item >> 1 : (item >> 1) ^ 0xedb88320U;
        return item;
    }).ToArray();

    internal void Encoded(ReadOnlyMemory<byte> bytes)
    {
        foreach (var value in bytes.Span) tail[encodedBytes++ % 8] = value;
    }
    internal void Decoded(ReadOnlyMemory<byte> bytes)
    {
        decodedBytes += bytes.Length;
        if (encoding == "gzip") foreach (var value in bytes.Span) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        else if (encoding == "deflate") foreach (var value in bytes.Span) { adlerA = (adlerA + value) % 65521; adlerB = (adlerB + adlerA) % 65521; }
    }
    internal void Verify()
    {
        if (encoding is not ("gzip" or "deflate")) return;
        Span<byte> end = stackalloc byte[8];
        for (var i = 0; i < 8; i++) end[i] = tail[(encodedBytes + i) % 8];
        if (encoding == "gzip" && (encodedBytes < 18 || BinaryPrimitives.ReadUInt32LittleEndian(end) != ~crc || BinaryPrimitives.ReadUInt32LittleEndian(end[4..]) != decodedBytes) ||
            encoding == "deflate" && (encodedBytes < 6 || BinaryPrimitives.ReadUInt32BigEndian(end[4..]) != ((adlerB << 16) | adlerA)))
            throw new CalendarSubscriptionException("calendar_compression_incomplete");
    }
}

internal sealed class CompleteBrotliStream(Stream input) : Stream
{
    private BrotliDecoder decoder;
    private readonly byte[] encoded = new byte[64 * 1024];
    private int offset, count;
    private bool finished, disposed;
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        if (buffer.Length == 0) return 0;
        while (true)
        {
            if (finished)
            {
                if (count != 0 || await input.ReadAsync(encoded, token) != 0) throw new CalendarSubscriptionException("calendar_compression_trailing_bytes");
                return 0;
            }
            var status = decoder.Decompress(encoded.AsSpan(offset, count), buffer.Span, out var consumed, out var written);
            offset += consumed; count -= consumed;
            if (status == OperationStatus.InvalidData) throw new CalendarSubscriptionException("calendar_compression_incomplete");
            if (status == OperationStatus.Done) finished = true;
            if (written != 0) return written;
            if (finished) continue;
            if (status != OperationStatus.NeedMoreData || count == encoded.Length) throw new CalendarSubscriptionException("calendar_compression_incomplete");
            if (count > 0) Buffer.BlockCopy(encoded, offset, encoded, 0, count);
            offset = 0;
            var read = await input.ReadAsync(encoded.AsMemory(count), token);
            if (read == 0) throw new CalendarSubscriptionException("calendar_compression_incomplete");
            count += read;
        }
    }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    { if (disposing && !disposed) { disposed = true; decoder.Dispose(); input.Dispose(); } base.Dispose(disposing); }
    public override ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
