using System.CommandLine;

namespace Eidosup.Commands;

internal static class GlobalOptions
{
    static GlobalOptions()
    {
        Color.AddValidator(result =>
        {
            var value = result.GetValueOrDefault<string>();
            if (value is not ("auto" or "always" or "never"))
            {
                result.ErrorMessage = "--color must be auto, always, or never.";
            }
        });
    }

    public static Option<bool> Verbose { get; } = new(["--verbose", "-v"], "Include detailed diagnostics for failures.");

    public static Option<bool> Json { get; } = new("--json", "Write machine-readable output when supported.");

    public static Option<bool> Quiet { get; } = new(["--quiet", "-q"], "Suppress non-error human-readable output.");

    public static Option<string> Color { get; } = new("--color", () => "auto", "Color mode: auto, always, or never.");
}
