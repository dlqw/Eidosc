using System.Security.Cryptography;
using Eidosup.Diagnostics;
using Eidosup.Installation;
using Eidosup.Toolchains;

namespace Eidosup.Proxies;

public sealed class ToolchainResolver
{
    public async Task<ResolvedToolchain> ResolveAsync(
        ToolInstallLayout layout,
        string commandName,
        string? selector,
        CancellationToken cancellationToken)
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
        ToolchainSelectorState selected;
        ToolchainSelectionSource source;
        if (selector == null)
        {
            if (state.Default == null)
            {
                throw new EidosupException(
                    EidosupErrorCode.NoActiveToolchain,
                    EidosupExitCodes.NoActiveToolchain,
                    "No default Eidos toolchain is configured.",
                    "Run eidosup setup to install and activate a verified toolchain.");
            }

            selected = state.Selectors.SingleOrDefault(candidate =>
                           string.Equals(candidate.Selector, state.Default.Selector, StringComparison.Ordinal) &&
                           string.Equals(candidate.ToolchainId, state.Default.ToolchainId, StringComparison.Ordinal))
                       ?? throw Corrupt("The default toolchain does not match a registered selector.");
            source = ToolchainSelectionSource.Default;
        }
        else
        {
            selected = state.Selectors.SingleOrDefault(candidate =>
                           string.Equals(candidate.Selector, selector, StringComparison.Ordinal))
                       ?? throw new EidosupException(
                           EidosupErrorCode.ToolchainUnavailable,
                           EidosupExitCodes.ToolchainUnavailable,
                           $"Toolchain selector '{selector}' is not installed.",
                           "Install the requested selector before trying to run it.");
            source = ToolchainSelectionSource.Explicit;
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

        return new ResolvedToolchain(
            selected.Selector,
            source,
            installed.Id,
            toolchainDirectory,
            commandPath,
            runtimePath,
            layout.RootDirectory);
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
