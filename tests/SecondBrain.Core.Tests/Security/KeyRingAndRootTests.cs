using System.Text;
using Microsoft.AspNetCore.DataProtection;
using SecondBrain.Core.Security;
using SecondBrain.Infrastructure.Security;
using Xunit;

namespace SecondBrain.Core.Tests.Security;

public sealed class KeyRingAndRootTests : IDisposable
{
    private readonly string root = Path.Combine(UnixPath.Canonicalize(Path.GetTempPath()), "secondbrain-security-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(root, "data");
    private string Incoming => Path.Combine(root, "incoming");
    private static readonly UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public KeyRingAndRootTests()
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(Data);
        else Directory.CreateDirectory(Data, PrivateDirectory);
        Directory.CreateDirectory(Incoming);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Incoming, PrivateDirectory | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
    }

    [Fact]
    public async Task KeyRingRotationRetainsAllKidsAndPersistsActiveKey()
    {
        using var ring = new FileKeyRing(Data, new FakeProtection());
        var bytes = Encoding.UTF8.GetBytes("credential-secret");
        var oldKid = ring.ActiveKid;
        var oldSignature = ring.Sign(oldKid, bytes);
        Assert.True(Ulid.TryParse(oldKid, out _));
        var nextKid = await ring.RotateAsync();
        Assert.NotEqual(oldKid, nextKid);
        Assert.Equal(nextKid, ring.ActiveKid);
        Assert.True(ring.Verify(bytes, oldSignature));
        Assert.True(ring.Verify(bytes, ring.Sign(nextKid, bytes)));
        Assert.False(ring.Verify(Encoding.UTF8.GetBytes("other"), oldSignature));
        Assert.False(ring.Verify(bytes, new byte[31]));
        using var reopened = new FileKeyRing(Data, new FakeProtection());
        Assert.Equal(nextKid, reopened.ActiveKid);
        Assert.True(reopened.Verify(bytes, oldSignature));
        Assert.Equal(2, reopened.Kids.Count);
    }

    [Fact]
    public async Task KeyRingRevokeAllRequiresHookAndBumpsEpochExactlyOnce()
    {
        using var ring = new FileKeyRing(Data, new FakeProtection());
        var initial = ring.ActiveKid;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ring.RotateAsync(true));
        Assert.Equal(initial, ring.ActiveKid);
        var hook = new EpochHook();
        await ring.RotateAsync(true, hook);
        Assert.Equal(1, hook.Calls);
        await ring.RotateAsync(false, hook);
        Assert.Equal(1, hook.Calls);
    }

    [Fact]
    public void KeyRingUnknownKidIsRejectedAndFilesArePrivate()
    {
        using var ring = new FileKeyRing(Data, new FakeProtection());
        Assert.Throws<KeyNotFoundException>(() => ring.Sign("unknown", [1]));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(PrivateDirectory, File.GetUnixFileMode(Path.Combine(Data, "keyring")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(Data, "keyring", "hmac.json")));
        }
    }

    [Fact]
    public void KeyRingRefusesCorruptOrPermissiveExistingRing()
    {
        using (var ring = new FileKeyRing(Data, new FakeProtection())) { }
        var path = Path.Combine(Data, "keyring", "hmac.json");
        File.WriteAllText(path, "{ invalid json");
        Assert.Throws<InvalidDataException>(() => new FileKeyRing(Data, new FakeProtection()));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
            Assert.Throws<UnauthorizedAccessException>(() => new FileKeyRing(Data, new FakeProtection()));
        }
    }

    [Fact]
    public void LockExcludesOtherHandlesAndReleasesWithoutDeletingInode()
    {
        using (var held = DataRootLock.Acquire(Data))
            Assert.Throws<DataRootLockedException>(() => DataRootLock.Acquire(Data));
        Assert.True(File.Exists(Path.Combine(Data, ".lock")));
        using var next = DataRootLock.Acquire(Data);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(Data, ".lock")));
    }

    [Fact]
    public void LockRejectsSymlink()
    {
        var target = Path.Combine(root, "outside");
        File.WriteAllText(target, "must stay unchanged");
        File.CreateSymbolicLink(Path.Combine(Data, ".lock"), target);
        Assert.Throws<IOException>(() => DataRootLock.Acquire(Data));
        Assert.Equal("must stay unchanged", File.ReadAllText(target));
    }

    [Fact]
    public void RootSecurityValidatesOwnershipAndExactModes()
    {
        if (OperatingSystem.IsWindows()) return;
        var uid = RootSecurityValidator.CurrentUid();
        var gid = RootSecurityValidator.CurrentGid();
        Assert.All(RootSecurityValidator.Validate(Data, Incoming, uid, gid, uid), check => Assert.True(check.Passed, check.Message));
        Assert.Contains(RootSecurityValidator.Validate(Data, Incoming, uid + 1, gid, uid), check => !check.Passed);
        File.SetUnixFileMode(Data, PrivateDirectory | UnixFileMode.GroupRead);
        Assert.Throws<UnauthorizedAccessException>(() => RootSecurityValidator.ValidateOrThrow(Data, Incoming, uid, gid, uid));
    }

    [Theory]
    [InlineData(0u, 1u)]
    [InlineData(1u, 0u)]
    [InlineData(1u, 1u)]
    public void RootSecurityRefusesSharedOrRootServiceIdentities(uint daemon, uint sync)
        => Assert.Throws<UnauthorizedAccessException>(() => RootSecurityValidator.ValidateDaemonIdentities(daemon, sync));

    [Fact]
    public void RootSecurityAcceptsDistinctNonRootIdentities()
        => RootSecurityValidator.ValidateDaemonIdentities(1654, 1656);

    private sealed class EpochHook : IAccountEpochRevoker
    {
        public int Calls { get; private set; }
        public ValueTask BumpEpochAsync(CancellationToken cancellationToken = default) { Calls++; return ValueTask.CompletedTask; }
    }

    private sealed class FakeProtection : IDataProtectionProvider, IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => this;
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();
        public byte[] Unprotect(byte[] protectedData) => protectedData.ToArray();
    }

    public void Dispose() => Directory.Delete(root, true);
}
