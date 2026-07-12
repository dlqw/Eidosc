using Eidosc.ProjectSystem;
using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Diagnostics;
using Eidosc.Cli.Resources;
using Eidosc.Pipeline;

namespace Eidosc.Cli.Commands.Pkg;

public static class PkgListCommand
{
    public static Command Create()
    {
        var command = new Command("list", CliMessages.PkgListCommandDescription);
        command.Handler = CommandHandler.Create(() => Execute());
        return command;
    }

    private static int Execute()
    {
        var commandStopwatch = Stopwatch.StartNew();
        var dir = Directory.GetCurrentDirectory();
        var lockPath = Path.Combine(dir, "eidos.lock.json");
        CliOutput.WriteAction(
            CliMessages.PkgListingAction,
            CliMessages.PkgDependenciesFromSubject(lockPath),
            useColors: true);

        if (!File.Exists(lockPath))
        {
            Console.WriteLine(CliMessages.PkgNoLockFile);
            commandStopwatch.Stop();
            CliOutput.WriteFinished(
                "pkg list",
                true,
                commandStopwatch.Elapsed,
                useColors: true,
                details: CliMessages.PkgNoLockFileDetail);
            return 0;
        }

        if (!EidosLockFile.TryLoad(lockPath, out var lockFile) || lockFile == null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(CliMessages.PkgLockReadFailed);
            Console.ResetColor();
            commandStopwatch.Stop();
            CliOutput.WriteFinished(
                "pkg list",
                false,
                commandStopwatch.Elapsed,
                useColors: true,
                details: CliMessages.PkgLockReadFailedDetail);
            return 1;
        }

        if (lockFile.Packages.Count == 0)
        {
            Console.WriteLine(CliMessages.PkgNoDependencies);
            commandStopwatch.Stop();
            CliOutput.WriteFinished(
                "pkg list",
                true,
                commandStopwatch.Elapsed,
                useColors: true,
                details: CliMessages.PkgDependencyCountDetail(0));
            return 0;
        }

        Console.WriteLine(CliMessages.PkgDependenciesHeader(lockFile.Packages.Count));
        foreach (var (name, pkg) in lockFile.Packages.OrderBy(p => p.Key))
        {
            var source = PkgLockEntryWriter.DescribeLockedSource(pkg);
            var hashSuffix = pkg.ContentHash != null
                ? $" ({pkg.ContentHash[..Math.Min(18, pkg.ContentHash.Length)]}...)"
                : "";
            Console.WriteLine(CliMessages.PkgListLine(name, source, hashSuffix));
        }

        commandStopwatch.Stop();
        CliOutput.WriteFinished(
            "pkg list",
            true,
            commandStopwatch.Elapsed,
            useColors: true,
            details: CliMessages.PkgDependencyCountDetail(lockFile.Packages.Count));
        return 0;
    }
}
