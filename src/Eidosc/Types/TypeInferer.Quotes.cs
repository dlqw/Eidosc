using Eidosc.Ast.Expressions;
using Eidosc.Symbols;
using Eidosc.Syntax;
using Eidosc.Utils;

namespace Eidosc.Types;

public sealed partial class TypeInferer
{
    private Type InferQuote(QuoteExpr quote, Type? expectedType = null)
    {
        if (!_allowComptimeFunctionReferences)
        {
            AddError(quote.Span, "quote expressions are compile-time values and may only be evaluated in comptime code");
        }

        var kind = quote.Kind;
        if (kind == null && expectedType != null && TryInferQuoteKind(expectedType, out var inferredKind))
        {
            kind = inferredKind;
            quote.SetKind(inferredKind);
        }

        if (kind == null)
        {
            AddError(quote.Span, "quote kind cannot be inferred uniquely; specify item, member, stmt, expr, pattern, type, or tokens");
            foreach (var splice in quote.Parts.OfType<QuoteSplicePart>())
            {
                InferQuoteSplice(splice, null);
            }

            return CreateErrorRecoveryType();
        }

        var entry = SyntaxSchema.Get(kind.Value);
        foreach (var splice in quote.Parts.OfType<QuoteSplicePart>())
        {
            InferQuoteSplice(splice, entry);
        }

        return CreateQuoteResultType(entry);
    }

    private void InferQuoteSplice(QuoteSplicePart splice, SyntaxSchemaEntry? target)
    {
        if (splice.Value == null)
        {
            AddError(splice.Span, "quote splice is missing its comptime expression");
            return;
        }

        var spliceType = _substitution.Apply(SafeInferExpression(splice.Value));
        if (splice.IsMany)
        {
            if (!TryGetSequenceElement(spliceType, out var elementType) ||
                elementType is not TyCon elementSyntax ||
                !IsMetaType(elementSyntax, WellKnownTypeIds.MetaSyntaxId, WellKnownStrings.Meta.Types.Syntax))
            {
                AddError(splice.Span, "..$() requires a sequence of typed meta.Syntax values");
            }

            return;
        }

        if (TryGetSequenceElement(spliceType, out _))
        {
            AddError(splice.Span, "$() accepts one value; use ..$() to splice a syntax sequence");
            return;
        }

        if (spliceType is TyFun)
        {
            AddError(splice.Span, "function values cannot be reified into quote syntax");
            return;
        }

        if (target != null &&
            spliceType is TyCon { Args.Count: 1 } syntax &&
            IsMetaType(syntax, WellKnownTypeIds.MetaSyntaxId, WellKnownStrings.Meta.Types.Syntax) &&
            TryGetMarkerCategory(syntax.Args[0], out var sourceCategory) &&
            !SyntaxSchema.CanEmbed(sourceCategory, target.Category))
        {
            AddError(
                splice.Span,
                $"meta.Syntax[{sourceCategory}] cannot be embedded in quote {target.SourceName}");
        }
    }

    private Type CreateQuoteResultType(SyntaxSchemaEntry entry)
    {
        if (entry.Cardinality == SyntaxCardinality.Tokens)
        {
            return MetaSchemaRegistry.MetaType(WellKnownStrings.Meta.Types.Tokens, WellKnownTypeIds.MetaTokensId);
        }

        var marker = CreateMarkerType(entry.QuoteKind);
        var syntax = MetaSchemaRegistry.MetaType(WellKnownStrings.Meta.Types.Syntax, WellKnownTypeIds.MetaSyntaxId) with
        {
            Args = [marker]
        };
        if (entry.Cardinality == SyntaxCardinality.Singular)
        {
            return syntax;
        }

        var sequenceSymbol = _symbolTable.LookupType(WellKnownStrings.BuiltinTypes.Seq) ?? SymbolId.None;
        return new TyCon
        {
            Name = WellKnownStrings.BuiltinTypes.Seq,
            Symbol = sequenceSymbol,
            Id = _symbolTable.GetSymbol(sequenceSymbol)?.TypeId ?? TypeId.None,
            Args = [syntax]
        };
    }

    private static Type CreateMarkerType(QuoteKind kind) => kind switch
    {
        QuoteKind.Item or QuoteKind.Items =>
            MetaSchemaRegistry.MetaType(WellKnownStrings.Meta.Types.Item, WellKnownTypeIds.MetaItemId),
        QuoteKind.Member or QuoteKind.Members =>
            MetaSchemaRegistry.MetaType(WellKnownStrings.Meta.Types.Member, WellKnownTypeIds.MetaMemberId),
        QuoteKind.Statement =>
            MetaSchemaRegistry.MetaType(WellKnownStrings.Meta.Types.Stmt, WellKnownTypeIds.MetaStmtId),
        QuoteKind.Expression =>
            MetaSchemaRegistry.MetaType(WellKnownStrings.Meta.Types.Expr, WellKnownTypeIds.MetaExprId),
        QuoteKind.Pattern =>
            MetaSchemaRegistry.MetaType(WellKnownStrings.Meta.Types.Pattern, WellKnownTypeIds.MetaPatternId),
        QuoteKind.Type =>
            MetaSchemaRegistry.MetaType(WellKnownStrings.Meta.Types.TypeSyntax, WellKnownTypeIds.MetaTypeSyntaxId),
        _ => MetaSchemaRegistry.MetaType(WellKnownStrings.Meta.Types.Tokens, WellKnownTypeIds.MetaTokensId)
    };

