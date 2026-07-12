using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Eidosc.Cli.Lsp;
using Eidosc.Ide;
using Eidosc.Pipeline;

namespace Eidosc.Benchmarks;

[Config(typeof(BenchmarkConfig))]
[MemoryDiagnoser]
public class LspHandlerBenchmarks
{
    private IdeSemanticSnapshot _snapshot = null!;
    private LspSemanticMapper.SnapshotIndex _index = null!;
    private LspSemanticTokens _cachedSemanticTokens = null!;
    private string _source = "";
    private string _path = "";
    private (int Line, int Character) _hoverPosition;
    private (int Line, int Character) _definitionPosition;
    private (int Line, int Character) _referencesPosition;

    [GlobalSetup]
    public void Setup()
    {
        var input = BenchmarkInputs.Load(Path.Combine("..", "projects", "snake-gui", "src", "Main.eidos"));
        _source = input.SourceText;
        _path = input.SourcePath;
        _hoverPosition = FindPosition(_source, "advance_state(current)");
        _definitionPosition = FindPosition(_source, "advance_state ::");
        _referencesPosition = FindPosition(_source, "GameState ::");

        var pipeline = new CompilationPipeline(input.SourceText, input.Options);
        _snapshot = IdeSemanticSnapshotBuilder.Build(pipeline.Run());
        _index = new LspSemanticMapper.SnapshotIndex(_snapshot);
        _cachedSemanticTokens = LspSemanticMapper.MapSemanticTokens(_snapshot, _index, _path, _source);
    }

    [Benchmark(Baseline = true)]
    public LspHover? CachedSnapshotHoverHandler()
    {
        return LspSemanticMapper.MapHover(_snapshot, _index, _hoverPosition.Line, _hoverPosition.Character);
    }

    [Benchmark]
    public LspLocation? CachedSnapshotDefinitionHandler()
    {
        return LspSemanticMapper.MapDefinition(
            _snapshot,
            _index,
            _definitionPosition.Line,
            _definitionPosition.Character);
    }

    [Benchmark]
    public List<LspLocation> CachedSnapshotReferencesHandler()
    {
        return LspSemanticMapper.MapReferences(
            _snapshot,
            _index,
            _referencesPosition.Line,
            _referencesPosition.Character);
    }

    [Benchmark]
    public LspSemanticTokens CachedSnapshotSemanticTokensColdDerivedHandler()
    {
        return LspSemanticMapper.MapSemanticTokens(_snapshot, _index, _path, _source);
    }

    [Benchmark]
    public LspSemanticTokens CachedSnapshotSemanticTokensWarmDerivedHandler()
    {
        return _cachedSemanticTokens;
    }

    private static (int Line, int Character) FindPosition(string source, string marker)
    {
        var index = source.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidOperationException($"Benchmark marker not found: {marker}");
        }

        var line = 0;
        var lineStart = 0;
        for (var i = 0; i < index; i++)
        {
            if (source[i] != '\n')
            {
                continue;
            }

            line++;
            lineStart = i + 1;
        }

        return (line, index - lineStart);
    }
}
