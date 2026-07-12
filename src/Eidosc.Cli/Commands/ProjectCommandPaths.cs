using Eidosc.CodeGen;
using Eidosc.Pipeline;

namespace Eidosc.Cli.Commands;

internal static class ProjectCommandPaths
{
    public static string ResolveDebugOutputPath(
        string? requestedDebugRoot,
        ProjectCommandInputResolution inputResolution)
    {
        var root = !string.IsNullOrWhiteSpace(requestedDebugRoot)
            ? Path.GetFullPath(requestedDebugRoot)
            : GetDefaultDebugRoot(inputResolution);

        return Path.Combine(root, GetArtifactStem(inputResolution));
    }

    public static string ResolveNativeOutputPath(
        string? requestedOutputPath,
        ProjectCommandInputResolution inputResolution,
        TargetInfo targetInfo)
    {
        if (!string.IsNullOrWhiteSpace(requestedOutputPath))
        {
            return Path.GetFullPath(requestedOutputPath);
        }

        var projectDirectory = ResolveProjectDirectory(inputResolution);
        if (projectDirectory == null)
        {
            return Path.ChangeExtension(inputResolution.SourceFilePath, targetInfo.ExecutableExtension);
        }

        return Path.Combine(
            projectDirectory,
            "build",
            GetArtifactStem(inputResolution) + targetInfo.ExecutableExtension);
    }

    public static string ResolveLlvmIrOutputPath(
        string? requestedOutputPath,
        ProjectCommandInputResolution inputResolution)
    {
        if (!string.IsNullOrWhiteSpace(requestedOutputPath))
        {
            return Path.GetFullPath(requestedOutputPath);
        }

        var projectDirectory = ResolveProjectDirectory(inputResolution);
        if (projectDirectory == null)
        {
            return Path.ChangeExtension(inputResolution.SourceFilePath, ".ll");
        }

        return Path.Combine(projectDirectory, "build", GetArtifactStem(inputResolution) + ".ll");
    }

    public static string? ResolveProjectDirectory(ProjectCommandInputResolution inputResolution)
    {
        if (inputResolution.ProjectTarget != null)
        {
            return inputResolution.ProjectTarget.ProjectDirectory;
        }

        if (!string.IsNullOrWhiteSpace(inputResolution.ImportResolution.ProjectFilePath))
        {
            return Path.GetDirectoryName(inputResolution.ImportResolution.ProjectFilePath);
        }

        return TryInferProjectDirectoryFromSourceRoot(inputResolution.SourceFilePath);
    }

    private static string GetDefaultDebugRoot(ProjectCommandInputResolution inputResolution)
    {
        var projectDirectory = ResolveProjectDirectory(inputResolution);
        return projectDirectory == null
            ? Path.GetFullPath("debug")
            : Path.Combine(projectDirectory, "debug");
    }

    private static string GetArtifactStem(ProjectCommandInputResolution inputResolution)
    {
        if (inputResolution.ProjectTarget != null)
        {
            return SanitizePathSegment(inputResolution.ProjectTarget.TargetName);
        }

        return Path.GetFileNameWithoutExtension(inputResolution.SourceFilePath);
    }

    private static string? TryInferProjectDirectoryFromSourceRoot(string sourceFilePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(sourceFilePath));
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var name = Path.GetFileName(directory);
            if (string.Equals(name, "src", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "source", StringComparison.OrdinalIgnoreCase))
            {
                return Directory.GetParent(directory)?.FullName;
            }

            var parent = Directory.GetParent(directory)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            directory = parent;
        }

        return null;
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "debug";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }
}
