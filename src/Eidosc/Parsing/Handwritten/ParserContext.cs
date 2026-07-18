using Eidosc.Diagnostic;
using Eidosc.ProjectSystem;
using Eidosc.Utilities;
using Eidosc.Utils;

namespace Eidosc.Parsing.Handwritten;

public sealed class ParserContext
{
    private readonly IReadOnlyList<Token> _tokens;
    private readonly EofToken _syntheticEof;
    private readonly string? _sourceText;
    private int _position;
    private readonly List<Diagnostic.Diagnostic> _diagnostics = [];
    private readonly HashSet<string> _namespaceRoots = new(StringComparer.Ordinal)
    {
        WellKnownStrings.Std.Module,
        WellKnownStrings.Meta.Module,
        WellKnownStrings.Build.Module
    };
    private int _stepCount;
    private const int MaxSteps = 500_000;

    public ParserContext(
        IReadOnlyList<Token> tokens,
        string sourcePath,
        string languageVersion = EidosLanguageVersions.Current,
        string? sourceText = null)
    {
        _tokens = tokens;
        var eofLocation = tokens.Count > 0 ? tokens[^1].Location + tokens[^1].Length : SourceLocation.Empty;
        _syntheticEof = new EofToken(eofLocation);
        _position = 0;
        _sourceText = sourceText;
        SourcePath = sourcePath;
        LanguageVersion = string.IsNullOrWhiteSpace(languageVersion)
            ? EidosLanguageVersions.Current
            : languageVersion.Trim();
        if (!EidosLanguageVersions.IsMigrationVersion(LanguageVersion))
        {
            throw new InvalidOperationException($"Unsupported Eidos language version '{LanguageVersion}'.");
        }
    }

    public string SourcePath { get; }
    public string LanguageVersion { get; }
    public bool IsNameFirstSyntax => !string.Equals(LanguageVersion, EidosLanguageVersions.Legacy, StringComparison.Ordinal);
    public bool UsesDotNamespaces =>
        string.Equals(LanguageVersion, EidosLanguageVersions.Previous, StringComparison.Ordinal) ||
        string.Equals(LanguageVersion, EidosLanguageVersions.Current, StringComparison.Ordinal);
    public bool SupportsTypedClauses => string.Equals(LanguageVersion, EidosLanguageVersions.Current, StringComparison.Ordinal);
    public IReadOnlyList<Diagnostic.Diagnostic> Diagnostics => _diagnostics;
    public int Position => _position;
    public int RawPosition => _position;
    public bool IsEof => _position >= _tokens.Count || Current is EofToken;

    public Token Current
    {
        get
        {
            SkipComments();
            return _position >= 0 && _position < _tokens.Count ? _tokens[_position] : _syntheticEof;
        }
    }

    public Token Peek(int offset = 0)
    {
        SkipComments();
        var index = _position + offset;
        return index >= 0 && index < _tokens.Count ? _tokens[index] : _syntheticEof;
    }

    public Token RawCurrent => RawPeek();

    public Token RawPeek(int offset = 0)
    {
        var index = _position + offset;
        return index >= 0 && index < _tokens.Count ? _tokens[index] : _syntheticEof;
    }

    private void SkipComments()
    {
        while (_position < _tokens.Count && _tokens[_position] is CommentToken)
            _position++;
    }

    public Token Advance()
    {
        _stepCount++;
        if (_stepCount > MaxSteps)
            throw new InvalidOperationException(
                $"Parser exceeded {MaxSteps} steps at position {_position}/{_tokens.Count}, " +
                $"token='{GetText()}', source={SourcePath}");
        var token = Current;
        if (_position < _tokens.Count) _position++;
        return token;
    }

    public Token AdvanceRaw()
    {
        _stepCount++;
        if (_stepCount > MaxSteps)
        {
            throw new InvalidOperationException(
                $"Parser exceeded {MaxSteps} steps at position {_position}/{_tokens.Count}, " +
                $"token='{GetRawText()}', source={SourcePath}");
        }

        var token = RawCurrent;
        if (_position < _tokens.Count)
        {
            _position++;
        }

        return token;
    }

    public bool Match(string expected)
    {
        if (TextEquals(Current, expected))
        {
            Advance();
            return true;
        }
        return false;
    }

    public Token Expect(string expected, string errorMessage)
    {
        if (TextEquals(Current, expected))
            return Advance();
        Error(errorMessage);
        Advance();
        return Peek(-1);
    }

    public Token Expect(string expected) =>
        Expect(expected, DiagnosticMessages.ParserExpectedToken(expected));

    public bool Check(string expected) => TextEquals(Current, expected);

    public bool CheckPeek(int offset, string expected) => TextEquals(Peek(offset), expected);

    public int SavePosition() => _position;
    public void RestorePosition(int position) => _position = position;

