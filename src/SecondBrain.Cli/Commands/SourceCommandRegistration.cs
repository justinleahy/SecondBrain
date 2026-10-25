using System.CommandLine;
using System.Text;
using SecondBrain.Core.Sources;

namespace SecondBrain.Cli.Commands;

internal static class SourceCommandRegistration
{
    public static void Add(RootCommand root, CliServices services, Action<Command, Func<ParseResult, CancellationToken, Task<CliResult>>> bind)
    {
        ISourceCommands Adapter() => services.Sources ?? throw new CliPreconditionException("The source HTTP adapter is unavailable.");
        var sources = new Command("sources", "Manage folder sources and read-only shared calendars.");
        var add = new Command("add-calendar", "Register a calendar; the private link is entered without echo or read from stdin.");
        var name = new Option<string>("--name") { Required = true };
        var stdin = new Option<bool>("--url-stdin") { Description = "Read the private calendar URL from standard input, not command arguments." };
        var interval = new Option<int>("--poll-interval") { DefaultValueFactory = _ => 3600, Description = "Refresh interval in seconds (at least 300)." };
        var paused = new Option<bool>("--paused");
        foreach (var option in new Option[] { name, stdin, interval, paused }) add.Options.Add(option);
        bind(add, async (parse, token) =>
        {
            var selectedName = parse.GetValue(name)!; var seconds = parse.GetValue(interval);
            try { CalendarSubscriptionService.Validate(selectedName, seconds); }
            catch (CalendarSubscriptionException) { throw new CliUsageException("Use a name of 1–200 characters and a refresh interval of at least 300 seconds."); }
            var url = parse.GetValue(stdin) ? await ReadStdinAsync(services.Input, token) : await services.ReadCalendarLink(token);
            try { _ = CalendarLink.Parse(url); }
            catch (CalendarSubscriptionException) { throw new CliUsageException("Enter one HTTPS or webcal calendar link without credentials or a fragment."); }
            return await Adapter().AddCalendarAsync(selectedName.Trim(), url, seconds, !parse.GetValue(paused), token);
        });
        sources.Subcommands.Add(add);
        var list = new Command("list", "List source status without private calendar links.");
        bind(list, (_, token) => Adapter().ListSourcesAsync(token)); sources.Subcommands.Add(list);
        var scan = new Command("scan", "Queue a source scan or calendar refresh.");
        var scanId = new Argument<string>("id"); var deep = new Option<bool>("--deep");
        scan.Arguments.Add(scanId); scan.Options.Add(deep);
        bind(scan, (parse, token) => Adapter().ScanSourceAsync(parse.GetValue(scanId)!, parse.GetValue(deep), token)); sources.Subcommands.Add(scan);
        foreach (var operation in new[] { "pause", "resume", "remove" })
        {
            var command = new Command(operation, operation == "remove" ? "Stop a source and hide its imported documents." : operation + " a source.");
            var id = new Argument<string>("id"); command.Arguments.Add(id);
            var purge = new Option<bool>("--purge") { Description = "Permanently purge a source and its retained data; external originals remain." };
            if (operation == "remove") command.Options.Add(purge);
            bind(command, (parse, token) => operation == "remove" ? Adapter().RemoveSourceAsync(parse.GetValue(id)!, token, parse.GetValue(purge)) :
                Adapter().SetSourceEnabledAsync(parse.GetValue(id)!, operation == "resume", token));
            sources.Subcommands.Add(command);
        }
        root.Subcommands.Add(sources);
    }

    private static async Task<string> ReadStdinAsync(TextReader input, CancellationToken token)
    {
        var buffer = new char[8195]; var length = 0;
        while (length < buffer.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(length), token);
            if (read == 0) return new string(buffer, 0, length).TrimEnd('\r', '\n');
            length += read;
        }
        throw new CliUsageException("The calendar link exceeds the 8192-character limit.");
    }

    internal static Task<string> ReadHiddenLinkAsync(CancellationToken token)
    {
        if (Console.IsInputRedirected) throw new CliUsageException("Use --url-stdin to read a calendar link from redirected input.");
        Console.Error.Write("Shared calendar link (hidden): ");
        var text = new StringBuilder();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; }
            else if (!char.IsControl(key.KeyChar))
            {
                if (text.Length == 8192) throw new CliUsageException("The calendar link exceeds the 8192-character limit.");
                text.Append(key.KeyChar);
            }
        }
        Console.Error.WriteLine();
        return Task.FromResult(text.ToString());
    }
}
