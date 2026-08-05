using System.Text;
using System.Text.Json;
using Eidosc.Cli.Lsp;
using Eidosc.Ide;
using Eidosc.BuildSystem;
using Eidosc.ProjectSystem;
using Eidosc.Tests.Fixtures;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class LspServerRunLoopTests
{
    [Fact]
    public async Task RunAsync_OpeningPhysicalPreludeModule_DoesNotPublishDuplicateInstances()
    {
        var eidoscRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var inputPath = Path.Combine(
            eidoscRoot,
            "src",
            "Eidosc",
            "Stdlib",
            "Precompiled",
            "std",
            "trait_invoke.eidos");
        var source = File.ReadAllText(inputPath);
        var uri = new Uri(inputPath).AbsoluteUri;
        using var prefix = new MemoryStream();
        using var suffix = new MemoryStream();
        using var didOpen = CreateDidOpen(uri, 1, source);
        using var hover = CreateHover(uri, 2);
        using var shutdown = JsonDocument.Parse("""{"jsonrpc":"2.0","id":3,"method":"shutdown","params":null}""");
        using var exit = JsonDocument.Parse("""{"jsonrpc":"2.0","method":"exit","params":null}""");
        await JsonRpc.WriteMessageAsync(prefix, didOpen.RootElement);
        await JsonRpc.WriteMessageAsync(prefix, hover.RootElement);
        await JsonRpc.WriteMessageAsync(suffix, shutdown.RootElement);
        await JsonRpc.WriteMessageAsync(suffix, exit.RootElement);
        var prefixBytes = prefix.ToArray();
        var published = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var input = new PublicationGatedInputStream(
            prefixBytes.Concat(suffix.ToArray()).ToArray(),
            prefixBytes.Length,
            published.Task);
        using var output = new PublicationSignalingStream(published);

        using var server = new LspServer(input, output, [], diagnosticDebounce: TimeSpan.Zero);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await server.RunAsync(timeout.Token);

        output.Position = 0;
        JsonElement? publication = null;
        while (await JsonRpc.ReadMessageAsync(output, timeout.Token) is { } message)
        {
            if (message.TryGetProperty("method", out var method) &&
                method.GetString() == "textDocument/publishDiagnostics")
            {
                publication = message.Clone();
            }
        }

        Assert.True(publication.HasValue);
        var diagnostics = publication.Value.GetProperty("params").GetProperty("diagnostics").EnumerateArray().ToArray();
        Assert.DoesNotContain(diagnostics, diagnostic =>
            diagnostic.TryGetProperty("severity", out var severity) && severity.GetInt32() == 1);
        Assert.DoesNotContain(diagnostics, diagnostic =>
            diagnostic.GetProperty("message").GetString()?.Contains("Duplicate instance declaration", StringComparison.Ordinal) == true ||
            diagnostic.GetProperty("message").GetString()?.Contains("reserved for toolchain-owned source", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task RunAsync_DiagnosticsAndHoverSameVersion_ShareSnapshotCompile()
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        var uri = new Uri(Path.GetFullPath("shared_snapshot.eidos")).AbsoluteUri;
        var didOpen = JsonDocument.Parse($$"""
        {
          "jsonrpc": "2.0",
          "method": "textDocument/didOpen",
          "params": {
            "textDocument": {
              "uri": {{JsonSerializer.Serialize(uri)}},
              "languageId": "eidos",
              "version": 1,
              "text": "value :: 1;"
            }
          }
        }
        """);
        var hover = JsonDocument.Parse($$"""
        {
          "jsonrpc": "2.0",
          "id": 2,
          "method": "textDocument/hover",
          "params": {
            "textDocument": { "uri": {{JsonSerializer.Serialize(uri)}} },
            "position": { "line": 0, "character": 0 }
          }
        }
        """);
        var shutdown = JsonDocument.Parse("""{"jsonrpc":"2.0","id":3,"method":"shutdown","params":null}""");
        var exit = JsonDocument.Parse("""{"jsonrpc":"2.0","method":"exit","params":null}""");

        await JsonRpc.WriteMessageAsync(input, didOpen.RootElement);
        await JsonRpc.WriteMessageAsync(input, hover.RootElement);
        await JsonRpc.WriteMessageAsync(input, shutdown.RootElement);
        await JsonRpc.WriteMessageAsync(input, exit.RootElement);
        input.Position = 0;

        var compileCount = 0;
        using var server = new LspServer(
            input,
            output,
            [],
            compileDocumentOverride: (_, _) =>
            {
                Interlocked.Increment(ref compileCount);
                return new IdeSemanticSnapshot
                {
                    Success = true,
                    InputFile = "shared_snapshot.eidos",
                    CompletedPhase = "types"
                };
            },
            diagnosticDebounce: TimeSpan.Zero);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await server.RunAsync(timeout.Token);

        Assert.Equal(1, compileCount);
    }

    [Fact]
    public async Task RunAsync_RepeatedHoverSameVersion_ReusesSnapshot()
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        var uri = new Uri(Path.GetFullPath("warm_hover.eidos")).AbsoluteUri;
        var didOpen = JsonDocument.Parse($$"""
        {
          "jsonrpc": "2.0",
          "method": "textDocument/didOpen",
          "params": {
            "textDocument": {
              "uri": {{JsonSerializer.Serialize(uri)}},
              "languageId": "eidos",
              "version": 1,
              "text": "value :: 1;"
            }
          }
        }
        """);
        var hover = JsonDocument.Parse($$"""
        {
          "jsonrpc": "2.0",
          "id": 2,
          "method": "textDocument/hover",
          "params": {
            "textDocument": { "uri": {{JsonSerializer.Serialize(uri)}} },
            "position": { "line": 0, "character": 0 }
          }
        }
        """);
        var shutdown = JsonDocument.Parse("""{"jsonrpc":"2.0","id":3,"method":"shutdown","params":null}""");
        var exit = JsonDocument.Parse("""{"jsonrpc":"2.0","method":"exit","params":null}""");

        await JsonRpc.WriteMessageAsync(input, didOpen.RootElement);
        await JsonRpc.WriteMessageAsync(input, hover.RootElement);
        await JsonRpc.WriteMessageAsync(input, hover.RootElement);
        await JsonRpc.WriteMessageAsync(input, shutdown.RootElement);
        await JsonRpc.WriteMessageAsync(input, exit.RootElement);
        input.Position = 0;

        var compileCount = 0;
        using var server = new LspServer(
            input,
            output,
            [],
            compileDocumentOverride: (_, _) =>
            {
                Interlocked.Increment(ref compileCount);
                return new IdeSemanticSnapshot
                {
                    Success = true,
                    InputFile = "warm_hover.eidos",
                    CompletedPhase = "types"
                };
            },
            diagnosticDebounce: TimeSpan.FromMinutes(5));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await server.RunAsync(timeout.Token);

        Assert.Equal(1, compileCount);
    }

    [Fact]
    public async Task RunAsync_ProjectContextChange_InvalidatesExistingSnapshots()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"eidos-lsp-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectRoot);
        File.WriteAllText(
            Path.Combine(projectRoot, "eidos.toml"),
            "manifestSchema = 3\n[language]\nversion = \"0.9.0-alpha.1\"\n");

        try
        {
            using var input = new MemoryStream();
            using var output = new MemoryStream();
            var uri = new Uri(Path.GetFullPath("project_context_snapshot.eidos")).AbsoluteUri;
            using var didOpen = CreateDidOpen(uri, 1, "value :: 1;");
            using var hover = CreateHover(uri, 2);
            using var setProject = JsonDocument.Parse($$"""
            {
              "jsonrpc": "2.0",
              "method": "eidos/setProjectContext",
              "params": { "projectUri": {{JsonSerializer.Serialize(new Uri(projectRoot).AbsoluteUri)}} }
            }
            """);
            using var shutdown = JsonDocument.Parse("""{"jsonrpc":"2.0","id":3,"method":"shutdown","params":null}""");
            using var exit = JsonDocument.Parse("""{"jsonrpc":"2.0","method":"exit","params":null}""");

            await JsonRpc.WriteMessageAsync(input, didOpen.RootElement);
            await JsonRpc.WriteMessageAsync(input, hover.RootElement);
            await JsonRpc.WriteMessageAsync(input, setProject.RootElement);
            await JsonRpc.WriteMessageAsync(input, hover.RootElement);
            await JsonRpc.WriteMessageAsync(input, shutdown.RootElement);
            await JsonRpc.WriteMessageAsync(input, exit.RootElement);
            input.Position = 0;

            var compileCount = 0;
            using var server = new LspServer(
                input,
                output,
                [],
                compileDocumentOverride: (path, _) =>
                {
                    Interlocked.Increment(ref compileCount);
                    return new IdeSemanticSnapshot
                    {
                        Success = true,
                        InputFile = path,
                        CompletedPhase = "types"
                    };
                },
                diagnosticDebounce: TimeSpan.FromMinutes(5));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await server.RunAsync(timeout.Token);

            Assert.Equal(2, compileCount);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_UnrelatedOpenDocumentChange_DoesNotInvalidateCurrentSnapshot()
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        var currentUri = new Uri(Path.GetFullPath("current_snapshot.eidos")).AbsoluteUri;
        var unrelatedUri = new Uri(Path.GetFullPath("unrelated_snapshot.eidos")).AbsoluteUri;
        using var openCurrent = CreateDidOpen(currentUri, 1, "value :: 1;");
        using var hoverCurrent = CreateHover(currentUri, 2);
        using var openUnrelated = CreateDidOpen(unrelatedUri, 1, "other :: 1;");
        using var changeUnrelated = CreateDidChange(unrelatedUri, 2, "other :: 2;");
        using var shutdown = JsonDocument.Parse("""{"jsonrpc":"2.0","id":3,"method":"shutdown","params":null}""");
        using var exit = JsonDocument.Parse("""{"jsonrpc":"2.0","method":"exit","params":null}""");

        await JsonRpc.WriteMessageAsync(input, openCurrent.RootElement);
        await JsonRpc.WriteMessageAsync(input, hoverCurrent.RootElement);
        await JsonRpc.WriteMessageAsync(input, openUnrelated.RootElement);
        await JsonRpc.WriteMessageAsync(input, changeUnrelated.RootElement);
        await JsonRpc.WriteMessageAsync(input, hoverCurrent.RootElement);
        await JsonRpc.WriteMessageAsync(input, shutdown.RootElement);
        await JsonRpc.WriteMessageAsync(input, exit.RootElement);
        input.Position = 0;

        var compileCount = 0;
        using var server = new LspServer(
            input,
            output,
            [],
            compileDocumentOverride: (path, _) =>
            {
                Interlocked.Increment(ref compileCount);
                return new IdeSemanticSnapshot
                {
                    Success = true,
                    InputFile = path,
                    CompletedPhase = "types"
                };
            },
            diagnosticDebounce: TimeSpan.FromMinutes(5));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await server.RunAsync(timeout.Token);

        Assert.Equal(1, compileCount);
    }

    [Fact]
    public async Task RunAsync_EofWithPendingDiagnostics_DoesNotHang()
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        var uri = new Uri(Path.GetFullPath("pending_diagnostics.eidos")).AbsoluteUri;
        var didOpen = JsonDocument.Parse($$"""
        {
          "jsonrpc": "2.0",
          "method": "textDocument/didOpen",
          "params": {
            "textDocument": {
              "uri": {{JsonSerializer.Serialize(uri)}},
              "languageId": "eidos",
              "version": 1,
              "text": "value :: 1;"
            }
          }
        }
        """);
        await JsonRpc.WriteMessageAsync(input, didOpen.RootElement);
        input.Position = 0;

        var compileEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var unblockCompile = new ManualResetEventSlim(false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var server = new LspServer(
            input,
            output,
            [],
            compileDocumentOverride: (_, _) =>
            {
                compileEntered.TrySetResult(true);
                unblockCompile.Wait(TimeSpan.FromSeconds(30));
                return new IdeSemanticSnapshot
                {
                    Success = true,
                    InputFile = "pending_diagnostics.eidos",
                    CompletedPhase = "types"
                };
            },
            diagnosticDebounce: TimeSpan.Zero);

        var runTask = server.RunAsync(timeout.Token);
        try
        {
            var firstCompletion = await Task.WhenAny(compileEntered.Task, runTask)
                .WaitAsync(TimeSpan.FromSeconds(15));
            if (ReferenceEquals(firstCompletion, compileEntered.Task))
            {
                await runTask.WaitAsync(TimeSpan.FromSeconds(15));
            }
            else
            {
                await runTask;
            }
        }
        finally
        {
            unblockCompile.Set();
        }
    }

    [Fact]
    public async Task RunAsync_GeneratedDocumentRequest_ReturnsVirtualSource()
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        var sourceUri = new Uri(Path.GetFullPath("generated_document.eidos")).AbsoluteUri;
        const string generatedUri = "eidos-generated://stable.eidos";
        using var didOpen = JsonDocument.Parse($$"""
        {
          "jsonrpc": "2.0",
          "method": "textDocument/didOpen",
          "params": {
            "textDocument": {
              "uri": {{JsonSerializer.Serialize(sourceUri)}},
              "languageId": "eidos",
              "version": 1,
              "text": "value :: 1;"
            }
          }
        }
        """);
        using var hover = JsonDocument.Parse($$"""
        {
          "jsonrpc": "2.0",
          "id": 2,
          "method": "textDocument/hover",
          "params": {
            "textDocument": { "uri": {{JsonSerializer.Serialize(sourceUri)}} },
            "position": { "line": 0, "character": 0 }
          }
        }
        """);
        using var generated = JsonDocument.Parse($$"""
        {
          "jsonrpc": "2.0",
          "id": 3,
          "method": "eidos/generatedDocument",
          "params": { "uri": {{JsonSerializer.Serialize(generatedUri)}} }
        }
        """);
        using var shutdown = JsonDocument.Parse("""{"jsonrpc":"2.0","id":4,"method":"shutdown","params":null}""");
        using var exit = JsonDocument.Parse("""{"jsonrpc":"2.0","method":"exit","params":null}""");

        await JsonRpc.WriteMessageAsync(input, didOpen.RootElement);
        await JsonRpc.WriteMessageAsync(input, hover.RootElement);
        await JsonRpc.WriteMessageAsync(input, generated.RootElement);
        await JsonRpc.WriteMessageAsync(input, shutdown.RootElement);
        await JsonRpc.WriteMessageAsync(input, exit.RootElement);
        input.Position = 0;

        using var server = new LspServer(
            input,
            output,
            [],
            compileDocumentOverride: (_, _) => new IdeSemanticSnapshot
            {
                Success = true,
                InputFile = "generated_document.eidos",
                CompletedPhase = "types",
                GeneratedDocuments =
                [
                    new IdeGeneratedDocumentEntry
                    {
                        Uri = generatedUri,
                        StableIdentity = "stable",
                        GeneratorIdentity = "deriveAnswer",
                        TargetIdentity = "Subject",
                        Content = "answer :: Int;"
                    }
                ]
            },
            diagnosticDebounce: TimeSpan.FromMinutes(5));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await server.RunAsync(timeout.Token);

        output.Position = 0;
        JsonElement? generatedResponse = null;
        while (await JsonRpc.ReadMessageAsync(output, timeout.Token) is { } message)
        {
            if (message.TryGetProperty("id", out var id) && id.GetInt32() == 3)
            {
                generatedResponse = message.Clone();
                break;
            }
        }

        Assert.True(generatedResponse.HasValue);
        var result = generatedResponse.Value.GetProperty("result");
        Assert.Equal(generatedUri, result.GetProperty("uri").GetString());
        Assert.Equal("answer :: Int;", result.GetProperty("content").GetString());
    }

    [Fact]
    public async Task RunAsync_BuildGeneratedModuleSupportsDefinitionVirtualDocumentAndCacheRestore()
    {
        using var workspace = TestTempWorkspace.Create("eidos_lsp_build_generated_module");
        workspace.WriteText(
            "eidos.toml",
            """
            manifestSchema = 3

            [language]
            version = "0.9.0-alpha.1"

            [package]
            name = "dev.eidos.test.lsp-generated-module"
            version = "0.1.0"

            [build]
            program = "build.eidos"
            outputRoots = ["build/generated"]
            """);
        workspace.WriteText(
            "build.eidos",
            """
            Session :: comptime build.session();
            Emit :: comptime build.emit(Session);
            Generated :: comptime build.generated_module(Emit, "generated.schema", quote items {
                export answer :: Int = 42;
            }, "main");
            BuildGraph :: comptime build.graph(Emit, [], [Generated]);
            """);
        const string source = """
            import generated.schema

            main :: Unit -> Int
            {
                _ => generated.schema.answer
            }
            """;
        var sourcePath = workspace.WriteText("src/Main.eidos", source);
        var loaded = EidosProjectConfigurationLoader.LoadFromPath(workspace.Root);
        var primingResult = await EidosBuildHost.RunAsync(new EidosBuildHostOptions
        {
            ProjectDirectory = workspace.Root,
            Configuration = loaded.Configuration.Build!,
            LanguageVersion = loaded.Configuration.LanguageVersion,
            TargetName = "main",
            ImportSearchRoots = loaded.Configuration.SourceRoots,
            NoImplicitPrelude = loaded.Configuration.NoImplicitPrelude,
            UseCache = true
        });
        Assert.True(primingResult.Success);
        var generatedArtifact = Assert.Single(primingResult.Graph!.Artifacts);

        using var input = new MemoryStream();
        using var output = new MemoryStream();
        var sourceUri = new Uri(sourcePath).AbsoluteUri;
        using var didOpen = JsonDocument.Parse($$"""
        {
          "jsonrpc": "2.0",
          "method": "textDocument/didOpen",
          "params": {
            "textDocument": {
              "uri": {{JsonSerializer.Serialize(sourceUri)}},
              "languageId": "eidos",
              "version": 1,
              "text": {{JsonSerializer.Serialize(source)}}
            }
          }
        }
        """);
        using var definition = JsonDocument.Parse($$"""
        {
          "jsonrpc": "2.0",
          "id": 2,
          "method": "textDocument/definition",
          "params": {
            "textDocument": { "uri": {{JsonSerializer.Serialize(sourceUri)}} },
            "position": { "line": 4, "character": 28 }
          }
        }
        """);
        using var generated = JsonDocument.Parse($$"""
        {
          "jsonrpc": "2.0",
          "id": 3,
          "method": "eidos/generatedDocument",
          "params": { "uri": {{JsonSerializer.Serialize(generatedArtifact.SourceUri)}} }
        }
        """);
        using var shutdown = JsonDocument.Parse("""{"jsonrpc":"2.0","id":4,"method":"shutdown","params":null}""");
        using var exit = JsonDocument.Parse("""{"jsonrpc":"2.0","method":"exit","params":null}""");

        await JsonRpc.WriteMessageAsync(input, didOpen.RootElement);
        await JsonRpc.WriteMessageAsync(input, definition.RootElement);
        await JsonRpc.WriteMessageAsync(input, generated.RootElement);
        await JsonRpc.WriteMessageAsync(input, shutdown.RootElement);
        await JsonRpc.WriteMessageAsync(input, exit.RootElement);
        input.Position = 0;

        using var server = new LspServer(input, output, [], diagnosticDebounce: TimeSpan.FromMinutes(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.RunAsync(timeout.Token);

        output.Position = 0;
        JsonElement? definitionResponse = null;
        JsonElement? generatedResponse = null;
        while (await JsonRpc.ReadMessageAsync(output, timeout.Token) is { } message)
        {
            if (!message.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            if (id.GetInt32() == 2)
            {
                definitionResponse = message.Clone();
            }
            else if (id.GetInt32() == 3)
            {
                generatedResponse = message.Clone();
            }
        }

        Assert.True(definitionResponse.HasValue);
        Assert.Contains(
            generatedArtifact.SourceUri,
            definitionResponse.Value.GetProperty("result").GetRawText(),
            StringComparison.Ordinal);
        Assert.True(generatedResponse.HasValue);
        var generatedDocument = generatedResponse.Value.GetProperty("result");
        Assert.Equal(generatedArtifact.SourceUri, generatedDocument.GetProperty("uri").GetString());
        Assert.Equal(generatedArtifact.EmbeddedSource, generatedDocument.GetProperty("content").GetString());
    }

    [Fact]
    public async Task RunAsync_ManifestCodeAction_UsesNamingIdentityAdapter()
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        var uri = new Uri(Path.Combine(Path.GetTempPath(), "eidos-lsp-manifest", "eidos.toml")).AbsoluteUri;
        const string text = """
[package]
name = "Acme.Core"
version = "0.9.0-alpha.1"
""";
        using var didOpen = JsonDocument.Parse($$"""
        {
          "jsonrpc": "2.0",
          "method": "textDocument/didOpen",
          "params": {
            "textDocument": {
              "uri": {{JsonSerializer.Serialize(uri)}},
              "languageId": "toml",
              "version": 1,
              "text": {{JsonSerializer.Serialize(text)}}
            }
          }
        }
        """);
        using var codeAction = JsonDocument.Parse($$"""
        {
          "jsonrpc": "2.0",
          "id": 2,
          "method": "textDocument/codeAction",
          "params": {
            "textDocument": { "uri": {{JsonSerializer.Serialize(uri)}} },
            "range": {
              "start": { "line": 1, "character": 0 },
              "end": { "line": 1, "character": 30 }
            },
            "context": { "diagnostics": [] }
          }
        }
        """);
        using var shutdown = JsonDocument.Parse("""{"jsonrpc":"2.0","id":3,"method":"shutdown","params":null}""");

        await JsonRpc.WriteMessageAsync(input, didOpen.RootElement);
        await JsonRpc.WriteMessageAsync(input, codeAction.RootElement);
        await JsonRpc.WriteMessageAsync(input, shutdown.RootElement);
        input.Position = 0;

        using var server = new LspServer(input, output, [], diagnosticDebounce: TimeSpan.FromMinutes(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await server.RunAsync(timeout.Token);

        output.Position = 0;
        JsonElement? response = null;
        while (await JsonRpc.ReadMessageAsync(output, timeout.Token) is { } message)
        {
            if (message.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == 2)
            {
                response = message.Clone();
                break;
            }
        }

        Assert.True(response.HasValue);
        var action = Assert.Single(response.Value.GetProperty("result").EnumerateArray());
        Assert.Equal("Rename Acme.Core to acme.core", action.GetProperty("title").GetString());
    }

    [Fact]
    public async Task RunAsync_SourceCodeAction_UsesCurrentDocumentVersionAndOffsetDerivedRange()
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        var uri = new Uri(Path.GetFullPath("versioned_code_action.eidos")).AbsoluteUri;
        const string openedText = "old";
        const string currentText = "α😀\r\nsecond";
        using var didOpen = CreateDidOpen(uri, 41, openedText);
        using var didChange = CreateDidChange(uri, 42, currentText);
        using var codeAction = JsonDocument.Parse($$"""
        {
          "jsonrpc": "2.0",
          "id": 9,
          "method": "textDocument/codeAction",
          "params": {
            "textDocument": { "uri": {{JsonSerializer.Serialize(uri)}} },
            "range": {
              "start": { "line": 0, "character": 0 },
              "end": { "line": 1, "character": 6 }
            },
            "context": { "diagnostics": [] }
          }
        }
        """);
        using var shutdown = JsonDocument.Parse("""{"jsonrpc":"2.0","id":10,"method":"shutdown","params":null}""");

        await JsonRpc.WriteMessageAsync(input, didOpen.RootElement);
        await JsonRpc.WriteMessageAsync(input, didChange.RootElement);
        await JsonRpc.WriteMessageAsync(input, codeAction.RootElement);
        await JsonRpc.WriteMessageAsync(input, shutdown.RootElement);
        input.Position = 0;

        using var server = new LspServer(
            input,
            output,
            [],
            compileDocumentOverride: (_, _) => new IdeSemanticSnapshot
            {
                Success = true,
                InputFile = LspServer.UriToFilePath(uri),
                Refactors =
                [
                    new IdeDiagnosticEntry
                    {
                        Code = "S1005",
                        Span = new IdeSpan { Start = 1, Length = 7 },
                        Suggestions =
                        [
                            new IdeDiagnosticSuggestionEntry
                            {
                                Kind = "StyleRewrite",
                                Message = "Rewrite selection",
                                Span = new IdeSpan { Start = 1, Length = 7 },
                                Replacement = "replacement"
                            }
                        ]
                    }
                ]
            },
            diagnosticDebounce: TimeSpan.FromMinutes(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await server.RunAsync(timeout.Token);

        output.Position = 0;
        JsonElement? response = null;
        while (await JsonRpc.ReadMessageAsync(output, timeout.Token) is { } message)
        {
            if (message.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == 9)
            {
                response = message.Clone();
                break;
            }
        }

        var action = Assert.Single(response!.Value.GetProperty("result").EnumerateArray());
        Assert.Equal("refactor.rewrite", action.GetProperty("kind").GetString());
        var documentEdit = Assert.Single(action.GetProperty("edit").GetProperty("documentChanges").EnumerateArray());
        Assert.Equal(42, documentEdit.GetProperty("textDocument").GetProperty("version").GetInt32());
        var edit = Assert.Single(documentEdit.GetProperty("edits").EnumerateArray());
        var range = edit.GetProperty("range");
        Assert.Equal(0, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(1, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(1, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(3, range.GetProperty("end").GetProperty("character").GetInt32());
    }

    private static JsonDocument CreateDidOpen(string uri, int version, string text) =>
        JsonDocument.Parse($$"""
        {
          "jsonrpc": "2.0",
          "method": "textDocument/didOpen",
          "params": {
            "textDocument": {
              "uri": {{JsonSerializer.Serialize(uri)}},
              "languageId": "eidos",
              "version": {{version}},
              "text": {{JsonSerializer.Serialize(text)}}
            }
          }
        }
        """);

    private static JsonDocument CreateDidChange(string uri, int version, string text) =>
        JsonDocument.Parse($$"""
        {
          "jsonrpc": "2.0",
          "method": "textDocument/didChange",
          "params": {
            "textDocument": {
              "uri": {{JsonSerializer.Serialize(uri)}},
              "version": {{version}}
            },
            "contentChanges": [{ "text": {{JsonSerializer.Serialize(text)}} }]
          }
        }
        """);

    private static JsonDocument CreateHover(string uri, int id) =>
        JsonDocument.Parse($$"""
        {
          "jsonrpc": "2.0",
          "id": {{id}},
          "method": "textDocument/hover",
          "params": {
            "textDocument": { "uri": {{JsonSerializer.Serialize(uri)}} },
            "position": { "line": 0, "character": 0 }
          }
        }
        """);

    private sealed class PublicationGatedInputStream(
        byte[] buffer,
        long gatePosition,
        Task publication) : MemoryStream(buffer)
    {
        private bool _gatePassed;

        public override int ReadByte()
        {
            if (!_gatePassed && Position >= gatePosition)
            {
                if (!publication.Wait(TimeSpan.FromSeconds(30)))
                {
                    throw new TimeoutException("LSP diagnostics were not published before shutdown");
                }
                _gatePassed = true;
            }

            return base.ReadByte();
        }
    }

    private sealed class PublicationSignalingStream(
        TaskCompletionSource<bool> published) : MemoryStream
    {
        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            SignalWhenDiagnosticsArePublished(buffer.Span);
        }

        public override async Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            await base.WriteAsync(buffer, offset, count, cancellationToken);
            SignalWhenDiagnosticsArePublished(buffer.AsSpan(offset, count));
        }

        private void SignalWhenDiagnosticsArePublished(ReadOnlySpan<byte> buffer)
        {
            if (Encoding.UTF8.GetString(buffer).Contains(
                    "textDocument/publishDiagnostics",
                    StringComparison.Ordinal))
            {
                published.TrySetResult(true);
            }
        }
    }
}
