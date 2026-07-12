using System.Buffers;
using System.Runtime.CompilerServices;
using Eidosc.Utils;

namespace Eidosc;

/// <summary>
/// Lexer-only context, decoupled from the dead LR parser runtime.
/// Contains only the fields needed for tokenization and token rewriters.
/// </summary>
public class LexerContext
{
    // Lexer fields (from CompileContext)
    public readonly ISourceStream Source;
    public readonly Dictionary<char, LexerRule[][]> LexerOnlySymbolLookup;
    public readonly Dictionary<char, LexerRule[][]> LexerSymbolLookup;
    public readonly LexerRule[][] OtherLexerSymbols;
    public readonly TokenFilter[] TokenFilters = [];
    private readonly SearchValues<char> _skipSet;

    // Terminals list (needed by Token Rewriters to find Terminal by DebugName)
    public readonly List<Terminal> Terminals;

    /// <summary>
    /// Trie-based keyword lookup for O(L) longest-match. Built from terminals.
    /// </summary>
    public readonly Parsing.Lexer.KeywordTrie? KeywordTrie;

    // Token stream management
    internal readonly Stack<Token> PendingTokenStack = new();
    internal readonly Stack<Token> PreviewTokens = new();
    internal IEnumerator<Token>? TokenStream;

    private readonly List<Diagnostic.Diagnostic> _diagnostics = new();

    /// <summary>
    /// 获取所有诊断信息（只读）
    /// </summary>
    public List<Diagnostic.Diagnostic> Diagnostics => _diagnostics;

    public LexerContext(
        ISourceStream source,
        ScannerData scannerData,
        List<Terminal> terminals)
    {
        Source = source;
        LexerOnlySymbolLookup = scannerData.LexerOnlySymbolLookup;
        LexerSymbolLookup = scannerData.LexerSymbolLookup;
        OtherLexerSymbols = scannerData.OtherLexerSymbols;
        Terminals = terminals;
        KeywordTrie = Parsing.Lexer.KeywordTrie.BuildFromTerminals(terminals);
        _skipSet = SearchValues.Create(" \t\r\n\v");
    }

    public void Report(Diagnostic.Diagnostic diagnostic)
    {
        _diagnostics.Add(diagnostic);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipWhitespace(ISourceStream source)
    {
        ReadOnlySpan<char> remaining = source.RemainingSpan;
        int offset = remaining.IndexOfAnyExcept(_skipSet);
        if (offset < 0)
            source.PreviewPosition += remaining.Length;
        else
            source.PreviewPosition += offset;
    }
}
