using Eidosc.ProjectSystem;
using Eidosc.Cli.Resources;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;

namespace Eidosc.Cli.Commands;

internal static class ProjectImportResolutionCli
{
    public static void WriteSummary(
        ProjectImportSearchResolution resolution,
        ResolvedEidosProjectTarget? projectTarget,
        bool useColors)
    {
        if (!string.IsNullOrWhiteSpace(resolution.ProjectFilePath))
        {
            CliOutput.WriteStatus(
                DiagnosticLevel.Note,
                CliMessages.ProjectConfigSummary(resolution.ProjectFilePath),
                useColors);
        }

        if (resolution.SourceSearchRoots.Length > 0)
        {
            CliOutput.WriteStatus(
                DiagnosticLevel.Note,
                CliMessages.ProjectSourceRootsSummary(string.Join(", ", resolution.SourceSearchRoots)),
                useColors);
        }

        if (resolution.ImportSearchRoots.Length > 0)
        {
            CliOutput.WriteStatus(
                DiagnosticLevel.Note,
                CliMessages.ProjectImportRootsSummary(string.Join(", ", resolution.ImportSearchRoots)),
                useColors);
        }

        if (projectTarget == null)
        {
            return;
        }

        CliOutput.WriteStatus(
            DiagnosticLevel.Note,
            CliMessages.ProjectEntryTargetSummary(projectTarget.TargetName, projectTarget.Kind),
            useColors);
        CliOutput.WriteStatus(
            DiagnosticLevel.Note,
            CliMessages.ProjectTargetEntrySummary(projectTarget.EntryFilePath),
            useColors);

        if (projectTarget.TargetDependencies.Length > 0)
        {
            CliOutput.WriteStatus(
                DiagnosticLevel.Note,
                CliMessages.ProjectTargetDependenciesSummary(string.Join(", ", projectTarget.TargetDependencies)),
                useColors);
        }

        if (projectTarget.ProjectDependencies.Length > 0)
        {
            var dependencySummary = projectTarget.ProjectDependencies
                .Select(dependency => $"{dependency.Name} -> {FormatProjectTarget(dependency.ProjectDirectory, dependency.TargetName)}");
            CliOutput.WriteStatus(
                DiagnosticLevel.Note,
                CliMessages.ProjectDependenciesSummary(string.Join(", ", dependencySummary)),
                useColors);
        }

        if (projectTarget.DependencySearchRoots.Length > 0)
        {
            CliOutput.WriteStatus(
                DiagnosticLevel.Note,
                CliMessages.ProjectDependencySearchRootsSummary(string.Join(", ", projectTarget.DependencySearchRoots)),
                useColors);
        }

        var graphNodes = projectTarget.BuildGraph.Nodes
            .Select(node => FormatProjectTarget(node.ProjectDirectory, node.TargetName));
        CliOutput.WriteStatus(
            DiagnosticLevel.Note,
            CliMessages.ProjectBuildGraphSummary(string.Join(" -> ", graphNodes)),
            useColors);
    }

    private static string FormatProjectTarget(string projectDirectory, string targetName)
    {
        var projectName = Path.GetFileName(projectDirectory);
        return string.IsNullOrWhiteSpace(projectName)
            ? targetName
            : $"{projectName}::{targetName}";
    }
}
