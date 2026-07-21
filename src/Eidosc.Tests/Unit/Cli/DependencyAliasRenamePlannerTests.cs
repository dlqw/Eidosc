using Eidosc.ProjectSystem;

namespace Eidosc.Tests.Unit.Cli;

public sealed class DependencyAliasRenamePlannerTests
{
    [Fact]
    public void CreatePlan_PathDependencyGraph_RenamesEachManifestIdentityAndAstReferences()
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            $"eidos-dependency-alias-rename-{Guid.NewGuid():N}");
        var appDir = Path.Combine(tempDir, "app");
        var libraryDir = Path.Combine(tempDir, "library");
        Directory.CreateDirectory(Path.Combine(appDir, "src"));
        Directory.CreateDirectory(Path.Combine(libraryDir, "src"));

        var appManifest = Path.Combine(appDir, "eidos.toml");
        var libraryManifest = Path.Combine(libraryDir, "eidos.toml");
        var appSource = Path.Combine(appDir, "src", "main.eidos");
        var librarySource = Path.Combine(libraryDir, "src", "lib.eidos");
        File.WriteAllText(appManifest, """
            manifestSchema = 3
            sourceRoots = ["src"]

            [language]
            version = "0.7.0-alpha.1"

            [package]
            name = "dev.eidos.app"
            version = "0.1.0"

            [dependencies]
            RayLib = { path = "../library" }
            """);
        File.WriteAllText(libraryManifest, """
            manifestSchema = 3
            sourceRoots = ["src"]

            [language]
            version = "0.7.0-alpha.1"

            [package]
            name = "dev.eidos.library"
            version = "0.1.0"

            [dependencies]
            JSONApi = "1.0.0"
            """);
        File.WriteAllText(appSource, """
            import RayLib.graphics

            keep :: RayLib.graphics.Color -> RayLib.graphics.Color
            {
                color => RayLib.graphics.keep(color)
            }

            @[derive(RayLib.meta.Derive)] User :: type
            {
                value :: Int,
            }

            // RayLib.graphics in a comment is not a symbol.
            label :: String = "RayLib.graphics";
            """);
        File.WriteAllText(librarySource, """
            import JSONApi.client

            request :: JSONApi.client.Request -> JSONApi.client.Request
            {
                value => JSONApi.client.keep(value)
            }

            local_text :: String = "RayLib.graphics";
            """);

        try
        {
            var plan = DependencyAliasRenamePlanner.CreatePlan(appDir);

            Assert.True(plan.CanApply, string.Join(Environment.NewLine, plan.Diagnostics));
            Assert.Equal("ready", plan.Status);
            Assert.Equal(2, plan.Packages.Length);
            Assert.Contains(
                plan.Packages,
                package => package.AliasRenames.TryGetValue("RayLib", out var renamed) && renamed == "ray_lib");
            Assert.Contains(
                plan.Packages,
                package => package.AliasRenames.TryGetValue("JSONApi", out var renamed) && renamed == "json_api");

            DependencyAliasRenamePlanner.ApplyPlan(plan);

            Assert.Contains("ray_lib =", File.ReadAllText(appManifest), StringComparison.Ordinal);
            Assert.Contains("json_api =", File.ReadAllText(libraryManifest), StringComparison.Ordinal);
            var rewrittenApp = File.ReadAllText(appSource);
            Assert.Contains("import ray_lib.graphics", rewrittenApp, StringComparison.Ordinal);
            Assert.Contains("ray_lib.graphics.Color", rewrittenApp, StringComparison.Ordinal);
            Assert.Contains("ray_lib.graphics.keep", rewrittenApp, StringComparison.Ordinal);
            Assert.Contains("@[derive(ray_lib.meta.Derive)]", rewrittenApp, StringComparison.Ordinal);
            Assert.Contains("// RayLib.graphics in a comment", rewrittenApp, StringComparison.Ordinal);
            Assert.Contains("\"RayLib.graphics\"", rewrittenApp, StringComparison.Ordinal);

            var rewrittenLibrary = File.ReadAllText(librarySource);
            Assert.Contains("import json_api.client", rewrittenLibrary, StringComparison.Ordinal);
            Assert.Contains("json_api.client.Request", rewrittenLibrary, StringComparison.Ordinal);
            Assert.Contains("json_api.client.keep", rewrittenLibrary, StringComparison.Ordinal);
            Assert.Contains("\"RayLib.graphics\"", rewrittenLibrary, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void CreatePlan_ExplicitAliasRename_UsesRequestedCanonicalSpelling()
    {
        var tempDir = CreateProject(
            "RayLib = \"1.0.0\"",
            "import RayLib.graphics\nvalue :: RayLib.graphics.Color = RayLib.graphics.default_color;\n");

        try
        {
            var plan = DependencyAliasRenamePlanner.CreatePlan(
                tempDir,
                "RayLib",
                "raylib",
                includePathDependencies: false);

            Assert.True(plan.CanApply, string.Join(Environment.NewLine, plan.Diagnostics));
            DependencyAliasRenamePlanner.ApplyPlan(plan);

            Assert.Contains("raylib =", File.ReadAllText(Path.Combine(tempDir, "eidos.toml")), StringComparison.Ordinal);
            var source = File.ReadAllText(Path.Combine(tempDir, "src", "main.eidos"));
            Assert.Contains("import raylib.graphics", source, StringComparison.Ordinal);
            Assert.Contains("raylib.graphics.Color", source, StringComparison.Ordinal);
            Assert.Contains("raylib.graphics.default_color", source, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CreatePlan_NormalizedAliasCollision_BlocksWithoutWriting()
    {
        var tempDir = CreateProject(
            "RayLib = \"1.0.0\"\nray_lib = \"2.0.0\"",
            "import RayLib.graphics\n");
        var manifestPath = Path.Combine(tempDir, "eidos.toml");
        var sourcePath = Path.Combine(tempDir, "src", "main.eidos");
        var originalManifest = File.ReadAllText(manifestPath);
        var originalSource = File.ReadAllText(sourcePath);

        try
        {
            var plan = DependencyAliasRenamePlanner.CreatePlan(tempDir);

            Assert.False(plan.CanApply);
            Assert.Equal("blocked", plan.Status);
            Assert.Contains(
                plan.Diagnostics,
                diagnostic => diagnostic.Contains("conflicts with an existing alias", StringComparison.Ordinal));
            Assert.Throws<InvalidOperationException>(() => DependencyAliasRenamePlanner.ApplyPlan(plan));
            Assert.Equal(originalManifest, File.ReadAllText(manifestPath));
            Assert.Equal(originalSource, File.ReadAllText(sourcePath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ApplyPlan_ChangedSource_RejectsWholePlanBeforeWritingManifest()
    {
        var tempDir = CreateProject(
            "RayLib = \"1.0.0\"",
            "import RayLib.graphics\n");
        var manifestPath = Path.Combine(tempDir, "eidos.toml");
        var sourcePath = Path.Combine(tempDir, "src", "main.eidos");
        var originalManifest = File.ReadAllText(manifestPath);

        try
        {
            var plan = DependencyAliasRenamePlanner.CreatePlan(tempDir);
            File.AppendAllText(sourcePath, "# changed\n");

            Assert.Throws<InvalidOperationException>(() => DependencyAliasRenamePlanner.ApplyPlan(plan));
            Assert.Equal(originalManifest, File.ReadAllText(manifestPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CreatePlan_QuotedDependencyTableKey_RenamesOnlyTheKeySegment()
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            $"eidos-dependency-alias-table-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempDir, "src"));
        File.WriteAllText(Path.Combine(tempDir, "eidos.toml"), """
            manifestSchema = 3

            [language]
            version = "0.7.0-alpha.1"

            [package]
            name = "dev.eidos.table"
            version = "0.1.0"

            [dependencies."RayLib"]
            version = "1.0.0"
            """);
        File.WriteAllText(
            Path.Combine(tempDir, "src", "main.eidos"),
            "import RayLib.graphics\n");

        try
        {
            var plan = DependencyAliasRenamePlanner.CreatePlan(tempDir);

            Assert.True(plan.CanApply, string.Join(Environment.NewLine, plan.Diagnostics));
            DependencyAliasRenamePlanner.ApplyPlan(plan);
            var manifest = File.ReadAllText(Path.Combine(tempDir, "eidos.toml"));
            Assert.Contains("[dependencies.\"ray_lib\"]", manifest, StringComparison.Ordinal);
            Assert.Contains(
                "import ray_lib.graphics",
                File.ReadAllText(Path.Combine(tempDir, "src", "main.eidos")),
                StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void CreatePlan_LiteralQuotedDependencyTableKey_RenamesOnlyTheKeySegment()
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            $"eidos-dependency-alias-literal-table-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempDir, "src"));
        File.WriteAllText(Path.Combine(tempDir, "eidos.toml"), """
            manifestSchema = 3

            [language]
            version = "0.7.0-alpha.1"

            [package]
            name = "dev.eidos.literal"
            version = "0.1.0"

            [dependencies.'RayLib']
            version = "1.0.0"
            """);
        File.WriteAllText(
            Path.Combine(tempDir, "src", "main.eidos"),
            "import RayLib.graphics\n");

        try
        {
            var plan = DependencyAliasRenamePlanner.CreatePlan(tempDir);

            Assert.True(plan.CanApply, string.Join(Environment.NewLine, plan.Diagnostics));
            DependencyAliasRenamePlanner.ApplyPlan(plan);
            var manifest = File.ReadAllText(Path.Combine(tempDir, "eidos.toml"));
            Assert.Contains("[dependencies.'ray_lib']", manifest, StringComparison.Ordinal);
            Assert.Contains(
                "import ray_lib.graphics",
                File.ReadAllText(Path.Combine(tempDir, "src", "main.eidos")),
                StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static string CreateProject(string dependencies, string source)
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            $"eidos-dependency-alias-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempDir, "src"));
        File.WriteAllText(Path.Combine(tempDir, "eidos.toml"), $$"""
            manifestSchema = 3
            sourceRoots = ["src"]

            [language]
            version = "0.7.0-alpha.1"

            [package]
            name = "dev.eidos.test"
            version = "0.1.0"

            [dependencies]
            {{dependencies}}
            """);
        File.WriteAllText(Path.Combine(tempDir, "src", "main.eidos"), source);
        return tempDir;
    }
}