    public bool MatchAny(ReadOnlySpan<string> expected, out string matched)
    {
        foreach (var e in expected)
        {
            if (TextEquals(Current, e))
            {
                matched = e;
                Advance();
                return true;
            }
        }
        matched = "";
        return false;
    }

    public void Error(string message, SourceLocation? location = null)
    {
        Error(message, "E4001", location);
    }

    public void Error(string message, string code, SourceLocation? location = null)
    {
        var loc = location ?? Current.Location;
        _diagnostics.Add(Diagnostic.Diagnostic.Error(
                DiagnosticMessages.ParserDiagnosticWithPosition(message, _position, GetText()),
                code)
            .WithLabel(new SourceSpan(loc, 1), DiagnosticMessages.ParserHereLabel));
    }

    public void AddDiagnostic(Diagnostic.Diagnostic diagnostic)
    {
        _diagnostics.Add(diagnostic);
    }

    public string GetText(Token? token = null)
    {
        token ??= Current;
        return token switch
        {
            ContentToken ct => GetTokenSyntaxText(ct),
            EofToken => "<eof>",
            ErrorToken et => et.Message,
            _ => token.ToString() ?? ""
        };
    }

    public string GetRawText(Token? token = null)
    {
        token ??= RawCurrent;
        return token switch
        {
            CommentToken comment => comment.Comment,
            ContentToken content => content.TextId.Resolve(),
            EofToken => string.Empty,
            ErrorToken error => error.Message,
            _ => token.ToString() ?? string.Empty
        };
    }

    public string GetSourceSlice(int startPosition, int endPosition)
    {
        if (_sourceText == null ||
            startPosition < 0 ||
            endPosition < startPosition ||
            endPosition > _sourceText.Length)
        {
            return string.Empty;
        }

        return _sourceText[startPosition..endPosition];
    }

    public string GetLiteralRawText(Token? token = null)
    {
        token ??= Current;
        if (token is not ContentToken contentToken)
        {
            return GetText(token);
        }

        return contentToken.Terminal.DebugName switch
        {
            "stringLiteral" when contentToken.Value is string stringValue => QuoteLiteral(stringValue, '"'),
            "charLiteral" when contentToken.Value is char charValue => QuoteLiteral(charValue.ToString(), '\''),
            "stringLiteral" => QuoteLiteral(GetText(token), '"'),
            "charLiteral" => QuoteLiteral(GetText(token), '\''),
            _ => GetText(token)
        };
    }

    private static string QuoteLiteral(string text, char quote)
    {
        if (text.Length >= 2 && text[0] == quote && text[^1] == quote)
        {
            return text;
        }

        var builder = new System.Text.StringBuilder(text.Length + 2);
        builder.Append(quote);
        foreach (var ch in text)
        {
            builder.Append(ch switch
            {
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '\0' => "\\0",
                '"' when quote == '"' => "\\\"",
                '\'' when quote == '\'' => "\\'",
                _ => ch.ToString()
            });
        }

        builder.Append(quote);
        return builder.ToString();
    }

    private static string GetTokenSyntaxText(ContentToken ct)
    {
        if (ct.Kind is SyntaxKind.StringLiteral or SyntaxKind.CharLiteral ||
            ct.Terminal.DebugName is "stringLiteral" or "charLiteral")
        {
            return ct.TextId.Resolve();
        }

        return ct.Value switch
        {
            string text => text,
            StringId id => id.Resolve(),
            _ => ct.TextId.Resolve()
        };
    }

    public SourceSpan SpanFrom(Token start, Token? end = null)
    {
        end ??= Peek(-1);
        var endLoc = end.Location;
        return new SourceSpan(start.Location, endLoc.Position - start.Location.Position + end.Length);
    }

    public bool IsAtPunctuation() => Current is ContentToken { Terminal.Flags: TerminalFlag.IsPunctuation };
    public bool IsAtKeyword() => Current is ContentToken { Terminal.Flags: TerminalFlag.IsKeyword };

    public void RegisterNamespaceRoot(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            _namespaceRoots.Add(name);
        }
    }

    public bool IsKnownNamespaceRoot(Token token) =>
        TokenKind.IsAnyIdentifier(token) && _namespaceRoots.Contains(GetText(token));

    private static bool TextEquals(Token token, string expected)
    {
        if (token is ContentToken ct)
        {
            var text = GetTokenSyntaxText(ct);
            if (string.Equals(text, expected, StringComparison.Ordinal))
            {
                return true;
            }
        }

        var display = token.ToString();
        const string marker = "Token:";
        var tokenMarker = display?.LastIndexOf(marker, StringComparison.Ordinal) ?? -1;
        if (tokenMarker >= 0)
        {
            var tokenText = display![(tokenMarker + marker.Length)..].Trim();
            return string.Equals(tokenText, expected, StringComparison.Ordinal);
        }

        return false;
    }
}
