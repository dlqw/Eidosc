using Eidosc.Cli.Resources;

namespace Eidosc.Cli.Repl;

public sealed class ReplCommandRegistry
{
    private readonly ReplSession _session;

    public ReplCommandRegistry(ReplSession session)
    {
        _session = session;
    }

    public async Task<bool> ExecuteAsync(string input)
    {
        var parts = input.Split(' ', 2);
        var command = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1].Trim() : "";

        switch (command)
        {
            case ":help":
                PrintHelp();
                return false;

            case ":quit":
            case ":q":
                return true;

            case ":type":
                if (string.IsNullOrEmpty(arg))
                {
                    Console.WriteLine(CliMessages.ReplTypeUsage);
                    return false;
                }
                _session.ShowType(arg);
                return false;

            case ":load":
            case ":l":
                if (string.IsNullOrEmpty(arg))
                {
                    Console.WriteLine(CliMessages.ReplLoadUsage);
                    return false;
                }
                _session.EvaluateFile(arg);
                return false;

            case ":env":
                _session.PrintEnvironment();
                return false;

            case ":clear":
                _session.ClearEnvironment();
                return false;

            default:
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(CliMessages.ReplUnknownCommand(command));
                Console.ResetColor();
                return false;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine(CliMessages.ReplHelpAvailableCommands);
        Console.WriteLine(CliMessages.ReplHelpShowHelp);
        Console.WriteLine(CliMessages.ReplHelpExit);
        Console.WriteLine(CliMessages.ReplHelpType);
        Console.WriteLine(CliMessages.ReplHelpLoad);
        Console.WriteLine(CliMessages.ReplHelpEnv);
        Console.WriteLine(CliMessages.ReplHelpClear);
        Console.WriteLine();
        Console.WriteLine(CliMessages.ReplHelpEvaluate);
    }
}
