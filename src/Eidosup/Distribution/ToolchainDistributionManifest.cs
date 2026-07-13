using System.Text.Json;
using System.Text.Json.Serialization;
using Eidosup.Diagnostics;
using Eidosup.Installation;
using Eidosup.Toolchains;

namespace Eidosup.Distribution;

public sealed record ToolchainDistributionManifest(
    int Schema,
    string Toolchain,
    string Channel,
    string Host,
    ToolchainProductIdentity Eidosc,
    ToolchainLanguageIdentity Language,
    IReadOnlyList<ToolchainProfileDefinition> Profiles,
    IReadOnlyList<ToolchainComponentDefinition> Components,
    IReadOnlyList<ToolchainTargetDefinition> Targets,
    ToolchainRequirementSet Requirements,
    DateTimeOffset PublishedAt)
{
    public const int CurrentSchema = 1;
    public const int MaximumManifestBytes = 8 * 1024 * 1024;
    public const int MaximumComponents = 128;
    public const int MaximumTargets = 64;
    public const int MaximumFiles = 16_384;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private static StringComparer InstallPathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static async Task<ToolchainDistributionManifest> ReadAsync(
        string path,
        string expectedVersion,
        string expectedHost,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length is <= 0 or > MaximumManifestBytes ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw Invalid($"Toolchain manifest '{fullPath}' is missing, linked, empty, or exceeds {MaximumManifestBytes} bytes.");
        }

        try
        {
            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 32 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var manifest = await JsonSerializer.DeserializeAsync<ToolchainDistributionManifest>(
                               stream,
                               JsonOptions,
                               cancellationToken)
                           ?? throw new JsonException("Toolchain manifest is empty.");
            manifest.Validate(expectedVersion, expectedHost);
            return manifest;
        }
        catch (EidosupException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or FormatException)
        {
            throw Invalid($"Toolchain manifest '{fullPath}' is invalid.", exception);
        }
    }

    public void Validate(string expectedVersion, string expectedHost)
    {
        var version = ParseCanonicalVersion(expectedVersion, "expected Eidosc version");
        if (Schema != CurrentSchema ||
            !string.Equals(Host, expectedHost, StringComparison.Ordinal) ||
            !PlatformContext.IsSupportedRid(Host) ||
            !string.Equals(Toolchain, $"eidosc-{version}-{Host}", StringComparison.Ordinal) ||
            Eidosc == null ||
            Language == null ||
            Profiles == null ||
            Components == null ||
            Targets == null ||
            Requirements == null ||
            PublishedAt == default)
        {
            throw Invalid("Toolchain manifest identity or required fields are invalid.");
        }

        var declaredVersion = ParseCanonicalVersion(Eidosc.Version, "Eidosc component version");
        _ = ParseCanonicalVersion(Language.Version, "Eidos language version");
        if (!string.Equals(declaredVersion, version, StringComparison.Ordinal) ||
            !IsCommitSha(Eidosc.Commit) ||
            Channel is not ("stable" or "preview" or "nightly") ||
            Requirements.Llvm == null ||
            string.IsNullOrWhiteSpace(Requirements.Llvm.Supported) ||
            Components.Count is <= 0 or > MaximumComponents ||
            Targets.Count > MaximumTargets)
        {
            throw Invalid("Toolchain manifest product, channel, or requirement metadata is invalid.");
        }

        ValidateComponents();
        ValidateTargets();
        ValidateProfiles();
    }

    public ToolchainProfileDefinition GetProfile(ToolchainProfile profile) =>
        Profiles.Single(candidate => string.Equals(candidate.Name, profile.ToString().ToLowerInvariant(), StringComparison.Ordinal));

    public ToolchainComponentDefinition GetComponent(string id) =>
        Components.Single(component => string.Equals(component.Id, id, StringComparison.Ordinal));

    public ToolchainTargetDefinition GetTarget(string name) =>
        Targets.Single(target => string.Equals(target.Name, name, StringComparison.Ordinal));

    private void ValidateComponents()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var ownedFiles = new HashSet<string>(InstallPathComparer);
        var artifacts = new Dictionary<string, ToolchainComponentArtifact>(StringComparer.Ordinal);
        var totalFiles = 0;
        foreach (var component in Components)
        {
            if (component == null ||
                !IsSafeIdentifier(component.Id, allowAt: true) ||
                !IsSafeIdentifier(component.Name, allowAt: false) ||
                !ids.Add(component.Id) ||
                !string.Equals(ParseCanonicalVersion(component.Version, $"component '{component.Id}' version"), component.Version, StringComparison.Ordinal) ||
                component.Dependencies == null ||
                component.Conflicts == null ||
                component.Artifact == null ||
                component.Files is not { Count: > 0 } ||
                component.Dependencies.Any(dependency => !IsSafeIdentifier(dependency, allowAt: true)) ||
                component.Conflicts.Any(conflict => !IsSafeIdentifier(conflict, allowAt: true)) ||
                component.Dependencies.Distinct(StringComparer.Ordinal).Count() != component.Dependencies.Count ||
                component.Conflicts.Distinct(StringComparer.Ordinal).Count() != component.Conflicts.Count ||
                component.Dependencies.Contains(component.Id, StringComparer.Ordinal) ||
                component.Conflicts.Contains(component.Id, StringComparer.Ordinal) ||
                !IsSafeAssetName(component.Artifact.Name) ||
                component.Artifact.Size <= 0 ||
                !ToolchainIdentity.IsCanonicalSha256(component.Artifact.Sha256))
            {
                throw Invalid("Toolchain component metadata is invalid or duplicated.");
            }

            if (artifacts.TryGetValue(component.Artifact.Name, out var existingArtifact))
            {
                if (existingArtifact.Size != component.Artifact.Size ||
                    !string.Equals(existingArtifact.Sha256, component.Artifact.Sha256, StringComparison.Ordinal))
                {
                    throw Invalid($"Artifact '{component.Artifact.Name}' has inconsistent size or digest metadata.");
                }
            }
            else
            {
                artifacts.Add(component.Artifact.Name, component.Artifact);
            }

            if (component.Target != null && !IsSafeIdentifier(component.Target, allowAt: false))
            {
                throw Invalid($"Component '{component.Id}' has an invalid target name.");
            }

            var componentFiles = new HashSet<string>(InstallPathComparer);
            foreach (var file in component.Files)
            {
                totalFiles++;
                if (totalFiles > MaximumFiles ||
                    file == null ||
                    file.Size < 0 ||
                    !ToolchainIdentity.IsCanonicalSha256(file.Sha256) ||
                    !InstallManifest.TryNormalizeRelativePath(file.Path, out var normalized) ||
                    !string.Equals(normalized, file.Path, StringComparison.Ordinal) ||
                    !componentFiles.Add(normalized) ||
                    !ownedFiles.Add(normalized))
                {
                    throw Invalid("Toolchain component file ownership is invalid, duplicated, or too large.");
                }
            }
        }

        foreach (var component in Components)
        {
            if (component.Dependencies.Any(dependency => !ids.Contains(dependency)) ||
                component.Conflicts.Any(conflict => !ids.Contains(conflict)))
            {
                throw Invalid($"Component '{component.Id}' refers to an unknown dependency or conflict.");
            }
        }

        foreach (var component in Components)
        {
            VisitDependencies(component.Id, ids, new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
        }

        var executableName = Host.StartsWith("win-", StringComparison.Ordinal) ? "eidosc.exe" : "eidosc";
        var core = Components.SingleOrDefault(static component =>
            string.Equals(component.Id, "eidosc-core", StringComparison.Ordinal));
        var std = Components.SingleOrDefault(static component =>
            string.Equals(component.Id, "eidos-std", StringComparison.Ordinal));
        if (core == null ||
            !string.Equals(core.Name, "eidosc-core", StringComparison.Ordinal) ||
            !string.Equals(core.Version, Eidosc.Version, StringComparison.Ordinal) ||
            !core.Required ||
            core.Target != null ||
            !core.Files.Any(file =>
                string.Equals(file.Path, executableName, StringComparison.Ordinal) && file.Executable) ||
            std == null ||
            !string.Equals(std.Name, "eidos-std", StringComparison.Ordinal) ||
            !std.Required ||
            std.Target != null)
        {
            throw Invalid("Toolchain manifest must contain required eidosc-core and eidos-std components with an executable host compiler.");
        }
    }

    private void ValidateTargets()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var triples = new HashSet<string>(StringComparer.Ordinal);
        ToolchainTargetDefinition? hostTarget = null;
        foreach (var target in Targets)
        {
            if (target == null ||
                !IsSafeIdentifier(target.Name, allowAt: false) ||
                !names.Add(target.Name) ||
                string.IsNullOrWhiteSpace(target.Triple) ||
                !string.Equals(target.Triple, target.Triple.Trim(), StringComparison.Ordinal) ||
                target.Triple.Any(char.IsControl) ||
                target.Triple.Length > 128 ||
                !triples.Add(target.Triple) ||
                !IsSafeIdentifier(target.Component, allowAt: true) ||
                target.Linker == null ||
                !Enum.IsDefined(target.Support))
            {
                throw Invalid("Toolchain target metadata is invalid or duplicated.");
            }

            var component = Components.SingleOrDefault(candidate =>
                string.Equals(candidate.Id, target.Component, StringComparison.Ordinal));
            if (component == null ||
                !string.Equals(component.Target, target.Name, StringComparison.Ordinal) ||
                !string.Equals(component.Name, "eidos-runtime", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(target.Linker.Command) ||
                target.Linker.Command.Any(char.IsControl) ||
                target.Linker.Command.Length > 128)
            {
                throw Invalid($"Target '{target.Name}' does not map to a valid runtime component and linker requirement.");
            }

            if (string.Equals(target.Name, Host, StringComparison.Ordinal))
            {
                if (target.Support != ToolchainTargetSupport.Host || target.Linker.ExternalSdkRequired || hostTarget != null)
                {
                    throw Invalid($"Host target '{Host}' must be unique, host-supported, and link without an external target SDK.");
                }

                hostTarget = target;
            }
            else if (target.Support != ToolchainTargetSupport.CrossCompile || !target.Linker.ExternalSdkRequired)
            {
                throw Invalid($"Non-host target '{target.Name}' must be marked cross-compile and declare its external SDK requirement.");
            }
        }

        if (hostTarget == null)
        {
            throw Invalid($"Toolchain manifest does not define a host target for '{Host}'.");
        }

        if (Components.Any(component => component.Target != null && !names.Contains(component.Target)))
        {
            throw Invalid("A target-specific component is not represented in the target index.");
        }
    }

    private void ValidateProfiles()
    {
        var expectedNames = new[] { "minimal", "default", "complete" };
        if (Profiles.Count != expectedNames.Length ||
            Profiles.Any(static profile => profile == null || profile.Name == null) ||
            !Profiles.Select(static profile => profile.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(expectedNames.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw Invalid("Toolchain manifest must define minimal, default, and complete profiles exactly once.");
        }

        var componentIds = Components.Select(static component => component.Id).ToHashSet(StringComparer.Ordinal);
        var required = Components.Where(static component => component.Required)
            .Select(static component => component.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var profile in Profiles)
        {
            if (profile == null ||
                profile.Components == null ||
                profile.Components.Distinct(StringComparer.Ordinal).Count() != profile.Components.Count ||
                profile.Components.Any(component => !componentIds.Contains(component)) ||
                !required.IsSubsetOf(profile.Components))
            {
                throw Invalid($"Profile '{profile?.Name}' contains invalid components or omits a required component.");
            }
        }

        var minimal = GetProfile(ToolchainProfile.Minimal).Components.ToHashSet(StringComparer.Ordinal);
        var defaults = GetProfile(ToolchainProfile.Default).Components.ToHashSet(StringComparer.Ordinal);
        var complete = GetProfile(ToolchainProfile.Complete).Components.ToHashSet(StringComparer.Ordinal);
        var hostRuntime = Targets.Single(target => string.Equals(target.Name, Host, StringComparison.Ordinal)).Component;
        if (!minimal.IsSubsetOf(defaults) ||
            !defaults.IsSubsetOf(complete) ||
            minimal.Contains(hostRuntime) ||
            !defaults.Contains(hostRuntime))
        {
            throw Invalid("Toolchain profiles must be monotonic, with the host runtime absent from minimal and present in default and complete.");
        }
    }

    private void VisitDependencies(
        string id,
        IReadOnlySet<string> ids,
        ISet<string> visiting,
        ISet<string> visited)
    {
        if (visited.Contains(id))
        {
            return;
        }

        if (!visiting.Add(id))
        {
            throw Invalid($"Toolchain component dependency cycle includes '{id}'.");
        }

        var component = Components.Single(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        foreach (var dependency in component.Dependencies)
        {
            if (!ids.Contains(dependency))
            {
                throw Invalid($"Component '{id}' has unknown dependency '{dependency}'.");
            }

            VisitDependencies(dependency, ids, visiting, visited);
        }

        visiting.Remove(id);
        visited.Add(id);
    }

    private static string ParseCanonicalVersion(string value, string description)
    {
        var parsed = SemanticVersion.Parse(value);
        if (!string.Equals(parsed.ToString(), value, StringComparison.Ordinal))
        {
            throw Invalid($"The {description} must use canonical SemVer spelling.");
        }

        return parsed.ToString();
    }

    private static bool IsCommitSha(string? value) =>
        value is { Length: 40 } && value.All(static character => char.IsAsciiHexDigit(character) && !char.IsUpper(character));

    private static bool IsSafeIdentifier(string? value, bool allowAt) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' || allowAt && character == '@');

    private static bool IsSafeAssetName(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 255 &&
        Path.GetFileName(value) == value &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static EidosupException Invalid(string message, Exception? inner = null) => new(
        EidosupErrorCode.InvalidReleaseMetadata,
        EidosupExitCodes.InvalidRelease,
        message,
        "Use a release whose signed toolchain manifest, component ownership, profiles, and targets pass schema validation.",
        inner);
}

public sealed record ToolchainProductIdentity(string Version, string Commit);

public sealed record ToolchainLanguageIdentity(string Version);

public sealed record ToolchainProfileDefinition(string Name, IReadOnlyList<string> Components);

public sealed record ToolchainComponentDefinition(
    string Id,
    string Name,
    string Version,
    bool Required,
    string? Target,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> Conflicts,
    ToolchainComponentArtifact Artifact,
    IReadOnlyList<ToolchainComponentFile> Files);

public sealed record ToolchainComponentArtifact(string Name, long Size, string Sha256);

public sealed record ToolchainComponentFile(
    string Path,
    long Size,
    string Sha256,
    bool Executable = false);

public sealed record ToolchainTargetDefinition(
    string Name,
    string Triple,
    string Component,
    ToolchainTargetSupport Support,
    ToolchainLinkerRequirement Linker);

public enum ToolchainTargetSupport
{
    Host,
    CrossCompile
}

public sealed record ToolchainLinkerRequirement(string Command, bool ExternalSdkRequired);

public sealed record ToolchainRequirementSet(ToolchainLlvmRequirement Llvm);

public sealed record ToolchainLlvmRequirement(string Supported);
