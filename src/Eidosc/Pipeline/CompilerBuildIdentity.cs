using System.Reflection;

namespace Eidosc.Pipeline;

public static class CompilerBuildIdentity
{
    private static readonly Lazy<string> CurrentValue = new(CreateCurrent);

    public static string Current => CurrentValue.Value;

    private static string CreateCurrent()
    {
        var assembly = typeof(CompilerBuildIdentity).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "unknown";
        return $"{informationalVersion}+{assembly.ManifestModule.ModuleVersionId:N}";
    }
}
