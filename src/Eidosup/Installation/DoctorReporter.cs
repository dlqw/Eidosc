using System.Text.Json;
using System.Text.Json.Serialization;
using Eidosup.Diagnostics;
using Eidosup.Proxies;
using Eidosup.Toolchains;

namespace Eidosup.Installation;

public enum DoctorCheckStatus
{
    Pass,
    Warning,
    Fail
}

public enum DoctorSeverity
{
    Info,
    Warning,
    Error
}

public sealed record DoctorCheck(
    string Id,
    DoctorCheckStatus Status,
    DoctorSeverity Severity,
    string Summary,
    string? Detail = null,
    string? Remediation = null);

public sealed record DoctorReport(string Platform, IReadOnlyList<DoctorCheck> Checks)
{
    public int SchemaVersion => 1;

    public bool Healthy => Checks.All(static check => check.Status != DoctorCheckStatus.Fail || check.Severity != DoctorSeverity.Error);
}

public interface IDoctorEnvironment
{
    PlatformContext DetectPlatform();

    string? FindCommand(string command);

    string? GetEnvironmentVariable(string name);

    string GetFullPath(string path);

    bool DirectoryExists(string path);

    bool FileExists(string path);

    IEnumerable<string> EnumerateDirectories(string path);
}

public interface IToolchainStateReader
{
    Task<ToolchainState> ReadAsync(
        PlatformContext platform,
        string installRoot,
        CancellationToken cancellationToken);
}

public sealed class SystemToolchainStateReader : IToolchainStateReader
{
    public Task<ToolchainState> ReadAsync(
        PlatformContext platform,
        string installRoot,
        CancellationToken cancellationToken) =>
        ToolchainStateStore.ReadVerifiedAsync(
            ToolInstallLayout.Create(platform, installRoot, downloadRoot: null),
            cancellationToken);
}

public sealed class SystemDoctorEnvironment : IDoctorEnvironment
{
    public PlatformContext DetectPlatform() => PlatformContext.Detect();

    public string? FindCommand(string command) => CommandProbe.TryFind(command);

    public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

    public string GetFullPath(string path) => Path.GetFullPath(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool FileExists(string path) => File.Exists(path);

    public IEnumerable<string> EnumerateDirectories(string path) => Directory.EnumerateDirectories(path);
}

public sealed class DoctorReporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly IDoctorEnvironment _environment;
    private readonly ILlvmDependencyProbe _dependencyProbe;
    private readonly IToolchainStateReader _stateReader;

    public DoctorReporter(
        IDoctorEnvironment? environment = null,
        ILlvmDependencyProbe? dependencyProbe = null,
        IToolchainStateReader? stateReader = null)
    {
        _environment = environment ?? new SystemDoctorEnvironment();
        _dependencyProbe = dependencyProbe ?? new LlvmDependencyProbe();
        _stateReader = stateReader ?? new SystemToolchainStateReader();
    }

    public async Task<int> RunAsync(
        string? installRootOverride,
        bool json,
        TextWriter? writer = null,
        CancellationToken cancellationToken = default)
    {
        writer ??= Console.Out;
        var report = await EvaluateAsync(installRootOverride, cancellationToken);
        if (json)
        {
            writer.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        }
        else
        {
            WriteHumanReadable(report, writer);
        }

        return report.Healthy ? EidosupExitCodes.Success : EidosupExitCodes.DoctorUnhealthy;
    }

