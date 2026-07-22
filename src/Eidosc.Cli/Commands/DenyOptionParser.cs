using Eidosc.Cli.Resources;
using System.CommandLine;

namespace Eidosc.Cli.Commands;

internal static class DenyOptionParser
{
    public static Option<string[]> Create()
    {
        var option = new Option<string[]>("--deny", CliMessages.DenyOptionDescription);
        option.AddValidator(result =>
        {
            var unsupported = EnumerateEntries(result.GetValueOrDefault<string[]>() ?? [])
                .Where(static entry => !string.Equals(entry, "style", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (unsupported.Length > 0)
            {
                result.ErrorMessage = $"Unsupported --deny category: {string.Join(", ", unsupported)}. Supported categories: style.";
            }
        });
        return option;
    }

    public static bool IncludesStyle(IEnumerable<string>? values) =>
        EnumerateEntries(values ?? [])
            .Any(static entry => string.Equals(entry, "style", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> EnumerateEntries(IEnumerable<string> values) =>
        values.SelectMany(static value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
