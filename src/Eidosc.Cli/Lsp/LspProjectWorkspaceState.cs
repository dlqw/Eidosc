using Eidosc.Cli.Commands;
using Eidosc.Semantic;

namespace Eidosc.Cli.Lsp;

internal sealed record LspProjectWorkspaceState(
    string Key,
    string[] Roots,
    string[] IndexedFiles,
    string StdlibImageFingerprint)
{
    public static LspProjectWorkspaceState Create(
        string filePath,
        ProjectCommandInputResolution inputResolution,
        IReadOnlyDictionary<string, string[]> packageImportRoots,
        IReadOnlyList<string> explicitImportRoots,
        LspDependencyFingerprintCache fingerprintCache)
    {
        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? Directory.GetCurrentDirectory();
        var roots = new List<string>();
        AddRoots(roots, inputResolution.ProjectTarget?.EffectiveSearchRoots ??
                        inputResolution.ImportResolution.EffectiveSearchRoots,
            Directory.GetCurrentDirectory());
        foreach (var packageRoots in packageImportRoots.Values)
        {
            AddRoots(roots, packageRoots, Directory.GetCurrentDirectory());
        }
        AddRoots(roots, explicitImportRoots, baseDirectory);

        var normalizedRoots = roots
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static root => root, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var indexedFiles = normalizedRoots
            .SelectMany(root => fingerprintCache.GetIndexedFiles(root, Directory.GetCurrentDirectory()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var keyParts = new[]
        {
            inputResolution.GetLanguageVersion(),
            inputResolution.ImportResolution.ProjectFilePath ?? "",
            inputResolution.ProjectTarget?.TargetName ?? "",
            string.Join("|", normalizedRoots),
            PrecompiledModuleRegistry.GetStdlibImageFingerprint()
        };

        return new LspProjectWorkspaceState(
            string.Join("\n", keyParts),
            normalizedRoots,
            indexedFiles,
            PrecompiledModuleRegistry.GetStdlibImageFingerprint());
    }

    private static void AddRoots(List<string> roots, IEnumerable<string>? values, string baseDirectory)
    {
        if (values == null)
        {
            return;
        }

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            roots.Add(Path.GetFullPath(Path.IsPathRooted(value)
                ? value
                : Path.Combine(baseDirectory, value)));
        }
    }
}