    public async Task<DoctorReport> EvaluateAsync(
        string? installRootOverride,
        CancellationToken cancellationToken = default)
    {
        var platform = _environment.DetectPlatform();
        var checks = new List<DoctorCheck>
        {
            new(
                "platform.supported",
                DoctorCheckStatus.Pass,
                DoctorSeverity.Info,
                $"Host platform '{platform.Rid}' is supported.")
        };

        AddCommandCheck(
            checks,
            "command.eidosc",
            platform.ExecutableName,
            DoctorSeverity.Error,
            "Run 'eidosup setup' to install a verified Eidosc toolchain.");
        AddCommandCheck(
            checks,
            "command.clang",
            platform.IsWindows ? "clang.exe" : "clang",
            DoctorSeverity.Warning,
            "Install a supported LLVM/Clang toolchain or run the dependency setup explicitly.");
        AddCommandCheck(
            checks,
            "command.llc",
            platform.IsWindows ? "llc.exe" : "llc",
            DoctorSeverity.Warning,
            "Install the LLVM command-line tools and ensure their bin directory is on PATH.");
        AddLlvmDependencyCheck(checks, await _dependencyProbe.ProbeAsync(cancellationToken));

        foreach (var variable in new[] { "EIDOS_HOME", "EIDOS_LLVM_HOME" })
        {
            var value = _environment.GetEnvironmentVariable(variable);
            checks.Add(string.IsNullOrWhiteSpace(value)
                ? new DoctorCheck(
                    $"environment.{variable.ToLowerInvariant()}",
                    DoctorCheckStatus.Warning,
                    DoctorSeverity.Info,
                    $"{variable} is not set.",
                    Remediation: "Run 'eidosup setup' without --skip-env after installing the toolchain.")
                : new DoctorCheck(
                    $"environment.{variable.ToLowerInvariant()}",
                    DoctorCheckStatus.Pass,
                    DoctorSeverity.Info,
                    $"{variable} is set.",
                    value));
        }

        foreach (var variable in new[] { "EIDOSC_HOME", "EIDOS_RUNTIME_PATH" })
        {
            var value = _environment.GetEnvironmentVariable(variable);
            checks.Add(string.IsNullOrWhiteSpace(value)
                ? new DoctorCheck(
                    $"environment.{variable.ToLowerInvariant()}",
                    DoctorCheckStatus.Pass,
                    DoctorSeverity.Info,
                    $"Legacy version-bound variable {variable} is not set.")
                : new DoctorCheck(
                    $"environment.{variable.ToLowerInvariant()}",
                    DoctorCheckStatus.Warning,
                    DoctorSeverity.Warning,
                    $"Legacy version-bound variable {variable} is still set.",
                    value,
                    "Run 'eidosup setup' without --skip-env; the shim now supplies this value only to the selected compiler process."));
        }

        await AddInstallRootChecksAsync(checks, platform, installRootOverride, cancellationToken);
        return new DoctorReport(platform.Rid, checks);
    }

    private static void AddLlvmDependencyCheck(
        ICollection<DoctorCheck> checks,
        DependencyProbeResult probe)
    {
        checks.Add(probe.Health switch
        {
            DependencyHealth.Compatible => new DoctorCheck(
                "dependency.llvm",
                DoctorCheckStatus.Pass,
                DoctorSeverity.Info,
                probe.Detail,
                probe.ClangPath),
            DependencyHealth.Missing => new DoctorCheck(
                "dependency.llvm",
                DoctorCheckStatus.Warning,
                DoctorSeverity.Warning,
                probe.Detail,
                Remediation: "Run 'eidosup setup' to install the supported LLVM dependency explicitly."),
            _ => new DoctorCheck(
                "dependency.llvm",
                DoctorCheckStatus.Fail,
                DoctorSeverity.Error,
                probe.Detail,
                probe.ClangPath,
                $"Install LLVM/Clang major version {probe.Requirement.MinimumMajorVersion}-{probe.Requirement.MaximumMajorVersion} and retry.")
        });
    }

    private void AddCommandCheck(
        ICollection<DoctorCheck> checks,
        string id,
        string command,
        DoctorSeverity missingSeverity,
        string remediation)
    {
        var path = _environment.FindCommand(command);
        checks.Add(path == null
            ? new DoctorCheck(
                id,
                missingSeverity == DoctorSeverity.Error ? DoctorCheckStatus.Fail : DoctorCheckStatus.Warning,
                missingSeverity,
                $"Command '{command}' was not found on PATH.",
                Remediation: remediation)
            : new DoctorCheck(id, DoctorCheckStatus.Pass, DoctorSeverity.Info, $"Command '{command}' is available.", path));
    }

    private async Task AddInstallRootChecksAsync(
        ICollection<DoctorCheck> checks,
        PlatformContext platform,
        string? installRootOverride,
        CancellationToken cancellationToken)
    {
        var configuredRoot = string.IsNullOrWhiteSpace(installRootOverride)
            ? _environment.GetEnvironmentVariable("EIDOS_HOME")
            : installRootOverride;
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            checks.Add(new DoctorCheck(
                "install.root",
                DoctorCheckStatus.Warning,
                DoctorSeverity.Info,
                "No install root is configured.",
                Remediation: "Run 'eidosup setup' or pass --install-root to inspect a specific location."));
            return;
        }

