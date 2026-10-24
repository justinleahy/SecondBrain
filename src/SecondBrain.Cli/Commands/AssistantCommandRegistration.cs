using System.CommandLine;

namespace SecondBrain.Cli.Commands;

internal static class AssistantCommandRegistration
{
    public static void Add(RootCommand root, CliServices services,
        Action<Command, Func<ParseResult, CancellationToken, Task<CliResult>>> bind,
        Option<bool> json, TextWriter output, TextWriter error)
    {
        IAssistantCommands Adapter() => services.Assistant ?? throw new CliPreconditionException("The assistant HTTP adapter is unavailable.");
        var ask = new Command("ask", "Answer one question from the vault with citations, without changing notes.");
        var question = new Argument<string>("question");
        var deep = new Option<bool>("--deep") { Description = "Allow up to five searches, within the configured turn limits." };
        ask.Arguments.Add(question);
        ask.Options.Add(deep);
        bind(ask, (parse, token) => Adapter().AskAsync(parse.GetValue(question)!, parse.GetValue(deep), token));
        root.Subcommands.Add(ask);

        var chat = new Command("chat", "Start or resume a conversation. /new starts another; /exit leaves the chat.");
        var conversation = new Argument<string?>("conversation-id") { Arity = ArgumentArity.ZeroOrOne };
        chat.Arguments.Add(conversation);
        bind(chat, (parse, token) => Adapter().ChatAsync(parse.GetValue(conversation), services.Input, output, error, parse.GetValue(json), token));
        root.Subcommands.Add(chat);

        var pending = new Command("pending", "Review proposals; approval and discard require a paired CLI session.");
        var list = new Command("list");
        bind(list, (_, token) => Adapter().PendingAsync("list", null, token));
        pending.Subcommands.Add(list);
        foreach (var operation in new[] { "approve", "discard" })
        {
            var command = new Command(operation);
            var id = new Argument<string>("id");
            command.Arguments.Add(id);
            bind(command, (parse, token) => Adapter().PendingAsync(operation, parse.GetValue(id), token));
            pending.Subcommands.Add(command);
        }
        root.Subcommands.Add(pending);
    }
}
