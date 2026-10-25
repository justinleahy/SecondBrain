using System.Security.Cryptography;

namespace SecondBrain.Storage.Backup;

public static partial class BackupCipher
{
    /// <summary>The caller must explicitly complete a successful package. Disposing an unfinished writer never authenticates a partial package.</summary>
    public static async Task<EncryptionStream> OpenEncryptionAsync(Stream archive, ReadOnlyMemory<byte> backupKey, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (!archive.CanWrite) throw new ArgumentException("The backup destination must be writable.", nameof(archive));
        if (backupKey.Length != KeyBytes) throw new ArgumentException("Backup keys must contain exactly 32 random bytes.", nameof(backupKey));
        var writer = new EncryptionStream(archive, backupKey.Span);
        try { await archive.WriteAsync(writer.header, token).ConfigureAwait(false); return writer; }
        catch { await writer.DisposeAsync(); throw; }
    }

    public sealed class EncryptionStream : Stream
    {
        private readonly Stream destination;
        private readonly AesGcm cipher;
        internal readonly byte[] header = new byte[HeaderBytes];
        private readonly byte[] input = new byte[SegmentBytes];
        private readonly byte[] output = new byte[SegmentBytes];
        private readonly byte[] nonce = new byte[12];
        private readonly byte[] associated = new byte[44];
        private readonly byte[] frame = new byte[4];
        private readonly byte[] tag = new byte[TagBytes];
        private int buffered;
        private ulong index;
        private bool completed, faulted;
        private int disposed;

        internal EncryptionStream(Stream destination, ReadOnlySpan<byte> key)
        {
            this.destination = destination;
            var dataKey = RandomNumberGenerator.GetBytes(KeyBytes);
            try
            {
                Magic.CopyTo(header); RandomNumberGenerator.Fill(header.AsSpan(8, 16)); RandomNumberGenerator.Fill(header.AsSpan(24, 12));
                using (var wrapper = new AesGcm(key, TagBytes))
                    wrapper.Encrypt(header.AsSpan(24, 12), dataKey, header.AsSpan(36, KeyBytes), header.AsSpan(68, TagBytes), header.AsSpan(0, 24));
                SHA256.HashData(header, associated.AsSpan(0, 32));
                cipher = new AesGcm(dataKey, TagBytes);
            }
            finally { CryptographicOperations.ZeroMemory(dataKey); }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            RequireWritable();
            try
            {
                while (!buffer.IsEmpty)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = Math.Min(buffer.Length, input.Length - buffered);
                    buffer[..count].CopyTo(input.AsMemory(buffered)); buffered += count; buffer = buffer[count..];
                    if (buffered == input.Length) await EmitAsync(buffered, cancellationToken).ConfigureAwait(false);
                }
            }
            catch { faulted = true; throw; }
        }

        public async Task CompleteAsync(CancellationToken token = default)
        {
            RequireWritable();
            try
            {
                if (buffered > 0) await EmitAsync(buffered, token).ConfigureAwait(false);
                await EmitAsync(0, token).ConfigureAwait(false);
                await destination.FlushAsync(token).ConfigureAwait(false);
                completed = true;
            }
            catch { faulted = true; throw; }
        }

        private async Task EmitAsync(int length, CancellationToken token)
        {
            Frame(index, length, nonce, associated, frame);
            cipher.Encrypt(nonce, input.AsSpan(0, length), output.AsSpan(0, length), tag, associated);
            await destination.WriteAsync(frame, token).ConfigureAwait(false);
            if (length != 0) await destination.WriteAsync(output.AsMemory(0, length), token).ConfigureAwait(false);
            await destination.WriteAsync(tag, token).ConfigureAwait(false);
            buffered = 0;
            if (length != 0) index = checked(index + 1);
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            RequireWritable();
            try
            {
                if (buffered > 0) await EmitAsync(buffered, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch { faulted = true; throw; }
        }

        private void RequireWritable()
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (completed || faulted) throw new InvalidOperationException("This backup writer is complete or failed.");
        }
        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                cipher.Dispose(); CryptographicOperations.ZeroMemory(input); CryptographicOperations.ZeroMemory(output);
            }
            base.Dispose(disposing);
        }
        public override ValueTask DisposeAsync() { Dispose(true); GC.SuppressFinalize(this); return ValueTask.CompletedTask; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => disposed == 0 && !completed && !faulted;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
