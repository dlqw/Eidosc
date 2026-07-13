using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eidosup.Diagnostics;
using Eidosup.Installation;
using Eidosup.Toolchains;

namespace Eidosup.Proxies;

public interface IShimInstaller
{
    Task<ShimInstallResult> InstallAsync(
        ToolInstallLayout layout,
        bool dryRun,
        CancellationToken cancellationToken);
}

public enum ShimMaterialization
{
    HardLink,
    Copy
}

public sealed record ShimInstallResult(
    string ManagerPath,
    string ShimPath,
    ShimMaterialization Materialization,
    bool Changed,
    bool DryRun);

public sealed class ShimInstaller : IShimInstaller
{
    public const int ManifestSchema = 1;
    public const string ManifestFileName = ".eidosup-shims.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private readonly string _sourceExecutable;
    private readonly Func<DateTimeOffset> _clock;

    public ShimInstaller(string? sourceExecutable = null, Func<DateTimeOffset>? clock = null)
    {
        _sourceExecutable = Path.GetFullPath(sourceExecutable ?? ResolveCurrentExecutable());
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<ShimInstallResult> InstallAsync(
        ToolInstallLayout layout,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ValidateSource();

        var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        var managerPath = Path.Combine(layout.BinDirectory, $"eidosup{extension}");
        var shimPath = Path.Combine(layout.BinDirectory, $"eidosc{extension}");
        var manifestPath = Path.Combine(layout.BinDirectory, ManifestFileName);
        if (dryRun)
        {
            return new ShimInstallResult(
                managerPath,
                shimPath,
                ShimMaterialization.HardLink,
                Changed: true,
                DryRun: true);
        }

        Directory.CreateDirectory(layout.BinDirectory);
        var existingManifest = await ReadManifestAsync(manifestPath, cancellationToken);
        ValidateOwnedTargets(existingManifest, managerPath, shimPath, manifestPath);
        await ValidateOwnedContentAsync(existingManifest, managerPath, shimPath, cancellationToken);

        var managerChanged = !ToolInstallLayout.PathEquals(_sourceExecutable, managerPath) &&
                             !await FilesEqualAsync(_sourceExecutable, managerPath, cancellationToken);
        var managerSource = managerChanged ? null : managerPath;
        var identifier = Guid.NewGuid().ToString("N");
        var managerTemporary = Path.Combine(layout.BinDirectory, $".eidosup.{identifier}.tmp");
        var shimTemporary = Path.Combine(layout.BinDirectory, $".eidosc.{identifier}.tmp");
        var manifestTemporary = Path.Combine(layout.BinDirectory, $"{ManifestFileName}.{identifier}.tmp");
        var managerBackup = Path.Combine(layout.BinDirectory, $".eidosup.{identifier}.bak");
        var shimBackup = Path.Combine(layout.BinDirectory, $".eidosc.{identifier}.bak");
        var manifestBackup = Path.Combine(layout.BinDirectory, $"{ManifestFileName}.{identifier}.bak");
        var managerCommitted = false;
        var shimCommitted = false;
        var manifestCommitted = false;
        var managerBackedUp = false;
        var shimBackedUp = false;
        var manifestBackedUp = false;
        var materialization = ShimMaterialization.HardLink;

        try
        {
            if (managerChanged)
            {
                await CopyDurableAsync(_sourceExecutable, managerTemporary, cancellationToken);
                managerSource = managerTemporary;
            }

            managerSource ??= managerPath;
            var shimChanged = managerChanged ||
                              !await FilesEqualAsync(managerSource, shimPath, cancellationToken);
            var managerDigest = await HashAsync(managerSource, cancellationToken);
            var productVersion = GetProductVersion();
            if (!managerChanged &&
                !shimChanged &&
                existingManifest != null &&
                string.Equals(existingManifest.Sha256, managerDigest, StringComparison.Ordinal) &&
                string.Equals(existingManifest.ProductVersion, productVersion, StringComparison.Ordinal))
            {
                return new ShimInstallResult(
                    managerPath,
                    shimPath,
                    existingManifest.Materialization,
                    Changed: false,
                    DryRun: false);
            }

            if (shimChanged)
            {
                materialization = TryCreateHardLink(shimTemporary, managerSource)
                    ? ShimMaterialization.HardLink
                    : ShimMaterialization.Copy;
                if (materialization == ShimMaterialization.Copy)
                {
                    await CopyDurableAsync(managerSource, shimTemporary, cancellationToken);
                }
                else
                {
                    CopyUnixMode(managerSource, shimTemporary);
                }
            }
            else
            {
                materialization = existingManifest?.Materialization ?? ShimMaterialization.Copy;
            }

            var newManifest = new ShimInstallManifest(
                ManifestSchema,
                Path.GetFileName(managerPath),
                Path.GetFileName(shimPath),
                managerDigest,
                productVersion,
                materialization,
                _clock());
            await WriteManifestAsync(manifestTemporary, newManifest, cancellationToken);

            if (managerChanged && File.Exists(managerPath))
            {
                File.Move(managerPath, managerBackup);
                managerBackedUp = true;
            }
            if (shimChanged && File.Exists(shimPath))
            {
                File.Move(shimPath, shimBackup);
                shimBackedUp = true;
            }
            if (File.Exists(manifestPath))
            {
                File.Move(manifestPath, manifestBackup);
                manifestBackedUp = true;
            }

            if (managerChanged)
            {
                File.Move(managerTemporary, managerPath);
                managerCommitted = true;
            }
            if (shimChanged)
            {
                File.Move(shimTemporary, shimPath);
                shimCommitted = true;
            }
            File.Move(manifestTemporary, manifestPath);
            manifestCommitted = true;

            DeleteIfExists(managerBackup);
            DeleteIfExists(shimBackup);
            DeleteIfExists(manifestBackup);
            return new ShimInstallResult(
                managerPath,
                shimPath,
                materialization,
                Changed: true,
                DryRun: false);
        }
        catch
        {
            if (manifestCommitted) DeleteIfExists(manifestPath);
            if (shimCommitted) DeleteIfExists(shimPath);
            if (managerCommitted) DeleteIfExists(managerPath);
            if (manifestBackedUp) File.Move(manifestBackup, manifestPath, overwrite: true);
            if (shimBackedUp) File.Move(shimBackup, shimPath, overwrite: true);
            if (managerBackedUp) File.Move(managerBackup, managerPath, overwrite: true);
            throw;
        }
        finally
        {
            DeleteIfExists(managerTemporary);
            DeleteIfExists(shimTemporary);
            DeleteIfExists(manifestTemporary);
            DeleteIfExists(managerBackup);
            DeleteIfExists(shimBackup);
            DeleteIfExists(manifestBackup);
        }
    }

    private void ValidateSource()
    {
        if (!File.Exists(_sourceExecutable) ||
            (File.GetAttributes(_sourceExecutable) & FileAttributes.ReparsePoint) != 0)
        {
            throw new EidosupException(
                EidosupErrorCode.InstallFailure,
                EidosupExitCodes.InstallFailure,
                $"The running Eidosup executable '{_sourceExecutable}' is not a regular file.",
                "Run setup from an extracted or downloaded Eidosup executable.");
        }
    }

    private static string ResolveCurrentExecutable()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new EidosupException(
                EidosupErrorCode.InstallFailure,
                EidosupExitCodes.InstallFailure,
                "The current Eidosup executable path could not be determined.");
        }

