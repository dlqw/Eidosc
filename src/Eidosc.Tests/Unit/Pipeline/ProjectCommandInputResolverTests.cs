using Eidosc.Cli.Commands;
using Eidosc.CodeGen;
using Eidosc.Tests.Fixtures;

namespace Eidosc.Tests.Unit.Pipeline;

public class ProjectCommandInputResolverTests
{
    [Fact]
    public void Resolve_ProjectPathInput_SelectsDefaultTargetEntry()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_cli_project_input");
        var tempDir = workspace.Root;
        var projectDir = Path.Combine(tempDir, "App");
        var entryFile = Path.Combine(projectDir, "src", "App", "main.eidos");
        Directory.CreateDirectory(Path.GetDirectoryName(entryFile)!);
        File.WriteAllText(entryFile, "main :: Unit -> Unit { _ => () }");
        File.WriteAllText(
            Path.Combine(projectDir, "eidos.toml"),
            """
            sourceRoots = ["src"]
            defaultTarget = "app"

            [[targets]]
            name = "app"
            entry = "src/App/main.eidos"
            """);

        var resolved = ProjectCommandInputResolver.Resolve(projectDir, null, null);

        Assert.NotNull(resolved.ProjectTarget);
        Assert.Equal(Path.GetFullPath(entryFile), resolved.SourceFilePath);
        Assert.Equal("app", resolved.ProjectTarget!.TargetName);
        Assert.Equal(Path.GetFullPath(Path.Combine(projectDir, "eidos.toml")), resolved.ImportResolution.ProjectFilePath);
    }

    [Fact]
    public void Resolve_ProjectPathInput_InferDefaultMainTargetFromMinimalManifest()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_cli_project_input");
        var tempDir = workspace.Root;
        var projectDir = Path.Combine(tempDir, "App");
        var entryFile = Path.Combine(projectDir, "src", "main.eidos");
        Directory.CreateDirectory(Path.GetDirectoryName(entryFile)!);
        File.WriteAllText(entryFile, "main :: Unit -> Int { _ => 0 }");
        File.WriteAllText(
            Path.Combine(projectDir, "eidos.toml"),
            """
            [package]
            name = "dev.eidos.app"
            version = "0.1.0"
            """);

        var resolved = ProjectCommandInputResolver.Resolve(projectDir, null, null);

        Assert.NotNull(resolved.ProjectTarget);
        Assert.Equal(Path.GetFullPath(entryFile), resolved.SourceFilePath);
        Assert.Equal("main", resolved.ProjectTarget!.TargetName);
        Assert.Equal("executable", resolved.ProjectTarget.Kind);
    }

    [Fact]
    public void Resolve_ProjectOption_SelectsConfiguredTarget()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_cli_project_input");
        var tempDir = workspace.Root;
        var projectDir = Path.Combine(tempDir, "App");
        var appEntry = Path.Combine(projectDir, "src", "App", "main.eidos");
        var coreEntry = Path.Combine(projectDir, "src", "Core", "mod.eidos");
        Directory.CreateDirectory(Path.GetDirectoryName(appEntry)!);
        Directory.CreateDirectory(Path.GetDirectoryName(coreEntry)!);
        File.WriteAllText(appEntry, "main :: Unit -> Unit { _ => () }");
        File.WriteAllText(coreEntry, "core :: Unit -> Unit { _ => () }");
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

        var resolved = ProjectCommandInputResolver.Resolve("", projectDir, "core");

        Assert.NotNull(resolved.ProjectTarget);
        Assert.Equal("core", resolved.ProjectTarget!.TargetName);
        Assert.Equal(Path.GetFullPath(coreEntry), resolved.SourceFilePath);
    }

    [Fact]
    public void ResolveDocument_ProjectOption_PreservesActiveFileAndKeepsPackageImports()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_cli_project_input");
        var tempDir = workspace.Root;
        var appDir = Path.Combine(tempDir, "App");
        var depDir = Path.Combine(tempDir, "Raylib");
        var activeFile = Path.Combine(appDir, "src", "Scratch.eidos");
        var appEntry = Path.Combine(appDir, "src", "main.eidos");
        var depEntry = Path.Combine(depDir, "src", "Raw.eidos");
        Directory.CreateDirectory(Path.GetDirectoryName(activeFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(depEntry)!);
        File.WriteAllText(activeFile, "import Raylib.Raw");
        File.WriteAllText(appEntry, "main :: Int -> Int { _ => 0 }");
        File.WriteAllText(depEntry, "Raw :: module { export answer :: Int { 42 } }");
        File.WriteAllText(
            Path.Combine(depDir, "eidos.toml"),
            """
            [package]
            name = "dev.eidos.raylib"
            version = "0.1.0"

            [[targets]]
            name = "lib"
            entry = "src/Raw.eidos"
            kind = "library"
            """);
        File.WriteAllText(
            Path.Combine(appDir, "eidos.toml"),
            """
            [package]
            name = "dev.eidos.app"
            version = "0.1.0"

            [dependencies]
            Raylib = { path = "../Raylib", target = "lib" }
            """);

        var resolved = ProjectCommandInputResolver.ResolveDocument(activeFile, appDir, null);

        Assert.NotNull(resolved.ProjectTarget);
        Assert.Equal(Path.GetFullPath(activeFile), resolved.SourceFilePath);
        Assert.Equal(Path.GetFullPath(appEntry), resolved.ProjectTarget!.EntryFilePath);
        Assert.True(resolved.ProjectTarget.PackageImportRoots.ContainsKey("Raylib"));
        Assert.Contains(
            Path.GetFullPath(Path.Combine(depDir, "src")),
            resolved.ProjectTarget.PackageImportRoots["Raylib"]);
    }

    [Fact]
    public void ResolveDocument_ProjectOption_InfersTargetFromActiveEntryFile()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_cli_project_input");
        var tempDir = workspace.Root;
        var projectDir = Path.Combine(tempDir, "App");
        var appEntry = Path.Combine(projectDir, "src", "App.eidos");
        var toolEntry = Path.Combine(projectDir, "src", "Tool.eidos");
        Directory.CreateDirectory(Path.GetDirectoryName(appEntry)!);
        File.WriteAllText(appEntry, "main :: Int -> Int { _ => 0 }");
        File.WriteAllText(toolEntry, "main :: Int -> Int { _ => 0 }");
        File.WriteAllText(
            Path.Combine(projectDir, "eidos.toml"),
            """
            [[targets]]
            name = "app"
            entry = "src/App.eidos"

            [[targets]]
            name = "tool"
            entry = "src/Tool.eidos"
            """);

        var resolved = ProjectCommandInputResolver.ResolveDocument(toolEntry, projectDir, null);

        Assert.NotNull(resolved.ProjectTarget);
        Assert.Equal(Path.GetFullPath(toolEntry), resolved.SourceFilePath);
        Assert.Equal("tool", resolved.ProjectTarget!.TargetName);
    }

    [Fact]
    public void Resolve_EmptyInputFromProjectDirectory_SelectsDefaultTargetEntry()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_cli_project_input");
        var tempDir = workspace.Root;
        var projectDir = Path.Combine(tempDir, "App");
        var entryFile = Path.Combine(projectDir, "src", "Main.eidos");
        Directory.CreateDirectory(Path.GetDirectoryName(entryFile)!);
        File.WriteAllText(entryFile, "main :: Unit -> Unit { _ => () }");
        File.WriteAllText(
            Path.Combine(projectDir, "eidos.toml"),
            """
            defaultTarget = "main"

            [[targets]]
            name = "main"
            entry = "src/Main.eidos"
            """);

        var resolved = ProjectCommandInputResolver.Resolve("", null, null, workingDirectory: projectDir);

        Assert.NotNull(resolved.ProjectTarget);
        Assert.Equal(Path.GetFullPath(entryFile), resolved.SourceFilePath);
        Assert.Equal("main", resolved.ProjectTarget!.TargetName);
    }

    [Fact]
    public void Resolve_EmptyInputFromProjectSubdirectory_FindsNearestProject()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_cli_project_input");
        var tempDir = workspace.Root;
        var projectDir = Path.Combine(tempDir, "App");
        var subDir = Path.Combine(projectDir, "src");
        var entryFile = Path.Combine(subDir, "Main.eidos");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(entryFile, "main :: Unit -> Unit { _ => () }");
        File.WriteAllText(
            Path.Combine(projectDir, "eidos.toml"),
            """
            defaultTarget = "main"

            [[targets]]
            name = "main"
            entry = "src/Main.eidos"
            """);

        var resolved = ProjectCommandInputResolver.Resolve(null, null, "main", workingDirectory: subDir);

        Assert.NotNull(resolved.ProjectTarget);
        Assert.Equal(Path.GetFullPath(entryFile), resolved.SourceFilePath);
        Assert.Equal("main", resolved.ProjectTarget!.TargetName);
    }

    [Fact]
    public void Resolve_TargetNameWithoutProjectMode_ReportsUsageError()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_cli_project_input");
        var tempDir = workspace.Root;
        var sourceFile = Path.Combine(tempDir, "main.eidos");
        File.WriteAllText(sourceFile, "main :: Unit -> Unit { _ => () }");

        var ex = Assert.Throws<InvalidOperationException>(
            () => ProjectCommandInputResolver.Resolve(sourceFile, null, "app"));

        Assert.Contains("--target-name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveDebugOutputPath_SourceUnderSrc_UsesSiblingDebugDirectory()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_cli_project_input");
        var tempDir = workspace.Root;
        var projectDir = Path.Combine(tempDir, "App");
        var sourceFile = Path.Combine(projectDir, "src", "Main.eidos");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        File.WriteAllText(sourceFile, "main :: Unit -> Unit { _ => () }");

        var resolved = ProjectCommandInputResolver.Resolve(sourceFile, null, null);

        var debugOutputPath = ProjectCommandPaths.ResolveDebugOutputPath(null, resolved);

        Assert.Equal(Path.GetFullPath(Path.Combine(projectDir, "debug", "Main")), debugOutputPath);
    }

    [Fact]
    public void ResolveOutputPaths_ProjectTarget_UseSiblingBuildAndDebugDirectories()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_cli_project_input");
        var tempDir = workspace.Root;
        var projectDir = Path.Combine(tempDir, "App");
        var entryFile = Path.Combine(projectDir, "src", "App", "main.eidos");
        Directory.CreateDirectory(Path.GetDirectoryName(entryFile)!);
        File.WriteAllText(entryFile, "main :: Unit -> Unit { _ => () }");
        File.WriteAllText(
            Path.Combine(projectDir, "eidos.toml"),
            """
            defaultTarget = "app"

            [[targets]]
            name = "app"
            entry = "src/App/main.eidos"
            """);

        var resolved = ProjectCommandInputResolver.Resolve(projectDir, null, null);

        var debugOutputPath = ProjectCommandPaths.ResolveDebugOutputPath(null, resolved);
        var nativeOutputPath = ProjectCommandPaths.ResolveNativeOutputPath(null, resolved, TargetInfo.Default);
        var llvmOutputPath = ProjectCommandPaths.ResolveLlvmIrOutputPath(null, resolved);

        Assert.Equal(Path.GetFullPath(Path.Combine(projectDir, "debug", "app")), debugOutputPath);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(projectDir, "build", "app" + TargetInfo.Default.ExecutableExtension)),
            nativeOutputPath);
        Assert.Equal(Path.GetFullPath(Path.Combine(projectDir, "build", "app.ll")), llvmOutputPath);
    }
}
