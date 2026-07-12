using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.Reflection;
using Eidosup.Commands;
using Eidosup.Diagnostics;
using Eidosup.Proxies;

namespace Eidosup;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (ProxyInvocation.TryCreate(Environment.ProcessPath, out var proxyInvocation))
            {
                return await new ProxyHost().RunAsync(
                    proxyInvocation!,
                    args,
                    CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            return ErrorReporter.Write(exception, verbose: false, json: false);
        }

        if (args is ["--version"])
        {
            Console.WriteLine(GetProductVersion());
            return EidosupExitCodes.Success;
        }

        return await CreateParser().InvokeAsync(args);
    }

    internal static Parser CreateParser()
    {
        var root = new RootCommand("Install, select, and maintain Eidos development toolchains.")
        {
            SetupCommand.Create(),
            DoctorCommand.Create(),
            ToolchainCommands.CreateToolchainCommand(),
            ToolchainCommands.CreateDefaultCommand(),
            ToolchainCommands.CreateUpdateCommand(),
            ToolchainCommands.CreateCheckCommand(),
            ToolchainCommands.CreateShowCommand(),
            ToolchainCommands.CreateRunCommand(),
            ToolchainCommands.CreateWhichCommand(),
            ToolchainCommands.CreateRollbackCommand()
        };
        root.AddGlobalOption(GlobalOptions.Verbose);
        root.AddGlobalOption(GlobalOptions.Json);

        return new CommandLineBuilder(root)
            .UseHelp()
            .UseTypoCorrections()
            .UseParseErrorReporting(EidosupExitCodes.InvalidArgument)
            .CancelOnProcessTermination()
            .UseExceptionHandler((exception, context) =>
            {
                var verbose = context.ParseResult.GetValueForOption(GlobalOptions.Verbose);
                var json = context.ParseResult.GetValueForOption(GlobalOptions.Json);
                context.ExitCode = ErrorReporter.Write(exception, verbose, json);
            }, EidosupExitCodes.InternalError)
            .Build();
    }

    private static string GetProductVersion()
    {
        var assembly = typeof(Program).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? assembly.GetName().Version?.ToString()
               ?? "unknown";
    }
}
