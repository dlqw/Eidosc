using Eidosup.Configuration;
using Eidosup.Diagnostics;
using Eidosup.Installation;
using Eidosup.Toolchains;

namespace Eidosup.Proxies;

public sealed class ProxyHost(
    ToolchainResolver? resolver = null,
    IProxyProcessRunner? processRunner = null)
{
    private readonly ToolchainResolver _resolver = resolver ?? new ToolchainResolver();
    private readonly IProxyProcessRunner _processRunner = processRunner ?? new ProxyProcessRunner();

    public async Task<int> RunAsync(
        ProxyInvocation invocation,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var layout = ToolInstallLayout.Create(
            PlatformContext.Detect(),
            invocation.RootDirectory,
            downloadRoot: null);
        string? selector = null;
        IReadOnlyList<string> forwardedArguments = arguments;
        if (arguments.Count > 0 && arguments[0].StartsWith('+'))
        {
            if (arguments[0].Length == 1)
            {
                throw new FormatException("A +toolchain selector cannot be empty.");
            }

            selector = Toolchains.ToolchainSpec.Parse(arguments[0][1..]).Canonical;
            forwardedArguments = arguments.Skip(1).ToArray();
        }

        ResolvedToolchain toolchain;
        try
        {
            toolchain = await _resolver.ResolveAsync(
                layout,
                invocation.CommandName,
                selector,
                cancellationToken);
        }
        catch (EidosupException exception) when (
            exception.Code == EidosupErrorCode.ToolchainUnavailable &&
            exception.Data["selector"] is string missingSelector)
        {
            var spec = ToolchainSpec.Parse(missingSelector);
            var settings = await new EidosupSettingsStore().ReadAsync(layout, cancellationToken);
            var install = settings.AutoInstall == AutoInstallMode.Enable ||
                          settings.AutoInstall == AutoInstallMode.Prompt && ConfirmInstall(missingSelector);
            if (!install || spec.Kind == ToolchainSpecKind.Custom)
            {
                throw;
            }

            await new ToolchainManager().InstallAsync(
                new ToolchainManagementOptions(InstallRoot: layout.RootDirectory, DownloadRoot: layout.DownloadDirectory),
                spec,
                force: false,
                dryRun: false,
                progress: null,
                cancellationToken);
            toolchain = await _resolver.ResolveAsync(
                layout,
                invocation.CommandName,
                selector,
                cancellationToken);
        }
        return await _processRunner.RunAsync(toolchain, forwardedArguments, cancellationToken);
    }

    private static bool ConfirmInstall(string selector)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            return false;
        }

        Console.Error.Write($"Toolchain '{selector}' is missing. Install it now? [y/N] ");
        var answer = Console.ReadLine();
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
