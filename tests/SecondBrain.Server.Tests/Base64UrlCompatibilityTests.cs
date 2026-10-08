using System.Buffers.Text;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace SecondBrain.Server.Tests;

/// <summary>Proves the BCL Base64Url encoder is a drop-in replacement for ASP.NET's WebEncoders.</summary>
public sealed class Base64UrlCompatibilityTests
{
    [Fact]
    public void EveryLengthUpTo64EncodesIdentically()
    {
        for (var length = 0; length <= 64; length++)
        {
            var bytes = new byte[length];
            for (var i = 0; i < length; i++) bytes[i] = (byte)(i * 37 + length);
            Assert.Equal(WebEncoders.Base64UrlEncode(bytes), Base64Url.EncodeToString(bytes));
        }
    }

    [Fact]
    public void RandomThirtyTwoByteBuffersEncodeIdentically()
    {
        var random = new Random(20261008);
        var bytes = new byte[32];
        for (var i = 0; i < 10_000; i++)
        {
            random.NextBytes(bytes);
            Assert.Equal(WebEncoders.Base64UrlEncode(bytes), Base64Url.EncodeToString(bytes));
        }
    }
}
