using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.Reflection;
using Eidosup.Commands;
using Eidosup.Diagnostics;

namespace Eidosup;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["--version"])
        {
            Console.WriteLine(GetProductVersion());
            return EidosupExitCodes.Success;
        }

        var root = new RootCommand("Bootstrap and maintain an Eidos development environment.")
        {
            SetupCommand.Create(),
            DoctorCommand.Create()
        };
        root.AddGlobalOption(GlobalOptions.Verbose);
        root.AddGlobalOption(GlobalOptions.Json);

        var parser = new CommandLineBuilder(root)
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

        return await parser.InvokeAsync(args);
    }

    private static string GetProductVersion()
    {
        var assembly = typeof(Program).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? assembly.GetName().Version?.ToString()
               ?? "unknown";
    }
}
