using Eidosc.Cli.Commands;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Xunit;

namespace Eidosc.Tests.Unit.Cli;

public sealed class BuildCommandNativeCodegenTests
{
    [Fact]
    public void ResolveOptimizationLevel_ReleaseDefaultsToO2()
    {
        Assert.Equal(2, BuildCommand.ResolveOptimizationLevel(BuildMode.Release, requestedOptimizationLevel: null));
    }

    [Fact]
    public void ResolveOptimizationLevel_DevDefaultsToO0()
    {
        Assert.Equal(0, BuildCommand.ResolveOptimizationLevel(BuildMode.Dev, requestedOptimizationLevel: null));
    }

    [Theory]
    [InlineData(BuildMode.Dev, 2)]
    [InlineData(BuildMode.Release, 0)]
    public void ResolveOptimizationLevel_RespectsExplicitOptimizationLevel(BuildMode buildMode, int requested)
    {
        Assert.Equal(requested, BuildCommand.ResolveOptimizationLevel(buildMode, requested));
    }

    [Fact]
    public void NormalizeCodegenMode_NormalizesExplicitModes()
    {
        Assert.Equal(BuildCommand.NativeCodegenModes.FullModule, BuildCommand.NormalizeCodegenMode(null));
        Assert.Equal(BuildCommand.NativeCodegenModes.Auto, BuildCommand.NormalizeCodegenMode("AUTO"));
        Assert.Equal(BuildCommand.NativeCodegenModes.FullModule, BuildCommand.NormalizeCodegenMode("unknown"));
        Assert.True(BuildCommand.IsSupportedNativeCodegenMode("AUTO"));
        Assert.True(BuildCommand.IsSupportedNativeCodegenMode("OBJECT-GROUPS"));
        Assert.False(BuildCommand.IsSupportedNativeCodegenMode("unknown"));
        Assert.False(BuildCommand.IsObjectGroupsCodegenMode(null));
    }

    [Fact]
    public void ResolveNativeCodegenMode_UsesObjectGroupsForDevAuto()
    {
        Assert.Equal(
            BuildCommand.NativeCodegenModes.ObjectGroups,
            BuildCommand.ResolveNativeCodegenMode(BuildMode.Dev, BuildCommand.NativeCodegenModes.Auto));
        Assert.Equal(
            BuildCommand.NativeCodegenModes.FullModule,
            BuildCommand.ResolveNativeCodegenMode(BuildMode.Release, BuildCommand.NativeCodegenModes.Auto));
        Assert.Equal(
            BuildCommand.NativeCodegenModes.FullModule,
            BuildCommand.ResolveNativeCodegenMode(BuildMode.Dev, BuildCommand.NativeCodegenModes.FullModule));
    }

    [Fact]
    public void ResolveMaxObjectGroups_CapsDevAutoOnly()
    {
        Assert.Equal(
            BuildCommand.DevAutoObjectGroupCap,
            BuildCommand.ResolveMaxObjectGroups(BuildMode.Dev, BuildCommand.NativeCodegenModes.Auto, requestedMaxObjectGroups: 0));
        Assert.Equal(
            0,
            BuildCommand.ResolveMaxObjectGroups(BuildMode.Release, BuildCommand.NativeCodegenModes.Auto, requestedMaxObjectGroups: 0));
        Assert.Equal(
            0,
            BuildCommand.ResolveMaxObjectGroups(BuildMode.Dev, BuildCommand.NativeCodegenModes.ObjectGroups, requestedMaxObjectGroups: 0));
        Assert.Equal(
            16,
            BuildCommand.ResolveMaxObjectGroups(BuildMode.Dev, BuildCommand.NativeCodegenModes.Auto, requestedMaxObjectGroups: 16));
    }

    [Fact]
    public void CreateFullBuildArtifactFlags_UsesResolvedDevAutoObjectGroupCap()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_build_codegen_auto_{Guid.NewGuid():N}");
        try
        {
            var (_, resolution, compileOptions) = CreateResolvedProject(tempDir);
            var options = CreateBuildOptions(CompileTarget.Native);
            options.BuildMode = BuildMode.Dev;
            options.CodegenMode = BuildCommand.NativeCodegenModes.Auto;
            options.MaxObjectGroups = 0;

            var flags = BuildCommand.CreateFullBuildArtifactFlags(
                resolution,
                compileOptions,
                options,
                optimizationLevel: 0,
                targetInfo: null,
                outputPath: Path.Combine(tempDir, "main.exe"),
                includeOutputPath: false).ToArray();

            Assert.Contains("codegenMode=object-groups", flags);
            Assert.Contains($"maxObjectGroups={BuildCommand.DevAutoObjectGroupCap}", flags);
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
    public void NormalizeCodegenMode_AcceptsObjectGroupsCaseInsensitively()
    {
        Assert.Equal(BuildCommand.NativeCodegenModes.ObjectGroups, BuildCommand.NormalizeCodegenMode("OBJECT-GROUPS"));
        Assert.True(BuildCommand.IsObjectGroupsCodegenMode("object-groups"));
    }

    private static (string EntryPath, ProjectCommandInputResolution Resolution, CompilationOptions CompileOptions)
        CreateResolvedProject(string tempDir)
    {
        var srcDir = Path.Combine(tempDir, "src");
        Directory.CreateDirectory(srcDir);
        var entryPath = Path.Combine(srcDir, "Main.eidos");
        var projectPath = Path.Combine(tempDir, "eidos.toml");
        File.WriteAllText(projectPath, """
name = "flags-test"
sourceRoots = ["src"]

[[targets]]
name = "main"
entry = "src/Main.eidos"
kind = "executable"
""");
        File.WriteAllText(entryPath, """
Main :: module {
    main :: Unit -> Unit { _ => () }
}
""");

        var resolution = ProjectCommandInputResolver.Resolve(
            projectPath,
            project: null,
            targetName: "main",
            explicitImportRoots: null,
            workingDirectory: tempDir);
        var compileOptions = new CompilationOptions
        {
            InputFile = resolution.SourceFilePath,
            LanguageVersion = resolution.GetLanguageVersion(),
            Target = CompilationTarget.LlvmIr,
            StopAtPhase = CompilationPhase.Llvm,
            EnableMirOptimizations = true
        };
        return (entryPath, resolution, compileOptions);
    }

    private static BuildCommand.BuildOptions CreateBuildOptions(CompileTarget target) =>
        new()
        {
            Source = "src/Main.eidos",
            Target = target,
            BuildMode = BuildMode.Release
        };
}
