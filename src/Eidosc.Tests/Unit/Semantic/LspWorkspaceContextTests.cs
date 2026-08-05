using System.Text.Json;
using Eidosc.Cli.Lsp;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class LspWorkspaceContextTests
{
    [Fact]
    public void Initialize_SingleProjectRoot_BecomesActiveProject()
    {
        var projectRoot = CreateProject();
        try
        {
            var context = new LspWorkspaceContext();
            using var parameters = JsonDocument.Parse($$"""
            {
              "rootUri": {{JsonSerializer.Serialize(new Uri(projectRoot).AbsoluteUri)}},
              "workspaceFolders": [
                { "uri": {{JsonSerializer.Serialize(new Uri(projectRoot).AbsoluteUri)}}, "name": "sample" }
              ]
            }
            """);

            context.Initialize(parameters.RootElement);

            Assert.Equal(
                Path.Combine(projectRoot, "eidos.toml"),
                context.ResolveProjectFilePath(Path.Combine(projectRoot, "src", "main.eidos")));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void ResolveProjectFilePath_NearestProjectOverridesActiveProject()
    {
        var activeProject = CreateProject();
        var documentProject = CreateProject();
        try
        {
            var context = new LspWorkspaceContext();
            using var initialize = JsonDocument.Parse($$"""
            { "rootUri": {{JsonSerializer.Serialize(new Uri(activeProject).AbsoluteUri)}} }
            """);
            context.Initialize(initialize.RootElement);

            var resolved = context.ResolveProjectFilePath(
                Path.Combine(documentProject, "src", "main.eidos"));

            Assert.Equal(Path.Combine(documentProject, "eidos.toml"), resolved);
        }
        finally
        {
            Directory.Delete(activeProject, recursive: true);
            Directory.Delete(documentProject, recursive: true);
        }
    }

    [Fact]
    public void ResolveProjectFilePath_TrustedPhysicalStdlib_DoesNotBorrowActiveProject()
    {
        var projectRoot = CreateProject();
        try
        {
            var context = new LspWorkspaceContext();
            using var initialize = JsonDocument.Parse($$"""
            { "rootUri": {{JsonSerializer.Serialize(new Uri(projectRoot).AbsoluteUri)}} }
            """);
            context.Initialize(initialize.RootElement);
            var eidoscRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            var stdlibPath = Path.Combine(
                eidoscRoot,
                "src",
                "Eidosc",
                "Stdlib",
                "Precompiled",
                "std",
                "console.eidos");

            Assert.Null(context.ResolveProjectFilePath(stdlibPath));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void SetActiveProject_AndWorkspaceFolderRemoval_RecomputeContext()
    {
        var firstProject = CreateProject();
        var secondProject = CreateProject();
        try
        {
            var context = new LspWorkspaceContext();
            using var initialize = JsonDocument.Parse($$"""
            {
              "workspaceFolders": [
                { "uri": {{JsonSerializer.Serialize(new Uri(firstProject).AbsoluteUri)}}, "name": "first" },
                { "uri": {{JsonSerializer.Serialize(new Uri(secondProject).AbsoluteUri)}}, "name": "second" }
              ]
            }
            """);
            context.Initialize(initialize.RootElement);
            using var setProject = JsonDocument.Parse($$"""
            { "projectUri": {{JsonSerializer.Serialize(new Uri(firstProject).AbsoluteUri)}} }
            """);
            Assert.True(context.SetActiveProject(setProject.RootElement));

            using var removeFolder = JsonDocument.Parse($$"""
            {
              "event": {
                "removed": [
                  { "uri": {{JsonSerializer.Serialize(new Uri(firstProject).AbsoluteUri)}}, "name": "first" }
                ],
                "added": []
              }
            }
            """);
            Assert.True(context.UpdateWorkspaceFolders(removeFolder.RootElement));

            Assert.Equal(
                Path.Combine(secondProject, "eidos.toml"),
                context.ResolveProjectFilePath(Path.Combine(Path.GetTempPath(), "outside.eidos")));
        }
        finally
        {
            Directory.Delete(firstProject, recursive: true);
            Directory.Delete(secondProject, recursive: true);
        }
    }

    private static string CreateProject()
    {
        var root = Path.Combine(Path.GetTempPath(), $"eidos-lsp-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(
            Path.Combine(root, "eidos.toml"),
            "manifestSchema = 3\n[language]\nversion = \"0.9.0-alpha.1\"\n");
        return root;
    }
}
