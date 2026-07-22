using System.Text.Json;
using Eidosc.Cli.Lsp;
using Eidosc.Tests.Fixtures;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class LspPerformanceGateTests
{
    private const int HotRequestCount = 50;

    [Fact]
    public async Task ColdHotAndEditSession_ReportsLatencyAndKeepsHotPathScanFree()
    {
        using var workspace = TestTempWorkspace.Create("eidos_lsp_performance_gate");
        var currentPath = workspace.WriteText("Main.eidos", "value :: 1;");
        var unrelatedPath = workspace.WriteText("Other.eidos", "other :: 1;");
        var currentUri = new Uri(currentPath).AbsoluteUri;
        var unrelatedUri = new Uri(unrelatedPath).AbsoluteUri;
        using var input = new MemoryStream();
        using var output = new MemoryStream();

        await WriteDidOpen(input, currentUri, 1, "value :: 1;");
        await WriteHoverBatch(input, currentUri, 1, HotRequestCount + 1);
        await WriteDidOpen(input, unrelatedUri, 1, "other :: 1;");
        await WriteDidChange(input, unrelatedUri, 2, "other :: 2;");
        await WriteHoverBatch(input, currentUri, HotRequestCount + 1, 1);
        await WriteDidChange(input, currentUri, 2, "value :: 2;");
        await WriteHoverBatch(input, currentUri, HotRequestCount + 2, HotRequestCount + 1);
        using var shutdown = JsonDocument.Parse("""{"jsonrpc":"2.0","id":1000,"method":"shutdown","params":null}""");
        using var exit = JsonDocument.Parse("""{"jsonrpc":"2.0","method":"exit","params":null}""");
        await JsonRpc.WriteMessageAsync(input, shutdown.RootElement);
        await JsonRpc.WriteMessageAsync(input, exit.RootElement);
        input.Position = 0;

        using var server = new LspServer(
            input,
            output,
            [workspace.Root],
            diagnosticDebounce: TimeSpan.FromMinutes(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await server.RunAsync(timeout.Token);

        var metrics = server.GetPerformanceSnapshot();
        var report = $"compiles={metrics.SnapshotCompileCount}, hits={metrics.SnapshotCacheHitCount}, " +
                     $"scans={metrics.DirectoryScanCount}, samples={metrics.SnapshotAccessSampleCount}, " +
                     $"p50={metrics.SnapshotAccessP50Milliseconds:F3}ms, " +
                     $"p95={metrics.SnapshotAccessP95Milliseconds:F3}ms, " +
                     $"p99={metrics.SnapshotAccessP99Milliseconds:F3}ms, " +
                     $"fingerprint={metrics.DependencyFingerprintTotalMilliseconds:F3}ms";
        Console.WriteLine($"LSP performance gate: {report}");

        Assert.Equal(2, metrics.SnapshotCompileCount);
        Assert.Equal(HotRequestCount * 2 + 1, metrics.SnapshotCacheHitCount);
        Assert.Equal(0, metrics.DirectoryScanCount);
        Assert.Equal(HotRequestCount * 2 + 3, metrics.SnapshotAccessSampleCount);
        Assert.True(metrics.SnapshotAccessP95Milliseconds < 500, report);
        Assert.True(metrics.SnapshotAccessP50Milliseconds <= metrics.SnapshotAccessP95Milliseconds, report);
        Assert.True(metrics.SnapshotAccessP95Milliseconds <= metrics.SnapshotAccessP99Milliseconds, report);
    }

    private static async Task WriteDidOpen(Stream input, string uri, int version, string text)
    {
        var message = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            method = "textDocument/didOpen",
            @params = new
            {
                textDocument = new { uri, languageId = "eidos", version, text }
            }
        });
        await JsonRpc.WriteMessageAsync(input, message);
    }

    private static async Task WriteDidChange(Stream input, string uri, int version, string text)
    {
        var message = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            method = "textDocument/didChange",
            @params = new
            {
                textDocument = new { uri, version },
                contentChanges = new[] { new { text } }
            }
        });
        await JsonRpc.WriteMessageAsync(input, message);
    }

    private static async Task WriteHoverBatch(Stream input, string uri, int firstId, int count)
    {
        for (var offset = 0; offset < count; offset++)
        {
            var message = JsonSerializer.SerializeToElement(new
            {
                jsonrpc = "2.0",
                id = firstId + offset,
                method = "textDocument/hover",
                @params = new
                {
                    textDocument = new { uri },
                    position = new { line = 0, character = 0 }
                }
            });
            await JsonRpc.WriteMessageAsync(input, message);
        }
    }
}
