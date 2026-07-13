using System.Text;
using Eidosup.Diagnostics;
using Eidosup.Toolchains;

namespace Eidosup.Configuration;

public sealed record ProjectToolchainConfiguration(
    string FilePath,
    ToolchainSpec Toolchain,
    string Profile,
    IReadOnlyList<string> Components,
    IReadOnlyList<string> Targets);

public static partial class ProjectToolchainConfigurationReader
{
    public const string FileName = "eidos-toolchain.toml";

    public static async Task<ProjectToolchainConfiguration> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        IReadOnlyDictionary<string, object> values;
        try
        {
            values = await SimpleToml.ReadSectionAsync(
                fullPath,
                "toolchain",
                cancellationToken,
                strictDocument: true);
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            throw Invalid(fullPath, exception.Message, exception);
        }
        var unknown = values.Keys.Except(["channel", "profile", "components", "targets"], StringComparer.Ordinal).ToArray();
        if (unknown.Length != 0)
        {
            throw Invalid(fullPath, $"unknown [toolchain] keys: {string.Join(", ", unknown)}");
        }
        if (!values.TryGetValue("channel", out var channel) || channel is not string selector)
        {
            throw Invalid(fullPath, "[toolchain].channel must be a quoted toolchain selector");
        }

        var profile = values.TryGetValue("profile", out var configuredProfile) && configuredProfile is string profileValue
            ? profileValue
            : "default";
        if (profile is not ("minimal" or "default" or "complete"))
        {
            throw Invalid(fullPath, "[toolchain].profile must be minimal, default, or complete");
        }
        var components = ReadStringArray(values, "components", fullPath);
        var targets = ReadStringArray(values, "targets", fullPath);

        return new ProjectToolchainConfiguration(
            fullPath,
            ToolchainSpec.Parse(selector),
            profile,
            components,
            targets);
    }

    private static IReadOnlyList<string> ReadStringArray(
        IReadOnlyDictionary<string, object> values,
        string key,
        string path)
    {
        if (!values.TryGetValue(key, out var value))
        {
            return [];
        }

        if (value is not IReadOnlyList<string> items ||
            items.Any(static item =>
                string.IsNullOrWhiteSpace(item) ||
                !string.Equals(item, item.Trim(), StringComparison.Ordinal)) ||
            items.Distinct(StringComparer.Ordinal).Count() != items.Count)
        {
            throw Invalid(path, $"[toolchain].{key} must be an array of unique non-empty quoted strings without surrounding whitespace");
        }

        return items;
    }

    private static EidosupException Invalid(string path, string reason, Exception? inner = null) => new(
        EidosupErrorCode.InvalidArgument,
        EidosupExitCodes.InvalidArgument,
        $"Project toolchain file '{path}' is invalid: {reason.TrimEnd('.')}.",
        "Fix eidos-toolchain.toml; Eidosup never guesses through an invalid project pin.",
        inner);
}

internal static class SimpleToml
{
    public static async Task<IReadOnlyDictionary<string, object>> ReadSectionAsync(
        string path,
        string section,
        CancellationToken cancellationToken,
        bool strictDocument = false)
    {
        var lines = await File.ReadAllLinesAsync(path, new UTF8Encoding(false, true), cancellationToken);
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        var seenSections = new HashSet<string>(StringComparer.Ordinal);
        string? currentSection = null;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = StripComment(lines[index]).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (line.StartsWith("[[", StringComparison.Ordinal) || line.EndsWith("]]", StringComparison.Ordinal))
                {
                    throw new FormatException($"Unsupported TOML table array at {path}:{index + 1}.");
                }

                currentSection = line[1..^1].Trim();
                if (currentSection.Length == 0 || strictDocument && !string.Equals(currentSection, section, StringComparison.Ordinal))
                {
                    throw new FormatException($"Unknown or empty TOML section at {path}:{index + 1}.");
                }

                if (!seenSections.Add(currentSection))
                {
                    throw new FormatException($"Duplicate TOML section at {path}:{index + 1}.");
                }

                continue;
            }

            if (!string.Equals(currentSection, section, StringComparison.Ordinal))
            {
                if (strictDocument)
                {
                    throw new FormatException($"TOML assignments must be inside [{section}] at {path}:{index + 1}.");
                }

                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                throw new FormatException($"Invalid TOML assignment at {path}:{index + 1}.");
            }

            var key = line[..separator].Trim();
            if (!IsBareKey(key) || !result.TryAdd(key, ParseValue(line[(separator + 1)..].Trim(), path, index + 1)))
            {
                throw new FormatException($"Invalid or duplicate TOML key at {path}:{index + 1}.");
            }
        }

        return result;
    }

    private static object ParseValue(string value, string path, int line)
    {
        if (TryParseString(value, out var text))
        {
            return text;
        }

        if (value.StartsWith('[') && value.EndsWith(']'))
        {
            var body = value[1..^1].Trim();
            if (body.Length == 0)
            {
                return Array.Empty<string>();
            }

            var items = SplitArray(body);
            var strings = new List<string>(items.Count);
            foreach (var item in items)
            {
                if (!TryParseString(item.Trim(), out var parsed))
                {
                    throw new FormatException($"Only quoted string arrays are supported at {path}:{line}.");
                }

                strings.Add(parsed);
            }

            return strings;
        }

        if (bool.TryParse(value, out var boolean))
        {
            return boolean;
        }

        throw new FormatException($"Unsupported TOML value at {path}:{line}.");
    }

    private static bool TryParseString(string value, out string result)
    {
        result = string.Empty;
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            return false;
        }

        var builder = new System.Text.StringBuilder(value.Length - 2);
        for (var index = 1; index < value.Length - 1; index++)
        {
            var character = value[index];
            if (character != '\\')
            {
                if (char.IsControl(character))
                {
                    return false;
                }

                builder.Append(character);
                continue;
            }

            if (++index >= value.Length - 1)
            {
                return false;
            }

            builder.Append(value[index] switch
            {
                '"' => '"',
                '\\' => '\\',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                _ => '\0'
            });
            if (builder[^1] == '\0')
            {
                return false;
            }
        }

        result = builder.ToString();
        return true;
    }

    private static List<string> SplitArray(string body)
    {
        var items = new List<string>();
        var start = 0;
        var quoted = false;
        var escaped = false;
        for (var index = 0; index < body.Length; index++)
        {
            var character = body[index];
            if (escaped)
            {
                escaped = false;
            }
            else if (character == '\\' && quoted)
            {
                escaped = true;
            }
            else if (character == '"')
            {
                quoted = !quoted;
            }
            else if (character == ',' && !quoted)
            {
                items.Add(body[start..index]);
                start = index + 1;
            }
        }

        if (quoted || escaped)
        {
            throw new FormatException("Unterminated TOML string array.");
        }

        items.Add(body[start..]);
        return items;
    }

    private static string StripComment(string line)
    {
        var quoted = false;
        var escaped = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (escaped)
            {
                escaped = false;
            }
            else if (character == '\\' && quoted)
            {
                escaped = true;
            }
            else if (character == '"')
            {
                quoted = !quoted;
            }
            else if (character == '#' && !quoted)
            {
                return line[..index];
            }
        }

        return line;
    }

    private static bool IsBareKey(string key) =>
        key.Length > 0 && key.All(static character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
}
