using Eidosup.Diagnostics;

namespace Eidosup.Installation;

public enum DependencyInstallPolicy
{
    DiagnoseOnly,
    InstallMissing
}

public sealed record DependencyInstallOperation(string FileName, string Arguments, bool RequiresElevation);

public sealed record DependencyInstallPlan(
    string DependencyId,
    string ProviderId,
    IReadOnlyList<DependencyInstallOperation> Operations);

public sealed record DependencyResolution(
    DependencyProbeResult Probe,
    DependencyInstallPlan? Plan,
    bool Installed,
    bool PlannedOnly);

public interface IDependencyProvider
{
    DependencyInstallPlan CreateInstallPlan(PlatformContext platform);
}

public sealed class SystemLlvmDependencyProvider : IDependencyProvider
{
    private readonly Func<string, string?> _findCommand;
    private readonly Func<bool> _isElevated;

    public SystemLlvmDependencyProvider(
        Func<string, string?>? findCommand = null,
        Func<bool>? isElevated = null)
    {
        _findCommand = findCommand ?? CommandProbe.TryFind;
        _isElevated = isElevated ?? (() => OperatingSystem.IsWindows() || Environment.UserName == "root");
    }

    public DependencyInstallPlan CreateInstallPlan(PlatformContext platform)
    {
        if (platform.IsWindows)
        {
            return FirstAvailable(
                "winget", "install -e --id LLVM.LLVM --accept-source-agreements --accept-package-agreements",
                "choco", "install llvm -y --no-progress",
                "scoop", "install llvm");
        }

        if (platform.IsMacOs)
        {
            return _findCommand("brew") != null
                ? Plan("homebrew", new DependencyInstallOperation("brew", "install llvm", RequiresElevation: false))
                : throw DependencyErrors.ProviderUnavailable("Homebrew is required to install LLVM/Clang on macOS.");
        }

        if (!platform.IsLinux)
        {
            throw DependencyErrors.ProviderUnavailable($"No LLVM dependency provider supports host '{platform.Rid}'.");
        }

        var manager = FindFirst("apt-get", "dnf", "yum", "pacman", "zypper")
            ?? throw DependencyErrors.ProviderUnavailable("No supported Linux package manager was found.");
        var commands = manager switch
        {
            "apt-get" => new[] { ("apt-get", "update"), ("apt-get", "install -y clang llvm lld") },
            "dnf" => new[] { ("dnf", "install -y clang llvm lld") },
            "yum" => new[] { ("yum", "install -y clang llvm lld") },
            "pacman" => new[] { ("pacman", "-Sy --noconfirm clang llvm lld") },
            _ => new[] { ("zypper", "--non-interactive install clang llvm lld") }
        };
        if (_isElevated())
        {
            return new DependencyInstallPlan(
                "llvm",
                manager,
                commands.Select(item => new DependencyInstallOperation(item.Item1, item.Item2, false)).ToArray());
        }

        if (_findCommand("sudo") == null)
        {
            throw DependencyErrors.ProviderUnavailable(
                $"Package manager '{manager}' requires elevation, but sudo is unavailable.");
        }

        return new DependencyInstallPlan(
            "llvm",
            $"sudo-{manager}",
            commands.Select(item => new DependencyInstallOperation(
                "sudo",
                $"-n {item.Item1} {item.Item2}",
                RequiresElevation: true)).ToArray());
    }

    private DependencyInstallPlan FirstAvailable(params string[] commandAndArguments)
    {
        for (var index = 0; index < commandAndArguments.Length; index += 2)
        {
            var command = commandAndArguments[index];
            if (_findCommand(command) != null)
            {
                return Plan(
                    command,
                    new DependencyInstallOperation(command, commandAndArguments[index + 1], RequiresElevation: false));
            }
        }

        throw DependencyErrors.ProviderUnavailable(
            "No supported Windows package manager was found (winget, Chocolatey, or Scoop).");
    }

    private string? FindFirst(params string[] commands) =>
        commands.FirstOrDefault(command => _findCommand(command) != null);

    private static DependencyInstallPlan Plan(string provider, params DependencyInstallOperation[] operations) =>
        new("llvm", provider, operations);
}

public sealed class LlvmDependencyCoordinator
{
    private readonly ILlvmDependencyProbe _probe;
    private readonly IDependencyProvider _provider;
    private readonly IProcessRunner _runner;

    public LlvmDependencyCoordinator(
        ILlvmDependencyProbe? probe = null,
        IDependencyProvider? provider = null,
        IProcessRunner? runner = null)
    {
        _probe = probe ?? new LlvmDependencyProbe();
        _provider = provider ?? new SystemLlvmDependencyProvider();
        _runner = runner ?? new ProcessRunner();
    }

    public async Task<DependencyResolution> ResolveAsync(
        PlatformContext platform,
        DependencyInstallPolicy policy,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var initial = await _probe.ProbeAsync(cancellationToken);
        if (initial.IsCompatible)
        {
            return new DependencyResolution(initial, null, Installed: false, PlannedOnly: false);
        }

        if (initial.Health == DependencyHealth.Incompatible)
        {
            throw DependencyErrors.Incompatible(initial);
        }

        var plan = _provider.CreateInstallPlan(platform);
        if (policy == DependencyInstallPolicy.DiagnoseOnly)
        {
            return new DependencyResolution(initial, plan, Installed: false, PlannedOnly: true);
        }

        foreach (var operation in plan.Operations)
        {
            try
            {
                await _runner.RunAsync(
                    operation.FileName,
                    operation.Arguments,
                    dryRun,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not EidosupException)
            {
                throw new EidosupException(
                    EidosupErrorCode.DependencyInstallFailure,
                    EidosupExitCodes.DependencyInstallFailure,
                    $"Dependency provider '{plan.ProviderId}' failed while running '{operation.FileName}'.",
                    "Inspect the package-manager output, resolve the reported failure, and retry.",
                    exception);
            }
        }

        if (dryRun)
        {
            return new DependencyResolution(initial, plan, Installed: false, PlannedOnly: true);
        }

        var verified = await _probe.ProbeAsync(cancellationToken);
        if (!verified.IsCompatible)
        {
            throw DependencyErrors.InstallFailed(verified);
        }

        return new DependencyResolution(verified, plan, Installed: true, PlannedOnly: false);
    }
}
