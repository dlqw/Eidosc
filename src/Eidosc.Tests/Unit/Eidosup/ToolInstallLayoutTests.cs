using Eidosup.Installation;

namespace Eidosc.Tests.Unit.Eidosup;

[Collection(EidosupEnvironmentTestCollection.Name)]
public sealed class ToolInstallLayoutTests
{
    [Fact]
    public void Create_UsesEidosHomeWhenInstallRootIsNotExplicit()
    {
        var previous = Environment.GetEnvironmentVariable("EIDOS_HOME");
        var expected = Path.Combine(Path.GetTempPath(), $"eidos-home-{Guid.NewGuid():N}");
        try
        {
            Environment.SetEnvironmentVariable("EIDOS_HOME", expected);

            var layout = ToolInstallLayout.Create(
                PlatformContext.Detect(),
                installRoot: null,
                downloadRoot: null);

            Assert.True(ToolInstallLayout.PathEquals(expected, layout.RootDirectory));
            Assert.True(ToolInstallLayout.PathEquals(Path.Combine(expected, "downloads"), layout.DownloadDirectory));
        }
        finally
        {
            Environment.SetEnvironmentVariable("EIDOS_HOME", previous);
        }
    }

    [Fact]
    public void Create_ExplicitInstallRootOverridesEidosHome()
    {
        var previous = Environment.GetEnvironmentVariable("EIDOS_HOME");
        var configured = Path.Combine(Path.GetTempPath(), $"eidos-home-{Guid.NewGuid():N}");
        var explicitRoot = Path.Combine(Path.GetTempPath(), $"eidos-explicit-{Guid.NewGuid():N}");
        try
        {
            Environment.SetEnvironmentVariable("EIDOS_HOME", configured);

            var layout = ToolInstallLayout.Create(
                PlatformContext.Detect(),
                explicitRoot,
                downloadRoot: null);

            Assert.True(ToolInstallLayout.PathEquals(explicitRoot, layout.RootDirectory));
        }
        finally
        {
            Environment.SetEnvironmentVariable("EIDOS_HOME", previous);
        }
    }
}
