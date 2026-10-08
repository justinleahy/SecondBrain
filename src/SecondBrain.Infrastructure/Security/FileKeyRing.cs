using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using SecondBrain.Core.Security;

namespace SecondBrain.Infrastructure.Security;

/// <summary>Persistent versioned CSPRNG HMAC keys; ASP.NET Data Protection is supplied by the host.</summary>
public sealed class FileKeyRing : IKeyRing, IDisposable
{
    private readonly object gate = new();
    private readonly string directory;
    private readonly string path;
    private readonly SemaphoreSlim rotation = new(1);
    private RingDocument ring;
    private bool disposed;

    public FileKeyRing(string dataRoot, IDataProtectionProvider dataProtectionProvider)
    {
        DataProtectionProvider = dataProtectionProvider;
        directory = Path.Combine(dataRoot, "keyring");
        if (!Directory.Exists(dataRoot)) throw new IOException("Data root must be provisioned before creating the key ring.");
        if (UnixPath.Canonicalize(dataRoot) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)))
            throw new UnauthorizedAccessException("Data root must not traverse symlinks.");
        if (!Directory.Exists(directory))
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        if (new DirectoryInfo(directory).LinkTarget is not null || !OperatingSystem.IsWindows() &&
            (File.GetUnixFileMode(directory) != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute) || RootSecurityValidator.Ownership(directory).Uid != RootSecurityValidator.CurrentUid()))
            throw new UnauthorizedAccessException("Key ring must be daemon-owned with mode 0700 and must not be a symlink.");
        path = Path.Combine(directory, "hmac.json");
        using (DataRootLock.Acquire(directory))
        {
            if (File.Exists(path)) ring = Read();
            else
            {
                var kid = Ulid.NewUlid().ToString();
                ring = new RingDocument(kid, new Dictionary<string, byte[]>(StringComparer.Ordinal) { [kid] = RandomNumberGenerator.GetBytes(32) });
                Persist(ring);
            }
        }
        // Force the first Data Protection key to be persisted during initialization.
        _ = DataProtectionProvider.CreateProtector(KeyRingPurposes.Session).Protect(Array.Empty<byte>());
        if (!OperatingSystem.IsWindows())
            foreach (var file in Directory.EnumerateFiles(directory, "*.xml"))
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public string ActiveKid { get { lock (gate) { ThrowIfDisposed(); return ring.ActiveKid; } } }
    public IDataProtectionProvider DataProtectionProvider { get; }
    public IReadOnlyList<string> Kids { get { lock (gate) { ThrowIfDisposed(); return ring.Keys.Keys.ToArray(); } } }

    public byte[] Sign(string kid, ReadOnlySpan<byte> bytes)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (!ring.Keys.TryGetValue(kid, out var key)) throw new KeyNotFoundException("The requested key version is not retained.");
            return HMACSHA256.HashData(key, bytes);
        }
    }

    public bool Verify(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> signature)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var matched = false;
            foreach (var key in ring.Keys.Values)
            {
                var expected = HMACSHA256.HashData(key, bytes);
                matched |= CryptographicOperations.FixedTimeEquals(expected, signature);
                CryptographicOperations.ZeroMemory(expected);
            }
            return matched;
        }
    }

    public async Task<string> RotateAsync(bool revokeAll = false, IAccountEpochRevoker? epochRevoker = null, CancellationToken cancellationToken = default)
    {
        if (revokeAll && epochRevoker is null)
            throw new InvalidOperationException("Account epoch storage must be bound before --revoke-all can run.");
        await rotation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string kid;
            lock (gate)
            {
                ThrowIfDisposed();
                using var rootLock = DataRootLock.Acquire(directory);
                var next = Read();
                kid = Ulid.NewUlid().ToString();
                next.Keys.Add(kid, RandomNumberGenerator.GetBytes(32));
                next = next with { ActiveKid = kid };
                Persist(next);
                foreach (var key in ring.Keys.Values) CryptographicOperations.ZeroMemory(key);
                ring = next;
            }
            // Failure is surfaced, never claimed as successful revocation. The new key remains valid.
            if (revokeAll) await epochRevoker!.BumpEpochAsync(cancellationToken).ConfigureAwait(false);
            return kid;
        }
        finally { rotation.Release(); }
    }

    private RingDocument Read()
    {
        if (new FileInfo(path).LinkTarget is not null || !OperatingSystem.IsWindows() &&
            (File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite) || RootSecurityValidator.Ownership(path).Uid != RootSecurityValidator.CurrentUid()))
            throw new UnauthorizedAccessException("HMAC key ring must be daemon-owned with mode 0600 and must not be a symlink.");
        try
        {
            var value = JsonSerializer.Deserialize<RingDocument>(File.ReadAllBytes(path));
            if (value is null || value.Keys is null || value.Keys.Count == 0 || !value.Keys.ContainsKey(value.ActiveKid) ||
                value.Keys.Any(pair => !Ulid.TryParse(pair.Key, out _) || pair.Value is not { Length: 32 }))
                throw new InvalidDataException("HMAC key ring is invalid.");
            return value;
        }
        catch (JsonException) { throw new InvalidDataException("HMAC key ring is invalid."); }
    }

    private void Persist(RingDocument value)
    {
        var temporary = Path.Combine(directory, ".hmac-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options)) { JsonSerializer.Serialize(stream, value); stream.Flush(true); }
            File.Move(temporary, path, true);
            UnixSecurity.SyncDirectory(directory);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var key in ring.Keys.Values) CryptographicOperations.ZeroMemory(key);
        }
    }

    private sealed record RingDocument(string ActiveKid, Dictionary<string, byte[]> Keys);
}