        var name = Path.GetFileNameWithoutExtension(path);
        if (string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            throw new EidosupException(
                EidosupErrorCode.InstallFailure,
                EidosupExitCodes.InstallFailure,
                "Eidosup cannot install a stable shim while running through the shared dotnet host.",
                "Run setup from a self-contained Eidosup executable or its apphost.");
        }

        return path;
    }

    private static void ValidateOwnedTargets(
        ShimInstallManifest? manifest,
        string managerPath,
        string shimPath,
        string manifestPath)
    {
        if (manifest == null)
        {
            if (File.Exists(managerPath) || File.Exists(shimPath) || File.Exists(manifestPath))
            {
                throw Conflict("The managed bin directory contains unowned eidosup/eidosc files.");
            }

            return;
        }

        if (manifest.Schema != ManifestSchema ||
            !string.Equals(manifest.ManagerFile, Path.GetFileName(managerPath), StringComparison.Ordinal) ||
            !string.Equals(manifest.ShimFile, Path.GetFileName(shimPath), StringComparison.Ordinal) ||
            !ToolchainIdentity.IsCanonicalSha256(manifest.Sha256) ||
            string.IsNullOrWhiteSpace(manifest.ProductVersion) ||
            !Enum.IsDefined(manifest.Materialization) ||
            manifest.InstalledAt == default)
        {
            throw Conflict("The stable shim ownership manifest is invalid or unsupported.");
        }

        foreach (var path in new[] { managerPath, shimPath })
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw Conflict($"Managed shim path '{path}' is a link or reparse point.");
            }
        }
    }

    private static async Task<ShimInstallManifest?> ReadManifestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw Conflict("The stable shim ownership manifest is a link or reparse point.");
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<ShimInstallManifest>(
                stream,
                JsonOptions,
                cancellationToken);
        }
        catch (JsonException exception)
        {
            throw Conflict("The stable shim ownership manifest is malformed.", exception);
        }
    }

    private static async Task ValidateOwnedContentAsync(
        ShimInstallManifest? manifest,
        string managerPath,
        string shimPath,
        CancellationToken cancellationToken)
    {
        if (manifest == null)
        {
            return;
        }

        foreach (var path in new[] { managerPath, shimPath })
        {
            if (!File.Exists(path) ||
                !string.Equals(await HashAsync(path, cancellationToken), manifest.Sha256, StringComparison.Ordinal))
            {
                throw Conflict($"Managed shim path '{path}' no longer matches its ownership manifest.");
            }
        }
    }

    private static async Task WriteManifestAsync(
        string path,
        ShimInstallManifest manifest,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static async Task CopyDurableAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        const int BufferSize = 1024 * 1024;
        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, BufferSize, cancellationToken);
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
        CopyUnixMode(source, destination);
    }

    private static async Task<bool> FilesEqualAsync(
        string left,
        string right,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(left) || !File.Exists(right))
        {
            return false;
        }

        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        var leftHash = await HashAsync(left, cancellationToken);
        var rightHash = await HashAsync(right, cancellationToken);
        return string.Equals(leftHash, rightHash, StringComparison.Ordinal);
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static bool TryCreateHardLink(string linkPath, string targetPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateHardLinkWindows(linkPath, targetPath, IntPtr.Zero);
        }

        return CreateHardLinkUnix(targetPath, linkPath) == 0;
    }

    private static void CopyUnixMode(string source, string destination)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
        }
    }

    private static string GetProductVersion()
    {
        var assembly = typeof(ShimInstaller).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? assembly.GetName().Version?.ToString()
               ?? "unknown";
    }

    private static EidosupException Conflict(string message, Exception? innerException = null) => new(
        EidosupErrorCode.InstallConflict,
        EidosupExitCodes.InstallConflict,
        message,
        "Preserve the existing files for inspection, then remove them manually before reinstalling the Eidosup-managed shims.",
        innerException);

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int CreateHardLinkUnix(string existingPath, string newPath);

    private sealed record ShimInstallManifest(
        int Schema,
        string ManagerFile,
        string ShimFile,
        string Sha256,
        string ProductVersion,
        ShimMaterialization Materialization,
        DateTimeOffset InstalledAt);
}
