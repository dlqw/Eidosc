using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.Reflection;
using Eidosup.Commands;
using Eidosup.Diagnostics;
using Eidosup.Proxies;
using Eidosup.Installation;
using Eidosup.SelfManagement;

namespace Eidosup;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["__self-replace", .. var replacementArgs])
        {
            return await SelfLifecycleManager.RunReplacementHelperAsync(replacementArgs, CancellationToken.None);
        }

        if (args is ["__self-uninstall", .. var uninstallArgs])
        {
            return await SelfLifecycleManager.RunUninstallHelperAsync(uninstallArgs, CancellationToken.None);
        }

        if (args.Any(static argument => argument is "--quiet" or "-q") &&
            !args.Contains("--json", StringComparer.Ordinal))
        {
            Console.SetOut(TextWriter.Null);
        }

        try
        {
            var layout = ToolInstallLayout.Create(PlatformContext.Detect(), null, null);
            SelfLifecycleManager.CleanupStagedFiles(layout);
            if (ShouldRunScheduledUpdate(args))
            {
                try
                {
                    _ = await new SelfUpdateScheduler().RunIfDueAsync(layout, CancellationToken.None);
                }
                catch (Exception exception) when (exception is EidosupException or IOException or UnauthorizedAccessException)
                {
                }
            }
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
            return ErrorReporter.Write(
                exception,
                verbose: args.Any(static argument => argument is "--verbose" or "-v"),
                json: args.Contains("--json", StringComparer.Ordinal),
                colorMode: ReadEarlyColorMode(args));
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
            ToolchainCommands.CreateComponentCommand(),
            ToolchainCommands.CreateTargetCommand(),
            ToolchainCommands.CreateDefaultCommand(),
            ToolchainCommands.CreateUpdateCommand(),
            ToolchainCommands.CreateCheckCommand(),
            ToolchainCommands.CreateShowCommand(),
            ToolchainCommands.CreateRunCommand(),
            ToolchainCommands.CreateWhichCommand(),
            DocCommand.Create(),
            ToolchainCommands.CreateRollbackCommand(),
            ToolchainCommands.CreateOverrideCommand(),
            LifecycleCommands.CreateSetCommand(),
            LifecycleCommands.CreateSelfCommand(),
            LifecycleCommands.CreateCacheCommand(),
            LifecycleCommands.CreateSourceCommand(),
            LifecycleCommands.CreateCompletionsCommand()
        };
        root.AddGlobalOption(GlobalOptions.Verbose);
        root.AddGlobalOption(GlobalOptions.Json);
        root.AddGlobalOption(GlobalOptions.Quiet);
        root.AddGlobalOption(GlobalOptions.Color);

        return new CommandLineBuilder(root)
            .UseHelp()
            .UseTypoCorrections()
            .UseParseErrorReporting(EidosupExitCodes.InvalidArgument)
            .CancelOnProcessTermination()
            .UseExceptionHandler((exception, context) =>
            {
                if (IsBrokenPipe(exception))
                {
                    context.ExitCode = EidosupExitCodes.Success;
                    return;
                }

                var verbose = context.ParseResult.GetValueForOption(GlobalOptions.Verbose);
                var json = context.ParseResult.GetValueForOption(GlobalOptions.Json);
                var color = context.ParseResult.GetValueForOption(GlobalOptions.Color) ?? "auto";
                context.ExitCode = ErrorReporter.Write(exception, verbose, json, colorMode: color);
            }, EidosupExitCodes.InternalError)
            .Build();
    }

    private static bool IsBrokenPipe(Exception exception) =>
        exception is IOException io &&
        ((io.HResult & 0xffff) is 32 or 109 or 232 ||
         io.Message.Contains("broken pipe", StringComparison.OrdinalIgnoreCase) ||
         io.Message.Contains("pipe is being closed", StringComparison.OrdinalIgnoreCase));

    private static bool ShouldRunScheduledUpdate(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args.Any(static argument => argument is "--version" or "--help" or "-h" or "-?"))
        {
            return false;
        }

        if (args.Any(static argument => argument is "self" or "completions"))
        {
            return false;
        }

        for (var index = 0; index + 1 < args.Count; index++)
        {
            if (args[index] == "set" && args[index + 1] == "auto-self-update")
            {
                return false;
            }
        }

        return true;
    }

    private static string ReadEarlyColorMode(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (args[index].StartsWith("--color=", StringComparison.Ordinal))
            {
                return args[index]["--color=".Length..];
            }

            if (args[index] == "--color" && index + 1 < args.Count)
            {
                return args[index + 1];
            }
        }

        return "auto";
    }

    private static string GetProductVersion()
    {
        var assembly = typeof(Program).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? assembly.GetName().Version?.ToString()
               ?? "unknown";
    }
}
