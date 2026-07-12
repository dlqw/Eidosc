using MemoryPack;

namespace Eidosc;

[MemoryPackable]
public partial class ScannerData(
    Dictionary<char, LexerRule[][]> lexerOnlySymbolLookup,
    Dictionary<char, LexerRule[][]> lexerSymbolLookup,
    LexerRule[][] otherLexerSymbols)
{
    public readonly Dictionary<char, LexerRule[][]> LexerOnlySymbolLookup = lexerOnlySymbolLookup;
    public readonly Dictionary<char, LexerRule[][]> LexerSymbolLookup = lexerSymbolLookup;
    public readonly LexerRule[][] OtherLexerSymbols = otherLexerSymbols;
}

[MemoryPackable]
public partial class GrammarData(List<Terminal> terminals, List<NonTerminal> nonTerminals, Terminal syntaxError, Terminal eof)
{
    public readonly List<Terminal> Terminals = terminals;
    public readonly List<NonTerminal> NonTerminals = nonTerminals;
    public readonly Terminal SyntaxError = syntaxError;
    public readonly Terminal Eof = eof;
}

[MemoryPackable]
public partial class CacheData(
    GrammarData grammarData,
    ScannerData scannerData,
    string cacheVersion)
{
    public readonly GrammarData GrammarData = grammarData;
    public readonly ScannerData ScannerData = scannerData;
    public readonly string CacheVersion = cacheVersion;
}
