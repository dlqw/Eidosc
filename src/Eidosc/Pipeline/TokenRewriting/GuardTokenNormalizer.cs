using Eidosc.Utilities;

namespace Eidosc.Pipeline.TokenRewriting;

internal static class GuardTokenNormalizer
{
    public static void Normalize(
        List<Token>? tokens,
        LexerContext? compileContext,
        bool rewriteAnonymousLambdas = true)
    {
        if (tokens == null || tokens.Count == 0)
        {
            return;
        }

        NormalizePatternGuardWildcards(tokens, compileContext);
        NormalizeParenthesizedGuardExpr(tokens);
        if (rewriteAnonymousLambdas)
        {
            AnonymousLambdaTokenRewriter.Rewrite(tokens, compileContext);
        }
        CurriedPatternBranchTokenRewriter.Rewrite(tokens, compileContext);
    }

    private static void NormalizePatternGuardWildcards(List<Token> tokens, LexerContext? compileContext)
    {
        if (compileContext == null || FindIdentifierTerminal(compileContext) is not { } identifierTerminal)
        {
            return;
        }

        for (var index = 0; index < tokens.Count; index++)
        {
            if (!IsTokenText(tokens[index], WellKnownStrings.Keywords.When) ||
                !TryFindPatternGuardArrow(tokens, index + 1, out var arrowIndex))
            {
                continue;
            }

            for (var guardIndex = index + 1; guardIndex < arrowIndex; guardIndex++)
            {
                if (tokens[guardIndex] is not ContentToken wildcard ||
                    !IsStandaloneWildcard(wildcard))
                {
                    continue;
                }

                tokens[guardIndex] = new ContentToken(
                    wildcard.Location,
                    SyntaxKind.PtUnderscore,
                    identifierTerminal,
                    "_".GetOrIntern(),
                    wildcard.Length,
                    "_");
            }

            index = arrowIndex;
        }
    }

    private static void NormalizeParenthesizedGuardExpr(List<Token> tokens)
    {
        for (var index = 0; index < tokens.Count; index++)
        {
            if (!IsTokenText(tokens[index], WellKnownStrings.Keywords.When) ||
                index + 1 >= tokens.Count ||
                !IsTokenText(tokens[index + 1], "(") ||
                !TryFindMatchingParen(tokens, index + 1, out var closingParenIndex) ||
                closingParenIndex <= index + 2)
            {
                continue;
            }

            var nextIndex = closingParenIndex + 1;
            if (nextIndex < tokens.Count && IsTokenText(tokens[nextIndex], WellKnownStrings.Punctuation.LeftArrow))
            {
                continue;
            }

            if (ContainsTopLevelComma(tokens, index + 2, closingParenIndex - 1))
            {
                continue;
            }

            tokens.RemoveAt(closingParenIndex);
            tokens.RemoveAt(index + 1);
        }
    }

    private static Terminal? FindIdentifierTerminal(LexerContext compileContext)
    {
        foreach (var terminal in compileContext.Terminals)
        {
            if (string.Equals(terminal.DebugName, WellKnownStrings.Terminals.Identifier, StringComparison.Ordinal))
            {
                return terminal;
            }
        }

        return null;
    }

    private static bool TryFindPatternGuardArrow(IReadOnlyList<Token> tokens, int startIndex, out int arrowIndex)
    {
        arrowIndex = -1;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        for (var index = startIndex; index < tokens.Count; index++)
        {
            switch (GetTokenText(tokens[index]))
            {
                case "(":
                    parenDepth++;
                    continue;
                case ")":
                    parenDepth = Math.Max(0, parenDepth - 1);
                    continue;
                case "[":
                    bracketDepth++;
                    continue;
                case "]":
                    bracketDepth = Math.Max(0, bracketDepth - 1);
                    continue;
                case "{":
                    braceDepth++;
                    continue;
                case "}":
                    braceDepth = Math.Max(0, braceDepth - 1);
                    continue;
            }

            if (parenDepth != 0 || bracketDepth != 0 || braceDepth != 0)
            {
                continue;
            }

            if (IsTokenText(tokens[index], WellKnownStrings.Punctuation.LeftArrow))
            {
                arrowIndex = index;
                return true;
            }

            if (IsTokenText(tokens[index], WellKnownStrings.Punctuation.FatArrow))
            {
                return false;
            }
        }

        return false;
    }

    private static bool TryFindMatchingParen(IReadOnlyList<Token> tokens, int openParenIndex, out int closingParenIndex)
    {
        closingParenIndex = -1;
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
                closingParenIndex = index;
                return true;
            }
        }

        return false;
    }

    private static bool ContainsTopLevelComma(IReadOnlyList<Token> tokens, int startIndex, int endIndex)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        for (var index = startIndex; index <= endIndex && index < tokens.Count; index++)
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
                    return true;
            }
        }

        return false;
    }

    private static bool IsStandaloneWildcard(ContentToken token)
    {
        return token.Kind == SyntaxKind.PtUnderscore;
    }

    private static bool IsTokenText(Token token, string expected)
    {
        return string.Equals(GetTokenText(token), expected, StringComparison.Ordinal);
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
}
