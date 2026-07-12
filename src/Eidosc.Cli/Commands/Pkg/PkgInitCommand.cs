using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using Eidosc.Cli.Resources;

namespace Eidosc.Cli.Commands.Pkg;

public static class PkgInitCommand
{
    public static Command Create()
    {
        var command = new Command("init", CliMessages.PkgInitCommandDescription)
        {
            new Option<string>("--name", CliMessages.PkgPackageNameOptionDescription),
            new Option<string>("--version", () => "0.1.0", CliMessages.PkgInitialVersionOptionDescription),
            new Option<string>("--kind", () => "executable", CliMessages.PkgKindOptionDescription),
            new Option<string[]>("--source-root", CliMessages.PkgSourceRootOptionDescription),
            new Option<string>("--description", CliMessages.PkgDescriptionOptionDescription),
            new Option<string>("--license", CliMessages.PkgLicenseOptionDescription),
        };

        command.Handler = CommandHandler.Create<InitOptions>(Execute);
        return command;
    }

    private sealed class InitOptions : EidosProjectInitOptions
    {
    }

    private static int Execute(InitOptions options)
    {
        return EidosProjectInitializer.Initialize(
            Directory.GetCurrentDirectory(),
            options,
            "pkg init",
            useColors: true);
    }
}
