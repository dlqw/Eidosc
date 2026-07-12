using System.Text;
using Eidosc.Debug;
using Eidosc.Parsing.Lexer;
using Eidosc.Pipeline;
using Eidosc.Utilities;
using Eidosc.Utils;
using EidosDiagnostic = Eidosc.Diagnostic.Diagnostic;

namespace Eidosc.CodeFormatting;

public static class EidosFormatter
{
    private static readonly HashSet<string> BinaryOperators = new(StringComparer.Ordinal)
    {
        "=", ":=", "=>", "->", "|", "+", "-", "*", "/", "%", "==", "!=", "<", "<=", ">", ">=",
        "&&", "||", "??", "|>", "<-", "in", "as", "when"
    };

    private static readonly HashSet<string> NoSpaceBefore = new(StringComparer.Ordinal)
    {
        ")", "]", ",", ";", ".", "::", ":"
    };

    private static readonly HashSet<string> NoSpaceAfter = new(StringComparer.Ordinal)
    {
        "(", "[", ".", "::", "@", "!"
    };

    private static readonly HashSet<string> ControlKeywords = new(StringComparer.Ordinal)
    {
        "then", "else", "with"
    };

    public static EidosFormatResult Format(
        string sourceText,
        string inputFile = "stdin.eidos",
        EidosFormatterOptions? options = null)
    {
        options ??= new EidosFormatterOptions();

        if (options.ValidateSyntax)
        {
            var parseResult = RunPipeline(sourceText, inputFile, CompilationPhase.Parser, options);
            if (!parseResult.Success)
            {
                return EidosFormatResult.Failed(parseResult.Diagnostics);
            }
        }

        var lexResult = RunRawLexer(sourceText, inputFile);
        if (lexResult.Diagnostics.Count > 0)
        {
            return EidosFormatResult.Failed(lexResult.Diagnostics);
        }

        var errorTokens = lexResult.Tokens.OfType<ErrorToken>().ToList();
        if (errorTokens.Count > 0)
        {
            var diagnostics = errorTokens
                .Select(token => EidosDiagnostic.Error(token.Message, "E4000"))
                .ToList();
            return EidosFormatResult.Failed(diagnostics);
        }

        var formatter = new TokenFormatter(sourceText, lexResult.Tokens, options);
        var formatted = formatter.Format();
        if (options.ValidateSyntax)
        {
            var formattedParseResult = RunPipeline(formatted, inputFile, CompilationPhase.Parser, options);
            if (!formattedParseResult.Success)
            {
                return EidosFormatResult.Failed(formattedParseResult.Diagnostics);
            }
        }

        return EidosFormatResult.Ok(formatted);
    }

    private static CompilationResult RunPipeline(
        string sourceText,
        string inputFile,
        CompilationPhase stopAtPhase,
        EidosFormatterOptions options)
    {
        var pipeline = new CompilationPipeline(sourceText, new CompilationOptions
        {
            InputFile = inputFile,
            StopAtPhase = stopAtPhase,
            LanguageVersion = options.LanguageVersion,
            DebugLevel = DebugLevel.Minimal,
            UseColors = false,
            Verbose = false
        });

        return pipeline.Run();
    }

    private static RawLexResult RunRawLexer(string sourceText, string inputFile)
    {
        var (grammarData, scannerData) = LexerTableBuilder.Build();
        var sourceStream = new SourceStream(
            sourceText,
            4,
            new SourceLocation(0, 0, 0, inputFile));
        var context = new LexerContext(sourceStream, scannerData, grammarData.Terminals);
        Scanner.Init(context);

        var tokens = new List<Token>();
        while (context.TokenStream!.MoveNext())
        {
            tokens.Add(context.TokenStream.Current);
        }

        return new RawLexResult(tokens, context.Diagnostics);
    }

    private sealed record RawLexResult(
        IReadOnlyList<Token> Tokens,
        IReadOnlyList<EidosDiagnostic> Diagnostics);

