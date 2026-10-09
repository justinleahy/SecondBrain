using System.Net;
using SecondBrain.Core.Privacy;
using SecondBrain.Core.Providers;
using Xunit;

namespace SecondBrain.Providers.OpenAICompatible.Tests;

public sealed class InferenceBodyAdmissionTests
{
    [Theory]
    [InlineData("role")]
    [InlineData("binding")]
    [InlineData("canary")]
    public async Task ReplacementBetweenBodyChunksRefusesTheLaterWrite(string change)
    {
        await using var context = await ProviderTestContext.StartAsync();
        var binding = context.Registry.GetRole(ModelRole.Chat).Provider;
        using var client = context.Factory.CreateClient(ModelRole.Chat, binding);
        var reachedSecondChunk = false;
        using var content = new SplitContent(() =>
        {
            var candidate = ProviderTestContext.CreateOptions(context.Endpoint);
            if (change == "role")
            {
                candidate.Providers["other"] = new() { Kind = "openai_compatible", Endpoint = context.Endpoint.AbsoluteUri };
                candidate.Models.Chat!.Provider = "other";
            }
            if (change == "binding") candidate.Providers["test-local"].Endpoint = context.Endpoint.AbsoluteUri + "/changed";
            if (change == "canary") candidate.Privacy.EgressCanary = true;
            context.Options.Reload(candidate);
            reachedSecondChunk = true;
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(context.Endpoint.AbsoluteUri + "/chat/completions")) { Content = content };
        await Assert.ThrowsAsync<PrivacyPolicyException>(() => client.SendAsync(request));
        Assert.True(reachedSecondChunk);
        Assert.False(content.CompletedSecondChunk);
    }

    private sealed class SplitContent(Action betweenChunks) : HttpContent
    {
        public bool CompletedSecondChunk { get; private set; }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync("{\"model\":\"mock-chat\",\"messages\":["u8.ToArray());
            await stream.FlushAsync();
            betweenChunks();
            await stream.WriteAsync(new byte[128 * 1024]);
            await stream.FlushAsync();
            CompletedSecondChunk = true;
        }
    }
}
