namespace Eidosc.Cli.Commands;

public static partial class BuildCommand
{
    internal const int DevAutoObjectGroupCap = 64;

    internal static int ResolveOptimizationLevel(BuildMode buildMode, int? requestedOptimizationLevel)
    {
        return requestedOptimizationLevel ?? (buildMode == BuildMode.Dev ? 0 : 2);
    }

    internal static string NormalizeCodegenMode(string? value)
    {
        return string.Equals(value, NativeCodegenModes.ObjectGroups, StringComparison.OrdinalIgnoreCase)
            ? NativeCodegenModes.ObjectGroups
            : string.Equals(value, NativeCodegenModes.Auto, StringComparison.OrdinalIgnoreCase)
                ? NativeCodegenModes.Auto
                : NativeCodegenModes.FullModule;
    }

    internal static string ResolveNativeCodegenMode(BuildMode buildMode, string? value)
    {
        var normalized = NormalizeCodegenMode(value);
        if (!string.Equals(normalized, NativeCodegenModes.Auto, StringComparison.Ordinal))
        {
            return normalized;
        }

        return buildMode == BuildMode.Dev
            ? NativeCodegenModes.ObjectGroups
            : NativeCodegenModes.FullModule;
    }

    internal static int ResolveMaxObjectGroups(BuildMode buildMode, string? requestedCodegenMode, int requestedMaxObjectGroups)
    {
        var normalized = NormalizeCodegenMode(requestedCodegenMode);
        if (requestedMaxObjectGroups > 0 ||
            !string.Equals(normalized, NativeCodegenModes.Auto, StringComparison.Ordinal) ||
            buildMode != BuildMode.Dev)
        {
            return Math.Max(0, requestedMaxObjectGroups);
        }

        return DevAutoObjectGroupCap;
    }

    internal static bool IsSupportedNativeCodegenMode(string? value)
    {
        return string.Equals(value, NativeCodegenModes.Auto, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, NativeCodegenModes.FullModule, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, NativeCodegenModes.ObjectGroups, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsObjectGroupsCodegenMode(string? value)
    {
        return string.Equals(NormalizeCodegenMode(value), NativeCodegenModes.ObjectGroups, StringComparison.Ordinal);
    }

    internal static class NativeCodegenModes
    {
        public const string Auto = "auto";
        public const string FullModule = "full-module";
        public const string ObjectGroups = "object-groups";
    }
}
