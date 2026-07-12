namespace Eidosc.Parsing.Handwritten;

internal static class InternalNameParser
{
    public static bool TryParseLeadingDoubleUnderscoreName(ParserContext ctx, out string name)
    {
        name = string.Empty;
        if (!ctx.Check("_") ||
            !ctx.CheckPeek(1, "_") ||
            !TokenKind.IsAnyIdentifier(ctx.Peek(2)))
        {
            return false;
        }

        name = "__" + ctx.GetText(ctx.Peek(2));
        ctx.Advance();
        ctx.Advance();
        ctx.Advance();
        return true;
    }
}
