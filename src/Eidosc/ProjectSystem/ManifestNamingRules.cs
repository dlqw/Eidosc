using System.Text;

namespace Eidosc.ProjectSystem;

/// <summary>
/// Naming rules for manifest identities. Manifest strings are not Eidos
/// identifiers: package ids use lower-kebab segments while dependency aliases
/// use lower_snake_case and are converted to the corresponding import root.
/// </summary>
public static class ManifestNamingRules
{
    public sealed record Diagnostic(
        string Code,
        string Message,
        string Subject,
        string? SuggestedName);

    public static IReadOnlyList<Diagnostic> Analyze(
        EidosProjectManifestDocument manifest,
        string projectDirectory)
    {
        var diagnostics = new List<Diagnostic>();
        if (manifest.Package?.Name is { Length: > 0 } packageName && !IsPackageId(packageName))
        {
            diagnostics.Add(new Diagnostic(
                "S1107",
                $"package id '{packageName}' must use lower-kebab-case dot-separated segments",
                packageName,
                NormalizePackageId(packageName)));
        }

        var aliases = manifest.Dependencies is { } dependencies
            ? dependencies.Keys.AsEnumerable()
            : Array.Empty<string>();
        foreach (var alias in aliases)
        {
            if (!IsDependencyAlias(alias))
            {
                diagnostics.Add(new Diagnostic(
                    "S1108",
                    $"dependency alias '{alias}' must use lower_snake_case",
                    alias,
                    NormalizeDependencyAlias(alias)));
            }
        }

        var roots = manifest.SourceRoots is { Length: > 0 } ? manifest.SourceRoots : ["src"];
        foreach (var root in roots)
        {
            if (!IsRelativePathCanonical(root))
            {
                diagnostics.Add(new Diagnostic(
                    "S1110",
                    $"source root '{root}' must use lower_snake_case path segments",
                    root,
                    NormalizePath(root)));
                continue;
            }

            var absoluteRoot = Path.GetFullPath(root, projectDirectory);
            if (!Directory.Exists(absoluteRoot))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(absoluteRoot, "*.eidos", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(absoluteRoot, file);
                var fileName = Path.GetFileName(relative);
                if (!IsModuleFileName(fileName))
                {
                    var expected = NormalizeDependencyAlias(Path.GetFileNameWithoutExtension(fileName)) + ".eidos";
                    diagnostics.Add(new Diagnostic(
                        "S1105",
                        $"module file '{fileName}' must use lower_snake_case",
                        fileName,
                        expected));
                }

                var directory = Path.GetDirectoryName(relative);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                foreach (var segment in directory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                {
                    if (!IsModuleSegment(segment))
                    {
                        diagnostics.Add(new Diagnostic(
                            "S1110",
                            $"module directory '{segment}' must use lower_snake_case",
                            segment,
                            NormalizeDependencyAlias(segment)));
                    }
                }
            }
        }

        return diagnostics;
    }

    public static bool IsPackageId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var segments = value.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments.All(IsPackageSegment);
    }

    public static bool IsDependencyAlias(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value[0] == '_' || value[^1] == '_')
        {
            return false;
        }

        return value.All(static character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
    }

    public static bool IsModuleSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value[0] == '_' || value[^1] == '_')
        {
            return false;
        }

        return value.All(static character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
    }

    public static bool IsModuleFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(Path.GetExtension(value), ".eidos", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsModuleSegment(Path.GetFileNameWithoutExtension(value));
    }

    public static string NormalizeDependencyAlias(string value)
    {
        var builder = new StringBuilder(value.Length + 4);
        var previousWasSeparator = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character is '-' or ' ' or '.')
            {
                if (!previousWasSeparator && builder.Length > 0)
                {
                    builder.Append('_');
                }

                previousWasSeparator = true;
                continue;
            }

            if (char.IsUpper(character))
            {
                if (builder.Length > 0 && !previousWasSeparator)
                {
                    var previous = value[index - 1];
                    var nextIsLower = index + 1 < value.Length && char.IsLower(value[index + 1]);
                    if (char.IsLower(previous) || char.IsDigit(previous) || nextIsLower)
                    {
                        builder.Append('_');
                    }
                }

                builder.Append(char.ToLowerInvariant(character));
                previousWasSeparator = false;
                continue;
            }

            builder.Append(char.ToLowerInvariant(character));
            previousWasSeparator = false;
        }

        return builder.ToString().Trim('_');
    }

    public static string NormalizeModulePathSegment(string value)
    {
        var physicalName = NormalizeDependencyAlias(value);
        return string.Concat(physicalName
            .Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(static word => char.ToUpperInvariant(word[0]) + word[1..]));
    }

    public static string NormalizePackageId(string value)
    {
        return string.Join('.', value
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizePackageSegment));
    }

    private static bool IsRelativePathCanonical(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
        {
            return false;
        }

        return value
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .All(IsModuleSegment);
    }

    private static string NormalizePath(string value) => string.Join(
        '/',
        value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeDependencyAlias));

    private static bool IsPackageSegment(string segment)
    {
        if (segment.Length == 0 || segment[0] == '-' || segment[^1] == '-')
        {
            return false;
        }

        return segment.All(static character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
    }

    private static string NormalizePackageSegment(string segment)
    {
        var normalized = NormalizeDependencyAlias(segment)
            .Replace('_', '-')
            .Trim('-');
        return normalized;
    }
}
