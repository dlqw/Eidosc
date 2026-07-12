using Eidosc.Utilities;
using Eidosc.Utils;

namespace Eidosc.Pipeline.TokenRewriting;

internal static class AnonymousLambdaTokenRewriter
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
        var branchContainers = new Stack<BranchContainer>();
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

            if (TryPeekTopLevelBranch(branchContainers, out var branchContainer))
            {
                if (text is WellKnownStrings.Punctuation.FatArrow && branchContainer.AwaitingBranchArrow)
                {
                    branchContainer.AwaitingBranchArrow = false;
                    continue;
                }

                if (text is WellKnownStrings.Punctuation.FatArrow && !branchContainer.AwaitingBranchArrow)
                {
                    // Top-level branch RHS chains belong to function/match branch syntax,
                    // not anonymous lambda desugaring. Leave them for the parser.
                    continue;
                }

                if (text is "," && !branchContainer.AwaitingBranchArrow)
                {
                    branchContainer.AwaitingBranchArrow = true;
                    continue;
                }
            }

            if (text is WellKnownStrings.Punctuation.FatArrow &&
                TryRewriteLambdaAt(tokens, terminals, index, out var rewrittenIndex))
            {
                index = rewrittenIndex;
                continue;
            }

            if (text is "(")
            {
                if (branchContainers.TryPeek(out var container))
                {
                    container.NestedParenDepth++;
                }

                parenDepth++;
                continue;
            }

            if (text is ")")
            {
                parenDepth = Math.Max(0, parenDepth - 1);
                if (branchContainers.TryPeek(out var container))
                {
                    container.NestedParenDepth = Math.Max(0, container.NestedParenDepth - 1);
                }

                continue;
            }

            if (text is "[")
            {
                if (branchContainers.TryPeek(out var container))
                {
                    container.NestedBracketDepth++;
                }

                bracketDepth++;
                continue;
            }

            if (text is "]")
            {
                bracketDepth = Math.Max(0, bracketDepth - 1);
                if (branchContainers.TryPeek(out var container))
                {
                    container.NestedBracketDepth = Math.Max(0, container.NestedBracketDepth - 1);
                }

                continue;
            }

            if (text is "{")
            {
                if (TryConsumePendingContainer(pendingMatchBodies, parenDepth, bracketDepth, braceDepth))
                {
                    branchContainers.Push(new BranchContainer());
                }
                else if (!IsTokenTextAt(tokens, index - 1, WellKnownStrings.Punctuation.RightArrow) &&
                         TryConsumePendingContainer(pendingFunctionBodies, parenDepth, bracketDepth, braceDepth))
                {
                    branchContainers.Push(new BranchContainer());
                }
                else if (branchContainers.TryPeek(out var container))
                {
                    container.NestedBraceDepth++;
                }

                braceDepth++;
                continue;
            }

            if (text is "}")
            {
                braceDepth = Math.Max(0, braceDepth - 1);

                if (branchContainers.TryPeek(out var container))
                {
                    if (container.NestedBraceDepth > 0)
                    {
                        container.NestedBraceDepth--;
                    }
                    else
                    {
                        branchContainers.Pop();
                    }
                }

                pendingFunctionBodies.RemoveAll(pending =>
                    pending.ParenDepth == parenDepth &&
                    pending.BracketDepth == bracketDepth &&
                    pending.BraceDepth > braceDepth);
            }
        }
    }

    private static bool TryRewriteLambdaAt(
        List<Token> tokens,
        TerminalSet terminals,
        int arrowIndex,
        out int rewrittenIndex)
    {
        rewrittenIndex = arrowIndex;

        if (!TryFindLambdaParameters(tokens, arrowIndex, out var parameterStart, out var parameterEnd, out var parenthesized))
        {
            return false;
        }

        var replacement = new List<Token>();
        var anchor = tokens[parameterStart];

        replacement.Add(CreateToken(terminals.Fn, WellKnownStrings.Keywords.Fn, anchor.Location));
        if (parenthesized)
        {
            for (var index = parameterStart; index <= parameterEnd; index++)
            {
                replacement.Add(tokens[index]);
            }
        }
        else
        {
            replacement.Add(CreateToken(terminals.LParen, "(", anchor.Location));
            replacement.Add(tokens[parameterStart]);
            replacement.Add(CreateToken(terminals.RParen, ")", tokens[arrowIndex].Location));
        }

        tokens.RemoveRange(parameterStart, arrowIndex - parameterStart + 1);
        tokens.InsertRange(parameterStart, replacement);

        rewrittenIndex = parameterStart + replacement.Count - 1;
        return true;
    }

    private static bool TryFindLambdaParameters(
        IReadOnlyList<Token> tokens,
        int arrowIndex,
        out int parameterStart,
        out int parameterEnd,
        out bool parenthesized)
    {
        parameterStart = -1;
        parameterEnd = -1;
        parenthesized = false;

        if (arrowIndex <= 0)
        {
            return false;
        }

        var candidateIndex = arrowIndex - 1;
        if (IsSimpleParameter(tokens[candidateIndex]))
        {
            parameterStart = candidateIndex;
            parameterEnd = candidateIndex;
            return true;
        }

        if (!IsTokenText(tokens[candidateIndex], ")") ||
            !TryFindMatchingOpenParen(tokens, candidateIndex, out var openParenIndex) ||
            IsCtorLikeOrCallLikePrefix(tokens, openParenIndex) ||
            !IsSupportedParameterList(tokens, openParenIndex + 1, candidateIndex - 1))
        {
            return false;
        }

        parameterStart = openParenIndex;
        parameterEnd = candidateIndex;
        parenthesized = true;
        return true;
    }

    private static bool IsSupportedParameterList(IReadOnlyList<Token> tokens, int startIndex, int endIndex)
    {
        if (startIndex > endIndex)
        {
            return false;
        }

        var segmentStart = startIndex;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        for (var index = startIndex; index <= endIndex; index++)
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
                    if (!IsSupportedParameterSegment(tokens, segmentStart, index - 1))
                    {
                        return false;
                    }

                    segmentStart = index + 1;
                    break;
            }
        }

        return IsSupportedParameterSegment(tokens, segmentStart, endIndex);
    }

    private static bool IsSupportedParameterSegment(IReadOnlyList<Token> tokens, int startIndex, int endIndex)
    {
        if (startIndex > endIndex)
        {
            return false;
        }

        if (startIndex == endIndex)
        {
            return IsSimpleParameter(tokens[startIndex]);
        }

        return IsTokenText(tokens[startIndex], "(") &&
               IsTokenText(tokens[endIndex], ")") &&
               TryFindMatchingCloseParen(tokens, startIndex, out var closeParenIndex) &&
               closeParenIndex == endIndex &&
               IsSupportedParameterList(tokens, startIndex + 1, endIndex - 1);
    }

    private static bool IsSimpleParameter(Token token)
    {
        return token is ContentToken contentToken &&
               (contentToken.Kind == SyntaxKind.Identifier ||
                contentToken.Kind == SyntaxKind.PtUnderscore);
    }

    private static bool TryFindMatchingOpenParen(IReadOnlyList<Token> tokens, int closeParenIndex, out int openParenIndex)
    {
        openParenIndex = -1;
        var depth = 0;
        for (var index = closeParenIndex; index >= 0; index--)
        {
            if (IsTokenText(tokens[index], ")"))
            {
                depth++;
                continue;
            }

            if (!IsTokenText(tokens[index], "("))
            {
                continue;
            }

            depth--;
            if (depth == 0)
            {
                openParenIndex = index;
                return true;
            }
        }

        return false;
    }

    private static bool IsCtorLikeOrCallLikePrefix(IReadOnlyList<Token> tokens, int openParenIndex)
    {
        var prefixIndex = openParenIndex - 1;
        if (prefixIndex < 0)
        {
            return false;
        }

        return tokens[prefixIndex] is ContentToken contentToken &&
               contentToken.Kind is SyntaxKind.Identifier or SyntaxKind.TypeIdentifier;
    }

    private static bool TryFindMatchingCloseParen(IReadOnlyList<Token> tokens, int openParenIndex, out int closeParenIndex)
    {
        closeParenIndex = -1;
        var depth = 0;
        for (var index = openParenIndex; index < tokens.Count; index++)
        {
            if (IsTokenText(tokens[index], "("))
            {
                depth++;
                continue;
            }

            if (!IsTokenText(tokens[index], ")"))
            {
                continue;
            }

            depth--;
            if (depth == 0)
            {
                closeParenIndex = index;
                return true;
            }
        }

        return false;
    }

    private static bool TryPeekTopLevelBranch(Stack<BranchContainer> branchContainers, out BranchContainer container)
    {
        if (branchContainers.TryPeek(out container!))
        {
            return container.NestedParenDepth == 0 &&
                   container.NestedBracketDepth == 0 &&
                   container.NestedBraceDepth == 0;
        }

        return false;
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

    private sealed class BranchContainer
    {
        public bool AwaitingBranchArrow { get; set; } = true;
        public int NestedParenDepth { get; set; }
        public int NestedBracketDepth { get; set; }
        public int NestedBraceDepth { get; set; }
    }

    private sealed record PendingContainer(int ParenDepth, int BracketDepth, int BraceDepth);

    private sealed record TerminalSet(Terminal Fn, Terminal LParen, Terminal RParen)
    {
        public static bool TryCreate(LexerContext compileContext, out TerminalSet terminals)
        {
            terminals = null!;
            if (FindTerminal(compileContext, WellKnownStrings.Keywords.Fn) is not { } fn ||
                FindTerminal(compileContext, "(") is not { } lParen ||
                FindTerminal(compileContext, ")") is not { } rParen)
            {
                return false;
            }

            terminals = new TerminalSet(fn, lParen, rParen);
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
