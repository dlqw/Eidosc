using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Unit.Lexer;

public class ScannerTests
{
    private static readonly TestPathConfig Paths = TestPathConfig.Current;

    [Fact]
    public void CompilationHelper_Create_ReturnsHelper()
    {
        var helper = CompilationHelper.Create("");

        Assert.NotNull(helper);
        Assert.Equal("", helper.GetSource());
    }

    [Fact]
    public void CompilationHelper_Create_WithSource_ReturnsSource()
    {
        var source = "42";
        var helper = CompilationHelper.Create(source);

        Assert.Equal(source, helper.GetSource());
    }

    [Fact]
    public void CompilationHelper_Create_WithComplexSource_ReturnsSource()
    {
        var source = "add :: Int -> Int -> Int { x, y => x + y }";
        var helper = CompilationHelper.Create(source);

        Assert.Equal(source, helper.GetSource());
    }

    [Fact]
    public void TestSourceLoader_GetFullPath_ReturnsValidPath()
    {
        var path = TestSourceLoader.GetFullPath(Paths.FixtureProjectRoot);

        Assert.True(Directory.Exists(path));
    }
}
