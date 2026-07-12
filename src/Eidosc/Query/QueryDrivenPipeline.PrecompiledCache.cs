using Eidosc.Pipeline;

namespace Eidosc.Query;

public sealed partial class QueryDrivenPipeline
{
    private bool TryGetOrCreatePrecompiledSignatureTokens(
        string sourceText,
        string sourceName,
        out IReadOnlyList<Token> tokens,
        out IReadOnlyList<Diagnostic.Diagnostic> lexerDiagnostics)
    {
        var cached = PrecompiledModuleCache.GetOrCreateTokens(
            PrecompiledTokenCacheKind.SignatureOnly,
            sourceText,
            sourceName,
            _moduleParseService!,
            _cancellationToken,
            addLexerErrorDiagnosticsBeforeContextDiagnostics: false);
        tokens = cached.Tokens;
        lexerDiagnostics = cached.LexerDiagnostics;
        AddProfilingCounter(
            cached.CacheHit
                ? "Query.precompiledSignatureTokenCache.hits"
                : "Query.precompiledSignatureTokenCache.misses",
            1);
        return true;
    }
}
