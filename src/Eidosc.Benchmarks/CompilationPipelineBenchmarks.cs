using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Eidosc.Pipeline;

namespace Eidosc.Benchmarks;

[Config(typeof(BenchmarkConfig))]
[MemoryDiagnoser]
public class CompilationPipelineBenchmarks
{
    private BenchmarkInput _basic = null!;
    private BenchmarkInput _stdlib = null!;
    private BenchmarkInput _snakeGui = null!;

    [GlobalSetup]
    public void Setup()
    {
        _basic = BenchmarkInputs.Load(Path.Combine("..", "projects", "test", "src", "basic", "functions.eidos"));
        _stdlib = BenchmarkInputs.Load(Path.Combine("..", "projects", "test", "src", "stdlib", "std_prelude_import.eidos"));
        _snakeGui = BenchmarkInputs.Load(Path.Combine("..", "projects", "snake-gui", "src", "Main.eidos"));
    }

    [Benchmark(Baseline = true)]
    public CompilationResult BasicTypes() => Run(_basic);

    [Benchmark]
    public CompilationResult StdlibPreludeTypes() => Run(_stdlib);

    [Benchmark]
    public CompilationResult SnakeGuiTypes() => Run(_snakeGui);

    private static CompilationResult Run(BenchmarkInput input)
    {
        var pipeline = new CompilationPipeline(input.SourceText, input.Options);
        return pipeline.Run();
    }
}
