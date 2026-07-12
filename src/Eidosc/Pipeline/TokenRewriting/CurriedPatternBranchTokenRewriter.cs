using Eidosc.Utilities;
using Eidosc.Utils;

namespace Eidosc.Pipeline.TokenRewriting;

internal static class CurriedPatternBranchTokenRewriter
{
    public static void Rewrite(List<Token> tokens, LexerContext? compileContext)
    {
        if (compileContext == null || tokens.Count == 0)
        {
            return;
        }

        if (!TerminalSet.TryCreate(compileContext, out var terminals))
        {
            return;
        }

        var pendingMatchBodies = new List<PendingContainer>();
        var pendingFunctionBodies = new List<PendingContainer>();
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        for (var index = 0; index < tokens.Count; index++)
        {
            var text = GetTokenText(tokens[index]);

            if (text is WellKnownStrings.Keywords.Match)
            {
                pendingMatchBodies.Add(new PendingContainer(parenDepth, bracketDepth, braceDepth));
            }
            else if (text is WellKnownStrings.Keywords.Func)
            {
                pendingFunctionBodies.Add(new PendingContainer(parenDepth, bracketDepth, braceDepth));
            }

            switch (text)
            {
                case "(":
                    parenDepth++;
                    break;
                case ")":
                    parenDepth = Math.Max(0, parenDepth - 1);
                    break;
                case "[":
                    bracketDepth++;
                    break;
                case "]":
                    bracketDepth = Math.Max(0, bracketDepth - 1);
                    break;
                case "{":
                    if (TryConsumePendingContainer(pendingMatchBodies, parenDepth, bracketDepth, braceDepth) ||
                        (!IsTokenTextAt(tokens, index - 1, WellKnownStrings.Punctuation.RightArrow) &&
                         TryConsumePendingContainer(pendingFunctionBodies, parenDepth, bracketDepth, braceDepth)))
                    {
                        RewriteContainer(tokens, terminals, index);
                    }

                    braceDepth++;
                    break;
                case "}":
                    braceDepth = Math.Max(0, braceDepth - 1);
                    pendingFunctionBodies.RemoveAll(pending =>
                        pending.ParenDepth == parenDepth &&
                        pending.BracketDepth == bracketDepth &&
                        pending.BraceDepth > braceDepth);
                    break;
            }
        }
    }

    private static void RewriteContainer(List<Token> tokens, TerminalSet terminals, int openBraceIndex)
    {
        if (!TryFindMatchingCloseBrace(tokens, openBraceIndex, out var closeBraceIndex) ||
            closeBraceIndex <= openBraceIndex + 1)
        {
            return;
        }

        var branchRanges = CollectTopLevelBranchRanges(tokens, openBraceIndex + 1, closeBraceIndex);
        for (var index = branchRanges.Count - 1; index >= 0; index--)
        {
            var range = branchRanges[index];
            RewriteBranch(tokens, terminals, range.Start, range.EndExclusive);
        }
    }

    private static List<TokenRange> CollectTopLevelBranchRanges(
        IReadOnlyList<Token> tokens,
        int startIndex,
        int endExclusive)
    {
        var ranges = new List<TokenRange>();
        var branchStart = startIndex;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        for (var index = startIndex; index < endExclusive; index++)
        {
            switch (GetTokenText(tokens[index]))
            {
                case "(":
                    parenDepth++;
                    break;
                case ")":
                    parenDepth = Math.Max(0, parenDepth - 1);
                    break;
                case "[":
                    bracketDepth++;
                    break;
                case "]":
                    bracketDepth = Math.Max(0, bracketDepth - 1);
                    break;
                case "{":
                    braceDepth++;
                    break;
                case "}":
                    braceDepth = Math.Max(0, braceDepth - 1);
                    break;
                case "," when parenDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                    ranges.Add(new TokenRange(branchStart, index));
                    branchStart = index + 1;
                    break;
            }
        }

        if (branchStart < endExclusive)
        {
            ranges.Add(new TokenRange(branchStart, endExclusive));
        }

        return ranges;
    }

