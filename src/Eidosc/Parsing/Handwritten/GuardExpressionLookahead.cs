namespace Eidosc.Parsing.Handwritten;

internal static class GuardExpressionLookahead
{
    public static bool HasTopLevelPatternGuardSource(ParserContext ctx)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        for (var offset = 0; ctx.Peek(offset) is not EofToken; offset++)
        {
            var text = ctx.GetText(ctx.Peek(offset));
            switch (text)
            {
                case "(":
                    parenDepth++;
                    continue;
                case ")":
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                    {
                        return false;
                    }

                    parenDepth = Math.Max(0, parenDepth - 1);
                    continue;
                case "[":
                    bracketDepth++;
                    continue;
                case "]":
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                    {
                        return false;
                    }

                    bracketDepth = Math.Max(0, bracketDepth - 1);
                    continue;
                case "{":
                    braceDepth++;
                    continue;
                case "}":
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                    {
                        return false;
                    }

                    braceDepth = Math.Max(0, braceDepth - 1);
                    continue;
            }

            if (parenDepth != 0 || bracketDepth != 0 || braceDepth != 0)
            {
                continue;
            }

            if (text == "<-")
            {
                return true;
            }

            if (text is "=>" or "when" or ",")
            {
                return false;
            }
        }

        return false;
    }
}
