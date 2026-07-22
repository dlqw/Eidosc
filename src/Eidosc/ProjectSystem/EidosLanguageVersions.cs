namespace Eidosc.ProjectSystem;

public static class EidosLanguageVersions
{
    public const string Legacy = "legacy";
    public const string PreviousPrevious = "0.5.0-alpha.1";
    public const string Previous = "0.6.0-alpha.1";
    public const string Current = "0.7.0-alpha.1";

    public static string DefaultForNewProjects => Current;

    public static string DefaultForExistingProjects => Current;

    public static bool IsSupported(string value)
    {
        return string.Equals(value, Current, StringComparison.Ordinal);
    }

    public static bool IsMigrationVersion(string value)
    {
        return string.Equals(value, Legacy, StringComparison.Ordinal) ||
               string.Equals(value, PreviousPrevious, StringComparison.Ordinal) ||
               string.Equals(value, Previous, StringComparison.Ordinal) ||
               IsSupported(value);
    }

    public static string Normalize(string? value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (!IsSupported(normalized))
        {
            throw new InvalidOperationException($"Unsupported Eidos language version '{normalized}'.");
        }

        return normalized;
    }
}
