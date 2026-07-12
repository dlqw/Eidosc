using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Eidosc.Pipeline;
using Eidosc.Query;

namespace Eidosc.Benchmarks;

[Config(typeof(BenchmarkConfig))]
[MemoryDiagnoser]
public class QuerySessionBenchmarks
{
    private BenchmarkInput _input = null!;
    private PipelineQuerySession _session = null!;
    private string _editedSource = "";
    private string _innerTriviaEditSource = "";
    private string _realEditSource = "";
    private string[] _spanPreservingTokenEditSources = [];
    private long _version;

    [GlobalSetup]
    public void Setup()
    {
        _input = BenchmarkInputs.Load(Path.Combine("..", "projects", "snake-gui", "src", "Main.eidos"));
        _editedSource = _input.SourceText + Environment.NewLine;
        _innerTriviaEditSource = CreateInnerSpanPreservingTriviaEdit(_input.SourceText);
        _realEditSource = _input.SourceText.Replace("tick: 0", "tick: 1", StringComparison.Ordinal);
        _spanPreservingTokenEditSources = CreateSpanPreservingTokenEdits(_input.SourceText);
        _session = new PipelineQuerySession();
        _session.Compile(_input.SourcePath, _input.SourceText, _input.Options, documentVersion: 1);
        _version = 2;
    }

    [Benchmark(Baseline = true)]
    public CompilationResult WarmNoChange()
    {
        return _session.Compile(_input.SourcePath, _input.SourceText, _input.Options, documentVersion: 1);
    }

    [Benchmark]
    public CompilationResult HotEditSamePath()
    {
        var source = (_version & 1) == 0 ? _editedSource : _input.SourceText;
        return _session.Compile(_input.SourcePath, source, _input.Options, documentVersion: _version++);
    }

    [Benchmark]
    public CompilationResult InnerSpanPreservingTriviaEditSamePath()
    {
        var source = (_version & 1) == 0 ? _innerTriviaEditSource : _input.SourceText;
        return _session.Compile(_input.SourcePath, source, _input.Options, documentVersion: _version++);
    }

    [Benchmark]
    public CompilationResult RealLocalEditSamePath()
    {
        var source = (_version & 1) == 0 ? _realEditSource : _input.SourceText;
        return _session.Compile(_input.SourcePath, source, _input.Options, documentVersion: _version++);
    }

    [Benchmark]
    public CompilationResult FreshLocalEditSamePath()
    {
        var source = _input.SourceText.Replace("tick: 0", $"tick: {_version}", StringComparison.Ordinal);
        return _session.Compile(_input.SourcePath, source, _input.Options, documentVersion: _version++);
    }

    [Benchmark]
    public CompilationResult FreshSpanPreservingTokenEditSamePath()
    {
        var source = _spanPreservingTokenEditSources[(int)(_version % _spanPreservingTokenEditSources.Length)];
        return _session.Compile(_input.SourcePath, source, _input.Options, documentVersion: _version++);
    }

    private static string CreateInnerSpanPreservingTriviaEdit(string sourceText)
    {
        var index = sourceText.IndexOf(" :: ", StringComparison.Ordinal);
        if (index < 0)
        {
            return sourceText;
        }

        var chars = sourceText.ToCharArray();
        chars[index] = '\t';
        return new string(chars);
    }

    private static string[] CreateSpanPreservingTokenEdits(string sourceText)
    {
        const string marker = "Eidos Snake GUI";
        var index = sourceText.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return [sourceText];
        }

        var replaceIndex = index + marker.Length - 1;
        const string variants = "ABCDEFGHIJKLMNOP";
        var edits = new string[variants.Length];
        for (var i = 0; i < variants.Length; i++)
        {
            var chars = sourceText.ToCharArray();
            chars[replaceIndex] = variants[i];
            edits[i] = new string(chars);
        }

        return edits;
    }
}
