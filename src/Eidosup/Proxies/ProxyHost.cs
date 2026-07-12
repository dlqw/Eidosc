using Eidosup.Installation;

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
        var toolchain = await _resolver.ResolveAsync(
            layout,
            invocation.CommandName,
            selector: null,
            cancellationToken);
        return await _processRunner.RunAsync(toolchain, arguments, cancellationToken);
    }
}
