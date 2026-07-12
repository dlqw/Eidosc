using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Eidosc.Cli.Lsp;
using Eidosc.Ide;
using Eidosc.Pipeline;

namespace Eidosc.Benchmarks;

[Config(typeof(BenchmarkConfig))]
[MemoryDiagnoser]
public class LspMapperBenchmarks
{
    private IdeSemanticSnapshot _snakeGuiSnapshot = null!;
    private LspSemanticMapper.SnapshotIndex _snakeGuiIndex = null!;
    private string _snakeGuiSource = "";
    private string _snakeGuiPath = "";
    private (int Line, int Character) _hoverPosition;
    private (int Line, int Character) _definitionPosition;
    private (int Line, int Character) _referencesPosition;

    [GlobalSetup]
    public void Setup()
    {
        var input = BenchmarkInputs.Load(Path.Combine("..", "projects", "snake-gui", "src", "Main.eidos"));
        _snakeGuiSource = input.SourceText;
        _snakeGuiPath = input.SourcePath;
        _hoverPosition = FindPosition(_snakeGuiSource, "advance_state(current)");
        _definitionPosition = FindPosition(_snakeGuiSource, "advance_state ::");
        _referencesPosition = FindPosition(_snakeGuiSource, "GameState ::");
        var pipeline = new CompilationPipeline(input.SourceText, input.Options);
        _snakeGuiSnapshot = IdeSemanticSnapshotBuilder.Build(pipeline.Run());
        _snakeGuiIndex = new LspSemanticMapper.SnapshotIndex(_snakeGuiSnapshot);
    }

    [Benchmark]
    public LspHover? SnakeGuiCachedHover()
    {
        return LspSemanticMapper.MapHover(_snakeGuiSnapshot, _snakeGuiIndex, _hoverPosition.Line, _hoverPosition.Character);
    }

    [Benchmark]
    public LspLocation? SnakeGuiCachedDefinition()
    {
        return LspSemanticMapper.MapDefinition(
            _snakeGuiSnapshot,
            _snakeGuiIndex,
            _definitionPosition.Line,
            _definitionPosition.Character);
    }

    [Benchmark]
    public List<LspLocation> SnakeGuiCachedReferences()
    {
        return LspSemanticMapper.MapReferences(
            _snakeGuiSnapshot,
            _snakeGuiIndex,
            _referencesPosition.Line,
            _referencesPosition.Character);
    }

    [Benchmark]
    public LspSemanticTokens SnakeGuiCachedSemanticTokens()
    {
        return LspSemanticMapper.MapSemanticTokens(_snakeGuiSnapshot, _snakeGuiIndex, _snakeGuiPath, _snakeGuiSource);
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
