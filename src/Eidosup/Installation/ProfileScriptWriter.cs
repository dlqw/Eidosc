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
            $"export EIDOS_HOME=\"{EscapeForUnix(plan.EidosHome)}\""
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

        var separator = existingContent.EndsWith('\n') || existingContent.EndsWith('\r')
            ? string.Empty
            : Environment.NewLine;
        return existingContent + separator + newBlock;
    }

    public static string RemoveBlock(string existingContent)
    {
        var beginIndex = existingContent.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (beginIndex < 0)
        {
            return existingContent;
        }

        var endIndex = existingContent.IndexOf(EndMarker, beginIndex, StringComparison.Ordinal);
        if (endIndex < 0)
        {
            throw new InvalidDataException("The Eidosup shell profile block is missing its end marker.");
        }

        var end = endIndex + EndMarker.Length;
        while (end < existingContent.Length && existingContent[end] is '\r' or '\n')
        {
            end++;
        }

        return existingContent.Remove(beginIndex, end - beginIndex);
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
