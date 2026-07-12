using System.CommandLine;
using Eidosc.Cli.Resources;
using Eidosc.ProjectSystem;

namespace Eidosc.Cli.Commands;

internal sealed record ProjectCommandSourceInput(
    ProjectCommandInputResolution InputResolution,
    string SourceText,
    bool IsInMemorySource)
{
    public string SourceFilePath => InputResolution.SourceFilePath;
}

internal static class ProjectCommandSourceInputResolver
{
    private const string DefaultVirtualSourceFileName = "inline.eidos";

    public static Option<string> CreateSourceTextOption() =>
        new("--source-text", CliMessages.SourceTextOptionDescription);

    public static Option<bool> CreateStdinOption() =>
        new("--stdin", CliMessages.SourceStdinOptionDescription);

    public static async Task<ProjectCommandSourceInput> ResolveAndLoadAsync(
        string? source,
        string? project,
        string? targetName,
        IReadOnlyList<string>? explicitImportRoots,
        string? sourceText,
        bool stdin,
        TextReader? stdinReader = null)
    {
        if (sourceText != null && stdin)
        {
            throw new InvalidOperationException(CliMessages.SourceTextAndStdinMutuallyExclusive);
        }

        if (sourceText != null)
        {
            return ResolveInMemory(source, project, targetName, explicitImportRoots, sourceText);
        }

        if (stdin)
        {
            var stdinText = await (stdinReader ?? Console.In).ReadToEndAsync();
            return ResolveInMemory(source, project, targetName, explicitImportRoots, stdinText);
        }

        var inputResolution = ProjectCommandInputResolver.Resolve(
            source,
            project,
            targetName,
            explicitImportRoots);

        if (!File.Exists(inputResolution.SourceFilePath))
        {
            throw new FileNotFoundException(CliMessages.SourceFileNotFound(inputResolution.SourceFilePath));
        }

        var fileSourceText = await File.ReadAllTextAsync(inputResolution.SourceFilePath);
        return new ProjectCommandSourceInput(inputResolution, fileSourceText, IsInMemorySource: false);
    }

    private static ProjectCommandSourceInput ResolveInMemory(
        string? source,
        string? project,
        string? targetName,
        IReadOnlyList<string>? explicitImportRoots,
        string sourceText)
    {
        var inputResolution = HasContextInput(source, project, targetName)
            ? ProjectCommandInputResolver.Resolve(source, project, targetName, explicitImportRoots)
            : ResolveDefaultVirtualInput(explicitImportRoots);

        return new ProjectCommandSourceInput(inputResolution, sourceText, IsInMemorySource: true);
    }

    private static ProjectCommandInputResolution ResolveDefaultVirtualInput(
        IReadOnlyList<string>? explicitImportRoots)
    {
        var sourceFilePath = Path.GetFullPath(DefaultVirtualSourceFileName);
        var importResolution = EidosProjectConfigurationLoader.ResolveImportSearchRoots(
            sourceFilePath,
            explicitImportRoots);
        return new ProjectCommandInputResolution(sourceFilePath, importResolution, null);
    }

    private static bool HasContextInput(string? source, string? project, string? targetName) =>
        !string.IsNullOrWhiteSpace(source) ||
        !string.IsNullOrWhiteSpace(project) ||
        !string.IsNullOrWhiteSpace(targetName);
}
