using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Eidosc.Symbols;
using Eidosc.Types;

namespace Eidosc.Benchmarks;

[Config(typeof(BenchmarkConfig))]
[MemoryDiagnoser]
public class TypesBenchmarks
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
    public bool SnakeGuiInfer() => Infer(_snakeGuiParse, _snakeGui);

    [Benchmark]
    public bool StdlibPreludeInfer() => Infer(_stdlibPreludeParse, _stdlibPrelude);

    private static bool Infer(CompilationResult parsed, BenchmarkInput input)
    {
        var symbolTable = new SymbolTable();
        var resolver = new NameResolver(symbolTable, input.SourceText, input.Options.ImportSearchRoots);
        resolver.Resolve(parsed.Ast!);

        var inferer = new TypeInferer(symbolTable);
        return inferer.Infer(parsed.Ast!);
    }

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
}
