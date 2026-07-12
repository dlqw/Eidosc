namespace Eidosup.Installation;

public static class ProfileScriptWriter
{
    private const string BeginMarker = "# >>> eidosup >>>";
    private const string EndMarker = "# <<< eidosup <<<";

    public static string BuildUnixProfileBlock(EnvironmentPlan plan)
    {
        var lines = new List<string>
        {
            BeginMarker,
            $"export EIDOS_HOME=\"{EscapeForUnix(plan.EidosHome)}\"",
            $"export EIDOSC_HOME=\"{EscapeForUnix(plan.EidoscHome)}\"",
            $"export EIDOS_RUNTIME_PATH=\"{EscapeForUnix(plan.RuntimePath)}\""
        };

        if (!string.IsNullOrWhiteSpace(plan.LlvmHome))
        {
            lines.Add($"export EIDOS_LLVM_HOME=\"{EscapeForUnix(plan.LlvmHome!)}\"");
        }

        var uniqueEntries = plan.PathEntries.Distinct(StringComparer.Ordinal).ToArray();
        if (uniqueEntries.Length > 0)
        {
            var joined = string.Join(':', uniqueEntries.Select(EscapeForUnix));
            lines.Add($"export PATH=\"{joined}:$PATH\"");
        }

        lines.Add(EndMarker);
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    public static string UpsertBlock(string existingContent, string newBlock)
    {
        var beginIndex = existingContent.IndexOf(BeginMarker, StringComparison.Ordinal);
        var endIndex = beginIndex >= 0
            ? existingContent.IndexOf(EndMarker, beginIndex, StringComparison.Ordinal)
            : -1;
        if (beginIndex >= 0 && endIndex > beginIndex)
        {
            var endMarkerEnd = endIndex + EndMarker.Length;
            while (endMarkerEnd < existingContent.Length &&
                   (existingContent[endMarkerEnd] == '\r' || existingContent[endMarkerEnd] == '\n'))
            {
                endMarkerEnd++;
            }

            return existingContent[..beginIndex] + newBlock + existingContent[endMarkerEnd..];
        }

        if (string.IsNullOrWhiteSpace(existingContent))
        {
            return newBlock;
        }

        var separator = existingContent.EndsWith(Environment.NewLine, StringComparison.Ordinal) ? string.Empty : Environment.NewLine;
        return existingContent + separator + newBlock;
    }

    public static IReadOnlyList<string> GetDefaultUnixProfiles()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return
        [
            Path.Combine(home, ".profile"),
            Path.Combine(home, ".bashrc"),
            Path.Combine(home, ".zshrc")
        ];
    }

    private static string EscapeForUnix(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("`", "\\`", StringComparison.Ordinal)
        .Replace("$", "\\$", StringComparison.Ordinal);
}
