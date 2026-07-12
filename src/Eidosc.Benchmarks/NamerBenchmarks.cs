using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Eidosc.Symbols;

namespace Eidosc.Benchmarks;

[Config(typeof(BenchmarkConfig))]
[MemoryDiagnoser]
public class NamerBenchmarks
{
    private BenchmarkInput _snakeGui = null!;
    private BenchmarkInput _stdlibPrelude = null!;
    private CompilationResult _snakeGuiParse = null!;
    private CompilationResult _stdlibPreludeParse = null!;

    [GlobalSetup]
    public void Setup()
    {
        _snakeGui = BenchmarkInputs.Load(Path.Combine("..", "projects", "snake-gui", "src", "Main.eidos"));
        _stdlibPrelude = BenchmarkInputs.Load(Path.Combine("..", "projects", "test", "src", "stdlib", "std_prelude_import.eidos"));
        _snakeGuiParse = Parse(_snakeGui);
        _stdlibPreludeParse = Parse(_stdlibPrelude);
    }

    [Benchmark(Baseline = true)]
    public bool SnakeGuiResolve() => Resolve(_snakeGuiParse, _snakeGui);

    [Benchmark]
    public bool StdlibPreludeResolve() => Resolve(_stdlibPreludeParse, _stdlibPrelude);

    private static CompilationResult Parse(BenchmarkInput input)
    {
        var options = CloneOptions(input.Options);
        options.StopAtPhase = CompilationPhase.Parser;
        return new CompilationPipeline(input.SourceText, options).Run();
    }

    private static CompilationOptions CloneOptions(CompilationOptions options)
    {
        return new CompilationOptions
        {
            InputFile = options.InputFile,
            LanguageVersion = options.LanguageVersion,
            Target = options.Target,
            StopAtPhase = options.StopAtPhase,
            DebugLevel = options.DebugLevel,
            EnableMirOptimizations = options.EnableMirOptimizations,
            UseColors = options.UseColors,
            ImportSearchRoots = options.ImportSearchRoots,
            PackageImportRoots = new Dictionary<string, string[]>(options.PackageImportRoots, StringComparer.Ordinal),
            NoImplicitPrelude = options.NoImplicitPrelude
        };
    }

    private static bool Resolve(CompilationResult parsed, BenchmarkInput input)
    {
        var symbolTable = new SymbolTable();
        var resolver = new NameResolver(symbolTable, input.SourceText, input.Options.ImportSearchRoots);
        return resolver.Resolve(parsed.Ast!);
    }
}
