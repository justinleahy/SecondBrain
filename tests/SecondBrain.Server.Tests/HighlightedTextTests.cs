using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SecondBrain.Core.Retrieval;
using SecondBrain.Server.Components.Pages;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class HighlightedTextTests
{
    [Fact]
    public async Task HighlightsAreTextAndCannotInjectHtml()
    {
        var html = await RenderAsync("<script>alert(1)</script>", [new(0, 8)]);
        Assert.Contains("<mark>&lt;script&gt;</mark>", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;/script&gt;", html);
    }

    [Fact]
    public async Task OverlappingOutOfOrderAndInvalidRangesCannotDropOrDuplicateText()
    {
        var html = await RenderAsync("abcdefgh", [new(4, 99), new(-3, 2), new(1, 5), new(7, 3)]);
        Assert.Equal("<mark>ab</mark><mark>cde</mark><mark>fgh</mark>", html);
    }

    private static async Task<string> RenderAsync(string text, IReadOnlyList<HighlightRange> ranges)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<HighlightedText>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(HighlightedText.Text)] = text,
                [nameof(HighlightedText.Ranges)] = ranges
            }));
            return root.ToHtmlString();
        });
    }
}
