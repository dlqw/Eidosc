using Eidosc.Diagnostic;

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
        string expectedAfterSeparatorMessage)
    {
        return ctx.UsesDotNamespaces
            ? ParseDotItemPath(ctx, isItem, expectedAfterSeparatorMessage)
            : ParseLegacyItemPath(ctx, isItem, expectedAfterSeparatorMessage);
    }

    public static List<string> Parse(
        ParserContext ctx,
        Func<Token, bool> isSegment,
        string expectedAfterSeparatorMessage)
    {
        if (!ctx.UsesDotNamespaces)
        {
            return ParseLegacy(ctx, isSegment, expectedAfterSeparatorMessage);
        }

        var parts = new List<string> { ctx.GetText() };
        ctx.Advance();
        ConsumeDotSegments(ctx, parts, isSegment, expectedAfterSeparatorMessage);
        RecoverRemovedQualifiedSeparators(ctx, parts, isSegment, expectedAfterSeparatorMessage);
        return parts;
    }

    public static bool IsQualifiedPathLookahead(ParserContext ctx)
    {
        if (ctx.UsesDotNamespaces)
        {
            if (!ctx.CheckPeek(1, ".") || !TokenKind.IsAnyIdentifier(ctx.Peek(2)))
            {
                return false;
            }

            return ctx.IsKnownNamespaceRoot(ctx.Current);
        }

        return ctx.CheckPeek(1, "::") || IsLegacyModulePathLookahead(ctx);
    }

    public static bool IsPackageQualifiedItemLookahead(ParserContext ctx)
        => IsPackageQualifiedItemLookahead(ctx, TokenKind.IsAnyIdentifier);

    public static bool IsPackageQualifiedItemLookahead(ParserContext ctx, Func<Token, bool> isItem)
    {
        if (ctx.UsesDotNamespaces)
        {
            return TokenKind.IsAnyIdentifier(ctx.Current) &&
                   IsDotItemPathLookahead(ctx, isItem);
        }

        return ctx.CheckPeek(1, "::") &&
               IsLegacyPackageQualifiedItemLookahead(ctx, isItem, separatorOffset: 1);
    }

    private static ParsedQualifiedPath ParseDotItemPath(
        ParserContext ctx,
        Func<Token, bool> isItem,
        string expectedAfterSeparatorMessage)
    {
        var parts = new List<string> { ctx.GetText() };
        ctx.Advance();
        ConsumeDotSegments(ctx, parts, TokenKind.IsAnyIdentifier, expectedAfterSeparatorMessage);
        RecoverRemovedQualifiedSeparators(ctx, parts, TokenKind.IsAnyIdentifier, expectedAfterSeparatorMessage);

        return parts.Count == 0
            ? new ParsedQualifiedPath(null, [], string.Empty)
            : new ParsedQualifiedPath(null, parts.Take(parts.Count - 1).ToList(), parts[^1]);
    }

    private static void ConsumeDotSegments(
        ParserContext ctx,
        List<string> parts,
        Func<Token, bool> isSegment,
        string expectedAfterSeparatorMessage)
    {
        while (ctx.Check("."))
        {
            if (!isSegment(ctx.Peek(1)))
            {
                break;
            }

            ctx.Advance();
            parts.Add(ctx.GetText());
            ctx.Advance();
        }

        if (ctx.Check(".") && !isSegment(ctx.Peek(1)) && !ctx.CheckPeek(1, "{") && !ctx.CheckPeek(1, "*"))
        {
            ctx.Error(expectedAfterSeparatorMessage);
        }
    }

    private static void RecoverRemovedQualifiedSeparators(
        ParserContext ctx,
        List<string> parts,
        Func<Token, bool> isSegment,
        string expectedAfterSeparatorMessage)
    {
        while (ctx.Check("::"))
        {
            ctx.Error(DiagnosticMessages.ParserQualifiedDoubleColonRemoved);
            ctx.Advance();
            if (!isSegment(ctx.Current))
            {
                ctx.Error(expectedAfterSeparatorMessage);
                return;
            }

            parts.Add(ctx.GetText());
            ctx.Advance();
            ConsumeDotSegments(ctx, parts, isSegment, expectedAfterSeparatorMessage);
        }
    }

    private static bool IsDotItemPathLookahead(ParserContext ctx, Func<Token, bool> isItem)
    {
        var offset = 1;
        var sawDot = false;
        while (ctx.CheckPeek(offset, ".") && TokenKind.IsAnyIdentifier(ctx.Peek(offset + 1)))
        {
            sawDot = true;
            offset += 2;
        }

        return sawDot && isItem(ctx.Peek(offset - 1));
    }

    private static ParsedQualifiedPath ParseLegacyItemPath(
        ParserContext ctx,
        Func<Token, bool> isItem,
        string expectedAfterSeparatorMessage)
    {
        var first = ctx.GetText();
        ctx.Advance();

        if (ctx.Check("::") && IsLegacyPackageQualifiedItemLookahead(ctx, isItem, separatorOffset: 0))
        {
            ctx.Advance();
            return ParseLegacyPackageQualifiedItemPath(ctx, first, isItem, expectedAfterSeparatorMessage);
        }

        var parts = new List<string> { first };
        ConsumeLegacyModulePathSegments(ctx, parts, TokenKind.IsAnyIdentifier);

        while (ctx.Match("::"))
        {
            if (TokenKind.IsAnyIdentifier(ctx.Current))
            {
                parts.Add(ctx.GetText());
                ctx.Advance();
            }
            else
            {
                ctx.Error(expectedAfterSeparatorMessage);
                break;
            }
        }

        return parts.Count == 0
            ? new ParsedQualifiedPath(null, [], string.Empty)
            : new ParsedQualifiedPath(null, parts.Take(parts.Count - 1).ToList(), parts[^1]);
    }

    private static List<string> ParseLegacy(
        ParserContext ctx,
        Func<Token, bool> isSegment,
        string expectedAfterSeparatorMessage)
    {
        var parts = new List<string> { ctx.GetText() };
        ctx.Advance();

        ConsumeLegacyModulePathSegments(ctx, parts, isSegment);
        while (ctx.Match("::"))
        {
            if (isSegment(ctx.Current))
            {
                parts.Add(ctx.GetText());
                ctx.Advance();
            }
            else
            {
                ctx.Error(expectedAfterSeparatorMessage);
                break;
            }
        }

        return parts;
    }

    private static ParsedQualifiedPath ParseLegacyPackageQualifiedItemPath(
        ParserContext ctx,
        string packageAlias,
        Func<Token, bool> isItem,
        string expectedAfterSeparatorMessage)
    {
        var modulePath = new List<string>();
        if (!TokenKind.IsAnyIdentifier(ctx.Current))
        {
            ctx.Error(expectedAfterSeparatorMessage);
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
                ctx.Error(expectedAfterSeparatorMessage);
                break;
            }
        }

        if (!ctx.Match("::"))
        {
            ctx.Error(expectedAfterSeparatorMessage);
            return new ParsedQualifiedPath(packageAlias, modulePath, string.Empty);
        }

        if (!isItem(ctx.Current))
        {
            ctx.Error(expectedAfterSeparatorMessage);
            return new ParsedQualifiedPath(packageAlias, modulePath, string.Empty);
        }

        var name = ctx.GetText();
        ctx.Advance();
        return new ParsedQualifiedPath(packageAlias, modulePath, name);
    }

    private static bool IsLegacyPackageQualifiedItemLookahead(
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

    private static void ConsumeLegacyModulePathSegments(
        ParserContext ctx,
        List<string> parts,
        Func<Token, bool> isSegment)
    {
        while ((ctx.Check(".") || ctx.Check("/")) &&
               isSegment(ctx.Peek(1)) &&
               IsLegacyModulePathContinuationLookahead(ctx))
        {
            ctx.Advance();
            parts.Add(ctx.GetText());
            ctx.Advance();
        }
    }

    private static bool IsLegacyModulePathLookahead(ParserContext ctx)
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

    private static bool IsLegacyModulePathContinuationLookahead(ParserContext ctx)
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
