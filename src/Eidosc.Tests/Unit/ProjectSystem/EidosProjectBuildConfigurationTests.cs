using Eidosc.ProjectSystem;
using Eidosc.Tests.Fixtures;

namespace Eidosc.Tests.Unit.ProjectSystem;

public sealed class EidosProjectBuildConfigurationTests
{
    [Fact]
    public void ManifestBuildSection_RoundTripsAndLoaderNormalizesCapabilities()
    {
        using var workspace = TestTempWorkspace.Create("eidos_build_manifest");
        workspace.WriteText("build.eidos", "Context :: comptime Build::context();");
        workspace.WriteText("schema/model.json", "{}");
        workspace.WriteText("tools/generator.exe", "tool");
        var manifest = new EidosProjectManifestDocument
        {
            Build = new EidosProjectBuildManifestDocument
            {
                Program = "build.eidos",
                FileInputs = ["schema/model.json"],
                Environment = ["SDK_ROOT"],
                OutputRoots = ["build/generated"],
                Tools =
                [
                    new EidosProjectBuildToolManifestDocument
                    {
                        Name = "generator",
                        Path = "tools/generator.exe"
                    }
                ]
            }
        };
        var text = manifest.ToToml();
        workspace.WriteText("eidos.toml", text);

        var parsed = EidosProjectManifestDocument.Parse(text);
        var loaded = EidosProjectConfigurationLoader.LoadFromPath(workspace.Root).Configuration.Build;

        Assert.Contains("[build]", text, StringComparison.Ordinal);
        Assert.Contains("[[build.tools]]", text, StringComparison.Ordinal);
        Assert.Equal("generator", Assert.Single(parsed.Build!.Tools!).Name);
        Assert.NotNull(loaded);
        Assert.Equal(workspace.Path("build.eidos"), loaded!.Program);
        Assert.Equal([workspace.Path("schema", "model.json")], loaded.FileInputs);
        Assert.Equal(["SDK_ROOT"], loaded.Environment);
        Assert.Equal([workspace.Path("build", "generated")], loaded.OutputRoots);
        Assert.Equal(workspace.Path("tools", "generator.exe"), Assert.Single(loaded.Tools).Path);
    }

    [Fact]
    public void Loader_RejectsBuildInputThatEscapesProjectRoot()
    {
        using var workspace = TestTempWorkspace.Create("eidos_build_manifest_escape");
        workspace.WriteText("build.eidos", "Context :: comptime Build::context();");
        workspace.WriteText(
            "eidos.toml",
            """
            manifestSchema = 3

            [build]
            program = "build.eidos"
            fileInputs = ["../secret.txt"]
            """);

        var error = Assert.Throws<InvalidOperationException>(
            () => EidosProjectConfigurationLoader.LoadFromPath(workspace.Root));

        Assert.Contains("escapes the project root", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_DefaultsBuildOutputRootToBuildDirectory()
    {
        using var workspace = TestTempWorkspace.Create("eidos_build_manifest_default_output");
        workspace.WriteText("build.eidos", "Context :: comptime Build::context();");
        workspace.WriteText(
            "eidos.toml",
            """
            manifestSchema = 3

            [build]
            program = "build.eidos"
            """);

        var build = EidosProjectConfigurationLoader.LoadFromPath(workspace.Root).Configuration.Build;

        Assert.Equal([workspace.Path("build")], build!.OutputRoots);
    }

    [Fact]
    public void Loader_RejectsOverlappingBuildInputsAndOutputRoots()
    {
        using var workspace = TestTempWorkspace.Create("eidos_build_manifest_overlap");
        workspace.WriteText("build.eidos", "Context :: comptime Build::context();");
        workspace.WriteText("generated/input.txt", "input");
        workspace.WriteText(
            "eidos.toml",
            """
            manifestSchema = 3

            [build]
            program = "build.eidos"
            fileInputs = ["generated/input.txt"]
            outputRoots = ["generated"]
            """);

        var error = Assert.Throws<InvalidOperationException>(
            () => EidosProjectConfigurationLoader.LoadFromPath(workspace.Root));

        Assert.Contains("must be disjoint", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_RejectsEmptyBuildInputEntries()
    {
        using var workspace = TestTempWorkspace.Create("eidos_build_manifest_empty_input");
        workspace.WriteText("build.eidos", "Context :: comptime Build::context();");
        workspace.WriteText(
            "eidos.toml",
            """
            manifestSchema = 3

            [build]
            program = "build.eidos"
            fileInputs = [""]
            """);

        var error = Assert.Throws<InvalidOperationException>(
            () => EidosProjectConfigurationLoader.LoadFromPath(workspace.Root));

        Assert.Contains("cannot be empty", error.Message, StringComparison.Ordinal);
    }
}
