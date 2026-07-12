using Eidosup.Installation;

namespace Eidosc.Tests.Unit.Eidosup;

public sealed class EnvironmentConfiguratorTests
{
    [Fact]
    public void MergePathEntries_PrependsNewEntriesAndDeduplicates()
    {
        var merged = EnvironmentConfigurator.MergePathEntries(
            @"C:\LLVM\bin;C:\Windows\System32",
            [@"C:\Tools\Eidos", @"C:\LLVM\bin"],
            ';');

        Assert.Equal(@"C:\Tools\Eidos;C:\LLVM\bin;C:\Windows\System32", merged);
    }
}
