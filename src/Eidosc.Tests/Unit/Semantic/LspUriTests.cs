using Eidosc.Cli.Lsp;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class LspUriTests
{
    [Fact]
    public void UriToFilePath_ConvertsWindowsDriveFileUri()
    {
        var path = LspServer.UriToFilePath("file:///C:/eidos/tests/simple.eidos");

        Assert.Equal(
            Path.GetFullPath(@"C:\eidos\tests\simple.eidos"),
            Path.GetFullPath(path));
    }

    [Fact]
    public void UriToFilePath_UnescapesFileUri()
    {
        var path = LspServer.UriToFilePath("file:///C:/eidos%20workspace/basic.eidos");

        Assert.Contains("eidos workspace", path);
    }

    [Fact]
    public void UriToFilePath_ConvertsEncodedWindowsDriveSeparator()
    {
        var path = LspServer.UriToFilePath("file:///D%3A/Project/eidos_workspace/projects/snake/src/main.eidos");

        Assert.Equal(
            Path.GetFullPath(@"D:\Project\eidos_workspace\projects\snake\src\main.eidos"),
            Path.GetFullPath(path));
    }
}
