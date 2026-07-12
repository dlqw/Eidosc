using Eidosc.Pipeline.TokenRewriting;
using System.Diagnostics;
using Eidosc.Debug;
using Eidosc.Diagnostic;
using Eidosc.Parsing.Lexer;
using Eidosc.Utils;
using MemoryPack;

namespace Eidosc.Pipeline;

public sealed partial class CompilationPipeline
{
    private void LoadOrBuildGrammarData()
    {
        var sw = Stopwatch.StartNew();
        var allocatedBytesBefore = GetCurrentAllocatedBytes();

        var cachePath = GetCachePath();
        using (MeasureSubphase(CompilationPhase.Lexer, "grammar_cache.get"))
        {
            if (GrammarDataCache.TryGet(cachePath, GrammarCacheVersion, out var cachedGrammarData, out var cachedScannerData))
            {
                _grammarData = cachedGrammarData;
                _scannerData = cachedScannerData;
                _moduleParseService = new ModuleParseService(_scannerData, _grammarData);
                RecordPhaseMetrics(CompilationPhase.Lexer, sw, allocatedBytesBefore);
                return;
            }
        }

        using (MeasureSubphase(CompilationPhase.Lexer, "grammar_build.lexer_table"))
        {
            (_grammarData, _scannerData) = LexerTableBuilder.Build();
            _moduleParseService = new ModuleParseService(_scannerData, _grammarData);
        }

        try
        {
            using (MeasureSubphase(CompilationPhase.Lexer, "grammar_cache.write"))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                var cacheData = new CacheData(
                    _grammarData,
                    _scannerData,
                    GrammarCacheVersion);
                GrammarDataCache.Store(cacheData);
                File.WriteAllBytes(cachePath, MemoryPackSerializer.Serialize(cacheData));
            }
        }
        catch (Exception ex)
        {
            _diagnostics.Add(Diagnostic.Diagnostic.Warning(
                DiagnosticMessages.GrammarCacheSaveFailed(ex.Message), "W5002"));
        }

        RecordPhaseMetrics(CompilationPhase.Lexer, sw, allocatedBytesBefore);
    }

    private bool RunLexer()
    {
        using (MeasureSubphase(CompilationPhase.Lexer, "create_compile_context"))
        {
            var sourceStream = new SourceStream(
                _sourceCode,
                4,
                new SourceLocation(0, 0, 0, GetPrimarySourceName()));
            _compileContext = new LexerContext(
                sourceStream, _scannerData!, _grammarData!.Terminals);
        }

        using (MeasureSubphase(CompilationPhase.Lexer, "scanner_init"))
        {
            Scanner.Init(_compileContext);
        }

        using (MeasureSubphase(CompilationPhase.Lexer, "tokenize_stream"))
        {
            _tokens = new List<Token>();
            while (_compileContext.TokenStream!.MoveNext())
            {
                _tokens.Add(_compileContext.TokenStream.Current);
            }
        }

        ModuleParseUtilities.AddLexerErrorDiagnostics(_tokens, _diagnostics);

        using (MeasureSubphase(CompilationPhase.Lexer, "guard_token_normalize"))
        {
            GuardTokenNormalizer.Normalize(
                _tokens,
                _compileContext,
                rewriteAnonymousLambdas: false);
        }

        if (_debugContext.IsEnabled)
        {
            using (MeasureSubphase(CompilationPhase.Lexer, "debug_emit"))
            {
                _debugContext.Emit("tokens", PhaseOutput.FormatTokens(_tokens));

                if (_debugContext.Level >= DebugLevel.Diagnostic)
                {
                    _debugContext.Emit("token_count", _tokens.Count.ToString());
                }
            }
        }

        if (_tokens.Count == 1 && _tokens[0] is EofToken && !string.IsNullOrWhiteSpace(_sourceCode))
        {
            _diagnostics.Add(Diagnostic.Diagnostic.Warning(
                DiagnosticMessages.SourceContainsNoDeclarations, "W4002"));
        }

        return true;
    }
}
