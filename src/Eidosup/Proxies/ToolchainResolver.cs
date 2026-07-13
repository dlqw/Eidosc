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
                Path.Combine(verified.RootDirectory, "stdlib"),
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
                installed.DistributionManifestSha256,
                cancellationToken,
                installed.Rid,
                installed.Version))
        {
            throw Corrupt($"Toolchain '{installed.Id}' failed activation-time manifest verification.");
        }

        var commandPath = Path.Combine(toolchainDirectory, platform.ExecutableName);
        var runtimePath = Path.Combine(toolchainDirectory, "runtime");
        var stdlibPath = Path.Combine(toolchainDirectory, "stdlib");
        if (!File.Exists(commandPath) ||
            (File.GetAttributes(commandPath) & FileAttributes.ReparsePoint) != 0 ||
            Directory.Exists(runtimePath) && (File.GetAttributes(runtimePath) & FileAttributes.ReparsePoint) != 0 ||
            !Directory.Exists(stdlibPath) ||
            (File.GetAttributes(stdlibPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw Corrupt($"Toolchain '{installed.Id}' does not contain the required eidosc/stdlib layout.");
        }

        var managedCompatibility = await ToolchainCompatibilityVerifier.VerifyAsync(
            currentDirectory,
            toolchainDirectory,
            cancellationToken,
            installed.Version);
        EnsureProjectComposition(selection, manifest);
        return new ResolvedToolchain(
            selected.Selector,
            selection.Source,
            installed.Id,
            toolchainDirectory,
            commandPath,
            runtimePath,
            stdlibPath,
            layout.RootDirectory,
            selection.SourcePath,
            IsCustom: false,
            managedCompatibility);
    }

    private static void EnsureProjectComposition(ToolchainSelection selection, InstallManifest manifest)
    {
        var configuration = selection.ProjectConfiguration;
        if (configuration == null)
        {
            return;
        }

        var installedProfile = ToolchainComponentSolver.ParseProfile(manifest.Profile);
        var requestedProfile = ToolchainComponentSolver.ParseProfile(configuration.Profile);
        var profileMatches = installedProfile >= requestedProfile;
        var missingComponents = configuration.Components.Where(requested =>
            !manifest.Components.Any(component =>
                string.Equals(component.Id, requested, StringComparison.Ordinal) ||
                component.Target == null && string.Equals(component.Name, requested, StringComparison.Ordinal))).ToArray();
        var missingTargets = configuration.Targets.Except(manifest.Targets, StringComparer.Ordinal).ToArray();
        if (profileMatches && missingComponents.Length == 0 && missingTargets.Length == 0)
        {
            return;
        }

        var exception = new EidosupException(
            EidosupErrorCode.ToolchainUnavailable,
            EidosupExitCodes.ToolchainUnavailable,
            $"Toolchain '{selection.Selector.Selector}' does not satisfy component requirements in '{configuration.FilePath}'.",
            "Run eidosup through an enabled auto-install policy or synchronize the selected profile, components, and targets explicitly.");
        exception.Data["selector"] = selection.Selector.Selector;
        exception.Data["projectConfiguration"] = configuration;
        throw exception;
    }

    private static bool MatchesState(InstallManifest manifest, InstalledToolchainState installed) =>
        string.Equals(manifest.ToolchainId, installed.Id, StringComparison.Ordinal) &&
        string.Equals(manifest.IdentitySha256, installed.IdentitySha256, StringComparison.Ordinal) &&
        string.Equals(manifest.CompositionSha256, installed.CompositionSha256, StringComparison.Ordinal) &&
        string.Equals(manifest.DistributionManifestName, installed.DistributionManifestName, StringComparison.Ordinal) &&
        string.Equals(manifest.DistributionManifestSha256, installed.DistributionManifestSha256, StringComparison.Ordinal) &&
        string.Equals(manifest.ReleaseTag, installed.ReleaseTag, StringComparison.Ordinal) &&
        string.Equals(manifest.Version, installed.Version, StringComparison.Ordinal) &&
        string.Equals(manifest.Rid, installed.Rid, StringComparison.Ordinal) &&
        string.Equals(manifest.Source, installed.Source, StringComparison.Ordinal) &&
        string.Equals(manifest.Profile, installed.Profile, StringComparison.Ordinal) &&
        manifest.ExplicitComponents.SequenceEqual(installed.ExplicitComponents, StringComparer.Ordinal) &&
        manifest.ExplicitTargets.SequenceEqual(installed.ExplicitTargets, StringComparer.Ordinal) &&
        ComponentsEqual(manifest.Components, installed.Components) &&
        manifest.Targets.SequenceEqual(installed.Targets) &&
        manifest.Artifacts.SequenceEqual(installed.Artifacts) &&
        manifest.InstalledAt == installed.InstalledAt;

    private static bool ComponentsEqual(
        IReadOnlyList<InstalledComponent> left,
        IReadOnlyList<InstalledComponent> right) =>
        left.Count == right.Count && left.Zip(right).All(pair =>
            string.Equals(pair.First.Id, pair.Second.Id, StringComparison.Ordinal) &&
            string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal) &&
            string.Equals(pair.First.Version, pair.Second.Version, StringComparison.Ordinal) &&
            pair.First.Required == pair.Second.Required &&
            string.Equals(pair.First.Target, pair.Second.Target, StringComparison.Ordinal) &&
            pair.First.Files.SequenceEqual(pair.Second.Files, StringComparer.Ordinal));

    private static EidosupException Corrupt(string message) => new(
        EidosupErrorCode.StateCorrupt,
        EidosupExitCodes.StateCorrupt,
        message,
        "Run eidosup doctor, then reinstall the affected toolchain before activating it.");
}
