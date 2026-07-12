using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Diagnostics;
using Eidosc.Cli.Resources;
using Eidosc.Cli.Repl;

namespace Eidosc.Cli.Commands;

public static class ReplCommand
{
    public static Command Create()
    {
        var importRootOption = ImportRootOptions.Create();
        var command = new Command("repl", CliMessages.ReplCommandDescription)
        {
            new Option<string>("--project", CliMessages.ReplProjectOptionDescription),
            importRootOption
        };

        command.Handler = CommandHandler.Create<ReplOptions>(Execute);
        return command;
    }

    private sealed class ReplOptions
    {
        public string? Project { get; set; }
        public string[] ImportRoot { get; set; } = [];
    }

    private static async Task<int> Execute(ReplOptions options)
    {
        var commandStopwatch = Stopwatch.StartNew();
        CliOutput.WriteAction(CliMessages.StartingAction, CliMessages.ReplSessionAction, useColors: true);
        var session = new ReplSession(options.ImportRoot);
        await session.RunAsync();
        commandStopwatch.Stop();
        CliOutput.WriteFinished("repl", true, commandStopwatch.Elapsed, useColors: true);
        return 0;
    }
}
