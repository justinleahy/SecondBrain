using SecondBrain.Infrastructure.Ingestion;
using Xunit;

namespace SecondBrain.Extractor.Tests;

/// <summary>Executed by the Linux deployment harness with separate daemon and sync identities.</summary>
public sealed class DistinctIdentityStagingTests
{
    [Fact]
    [Trait("Category", "Qualification")]
    public async Task ConcurrentSyncIdentityCannotRedirectStagingOrReadPrivateStage()
    {
        var root = Environment.GetEnvironmentVariable("SECONDBRAIN_STAGING_QUALIFICATION_ROOT")
            ?? throw new InvalidOperationException("Run deploy/tests/distinct-identity-staging.sh.");
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux identity qualification.");
        using var stager = new InputStager(Path.Combine(root, "stage"));
        await Assert.ThrowsAnyAsync<IOException>(async () => await stager.OpenSourceAsync(Path.Combine(root, "incoming"), "symlink.md"));
        var hardLink = await Assert.ThrowsAsync<InvalidDataException>(async () => await stager.OpenSourceAsync(Path.Combine(root, "incoming"), "hardlink.md"));
        Assert.Equal("source_multiple_links", hardLink.Message);
        var accepted = 0; var rejected = 0;
        for (var attempt = 0; attempt < 2000; attempt++)
        {
            try
            {
                await using var staged = await stager.OpenSourceAsync(Path.Combine(root, "incoming"), "racing.md");
                using var reader = new StreamReader(staged.Content);
                Assert.Equal("safe sync bytes", await reader.ReadToEndAsync());
                Assert.False(staged.Content.CanWrite);
                accepted++;
            }
            catch (IOException) { rejected++; }
            catch (InvalidDataException) { rejected++; }
            if (attempt % 20 == 0) await Task.Delay(1);
        }
        Assert.True(accepted > 0, "The race must exercise successful safe reads.");
        Assert.True(rejected > 0, "The race must exercise rejected symlink swaps.");
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "stage")));
        // The harness independently checks this directory from the sync identity.
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.Combine(root, "stage")));
        var claims = new InboxClaims(stager);
        var inbox = await claims.EnsureAsync(Path.Combine(root, "incoming"));
        // The sync UID cannot replace a daemon-owned private claim entry inside
        // the sticky inbox, but it does own the enclosing incoming directory.
        Assert.False(Directory.Exists(Path.Combine(inbox, "claimed-substitution")));
        await File.WriteAllTextAsync(Path.Combine(root, "control", "replace-inbox"), "replace");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await File.ReadAllTextAsync(Path.Combine(root, "control", "done"), deadline.Token) != "replacement complete")
            await Task.Delay(10, deadline.Token);
        var claim = await claims.ClaimAsync(inbox, "safe.txt");
        await using (var staged = await stager.OpenSourceAsync(inbox, claim.RelativeClaimPath))
        {
            using var reader = new StreamReader(staged.Content);
            Assert.Equal("safe inbox bytes", await reader.ReadToEndAsync());
        }
        await claims.CompleteAsync(inbox, claim);
        using var fresh = new InputStager(Path.Combine(root, "stage", "fresh"));
        var replaced = await Assert.ThrowsAsync<InvalidDataException>(() => new InboxClaims(fresh).EnsureAsync(Path.Combine(root, "incoming")).AsTask());
        Assert.Equal("inbox_directory_ownership", replaced.Message);
    }
}
