using System.Text.RegularExpressions;
using Eidosup.Diagnostics;

namespace Eidosup.Installation;

public enum DependencyHealth
{
    Compatible,
    Missing,
    Incompatible
}

public sealed record DependencyRequirement(
    string Id,
    string DisplayName,
    int MinimumMajorVersion,
    int MaximumMajorVersion,
    IReadOnlyList<string> RequiredCommands);

public static class EidosDependencyRequirements
{
    public static DependencyRequirement Llvm { get; } = new(
        "llvm",
        "LLVM/Clang",
        MinimumMajorVersion: 18,
        MaximumMajorVersion: 22,
        RequiredCommands: ["clang", "llvm-ar"]);
}

public sealed record CommandCaptureResult(int ExitCode, string StandardOutput, string StandardError);

public sealed record DependencyProbeResult(
    DependencyRequirement Requirement,
    DependencyHealth Health,
    int? DetectedMajorVersion,
    string? DetectedVersion,
    string? ClangPath,
    string? LlvmHome,
    IReadOnlyDictionary<string, string> Commands,
    IReadOnlyList<string> MissingCommands,
    string Detail)
{
    public bool IsCompatible => Health == DependencyHealth.Compatible;
}

public interface ILlvmDependencyProbe
{
    Task<DependencyProbeResult> ProbeAsync(CancellationToken cancellationToken);
}

public sealed class LlvmDependencyProbe : ILlvmDependencyProbe
{
    private static readonly Regex VersionPattern = new(
        @"(?:Apple\s+)?clang\s+version\s+(?<version>[0-9]+(?:\.[0-9]+){0,2})",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly Func<string, string?> _findCommand;
    private readonly Func<string, string, CancellationToken, Task<CommandCaptureResult>> _capture;

    public LlvmDependencyProbe(
        Func<string, string?>? findCommand = null,
        Func<string, string, CancellationToken, Task<CommandCaptureResult>>? capture = null)
    {
        _findCommand = findCommand ?? LlvmCommandLocator.TryFind;
        _capture = capture ?? ProcessRunner.CaptureAsync;
    }

    public async Task<DependencyProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        var requirement = EidosDependencyRequirements.Llvm;
        var commands = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var command in requirement.RequiredCommands)
        {
            if (_findCommand(command) is { } path)
            {
                commands.Add(command, path);
            }
        }

        var missing = requirement.RequiredCommands
            .Where(command => !commands.ContainsKey(command))
            .ToArray();
        if (!commands.TryGetValue("clang", out var clangPath))
        {
            return new DependencyProbeResult(
                requirement,
                DependencyHealth.Missing,
                null,
                null,
                null,
                null,
                commands,
                missing,
                $"Missing required command{(missing.Length == 1 ? string.Empty : "s")}: {string.Join(", ", missing)}.");
        }

        var capture = await _capture(clangPath, "--version", cancellationToken);
        var output = string.Join('\n', new[] { capture.StandardOutput, capture.StandardError }
            .Where(static value => !string.IsNullOrWhiteSpace(value)));
        var match = VersionPattern.Match(output);
        if (capture.ExitCode != 0 || !match.Success ||
            !int.TryParse(match.Groups["version"].Value.Split('.')[0], out var major))
        {
            return new DependencyProbeResult(
                requirement,
                DependencyHealth.Incompatible,
                null,
                null,
                clangPath,
                ResolveLlvmHome(clangPath),
                commands,
                missing,
                "clang was found, but its version could not be verified.");
        }

        var version = match.Groups["version"].Value;
        var compatible = major >= requirement.MinimumMajorVersion && major <= requirement.MaximumMajorVersion;
        if (!compatible)
        {
            return new DependencyProbeResult(
                requirement,
                DependencyHealth.Incompatible,
                major,
                version,
                clangPath,
                ResolveLlvmHome(clangPath),
                commands,
                missing,
                $"LLVM/Clang {version} is outside supported major versions {requirement.MinimumMajorVersion}-{requirement.MaximumMajorVersion}.");
        }

        if (missing.Length > 0)
        {
            return new DependencyProbeResult(
                requirement,
                DependencyHealth.Missing,
                major,
                version,
                clangPath,
                ResolveLlvmHome(clangPath),
                commands,
                missing,
                $"LLVM/Clang {version} is compatible, but required command{(missing.Length == 1 ? string.Empty : "s")} {string.Join(", ", missing)} {(missing.Length == 1 ? "was" : "were")} not found.");
        }

        return new DependencyProbeResult(
            requirement,
            DependencyHealth.Compatible,
            major,
            version,
            clangPath,
            ResolveLlvmHome(clangPath),
            commands,
            [],
            $"LLVM/Clang {version} satisfies supported major versions {requirement.MinimumMajorVersion}-{requirement.MaximumMajorVersion}.");
    }

    private static string? ResolveLlvmHome(string? clangPath)
    {
        var binDirectory = clangPath == null ? null : Path.GetDirectoryName(clangPath);
        return binDirectory == null ? null : Directory.GetParent(binDirectory)?.FullName;
    }
}

public static class LlvmCommandLocator
{
    public static string? TryFind(string command)
    {
        if (CommandProbe.TryFind(command) is { } path)
        {
            return path;
        }

        foreach (var binDirectory in KnownBinDirectories())
        {
            var candidate = Path.Combine(
                binDirectory,
                OperatingSystem.IsWindows() && !command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? $"{command}.exe"
                    : command);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> KnownBinDirectories()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LLVM", "bin");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LLVM", "bin");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "apps", "llvm", "current", "bin");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/opt/homebrew/opt/llvm/bin";
            yield return "/usr/local/opt/llvm/bin";
        }
    }
}

public static class DependencyErrors
{
    public static EidosupException Incompatible(DependencyProbeResult result) => new(
        EidosupErrorCode.DependencyIncompatible,
        EidosupExitCodes.DependencyIncompatible,
        result.Detail,
        $"Install a supported {result.Requirement.DisplayName} major version between {result.Requirement.MinimumMajorVersion} and {result.Requirement.MaximumMajorVersion}, then retry.");

    public static EidosupException ProviderUnavailable(string detail) => new(
        EidosupErrorCode.DependencyProviderUnavailable,
        EidosupExitCodes.DependencyProviderUnavailable,
        detail,
        "Install LLVM/Clang manually or make a supported package manager available, then retry.");

    public static EidosupException InstallFailed(DependencyProbeResult result) => new(
        EidosupErrorCode.DependencyInstallFailure,
        EidosupExitCodes.DependencyInstallFailure,
        $"LLVM dependency installation completed, but verification failed: {result.Detail}",
        "Inspect the package-manager output and ensure the supported LLVM bin directory is on PATH.");
}
