using System.Security.Cryptography;
using System.Text.Json;
using Eidosup.Diagnostics;
using Eidosup.Serialization;
using Eidosup.Toolchains;

namespace Eidosup.Installation;

public sealed record InstallManifest(
    int Schema,
    string ToolchainId,
    string IdentitySha256,
    string CompositionSha256,
    string DistributionManifestName,
    string DistributionManifestSha256,
    string ReleaseTag,
    string Version,
    string Rid,
    string Source,
    string Profile,
    IReadOnlyList<string> ExplicitComponents,
    IReadOnlyList<string> ExplicitTargets,
    IReadOnlyList<InstalledComponent> Components,
    IReadOnlyList<string> Targets,
    IReadOnlyList<InstalledArtifact> Artifacts,
    DateTimeOffset InstalledAt,
    IReadOnlyList<InstalledFile> Files)
{
    public const int CurrentSchema = 3;
    public const string FileName = ".eidosup-install.json";

    private const string CompilerGrammarCachePath = "cache/grammar.bin";

    public async Task WriteAsync(string directory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, FileName);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 16 * 1024,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                this,
                EidosupJsonContext.Default.InstallManifest,
                cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    public static async Task<InstallManifest?> TryReadAsync(string directory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var manifest = await JsonSerializer.DeserializeAsync(
                stream,
                EidosupJsonContext.Default.InstallManifest,
                cancellationToken);
            return manifest is { Schema: CurrentSchema } ? manifest : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<bool> VerifyAsync(
        string directory,
        string expectedDistributionManifestSha256,
        CancellationToken cancellationToken,
        string? expectedRid = null,
        string? expectedVersion = null)
    {
        if (Schema != CurrentSchema ||
            !HasValidIdentity(directory) ||
            !string.Equals(
                DistributionManifestSha256,
                expectedDistributionManifestSha256,
                StringComparison.Ordinal) ||
            expectedRid != null && !string.Equals(Rid, expectedRid, StringComparison.Ordinal) ||
            expectedVersion != null && !string.Equals(Version, expectedVersion, StringComparison.Ordinal) ||
            Files is not { Count: > 0 } ||
            !HasValidOwnership())
        {
            return false;
        }

        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root) ||
            (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }

        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var expectedFiles = new HashSet<string>(pathComparer);
        foreach (var file in Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file is null ||
                file.Size < 0 ||
                !ToolchainIdentity.IsCanonicalSha256(file.Sha256) ||
                !TryNormalizeRelativePath(file.Path, out var relativePath) ||
                !string.Equals(relativePath, file.Path, StringComparison.Ordinal) ||
                !expectedFiles.Add(relativePath))
            {
                return false;
            }

            var path = Path.GetFullPath(Path.Combine(
                root,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!ToolInstallLayout.IsWithin(root, path) ||
                !File.Exists(path) ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            var info = new FileInfo(path);
            if (info.Length != file.Size)
            {
                return false;
            }

            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(path);
                var executable = (mode & (UnixFileMode.UserExecute |
                                          UnixFileMode.GroupExecute |
                                          UnixFileMode.OtherExecute)) != 0;
                if (executable != file.Executable)
                {
                    return false;
                }
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(digest, file.Sha256, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return TryCollectInstalledFiles(root, pathComparer, out var actualFiles) &&
               expectedFiles.SetEquals(actualFiles);
    }

    public bool HasValidIdentity(string directory)
    {
        try
        {
            var identity = ToolchainIdentity.Create(
                Version,
                Rid,
                Source,
                ReleaseTag,
                DistributionManifestName,
                DistributionManifestSha256,
                Components.Select(static component => component.Id),
                ToolchainComponentSolver.ParseProfile(Profile),
                ExplicitComponents,
                ExplicitTargets);
            var directoryName = Path.GetFileName(
                Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.Equals(identity.Id, ToolchainId, StringComparison.Ordinal) &&
                   string.Equals(identity.IdentitySha256, IdentitySha256, StringComparison.Ordinal) &&
                   string.Equals(identity.CompositionSha256, CompositionSha256, StringComparison.Ordinal) &&
                   string.Equals(directoryName, ToolchainId, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    public static bool TryNormalizeRelativePath(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path) ||
            path.Contains('\\') ||
            path.Contains(':') ||
            path.IndexOf('\0') >= 0 ||
            path.StartsWith('/') ||
            path.EndsWith('/'))
        {
            return false;
        }

        var segments = path.Split('/');
        if (segments.Any(static segment =>
                string.IsNullOrWhiteSpace(segment) ||
                segment is "." or ".." ||
                segment.Any(char.IsControl)))
        {
            return false;
        }

        normalized = string.Join('/', segments);
        return !string.Equals(normalized, FileName, StringComparison.Ordinal);
    }

    private bool HasValidOwnership()
    {
        if (Components is not { Count: > 0 } ||
            Targets == null ||
            ExplicitComponents == null ||
            ExplicitTargets == null ||
            Artifacts is not { Count: > 0 } ||
            !Enum.TryParse<ToolchainProfile>(Profile, ignoreCase: true, out var profile) ||
            !Enum.IsDefined(profile) ||
            Components.Select(static component => component.Id).Distinct(StringComparer.Ordinal).Count() != Components.Count ||
            ExplicitComponents.Distinct(StringComparer.Ordinal).Count() != ExplicitComponents.Count ||
            ExplicitTargets.Distinct(StringComparer.Ordinal).Count() != ExplicitTargets.Count ||
            Targets.Distinct(StringComparer.Ordinal).Count() != Targets.Count ||
            Artifacts.Select(static artifact => artifact.Name).Distinct(StringComparer.Ordinal).Count() != Artifacts.Count)
        {
            return false;
        }

        var ownedPaths = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        foreach (var component in Components)
        {
            if (component == null ||
                string.IsNullOrWhiteSpace(component.Id) ||
                string.IsNullOrWhiteSpace(component.Name) ||
                string.IsNullOrWhiteSpace(component.Version) ||
                component.Files is not { Count: > 0 })
            {
                return false;
            }

            foreach (var path in component.Files)
            {
                if (!TryNormalizeRelativePath(path, out var normalized) ||
                    !string.Equals(normalized, path, StringComparison.Ordinal) ||
                    !ownedPaths.Add(normalized))
                {
                    return false;
                }
            }
        }

        if (!ownedPaths.SetEquals(Files.Select(static file => file.Path)))
        {
            return false;
        }

        var componentIds = Components.Select(static component => component.Id).ToHashSet(StringComparer.Ordinal);
        if (ExplicitComponents.Any(component => !componentIds.Contains(component)) ||
            ExplicitTargets.Any(target => !Targets.Contains(target, StringComparer.Ordinal)))
        {
            return false;
        }

        return Artifacts.All(static artifact =>
                   artifact != null &&
                   Path.GetFileName(artifact.Name) == artifact.Name &&
                   artifact.Size > 0 &&
                   ToolchainIdentity.IsCanonicalSha256(artifact.Sha256)) &&
               Targets.All(target => Components.Any(component => string.Equals(component.Target, target, StringComparison.Ordinal)));
    }

    private static bool TryCollectInstalledFiles(
        string root,
        StringComparer pathComparer,
        out HashSet<string> files)
    {
        files = new HashSet<string>(pathComparer);
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.TryPop(out var directory))
        {
            foreach (var childDirectory in Directory.EnumerateDirectories(directory))
            {
                if ((File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                directories.Push(childDirectory);
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                var relativePath = Path.GetRelativePath(root, file)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (string.Equals(relativePath, FileName, StringComparison.Ordinal) ||
                    string.Equals(relativePath, CompilerGrammarCachePath, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!TryNormalizeRelativePath(relativePath, out var normalized) ||
                    !files.Add(normalized))
                {
                    return false;
                }
            }
        }

        return true;
    }

    public static EidosupException Conflict(string targetDirectory) => new(
        EidosupErrorCode.InstallConflict,
        EidosupExitCodes.InstallConflict,
        $"Install target '{targetDirectory}' already contains a different or unverifiable toolchain.",
        "Choose another composition, remove the unmanaged directory, or use --force to replace it transactionally.");
}

public sealed record InstalledComponent(
    string Id,
    string Name,
    string Version,
    bool Required,
    string? Target,
    IReadOnlyList<string> Files);

public sealed record InstalledArtifact(string Name, string Sha256, long Size);
