using Eidosup.Diagnostics;
using Eidosup.Proxies;

namespace Eidosc.Tests.Unit.Eidosup;

public sealed class ProxyInvocationTests
{
    [Fact]
    public void TryCreate_RecognizesManagedEidoscShim()
    {
        var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        var root = Path.Combine(Path.GetTempPath(), "eidos-proxy-root");
        var path = Path.Combine(root, "bin", $"eidosc{extension}");

        var recognized = ProxyInvocation.TryCreate(path, out var invocation);

        Assert.True(recognized);
        Assert.Equal("eidosc", invocation?.CommandName);
        Assert.Equal(Path.GetFullPath(root), invocation?.RootDirectory);
    }

    [Fact]
    public void TryCreate_IgnoresEidosupManagerName()
    {
        var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        var path = Path.Combine(Path.GetTempPath(), "eidos", "bin", $"eidosup{extension}");

        Assert.False(ProxyInvocation.TryCreate(path, out var invocation));
        Assert.Null(invocation);
    }

    [Fact]
    public void TryCreate_RejectsEidoscOutsideManagedBin()
    {
        var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        var path = Path.Combine(Path.GetTempPath(), "eidos", "copied", $"eidosc{extension}");

        var exception = Assert.Throws<EidosupException>(() => ProxyInvocation.TryCreate(path, out _));

        Assert.Equal(EidosupErrorCode.ProxyFailure, exception.Code);
    }
}
