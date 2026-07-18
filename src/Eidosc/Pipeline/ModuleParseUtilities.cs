using Eidosc.Ast.Declarations;
using Eidosc.Diagnostic;
using Eidosc.Parsing.Handwritten;
using Eidosc.Pipeline.TokenRewriting;
using Eidosc.Utils;

namespace Eidosc.Pipeline;

internal static class ModuleParseUtilities
{
    public static ModuleParseResult ParseSource(
        string sourceText,
        string sourceName,
        string languageVersion,
        ScannerData scannerData,
        GrammarData grammarData,
        CancellationToken cancellationToken = default)
    {
        var lexResult = LexSource(
            sourceText,
            sourceName,
            scannerData,
            grammarData,
            cancellationToken);
        var parseResult = ParseTokenList(lexResult.Tokens, sourceName, languageVersion, sourceText: sourceText);
        var diagnostics = new List<Diagnostic.Diagnostic>(lexResult.Diagnostics.Count + parseResult.Diagnostics.Count);
        diagnostics.AddRange(lexResult.Diagnostics);
        diagnostics.AddRange(parseResult.Diagnostics);

        return new ModuleParseResult(
            parseResult.Ast,
            lexResult.Tokens,
            diagnostics,
            parseResult.Ast != null && !diagnostics.Any(static diagnostic => diagnostic.Level == DiagnosticLevel.Error));
    }

    public static ModuleLexResult LexSource(
        string sourceText,
        string sourceName,
        ScannerData scannerData,
        GrammarData grammarData,
        CancellationToken cancellationToken = default,
        bool addLexerErrorDiagnosticsBeforeContextDiagnostics = true)
    {
        var diagnostics = new List<Diagnostic.Diagnostic>();
        var sourceStream = new SourceStream(sourceText, 4, new SourceLocation(0, 0, 0, sourceName));
        var context = new LexerContext(sourceStream, scannerData, grammarData.Terminals);

        Scanner.Init(context);
        var tokens = new List<Token>();
        while (context.TokenStream!.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            tokens.Add(context.TokenStream.Current);
        }

        if (addLexerErrorDiagnosticsBeforeContextDiagnostics)
        {
            AddLexerErrorDiagnostics(tokens, diagnostics);
        }

        GuardTokenNormalizer.Normalize(tokens, context, rewriteAnonymousLambdas: false);
        diagnostics.AddRange(context.Diagnostics);

        if (!addLexerErrorDiagnosticsBeforeContextDiagnostics)
        {
            AddLexerErrorDiagnostics(tokens, diagnostics);
        }

        return new ModuleLexResult(tokens, diagnostics);
    }

    public static ModuleParseResult ParseTokenList(
        IReadOnlyList<Token> tokens,
        string sourceName,
        string languageVersion,
        IReadOnlyList<Diagnostic.Diagnostic>? lexerDiagnostics = null,
        string? sourceText = null)
    {
        var parseTokens = CloneTokenList(tokens);
        var (ast, parserDiagnostics) = SyntaxParser.Parse(
            parseTokens,
            sourceName,
            languageVersion,
            sourceText: sourceText);
        var diagnostics = new List<Diagnostic.Diagnostic>(
            (lexerDiagnostics?.Count ?? 0) + parserDiagnostics.Count);
        if (lexerDiagnostics is not null)
        {
            diagnostics.AddRange(lexerDiagnostics);
        }

        diagnostics.AddRange(parserDiagnostics);
        return new ModuleParseResult(
            ast,
            parseTokens,
            diagnostics,
            ast != null && !diagnostics.Any(static diagnostic => diagnostic.Level == DiagnosticLevel.Error));
    }

    public static void AddLexerErrorDiagnostics(
        IReadOnlyList<Token> tokens,
        List<Diagnostic.Diagnostic> diagnostics)
    {
        foreach (var token in tokens)
        {
            if (token is not ErrorToken errorToken)
            {
                continue;
            }

            var message = errorToken.Message switch
            {
                "ErrBadStrLiteral" => "Bad string literal.",
                "ErrInvEscape" => "Invalid escape sequence.",
                "ErrBadChar" => "Bad character literal.",
                "ErrInvNumber" => "Invalid number literal.",
                var text when string.Equals(text, DiagnosticMessages.UnexpectedEndOfFile, StringComparison.Ordinal) =>
                    DiagnosticMessages.UnexpectedEndOfFile,
                _ => errorToken.Message
            };
            var code = errorToken.Message is "ErrBadStrLiteral" or "ErrInvEscape" or "ErrBadChar"
                ? "E4002"
                : "E4001";

            diagnostics.Add(Diagnostic.Diagnostic.Error(message, code)
                .WithLabel(new SourceSpan(errorToken.Location, Math.Max(errorToken.Length, 1)), DiagnosticMessages.ParserHereLabel));
        }
    }

    public static List<Token> CloneTokenList(IReadOnlyList<Token> tokens)
    {
        var clone = new List<Token>(tokens.Count);
        foreach (var token in tokens)
        {
            clone.Add(CloneToken(token));
        }

        return clone;
    }

    private static Token CloneToken(Token token)
    {
        var clone = token switch
        {
            ContentToken content => new ContentToken(
                content.Location,
                content.Kind,
                content.Terminal,
                content.TextId,
                content.Length,
                content.Value),
            ErrorToken error => new ErrorToken(error.Location, error.Message),
            CommentToken comment => new CommentToken(comment.Location, comment.Comment),
            EofToken eof => new EofToken(eof.Location),
            _ => token
        };
        clone.IsLexerOnly = token.IsLexerOnly;
        return clone;
    }
}

internal sealed class ModuleParseService
{
    private readonly ScannerData _scannerData;
    private readonly GrammarData _grammarData;

    public ModuleParseService(ScannerData scannerData, GrammarData grammarData)
    {
        _scannerData = scannerData;
        _grammarData = grammarData;
    }

    public ModuleParseResult ParseSource(
        string sourceText,
        string sourceName,
        string languageVersion,
        CancellationToken cancellationToken = default)
    {
        return ModuleParseUtilities.ParseSource(
            sourceText,
            sourceName,
            languageVersion,
            _scannerData,
            _grammarData,
            cancellationToken);
    }

    public ModuleLexResult LexSource(
        string sourceText,
        string sourceName,
        CancellationToken cancellationToken = default,
        bool addLexerErrorDiagnosticsBeforeContextDiagnostics = true)
    {
        return ModuleParseUtilities.LexSource(
            sourceText,
            sourceName,
            _scannerData,
            _grammarData,
            cancellationToken,
            addLexerErrorDiagnosticsBeforeContextDiagnostics);
    }

    public ModuleParseResult ParseTokenList(
        IReadOnlyList<Token> tokens,
        string sourceName,
        string languageVersion,
        IReadOnlyList<Diagnostic.Diagnostic>? lexerDiagnostics = null)
    {
        return ModuleParseUtilities.ParseTokenList(
            tokens,
            sourceName,
            languageVersion,
            lexerDiagnostics);
    }
}

internal sealed record ModuleParseResult(
    ModuleDecl? Ast,
    List<Token> Tokens,
    List<Diagnostic.Diagnostic> Diagnostics,
    bool Success);

internal sealed record ModuleLexResult(
    List<Token> Tokens,
    IReadOnlyList<Diagnostic.Diagnostic> Diagnostics);
