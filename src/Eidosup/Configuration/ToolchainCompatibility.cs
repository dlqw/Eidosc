using System.Text.Json;
using Eidosup.Diagnostics;
using Eidosup.Distribution;

namespace Eidosup.Configuration;

public enum ToolchainCompatibilityStatus
{
    NotApplicable,
    Compatible,
    Unknown
}

public sealed record ToolchainCompatibilityResult(
    ToolchainCompatibilityStatus Status,
    string? ProjectLanguageVersion,
    string? SupportedLanguageRange,
    string? ProjectManifestPath);

public static class ToolchainCompatibilityVerifier
{
    public const string CompatibilityFileName = "compatibility.json";

    public static async Task<ToolchainCompatibilityResult> VerifyAsync(
        string workingDirectory,
        string toolchainDirectory,
        CancellationToken cancellationToken,
        string? expectedEidoscVersion = null)
    {
        var projectManifest = FindAncestorFile(workingDirectory, "eidos.toml");
        if (projectManifest == null)
        {
            return new ToolchainCompatibilityResult(ToolchainCompatibilityStatus.NotApplicable, null, null, null);
        }

        var language = await ReadLanguageVersionAsync(projectManifest, cancellationToken);
        if (language == null)
        {
            return new ToolchainCompatibilityResult(ToolchainCompatibilityStatus.NotApplicable, null, null, projectManifest);
        }

        var compatibilityPath = Path.Combine(toolchainDirectory, CompatibilityFileName);
        if (!File.Exists(compatibilityPath))
        {
            return new ToolchainCompatibilityResult(
                ToolchainCompatibilityStatus.Unknown,
                language,
                null,
                projectManifest);
        }
        if ((File.GetAttributes(compatibilityPath) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(compatibilityPath).Length > 1024 * 1024)
        {
            throw Invalid(compatibilityPath, "the file is a link or exceeds 1 MiB");
        }

        try
        {
            await using var stream = File.OpenRead(compatibilityPath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schema", out var schemaElement) ||
                !schemaElement.TryGetInt32(out var schema) || schema != 1 ||
                !root.TryGetProperty("component", out var componentElement) ||
                componentElement.GetString() != "eidosc" ||
                !root.TryGetProperty("version", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("language", out var languageElement) ||
                !languageElement.TryGetProperty("supported", out var supportedElement) ||
                supportedElement.ValueKind != JsonValueKind.String)
            {
                throw Invalid(compatibilityPath, "schema, component, version, or language.supported is missing or invalid");
            }

            var declaredVersion = SemanticVersion.Parse(versionElement.GetString()!).ToString();
            if (expectedEidoscVersion != null &&
                !string.Equals(declaredVersion, expectedEidoscVersion, StringComparison.Ordinal))
            {
                throw Invalid(
                    compatibilityPath,
                    $"declared Eidosc version '{declaredVersion}' does not match installed version '{expectedEidoscVersion}'");
            }

            var range = supportedElement.GetString()!;
            if (!SemanticVersionRange.Parse(range).Contains(SemanticVersion.Parse(language)))
            {
                throw new EidosupException(
                    EidosupErrorCode.ToolchainUnavailable,
                    EidosupExitCodes.ToolchainUnavailable,
                    $"The selected toolchain supports Eidos language '{range}', but project '{projectManifest}' requires '{language}'.",
                    "Install or select a compatible Eidosc toolchain; Eidosup will not change the project's language version.");
            }

            return new ToolchainCompatibilityResult(
                ToolchainCompatibilityStatus.Compatible,
                language,
                range,
                projectManifest);
        }
        catch (EidosupException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            throw Invalid(compatibilityPath, "the JSON or SemVer range is malformed", exception);
        }
    }

    private static async Task<string?> ReadLanguageVersionAsync(string path, CancellationToken cancellationToken)
    {
        var values = await SimpleToml.ReadSectionAsync(path, "language", cancellationToken);
        if (!values.TryGetValue("version", out var version))
        {
            return null;
        }

        if (version is not string text)
        {
            throw Invalid(path, "[language].version must be a quoted SemVer");
        }

        return SemanticVersion.Parse(text).ToString();
    }

    private static string? FindAncestorFile(string workingDirectory, string fileName)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(workingDirectory));
        while (directory != null)
        {
            var path = Path.Combine(directory.FullName, fileName);
            if (File.Exists(path))
            {
                return path;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static EidosupException Invalid(string path, string reason, Exception? inner = null) => new(
        EidosupErrorCode.InvalidReleaseMetadata,
        EidosupExitCodes.InvalidRelease,
        $"Compatibility metadata '{path}' is invalid: {reason}.",
        "Reinstall or rebuild the toolchain with valid compatibility.json metadata.",
        inner);
}

public sealed record SemanticVersionRange(
    SemanticVersion? Minimum,
    bool IncludeMinimum,
    SemanticVersion? Maximum,
    bool IncludeMaximum)
{
    public static SemanticVersionRange Parse(string value)
    {
        SemanticVersion? minimum = null;
        SemanticVersion? maximum = null;
        var includeMinimum = false;
        var includeMaximum = false;
        foreach (var clause in value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (clause.StartsWith(">=", StringComparison.Ordinal))
            {
                if (minimum != null)
                {
                    throw new FormatException("A SemVer range cannot contain multiple lower bounds.");
                }
                minimum = SemanticVersion.Parse(clause[2..]);
                includeMinimum = true;
            }
            else if (clause.StartsWith('>'))
            {
                if (minimum != null)
                {
                    throw new FormatException("A SemVer range cannot contain multiple lower bounds.");
                }
                minimum = SemanticVersion.Parse(clause[1..]);
                includeMinimum = false;
            }
            else if (clause.StartsWith("<=", StringComparison.Ordinal))
            {
                if (maximum != null)
                {
                    throw new FormatException("A SemVer range cannot contain multiple upper bounds.");
                }
                maximum = SemanticVersion.Parse(clause[2..]);
                includeMaximum = true;
            }
            else if (clause.StartsWith('<'))
            {
                if (maximum != null)
                {
                    throw new FormatException("A SemVer range cannot contain multiple upper bounds.");
                }
                maximum = SemanticVersion.Parse(clause[1..]);
                includeMaximum = false;
            }
            else
            {
                throw new FormatException($"Unsupported SemVer range clause '{clause}'.");
            }
        }

        if (minimum == null && maximum == null)
        {
            throw new FormatException("A SemVer range must contain at least one bound.");
        }
        if (minimum != null && maximum != null &&
            (minimum > maximum || minimum == maximum && (!includeMinimum || !includeMaximum)))
        {
            throw new FormatException("A SemVer range lower bound must be below its upper bound.");
        }

        return new SemanticVersionRange(minimum, includeMinimum, maximum, includeMaximum);
    }

    public bool Contains(SemanticVersion version) =>
        (Minimum == null || (IncludeMinimum ? version >= Minimum : version > Minimum)) &&
        (Maximum == null || (IncludeMaximum ? version <= Maximum : version < Maximum));
}