    private sealed class TokenFormatter
    {
        private readonly string _sourceText;
        private readonly IReadOnlyList<Token> _tokens;
        private readonly EidosFormatterOptions _options;
        private readonly StringBuilder _builder = new();
        private readonly Stack<string> _containers = new();
        private readonly string _indentUnit;
        private readonly string _newLine;
        private int? _lastTokenEnd;
        private int _indent;
        private bool _atLineStart = true;
        private string? _previousText;
        private bool _previousDoubleColonNeedsSpace;

        public TokenFormatter(string sourceText, IReadOnlyList<Token> tokens, EidosFormatterOptions options)
        {
            _sourceText = sourceText;
            _tokens = tokens.Where(token => token is not EofToken).ToList();
            _options = options;
            _indentUnit = new string(' ', Math.Max(0, options.IndentSize));
            _newLine = options.NewLine ?? DetectNewLine(sourceText);
        }

        public string Format()
        {
            for (var i = 0; i < _tokens.Count; i++)
            {
                var token = _tokens[i];
                var text = GetTokenText(token);
                var nextText = FindNextText(i + 1);

                ApplyOriginalLineBreak(token, text);

                if (token is CommentToken)
                {
                    WriteComment(text);
                    RememberTokenEnd(token);
                    continue;
                }

                WriteToken(text, nextText);
                RememberTokenEnd(token);
            }

            TrimTrailingWhitespace();
            if (_options.FinalNewline)
            {
                EnsureLineBreak();
            }

            var formatted = CollapseEmptyBlocks(_builder.ToString());
            formatted = CompactSimpleIfExpressions(formatted);
            formatted = CompactSimpleConditionalBranches(formatted);
            return ExpandMultilineConditionalBlocks(formatted);
        }

        private void WriteToken(string text, string? nextText)
        {
            if (IsSignedNumber(text) && _previousText is not null && ShouldSplitSignedNumber(_previousText))
            {
                var sign = text[0].ToString();
                var number = text[1..];
                WriteSpaceIfNeededBefore(sign);
                AppendRaw(sign);
                _previousText = sign;
                WriteSpaceIfNeededBefore(number);
                AppendRaw(number);
                _previousText = number;
                return;
            }

            switch (text)
            {
                case "{":
                    WriteSpaceIfNeededBefore(text);
                    AppendRaw(text);
                    _containers.Push(text);
                    _indent++;
                    LineBreak();
                    _previousText = text;
                    return;

                case "}":
                    _indent = Math.Max(0, _indent - 1);
                    if (!_atLineStart)
                    {
                        LineBreak();
                    }
                    WriteIndentIfNeeded();
                    AppendRaw(text);
                    if (_containers.Count > 0 && _containers.Peek() == "{")
                    {
                        _containers.Pop();
                    }
                    _previousText = text;
                    if (nextText is "else" or "with" or "," or ";" or ")" or "]")
                    {
                        return;
                    }
                    LineBreak();
                    return;

                case "(":
                case "[":
                    WriteSpaceIfNeededBefore(text);
                    AppendRaw(text);
                    _containers.Push(text);
                    _previousText = text;
                    return;

                case ")":
                case "]":
                    AppendRaw(text);
                    if (_containers.Count > 0 && IsMatchingContainer(_containers.Peek(), text))
                    {
                        _containers.Pop();
                    }
                    _previousText = text;
                    return;

                case ",":
                    AppendRaw(text);
                    _previousText = text;
                    if (_containers.TryPeek(out var container) && container == "{")
                    {
                        LineBreak();
                    }
                    else if (nextText is not null and not ")" and not "]" and not "}")
                    {
                        AppendRaw(" ");
                    }
                    return;

                case ";":
                    AppendRaw(text);
                    _previousText = text;
                    LineBreak();
                    return;

                case ":":
                    AppendRaw(text);
                    _previousText = text;
                    if (nextText is not null and not "," and not ")" and not "]" and not "}")
                    {
                        AppendRaw(" ");
                    }
                    return;

                case "::":
                    if (ShouldSpaceAroundDoubleColon(nextText))
                    {
                        TrimTrailingWhitespace();
                        if (!_atLineStart && !CurrentLineText().EndsWith(' '))
                        {
                            AppendRaw(" ");
                        }

                        AppendRaw(text);
                        _previousText = text;
                        _previousDoubleColonNeedsSpace = true;

                        return;
                    }

                    AppendRaw(text);
                    _previousText = text;
                    _previousDoubleColonNeedsSpace = false;
                    return;
            }

            WriteSpaceIfNeededBefore(text);
            AppendRaw(text);
            _previousText = text;
            _previousDoubleColonNeedsSpace = false;
        }

