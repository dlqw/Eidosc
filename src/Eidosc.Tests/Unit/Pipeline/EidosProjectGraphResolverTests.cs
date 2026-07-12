using Eidosc.ProjectSystem;
using Eidosc.Pipeline;

namespace Eidosc.Tests.Unit.Pipeline;

public class EidosProjectGraphResolverTests
{
    [Fact]
    public void ResolveTarget_DefaultTargetBuildsEntryAndDependencyGraph()
    {
        var tempDir = CreateTempDirectory();
        var appProjectDir = Path.Combine(tempDir, "App");
        var sharedProjectDir = Path.Combine(tempDir, "Shared");
        Directory.CreateDirectory(appProjectDir);
        Directory.CreateDirectory(sharedProjectDir);

        var appEntry = Path.Combine(appProjectDir, "src", "App", "main.eidos");
        var appCoreEntry = Path.Combine(appProjectDir, "src", "Core", "mod.eidos");
        var sharedEntry = Path.Combine(sharedProjectDir, "src", "Shared", "mod.eidos");
        Directory.CreateDirectory(Path.GetDirectoryName(appEntry)!);
        Directory.CreateDirectory(Path.GetDirectoryName(appCoreEntry)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sharedEntry)!);
        Directory.CreateDirectory(Path.Combine(appProjectDir, "vendor"));
        Directory.CreateDirectory(Path.Combine(sharedProjectDir, "vendor"));

        File.WriteAllText(appEntry, "main :: Unit -> Unit { _ => () }");
        File.WriteAllText(appCoreEntry, "init :: Unit -> Unit { _ => () }");
        File.WriteAllText(sharedEntry, "shared :: Unit -> Unit { _ => () }");

        File.WriteAllText(
            Path.Combine(sharedProjectDir, "eidos.toml"),
            """
            sourceRoots = ["src"]
            importRoots = ["vendor"]
            defaultTarget = "shared"

            [[targets]]
            name = "shared"
            kind = "library"
            entry = "src/Shared/mod.eidos"
            """);

        File.WriteAllText(
            Path.Combine(appProjectDir, "eidos.toml"),
            """
            sourceRoots = ["src"]
            importRoots = ["vendor"]
            defaultTarget = "app"

            [[targets]]
            name = "core"
            kind = "library"
            entry = "src/Core/mod.eidos"

            [[targets]]
            name = "app"
            kind = "executable"
            entry = "src/App/main.eidos"
            dependencies = ["core"]
            projectDependencies = ["shared"]

            [dependencies.shared]
            path = "../Shared"
            """);

