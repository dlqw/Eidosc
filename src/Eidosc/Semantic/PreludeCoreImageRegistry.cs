using Eidosc.Utils;
using Eidosc.ProjectSystem;

namespace Eidosc.Semantic;

public static class PreludeCoreImageRegistry
{
    internal const string PackageAlias = "__eidos_prelude_core";

    private static readonly HashSet<string> CoreModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "Alternative",
        "Applicative",
        "Either",
        "Display",
        "Foldable",
        "Functions",
        "Functor",
        "Monad",
        "Monoid",
        "Option",
        "Ordering",
        "Predicate",
        "Prelude",
        "Result",
        "RuntimeArray",
        "Semigroup",
        "Seq",
        "TraitInvoke",
        "Traits",
        "Traversable"
    };

    public static IReadOnlyList<string> GetAvailableModulePaths() =>
        CoreModules.Order(StringComparer.Ordinal).ToArray();

    internal static bool IsCoreModuleName(string moduleName) => CoreModules.Contains(moduleName);

    public static bool TryGetSource(IReadOnlyList<string> effectiveModulePath, out string source)
    {
        source = string.Empty;
        return TryGetCoreModuleName(effectiveModulePath, out var moduleName) &&
               PrecompiledModuleRegistry.TryGetDistributionSource([WellKnownStrings.Std.Module, moduleName], out source);
    }

    public static bool TryGetSourceFilePath(IReadOnlyList<string> effectiveModulePath, out string filePath)
    {
        filePath = string.Empty;
        return TryGetCoreModuleName(effectiveModulePath, out var moduleName) &&
               PrecompiledModuleRegistry.TryGetDistributionSourceFilePath([WellKnownStrings.Std.Module, moduleName], out filePath);
    }

    public static bool TryGetModulePathFromSourcePath(string? path, out string[] effectiveModulePath)
    {
        effectiveModulePath = [];
        if (!PrecompiledModuleRegistry.TryGetModulePathFromSourcePath(path, out var distributionModulePath))
        {
            return false;
        }

        var segments = distributionModulePath.Split(
            WellKnownStrings.Operators.Divide,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length != 2 ||
            !string.Equals(segments[0], WellKnownStrings.Std.Module, StringComparison.OrdinalIgnoreCase) ||
            !CoreModules.Contains(segments[1]))
        {
            return false;
        }

        effectiveModulePath = [PackageAlias, segments[1]];
        return true;
    }

    public static string GetCoreImageFingerprint()
    {
        var builder = new System.Text.StringBuilder("prelude-core-image-v1:");
        foreach (var moduleName in CoreModules.Order(StringComparer.Ordinal))
        {
            if (PrecompiledModuleRegistry.TryGetDistributionSource([WellKnownStrings.Std.Module, moduleName], out var source))
            {
                builder.Append(moduleName)
                    .Append(':')
                    .AppendLine(ContentHash.ComputeHash(source));
            }
        }

        return ContentHash.ComputeHash(builder.ToString());
    }

    private static bool TryGetCoreModuleName(IReadOnlyList<string> effectiveModulePath, out string moduleName)
    {
        moduleName = string.Empty;
        if (effectiveModulePath.Count != 2 ||
            !string.Equals(effectiveModulePath[0], PackageAlias, StringComparison.Ordinal))
        {
            return false;
        }

        moduleName = effectiveModulePath[1];
        return CoreModules.Contains(moduleName);
    }
}
