namespace Eidosup.Installation;

public sealed class DoctorReporter
{
    public void Run(string? installRootOverride)
    {
        var platform = PlatformContext.Detect();
        var eidoscPath = CommandProbe.TryFind(platform.ExecutableName);
        var clangPath = CommandProbe.TryFind(platform.IsWindows ? "clang.exe" : "clang");
        var llcPath = CommandProbe.TryFind(platform.IsWindows ? "llc.exe" : "llc");
        var installRoot = string.IsNullOrWhiteSpace(installRootOverride)
            ? Environment.GetEnvironmentVariable("EIDOS_HOME")
            : Path.GetFullPath(installRootOverride);

        Console.WriteLine($"Platform: {platform.Rid}");
        PrintStatus("eidosc", eidoscPath);
        PrintStatus("clang", clangPath);
        PrintStatus("llc", llcPath);
        PrintValue("EIDOS_HOME", Environment.GetEnvironmentVariable("EIDOS_HOME"));
        PrintValue("EIDOSC_HOME", Environment.GetEnvironmentVariable("EIDOSC_HOME"));
        PrintValue("EIDOS_RUNTIME_PATH", Environment.GetEnvironmentVariable("EIDOS_RUNTIME_PATH"));
        PrintValue("EIDOS_LLVM_HOME", Environment.GetEnvironmentVariable("EIDOS_LLVM_HOME"));

        if (!string.IsNullOrWhiteSpace(installRoot))
        {
            Console.WriteLine($"Install root: {installRoot}");
            if (Directory.Exists(installRoot))
            {
                var versions = Path.Combine(installRoot, "toolchains", "eidosc");
                if (Directory.Exists(versions))
                {
                    foreach (var directory in Directory.EnumerateDirectories(versions).OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"  installed version dir: {directory}");
                    }
                }
            }
        }
    }

    private static void PrintStatus(string label, string? value)
    {
        Console.WriteLine(value == null
            ? $"[missing] {label}"
            : $"[ok] {label}: {value}");
    }

    private static void PrintValue(string label, string? value)
    {
        Console.WriteLine($"{label}: {(string.IsNullOrWhiteSpace(value) ? "<unset>" : value)}");
    }
}
