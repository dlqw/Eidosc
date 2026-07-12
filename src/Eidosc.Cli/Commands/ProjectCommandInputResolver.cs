using Eidosc.ProjectSystem;
using Eidosc.Cli.Resources;
using Eidosc.Pipeline;

namespace Eidosc.Cli.Commands;

internal sealed record ProjectCommandInputResolution(
    string SourceFilePath,
    ProjectImportSearchResolution ImportResolution,
    ResolvedEidosProjectTarget? ProjectTarget)
{
    public string GetLanguageVersion()
    {
        var config = ImportResolution.ProjectFilePath != null
            ? EidosProjectConfigurationLoader.TryLoadFromPath(ImportResolution.ProjectFilePath)?.Configuration
            : EidosProjectConfigurationLoader.TryLoadNearest(SourceFilePath)?.Configuration;

        return config?.LanguageVersion ?? EidosLanguageVersions.DefaultForExistingProjects;
    }
}

internal static class ProjectCommandInputResolver
{
    public static ProjectCommandInputResolution Resolve(
        string? source,
        string? project,
        string? targetName,
        IReadOnlyList<string>? explicitImportRoots = null,
        string? workingDirectory = null)
    {
        var baseDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(workingDirectory);
        var normalizedProject = NormalizeInput(project);
        var normalizedSource = NormalizeInput(source);
        var normalizedTargetName = NormalizeInput(targetName);

        if (!string.IsNullOrWhiteSpace(normalizedProject))
        {
            return ResolveProjectTarget(
                Path.GetFullPath(normalizedProject, baseDirectory),
                normalizedTargetName,
                explicitImportRoots);
        }

        if (LooksLikeProjectPath(normalizedSource, baseDirectory))
        {
            return ResolveProjectTarget(
                Path.GetFullPath(normalizedSource!, baseDirectory),
                normalizedTargetName,
                explicitImportRoots);
        }

        if (string.IsNullOrWhiteSpace(normalizedSource))
        {
            var currentProjectPath = TryFindNearestProjectFile(baseDirectory);
            if (currentProjectPath != null)
            {
                return ResolveProjectTarget(currentProjectPath, normalizedTargetName, explicitImportRoots);
            }

            if (!string.IsNullOrWhiteSpace(normalizedTargetName))
            {
                throw new InvalidOperationException(CliMessages.ProjectTargetNameRequiresProjectInput);
            }

            throw new InvalidOperationException(CliMessages.ProjectSourcePathRequired);
        }

        if (!string.IsNullOrWhiteSpace(normalizedTargetName))
        {
            throw new InvalidOperationException(CliMessages.ProjectTargetNameRequiresProjectInput);
        }

        var sourceFilePath = Path.GetFullPath(normalizedSource, baseDirectory);
        var importResolution = EidosProjectConfigurationLoader.ResolveImportSearchRoots(
            sourceFilePath,
            explicitImportRoots);
        return new ProjectCommandInputResolution(sourceFilePath, importResolution, null);
    }

    public static ProjectCommandInputResolution ResolveDocument(
        string? source,
        string? project,
        string? targetName,
        IReadOnlyList<string>? explicitImportRoots = null,
        string? workingDirectory = null)
    {
        var baseDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(workingDirectory);
        var normalizedProject = NormalizeInput(project);
        var normalizedSource = NormalizeInput(source);
        var normalizedTargetName = NormalizeInput(targetName);

        if (string.IsNullOrWhiteSpace(normalizedProject))
        {
            return Resolve(source, project, targetName, explicitImportRoots, workingDirectory);
        }

        var projectPath = Path.GetFullPath(normalizedProject, baseDirectory);
        var sourceFilePath = string.IsNullOrWhiteSpace(normalizedSource)
            ? null
            : Path.GetFullPath(normalizedSource, baseDirectory);
        var effectiveTargetName = normalizedTargetName ??
                                  TryInferTargetNameForDocument(projectPath, sourceFilePath);
        var projectResolution = ResolveProjectTarget(
            projectPath,
            effectiveTargetName,
            explicitImportRoots);

        if (string.IsNullOrWhiteSpace(normalizedSource) ||
            LooksLikeProjectPath(normalizedSource, baseDirectory))
        {
            return projectResolution;
        }

        if (!string.Equals(
                Path.GetExtension(normalizedSource),
                ".eidos",
                StringComparison.OrdinalIgnoreCase))
        {
            return projectResolution;
        }

        return projectResolution with { SourceFilePath = sourceFilePath! };
    }

    private static string? TryInferTargetNameForDocument(string projectPath, string? sourceFilePath)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath))
        {
            return null;
        }

        try
        {
            var project = EidosProjectConfigurationLoader.LoadFromPath(projectPath);
            string? matchedTargetName = null;
            foreach (var target in project.Configuration.Targets)
            {
                if (!string.Equals(target.Entry, sourceFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (matchedTargetName != null)
                {
                    return null;
                }

                matchedTargetName = target.Name;
            }

            return matchedTargetName;
        }
        catch
        {
            return null;
        }
    }

    private static ProjectCommandInputResolution ResolveProjectTarget(
        string projectPath,
        string? targetName,
        IReadOnlyList<string>? explicitImportRoots)
    {
        var resolvedTarget = EidosProjectGraphResolver.ResolveTarget(
            projectPath,
            targetName,
            explicitImportRoots);
        return new ProjectCommandInputResolution(
            resolvedTarget.EntryFilePath,
            resolvedTarget.ImportResolution,
            resolvedTarget);
    }

    private static bool LooksLikeProjectPath(string? input, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(input, baseDirectory);
            if (Directory.Exists(fullPath))
            {
                return true;
            }

            return string.Equals(
                Path.GetFileName(fullPath),
                EidosProjectConfigurationLoader.DefaultFileName,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? TryFindNearestProjectFile(string startDirectory)
    {
        try
        {
            var directory = Path.GetFullPath(startDirectory);
            while (!string.IsNullOrWhiteSpace(directory))
            {
                var projectFile = Path.Combine(directory, EidosProjectConfigurationLoader.DefaultFileName);
                if (File.Exists(projectFile))
                {
                    return projectFile;
                }

                var parent = Directory.GetParent(directory)?.FullName;
                if (string.IsNullOrWhiteSpace(parent) ||
                    string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                directory = parent;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string? NormalizeInput(string? input)
    {
        return string.IsNullOrWhiteSpace(input)
            ? null
            : input.Trim();
    }
}
