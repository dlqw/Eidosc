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
            version = "0.7.0-alpha.1"

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
                answer :: Int = 42;
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
            NoImplicitPrelude = loaded.Configuration.NoImplicitStdlib,
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
version = "0.7.0-alpha.1"
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
}