    private static bool TryInferQuoteKind(Type expectedType, out QuoteKind kind)
    {
        if (expectedType is TyCon tokens &&
            IsMetaType(tokens, WellKnownTypeIds.MetaTokensId, WellKnownStrings.Meta.Types.Tokens))
        {
            kind = QuoteKind.Tokens;
            return true;
        }

        if (expectedType is TyCon { Args.Count: 1 } syntax &&
            IsMetaType(syntax, WellKnownTypeIds.MetaSyntaxId, WellKnownStrings.Meta.Types.Syntax) &&
            TryGetSingularKind(syntax.Args[0], out kind))
        {
            return true;
        }

        if (TryGetSequenceElement(expectedType, out var element) &&
            element is TyCon { Args.Count: 1 } sequenceSyntax &&
            IsMetaType(sequenceSyntax, WellKnownTypeIds.MetaSyntaxId, WellKnownStrings.Meta.Types.Syntax))
        {
            if (sequenceSyntax.Args[0] is TyCon item &&
                IsMetaType(item, WellKnownTypeIds.MetaItemId, WellKnownStrings.Meta.Types.Item))
            {
                kind = QuoteKind.Items;
                return true;
            }

            if (sequenceSyntax.Args[0] is TyCon member &&
                IsMetaType(member, WellKnownTypeIds.MetaMemberId, WellKnownStrings.Meta.Types.Member))
            {
                kind = QuoteKind.Members;
                return true;
            }
        }

        kind = default;
        return false;
    }

    private static bool TryGetSingularKind(Type marker, out QuoteKind kind)
    {
        kind = marker is TyCon markerType
            ? markerType.Name switch
            {
                WellKnownStrings.Meta.Types.Item => QuoteKind.Item,
                WellKnownStrings.Meta.Types.Member => QuoteKind.Member,
                WellKnownStrings.Meta.Types.Stmt => QuoteKind.Statement,
                WellKnownStrings.Meta.Types.Expr => QuoteKind.Expression,
                WellKnownStrings.Meta.Types.Pattern => QuoteKind.Pattern,
                WellKnownStrings.Meta.Types.TypeSyntax => QuoteKind.Type,
                _ => default
            }
            : default;
        return marker is TyCon markerTyCon &&
               (IsMetaType(markerTyCon, WellKnownTypeIds.MetaItemId, WellKnownStrings.Meta.Types.Item) ||
                IsMetaType(markerTyCon, WellKnownTypeIds.MetaMemberId, WellKnownStrings.Meta.Types.Member) ||
                IsMetaType(markerTyCon, WellKnownTypeIds.MetaStmtId, WellKnownStrings.Meta.Types.Stmt) ||
                IsMetaType(markerTyCon, WellKnownTypeIds.MetaExprId, WellKnownStrings.Meta.Types.Expr) ||
                IsMetaType(markerTyCon, WellKnownTypeIds.MetaPatternId, WellKnownStrings.Meta.Types.Pattern) ||
                IsMetaType(markerTyCon, WellKnownTypeIds.MetaTypeSyntaxId, WellKnownStrings.Meta.Types.TypeSyntax));
    }

    private static bool TryGetMarkerCategory(Type marker, out SyntaxCategory category)
    {
        category = marker is TyCon markerType
            ? markerType.Name switch
            {
                WellKnownStrings.Meta.Types.Item => SyntaxCategory.Item,
                WellKnownStrings.Meta.Types.Member => SyntaxCategory.Member,
                WellKnownStrings.Meta.Types.Stmt => SyntaxCategory.Statement,
                WellKnownStrings.Meta.Types.Expr => SyntaxCategory.Expression,
                WellKnownStrings.Meta.Types.Pattern => SyntaxCategory.Pattern,
                WellKnownStrings.Meta.Types.TypeSyntax => SyntaxCategory.Type,
                _ => SyntaxCategory.Nested
            }
            : SyntaxCategory.Nested;
        return category != SyntaxCategory.Nested;
    }

    private static bool TryGetSequenceElement(Type type, out Type element)
    {
        if (type is TyCon { Name: WellKnownStrings.BuiltinTypes.Seq, Args.Count: 1 } sequence)
        {
            element = sequence.Args[0];
            return true;
        }

        element = BaseTypes.Unit;
        return false;
    }

    private static bool IsMetaType(TyCon type, int typeId, string name) =>
        type.Id.Value == typeId || string.Equals(type.Name, name, StringComparison.Ordinal);
}
