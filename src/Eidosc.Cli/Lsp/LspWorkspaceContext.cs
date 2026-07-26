using System.Text.Json;
using Eidosc.ProjectSystem;
using Eidosc.Semantic;

namespace Eidosc.Cli.Lsp;

internal sealed class LspWorkspaceContext
{
    private readonly object _sync = new();
    private readonly HashSet<string> _workspaceRoots = new(PathComparer);
    private string? _activeProjectFilePath;

    public void Initialize(JsonElement parameters)
    {
        lock (_sync)
        {
            _workspaceRoots.Clear();
            AddRootUri(parameters, "rootUri");
            AddRootPath(parameters, "rootPath");
            if (parameters.TryGetProperty("workspaceFolders", out var folders) &&
                folders.ValueKind == JsonValueKind.Array)
            {
                foreach (var folder in folders.EnumerateArray())
                {
                    AddRootUri(folder, "uri");
                }
            }

            _activeProjectFilePath = ResolveSingleWorkspaceProjectFile();
        }
    }

    public bool SetActiveProject(JsonElement parameters)
    {
        var candidate = TryReadFileUri(parameters, "projectUri") ??
                        TryReadString(parameters, "projectPath");
        var projectFilePath = TryResolveProjectFile(candidate);
        lock (_sync)
        {
            if (PathComparer.Equals(_activeProjectFilePath, projectFilePath))
            {
                return false;
            }

            _activeProjectFilePath = projectFilePath;
            return true;
        }
    }

    public bool UpdateWorkspaceFolders(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("event", out var workspaceEvent) ||
            workspaceEvent.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        lock (_sync)
        {
            var changed = false;
            if (workspaceEvent.TryGetProperty("removed", out var removed) &&
                removed.ValueKind == JsonValueKind.Array)
            {
                foreach (var folder in removed.EnumerateArray())
                {
                    var path = TryReadFileUri(folder, "uri");
                    if (path != null)
                    {
                        changed |= _workspaceRoots.Remove(NormalizePath(path));
                    }
                }
            }

            if (workspaceEvent.TryGetProperty("added", out var added) &&
                added.ValueKind == JsonValueKind.Array)
            {
                foreach (var folder in added.EnumerateArray())
                {
                    var path = TryReadFileUri(folder, "uri");
                    if (path != null)
                    {
                        changed |= _workspaceRoots.Add(NormalizePath(path));
                    }
                }
            }

            if (_activeProjectFilePath == null || !IsWithinAnyWorkspaceRoot(_activeProjectFilePath))
            {
                var projectFile = ResolveSingleWorkspaceProjectFile();
                if (!PathComparer.Equals(_activeProjectFilePath, projectFile))
                {
                    _activeProjectFilePath = projectFile;
                    changed = true;
                }
            }

            return changed;
        }
    }

    public string? ResolveProjectFilePath(string documentFilePath)
    {
        if (CompilerOwnedSourceGrant.IsTrustedStdlibSourcePath(documentFilePath))
        {
            return null;
        }

        var nearest = EidosProjectConfigurationLoader.TryLoadNearest(documentFilePath)?.FilePath;
        if (!string.IsNullOrWhiteSpace(nearest))
        {
            return nearest;
        }

        lock (_sync)
        {
            return _activeProjectFilePath;
        }
    }

    private void AddRootUri(JsonElement owner, string propertyName)
    {
        var path = TryReadFileUri(owner, propertyName);
        if (path != null)
        {
            _workspaceRoots.Add(NormalizePath(path));
        }
    }

    private void AddRootPath(JsonElement owner, string propertyName)
    {
        var path = TryReadString(owner, propertyName);
        if (!string.IsNullOrWhiteSpace(path))
        {
            _workspaceRoots.Add(NormalizePath(path));
        }
    }

    private string? ResolveSingleWorkspaceProjectFile()
    {
        var projects = _workspaceRoots
            .Select(TryResolveProjectFile)
            .Where(static path => path != null)
            .Distinct(PathComparer)
            .ToArray();
        return projects.Length == 1 ? projects[0] : null;
    }

    private bool IsWithinAnyWorkspaceRoot(string path)
    {
        var normalizedPath = NormalizePath(path);
        return _workspaceRoots.Any(root =>
            PathComparer.Equals(root, normalizedPath) ||
            normalizedPath.StartsWith(
                root + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
    }

    private static string? TryResolveProjectFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var normalized = Path.GetFullPath(path);
            if (string.Equals(
                    Path.GetFileName(normalized),
                    EidosProjectConfigurationLoader.DefaultFileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return File.Exists(normalized) ? normalized : null;
            }

            var manifest = Path.Combine(normalized, EidosProjectConfigurationLoader.DefaultFileName);
            if (File.Exists(manifest))
            {
                return manifest;
            }

            return EidosProjectConfigurationLoader.TryLoadNearest(normalized)?.FilePath;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? TryReadFileUri(JsonElement owner, string propertyName)
    {
        var value = TryReadString(owner, propertyName);
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.IsFile)
        {
            return null;
        }

        return uri.LocalPath;
    }

    private static string? TryReadString(JsonElement owner, string propertyName) =>
        owner.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
