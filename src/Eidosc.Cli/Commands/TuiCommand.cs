using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Diagnostics;
using Eidosc.Cli.Resources;
using Eidosc.Cli.Tui;

namespace Eidosc.Cli.Commands;

/// <summary>
/// TUI 命令 - 启动交互式终端界面
/// </summary>
public static class TuiCommand
{
    public static Command Create()
    {
        var command = new Command("tui", CliMessages.TuiCommandDescription)
        {
            new Argument<string?>("workspace", () => null, CliMessages.TuiWorkspaceArgumentDescription)
        };

        command.Handler = CommandHandler.Create<TuiOptions>(Execute);

        return command;
    }

    private sealed class TuiOptions
    {
        public string? Workspace { get; set; }
    }

    private static async Task<int> Execute(TuiOptions options)
    {
        var commandStopwatch = Stopwatch.StartNew();
        try
        {
            var workspace = options.Workspace == null
                ? Directory.GetCurrentDirectory()
                : Path.GetFullPath(options.Workspace);
            CliOutput.WriteAction(CliMessages.StartingAction, CliMessages.TuiWorkspaceSubject(workspace), useColors: true);
            var tui = new EidoscTui(options.Workspace);
            await tui.RunAsync();
            commandStopwatch.Stop();
            CliOutput.WriteFinished("tui", true, commandStopwatch.Elapsed, useColors: true);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(CliMessages.TuiStartFailed(ex.Message));
            commandStopwatch.Stop();
            CliOutput.WriteFinished("tui", false, commandStopwatch.Elapsed, useColors: false, details: CliMessages.TuiStartupFailedDetail, output: Console.Error);
            return 1;
        }
    }
}
