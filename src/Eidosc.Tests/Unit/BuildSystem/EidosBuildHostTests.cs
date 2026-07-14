using System.Text.Json;
using Eidosc.BuildSystem;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Tests.Fixtures;

namespace Eidosc.Tests.Unit.BuildSystem;

public sealed class EidosBuildHostTests
{
    [Fact]
    public async Task RunAsync_ExecutesRegisteredToolEmitsGeneratedSourceAndRestoresCache()
    {
        using var workspace = TestTempWorkspace.Create("eidos_build_host_execute");
        var template = workspace.WriteText(
            "tools/Generated.eidos",
            "Generated :: module { answer :: Int = 42; }\n");
        var (toolPath, arguments) = CreateCopyCommand(
            "tools/Generated.eidos",
            "build/generated/Generated.eidos");
        var program = workspace.WriteText(
            "build.eidos",
            CreateGraphProgram(arguments, target: "main"));
        var configuration = CreateConfiguration(workspace, program, template, toolPath);
        var options = CreateOptions(workspace, configuration);

        var first = await EidosBuildHost.RunAsync(options);
        var second = await EidosBuildHost.RunAsync(options);

        Assert.True(first.Success, FormatDiagnostics(first));
        Assert.NotNull(first.Graph);
        Assert.NotNull(first.Execution);
        Assert.False(first.Execution!.CacheHit);
        Assert.Single(first.GeneratedSourceFiles);
        Assert.Equal(
            File.ReadAllText(template),
            File.ReadAllText(workspace.Path("build", "generated", "Generated.eidos")));
        Assert.True(second.Success, FormatDiagnostics(second));
        Assert.True(second.Execution!.CacheHit);
        Assert.Equal(first.Graph!.CanonicalHash, second.Graph!.CanonicalHash);
        Assert.Equal(first.CacheFingerprint, second.CacheFingerprint);
    }

