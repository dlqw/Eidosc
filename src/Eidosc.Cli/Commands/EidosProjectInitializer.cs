using Eidosc.ProjectSystem;
using System.Diagnostics;
using Eidosc.Cli.Resources;
using Eidosc.Pipeline;

namespace Eidosc.Cli.Commands;

internal class EidosProjectInitOptions
{
    public string? Name { get; set; }
    public string Version { get; set; } = "0.1.0";
    public string Kind { get; set; } = "executable";
    public string? Description { get; set; }
    public string? License { get; set; }
    public string[] SourceRoot { get; set; } = [];
}

internal static class EidosProjectInitializer
{
    public static int Initialize(
        string directory,
        EidosProjectInitOptions options,
        string commandName,
        bool useColors,
        string? defaultPackageName = null)
    {
        var commandStopwatch = Stopwatch.StartNew();
        var dir = Path.GetFullPath(directory);
        var configPath = Path.Combine(dir, EidosProjectConfigurationLoader.DefaultFileName);
        CliOutput.WriteAction(CliMessages.ProjectCreatingAction, CliMessages.ProjectPackageInSubject(dir), useColors);

        if (!TryNormalizeKind(options.Kind, out var kind))
        {
            CliOutput.WriteStatus(
                Diagnostic.DiagnosticLevel.Error,
                CliMessages.ProjectInvalidKind(options.Kind),
                useColors);
            commandStopwatch.Stop();
            CliOutput.WriteFinished(commandName, false, commandStopwatch.Elapsed, useColors, CliMessages.ProjectInvalidKindDetail);
            return 1;
        }

        Directory.CreateDirectory(dir);
        if (File.Exists(configPath))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(CliMessages.ProjectManifestAlreadyExists(dir));
            Console.ResetColor();
            commandStopwatch.Stop();
            CliOutput.WriteFinished(
                commandName,
                false,
                commandStopwatch.Elapsed,
                useColors,
                details: CliMessages.ProjectManifestAlreadyExistsDetail);
            return 1;
        }

        var packageName = options.Name ?? defaultPackageName ?? InferPackageName(dir);
        var isExecutable = kind == "executable";
        var sourceRoots = NormalizeSourceRoots(options.SourceRoot);
        var primarySourceRoot = sourceRoots[0];
        var entryFile = isExecutable ? $"{primarySourceRoot}/Main.eidos" : "";
        var usesDefaultExecutableInference = isExecutable &&
                                             sourceRoots.Length == 1 &&
                                             string.Equals(sourceRoots[0], "src", StringComparison.Ordinal);
        EidosProjectTargetManifestDocument[]? targets = usesDefaultExecutableInference
            ? null
            : isExecutable
                ? [new EidosProjectTargetManifestDocument
                {
                    Name = "main",
                    Entry = entryFile,
                    Kind = "executable"
                }]
                : null;

        var manifest = new EidosProjectManifestDocument
        {
            ManifestSchema = 3,
            Language = new EidosProjectLanguageManifestDocument
            {
                Version = EidosLanguageVersions.DefaultForNewProjects
            },
            Package = new EidosProjectPackageManifestDocument
            {
                Name = packageName,
                Version = options.Version,
                Description = options.Description,
                License = options.License
            },
            SourceRoots = sourceRoots,
            Targets = targets
        };

        manifest.Save(configPath);

        var sourceRootPaths = sourceRoots
            .Select(sourceRoot => Path.Combine(dir, sourceRoot.Replace('/', Path.DirectorySeparatorChar)))
            .ToArray();

        foreach (var sourceRootPath in sourceRootPaths)
        {
            Directory.CreateDirectory(sourceRootPath);
        }

        Directory.CreateDirectory(Path.Combine(dir, "build"));
        Directory.CreateDirectory(Path.Combine(dir, "debug"));

        if (isExecutable)
        {
            var mainFile = Path.Combine(sourceRootPaths[0], "Main.eidos");
            if (!File.Exists(mainFile))
            {
                File.WriteAllText(mainFile,
                    """
                    Main :: module {
                        main :: Unit -> Int { _ => 0 }
                    }

                    """);
            }
        }

        Console.WriteLine(CliMessages.ProjectCreatedManifest(dir));
        CliOutput.WriteArtifact(CliMessages.ArtifactKindManifest, configPath, useColors);
        if (isExecutable)
        {
            Console.WriteLine(CliMessages.ProjectCreatedSourceFile(entryFile));
            CliOutput.WriteArtifact(CliMessages.ArtifactKindSource, Path.Combine(sourceRootPaths[0], "Main.eidos"), useColors);
        }
        foreach (var sourceRoot in sourceRoots)
        {
            Console.WriteLine(CliMessages.ProjectCreatedDirectory($"{sourceRoot}/"));
            CliOutput.WriteArtifact(
                CliMessages.ArtifactKindSource,
                Path.Combine(dir, sourceRoot.Replace('/', Path.DirectorySeparatorChar)),
                useColors);
        }
        Console.WriteLine(CliMessages.ProjectCreatedDirectory("build/"));
        CliOutput.WriteArtifact(CliMessages.ArtifactKindBuildDirectory, Path.Combine(dir, "build"), useColors);
        Console.WriteLine(CliMessages.ProjectCreatedDirectory("debug/"));
        CliOutput.WriteArtifact(CliMessages.ArtifactKindDebugDirectory, Path.Combine(dir, "debug"), useColors);
        commandStopwatch.Stop();
        CliOutput.WriteFinished(
            commandName,
            true,
            commandStopwatch.Elapsed,
            useColors,
            details: CliMessages.ProjectPackageDetail(packageName));
        return 0;
    }

    private static bool TryNormalizeKind(string kind, out string normalized)
    {
        normalized = kind.Trim().ToLowerInvariant();
        return normalized is "executable" or "library";
    }

    private static string InferPackageName(string dir)
    {
        var name = Path.GetFileName(dir) ?? "app";
        return $"dev.eidos.{name}";
    }

    private static string[] NormalizeSourceRoots(IReadOnlyList<string>? sourceRoots)
    {
        if (sourceRoots == null || sourceRoots.Count == 0)
        {
            return ["src"];
        }

        var normalized = new List<string>(sourceRoots.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourceRoot in sourceRoots)
        {
            var trimmed = sourceRoot.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            var portableRoot = trimmed
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/')
                .Trim('/');

            if (portableRoot.Length == 0 ||
                Path.IsPathRooted(trimmed) ||
                portableRoot.Split('/').Any(static part => part == ".."))
            {
                continue;
            }

            if (seen.Add(portableRoot))
            {
                normalized.Add(portableRoot);
            }
        }

        return normalized.Count == 0 ? ["src"] : [.. normalized];
    }
}
