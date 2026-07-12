using Eidosc.ProjectSystem;
using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Diagnostics;
using Eidosc.Cli.Resources;
using Eidosc.Pipeline;

namespace Eidosc.Cli.Commands.Pkg;

public static class PkgTreeCommand
{
    public static Command Create()
    {
        var command = new Command("tree", CliMessages.PkgTreeCommandDescription);
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
            CliMessages.PkgDependencyTreeForSubject(configPath),
            useColors: true);

        if (!File.Exists(configPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(CliMessages.PkgManifestMissingError);
            Console.ResetColor();
            commandStopwatch.Stop();
            CliOutput.WriteFinished(
                "pkg tree",
                false,
                commandStopwatch.Elapsed,
                useColors: true,
                details: CliMessages.PkgManifestMissingDetail);
            return 1;
        }

        var loaded = EidosProjectConfigurationLoader.LoadFromPath(configPath);
        var config = loaded.Configuration;
        var lockPath = Path.Combine(dir, "eidos.lock.json");
        EidosLockFile? lockFile = null;
        if (File.Exists(lockPath))
            EidosLockFile.TryLoad(lockPath, out lockFile);
        var packageName = config.Package?.Name ?? Path.GetFileName(dir) ?? "root";
        var packageVersion = config.Package?.Version.ToString() ?? "0.1.0";

        Console.WriteLine($"{packageName}@{packageVersion}");

        var deps = config.VersionedDependencies;
        if (deps == null || deps.Count == 0)
        {
            if (!config.NoImplicitStdlib)
                Console.WriteLine(CliMessages.PkgStdEmbeddedTreeEntry);
            commandStopwatch.Stop();
            CliOutput.WriteFinished(
                "pkg tree",
                true,
                commandStopwatch.Elapsed,
                useColors: true,
                details: CliMessages.PkgZeroExplicitDependenciesDetail);
            return 0;
        }

        var entries = deps.OrderBy(d => d.Key).ToList();
        if (!config.NoImplicitStdlib && !deps.ContainsKey("Std"))
            entries.Add(new KeyValuePair<string, DependencySpec>("Std", new DependencySpec { Version = "embedded" }));

        for (var i = 0; i < entries.Count; i++)
        {
            var (name, spec) = entries[i];
            var isLast = i == entries.Count - 1;
            var prefix = isLast ? "  └── " : "  ├── ";

            var desc = spec.SourceKind switch
            {
                DependencySourceKind.Path => CliMessages.PkgPathSource(spec.Path ?? ""),
                DependencySourceKind.Git => CliMessages.PkgGitSource(spec.Tag ?? spec.Branch ?? spec.Commit?[..8] ?? ""),
                DependencySourceKind.Version when lockFile?.Packages.GetValueOrDefault(name) is { } locked =>
                    PkgLockEntryWriter.DescribeLockedSource(locked),
                DependencySourceKind.Version => spec.Version ?? CliMessages.PkgEmbeddedSourceKind,
                _ => ""
            };
            Console.WriteLine($"{prefix}{name} ({desc})");
        }

        commandStopwatch.Stop();
        CliOutput.WriteFinished(
            "pkg tree",
            true,
            commandStopwatch.Elapsed,
            useColors: true,
            details: CliMessages.PkgDependencyCountDetail(entries.Count));
        return 0;
    }
}