        private void ApplyOriginalLineBreak(Token token, string text)
        {
            if (_previousText is null || !ShouldPreserveOriginalLineBreakBefore(text))
            {
                return;
            }

            if (_lastTokenEnd is not { } lastTokenEnd)
            {
                return;
            }

            var tokenStart = token.Location.Position;
            if (tokenStart <= lastTokenEnd || tokenStart > _sourceText.Length)
            {
                return;
            }

            var gap = _sourceText.AsSpan(lastTokenEnd, tokenStart - lastTokenEnd);
            if (gap.IndexOfAny('\n', '\r') >= 0 && !_atLineStart)
            {
                LineBreak();
            }
        }

        private void RememberTokenEnd(Token token)
        {
            if (token.Location.Position < 0 || token.Length < 0)
            {
                return;
            }

            _lastTokenEnd = Math.Min(_sourceText.Length, token.Location.Position + token.Length);
        }

        private static bool ShouldPreserveOriginalLineBreakBefore(string text)
        {
            return text is not ("{" or "}" or ")" or "]" or "," or ";" or "=>" or "->" or "." or "::");
        }

        private void WriteComment(string text)
        {
            if (text.StartsWith("//", StringComparison.Ordinal))
            {
                if (!_atLineStart)
                {
                    AppendRaw(" ");
                }
                WriteIndentIfNeeded();
                AppendRaw(text.TrimEnd());
                LineBreak();
                _previousText = text;
                return;
            }

            if (!_atLineStart)
            {
                AppendRaw(" ");
            }
            WriteIndentIfNeeded();
            AppendRaw(text.TrimEnd());
            if (text.Contains('\n') || text.Contains('\r'))
            {
                LineBreak();
            }
            _previousText = text;
        }

        private void WriteSpaceIfNeededBefore(string text)
        {
            if (_atLineStart)
            {
                WriteIndentIfNeeded();
                return;
            }

            WriteIndentIfNeeded();
            if (_previousText is null)
            {
                return;
            }

            TrimTrailingWhitespace();
            if (NeedsSpace(_previousText, text))
            {
                AppendRaw(" ");
            }
        }

        private bool NeedsSpace(string previous, string current)
        {
            if (previous == "::" && _previousDoubleColonNeedsSpace)
            {
                return true;
            }

            if (NoSpaceBefore.Contains(current) || NoSpaceAfter.Contains(previous))
            {
                return false;
            }

            if (current is "{" && previous is "(" or "[" or "@" or "::" or ".")
            {
                return false;
            }

            if (current is "{")
            {
                return true;
            }

            if (ControlKeywords.Contains(previous) || ControlKeywords.Contains(current))
            {
                return true;
            }

            if (BinaryOperators.Contains(previous) || BinaryOperators.Contains(current))
            {
                return true;
            }

            if (previous is "," or ":" or ";" or "{" || current is "=>")
            {
                return true;
            }

            return IsWordLike(previous) && IsWordLike(current);
        }