    private static void RewriteBranch(List<Token> tokens, TerminalSet terminals, int startIndex, int endExclusive)
    {
        var arrows = CollectTopLevelArrows(tokens, startIndex, endExclusive);
        if (arrows.Count <= 1)
        {
            return;
        }

        var segments = new List<TokenRange>(arrows.Count);
        var guards = new List<TokenRange>();
        var segmentStart = startIndex;

        foreach (var arrowIndex in arrows)
        {
            if (!TrySplitPatternSegment(tokens, segmentStart, arrowIndex, out var patternRange, out var guardRange))
            {
                return;
            }

            segments.Add(patternRange);
            if (guardRange is { } guard)
            {
                guards.Add(guard);
            }

            segmentStart = arrowIndex + 1;
        }

        if (segmentStart >= endExclusive)
        {
            return;
        }

        var finalExpression = new TokenRange(segmentStart, endExclusive);
        var replacement = new List<Token>();
        var firstPatternToken = tokens[segments[0].Start];
        var lastArrowToken = tokens[arrows[^1]];

        replacement.Add(CreateToken(terminals.LParen, "(", firstPatternToken.Location));
        for (var index = 0; index < segments.Count; index++)
        {
            AppendRange(tokens, replacement, segments[index]);
            if (index + 1 < segments.Count)
            {
                replacement.Add(CreateToken(terminals.Comma, ",", tokens[segments[index].EndExclusive - 1].Location));
            }
        }

        replacement.Add(CreateToken(terminals.RParen, ")", lastArrowToken.Location));

        foreach (var guard in guards)
        {
            AppendRange(tokens, replacement, guard);
        }

        replacement.Add(tokens[arrows[^1]]);
        AppendRange(tokens, replacement, finalExpression);

        tokens.RemoveRange(startIndex, endExclusive - startIndex);
        tokens.InsertRange(startIndex, replacement);
    }

    private static List<int> CollectTopLevelArrows(IReadOnlyList<Token> tokens, int startIndex, int endExclusive)
    {
        var arrows = new List<int>();
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        for (var index = startIndex; index < endExclusive; index++)
        {
            switch (GetTokenText(tokens[index]))
            {
                case "(":
                    parenDepth++;
                    break;
                case ")":
                    parenDepth = Math.Max(0, parenDepth - 1);
                    break;
                case "[":
                    bracketDepth++;
                    break;
                case "]":
                    bracketDepth = Math.Max(0, bracketDepth - 1);
                    break;
                case "{":
                    braceDepth++;
                    break;
                case "}":
                    braceDepth = Math.Max(0, braceDepth - 1);
                    break;
                case WellKnownStrings.Punctuation.FatArrow when parenDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                    arrows.Add(index);
                    break;
            }
        }

        return arrows;
    }

    private static bool TrySplitPatternSegment(
        IReadOnlyList<Token> tokens,
        int startIndex,
        int endExclusive,
        out TokenRange patternRange,
        out TokenRange? guardRange)
    {
        patternRange = default;
        guardRange = null;

        if (startIndex >= endExclusive)
        {
            return false;
        }

        var whenIndex = FindTopLevelWhen(tokens, startIndex, endExclusive);
        if (whenIndex < 0)
        {
            patternRange = new TokenRange(startIndex, endExclusive);
            return true;
        }

        if (whenIndex == startIndex)
        {
            return false;
        }

        patternRange = new TokenRange(startIndex, whenIndex);
        guardRange = new TokenRange(whenIndex, endExclusive);
        return true;
    }