        var installRoot = _environment.GetFullPath(configuredRoot);
        if (!_environment.DirectoryExists(installRoot))
        {
            checks.Add(new DoctorCheck(
                "install.root",
                DoctorCheckStatus.Warning,
                DoctorSeverity.Warning,
                "The configured install root does not exist.",
                installRoot,
                "Run 'eidosup setup' or correct EIDOS_HOME."));
            return;
        }

        checks.Add(new DoctorCheck(
            "install.root",
            DoctorCheckStatus.Pass,
            DoctorSeverity.Info,
            "The install root exists.",
            installRoot));
        var extension = platform.IsWindows ? ".exe" : string.Empty;
        var stableBin = Path.Combine(installRoot, "bin");
        var managerPath = Path.Combine(stableBin, $"eidosup{extension}");
        var shimPath = Path.Combine(stableBin, $"eidosc{extension}");
        var shimManifestPath = Path.Combine(stableBin, ShimInstaller.ManifestFileName);
        var stableCommandsExist = _environment.FileExists(managerPath) &&
                                  _environment.FileExists(shimPath) &&
                                  _environment.FileExists(shimManifestPath);
        checks.Add(stableCommandsExist
            ? new DoctorCheck(
                "shims.installed",
                DoctorCheckStatus.Pass,
                DoctorSeverity.Info,
                "The stable Eidosup manager and eidosc shim are installed.",
                stableBin)
            : new DoctorCheck(
                "shims.installed",
                DoctorCheckStatus.Fail,
                DoctorSeverity.Error,
                "The stable Eidosup manager or eidosc shim is missing.",
                stableBin,
                "Run 'eidosup setup' to reinstall the owned stable commands."));

        var commandPath = _environment.FindCommand(platform.ExecutableName);
        if (commandPath != null &&
            !ToolInstallLayout.PathEquals(_environment.GetFullPath(commandPath), _environment.GetFullPath(shimPath)))
        {
            checks.Add(new DoctorCheck(
                "shims.path",
                DoctorCheckStatus.Fail,
                DoctorSeverity.Error,
                "PATH resolves eidosc outside the managed stable bin directory.",
                commandPath,
                $"Put '{stableBin}' before legacy toolchain directories on PATH."));
        }
        else if (commandPath != null)
        {
            checks.Add(new DoctorCheck(
                "shims.path",
                DoctorCheckStatus.Pass,
                DoctorSeverity.Info,
                "PATH resolves eidosc to the managed stable shim.",
                commandPath));
        }

        var toolchainsDirectory = Path.Combine(installRoot, "toolchains");
        ToolchainState? state = null;
        try
        {
            state = await _stateReader.ReadAsync(platform, installRoot, cancellationToken);
            checks.Add(new DoctorCheck(
                "toolchains.state",
                DoctorCheckStatus.Pass,
                DoctorSeverity.Info,
                $"Toolchain state schema {state.Schema} revision {state.Revision} is readable.",
                Path.Combine(installRoot, "state", ToolchainStateStore.FileName)));
        }
        catch (EidosupException exception) when (exception.Code is EidosupErrorCode.StateCorrupt or EidosupErrorCode.StateUnsupported)
        {
            checks.Add(new DoctorCheck(
                "toolchains.state",
                DoctorCheckStatus.Fail,
                DoctorSeverity.Error,
                exception.Message,
                Path.Combine(installRoot, "state", ToolchainStateStore.FileName),
                exception.Hint));
        }

        var toolchains = state?.Toolchains.OrderBy(static toolchain => toolchain.Id, StringComparer.Ordinal).ToArray() ?? [];
        checks.Add(toolchains.Length == 0
            ? new DoctorCheck(
                "toolchains.installed",
                DoctorCheckStatus.Warning,
                DoctorSeverity.Warning,
                "No immutable managed Eidosc toolchain directories were found.",
                toolchainsDirectory,
                "Run 'eidosup setup' to install a verified release.")
            : new DoctorCheck(
                "toolchains.installed",
                DoctorCheckStatus.Pass,
                DoctorSeverity.Info,
                $"Found {toolchains.Length} immutable managed Eidosc toolchain{(toolchains.Length == 1 ? string.Empty : "s")}.",
                string.Join(", ", toolchains.Select(static toolchain => toolchain.Id))));

