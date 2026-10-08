using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using SecondBrain.Infrastructure.Security;

namespace SecondBrain.Server.Tests.Support;

/// <summary>An in-memory HMAC ring with retained ULID kids and ephemeral Data Protection.</summary>
public sealed class TestKeyRing : IKeyRing
{
    private readonly ConcurrentDictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
    private string _activeKid;
    private long _signCalls;
    private long _verifyCalls;
    private long _hmacCalls;

    public TestKeyRing()
    {
        _activeKid = Ulid.NewUlid().ToString();
        _keys[_activeKid] = RandomNumberGenerator.GetBytes(32);
    }

    public string ActiveKid => Volatile.Read(ref _activeKid);
    public IDataProtectionProvider DataProtectionProvider { get; } = new EphemeralDataProtectionProvider();
    public long SignCalls => Interlocked.Read(ref _signCalls);
    public long VerifyCalls => Interlocked.Read(ref _verifyCalls);
    public long HmacCalls => Interlocked.Read(ref _hmacCalls);

    public byte[] Sign(string kid, ReadOnlySpan<byte> bytes)
    {
        Interlocked.Increment(ref _signCalls);
        if (!_keys.TryGetValue(kid, out var key))
        {
            throw new KeyNotFoundException($"Unknown test HMAC kid: {kid}");
        }

        Interlocked.Increment(ref _hmacCalls);
        return HMACSHA256.HashData(key, bytes);
    }

    public bool Verify(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> signature)
    {
        Interlocked.Increment(ref _verifyCalls);
        var valid = false;
        foreach (var key in _keys.Values)
        {
            Interlocked.Increment(ref _hmacCalls);
            var candidate = HMACSHA256.HashData(key, bytes);
            valid |= CryptographicOperations.FixedTimeEquals(candidate, signature);
        }

        return valid;
    }

    public string Rotate()
    {
        var kid = Ulid.NewUlid().ToString();
        _keys[kid] = RandomNumberGenerator.GetBytes(32);
        Volatile.Write(ref _activeKid, kid);
        return kid;
    }

    public void ResetCounts()
    {
        Interlocked.Exchange(ref _signCalls, 0);
        Interlocked.Exchange(ref _verifyCalls, 0);
        Interlocked.Exchange(ref _hmacCalls, 0);
    }

    public void ResetSignCalls() => Interlocked.Exchange(ref _signCalls, 0);
}
