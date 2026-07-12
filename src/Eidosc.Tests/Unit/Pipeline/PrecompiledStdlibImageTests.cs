using Eidosc.Semantic;

namespace Eidosc.Tests.Unit.Pipeline;

public sealed class PrecompiledStdlibImageTests
{
    [Fact]
    public void GetStdlibImageFingerprint_ReturnsStableImageIdentity()
    {
        var first = PrecompiledModuleRegistry.GetStdlibImageFingerprint();
        var second = PrecompiledModuleRegistry.GetStdlibImageFingerprint();

        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.Equal(first, second);
    }
}
