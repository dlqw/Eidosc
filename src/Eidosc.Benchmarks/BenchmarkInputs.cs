using Eidosc.Pipeline;
using Eidosc.ProjectSystem;

namespace Eidosc.Benchmarks;

internal sealed class BenchmarkInput
{
    public required string Name { get; init; }
    public required string SourcePath { get; init; }
    public required string SourceText { get; init; }
    public required CompilationOptions Options { get; init; }
}

internal static class BenchmarkInputs
{
    public static BenchmarkInput Load(string relativePath, CompilationPhase? stopAtPhase = CompilationPhase.Types)
    {
        var workspaceRoot = FindWorkspaceRoot();
        var sourcePath = Path.GetFullPath(Path.Combine(workspaceRoot, relativePath));
        var project = EidosProjectConfigurationLoader.TryLoadNearest(sourcePath);
        var target = TryResolveProjectTarget(project, sourcePath);
        var importResolution = target?.ImportResolution ??
                               EidosProjectConfigurationLoader.ResolveImportSearchRoots(sourcePath);

        return new BenchmarkInput
        {
            Name = Path.GetFileNameWithoutExtension(sourcePath),
            SourcePath = sourcePath,
            SourceText = File.Exists(sourcePath) ? File.ReadAllText(sourcePath) : "",
            Options = new CompilationOptions
            {
                InputFile = sourcePath,
                LanguageVersion = project?.Configuration.LanguageVersion ??
                                EidosLanguageVersions.DefaultForExistingProjects,
                StopAtPhase = stopAtPhase,
                DebugLevel = Eidosc.Debug.DebugLevel.Minimal,
                UseColors = false,
                EnableDetailedProfiling = true,
                ImportSearchRoots = target?.EffectiveSearchRoots ??
                                    importResolution.EffectiveSearchRoots,
                PackageImportRoots = target?.PackageImportRoots ??
                                     new Dictionary<string, string[]>(StringComparer.Ordinal)
            }
        };
    }

    private static ResolvedEidosProjectTarget? TryResolveProjectTarget(
        LoadedEidosProjectConfiguration? project,
        string sourcePath)
    {
        if (project == null)
        {
            return null;
        }

        try
        {
            var targetName = project.Configuration.Targets
                .Where(target => string.Equals(target.Entry, sourcePath, StringComparison.OrdinalIgnoreCase))
                .Select(target => target.Name)
                .SingleOrDefault();
            if (targetName == null &&
                project.Configuration.Targets.Length == 1 &&
                string.Equals(project.Configuration.Targets[0].Entry, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                targetName = project.Configuration.Targets[0].Name;
            }

            return string.IsNullOrWhiteSpace(targetName)
                ? null
                : EidosProjectGraphResolver.ResolveTarget(project, targetName);
        }
        catch
        {
            return null;
        }
    }

    private static string FindWorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Eidosc.sln")))
            {
                return directory.FullName;
            }

            if (File.Exists(Path.Combine(directory.FullName, "Eidosc", "Eidosc.sln")))
            {
                return Path.Combine(directory.FullName, "Eidosc");
            }

            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