        private bool ShouldSpaceAroundDoubleColon(string? nextText)
        {
            if (!string.Equals(
                    _options.LanguageVersion,
                    Eidosc.ProjectSystem.EidosLanguageVersions.Current,
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(nextText))
            {
                return false;
            }

            var line = CurrentLineText().TrimStart();
            if (line.StartsWith("import ", StringComparison.Ordinal))
            {
                return false;
            }

            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line["export ".Length..].TrimStart();
            }

            if (line.Contains("::", StringComparison.Ordinal))
            {
                return false;
            }

            if (nextText is "module" or "type" or "trait" or "ability" or "instance" or "comptime")
            {
                return true;
            }

            return _previousText is not null &&
                   (IsLowerIdentifier(_previousText) || _previousText == "]") &&
                   IsTypeLikeHead(nextText);
        }

        private string CurrentLineText()
        {
            var text = _builder.ToString();
            var lastLineBreak = text.LastIndexOf(_newLine, StringComparison.Ordinal);
            return lastLineBreak < 0 ? text : text[(lastLineBreak + _newLine.Length)..];
        }

        private static bool IsLowerIdentifier(string text)
        {
            return text.Length > 0 &&
                   (char.IsLower(text[0]) || text[0] == '_') &&
                   text.All(ch => char.IsLetterOrDigit(ch) || ch == '_');
        }

        private static bool IsTypeLikeHead(string text)
        {
            return text.Length > 0 &&
                   (char.IsUpper(text[0]) || text[0] == '_' || text == "(") &&
                   (text == "(" || text.All(ch => char.IsLetterOrDigit(ch) || ch == '_'));
        }

        private string CompactSimpleIfExpressions(string text)
        {
            var lines = text.Split(_newLine);
            var compacted = new List<string>(lines.Length);

            for (var i = 0; i < lines.Length; i++)
            {
                if (TryCompactSimpleIf(lines, i, out var compactedLine, out var consumedLines))
                {
                    compacted.Add(compactedLine);
                    i += consumedLines - 1;
                    continue;
                }

                compacted.Add(lines[i]);
            }

            return string.Join(_newLine, compacted);
        }

        private bool TryCompactSimpleIf(
            string[] lines,
            int index,
            out string compactedLine,
            out int consumedLines)
        {
            compactedLine = "";
            consumedLines = 0;

            if (index + 3 >= lines.Length)
            {
                return false;
            }

            var firstLine = lines[index];
            var firstTrimmed = firstLine.Trim();
            if (!firstTrimmed.EndsWith(" then {", StringComparison.Ordinal))
            {
                return false;
            }

            var thenExpr = lines[index + 1].Trim();
            var elseOpen = lines[index + 2].Trim();
            var elseExpr = lines[index + 3].Trim();
            if (elseOpen != "} else {" ||
                !IsSimpleCompactBranch(thenExpr) ||
                !IsSimpleCompactBranch(elseExpr))
            {
                return false;
            }

            if (index + 4 >= lines.Length)
            {
                return false;
            }

            var closeLine = lines[index + 4].Trim();
            if (!TryParseCompactIfClose(closeLine, out var suffix))
            {
                return false;
            }

            var indent = firstLine[..(firstLine.Length - firstLine.TrimStart().Length)];
            var head = firstTrimmed[..^2];
            var candidate = $"{indent}{head} {thenExpr} else {elseExpr}{suffix}";
            if (candidate.Length > _options.MaxLineLength)
            {
                return false;
            }

            compactedLine = candidate;
            consumedLines = 5;
            return true;
        }

        private static bool IsSimpleCompactBranch(string text)
        {
            return text.Length > 0 &&
                   !text.StartsWith("//", StringComparison.Ordinal) &&
                   text.IndexOfAny(['{', '}', ';']) < 0;
        }

        private static bool TryParseCompactIfClose(string text, out string suffix)
        {
            suffix = "";
            if (text == "}")
            {
                return true;
            }

            if (text.StartsWith("}", StringComparison.Ordinal) &&
                text[1..].All(static ch => ch is ';' or ',' or ')' or ']'))
            {
                suffix = text[1..];
                return true;
            }

            return false;
        }

