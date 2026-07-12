using System.Runtime.InteropServices;

namespace Eidosup.Installation;

public sealed record PlatformContext(
    string Rid,
    string ExecutableName,
    bool IsWindows,
    bool IsLinux,
    bool IsMacOs)
{
    public static readonly IReadOnlyList<string> SupportedRids =
    [
        "win-x64",
        "win-arm64",
        "linux-x64",
        "linux-arm64",
        "osx-x64",
        "osx-arm64"
    ];

    public static bool IsSupportedRid(string? rid) => SupportedRids.Contains(rid, StringComparer.Ordinal);

    public static PlatformContext Detect()
    {
        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            _ => throw new PlatformNotSupportedException($"Unsupported CPU architecture: {RuntimeInformation.OSArchitecture}.")
        };

        if (OperatingSystem.IsWindows())
        {
            return new PlatformContext($"win-{architecture}", "eidosc.exe", true, false, false);
        }

        if (OperatingSystem.IsLinux())
        {
            return new PlatformContext($"linux-{architecture}", "eidosc", false, true, false);
        }

        if (OperatingSystem.IsMacOS())
        {
            return new PlatformContext($"osx-{architecture}", "eidosc", false, false, true);
        }

        throw new PlatformNotSupportedException($"Unsupported operating system: {RuntimeInformation.OSDescription}.");
    }

    public string GetPathVariableSeparator() => IsWindows ? ";" : ":";
}
