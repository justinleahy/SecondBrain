using System.Security.Cryptography;
using SecondBrain.Core.Ingestion;

namespace SecondBrain.Core.Operations;

public sealed record BackupInfo(string Name, long Bytes, DateTimeOffset CreatedAt, bool Partial);
public sealed record BackupCreated(BackupInfo Archive, long Files, long CompleteFiles, long PartialFiles)
{
    public BackupRetentionResult? Retention { get; init; }
}
public sealed record BackupRetentionResult(int Deleted, int Retained, bool MoreWork, int Deferred = 0);
public sealed record BackupKeyStatus(bool Ready, string Status);
public sealed class BackupException(string code) : Exception(code) { public string Code { get; } = code; }
public sealed class BackupKey : IDisposable
{
    private readonly byte[] bytes;
    private bool disposed;
    public BackupKey(ReadOnlySpan<byte> value)
    {
        if (value.Length != 32) throw new ArgumentException("A backup key must have 32 bytes.");
        bytes = value.ToArray();
    }
    public ReadOnlyMemory<byte> Bytes { get { ObjectDisposedException.ThrowIf(disposed, this); return bytes; } }
    public void Dispose() { if (!disposed) { disposed = true; CryptographicOperations.ZeroMemory(bytes); } }
}
public interface IBackupKeyProvider { ValueTask<BackupKey> ReadAsync(CancellationToken token = default); }
public interface IBackupOperations
{
    Task<BackupCreated> CreateAsync(IngestionAuthority actor, CancellationToken token = default);
    Task<IReadOnlyList<BackupInfo>> ListAsync(IngestionAuthority actor, CancellationToken token = default);
    Task<Stream> OpenAsync(string name, IngestionAuthority actor, CancellationToken token = default);
    Task<BackupKeyStatus> KeyStatusAsync(IngestionAuthority actor, CancellationToken token = default);
    Task<BackupRetentionResult> RotateAsync(IngestionAuthority? actor = null, CancellationToken token = default);
}
