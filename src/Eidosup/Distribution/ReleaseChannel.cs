namespace Eidosup.Distribution;

public enum ReleaseChannel
{
    Stable,
    Preview
}

public static class ReleaseChannelParser
{
    public static ReleaseChannel Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new FormatException("A release channel must be 'stable' or 'preview'.");
        }

        return value.ToLowerInvariant() switch
        {
            "stable" => ReleaseChannel.Stable,
            "preview" => ReleaseChannel.Preview,
            _ => throw new FormatException($"Unknown release channel '{value}'. Expected 'stable' or 'preview'.")
        };
    }
}
