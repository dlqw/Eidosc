namespace Eidosc.Symbols;

/// <summary>
/// Stable identity for a module inside a concrete package instance.
/// </summary>
public sealed record ModuleIdentity
{
    public static readonly string CurrentPackageInstanceKey = "current";

    public string? PackageAlias { get; init; }

    public string PackageInstanceKey { get; init; } = CurrentPackageInstanceKey;

    public IReadOnlyList<string> ModulePath { get; init; } = [];

    public static ModuleIdentity Create(
        string? packageAlias,
        string? packageInstanceKey,
        IReadOnlyList<string> modulePath)
    {
        return new ModuleIdentity
        {
            PackageAlias = string.IsNullOrWhiteSpace(packageAlias) ? null : packageAlias,
            PackageInstanceKey = string.IsNullOrWhiteSpace(packageInstanceKey)
                ? CreateDefaultPackageInstanceKey(packageAlias)
                : packageInstanceKey,
            ModulePath = modulePath
                .Where(static segment => !string.IsNullOrWhiteSpace(segment))
                .ToArray()
        };
    }

    public string ToIdentityKey()
    {
        var modulePath = string.Join(WellKnownStrings.Operators.Divide, ModulePath);
        var packagePart = string.IsNullOrWhiteSpace(PackageAlias)
            ? CurrentPackageInstanceKey
            : PackageAlias;
        return $"{packagePart}@{PackageInstanceKey}{WellKnownStrings.Separators.Path}{modulePath}";
    }

    public string ToDisplayKey()
    {
        var modulePath = string.Join(WellKnownStrings.Operators.Divide, ModulePath);
        return string.IsNullOrWhiteSpace(PackageAlias)
            ? modulePath
            : $"{PackageAlias}{WellKnownStrings.Separators.Path}{modulePath}";
    }

    public string ToFunctionModuleName()
    {
        return ToDisplayKey();
    }

    private static string CreateDefaultPackageInstanceKey(string? packageAlias)
    {
        return string.IsNullOrWhiteSpace(packageAlias)
            ? CurrentPackageInstanceKey
            : $"alias:{packageAlias}";
    }
}
