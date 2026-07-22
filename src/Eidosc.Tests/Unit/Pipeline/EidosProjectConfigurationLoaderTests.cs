using Eidosc.ProjectSystem;
using Eidosc.Pipeline;

namespace Eidosc.Tests.Unit.Pipeline;

public class EidosProjectConfigurationLoaderTests
{
    [Fact]
    public void LoadFromPath_MetaExtensionsResolveDeclaredResourcesAndStableFingerprint()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_project_meta_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempDir, "src"));
        Directory.CreateDirectory(Path.Combine(tempDir, "schemas", "nested"));
        var projectFile = Path.Combine(tempDir, "eidos.toml");
        File.WriteAllText(Path.Combine(tempDir, "src", "main.eidos"), "main :: Unit -> Int { _ => 0 }");
        File.WriteAllText(Path.Combine(tempDir, "schemas", "root.json"), "{\"root\":true}");
        File.WriteAllText(Path.Combine(tempDir, "schemas", "nested", "leaf.json"), "{\"leaf\":true}");
        File.WriteAllText(projectFile, """
            [meta]
            checks = ["quality.enforce_api_names"]

            [[meta.extensions]]
            name = "routes"
            entry = "routes.generate"
            stage = "semantic"
            scope = "package"
            inputs = ["schemas/**/*.json", "schemas/missing.json"]
            capabilities = ["read-semantics", "read-declared-resources", "emit-modules"]
            """);

        try
        {
            var first = EidosProjectConfigurationLoader.LoadFromPath(projectFile).Configuration.Meta!;
            var second = EidosProjectConfigurationLoader.LoadFromPath(projectFile).Configuration.Meta!;

            Assert.Equal(["quality.enforce_api_names"], first.Checks);
            var extension = Assert.Single(first.Extensions);
            Assert.Equal("routes.generate", extension.Entry);
            Assert.Equal(
                ["schemas/nested/leaf.json", "schemas/root.json", "schemas/missing.json"],
                extension.Resources.Select(static resource => resource.RelativePath));
            Assert.Equal([true, true, false], extension.Resources.Select(static resource => resource.Exists));
            Assert.All(extension.Resources, static resource => Assert.NotEmpty(resource.ContentHash));
            Assert.Equal(first.Fingerprint, second.Fingerprint);

            File.WriteAllText(Path.Combine(tempDir, "schemas", "root.json"), "{\"root\":false}");
            var changed = EidosProjectConfigurationLoader.LoadFromPath(projectFile).Configuration.Meta!;
            Assert.NotEqual(first.Fingerprint, changed.Fingerprint);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadFromPath_MetaExtensionsRejectUnknownCapabilitiesAndEscapingInputs()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_project_meta_invalid_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var projectFile = Path.Combine(tempDir, "eidos.toml");

        try
        {
            File.WriteAllText(projectFile, """
                [[meta.extensions]]
                name = "routes"
                entry = "routes.generate"
                inputs = ["../secret.json"]
                capabilities = ["ambient-filesystem"]
                """);

            var error = Assert.Throws<InvalidOperationException>(
                () => EidosProjectConfigurationLoader.LoadFromPath(projectFile));
            Assert.Contains("unknown capability", error.Message, StringComparison.Ordinal);

            File.WriteAllText(projectFile, """
                [[meta.extensions]]
                name = "routes"
                entry = "routes.generate"
                inputs = ["../secret.json"]
                capabilities = []
                """);
            error = Assert.Throws<InvalidOperationException>(
                () => EidosProjectConfigurationLoader.LoadFromPath(projectFile));
            Assert.Contains("project root", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadFromPath_MinimalManifest_UsesDefaultSourceRootsAndInfersMainTarget()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_project_config_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempDir, "src"));

        var projectFile = Path.Combine(tempDir, "eidos.toml");
        var mainFile = Path.Combine(tempDir, "src", "main.eidos");
        File.WriteAllText(projectFile, """
            [package]
            name = "dev.eidos.app"
            version = "0.1.0"
            """);
        File.WriteAllText(mainFile, "main :: Unit -> Int { _ => 0 }");

        try
        {
            var loaded = EidosProjectConfigurationLoader.LoadFromPath(projectFile);
            var config = loaded.Configuration;

            Assert.Equal(3, config.ManifestSchema);
            Assert.Equal([Path.GetFullPath(Path.Combine(tempDir, "src"))], config.SourceRoots);
            Assert.Equal("main", config.DefaultTarget);
            var target = Assert.Single(config.Targets);
            Assert.Equal("main", target.Name);
            Assert.Equal("executable", target.Kind);
            Assert.Equal(Path.GetFullPath(mainFile), target.Entry);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadFromPath_MinimalManifest_InfersLibTargetWhenMainMissing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_project_config_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempDir, "src"));

        var projectFile = Path.Combine(tempDir, "eidos.toml");
        var libFile = Path.Combine(tempDir, "src", "lib.eidos");
        File.WriteAllText(projectFile, """
            [package]
            name = "dev.eidos.lib"
            version = "0.1.0"
            """);
        File.WriteAllText(libFile, "lib :: module { id :: Int -> Int { x => x } }");

        try
        {
            var loaded = EidosProjectConfigurationLoader.LoadFromPath(projectFile);
            var config = loaded.Configuration;

            Assert.Equal("lib", config.DefaultTarget);
            var target = Assert.Single(config.Targets);
            Assert.Equal("lib", target.Name);
            Assert.Equal("library", target.Kind);
            Assert.Equal(Path.GetFullPath(libFile), target.Entry);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadFromPath_CargoLikeDependencySyntax_ReadsVersionStringAndInlineTables()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_project_config_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var projectFile = Path.Combine(tempDir, "eidos.toml");
        File.WriteAllText(
            projectFile,
            """
            [dependencies]
            json = "1.2.0"
            shared = { path = "../shared", target = "lib" }
            parser = { git = "https://example.invalid/parser.git", tag = "v1.0.0" }
            """);

        try
        {
            var loaded = EidosProjectConfigurationLoader.LoadFromPath(projectFile);
            var versioned = loaded.Configuration.VersionedDependencies!;
            var projectDependency = Assert.Single(loaded.Configuration.Dependencies);

            Assert.Equal("1.2.0", versioned["json"].Version);
            Assert.Equal("../shared", versioned["shared"].Path);
            Assert.Equal("lib", versioned["shared"].Target);
            Assert.Equal(Path.GetFullPath(Path.Combine(tempDir, "..", "shared", "eidos.toml")), projectDependency.Path);
            Assert.Equal("https://example.invalid/parser.git", versioned["parser"].Git);
            Assert.Equal("v1.0.0", versioned["parser"].Tag);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadFromPath_DuplicateDependencyAliasAcrossShortAndTableSyntax_Fails()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_project_config_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var projectFile = Path.Combine(tempDir, "eidos.toml");
        File.WriteAllText(
            projectFile,
            """
            [dependencies]
            shared = "1.0.0"

            [dependencies.shared]
            path = "../shared"
            """);

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => EidosProjectConfigurationLoader.LoadFromPath(projectFile));

            Assert.Contains("Failed to load project config", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void TryLoadNearest_FindsClosestAncestorProjectFileAndResolvesRelativeSearchRoots()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_project_config_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var rootProjectFile = Path.Combine(tempDir, "eidos.toml");
        var appDir = Path.Combine(tempDir, "app");
        var appProjectFile = Path.Combine(appDir, "eidos.toml");
        var sourceDir = Path.Combine(appDir, "src");
        var inputFile = Path.Combine(sourceDir, "main.eidos");

        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(rootProjectFile, """importRoots = ["shared"]""");
        File.WriteAllText(
            appProjectFile,
            """
            sourceRoots = ["src", "../workspace_src"]
            importRoots = ["local_modules", "../shared"]
            """);
        File.WriteAllText(inputFile, "main :: Unit -> Unit { _ => () }");

        try
        {
            var loaded = EidosProjectConfigurationLoader.TryLoadNearest(inputFile);

            Assert.NotNull(loaded);
            Assert.Equal(Path.GetFullPath(appProjectFile), loaded!.FilePath);
            Assert.Equal(
                [
                    Path.GetFullPath(Path.Combine(appDir, "src")),
                    Path.GetFullPath(Path.Combine(tempDir, "workspace_src"))
                ],
                loaded.Configuration.SourceRoots);
            Assert.Equal(
                [
                    Path.GetFullPath(Path.Combine(appDir, "local_modules")),
                    Path.GetFullPath(Path.Combine(tempDir, "shared"))
                ],
                loaded.Configuration.ImportRoots);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveImportSearchRoots_ExplicitRootsPreserveProjectSourceRoots()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_project_config_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var projectFile = Path.Combine(tempDir, "eidos.toml");
        var sourceDir = Path.Combine(tempDir, "app");
        var inputFile = Path.Combine(sourceDir, "main.eidos");
        var explicitRoot = Path.Combine(tempDir, "manual_modules");
        var sourceRoot = Path.Combine(tempDir, "src");

        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(
            projectFile,
            """
            sourceRoots = ["src"]
            importRoots = ["shared"]
            """);
        File.WriteAllText(inputFile, "main :: Unit -> Unit { _ => () }");

        try
        {
            var resolution = EidosProjectConfigurationLoader.ResolveImportSearchRoots(inputFile, [explicitRoot]);

            Assert.True(resolution.UsesExplicitImportRoots);
            Assert.Equal(Path.GetFullPath(projectFile), resolution.ProjectFilePath);
            Assert.Equal([Path.GetFullPath(sourceRoot)], resolution.SourceSearchRoots);
            Assert.Equal([Path.GetFullPath(explicitRoot)], resolution.ImportSearchRoots);
            Assert.Equal(
                [
                    Path.GetFullPath(sourceRoot),
                    Path.GetFullPath(explicitRoot)
                ],
                resolution.EffectiveSearchRoots);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveImportSearchRoots_ProjectConfigurationProvidesOrderedSearchRootsWhenExplicitRootsMissing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_project_config_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var projectFile = Path.Combine(tempDir, "eidos.toml");
        var sourceDir = Path.Combine(tempDir, "app");
        var inputFile = Path.Combine(sourceDir, "main.eidos");

        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(
            projectFile,
            """
            sourceRoots = ["src", "./app"]
            importRoots = ["shared_modules", "./vendor"]
            """);
        File.WriteAllText(inputFile, "main :: Unit -> Unit { _ => () }");

        try
        {
            var resolution = EidosProjectConfigurationLoader.ResolveImportSearchRoots(inputFile);

            Assert.False(resolution.UsesExplicitImportRoots);
            Assert.Equal(Path.GetFullPath(projectFile), resolution.ProjectFilePath);
            Assert.Equal(
                [
                    Path.GetFullPath(Path.Combine(tempDir, "src")),
                    Path.GetFullPath(Path.Combine(tempDir, "app"))
                ],
                resolution.SourceSearchRoots);
            Assert.Equal(
                [
                    Path.GetFullPath(Path.Combine(tempDir, "shared_modules")),
                    Path.GetFullPath(Path.Combine(tempDir, "vendor"))
                ],
                resolution.ImportSearchRoots);
            Assert.Equal(
                [
                    Path.GetFullPath(Path.Combine(tempDir, "src")),
                    Path.GetFullPath(Path.Combine(tempDir, "app")),
                    Path.GetFullPath(Path.Combine(tempDir, "shared_modules")),
                    Path.GetFullPath(Path.Combine(tempDir, "vendor"))
                ],
                resolution.EffectiveSearchRoots);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveImportSearchRoots_ExplicitRootsBypassBrokenProjectConfiguration()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_project_config_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var projectFile = Path.Combine(tempDir, "eidos.toml");
        var sourceDir = Path.Combine(tempDir, "app");
        var inputFile = Path.Combine(sourceDir, "main.eidos");
        var explicitRoot = Path.Combine(tempDir, "manual_modules");

        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(projectFile, """importRoots = [""");
        File.WriteAllText(inputFile, "main :: Unit -> Unit { _ => () }");

        try
        {
            var resolution = EidosProjectConfigurationLoader.ResolveImportSearchRoots(inputFile, [explicitRoot]);

            Assert.True(resolution.UsesExplicitImportRoots);
            Assert.Null(resolution.ProjectFilePath);
            Assert.Empty(resolution.SourceSearchRoots);
            Assert.Equal([Path.GetFullPath(explicitRoot)], resolution.ImportSearchRoots);
            Assert.Equal([Path.GetFullPath(explicitRoot)], resolution.EffectiveSearchRoots);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadFromPath_FfiConfiguration_NormalizesNativeLinkInputs()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_project_config_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var projectFile = Path.Combine(tempDir, "eidos.toml");
        File.WriteAllText(
            projectFile,
            """
            [ffi]
            libraries = ["raylib"]
            libraryPaths = ["native/lib"]
            includePaths = ["native/include"]
            nativeSources = ["native/shim.c"]
            linkerFlags = ["-lopengl32", "-lgdi32"]
            """);

        try
        {
            var loaded = EidosProjectConfigurationLoader.LoadFromPath(projectFile);
            var ffi = loaded.Configuration.Ffi;

            Assert.NotNull(ffi);
            Assert.Equal(["raylib"], ffi!.Libraries);
            Assert.Equal([Path.GetFullPath(Path.Combine(tempDir, "native", "lib"))], ffi.LibraryPaths);
            Assert.Equal([Path.GetFullPath(Path.Combine(tempDir, "native", "include"))], ffi.IncludePaths);
            Assert.Equal([Path.GetFullPath(Path.Combine(tempDir, "native", "shim.c"))], ffi.NativeSources);
            Assert.Equal(["-lopengl32", "-lgdi32"], ffi.LinkerFlags);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadFromPath_TomlManifest_ReadsPackageTargetsDependenciesAndPlatformFfi()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_project_config_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var projectFile = Path.Combine(tempDir, "eidos.toml");
        File.WriteAllText(
            projectFile,
            """
            manifestSchema = 3
            sourceRoots = ["src"]
            importRoots = ["imports"]
            defaultTarget = "main"

            [package]
            name = "dev.eidos.app"
            version = "1.2.3"
            description = "demo"
            authors = ["A"]
            license = "MIT"

            [[targets]]
            name = "main"
            entry = "src/Main.eidos"
            kind = "executable"
            dependencies = ["core"]
            projectDependencies = ["crypto_a"]

            [dependencies.crypto_a]
            path = "../crypto-a"
            target = "lib"

            [dependencies.crypto_b]
            git = "https://example.invalid/crypto-b.git"
            tag = "v1.0.0"

            [ffi]
            libraries = ["raylib"]
            libraryPaths = ["native/lib"]
            includePaths = ["native/include"]
            nativeSources = ["native/shim.c"]
            linkerFlags = ["-lopengl32"]

            [ffi.platform]
            windows = ["raylib_win"]
            linux = ["raylib_linux"]
            unix = ["raylib_unix"]
            """);

        try
        {
            var loaded = EidosProjectConfigurationLoader.LoadFromPath(projectFile);
            var config = loaded.Configuration;

            Assert.Equal(3, config.ManifestSchema);
            Assert.Equal("dev.eidos.app", config.Package?.Name);
            Assert.Equal("1.2.3", config.Package?.Version.ToString());
            Assert.Equal([Path.GetFullPath(Path.Combine(tempDir, "src"))], config.SourceRoots);
            Assert.Equal([Path.GetFullPath(Path.Combine(tempDir, "imports"))], config.ImportRoots);
            Assert.Equal("main", config.DefaultTarget);

            var target = Assert.Single(config.Targets);
            Assert.Equal("main", target.Name);
            Assert.Equal(Path.GetFullPath(Path.Combine(tempDir, "src", "Main.eidos")), target.Entry);
            Assert.Equal("executable", target.Kind);
            Assert.Equal(["core"], target.Dependencies);
            Assert.Equal(["crypto_a"], target.ProjectDependencies);

            var projectDependency = Assert.Single(config.Dependencies);
            Assert.Equal("crypto_a", projectDependency.Name);
            Assert.Equal(Path.GetFullPath(Path.Combine(tempDir, "..", "crypto-a", "eidos.toml")), projectDependency.Path);
            Assert.Equal("lib", projectDependency.Target);

            Assert.NotNull(config.VersionedDependencies);
            Assert.Equal("../crypto-a", config.VersionedDependencies!["crypto_a"].Path);
            Assert.Equal("https://example.invalid/crypto-b.git", config.VersionedDependencies["crypto_b"].Git);
            Assert.Equal("v1.0.0", config.VersionedDependencies["crypto_b"].Tag);

            var ffi = config.Ffi;
            Assert.NotNull(ffi);
            Assert.Contains("raylib", ffi.Libraries);
            Assert.Equal([Path.GetFullPath(Path.Combine(tempDir, "native", "lib"))], ffi.LibraryPaths);
            Assert.Equal([Path.GetFullPath(Path.Combine(tempDir, "native", "include"))], ffi.IncludePaths);
            Assert.Equal([Path.GetFullPath(Path.Combine(tempDir, "native", "shim.c"))], ffi.NativeSources);
            Assert.Equal(["-lopengl32"], ffi.LinkerFlags);
            Assert.Equal(["raylib_win"], ffi.Platform!["windows"]);

            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                Assert.Contains("raylib_win", ffi.Libraries);
            }
            else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
            {
                Assert.Contains("raylib_linux", ffi.Libraries);
            }
            else
            {
                Assert.Contains("raylib_unix", ffi.Libraries);
            }
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
