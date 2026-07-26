using Eidosc.Utils;

namespace Eidosc.Semantic;

internal sealed class CompilerOwnedSourceGrant
{
    private readonly HashSet<string> _sourcePaths;

    private CompilerOwnedSourceGrant(IEnumerable<string> sourcePaths)
    {
        _sourcePaths = sourcePaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static CompilerOwnedSourceGrant None { get; } = new([]);

    internal static CompilerOwnedSourceGrant Create(IEnumerable<string> sourcePaths) => new(sourcePaths);

    public bool Allows(SourceSpan span) =>
        !string.IsNullOrWhiteSpace(span.FilePath) && _sourcePaths.Contains(Normalize(span.FilePath!));

    internal static bool IsVerifiedStdlibSource(string? sourcePath, string sourceText)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return false;
        }

        if (PreludeCoreImageRegistry.TryGetModulePathFromSourcePath(sourcePath, out var coreModulePath) &&
            PreludeCoreImageRegistry.TryGetSource(coreModulePath, out var coreSource) &&
            string.Equals(sourceText, coreSource, StringComparison.Ordinal))
        {
            return true;
        }

        return PrecompiledModuleRegistry.TryGetModulePathFromSourcePath(sourcePath, out var modulePath) &&
               PrecompiledModuleRegistry.TryGetSource(modulePath, out var registeredSource) &&
               string.Equals(sourceText, registeredSource, StringComparison.Ordinal);
    }

    internal static bool IsTrustedStdlibSourcePath(string? sourcePath)
    {
        return PrecompiledModuleRegistry.IsTrustedStdlibSourcePath(sourcePath);
    }

    private static string Normalize(string path)
    {
        if (path.StartsWith('<') && path.EndsWith('>'))
        {
            return path;
        }

        try
        {
            return Path.GetFullPath(path).Replace('\\', '/');
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return path.Replace('\\', '/');
        }
    }
}
