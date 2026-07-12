using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Eidosc.Ide;
using Eidosc.Pipeline;

namespace Eidosc.Benchmarks;

[Config(typeof(BenchmarkConfig))]
[MemoryDiagnoser]
public class IdeSnapshotBenchmarks
{
    private CompilationResult _basicResult = null!;
    private CompilationResult _snakeGuiResult = null!;

    [GlobalSetup]
    public void Setup()
    {
        _basicResult = Compile(BenchmarkInputs.Load(Path.Combine("..", "projects", "test", "src", "types", "generic_signature.eidos")));
        _snakeGuiResult = Compile(BenchmarkInputs.Load(Path.Combine("..", "projects", "snake-gui", "src", "Main.eidos")));
    }

    [Benchmark(Baseline = true)]
    public IdeSemanticSnapshot GenericSignatureSnapshot() => IdeSemanticSnapshotBuilder.Build(_basicResult);

    [Benchmark]
    public IdeSemanticSnapshot SnakeGuiSnapshot() => IdeSemanticSnapshotBuilder.Build(_snakeGuiResult);

    private static CompilationResult Compile(BenchmarkInput input)
    {
        var pipeline = new CompilationPipeline(input.SourceText, input.Options);
        return pipeline.Run();
    }
}
