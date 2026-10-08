using System.CommandLine;

namespace SecondBrain.Cli;

/// <summary>The command host scaffold; commands are added in spec §12 / M0 item 15.</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var command = new RootCommand("SecondBrain CLI — M0 command scaffold.");
        return command.Parse(args).Invoke();
    }
}
