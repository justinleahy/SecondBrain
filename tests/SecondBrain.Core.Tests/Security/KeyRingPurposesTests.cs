using SecondBrain.Core.Security;
using Xunit;

namespace SecondBrain.Core.Tests.Security;

/// <summary>Data Protection purposes and the application name are persisted contracts (spec §15).</summary>
public sealed class KeyRingPurposesTests
{
    [Fact]
    public void PurposeValuesArePinned()
    {
        Assert.Equal("secondbrain.session", KeyRingPurposes.Session);
        Assert.Equal("secondbrain.cursor", KeyRingPurposes.Cursor);
        Assert.Equal("secondbrain.antiforgery", KeyRingPurposes.Antiforgery);
        Assert.Equal("secondbrain.capability", KeyRingPurposes.Capability);
        Assert.Equal("secondbrain", KeyRingPurposes.ApplicationName);
    }
}