        private string CompactSimpleConditionalBranches(string text)
        {
            var lines = text.Split(_newLine);
            var compacted = new List<string>(lines.Length);

            for (var i = 0; i < lines.Length; i++)
            {
                if (TryCompactSimpleThenBranch(lines, i, out var thenLines, out var thenConsumedLines))
                {
                    compacted.AddRange(thenLines);
                    i += thenConsumedLines - 1;
                    continue;
                }

                if (TryCompactSimpleElseBranch(lines, i, out var elseLines, out var elseConsumedLines))
                {
                    compacted.AddRange(elseLines);
                    i += elseConsumedLines - 1;
                    continue;
                }

                compacted.Add(lines[i]);
            }

            return string.Join(_newLine, compacted);
        }

        private bool TryCompactSimpleThenBranch(
            string[] lines,
            int index,
            out string[] compactedLines,
            out int consumedLines)
        {
            compactedLines = [];
            consumedLines = 0;

            if (index + 2 >= lines.Length)
            {
                return false;
            }

            var openLine = lines[index].TrimEnd();
            if (!openLine.EndsWith(" then {", StringComparison.Ordinal))
            {
                return false;
            }

            var branchExpr = lines[index + 1].Trim();
            if (!IsSimpleCompactBranch(branchExpr))
            {
                return false;
            }

            var closeLine = lines[index + 2];
            var closeTrimmed = closeLine.Trim();
            var branchHead = openLine[..^2];

            if (closeTrimmed == "} else {")
            {
                var elseIndent = closeLine[..(closeLine.Length - closeLine.TrimStart().Length)];
                var compactedLine = $"{branchHead} {branchExpr}";
                if (compactedLine.Length > _options.MaxLineLength)
                {
                    return false;
                }

                compactedLines = [compactedLine, $"{elseIndent}else {{"];
                consumedLines = 3;
                return true;
            }

            if (!TryParseCompactIfClose(closeTrimmed, out var suffix))
            {
                return false;
            }

            var candidate = $"{branchHead} {branchExpr}{suffix}";
            if (candidate.Length > _options.MaxLineLength)
            {
                return false;
            }

            compactedLines = [candidate];
            consumedLines = 3;
            return true;
        }

        private bool TryCompactSimpleElseBranch(
            string[] lines,
            int index,
            out string[] compactedLines,
            out int consumedLines)
        {
            compactedLines = [];
            consumedLines = 0;

            if (index + 2 >= lines.Length)
            {
                return false;
            }

            var openLine = lines[index];
            var openTrimmed = openLine.Trim();
            var includesCloseBrace = openTrimmed == "} else {";
            if (!includesCloseBrace && openTrimmed != "else {")
            {
                return false;
            }

            var branchExpr = lines[index + 1].Trim();
            if (!IsSimpleCompactBranch(branchExpr))
            {
                return false;
            }

            var closeTrimmed = lines[index + 2].Trim();
            if (!TryParseCompactIfClose(closeTrimmed, out var suffix))
            {
                return false;
            }

            var indent = openLine[..(openLine.Length - openLine.TrimStart().Length)];
            var elseLine = $"{indent}else {branchExpr}{suffix}";
            if (elseLine.Length > _options.MaxLineLength)
            {
                return false;
            }

            compactedLines = includesCloseBrace
                ? [$"{indent}}}", elseLine]
                : [elseLine];
            consumedLines = 3;
            return true;
        }

        private string CollapseEmptyBlocks(string text)
        {
            var current = text;
            while (true)
            {
                var next = CollapseEmptyBlocksOnce(current, out var changed);
                if (!changed)
                {
                    return next;
                }

                current = next;
            }
        }

