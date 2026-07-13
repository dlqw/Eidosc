using System.Text;
using Eidosup.Diagnostics;
using Eidosup.Distribution;
using Eidosup.Installation;

namespace Eidosup.Configuration;

public enum AutoSelfUpdateMode
{
    Enable,
    Disable,
    CheckOnly
}

public enum AutoInstallMode
{
    Enable,
    Disable,
    Prompt
}

public sealed record EidosupSettings(
    int Schema,
    string DefaultHost,
    AutoSelfUpdateMode AutoSelfUpdate,
    AutoInstallMode AutoInstall,
    string Source)
{
    public const int CurrentSchema = 1;

    public static EidosupSettings Default() => new(
        CurrentSchema,
        PlatformContext.Detect().Rid,
        AutoSelfUpdateMode.CheckOnly,
        AutoInstallMode.Prompt,
        "github:dlqw/Eidosc");
}

public sealed class EidosupSettingsStore
{
    public const string FileName = "settings.toml";
    private readonly TimeSpan _lockTimeout;

    public EidosupSettingsStore(TimeSpan? lockTimeout = null)
    {
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task<EidosupSettings> ReadAsync(
        ToolInstallLayout layout,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(layout.RootDirectory, FileName);
        if (!File.Exists(path))
        {
            return EidosupSettings.Default();
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = await File.ReadAllLinesAsync(path, new UTF8Encoding(false, true), cancellationToken);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = StripComment(lines[index]).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                throw Invalid(path, index + 1);
            }

            var key = line[..separator].Trim();
            var raw = line[(separator + 1)..].Trim();
            if (!values.TryAdd(key, ParseScalar(raw, path, index + 1)))
            {
                throw Invalid(path, index + 1);
            }
        }

        var knownKeys = new[] { "schema", "default-host", "auto-self-update", "auto-install", "source" };
        if (values.Keys.Except(knownKeys, StringComparer.Ordinal).Any() ||
            !values.TryGetValue("schema", out var schemaText) ||
            !int.TryParse(schemaText, out var schema) ||
            schema != EidosupSettings.CurrentSchema ||
            !values.TryGetValue("default-host", out var defaultHost) ||
            !PlatformContext.IsSupportedRid(defaultHost) ||
            !values.TryGetValue("auto-self-update", out var selfText) ||
            !TryParseEnum(selfText, out AutoSelfUpdateMode selfMode) ||
            !values.TryGetValue("auto-install", out var installText) ||
            !TryParseEnum(installText, out AutoInstallMode installMode) ||
            !values.TryGetValue("source", out var source))
        {
            throw new EidosupException(
                EidosupErrorCode.StateUnsupported,
                EidosupExitCodes.StateUnsupported,
                $"Eidosup settings '{path}' are invalid or use an unsupported schema.",
                "Repair settings.toml or move it aside and rerun a set command to create validated defaults.");
        }

        if (!IsValidSource(source))
        {
            throw new EidosupException(
                EidosupErrorCode.StateUnsupported,
                EidosupExitCodes.StateUnsupported,
                $"Eidosup settings '{path}' contain an invalid distribution source.",
                "Repair settings.toml or select a validated direct source or named source group.");
        }
        return new EidosupSettings(schema, defaultHost, selfMode, installMode, source);
    }

    public async Task WriteAsync(
        ToolInstallLayout layout,
        EidosupSettings settings,
        CancellationToken cancellationToken)
    {
        ValidateSettings(settings);
        Directory.CreateDirectory(layout.RootDirectory);
        await using var operationLock = await InstallOperationLock.AcquireAsync(
            layout.LockDirectory,
            _lockTimeout,
            cancellationToken,
            operationName: "settings");
        var path = Path.Combine(layout.RootDirectory, FileName);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        var text = $"schema = {settings.Schema}\n" +
                   $"default-host = \"{Escape(settings.DefaultHost)}\"\n" +
                   $"auto-self-update = \"{ToKebabCase(settings.AutoSelfUpdate)}\"\n" +
                   $"auto-install = \"{ToKebabCase(settings.AutoInstall)}\"\n" +
                   $"source = \"{Escape(settings.Source)}\"\n";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false, true), leaveOpen: true))
            {
                await writer.WriteAsync(text.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static string ParseScalar(string raw, string path, int line)
    {
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            var result = new StringBuilder(raw.Length - 2);
            for (var index = 1; index < raw.Length - 1; index++)
            {
                var character = raw[index];
                if (character != '\\')
                {
                    if (char.IsControl(character))
                    {
                        throw Invalid(path, line);
                    }

                    result.Append(character);
                    continue;
                }

                if (++index >= raw.Length - 1 || raw[index] is not ('\\' or '"'))
                {
                    throw Invalid(path, line);
                }

                result.Append(raw[index]);
            }

            return result.ToString();
        }

        if (raw.All(char.IsAsciiDigit))
        {
            return raw;
        }

        throw Invalid(path, line);
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string ToKebabCase<T>(T value) where T : struct, Enum =>
        string.Concat(value.ToString().SelectMany((character, index) =>
            index > 0 && char.IsUpper(character)
                ? new[] { '-', char.ToLowerInvariant(character) }
                : new[] { char.ToLowerInvariant(character) }));

    private static bool TryParseEnum<T>(string value, out T result) where T : struct, Enum =>
        Enum.TryParse(value.Replace("-", string.Empty, StringComparison.Ordinal), true, out result) &&
        Enum.IsDefined(result);

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

    private static void ValidateSettings(EidosupSettings settings)
    {
        if (settings.Schema != EidosupSettings.CurrentSchema ||
            !PlatformContext.IsSupportedRid(settings.DefaultHost) ||
            !Enum.IsDefined(settings.AutoSelfUpdate) ||
            !Enum.IsDefined(settings.AutoInstall))
        {
            throw new ArgumentException("Eidosup settings contain an invalid schema, host, or lifecycle mode.", nameof(settings));
        }

        if (!IsValidSource(settings.Source))
        {
            throw new ArgumentException("Eidosup settings contain an invalid distribution source.", nameof(settings));
        }
    }

    private static bool IsValidSource(string source)
    {
        try
        {
            _ = DistributionSourceDescriptor.Parse(source);
            return true;
        }
        catch (FormatException)
        {
            return DistributionSourceCatalogStore.IsValidName(source);
        }
    }

    private static EidosupException Invalid(string path, int line) => new(
        EidosupErrorCode.StateCorrupt,
        EidosupExitCodes.StateCorrupt,
        $"Eidosup settings '{path}' contain invalid TOML at line {line}.",
        "Repair settings.toml; Eidosup will not silently ignore invalid lifecycle or source configuration.");
}
