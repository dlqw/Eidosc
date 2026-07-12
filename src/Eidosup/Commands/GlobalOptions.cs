using System.CommandLine;

namespace Eidosup.Commands;

internal static class GlobalOptions
{
    public static Option<bool> Verbose { get; } = new(["--verbose", "-v"], "Include detailed diagnostics for failures.");

    public static Option<bool> Json { get; } = new("--json", "Write machine-readable output when supported.");
}
