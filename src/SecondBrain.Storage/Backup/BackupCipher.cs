using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SecondBrain.Storage.Backup;

/// <summary>
/// Version 1 backup encryption: a wrapped random AES-256 key and sequential authenticated 1-MiB frames.
/// Streams remain caller-owned. Decryption output must stay in private staging until this method and
/// manifest verification both succeed; an invalid later frame can follow an authenticated prefix.
/// </summary>
public static partial class BackupCipher
{
    public const int SegmentBytes = 1024 * 1024;
    public const int KeyBytes = 32;
    private const int HeaderBytes = 84;
    private const int TagBytes = 16;
    private static ReadOnlySpan<byte> Magic => "SBKB0001"u8;

    public static async Task EncryptAsync(Stream plaintext, Stream archive, ReadOnlyMemory<byte> backupKey, CancellationToken cancellationToken = default)
    {
        Validate(plaintext, archive, backupKey);
        await using var writer = await OpenEncryptionAsync(archive, backupKey, cancellationToken).ConfigureAwait(false);
        await plaintext.CopyToAsync(writer, 65536, cancellationToken).ConfigureAwait(false);
        await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task DecryptAsync(Stream archive, Stream plaintext, ReadOnlyMemory<byte> backupKey, CancellationToken cancellationToken = default)
    {
        Validate(archive, plaintext, backupKey);
        var key = new byte[KeyBytes];
        var input = new byte[SegmentBytes];
        var output = new byte[SegmentBytes];
        var header = new byte[HeaderBytes];
        var frame = new byte[4];
        var tag = new byte[TagBytes];
        var nonce = new byte[12];
        var associated = new byte[44];
        try
        {
            await archive.ReadExactlyAsync(header, cancellationToken);
            if (!header.AsSpan(0, 8).SequenceEqual(Magic)) throw InvalidArchive();
            using (var wrapper = new AesGcm(backupKey.Span, TagBytes))
                wrapper.Decrypt(header.AsSpan(24, 12), header.AsSpan(36, KeyBytes), header.AsSpan(68, TagBytes), key, header.AsSpan(0, 24));
            SHA256.HashData(header, associated.AsSpan(0, 32));
            using var cipher = new AesGcm(key, TagBytes);
            ulong index = 0;
            while (true)
            {
                await archive.ReadExactlyAsync(frame, cancellationToken);
                var length = BinaryPrimitives.ReadInt32BigEndian(frame);
                if (length is < 0 or > SegmentBytes) throw InvalidArchive();
                await archive.ReadExactlyAsync(input.AsMemory(0, length), cancellationToken);
                await archive.ReadExactlyAsync(tag, cancellationToken);
                Frame(index, length, nonce, associated, frame);
                cipher.Decrypt(nonce, input.AsSpan(0, length), tag, output.AsSpan(0, length), associated);
                if (length == 0)
                {
                    if (await archive.ReadAsync(frame.AsMemory(0, 1), cancellationToken) != 0) throw InvalidArchive();
                    break;
                }
                await plaintext.WriteAsync(output.AsMemory(0, length), cancellationToken);
                index = checked(index + 1);
            }
            await plaintext.FlushAsync(cancellationToken);
        }
        catch (Exception error) when (error is CryptographicException or EndOfStreamException or OverflowException)
        {
            throw InvalidArchive();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(output);
        }
    }

    private static void Validate(Stream input, Stream output, ReadOnlyMemory<byte> key)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (ReferenceEquals(input, output)) throw new ArgumentException("Backup input and output must be different streams.");
        if (!input.CanRead || !output.CanWrite) throw new ArgumentException("Backup streams must support reading and writing respectively.");
        if (key.Length != KeyBytes) throw new ArgumentException("Backup keys must contain exactly 32 random bytes.", nameof(key));
    }
    private static void Frame(ulong index, int length, Span<byte> nonce, Span<byte> associated, Span<byte> frame)
    {
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], index);
        BinaryPrimitives.WriteUInt64BigEndian(associated[32..], index);
        BinaryPrimitives.WriteInt32BigEndian(associated[40..], length);
        BinaryPrimitives.WriteInt32BigEndian(frame, length);
    }
    private static InvalidDataException InvalidArchive() => new("The backup is incomplete, corrupted, or cannot be authenticated with this key.");
}
