using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Eidosc.Cli.Lsp;

namespace Eidosc.Benchmarks;

[Config(typeof(BenchmarkConfig))]
[MemoryDiagnoser]
public class LspServerBenchmarks
{
    private const int SteadyStateHoverRequestCount = 20;

    private BenchmarkInput _input = null!;
    private JsonElement _initialize;
    private JsonElement _didOpen;
    private JsonElement _hover;
    private JsonElement _shutdown;
    private JsonElement _exit;

    [GlobalSetup]
    public void Setup()
    {
        _input = BenchmarkInputs.Load(Path.Combine("..", "projects", "snake-gui", "src", "Main.eidos"));
        var uri = new Uri(_input.SourcePath).AbsoluteUri;
        _initialize = JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""").RootElement.Clone();
        _didOpen = JsonDocument.Parse($$"""
        {
          "jsonrpc": "2.0",
          "method": "textDocument/didOpen",
          "params": {
            "textDocument": {
              "uri": {{JsonSerializer.Serialize(uri)}},
              "languageId": "eidos",
              "version": 1,
              "text": {{JsonSerializer.Serialize(_input.SourceText)}}
            }
          }
        }
        """).RootElement.Clone();
        _hover = JsonDocument.Parse($$"""
        {
          "jsonrpc": "2.0",
          "id": 2,
          "method": "textDocument/hover",
          "params": {
            "textDocument": { "uri": {{JsonSerializer.Serialize(uri)}} },
            "position": { "line": 0, "character": 0 }
          }
        }
        """).RootElement.Clone();
        _shutdown = JsonDocument.Parse("""{"jsonrpc":"2.0","id":3,"method":"shutdown","params":null}""").RootElement.Clone();
        _exit = JsonDocument.Parse("""{"jsonrpc":"2.0","method":"exit","params":null}""").RootElement.Clone();
    }

    [Benchmark(Baseline = true)]
    public async Task HoverRoundTrip()
    {
        await RunHoverRoundTripAsync(diagnosticDebounce: null);
    }

    [Benchmark]
    public async Task EagerDiagnosticsHoverRoundTrip()
    {
        await RunHoverRoundTripAsync(TimeSpan.Zero);
    }

    [Benchmark]
    public async Task WarmSnapshotHoverRoundTrip()
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        await JsonRpc.WriteMessageAsync(input, _initialize);
        await JsonRpc.WriteMessageAsync(input, _didOpen);
        await JsonRpc.WriteMessageAsync(input, _hover);
        await JsonRpc.WriteMessageAsync(input, _hover);
        await JsonRpc.WriteMessageAsync(input, _shutdown);
        await JsonRpc.WriteMessageAsync(input, _exit);
        input.Position = 0;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var server = new LspServer(input, output, [], diagnosticDebounce: TimeSpan.FromMinutes(5));
        await server.RunAsync(cts.Token);
    }

    [Benchmark]
    public async Task LongSessionRepeatedHoverRoundTrip()
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        await JsonRpc.WriteMessageAsync(input, _initialize);
        await JsonRpc.WriteMessageAsync(input, _didOpen);
        for (var i = 0; i < SteadyStateHoverRequestCount; i++)
        {
            await JsonRpc.WriteMessageAsync(input, _hover);
        }

        await JsonRpc.WriteMessageAsync(input, _shutdown);
        await JsonRpc.WriteMessageAsync(input, _exit);
        input.Position = 0;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var server = new LspServer(input, output, [], diagnosticDebounce: TimeSpan.FromMinutes(5));
        await server.RunAsync(cts.Token);
    }

    private async Task RunHoverRoundTripAsync(TimeSpan? diagnosticDebounce)
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        await JsonRpc.WriteMessageAsync(input, _initialize);
        await JsonRpc.WriteMessageAsync(input, _didOpen);
        await JsonRpc.WriteMessageAsync(input, _hover);
        await JsonRpc.WriteMessageAsync(input, _shutdown);
        await JsonRpc.WriteMessageAsync(input, _exit);
        input.Position = 0;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var server = new LspServer(input, output, [], diagnosticDebounce: diagnosticDebounce);
        await server.RunAsync(cts.Token);
    }
}
