using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Diagnostics;
using Eidosc.Cli.Resources;
using Eidosc.Cli.Lsp;

namespace Eidosc.Cli.Commands;

/// <summary>
/// LSP 语言服务器命令 - 启动 Eidos LSP 服务器
/// </summary>
public static class LspCommand
{
    public static Command Create()
    {
        var importRootOption = ImportRootOptions.Create();

        var command = new Command("lsp", CliMessages.LspServerDescription)
        {
            importRootOption
        };

        command.Handler = CommandHandler.Create<LspOptions>(Execute);
        return command;
    }

    private sealed class LspOptions
    {
        public string[] ImportRoot { get; set; } = [];
    }

    private static async Task<int> Execute(LspOptions options)
    {
        var commandStopwatch = Stopwatch.StartNew();
        CliOutput.WriteAction(CliMessages.StartingAction, CliMessages.LspServerAction, useColors: false, output: Console.Error);
        CliOutput.WriteStatus(
            Diagnostic.DiagnosticLevel.Info,
            CliMessages.LspServerStarting,
            false,
            Console.Error);

        using var server = new LspServer(
            Console.OpenStandardInput(),
            Console.OpenStandardOutput(),
            options.ImportRoot);

        try
        {
            await server.RunAsync();
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            // Log to stderr (stdout is used for JSON-RPC)
            await Console.Error.WriteLineAsync(CliMessages.LspServerError(ex.Message));
            commandStopwatch.Stop();
            CliOutput.WriteFinished("lsp", false, commandStopwatch.Elapsed, useColors: false, details: CliMessages.LspServerErrorDetail, output: Console.Error);
            return 1;
        }

        commandStopwatch.Stop();
        CliOutput.WriteFinished("lsp", true, commandStopwatch.Elapsed, useColors: false, output: Console.Error);
        return 0;
    }
}
