using System.CommandLine;
using Eidosc.Cli.Resources;

namespace Eidosc.Cli.Commands.Pkg;

public static class PkgCommand
{
    public static Command Create()
    {
        var command = new Command("pkg", CliMessages.PkgCommandDescription)
        {
            PkgInitCommand.Create(),
            PkgAddCommand.Create(),
            PkgInstallCommand.Create(),
            PkgUpdateCommand.Create(),
            PkgRemoveCommand.Create(),
            PkgListCommand.Create(),
            PkgTreeCommand.Create(),
            PkgBindCommand.Create(),
        };
        return command;
    }
}