        private string CollapseEmptyBlocksOnce(string text, out bool changed)
        {
            var lines = text.Split(_newLine);
            var collapsed = new List<string>(lines.Length);
            changed = false;

            for (var i = 0; i < lines.Length; i++)
            {
                if (TryCollapseEmptyBlock(lines, i, out var collapsedLine, out var remainderLine))
                {
                    collapsed.Add(collapsedLine);
                    if (remainderLine is not null)
                    {
                        collapsed.Add(remainderLine);
                    }

                    changed = true;
                    i++;
                    continue;
                }

                collapsed.Add(lines[i]);
            }

            return string.Join(_newLine, collapsed);
        }

        private static bool TryCollapseEmptyBlock(
            string[] lines,
            int index,
            out string collapsedLine,
            out string? remainderLine)
        {
            collapsedLine = "";
            remainderLine = null;

            if (index + 1 >= lines.Length)
            {
                return false;
            }

            var openLine = lines[index].TrimEnd();
            if (!openLine.EndsWith("{", StringComparison.Ordinal))
            {
                return false;
            }

            var closeLine = lines[index + 1];
            var closeTrimmed = closeLine.Trim();
            if (!closeTrimmed.StartsWith("}", StringComparison.Ordinal))
            {
                return false;
            }

            var suffix = closeTrimmed[1..];
            if (suffix.Length == 0 || suffix.All(static ch => ch is ';' or ',' or ')' or ']'))
            {
                collapsedLine = $"{openLine[..^1]}{{}}{suffix}";
                return true;
            }

            if (suffix == " else {")
            {
                var closeIndent = closeLine[..(closeLine.Length - closeLine.TrimStart().Length)];
                collapsedLine = $"{openLine[..^1]}{{}}";
                remainderLine = $"{closeIndent}else {{";
                return true;
            }

            return false;
        }

        private string ExpandMultilineConditionalBlocks(string text)
        {
            var lines = text.Split(_newLine);
            var expanded = new List<string>(lines.Length);

            foreach (var line in lines)
            {
                if (TryExpandConditionalOpenLine(line, out var openHead, out var thenLine, out var openBrace))
                {
                    expanded.Add(openHead);
                    expanded.Add(thenLine);
                    expanded.Add(openBrace);
                    continue;
                }

                if (TryExpandStandaloneOpenLine(line, "then", out thenLine, out openBrace))
                {
                    expanded.Add(thenLine);
                    expanded.Add(openBrace);
                    continue;
                }

                if (TryExpandElseOpenLine(line, out var closeBrace, out var elseLine, out var elseBrace))
                {
                    expanded.Add(closeBrace);
                    expanded.Add(elseLine);
                    expanded.Add(elseBrace);
                    continue;
                }

                if (TryExpandStandaloneOpenLine(line, "else", out elseLine, out elseBrace))
                {
                    expanded.Add(elseLine);
                    expanded.Add(elseBrace);
                    continue;
                }

                if (TryExpandElseOnlyOpenLine(line, out elseLine, out elseBrace))
                {
                    expanded.Add(elseLine);
                    expanded.Add(elseBrace);
                    continue;
                }

                expanded.Add(line);
            }

            return string.Join(_newLine, expanded);
        }

        private static bool TryExpandConditionalOpenLine(
            string line,
            out string head,
            out string thenLine,
            out string brace)
        {
            head = "";
            thenLine = "";
            brace = "";

            const string suffix = " then {";
            var trimmed = line.TrimEnd();
            if (!trimmed.EndsWith(suffix, StringComparison.Ordinal))
            {
                return false;
            }

            var indent = line[..(line.Length - line.TrimStart().Length)];
            head = trimmed[..^suffix.Length];
            thenLine = $"{indent}then";
            brace = $"{indent}{{";
            return true;
        }

        private static bool TryExpandStandaloneOpenLine(
            string line,
            string keyword,
            out string keywordLine,
            out string brace)
        {
            keywordLine = "";
            brace = "";

            var trimmed = line.Trim();
            if (!string.Equals(trimmed, $"{keyword} {{", StringComparison.Ordinal))
            {
                return false;
            }

            var indent = line[..(line.Length - line.TrimStart().Length)];
            keywordLine = $"{indent}{keyword}";
            brace = $"{indent}{{";
            return true;
        }

