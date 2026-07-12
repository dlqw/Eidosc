namespace Eidosup.Installation;

public static class CommandProbe
{
    public static string? TryFind(string commandName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(entry, commandName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (OperatingSystem.IsWindows())
            {
                var windowsCandidate = candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? candidate
                    : $"{candidate}.exe";
                if (File.Exists(windowsCandidate))
                {
                    return windowsCandidate;
                }
            }
        }

        return null;
    }
}
