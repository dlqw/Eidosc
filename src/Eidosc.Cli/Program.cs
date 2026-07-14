using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Help;
using System.CommandLine.NamingConventionBinder;
using System.CommandLine.Parsing;
using Eidosc.Cli.Commands;
using Eidosc.Cli.Commands.Migrate;
using Eidosc.Cli.Commands.Pkg;
using Eidosc.Cli.Resources;
using System.Reflection;

namespace Eidosc.Cli;

/// <summary>
/// Eidosc CLI 入口点
/// </summary>
internal class Program
{
    static async Task<int> Main(string[] args)
    {
        if (args is ["--version"])
        {
            Console.WriteLine(GetProductVersion());
            return 0;
        }

        var rootCommand = CreateRootCommand();

        var parser = new CommandLineBuilder(rootCommand)
            .UseHelp()
            .UseHelpBuilder(_ =>
            {
                var builder = new HelpBuilder(LocalizationResources.Instance);
                HelpCustomization.Apply(builder);
                return builder;
            })
            .Build();

        return await parser.InvokeAsync(args);
    }

    private static string GetProductVersion()
    {
        var assembly = typeof(Program).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? assembly.GetName().Version?.ToString()
               ?? "unknown";
    }

    private static RootCommand CreateRootCommand()
    {
        var rootCommand = new RootCommand(CliMessages.CliRootDescription)
        {
            NewCommand.Create(),
            BuildCommand.Create(),
            CacheCommand.Create(),
            RunCommand.Create(),
            AnalyzeCommand.Create(),
            ExplainCommand.Create(),
            ProfileBatchCommand.Create(),
            FmtCommand.Create(),
            IdeCommand.Create(),
            DebugCommand.Create(),
            InfoCommand.Create(),
            TuiCommand.Create(),
            DocCommand.Create(),
            LspCommand.Create(),
            ReplCommand.Create(),
            MetaCommand.Create(),
            MigrateCommand.Create(),
            PkgCommand.Create()
        };

        // 全局选项
        var verboseOption = new Option<bool>(["--verbose", "-v"], CliMessages.CliVerboseOptionDescription);
        var noColorOption = new Option<bool>("--no-color", CliMessages.CliNoColorOptionDescription);

        rootCommand.AddGlobalOption(verboseOption);
        rootCommand.AddGlobalOption(noColorOption);

        return rootCommand;
    }
}