        private static bool TryExpandElseOpenLine(
            string line,
            out string closeBrace,
            out string elseLine,
            out string elseBrace)
        {
            closeBrace = "";
            elseLine = "";
            elseBrace = "";

            if (line.Trim() != "} else {")
            {
                return false;
            }

            var indent = line[..(line.Length - line.TrimStart().Length)];
            closeBrace = $"{indent}}}";
            elseLine = $"{indent}else";
            elseBrace = $"{indent}{{";
            return true;
        }

        private static bool TryExpandElseOnlyOpenLine(
            string line,
            out string elseLine,
            out string elseBrace)
        {
            elseLine = "";
            elseBrace = "";

            if (line.Trim() != "else {")
            {
                return false;
            }

            var indent = line[..(line.Length - line.TrimStart().Length)];
            elseLine = $"{indent}else";
            elseBrace = $"{indent}{{";
            return true;
        }

        private static bool IsSignedNumber(string text)
        {
            return text.Length > 1 &&
                   text[0] is '+' or '-' &&
                   char.IsDigit(text[1]);
        }

        private static bool ShouldSplitSignedNumber(string previous)
        {
            return IsWordLike(previous) || previous is ")" or "]";
        }

        private static bool IsWordLike(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            var first = text[0];
            return char.IsLetterOrDigit(first) || first == '_' || first == '"' || first == '\'';
        }

        private void WriteIndentIfNeeded()
        {
            if (!_atLineStart)
            {
                return;
            }

            for (var i = 0; i < _indent; i++)
            {
                _builder.Append(_indentUnit);
            }
            _atLineStart = false;
        }

        private void LineBreak()
        {
            TrimTrailingWhitespace();
            if (_builder.Length == 0 || EndsWithNewLine())
            {
                _atLineStart = true;
                return;
            }

            _builder.Append(_newLine);
            _atLineStart = true;
        }

        private void EnsureLineBreak()
        {
            if (!EndsWithNewLine())
            {
                _builder.Append(_newLine);
            }
        }

        private void AppendRaw(string text)
        {
            _builder.Append(text);
            _atLineStart = false;
        }

        private void TrimTrailingWhitespace()
        {
            while (_builder.Length > 0 && _builder[^1] is ' ' or '\t')
            {
                _builder.Length--;
            }
        }

        private bool EndsWithNewLine()
        {
            if (_builder.Length == 0)
            {
                return false;
            }

            return _builder[^1] is '\n' or '\r';
        }

        private string? FindNextText(int startIndex)
        {
            for (var i = startIndex; i < _tokens.Count; i++)
            {
                if (_tokens[i] is EofToken)
                {
                    continue;
                }

                return GetTokenText(_tokens[i]);
            }

            return null;
        }

        private string GetTokenText(Token token)
        {
            if (token is ContentToken && token.Location.Position >= 0 &&
                token.Location.Position + token.Length <= _sourceText.Length &&
                token.Length > 0)
            {
                return _sourceText.Substring(token.Location.Position, token.Length);
            }

            return token switch
            {
                CommentToken comment => comment.Comment,
                ContentToken content => content.Value as string ?? content.TextId.Resolve(),
                ErrorToken error => error.Message,
                _ => token.ToString() ?? ""
            };
        }

        private static string DetectNewLine(string sourceText)
        {
            var index = sourceText.IndexOf('\n');
            if (index > 0 && sourceText[index - 1] == '\r')
            {
                return "\r\n";
            }

            return "\n";
        }

        private static bool IsMatchingContainer(string open, string close)
        {
            return (open, close) switch
            {
                ("(", ")") => true,
                ("[", "]") => true,
                ("{", "}") => true,
                _ => false
            };
        }
    }
}
