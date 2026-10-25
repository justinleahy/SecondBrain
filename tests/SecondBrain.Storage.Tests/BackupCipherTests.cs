using System.Buffers.Binary;
using System.Security.Cryptography;
using SecondBrain.Storage.Backup;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class BackupCipherTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1024 * 1024)]
    [InlineData(3 * 1024 * 1024 + 19)]
    public async Task RoundTripsFragmentedNonseekableStreams(int length)
    {
        var plaintext = RandomNumberGenerator.GetBytes(length);
        using var encrypted = new MemoryStream();
        await BackupCipher.EncryptAsync(new FragmentedStream(new MemoryStream(plaintext)), encrypted, Key);
        Assert.True(encrypted.CanWrite);
        using var recovered = new MemoryStream();
        await BackupCipher.DecryptAsync(new FragmentedStream(new MemoryStream(encrypted.ToArray())), recovered, Key);
        Assert.Equal(plaintext, recovered.ToArray());
        Assert.True(recovered.CanWrite);
    }

    [Fact]
    public async Task EveryArchiveHasAFreshWrappedDataKeyAndIdentifier()
    {
        var a = await Encrypt([1, 2, 3]); var b = await Encrypt([1, 2, 3]);
        Assert.False(a.SequenceEqual(b));
        Assert.Equal("SBKB0001"u8.ToArray(), a[..8]);
        Assert.False(a[8..24].SequenceEqual(b[8..24]));
        Assert.False(a[36..68].SequenceEqual(b[36..68]));
        Assert.Equal(new byte[] { 1, 2, 3 }, await Decrypt(a));
        Assert.Equal(new byte[] { 1, 2, 3 }, await Decrypt(b));
    }

    [Theory]
    [InlineData(0)] // format version
    [InlineData(8)] // archive id
    [InlineData(24)] // wrapping nonce
    [InlineData(36)] // wrapped key
    [InlineData(68)] // wrapping tag
    [InlineData(84)] // frame length
    [InlineData(88)] // encrypted payload
    [InlineData(91)] // payload tag
    [InlineData(110)] // completion frame length
    [InlineData(111)] // completion authentication tag
    public async Task RejectsTamperingAcrossHeaderPayloadAndCompletion(int offset)
    {
        var archive = await Encrypt([1, 2, 3]);archive[offset] ^= 1;
        await Assert.ThrowsAsync<InvalidDataException>(() => Decrypt(archive));
    }

    [Fact]
    public async Task RejectsWrongKeyAndDoesNotEmitUnauthenticatedFrameContent()
    {
        var archive = await Encrypt([1, 2, 3]);using var output = new MemoryStream();
        var wrongKey = RandomNumberGenerator.GetBytes(32);
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupCipher.DecryptAsync(new MemoryStream(archive), output, wrongKey));
        Assert.Empty(output.ToArray());
        archive[88] ^= 1;
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupCipher.DecryptAsync(new MemoryStream(archive), output, Key));
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public async Task RejectsTruncationIncludingRemovalOfAWholeFinalFrameAndTrailingBytes()
    {
        var archive = await Encrypt([1, 2, 3]);
        foreach (var length in new[] { 0, 7, 83, 84, 88, archive.Length - 20, archive.Length - 1 })
            await Assert.ThrowsAsync<InvalidDataException>(() => Decrypt(archive[..length]));
        await Assert.ThrowsAsync<InvalidDataException>(() => Decrypt([.. archive, 0]));
    }

    [Fact]
    public async Task RejectsReorderedRepeatedRemovedAndCrossArchiveFrames()
    {
        var archive = await Encrypt(RandomNumberGenerator.GetBytes(2 * BackupCipher.SegmentBytes));
        var frameLength = 4 + BackupCipher.SegmentBytes + 16;
        var first = archive[84..(84 + frameLength)]; var second = archive[(84 + frameLength)..(84 + 2 * frameLength)]; var end = archive[(84 + 2 * frameLength)..];
        await Assert.ThrowsAsync<InvalidDataException>(() => Decrypt([.. archive[..84], .. second, .. first, .. end]));
        await Assert.ThrowsAsync<InvalidDataException>(() => Decrypt([.. archive[..84], .. first, .. first, .. end]));
        await Assert.ThrowsAsync<InvalidDataException>(() => Decrypt([.. archive[..84], .. first, .. end]));
        var other = await Encrypt(RandomNumberGenerator.GetBytes(BackupCipher.SegmentBytes));
        await Assert.ThrowsAsync<InvalidDataException>(() => Decrypt([.. archive[..84], .. other[84..(84 + frameLength)], .. second, .. end]));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1024 * 1024 + 1)]
    [InlineData(int.MaxValue)]
    public async Task RejectsInvalidLengthsBeforeAllocatingFromArchiveInput(int length)
    {
        var archive = await Encrypt([1, 2, 3]);BinaryPrimitives.WriteInt32BigEndian(archive.AsSpan(84, 4), length);
        await Assert.ThrowsAsync<InvalidDataException>(() => Decrypt(archive));
    }

    [Fact]
    public async Task StreamsManySegmentsWithOneMiBMaximumReadRequestsAndBoundedWorkingBuffers()
    {
        using var plaintext = new GeneratedStream(32L * BackupCipher.SegmentBytes + 7);
        var path = Path.Combine(Path.GetTempPath(), "sb-cipher-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archive = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            await BackupCipher.EncryptAsync(plaintext, archive, Key);
            Assert.InRange(plaintext.LargestRead, 1, BackupCipher.SegmentBytes);
            archive.Position = 0;using var sink = new CountingSink();
            await BackupCipher.DecryptAsync(archive, sink, Key);
            Assert.Equal(plaintext.Total, sink.Count);
            Assert.Equal(BackupCipher.SegmentBytes, sink.LargestWrite);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationCannotCreateAnApparentlyCompleteArchive(bool afterFirstFrame)
    {
        using var plaintext = new GeneratedStream(2L * BackupCipher.SegmentBytes);
        using var cancel = new CancellationTokenSource();
        using var partial = new CancellingOutput(cancel, afterFirstFrame);
        if (!afterFirstFrame) cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BackupCipher.EncryptAsync(plaintext,partial,Key,cancel.Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => Decrypt(partial.ToArray()));
        if (afterFirstFrame) Assert.True(partial.Length > BackupCipher.SegmentBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    public async Task RequiresA256BitWrappingKey(int keyBytes)
    {
        using var input = new MemoryStream(); using var output = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentException>(() => BackupCipher.EncryptAsync(input, output, new byte[keyBytes]));
        await Assert.ThrowsAsync<ArgumentException>(() => BackupCipher.DecryptAsync(input, output, new byte[keyBytes]));
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public async Task IncrementalWriterSupportsFlushedRecordsAndExplicitCompletion()
    {
        using var output = new MemoryStream();
        await using (var writer = await BackupCipher.OpenEncryptionAsync(output, Key))
        {
            await writer.WriteAsync("record one"u8.ToArray()); await writer.FlushAsync();
            await writer.WriteAsync(" and record two"u8.ToArray()); await writer.CompleteAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync(new byte[]{1}).AsTask());
        }
        Assert.Equal("record one and record two", System.Text.Encoding.UTF8.GetString(await Decrypt(output.ToArray())));
        Assert.True(output.CanWrite);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposingAnUnfinishedWriterNeverCompletesAPartialPackage(bool flush)
    {
        using var output = new MemoryStream();
        await using (var writer = await BackupCipher.OpenEncryptionAsync(output, Key))
        { await writer.WriteAsync(new byte[]{1,2,3}); if(flush) await writer.FlushAsync(); }
        await Assert.ThrowsAsync<InvalidDataException>(() => Decrypt(output.ToArray()));
    }

    private static async Task<byte[]> Encrypt(byte[] bytes) {using var output=new MemoryStream();await BackupCipher.EncryptAsync(new MemoryStream(bytes),output,Key);return output.ToArray();}
    private static async Task<byte[]> Decrypt(byte[] bytes) {using var output=new MemoryStream();await BackupCipher.DecryptAsync(new MemoryStream(bytes),output,Key);return output.ToArray();}
    private abstract class NonseekableStream : Stream
    {
        public override bool CanSeek => false; public override long Length => throw new NotSupportedException(); public override long Position {get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException(); public override void SetLength(long value)=>throw new NotSupportedException(); public override void Flush(){} public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException(); public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
    private sealed class FragmentedStream(Stream inner) : NonseekableStream
    {
        public override bool CanRead=>true; public override bool CanWrite=>false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)=>inner.ReadAsync(buffer[..Math.Min(buffer.Length, 7919)],token);
        protected override void Dispose(bool disposing){if(disposing)inner.Dispose();base.Dispose(disposing);}
    }
    private sealed class GeneratedStream(long length) : NonseekableStream
    {
        private long remaining=length; public long Total{get;}=length; public int LargestRead {get;private set;}
        public override bool CanRead=>true;public override bool CanWrite=>false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
        {token.ThrowIfCancellationRequested();LargestRead=Math.Max(LargestRead,buffer.Length);var count=(int)Math.Min(buffer.Length,remaining);buffer.Span[..count].Fill(37);remaining-=count;return ValueTask.FromResult(count);}
    }
    private sealed class CancellingOutput(CancellationTokenSource cancel, bool afterFrame) : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            await base.WriteAsync(buffer, token);
            if (afterFrame && Length >= 84 + 4 + BackupCipher.SegmentBytes + 16) cancel.Cancel();
        }
    }
    private sealed class CountingSink : NonseekableStream
    {
        public long Count{get;private set;}public int LargestWrite{get;private set;}
        public override bool CanRead=>false;public override bool CanWrite=>true;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken token=default)
        {token.ThrowIfCancellationRequested();Assert.True(buffer.Span.IndexOfAnyExcept((byte)37)<0);Count+=buffer.Length;LargestWrite=Math.Max(LargestWrite,buffer.Length);return ValueTask.CompletedTask;}
    }
}
