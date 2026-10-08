using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Isopoh.Cryptography.Argon2;

namespace SecondBrain.Server.Auth;

public sealed record PasswordParameters(int Version = 1, int MemoryKiB = 65536, int Iterations = 3, int Lanes = 1);
public sealed record Argon2Calibration(PasswordParameters Parameters, double BaselineMilliseconds, double SelectedMilliseconds, string Hardware);

public interface IPasswordHasher
{
    PasswordParameters Parameters { get; }
    string Hash(string password);
    bool Verify(string encodedHash, string password);
    bool NeedsRehash(AccountRecord account);
}

/// <summary>Argon2id with independent random salts. API key authentication never calls this service.</summary>
public sealed class PasswordHasher(PasswordParameters parameters) : IPasswordHasher
{
    public PasswordParameters Parameters { get; } = parameters;
    public string Hash(string password)
    {
        var bytes = Encode(password);
        try
        {
            return Argon2.Hash(new Argon2Config
            {
                Type = Argon2Type.HybridAddressing, Version = Argon2Version.Nineteen,
                MemoryCost = Parameters.MemoryKiB, TimeCost = Parameters.Iterations,
                Lanes = Parameters.Lanes, Threads = Parameters.Lanes, Password = bytes,
                Salt = RandomNumberGenerator.GetBytes(16), HashLength = 32,
            });
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public bool Verify(string encodedHash, string password)
    {
        var bytes = Encode(password);
        try { return Argon2.Verify(encodedHash, bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public bool NeedsRehash(AccountRecord account) => account.PasswordVersion != Parameters.Version ||
        !account.PasswordHash.StartsWith($"$argon2id$v=19$m={Parameters.MemoryKiB},t={Parameters.Iterations},p={Parameters.Lanes}$", StringComparison.Ordinal);
    private static byte[] Encode(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (Encoding.UTF8.GetByteCount(password) > 1024) throw new ArgumentException("Password exceeds 1024 UTF-8 bytes.", nameof(password));
        return Encoding.UTF8.GetBytes(password);
    }
    public static Argon2Calibration Calibrate(TimeSpan? target = null)
    {
        var desired = (target ?? TimeSpan.FromMilliseconds(250)).TotalMilliseconds;
        var baseline = new PasswordParameters();
        // Warm tiered JIT before measuring: managed Argon2's cold code is substantially slower.
        for (var i = 0; i < 5; i++) _ = new PasswordHasher(baseline).Hash("calibration-warmup");
        static double Measure(PasswordParameters p)
        {
            var times = new double[3];
            for (var i = 0; i < times.Length; i++)
            {
                var timer = Stopwatch.StartNew();
                _ = new PasswordHasher(p).Hash("calibration-sample");
                times[i] = timer.Elapsed.TotalMilliseconds;
            }
            Array.Sort(times); return times[1];
        }
        var measured = Measure(baseline);
        var estimated = Math.Clamp(3 * desired / measured, 1, 12);
        var lower = baseline with { Iterations = (int)Math.Floor(estimated) };
        var upper = baseline with { Iterations = (int)Math.Ceiling(estimated) };
        var lowerTime = Measure(lower);
        var upperTime = lower == upper ? lowerTime : Measure(upper);
        var selected = Math.Abs(lowerTime - desired) <= Math.Abs(upperTime - desired) ? lower : upper;
        var selectedTime = selected == lower ? lowerTime : upperTime;
        return new(selected, measured, selectedTime, $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}; {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}; {Environment.ProcessorCount} CPUs");
    }
}