        if (state != null)
        {
            AddCustomToolchainCheck(checks, state.CustomToolchains);
            checks.Add(state.Default != null
                ? new DoctorCheck(
                    "toolchains.default",
                    DoctorCheckStatus.Pass,
                    DoctorSeverity.Info,
                    $"Global default selector '{state.Default.Selector}' is active.",
                    state.Default.ToolchainId)
                : state.DefaultConfigured
                    ? new DoctorCheck(
                        "toolchains.default",
                        DoctorCheckStatus.Warning,
                        DoctorSeverity.Info,
                        "The global default toolchain was explicitly cleared.",
                        Remediation: "Run 'eidosup default <selector>' when the stable shim should become active again.")
                    : new DoctorCheck(
                    "toolchains.default",
                    DoctorCheckStatus.Fail,
                    DoctorSeverity.Error,
                    "No global default toolchain is configured.",
                    Remediation: "Run 'eidosup default <selector>' or install the first verified toolchain."));
        }

        if (state is { UnmanagedDirectories.Count: > 0 })
        {
            checks.Add(new DoctorCheck(
                "toolchains.unmanaged",
                DoctorCheckStatus.Warning,
                DoctorSeverity.Warning,
                $"Found {state.UnmanagedDirectories.Count} unmanaged toolchain director{(state.UnmanagedDirectories.Count == 1 ? "y" : "ies")}.",
                string.Join(", ", state.UnmanagedDirectories.Select(static entry => entry.DirectoryName)),
                "Follow the state guidance and remove only directories that are no longer needed."));
        }

        var legacyDirectory = Path.Combine(toolchainsDirectory, "eidosc");
        if (_environment.DirectoryExists(legacyDirectory))
        {
            checks.Add(new DoctorCheck(
                "toolchains.legacy-layout",
                DoctorCheckStatus.Warning,
                DoctorSeverity.Warning,
                "A pre-state-layout Eidosc directory was found and will not be activated.",
                legacyDirectory,
                "Reinstall required toolchains, then remove the legacy directory manually after confirming it is no longer needed."));
        }
    }

    private void AddCustomToolchainCheck(
        ICollection<DoctorCheck> checks,
        IReadOnlyList<CustomToolchainState> customToolchains)
    {
        if (customToolchains.Count == 0)
        {
            checks.Add(new DoctorCheck(
                "toolchains.custom",
                DoctorCheckStatus.Pass,
                DoctorSeverity.Info,
                "No custom toolchains are linked."));
            return;
        }

        var invalid = customToolchains.Where(toolchain =>
                !_environment.DirectoryExists(toolchain.RootDirectory) ||
                !_environment.FileExists(toolchain.CommandPath) ||
                !_environment.DirectoryExists(toolchain.RuntimePath))
            .OrderBy(static toolchain => toolchain.Selector, StringComparer.Ordinal)
            .ToArray();
        if (invalid.Length != 0)
        {
            checks.Add(new DoctorCheck(
                "toolchains.custom",
                DoctorCheckStatus.Fail,
                DoctorSeverity.Error,
                $"{invalid.Length} linked custom toolchain{(invalid.Length == 1 ? " is" : "s are")} no longer usable.",
                string.Join(", ", invalid.Select(static toolchain => $"{toolchain.Selector}={toolchain.RootDirectory}")),
                "Restore each external build directory, or unlink and relink the affected custom toolchain."));
            return;
        }

        checks.Add(new DoctorCheck(
            "toolchains.custom",
            DoctorCheckStatus.Pass,
            DoctorSeverity.Info,
            $"Validated {customToolchains.Count} linked custom toolchain{(customToolchains.Count == 1 ? string.Empty : "s")}.",
            string.Join(", ", customToolchains
                .OrderBy(static toolchain => toolchain.Selector, StringComparer.Ordinal)
                .Select(static toolchain => $"{toolchain.Selector}={toolchain.RootDirectory}"))));
    }

    private static void WriteHumanReadable(DoctorReport report, TextWriter writer)
    {
        writer.WriteLine($"Platform: {report.Platform}");
        foreach (var check in report.Checks)
        {
            writer.WriteLine($"[{check.Status.ToString().ToUpperInvariant()}] {check.Id}: {check.Summary}");
            if (!string.IsNullOrWhiteSpace(check.Detail))
            {
                writer.WriteLine($"  detail: {check.Detail}");
            }

            if (!string.IsNullOrWhiteSpace(check.Remediation))
            {
                writer.WriteLine($"  fix: {check.Remediation}");
            }
        }

        writer.WriteLine(report.Healthy ? "Doctor result: healthy" : "Doctor result: action required");
    }
}
