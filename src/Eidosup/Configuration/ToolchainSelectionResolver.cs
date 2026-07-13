using Eidosup.Diagnostics;
using Eidosup.Installation;
using Eidosup.Proxies;
using Eidosup.Toolchains;

namespace Eidosup.Configuration;

public sealed record ToolchainSelection(
    ToolchainSelectorState Selector,
    ToolchainSelectionSource Source,
    string? SourcePath);

public sealed class ToolchainSelectionResolver
{
    public async Task<ToolchainSelection> SelectAsync(
        ToolchainState state,
        string? explicitSelector,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (explicitSelector != null)
        {
            return Create(state, ToolchainSpec.Parse(explicitSelector).Canonical, ToolchainSelectionSource.Explicit, null);
        }

        var environmentSelector = Environment.GetEnvironmentVariable("EIDOSUP_TOOLCHAIN");
        if (!string.IsNullOrWhiteSpace(environmentSelector))
        {
            if (!string.Equals(environmentSelector, environmentSelector.Trim(), StringComparison.Ordinal))
            {
                throw InvalidEnvironment(environmentSelector);
            }

            return Create(
                state,
                ToolchainSpec.Parse(environmentSelector).Canonical,
                ToolchainSelectionSource.Environment,
                "EIDOSUP_TOOLCHAIN");
        }

        foreach (var directory in EnumerateAncestors(workingDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectPath = Path.Combine(directory, ProjectToolchainConfigurationReader.FileName);
            if (File.Exists(projectPath))
            {
                var configuration = await ProjectToolchainConfigurationReader.ReadAsync(projectPath, cancellationToken);
                return Create(
                    state,
                    configuration.Toolchain.Canonical,
                    ToolchainSelectionSource.ProjectFile,
                    configuration.FilePath);
            }

            var overrideState = state.Overrides.SingleOrDefault(candidate =>
                ToolInstallLayout.PathEquals(candidate.Directory, directory));
            if (overrideState != null)
            {
                return Create(
                    state,
                    overrideState.Selector,
                    ToolchainSelectionSource.DirectoryOverride,
                    overrideState.Directory);
            }
        }

        if (state.Default == null)
        {
            throw new EidosupException(
                EidosupErrorCode.NoActiveToolchain,
                EidosupExitCodes.NoActiveToolchain,
                "No Eidos toolchain was selected by an explicit argument, EIDOSUP_TOOLCHAIN, project pin, directory override, or global default.",
                "Install a toolchain and set a default, or create eidos-toolchain.toml for the project.");
        }

        var selected = state.Selectors.SingleOrDefault(candidate =>
                           string.Equals(candidate.Selector, state.Default.Selector, StringComparison.Ordinal) &&
                           string.Equals(candidate.ToolchainId, state.Default.ToolchainId, StringComparison.Ordinal))
                       ?? throw Corrupt("The global default does not match a registered selector.");
        return new ToolchainSelection(selected, ToolchainSelectionSource.Default, null);
    }

    private static ToolchainSelection Create(
        ToolchainState state,
        string selector,
        ToolchainSelectionSource source,
        string? sourcePath)
    {
        var selected = state.Selectors.SingleOrDefault(candidate =>
            string.Equals(candidate.Selector, selector, StringComparison.Ordinal));
        if (selected == null)
        {
            var exception = new EidosupException(
                EidosupErrorCode.ToolchainUnavailable,
                EidosupExitCodes.ToolchainUnavailable,
                $"Toolchain selector '{selector}' selected by {Describe(source, sourcePath)} is not installed or linked.",
                $"Run 'eidosup toolchain install {selector}' or change the selecting configuration.");
            exception.Data["selector"] = selector;
            throw exception;
        }

        return new ToolchainSelection(selected, source, sourcePath);
    }

    private static IEnumerable<string> EnumerateAncestors(string workingDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(workingDirectory));
        while (directory != null)
        {
            yield return directory.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            directory = directory.Parent;
        }
    }

    private static string Describe(ToolchainSelectionSource source, string? sourcePath) => source switch
    {
        ToolchainSelectionSource.Environment => "EIDOSUP_TOOLCHAIN",
        ToolchainSelectionSource.ProjectFile => $"project file '{sourcePath}'",
        ToolchainSelectionSource.DirectoryOverride => $"directory override '{sourcePath}'",
        ToolchainSelectionSource.Explicit => "an explicit command argument",
        _ => "the global default"
    };

    private static EidosupException InvalidEnvironment(string value) => new(
        EidosupErrorCode.InvalidArgument,
        EidosupExitCodes.InvalidArgument,
        $"EIDOSUP_TOOLCHAIN value '{value}' contains leading or trailing whitespace.",
        "Set EIDOSUP_TOOLCHAIN to a canonical selector such as preview, 0.4.0-alpha.2, or custom:local.");

    private static EidosupException Corrupt(string message) => new(
        EidosupErrorCode.StateCorrupt,
        EidosupExitCodes.StateCorrupt,
        message,
        "Run eidosup doctor and repair the selecting state before activating a toolchain.");
}
