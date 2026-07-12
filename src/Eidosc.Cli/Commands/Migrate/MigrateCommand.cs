using System.CommandLine;

namespace Eidosc.Cli.Commands.Migrate;

public static class MigrateCommand
{
    public static Command Create()
    {
        return new Command("migrate", "Migration tools for Eidos projects and source files.")
        {
            MigrateSyntaxCommand.Create(),
            MigrateManifestCommand.Create()
        };
    }
}