    [Fact]
    public async Task RunAsync_ReadTextAndEnvironmentAreCapabilityTrackedAndFingerprintInputs()
    {
        using var workspace = TestTempWorkspace.Create("eidos_build_host_inputs");
        var input = workspace.WriteText("schema/model.txt", "v1");
        var variableName = $"EIDOS_BUILD_TEST_{Guid.NewGuid():N}";
        var program = workspace.WriteText(
            "build.eidos",
            $$"""
            Context :: comptime Build.context();
            Fs :: comptime Build.fs(Context);
            Env :: comptime Build.env(Context);
            FileText :: comptime Build.readText(Fs, "schema/model.txt");
            EnvText :: comptime Build.environment(Env, "{{variableName}}");
            Emit :: comptime Build.emit(Context);
            BuildGraph :: comptime Build.graph(Emit, [], []);
            """);
        var configuration = new EidosBuildConfiguration
        {
            Program = program,
            FileInputs = [input],
            Environment = [variableName],
            OutputRoots = [workspace.Path("build")]
        };
        var options = CreateOptions(workspace, configuration);

        try
        {
            Environment.SetEnvironmentVariable(variableName, "one");
            var first = await EidosBuildHost.RunAsync(options);
            Environment.SetEnvironmentVariable(variableName, "two");
            var second = await EidosBuildHost.RunAsync(options);

            Assert.True(first.Success, FormatDiagnostics(first));
            Assert.True(second.Success, FormatDiagnostics(second));
            Assert.Contains(first.CapabilityTrace, access =>
                access.Kind == "file" && access.Name == "schema/model.txt");
            Assert.Contains(first.CapabilityTrace, access =>
                access.Kind == "environment" && access.Name == variableName);
            Assert.NotEqual(first.CacheFingerprint, second.CacheFingerprint);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public async Task RunAsync_DirectoryInputsAreExpandedInStableOrderAndFingerprintNestedContent()
    {
        using var workspace = TestTempWorkspace.Create("eidos_build_host_directory_inputs");
        var inputDirectory = workspace.CreateDirectory("schema");
        workspace.WriteText("schema/zeta.txt", "zeta");
        workspace.WriteText("schema/nested/beta.txt", "beta");
        workspace.WriteText("schema/alpha.txt", "alpha");
        var program = workspace.WriteText(
            "build.eidos",
            "Context :: comptime Build.context();\nEmit :: comptime Build.emit(Context);\nBuildGraph :: comptime Build.graph(Emit, [], []);\n");
        var configuration = new EidosBuildConfiguration
        {
            Program = program,
            FileInputs = [inputDirectory],
            OutputRoots = [workspace.Path("build")]
        };
        var options = CreateOptions(workspace, configuration);

        var first = await EidosBuildHost.RunAsync(options);
        workspace.WriteText("schema/nested/beta.txt", "beta-v2");
        var second = await EidosBuildHost.RunAsync(options);

        Assert.True(first.Success, FormatDiagnostics(first));
        Assert.True(second.Success, FormatDiagnostics(second));
        Assert.Equal(
            ["schema/alpha.txt", "schema/nested/beta.txt", "schema/zeta.txt"],
            first.Dependencies
                .Where(static dependency => dependency.Kind == "file")
                .Select(static dependency => dependency.Name));
        Assert.NotEqual(first.CacheFingerprint, second.CacheFingerprint);
    }

    [Fact]
    public async Task RunAsync_UndeclaredAndMissingEnvironmentVariablesAreRejected()
    {
        using var workspace = TestTempWorkspace.Create("eidos_build_host_environment_denial");
        var variableName = $"EIDOS_BUILD_MISSING_{Guid.NewGuid():N}";
        var program = workspace.WriteText(
            "build.eidos",
            $$"""
            Context :: comptime Build.context();
            Env :: comptime Build.env(Context);
            Value :: comptime Build.environment(Env, "{{variableName}}");
            Emit :: comptime Build.emit(Context);
            BuildGraph :: comptime Build.graph(Emit, [], []);
            """);

        Environment.SetEnvironmentVariable(variableName, null);
        var undeclared = await EidosBuildHost.RunAsync(CreateOptions(
            workspace,
            new EidosBuildConfiguration
            {
                Program = program,
                OutputRoots = [workspace.Path("build")]
            }));
        var missing = await EidosBuildHost.RunAsync(CreateOptions(
            workspace,
            new EidosBuildConfiguration
            {
                Program = program,
                Environment = [variableName],
                OutputRoots = [workspace.Path("build")]
            }));

        Assert.False(undeclared.Success);
        Assert.Contains(undeclared.Diagnostics, diagnostic => diagnostic.Message.Contains(
            $"BuildEnv denied undeclared environment variable '{variableName}'",
            StringComparison.Ordinal));
        Assert.False(missing.Success);
        Assert.Contains(missing.Diagnostics, diagnostic => diagnostic.Message.Contains(
            $"Declared build environment variable '{variableName}' is not set",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_UnregisteredToolIsRejectedWhileConstructingTheGraph()
    {
        using var workspace = TestTempWorkspace.Create("eidos_build_host_tool_denial");
        var program = workspace.WriteText(
            "build.eidos",
            """
            Context :: comptime Build.context();
            Process :: comptime Build.process(Context);
            Emit :: comptime Build.emit(Context);
            Generate :: comptime Build.command(Process, "generate", "missing", [], [], ["build/out.txt"], []);
            BuildGraph :: comptime Build.graph(Emit, [Generate], []);
            """);
        var result = await EidosBuildHost.RunAsync(CreateOptions(
            workspace,
            new EidosBuildConfiguration
            {
                Program = program,
                OutputRoots = [workspace.Path("build")]
            }));

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains(
            "BuildProcess denied unregistered tool 'missing'",
            StringComparison.Ordinal));
        Assert.Null(result.Graph);
    }

    [Fact]
    public async Task RunAsync_RejectsDeclaredInputSymlinkThatEscapesTheProjectRoot()
    {
        using var workspace = TestTempWorkspace.Create("eidos_build_host_symlink_escape");
        using var external = TestTempWorkspace.Create("eidos_build_host_symlink_external");
        var externalInput = external.WriteText("secret.txt", "secret");
        var linkedInput = workspace.Path("schema", "linked.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(linkedInput)!);
        if (!TryCreateFileSymbolicLink(linkedInput, externalInput))
        {
            return;
        }

        var program = workspace.WriteText(
            "build.eidos",
            "Context :: comptime Build.context();\nEmit :: comptime Build.emit(Context);\nBuildGraph :: comptime Build.graph(Emit, [], []);\n");
        var result = await EidosBuildHost.RunAsync(CreateOptions(
            workspace,
            new EidosBuildConfiguration
            {
                Program = program,
                FileInputs = [linkedInput],
                OutputRoots = [workspace.Path("build")]
            }));

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "E5032");
    }

    [Fact]
    public async Task RunAsync_RejectsPhysicalInputOutputOverlapThroughDirectorySymlink()
    {
        using var workspace = TestTempWorkspace.Create("eidos_build_host_symlink_overlap");
        var inputDirectory = workspace.CreateDirectory("schema");
        workspace.WriteText("schema/model.txt", "model");
        var linkedOutput = workspace.Path("generated-link");
        if (!TryCreateDirectorySymbolicLink(linkedOutput, inputDirectory))
        {
            return;
        }

        var program = workspace.WriteText(
            "build.eidos",
            "Context :: comptime Build.context();\nEmit :: comptime Build.emit(Context);\nBuildGraph :: comptime Build.graph(Emit, [], []);\n");
        var result = await EidosBuildHost.RunAsync(CreateOptions(
            workspace,
            new EidosBuildConfiguration
            {
                Program = program,
                FileInputs = [inputDirectory],
                OutputRoots = [linkedOutput]
            }));

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "E5031");
    }

    [Fact]
    public void PureComptime_BuildCapabilityAccessIsRejected()
    {
        const string source = "Context :: comptime Build.context();";
        var result = new CompilationPipeline(
            source,
            new CompilationOptions
            {
                InputFile = "pure-comptime-build.eidos",
                AllowVirtualInputFile = true,
                LanguageVersion = EidosLanguageVersions.Current,
                Target = CompilationTarget.Typed,
                StopAtPhase = CompilationPhase.Types,
                NoImplicitPrelude = true
            }).Run();

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains(
                "requires the capability-constrained Build host",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_CycleAndDuplicateOutputsProduceStructuredDiagnosticsWithoutRunningTools()
    {
        using var workspace = TestTempWorkspace.Create("eidos_build_host_invalid_graph");
        var template = workspace.WriteText("tools/input.txt", "input");
        var (toolPath, arguments) = CreateCopyCommand("tools/input.txt", "build/shared.txt");
        var argumentList = FormatEidosList(arguments);
        var program = workspace.WriteText(
            "build.eidos",
            $$"""
            Context :: comptime Build.context();
            Process :: comptime Build.process(Context);
            Emit :: comptime Build.emit(Context);
            A :: comptime Build.command(Process, "a", "copy", {{argumentList}}, ["tools/input.txt"], ["build/shared.txt"], ["b"]);
            B :: comptime Build.command(Process, "b", "copy", {{argumentList}}, ["tools/input.txt"], ["build/shared.txt"], ["a"]);
            BuildGraph :: comptime Build.graph(Emit, [A, B], []);
            """);
        var configuration = CreateConfiguration(workspace, program, template, toolPath);

        var result = await EidosBuildHost.RunAsync(CreateOptions(workspace, configuration));

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "E5008");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "E5010");
        Assert.False(File.Exists(workspace.Path("build", "shared.txt")));
    }

    [Fact]
    public async Task RunAsync_DuplicateOutputsUseHostFilesystemCaseSemantics()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = TestTempWorkspace.Create("eidos_build_host_case_duplicate");
        var template = workspace.WriteText("tools/input.txt", "input");
        var (toolPath, arguments) = CreateCopyCommand("tools/input.txt", "build/shared.txt");
        var argumentList = FormatEidosList(arguments);
        var program = workspace.WriteText(
            "build.eidos",
            $$"""
            Context :: comptime Build.context();
            Process :: comptime Build.process(Context);
            Emit :: comptime Build.emit(Context);
            A :: comptime Build.command(Process, "a", "copy", {{argumentList}}, ["tools/input.txt"], ["build/shared.txt"], []);
            B :: comptime Build.command(Process, "b", "copy", {{argumentList}}, ["tools/input.txt"], ["BUILD/SHARED.TXT"], []);
            BuildGraph :: comptime Build.graph(Emit, [A, B], []);
            """);
        var configuration = CreateConfiguration(workspace, program, template, toolPath);

        var result = await EidosBuildHost.RunAsync(CreateOptions(workspace, configuration));

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "E5008");
        Assert.False(File.Exists(workspace.Path("build", "shared.txt")));
    }

    [Fact]
    public async Task RunAsync_UndeclaredInputAndTargetMisuseAreRejected()
    {
        using var workspace = TestTempWorkspace.Create("eidos_build_host_misuse");
        var template = workspace.WriteText("tools/input.txt", "input");
        var (toolPath, arguments) = CreateCopyCommand("tools/input.txt", "build/generated.eidos");
        var argumentList = FormatEidosList(arguments);
        var program = workspace.WriteText(
            "build.eidos",
            $$"""
            Context :: comptime Build.context();
            Process :: comptime Build.process(Context);
            Emit :: comptime Build.emit(Context);
            Generate :: comptime Build.command(Process, "generate", "copy", {{argumentList}}, ["undeclared.txt"], ["build/generated.eidos"], []);
            Generated :: comptime Build.generatedSource(Emit, "build/generated.eidos", "generate", "other-target");
            BuildGraph :: comptime Build.graph(Emit, [Generate], [Generated]);
            """);
        var configuration = CreateConfiguration(workspace, program, template, toolPath);

        var result = await EidosBuildHost.RunAsync(CreateOptions(workspace, configuration));

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "E5011");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "E5016");
    }

    private static EidosBuildHostOptions CreateOptions(
        TestTempWorkspace workspace,
        EidosBuildConfiguration configuration) => new()
        {
            ProjectDirectory = workspace.Root,
            Configuration = configuration,
            LanguageVersion = EidosLanguageVersions.Current,
            TargetName = "main",
            TraceBuild = true,
            UseCache = true,
            NoImplicitPrelude = true
        };

    private static EidosBuildConfiguration CreateConfiguration(
        TestTempWorkspace workspace,
        string program,
        string input,
        string toolPath) => new()
        {
            Program = program,
            FileInputs = [input],
            OutputRoots = [workspace.Path("build")],
            Tools = [new EidosBuildToolConfiguration { Name = "copy", Path = toolPath }]
        };

    private static string CreateGraphProgram(IReadOnlyList<string> arguments, string target)
    {
        var argumentList = FormatEidosList(arguments);
        return $$"""
            Context :: comptime Build.context();
            Process :: comptime Build.process(Context);
            Emit :: comptime Build.emit(Context);
            Generate :: comptime Build.command(Process, "generate", "copy", {{argumentList}}, ["tools/Generated.eidos"], ["build/generated/Generated.eidos"], []);
            Generated :: comptime Build.generatedSource(Emit, "build/generated/Generated.eidos", "generate", "{{target}}");
            BuildGraph :: comptime Build.graph(Emit, [Generate], [Generated]);
            """;
    }

    private static (string ToolPath, IReadOnlyList<string> Arguments) CreateCopyCommand(
        string input,
        string output)
    {
        if (OperatingSystem.IsWindows())
        {
            return (
                Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                ["/d", "/c", "copy", "/y", input.Replace('/', '\\'), output.Replace('/', '\\')]);
        }

        return ("/bin/cp", [input, output]);
    }

    private static string FormatEidosList(IEnumerable<string> values) =>
        $"[{string.Join(", ", values.Select(static value => JsonSerializer.Serialize(value)))}]";

    private static string FormatDiagnostics(EidosBuildHostResult result) =>
        string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code}: {diagnostic.Message}"));

    private static bool TryCreateFileSymbolicLink(string path, string target)
    {
        try
        {
            File.CreateSymbolicLink(path, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateDirectorySymbolicLink(string path, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(path, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