    private static int FindTopLevelWhen(IReadOnlyList<Token> tokens, int startIndex, int endExclusive)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        for (var index = startIndex; index < endExclusive; index++)
        {
            switch (GetTokenText(tokens[index]))
            {
                case "(":
                    parenDepth++;
                    break;
                case ")":
                    parenDepth = Math.Max(0, parenDepth - 1);
                    break;
                case "[":
                    bracketDepth++;
                    break;
                case "]":
                    bracketDepth = Math.Max(0, bracketDepth - 1);
                    break;
                case "{":
                    braceDepth++;
                    break;
                case "}":
                    braceDepth = Math.Max(0, braceDepth - 1);
                    break;
                case WellKnownStrings.Keywords.When when parenDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                    return index;
            }
        }

        return -1;
    }

    private static bool TryFindMatchingCloseBrace(IReadOnlyList<Token> tokens, int openBraceIndex, out int closeBraceIndex)
    {
        closeBraceIndex = -1;
        var depth = 0;
        for (var index = openBraceIndex; index < tokens.Count; index++)
        {
            if (IsTokenText(tokens[index], "{"))
            {
                depth++;
                continue;
            }

            if (!IsTokenText(tokens[index], "}"))
            {
                continue;
            }

            depth--;
            if (depth == 0)
            {
                closeBraceIndex = index;
                return true;
            }
        }

        return false;
    }

    private static void AppendRange(IReadOnlyList<Token> tokens, List<Token> target, TokenRange range)
    {
        for (var index = range.Start; index < range.EndExclusive; index++)
        {
            target.Add(tokens[index]);
        }
    }

    private static bool TryConsumePendingContainer(
        List<PendingContainer> pendingContainers,
        int parenDepth,
        int bracketDepth,
        int braceDepth)
    {
        for (var index = pendingContainers.Count - 1; index >= 0; index--)
        {
            var pending = pendingContainers[index];
            if (pending.ParenDepth != parenDepth ||
                pending.BracketDepth != bracketDepth ||
                pending.BraceDepth != braceDepth)
            {
                continue;
            }

            pendingContainers.RemoveAt(index);
            return true;
        }

        return false;
    }

    private static ContentToken CreateToken(Terminal terminal, string text, SourceLocation location)
    {
        var kind = SyntaxKindHelper.TryFromText(text, out var k) ? k : SyntaxKind.None;
        return new ContentToken(location, kind, terminal, text.GetOrIntern(), text.Length, text);
    }

    private static bool IsTokenText(Token token, string expected)
    {
        return string.Equals(GetTokenText(token), expected, StringComparison.Ordinal);
    }

    private static bool IsTokenTextAt(IReadOnlyList<Token> tokens, int index, string expected)
    {
        return index >= 0 &&
               index < tokens.Count &&
               IsTokenText(tokens[index], expected);
    }

    private static string GetTokenText(Token token)
    {
        return token switch
        {
            ContentToken contentToken => contentToken.TextId.Resolve(),
            EofToken => "<eof>",
            ErrorToken errorToken => errorToken.Message,
            _ => token.ToString() ?? string.Empty
        };
    }

    private readonly record struct TokenRange(int Start, int EndExclusive);

    private sealed record PendingContainer(int ParenDepth, int BracketDepth, int BraceDepth);

    private sealed record TerminalSet(Terminal LParen, Terminal RParen, Terminal Comma)
    {
        public static bool TryCreate(LexerContext compileContext, out TerminalSet terminals)
        {
            terminals = null!;
            if (FindTerminal(compileContext, "(") is not { } lParen ||
                FindTerminal(compileContext, ")") is not { } rParen ||
                FindTerminal(compileContext, ",") is not { } comma)
            {
                return false;
            }

            terminals = new TerminalSet(lParen, rParen, comma);
            return true;
        }

        private static Terminal? FindTerminal(LexerContext compileContext, string debugName)
        {
            foreach (var terminal in compileContext.Terminals)
            {
                if (string.Equals(terminal.DebugName, debugName, StringComparison.Ordinal))
                {
                    return terminal;
                }
            }

            return null;
        }
    }
}
