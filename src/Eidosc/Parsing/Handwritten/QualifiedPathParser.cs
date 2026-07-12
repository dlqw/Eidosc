namespace Eidosc.Parsing.Handwritten;

internal sealed record ParsedQualifiedPath(
    string? PackageAlias,
    List<string> ModulePath,
    string Name)
{
    public List<string> ToPathParts()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(PackageAlias))
        {
            parts.Add(PackageAlias);
        }

        parts.AddRange(ModulePath);
        if (!string.IsNullOrWhiteSpace(Name))
        {
            parts.Add(Name);
        }

        return parts;
    }
}

internal static class QualifiedPathParser
{
    public static ParsedQualifiedPath ParseItemPath(
        ParserContext ctx,
        Func<Token, bool> isItem,
        string expectedAfterColonMessage)
    {
        var first = ctx.GetText();
        ctx.Advance();

        if (ctx.Check("::") && IsPackageQualifiedItemLookahead(ctx, isItem, separatorOffset: 0))
        {
            ctx.Advance();
            return ParsePackageQualifiedItemPath(ctx, first, isItem, expectedAfterColonMessage);
        }

        var parts = new List<string> { first };
        ConsumeModulePathSegments(ctx, parts, TokenKind.IsAnyIdentifier);

        while (ctx.Match("::"))
        {
            if (TokenKind.IsAnyIdentifier(ctx.Current))
            {
                parts.Add(ctx.GetText());
                ctx.Advance();
            }
            else
            {
                ctx.Error(expectedAfterColonMessage);
                break;
            }
        }

        return parts.Count == 0
            ? new ParsedQualifiedPath(null, [], string.Empty)
            : new ParsedQualifiedPath(null, parts.Take(parts.Count - 1).ToList(), parts[^1]);
    }

    public static List<string> Parse(
        ParserContext ctx,
        Func<Token, bool> isSegment,
        string expectedAfterColonMessage)
    {
        var parts = new List<string> { ctx.GetText() };
        ctx.Advance();

        ConsumeModulePathSegments(ctx, parts, isSegment);

        while (ctx.Match("::"))
        {
            if (isSegment(ctx.Current))
            {
                parts.Add(ctx.GetText());
                ctx.Advance();
            }
            else
            {
                ctx.Error(expectedAfterColonMessage);
                break;
            }
        }

        return parts;
    }

    public static bool IsQualifiedPathLookahead(ParserContext ctx)
    {
        return ctx.CheckPeek(1, "::") || IsModulePathLookahead(ctx);
    }

    public static bool IsPackageQualifiedItemLookahead(ParserContext ctx)
    {
        return ctx.CheckPeek(1, "::") &&
               IsPackageQualifiedItemLookahead(ctx, TokenKind.IsAnyIdentifier, separatorOffset: 1);
    }

    public static bool IsPackageQualifiedItemLookahead(ParserContext ctx, Func<Token, bool> isItem)
    {
        return ctx.CheckPeek(1, "::") &&
               IsPackageQualifiedItemLookahead(ctx, isItem, separatorOffset: 1);
    }

    private static ParsedQualifiedPath ParsePackageQualifiedItemPath(
        ParserContext ctx,
        string packageAlias,
        Func<Token, bool> isItem,
        string expectedAfterColonMessage)
    {
        var modulePath = new List<string>();
        if (!TokenKind.IsAnyIdentifier(ctx.Current))
        {
            ctx.Error(expectedAfterColonMessage);
            return new ParsedQualifiedPath(packageAlias, modulePath, string.Empty);
        }

        modulePath.Add(ctx.GetText());
        ctx.Advance();
        while (ctx.Match(".") || ctx.Match("/"))
        {
            if (TokenKind.IsAnyIdentifier(ctx.Current))
            {
                modulePath.Add(ctx.GetText());
                ctx.Advance();
            }
            else
            {
                ctx.Error(expectedAfterColonMessage);
                break;
            }
        }

        if (!ctx.Match("::"))
        {
            ctx.Error(expectedAfterColonMessage);
            return new ParsedQualifiedPath(packageAlias, modulePath, string.Empty);
        }

        if (!isItem(ctx.Current))
        {
            ctx.Error(expectedAfterColonMessage);
            return new ParsedQualifiedPath(packageAlias, modulePath, string.Empty);
        }

        var name = ctx.GetText();
        ctx.Advance();
        return new ParsedQualifiedPath(packageAlias, modulePath, name);
    }

    private static bool IsPackageQualifiedItemLookahead(
        ParserContext ctx,
        Func<Token, bool> isItem,
        int separatorOffset)
    {
        var offset = separatorOffset;
        if (!ctx.CheckPeek(offset, "::") || !TokenKind.IsAnyIdentifier(ctx.Peek(offset + 1)))
        {
            return false;
        }

        offset += 2;
        while ((ctx.CheckPeek(offset, ".") || ctx.CheckPeek(offset, "/")) && TokenKind.IsAnyIdentifier(ctx.Peek(offset + 1)))
        {
            offset += 2;
        }

        return ctx.CheckPeek(offset, "::") && isItem(ctx.Peek(offset + 1));
    }

    private static void ConsumeModulePathSegments(
        ParserContext ctx,
        List<string> parts,
        Func<Token, bool> isSegment)
    {
        while ((ctx.Check(".") || ctx.Check("/")) &&
               isSegment(ctx.Peek(1)) &&
               IsModulePathContinuationLookahead(ctx))
        {
            ctx.Advance();
            parts.Add(ctx.GetText());
            ctx.Advance();
        }
    }

    private static bool IsModulePathLookahead(ParserContext ctx)
    {
        var offset = 1;
        if (!(ctx.CheckPeek(offset, ".") || ctx.CheckPeek(offset, "/")) || !TokenKind.IsAnyIdentifier(ctx.Peek(offset + 1)))
        {
            return false;
        }

        offset += 2;
        while ((ctx.CheckPeek(offset, ".") || ctx.CheckPeek(offset, "/")) && TokenKind.IsAnyIdentifier(ctx.Peek(offset + 1)))
        {
            offset += 2;
        }

        return ctx.CheckPeek(offset, "::");
    }

    private static bool IsModulePathContinuationLookahead(ParserContext ctx)
    {
        var offset = 0;
        if (!(ctx.CheckPeek(offset, ".") || ctx.CheckPeek(offset, "/")) || !TokenKind.IsAnyIdentifier(ctx.Peek(offset + 1)))
        {
            return false;
        }

        offset += 2;
        while ((ctx.CheckPeek(offset, ".") || ctx.CheckPeek(offset, "/")) && TokenKind.IsAnyIdentifier(ctx.Peek(offset + 1)))
        {
            offset += 2;
        }

        return ctx.CheckPeek(offset, "::");
    }

}
