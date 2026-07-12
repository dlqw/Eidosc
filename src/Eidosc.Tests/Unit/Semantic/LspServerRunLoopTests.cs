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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
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
            await compileEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            unblockCompile.Set();
        }
    }
}
