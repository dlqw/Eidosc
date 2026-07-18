using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
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
            Session :: comptime build.session();
            Fs :: comptime build.fs(Session);
            Env :: comptime build.env(Session);
            FileText :: comptime build.read_text(Fs, "schema/model.txt");
            EnvText :: comptime build.environment(Env, "{{variableName}}");
            Emit :: comptime build.emit(Session);
            BuildGraph :: comptime build.graph(Emit, [], []);
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
            "Session :: comptime build.session();\nEmit :: comptime build.emit(Session);\nBuildGraph :: comptime build.graph(Emit, [], []);\n");
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
            Session :: comptime build.session();
            Env :: comptime build.env(Session);
            Value :: comptime build.environment(Env, "{{variableName}}");
            Emit :: comptime build.emit(Session);
            BuildGraph :: comptime build.graph(Emit, [], []);
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
            Session :: comptime build.session();
            Process :: comptime build.process(Session);
            Emit :: comptime build.emit(Session);
            Generate :: comptime build.command(Process, "generate", "missing", [], [], ["build/out.txt"], []);
            BuildGraph :: comptime build.graph(Emit, [Generate], []);
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
            "Session :: comptime build.session();\nEmit :: comptime build.emit(Session);\nBuildGraph :: comptime build.graph(Emit, [], []);\n");
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
            "Session :: comptime build.session();\nEmit :: comptime build.emit(Session);\nBuildGraph :: comptime build.graph(Emit, [], []);\n");
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
        const string source = "Session :: comptime build.session();";
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
                "requires the capability-constrained build host",
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
            Session :: comptime build.session();
            Process :: comptime build.process(Session);
            Emit :: comptime build.emit(Session);
            A :: comptime build.command(Process, "a", "copy", {{argumentList}}, ["tools/input.txt"], ["build/shared.txt"], ["b"]);
            B :: comptime build.command(Process, "b", "copy", {{argumentList}}, ["tools/input.txt"], ["build/shared.txt"], ["a"]);
            BuildGraph :: comptime build.graph(Emit, [A, B], []);
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
            Session :: comptime build.session();
            Process :: comptime build.process(Session);
            Emit :: comptime build.emit(Session);
            A :: comptime build.command(Process, "a", "copy", {{argumentList}}, ["tools/input.txt"], ["build/shared.txt"], []);
            B :: comptime build.command(Process, "b", "copy", {{argumentList}}, ["tools/input.txt"], ["BUILD/SHARED.TXT"], []);
            BuildGraph :: comptime build.graph(Emit, [A, B], []);
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
            Session :: comptime build.session();
            Process :: comptime build.process(Session);
            Emit :: comptime build.emit(Session);
            Generate :: comptime build.command(Process, "generate", "copy", {{argumentList}}, ["undeclared.txt"], ["build/generated.eidos"], []);
            Generated :: comptime build.generated_source(Emit, "build/generated.eidos", "generate", "other-target");
            BuildGraph :: comptime build.graph(Emit, [Generate], [Generated]);
            """);
        var configuration = CreateConfiguration(workspace, program, template, toolPath);

        var result = await EidosBuildHost.RunAsync(CreateOptions(workspace, configuration));

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "E5011");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "E5016");
    }

    [Fact]
    public async Task RunAsync_MaterializesTypedGeneratedModuleWithStableOriginAndCacheIdentity()
    {
        using var workspace = TestTempWorkspace.Create("eidos_build_typed_module");
        var program = workspace.WriteText(
            "build.eidos",
            """
            Session :: comptime build.session();
            Emit :: comptime build.emit(Session);
            Generated :: comptime build.generated_module(Emit, "generated.schema", quote items {
                answer :: Int = 42;
            }, "main");
            BuildGraph :: comptime build.graph(Emit, [], [Generated]);
            """);
        var configuration = new EidosBuildConfiguration
        {
            Program = program,
            OutputRoots = [workspace.Path("build")]
        };

        var first = await EidosBuildHost.RunAsync(CreateOptions(workspace, configuration));
        var second = await EidosBuildHost.RunAsync(CreateOptions(workspace, configuration));

        Assert.True(first.Success, FormatDiagnostics(first));
        Assert.True(second.Success, FormatDiagnostics(second));
        Assert.True(second.Execution!.CacheHit);
        var artifact = Assert.Single(first.Graph!.Artifacts);
        Assert.Equal("generated-module", artifact.Kind);
        Assert.StartsWith("eidos-generated://build/", artifact.SourceUri, StringComparison.Ordinal);
        var generatedPath = workspace.Path("build", "generated", "schema.eidos");
        Assert.Equal(generatedPath, Assert.Single(first.GeneratedSourceFiles));
        Assert.Equal(workspace.Path("build"), Assert.Single(first.GeneratedSourceRoots));
        Assert.Contains("generated.schema :: module", await File.ReadAllTextAsync(generatedPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_FetchesOnlyDeclaredNetworkInputAndVerifiesPinnedDigest()
    {
        using var workspace = TestTempWorkspace.Create("eidos_build_fetch");
        var payload = Encoding.UTF8.GetBytes("pinned schema");
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var (url, server, stopServer) = StartSingleResponseServer(payload);
        var program = workspace.WriteText(
            "build.eidos",
            $$"""
            Session :: comptime build.session();
            Network :: comptime build.network(Session);
            Emit :: comptime build.emit(Session);
            Archive :: comptime build.fetch(Network, "{{url}}", build.Sha256.Sha256("{{digest}}"));
            BuildGraph :: comptime build.graph(Emit, [], [Archive]);
            """);
        var configuration = new EidosBuildConfiguration
        {
            Program = program,
            NetworkInputs = [url],
            OutputRoots = [workspace.Path("build")]
        };

        try
        {
            var result = await EidosBuildHost.RunAsync(CreateOptions(workspace, configuration));

            Assert.True(result.Success, FormatDiagnostics(result));
            await server.WaitAsync(TimeSpan.FromSeconds(10));
            var artifact = Assert.Single(result.Graph!.Artifacts);
            Assert.Equal("fetch", artifact.Kind);
            Assert.Equal(digest, artifact.ExpectedSha256);
            Assert.Equal(payload, await File.ReadAllBytesAsync(Path.GetFullPath(artifact.Path, workspace.Root)));
            Assert.Contains(result.Dependencies, dependency => dependency.Kind == "network" && dependency.Name == url);
            var networkAccess = Assert.Single(result.CapabilityTrace, access => access.Kind == "network");
            Assert.Equal(
                networkAccess.Fingerprint,
                Assert.Single(result.Dependencies, dependency => dependency.Kind == "network").Fingerprint);
        }
        finally
        {
            stopServer();
        }
    }

    [Fact]
    public async Task RunAsync_RejectsUndeclaredNetworkInputBeforeGraphExecution()
    {
        using var workspace = TestTempWorkspace.Create("eidos_build_fetch_undeclared");
        const string url = "https://example.invalid/undeclared-schema";
        var digest = new string('a', 64);
        var program = workspace.WriteText(
            "build.eidos",
            $$"""
            Session :: comptime build.session();
            Network :: comptime build.network(Session);
            Emit :: comptime build.emit(Session);
            Archive :: comptime build.fetch(Network, "{{url}}", build.Sha256.Sha256("{{digest}}"));
            BuildGraph :: comptime build.graph(Emit, [], [Archive]);
            """);
        var result = await EidosBuildHost.RunAsync(CreateOptions(
            workspace,
            new EidosBuildConfiguration
            {
                Program = program,
                OutputRoots = [workspace.Path("build")]
            }));

        Assert.False(result.Success);
        Assert.Null(result.Graph);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains(
            $"denied undeclared URL '{url}'",
            StringComparison.Ordinal));
        Assert.False(Directory.Exists(workspace.Path("build", ".fetch")));
    }

    [Fact]
    public async Task RunAsync_RejectsMismatchedFetchDigestWithoutLeavingOutput()
    {
        using var workspace = TestTempWorkspace.Create("eidos_build_fetch_digest_mismatch");
        var payload = Encoding.UTF8.GetBytes("unpinned response");
        var expectedDigest = new string('0', 64);
        var (url, server, stopServer) = StartSingleResponseServer(payload);
        var program = workspace.WriteText(
            "build.eidos",
            $$"""
            Session :: comptime build.session();
            Network :: comptime build.network(Session);
            Emit :: comptime build.emit(Session);
            Archive :: comptime build.fetch(Network, "{{url}}", build.Sha256.Sha256("{{expectedDigest}}"));
            BuildGraph :: comptime build.graph(Emit, [], [Archive]);
            """);
        var configuration = new EidosBuildConfiguration
        {
            Program = program,
            NetworkInputs = [url],
            OutputRoots = [workspace.Path("build")]
        };

        try
        {
            var result = await EidosBuildHost.RunAsync(CreateOptions(workspace, configuration));

            Assert.False(result.Success);
            await server.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains(result.Diagnostics, diagnostic =>
                diagnostic.Code == "E5026" &&
                diagnostic.Message.Contains("produced SHA-256", StringComparison.Ordinal));
            Assert.False(File.Exists(workspace.Path("build", ".fetch", expectedDigest)));
        }
        finally
        {
            stopServer();
        }
    }

    [Fact]
    public async Task RunAsync_ValidatesContentAddressedArtifactAndRestoresItFromCache()
    {
        using var workspace = TestTempWorkspace.Create("eidos_build_content_addressed");
        var template = workspace.WriteText("tools/input.bin", "content-addressed payload");
        var digest = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(template))).ToLowerInvariant();
        var (toolPath, arguments) = CreateCopyCommand("tools/input.bin", "build/artifacts/payload.bin");
        var program = workspace.WriteText(
            "build.eidos",
            $$"""
            Session :: comptime build.session();
            Process :: comptime build.process(Session);
            Emit :: comptime build.emit(Session);
            Produce :: comptime build.command(Process, "produce", "copy", {{FormatEidosList(arguments)}}, ["tools/input.bin"], ["build/artifacts/payload.bin"], []);
            Payload :: comptime build.content_addressed_artifact(Emit, "payload", "build/artifacts/payload.bin", "produce", "main", build.Sha256.Sha256("{{digest}}"));
            BuildGraph :: comptime build.graph(Emit, [Produce], [Payload]);
            """);
        var configuration = CreateConfiguration(workspace, program, template, toolPath);

        var first = await EidosBuildHost.RunAsync(CreateOptions(workspace, configuration));
        var second = await EidosBuildHost.RunAsync(CreateOptions(workspace, configuration));

        Assert.True(first.Success, FormatDiagnostics(first));
        Assert.True(second.Success, FormatDiagnostics(second));
        Assert.True(second.Execution!.CacheHit);
        var artifact = Assert.Single(first.Graph!.Artifacts);
        Assert.Equal("content-addressed", artifact.Kind);
        Assert.Equal(digest, artifact.ExpectedSha256);
        Assert.Contains(first.Execution!.Outputs, output =>
            output.Path == "build/artifacts/payload.bin" && output.Sha256 == digest);
    }

    [Fact]
    public async Task RunAsync_RejectsContentAddressedArtifactWithMismatchedDigest()
    {
        using var workspace = TestTempWorkspace.Create("eidos_build_content_addressed_mismatch");
        var template = workspace.WriteText("tools/input.bin", "actual payload");
        var (toolPath, arguments) = CreateCopyCommand("tools/input.bin", "build/artifacts/payload.bin");
        var expectedDigest = new string('f', 64);
        var program = workspace.WriteText(
            "build.eidos",
            $$"""
            Session :: comptime build.session();
            Process :: comptime build.process(Session);
            Emit :: comptime build.emit(Session);
            Produce :: comptime build.command(Process, "produce", "copy", {{FormatEidosList(arguments)}}, ["tools/input.bin"], ["build/artifacts/payload.bin"], []);
            Payload :: comptime build.content_addressed_artifact(Emit, "payload", "build/artifacts/payload.bin", "produce", "main", build.Sha256.Sha256("{{expectedDigest}}"));
            BuildGraph :: comptime build.graph(Emit, [Produce], [Payload]);
            """);
        var configuration = CreateConfiguration(workspace, program, template, toolPath);

        var result = await EidosBuildHost.RunAsync(CreateOptions(workspace, configuration));

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "E5028");
        Assert.DoesNotContain(result.Dependencies, static dependency => dependency.Kind == "output");
    }

    [Fact]
    public async Task RunAsync_RejectsExecutingATargetToolOnTheHost()
    {
        using var workspace = TestTempWorkspace.Create("eidos_build_target_tool");
        var program = workspace.WriteText(
            "build.eidos",
            """
            Session :: comptime build.session();
            Process :: comptime build.process(Session);
            Emit :: comptime build.emit(Session);
            Generate :: comptime build.command(Process, "generate", "target_tool", [], [], ["build/out"], []);
            BuildGraph :: comptime build.graph(Emit, [Generate], []);
            """);
        var configuration = new EidosBuildConfiguration
        {
            Program = program,
            OutputRoots = [workspace.Path("build")],
            Tools =
            [
                new EidosBuildToolConfiguration
                {
                    Name = "target_tool",
                    Path = Environment.ProcessPath!,
                    Execution = "target"
                }
            ]
        };

        var result = await EidosBuildHost.RunAsync(CreateOptions(workspace, configuration));

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("cannot execute target tool", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_ReleaseProfileRejectsVolatileBuildCapabilities()
    {
        using var workspace = TestTempWorkspace.Create("eidos_build_volatile_release");
        var program = workspace.WriteText(
            "build.eidos",
            "Session :: comptime build.session();\nEmit :: comptime build.emit(Session);\nBuildGraph :: comptime build.graph(Emit, [], []);\n");
        var configuration = new EidosBuildConfiguration
        {
            Program = program,
            OutputRoots = [workspace.Path("build")],
            VolatileCapabilities = ["clock"]
        };

        var result = await EidosBuildHost.RunAsync(CreateOptions(workspace, configuration, releaseProfile: true));

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "E5035");
    }

    [Fact]
    public async Task RunAsync_DevelopmentProfileMarksVolatileGraphAndDisablesCacheReuse()
    {
        using var workspace = TestTempWorkspace.Create("eidos_build_volatile_development");
        var program = workspace.WriteText(
            "build.eidos",
            "Session :: comptime build.session();\nEmit :: comptime build.emit(Session);\nBuildGraph :: comptime build.graph(Emit, [], []);\n");
        var configuration = new EidosBuildConfiguration
        {
            Program = program,
            OutputRoots = [workspace.Path("build")],
            VolatileCapabilities = ["clock"]
        };

        var first = await EidosBuildHost.RunAsync(CreateOptions(workspace, configuration));
        var second = await EidosBuildHost.RunAsync(CreateOptions(workspace, configuration));

        Assert.True(first.Success, FormatDiagnostics(first));
        Assert.True(second.Success, FormatDiagnostics(second));
        Assert.False(first.Graph!.IsReproducible);
        Assert.Equal(["clock"], first.Graph.VolatileCapabilities);
        Assert.False(first.Execution!.CacheHit);
        Assert.False(second.Execution!.CacheHit);
    }

    private static EidosBuildHostOptions CreateOptions(
        TestTempWorkspace workspace,
        EidosBuildConfiguration configuration,
        bool releaseProfile = false) => new()
        {
            ProjectDirectory = workspace.Root,
            Configuration = configuration,
            LanguageVersion = EidosLanguageVersions.Current,
            TargetName = "main",
            TraceBuild = true,
            UseCache = true,
            ReleaseProfile = releaseProfile,
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
            Session :: comptime build.session();
            Process :: comptime build.process(Session);
            Emit :: comptime build.emit(Session);
            Generate :: comptime build.command(Process, "generate", "copy", {{argumentList}}, ["tools/Generated.eidos"], ["build/generated/Generated.eidos"], []);
            Generated :: comptime build.generated_source(Emit, "build/generated/Generated.eidos", "generate", "{{target}}");
            BuildGraph :: comptime build.graph(Emit, [Generate], [Generated]);
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

    private static (string Url, Task Server, Action Stop) StartSingleResponseServer(byte[] payload)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                var requestBuffer = new byte[4096];
                using var request = new MemoryStream();
                while (true)
                {
                    var read = await stream.ReadAsync(requestBuffer);
                    if (read == 0)
                    {
                        break;
                    }
                    request.Write(requestBuffer, 0, read);
                    if (Encoding.ASCII.GetString(request.GetBuffer(), 0, checked((int)request.Length))
                        .Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        break;
                    }
                }

                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header);
                await stream.WriteAsync(payload);
            }
            finally
            {
                listener.Stop();
            }
        });
        return ($"http://127.0.0.1:{port}/schema", server, listener.Stop);
    }

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
