using Eidosc.ProjectSystem;
using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Diagnostics;
using Eidosc.Cli.Resources;
using Eidosc.Pipeline;

namespace Eidosc.Cli.Commands.Pkg;

public static class PkgUpdateCommand
{
    public static Command Create()
    {
        var command = new Command("update", CliMessages.PkgUpdateCommandDescription)
        {
            new Argument<string?>("name", () => null, CliMessages.PkgDependencyNameToUpdateArgumentDescription)
        };
        command.Handler = CommandHandler.Create<string?>(Execute);
        return command;
    }

    private static int Execute(string? name)
    {
        var commandStopwatch = Stopwatch.StartNew();
        var dir = Directory.GetCurrentDirectory();
        var configPath = Path.Combine(dir, EidosProjectConfigurationLoader.DefaultFileName);
        CliOutput.WriteAction(
            CliMessages.PkgUpdatingAction,
            name == null
                ? CliMessages.PkgAllDependenciesInSubject(configPath)
                : CliMessages.PkgNamedDependencyInSubject(name, configPath),
            useColors: true);

        if (!File.Exists(configPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(CliMessages.PkgManifestMissingError);
            Console.ResetColor();
            commandStopwatch.Stop();
            CliOutput.WriteFinished(
                "pkg update",
                false,
                commandStopwatch.Elapsed,
                useColors: true,
                details: CliMessages.PkgManifestMissingDetail);
            return 1;
        }

        var loaded = EidosProjectConfigurationLoader.LoadFromPath(configPath);
        var lockPath = Path.Combine(dir, "eidos.lock.json");

        // If specific name: keep all other lock entries, only re-resolve that one
        EidosLockFile? partialLock = null;
        if (name != null && File.Exists(lockPath))
        {
            EidosLockFile.TryLoad(lockPath, out var fullLock);
            if (fullLock != null)
            {
                partialLock = new EidosLockFile();
                foreach (var (pkgName, pkg) in fullLock.Packages)
                {
                    if (pkgName != name)
                        partialLock.Packages[pkgName] = pkg;
                }
            }
        }

        try
        {
            var resolver = new PackageDependencyResolver(dir);
            var graph = resolver.Resolve(loaded.Configuration, partialLock);

            var lockFile = new EidosLockFile();
            foreach (var (pkgName, pkg) in graph.Packages)
            {
                lockFile.Packages[pkgName] = PkgLockEntryWriter.CreateLockedPackage(pkg, dir);
                Console.WriteLine(CliMessages.PkgUpdateLine(pkgName, lockFile.Packages[pkgName].Source));
            }

            lockFile.Save(lockPath);
            CliOutput.WriteArtifact(CliMessages.ArtifactKindLockFile, lockPath, useColors: true);
            Console.WriteLine(CliMessages.PkgUpdatedDependencies(graph.Packages.Count));
            commandStopwatch.Stop();
            CliOutput.WriteFinished(
                "pkg update",
                true,
                commandStopwatch.Elapsed,
                useColors: true,
                details: CliMessages.PkgDependencyCountDetail(graph.Packages.Count));
            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(CliMessages.PkgUpdateFailed(ex.Message));
            Console.ResetColor();
            commandStopwatch.Stop();
            CliOutput.WriteFinished(
                "pkg update",
                false,
                commandStopwatch.Elapsed,
                useColors: true,
                details: CliMessages.PkgResolveFailedDetail);
            return 1;
        }
    }
}
