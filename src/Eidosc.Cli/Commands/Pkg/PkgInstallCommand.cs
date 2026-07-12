using Eidosc.ProjectSystem;
using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Diagnostics;
using Eidosc.Cli.Resources;
using Eidosc.Pipeline;

namespace Eidosc.Cli.Commands.Pkg;

public static class PkgInstallCommand
{
    public static Command Create()
    {
        var command = new Command("install", CliMessages.PkgInstallCommandDescription);
        command.Handler = CommandHandler.Create(() => Execute());
        return command;
    }

    private static int Execute()
    {
        var commandStopwatch = Stopwatch.StartNew();
        var dir = Directory.GetCurrentDirectory();
        var configPath = Path.Combine(dir, EidosProjectConfigurationLoader.DefaultFileName);
        CliOutput.WriteAction(
            CliMessages.PkgResolvingAction,
            CliMessages.PkgDependenciesForSubject(configPath),
            useColors: true);

        if (!File.Exists(configPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(CliMessages.PkgManifestMissingInitError);
            Console.ResetColor();
            commandStopwatch.Stop();
            CliOutput.WriteFinished(
                "pkg install",
                false,
                commandStopwatch.Elapsed,
                useColors: true,
                details: CliMessages.PkgManifestMissingDetail);
            return 1;
        }

        var loaded = EidosProjectConfigurationLoader.LoadFromPath(configPath);
        var config = loaded.Configuration;

        var lockPath = Path.Combine(dir, "eidos.lock.json");
        EidosLockFile? existingLock = null;
        if (File.Exists(lockPath))
            EidosLockFile.TryLoad(lockPath, out existingLock);

        try
        {
            var resolver = new PackageDependencyResolver(dir);
            var graph = resolver.Resolve(config, existingLock);

            var lockFile = new EidosLockFile();
            foreach (var (name, pkg) in graph.Packages)
            {
                var locked = PkgLockEntryWriter.CreateLockedPackage(pkg, dir);
                lockFile.Packages[name] = locked;

                var sourceDesc = PkgLockEntryWriter.DescribeSource(pkg, dir);
                Console.WriteLine(CliMessages.PkgDependencyLine(name, sourceDesc));
            }

            lockFile.Save(lockPath);
            CliOutput.WriteArtifact(CliMessages.ArtifactKindLockFile, lockPath, useColors: true);
            Console.WriteLine(CliMessages.PkgResolvedDependencies(graph.Packages.Count));
            commandStopwatch.Stop();
            CliOutput.WriteFinished(
                "pkg install",
                true,
                commandStopwatch.Elapsed,
                useColors: true,
                details: CliMessages.PkgDependencyCountDetail(graph.Packages.Count));
            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(CliMessages.PkgResolveDependenciesFailed(ex.Message));
            Console.ResetColor();
            commandStopwatch.Stop();
            CliOutput.WriteFinished(
                "pkg install",
                false,
                commandStopwatch.Elapsed,
                useColors: true,
                details: CliMessages.PkgResolveFailedDetail);
            return 1;
        }
    }
}
