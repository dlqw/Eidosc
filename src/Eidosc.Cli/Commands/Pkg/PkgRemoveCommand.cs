using Eidosc.ProjectSystem;
using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Diagnostics;
using Eidosc.Cli.Resources;
using Eidosc.Pipeline;

namespace Eidosc.Cli.Commands.Pkg;

public static class PkgRemoveCommand
{
    public static Command Create()
    {
        var command = new Command("remove", CliMessages.PkgRemoveCommandDescription)
        {
            new Argument<string>("name", CliMessages.PkgDependencyNameToRemoveArgumentDescription)
        };
        command.Handler = CommandHandler.Create<string>(Execute);
        return command;
    }

    private static int Execute(string name)
    {
        var commandStopwatch = Stopwatch.StartNew();
        var dir = Directory.GetCurrentDirectory();
        var configPath = Path.Combine(dir, EidosProjectConfigurationLoader.DefaultFileName);
        CliOutput.WriteAction(
            CliMessages.PkgRemovingAction,
            CliMessages.PkgRemoveActionSubject(name, configPath),
            useColors: true);

        if (!File.Exists(configPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(CliMessages.PkgManifestMissingError);
            Console.ResetColor();
            commandStopwatch.Stop();
            CliOutput.WriteFinished(
                "pkg remove",
                false,
                commandStopwatch.Elapsed,
                useColors: true,
                details: CliMessages.PkgManifestMissingDetail);
            return 1;
        }

        var manifest = EidosProjectManifestDocument.Load(configPath);
        var dependencies = manifest.Dependencies;

        if (dependencies == null || dependencies.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(CliMessages.PkgNoDependenciesInManifest);
            Console.ResetColor();
            commandStopwatch.Stop();
            CliOutput.WriteFinished(
                "pkg remove",
                true,
                commandStopwatch.Elapsed,
                useColors: true,
                details: CliMessages.PkgNoDependenciesDetail);
            return 0;
        }

        if (!dependencies.Remove(name))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(CliMessages.PkgDependencyNotFound(name));
            Console.ResetColor();
            commandStopwatch.Stop();
            CliOutput.WriteFinished(
                "pkg remove",
                true,
                commandStopwatch.Elapsed,
                useColors: true,
                details: CliMessages.PkgDependencyNotPresentDetail(name));
            return 0;
        }

        if (dependencies.Count == 0)
        {
            manifest.Dependencies = null;
        }

        manifest.Save(configPath);
        CliOutput.WriteArtifact(CliMessages.ArtifactKindManifest, configPath, useColors: true);

        // Also remove from lock file
        var lockPath = Path.Combine(dir, "eidos.lock.json");
        if (File.Exists(lockPath) && EidosLockFile.TryLoad(lockPath, out var lockFile) && lockFile != null)
        {
            lockFile.Packages.Remove(name);
            lockFile.Save(lockPath);
            CliOutput.WriteArtifact(CliMessages.ArtifactKindLockFile, lockPath, useColors: true);
        }

        Console.WriteLine(CliMessages.PkgRemovedDependency(name));
        commandStopwatch.Stop();
        CliOutput.WriteFinished(
            "pkg remove",
            true,
            commandStopwatch.Elapsed,
            useColors: true,
            details: CliMessages.PkgDependencyDetail(name));
        return 0;
    }
}
