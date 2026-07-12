namespace Eidosc.Cli.Commands;

internal static class CliFormatters
{
    public static string FormatBytes(long bytes)
    {
        const long kib = 1024;
        const long mib = kib * 1024;
        const long gib = mib * 1024;

        return bytes switch
        {
            >= gib => $"{bytes / (double)gib:F2} GiB",
            >= mib => $"{bytes / (double)mib:F2} MiB",
            >= kib => $"{bytes / (double)kib:F2} KiB",
            _ => $"{bytes} B"
        };
    }

    public static string FormatSignedBytes(long bytes)
    {
        if (bytes == 0)
        {
            return "0 B";
        }

        var prefix = bytes > 0 ? "+" : "-";
        return prefix + FormatBytes(Math.Abs(bytes));
    }
}
