using System.Security.Cryptography;
using Eidosup.Configuration;
using Eidosup.Diagnostics;
using Eidosup.Installation;
using Eidosup.Toolchains;

namespace Eidosup.Proxies;

public sealed class ToolchainResolver
{
    private readonly ToolchainSelectionResolver _selectionResolver;

    public ToolchainResolver(ToolchainSelectionResolver? selectionResolver = null)
    {
        _selectionResolver = selectionResolver ?? new ToolchainSelectionResolver();
    }

    public async Task<ResolvedToolchain> ResolveAsync(
        ToolInstallLayout layout,
        string commandName,
        string? selector,
        CancellationToken cancellationToken,
        string? workingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!string.Equals(commandName, "eidosc", StringComparison.Ordinal))
        {
            throw new EidosupException(
                EidosupErrorCode.ToolchainUnavailable,
                EidosupExitCodes.ToolchainUnavailable,
                $"The command '{commandName}' is not provided by the active Eidos toolchain.");
        }

        var state = await ToolchainStateStore.ReadAsync(layout, cancellationToken);
        var currentDirectory = workingDirectory ?? Environment.CurrentDirectory;
        var selection = await _selectionResolver.SelectAsync(
            state,
            selector,
            currentDirectory,
            cancellationToken);
        var selected = selection.Selector;
        if (selected.Kind == ToolchainSelectorKind.Custom)
        {
            var custom = state.CustomToolchains.SingleOrDefault(candidate =>
                             string.Equals(candidate.ToolchainId, selected.ToolchainId, StringComparison.Ordinal))
                         ?? throw Corrupt($"Custom selector '{selected.Selector}' refers to a missing link.");
            CustomToolchainState verified;
            try
            {
                verified = CustomToolchain.ValidateAndCreate(custom.Name, custom.RootDirectory, custom.LinkedAt);
            }
            catch (EidosupException exception)
            {
                throw new EidosupException(
                    exception.Code,
                    exception.ExitCode,
                    $"Custom toolchain '{custom.Name}' is no longer usable: {exception.Message}",
                    $"Repair the external path or run 'eidosup toolchain unlink {custom.Name}'.",
                    exception);
            }

            var compatibility = await ToolchainCompatibilityVerifier.VerifyAsync(
                currentDirectory,
                verified.RootDirectory,
                cancellationToken);
            return new ResolvedToolchain(
                selected.Selector,
                selection.Source,
                custom.ToolchainId,
                verified.RootDirectory,
                verified.CommandPath,
                verified.RuntimePath,
                layout.RootDirectory,
                selection.SourcePath,
                IsCustom: true,
                compatibility);
        }

        var installed = state.Toolchains.SingleOrDefault(candidate =>
                            string.Equals(candidate.Id, selected.ToolchainId, StringComparison.Ordinal))
                        ?? throw Corrupt($"Selector '{selected.Selector}' refers to a missing toolchain.");
        var platform = PlatformContext.Detect();
        if (!string.Equals(installed.Rid, platform.Rid, StringComparison.Ordinal))
        {
            throw new EidosupException(
                EidosupErrorCode.ToolchainUnavailable,
                EidosupExitCodes.ToolchainUnavailable,
                $"Toolchain '{installed.Id}' targets '{installed.Rid}', but this host is '{platform.Rid}'.",
                "Install a toolchain for the current host before activating it.");
        }

        var toolchainDirectory = layout.GetToolchainDirectory(installed.Id);
        var manifestPath = Path.Combine(toolchainDirectory, InstallManifest.FileName);
        if (!File.Exists(manifestPath) ||
            (File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw Corrupt($"Toolchain '{installed.Id}' has no regular install manifest.");
        }

        await using (var manifestStream = new FileStream(
                         manifestPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         bufferSize: 32 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var manifestDigest = Convert.ToHexString(
                    await SHA256.HashDataAsync(manifestStream, cancellationToken))
                .ToLowerInvariant();
            if (!string.Equals(manifestDigest, installed.InstallManifestSha256, StringComparison.Ordinal))
            {
                throw Corrupt($"Toolchain '{installed.Id}' install manifest has changed since registration.");
            }
        }

        var manifest = await InstallManifest.TryReadAsync(toolchainDirectory, cancellationToken);
        if (manifest == null ||
            !MatchesState(manifest, installed) ||
            !await manifest.VerifyAsync(
                toolchainDirectory,
                installed.AssetSha256,
                cancellationToken,
                installed.Rid,
                installed.Version))
        {
            throw Corrupt($"Toolchain '{installed.Id}' failed activation-time manifest verification.");
        }

        var commandPath = Path.Combine(toolchainDirectory, platform.ExecutableName);
        var runtimePath = Path.Combine(toolchainDirectory, "runtime");
        if (!File.Exists(commandPath) ||
            (File.GetAttributes(commandPath) & FileAttributes.ReparsePoint) != 0 ||
            !Directory.Exists(runtimePath) ||
            (File.GetAttributes(runtimePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw Corrupt($"Toolchain '{installed.Id}' does not contain the required eidosc/runtime layout.");
        }

        var managedCompatibility = await ToolchainCompatibilityVerifier.VerifyAsync(
            currentDirectory,
            toolchainDirectory,
            cancellationToken,
            installed.Version);
        return new ResolvedToolchain(
            selected.Selector,
            selection.Source,
            installed.Id,
            toolchainDirectory,
            commandPath,
            runtimePath,
            layout.RootDirectory,
            selection.SourcePath,
            IsCustom: false,
            managedCompatibility);
    }

    private static bool MatchesState(InstallManifest manifest, InstalledToolchainState installed) =>
        string.Equals(manifest.ToolchainId, installed.Id, StringComparison.Ordinal) &&
        string.Equals(manifest.ManifestSha256, installed.ManifestSha256, StringComparison.Ordinal) &&
        string.Equals(manifest.ReleaseTag, installed.ReleaseTag, StringComparison.Ordinal) &&
        string.Equals(manifest.Version, installed.Version, StringComparison.Ordinal) &&
        string.Equals(manifest.Rid, installed.Rid, StringComparison.Ordinal) &&
        string.Equals(manifest.Source, installed.Source, StringComparison.Ordinal) &&
        string.Equals(manifest.AssetName, installed.AssetName, StringComparison.Ordinal) &&
        string.Equals(manifest.AssetSha256, installed.AssetSha256, StringComparison.Ordinal) &&
        manifest.AssetSize == installed.AssetSize &&
        manifest.InstalledAt == installed.InstalledAt;

    private static EidosupException Corrupt(string message) => new(
        EidosupErrorCode.StateCorrupt,
        EidosupExitCodes.StateCorrupt,
        message,
        "Run eidosup doctor, then reinstall the affected toolchain before activating it.");
}
