using System.Text.Json;
using Eidosc.Cli.Lsp;
using Eidosc.Ide;

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
}