        try
        {
            var resolved = EidosProjectGraphResolver.ResolveTarget(appProjectDir);

            Assert.Equal("app", resolved.TargetName);
            Assert.Equal("executable", resolved.Kind);
            Assert.Equal(Path.GetFullPath(appEntry), resolved.EntryFilePath);
            Assert.Equal(["core"], resolved.TargetDependencies);
            Assert.Single(resolved.ProjectDependencies);

            var sharedDependency = resolved.ProjectDependencies[0];
            Assert.Equal("shared", sharedDependency.Name);
            Assert.Equal("shared", sharedDependency.TargetName);
            Assert.Equal(Path.GetFullPath(sharedEntry), sharedDependency.EntryFilePath);

            Assert.Equal(
                [
                    Path.GetFullPath(Path.Combine(appProjectDir, "src")),
                    Path.GetFullPath(Path.Combine(appProjectDir, "vendor"))
                ],
                resolved.ImportResolution.EffectiveSearchRoots);
            Assert.Equal(
                [
                    Path.GetFullPath(Path.Combine(sharedProjectDir, "src")),
                    Path.GetFullPath(Path.Combine(sharedProjectDir, "vendor"))
                ],
                resolved.DependencySearchRoots);
            Assert.Equal(
                [
                    Path.GetFullPath(Path.Combine(appProjectDir, "src")),
                    Path.GetFullPath(Path.Combine(appProjectDir, "vendor")),
                    Path.GetFullPath(Path.Combine(sharedProjectDir, "src")),
                    Path.GetFullPath(Path.Combine(sharedProjectDir, "vendor"))
                ],
                resolved.EffectiveSearchRoots);

            Assert.Equal(3, resolved.BuildGraph.Nodes.Length);
            Assert.Equal(3, resolved.BuildGraph.BuildOrder.Length);
            Assert.Equal("app", resolved.BuildGraph.Nodes[0].TargetName);
            Assert.Equal("core", resolved.BuildGraph.Nodes[1].TargetName);
            Assert.Equal("shared", resolved.BuildGraph.Nodes[2].TargetName);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveTarget_ExplicitImportRootsOverrideOnlyRootProjectImportRoots()
    {
        var tempDir = CreateTempDirectory();
        var appProjectDir = Path.Combine(tempDir, "App");
        var sharedProjectDir = Path.Combine(tempDir, "Shared");
        Directory.CreateDirectory(appProjectDir);
        Directory.CreateDirectory(sharedProjectDir);

        var appEntry = Path.Combine(appProjectDir, "src", "App", "main.eidos");
        var sharedEntry = Path.Combine(sharedProjectDir, "src", "Shared", "mod.eidos");
        var explicitRoot = Path.Combine(tempDir, "manual_modules");
        Directory.CreateDirectory(Path.GetDirectoryName(appEntry)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sharedEntry)!);
        Directory.CreateDirectory(Path.Combine(appProjectDir, "vendor"));
        Directory.CreateDirectory(Path.Combine(sharedProjectDir, "vendor"));
        Directory.CreateDirectory(explicitRoot);

        File.WriteAllText(appEntry, "main :: Unit -> Unit { _ => () }");
        File.WriteAllText(sharedEntry, "shared :: Unit -> Unit { _ => () }");

        File.WriteAllText(
            Path.Combine(sharedProjectDir, "eidos.toml"),
            """
            sourceRoots = ["src"]
            importRoots = ["vendor"]
            defaultTarget = "shared"

            [[targets]]
            name = "shared"
            entry = "src/Shared/mod.eidos"
            """);

        File.WriteAllText(
            Path.Combine(appProjectDir, "eidos.toml"),
            """
            sourceRoots = ["src"]
            importRoots = ["vendor"]
            defaultTarget = "app"

            [[targets]]
            name = "app"
            entry = "src/App/main.eidos"
            projectDependencies = ["shared"]

            [dependencies.shared]
            path = "../Shared"
            """);

        try
        {
            var resolved = EidosProjectGraphResolver.ResolveTarget(appProjectDir, explicitImportRoots: [explicitRoot]);

            Assert.True(resolved.ImportResolution.UsesExplicitImportRoots);
            Assert.Equal([Path.GetFullPath(explicitRoot)], resolved.ImportResolution.ImportSearchRoots);
            Assert.Equal(
                [
                    Path.GetFullPath(Path.Combine(appProjectDir, "src")),
                    Path.GetFullPath(explicitRoot)
                ],
                resolved.ImportResolution.EffectiveSearchRoots);
            Assert.Equal(
                [
                    Path.GetFullPath(Path.Combine(appProjectDir, "src")),
                    Path.GetFullPath(explicitRoot),
                    Path.GetFullPath(Path.Combine(sharedProjectDir, "src")),
                    Path.GetFullPath(Path.Combine(sharedProjectDir, "vendor"))
                ],
                resolved.EffectiveSearchRoots);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveTarget_PathDependencyCarriesBindingPackageFfi()
    {
        var tempDir = CreateTempDirectory();
        var appProjectDir = Path.Combine(tempDir, "App");
        var bindingProjectDir = Path.Combine(tempDir, "Binding");
        Directory.CreateDirectory(Path.Combine(appProjectDir, "src"));
        Directory.CreateDirectory(Path.Combine(bindingProjectDir, "src"));
        Directory.CreateDirectory(Path.Combine(bindingProjectDir, "native"));
        Directory.CreateDirectory(Path.Combine(bindingProjectDir, "include"));

        File.WriteAllText(Path.Combine(appProjectDir, "src", "Main.eidos"), "main :: Unit -> Unit { _ => () }");
        File.WriteAllText(Path.Combine(bindingProjectDir, "src", "Raw.eidos"), """
            Raw :: module {
                @ffi("demo_init")
                demo_init :: Unit -> Unit
            }
            """);
        File.WriteAllText(Path.Combine(bindingProjectDir, "native", "demo.c"), "void demo_init(void) {}");

        File.WriteAllText(
            Path.Combine(bindingProjectDir, "eidos.toml"),
            """
            manifestSchema = 3

            [package]
            name = "dev.eidos.demo"
            version = "0.1.0"

            [[targets]]
            name = "lib"
            entry = "src/Raw.eidos"
            kind = "library"

            [ffi]
            libraries = ["demo"]
            includePaths = ["include"]
            nativeSources = ["native/demo.c"]
            linkerFlags = ["-ldemo"]
            """);

        File.WriteAllText(
            Path.Combine(appProjectDir, "eidos.toml"),
            """
            manifestSchema = 3

            [package]
            name = "dev.eidos.app"
            version = "0.1.0"

            [dependencies]
            Demo = { path = "../Binding", target = "lib" }
            """);

        try
        {
            var resolved = EidosProjectGraphResolver.ResolveTarget(appProjectDir);

            Assert.NotNull(resolved.Ffi);
            Assert.Equal(["demo"], resolved.Ffi!.Libraries);
            Assert.Equal([Path.GetFullPath(Path.Combine(bindingProjectDir, "include"))], resolved.Ffi.IncludePaths);
            Assert.Equal([Path.GetFullPath(Path.Combine(bindingProjectDir, "native", "demo.c"))], resolved.Ffi.NativeSources);
            Assert.Equal(["-ldemo"], resolved.Ffi.LinkerFlags);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveTarget_InternalTargetCycle_ThrowsProjectGraphError()
    {
        var tempDir = CreateTempDirectory();
        var projectDir = Path.Combine(tempDir, "App");
        Directory.CreateDirectory(Path.Combine(projectDir, "src", "App"));
        Directory.CreateDirectory(Path.Combine(projectDir, "src", "Core"));
        File.WriteAllText(Path.Combine(projectDir, "src", "App", "main.eidos"), "main :: Unit -> Unit { _ => () }");
        File.WriteAllText(Path.Combine(projectDir, "src", "Core", "mod.eidos"), "core :: Unit -> Unit { _ => () }");
        File.WriteAllText(
            Path.Combine(projectDir, "eidos.toml"),
            """
            defaultTarget = "app"

            [[targets]]
            name = "app"
            entry = "src/App/main.eidos"
            dependencies = ["core"]

            [[targets]]
            name = "core"
            entry = "src/Core/mod.eidos"
            dependencies = ["app"]
            """);

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => EidosProjectGraphResolver.ResolveTarget(projectDir));

            Assert.Contains("dependency cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("app", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("core", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveTarget_ProjectDependencyCycle_ThrowsProjectGraphError()
    {
        var tempDir = CreateTempDirectory();
        var appProjectDir = Path.Combine(tempDir, "App");
        var sharedProjectDir = Path.Combine(tempDir, "Shared");
        Directory.CreateDirectory(Path.Combine(appProjectDir, "src", "App"));
        Directory.CreateDirectory(Path.Combine(sharedProjectDir, "src", "Shared"));

        File.WriteAllText(Path.Combine(appProjectDir, "src", "App", "main.eidos"), "main :: Unit -> Unit { _ => () }");
        File.WriteAllText(Path.Combine(sharedProjectDir, "src", "Shared", "mod.eidos"), "shared :: Unit -> Unit { _ => () }");

        File.WriteAllText(
            Path.Combine(appProjectDir, "eidos.toml"),
            """
            defaultTarget = "app"

            [[targets]]
            name = "app"
            entry = "src/App/main.eidos"
            projectDependencies = ["shared"]

            [dependencies.shared]
            path = "../Shared"
            """);

        File.WriteAllText(
            Path.Combine(sharedProjectDir, "eidos.toml"),
            """
            defaultTarget = "shared"

            [[targets]]
            name = "shared"
            entry = "src/Shared/mod.eidos"
            projectDependencies = ["app"]

            [dependencies.app]
            path = "../App"
            """);

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => EidosProjectGraphResolver.ResolveTarget(appProjectDir));

            Assert.Contains("dependency cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("App", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Shared", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveTarget_TargetDependsOnMissingTarget_ReportsTargetAndDependency()
    {
        var tempDir = CreateTempDirectory();
        var projectDir = Path.Combine(tempDir, "App");
        Directory.CreateDirectory(Path.Combine(projectDir, "src", "App"));
        File.WriteAllText(Path.Combine(projectDir, "src", "App", "main.eidos"), "main :: Unit -> Unit { _ => () }");
        File.WriteAllText(
            Path.Combine(projectDir, "eidos.toml"),
            """
            defaultTarget = "app"

            [[targets]]
            name = "app"
            entry = "src/App/main.eidos"
            dependencies = ["core"]
            """);

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => EidosProjectGraphResolver.ResolveTarget(projectDir));

            Assert.Contains("missing target", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("app", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("core", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveTarget_TargetDependsOnMissingProjectDependency_ReportsTargetAndDependency()
    {
        var tempDir = CreateTempDirectory();
        var projectDir = Path.Combine(tempDir, "App");
        Directory.CreateDirectory(Path.Combine(projectDir, "src", "App"));
        File.WriteAllText(Path.Combine(projectDir, "src", "App", "main.eidos"), "main :: Unit -> Unit { _ => () }");
        File.WriteAllText(
            Path.Combine(projectDir, "eidos.toml"),
            """
            defaultTarget = "app"

            [[targets]]
            name = "app"
            entry = "src/App/main.eidos"
            projectDependencies = ["shared"]
            """);

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => EidosProjectGraphResolver.ResolveTarget(projectDir));

            Assert.Contains("missing project dependency", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("app", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("shared", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveTarget_VersionedPathDependencyDirectoryMissing_ThrowsProjectGraphError()
    {
        var tempDir = CreateTempDirectory();
        var projectDir = Path.Combine(tempDir, "App");
        Directory.CreateDirectory(projectDir);

        try
        {
            Directory.CreateDirectory(Path.Combine(projectDir, "src"));
            File.WriteAllText(Path.Combine(projectDir, "src", "Main.eidos"), "main :: Unit -> Unit { _ => () }");
            File.WriteAllText(
                Path.Combine(projectDir, "eidos.toml"),
                """
                manifestSchema = 3

                [package]
                name = "dev.eidos.app"
                version = "0.1.0"

                [[targets]]
                name = "app"
                entry = "src/Main.eidos"

                [dependencies]
                Missing = { path = "../MissingPackage" }
                """);

            var ex = Assert.Throws<InvalidOperationException>(() => EidosProjectGraphResolver.ResolveTarget(projectDir));

            Assert.Contains("Missing", ex.Message, StringComparison.Ordinal);
            Assert.Contains("MissingPackage", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveTarget_ProjectWithoutDefaultTargetAndWithMultipleTargets_RequiresExplicitTargetName()
    {
        var tempDir = CreateTempDirectory();
        var projectDir = Path.Combine(tempDir, "App");
        Directory.CreateDirectory(Path.Combine(projectDir, "src", "App"));
        Directory.CreateDirectory(Path.Combine(projectDir, "src", "Core"));
        File.WriteAllText(Path.Combine(projectDir, "src", "App", "main.eidos"), "main :: Unit -> Unit { _ => () }");
        File.WriteAllText(Path.Combine(projectDir, "src", "Core", "mod.eidos"), "core :: Unit -> Unit { _ => () }");
        File.WriteAllText(
            Path.Combine(projectDir, "eidos.toml"),
            """
            [[targets]]
            name = "app"
            entry = "src/App/main.eidos"

            [[targets]]
            name = "core"
            entry = "src/Core/mod.eidos"
            """);

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => EidosProjectGraphResolver.ResolveTarget(projectDir));

            Assert.Contains("multiple targets", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("app", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("core", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_project_graph_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }
}
