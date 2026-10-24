using SecondBrain.Core.Chat;
using Xunit;

namespace SecondBrain.Core.Tests;

public sealed class ChatSlashCommandTests
{
    [Theory]
    [InlineData("/query")]
    [InlineData("/query note {\"shell\":\"touch /tmp/no\"}")]
    [InlineData("/query note {\"limit\":51}")]
    [InlineData("/recent 51")]
    [InlineData("/sources extra")]
    [InlineData("/search ")]
    [InlineData("/unknown")]
    public void InvalidOrUnknownCommandsAreRejected(string prompt) =>
        Assert.Throws<ArgumentException>(() => ChatSlashCommand.Parse(prompt));

    [Fact]
    public void StructuredQueryPreservesFiltersWithoutTreatingThemAsModelInstructions()
    {
        var command = ChatSlashCommand.Parse("/query article {\"tags\":[\"work\"],\"limit\":3,\"people\":[{\"value\":\"Ada Lovelace\"}]}")!;
        Assert.Equal("article", command.Query!.Type);
        Assert.Equal("work", Assert.Single(command.Query.Tags!));
        Assert.Equal("Ada Lovelace", Assert.Single(command.Query.People!).Value);
        Assert.Equal(3, command.Query.Limit);
        Assert.Null(ChatSlashCommand.Parse("What is /query used for?"));
        Assert.Equal("Remember this\nexactly", ChatSlashCommand.Parse("/save Remember this\nexactly")!.Argument);
    }
}
