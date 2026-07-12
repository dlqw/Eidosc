namespace Eidosc.Cli.Commands;

internal static class WarningOptionParser
{
    public static HashSet<string> ParseWarningCodes(IEnumerable<string>? rawValues)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (rawValues == null)
        {
            return result;
        }

        foreach (var raw in rawValues)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var tokens = raw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var token in tokens)
            {
                if (!string.IsNullOrWhiteSpace(token))
                {
                    result.Add(token);
                }
            }
        }

        return result;
    }
}
