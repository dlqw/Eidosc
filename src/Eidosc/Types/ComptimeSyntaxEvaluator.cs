using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Parsing.Handwritten;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Symbols;
using Eidosc.Syntax;
using Eidosc.Utilities;
using Eidosc.Utils;

namespace Eidosc.Types;

internal static class ComptimeSyntaxEvaluator
{
    public static bool TryEvaluate(
        QuoteExpr quote,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        value = ComptimeUnitValue.Instance;
        if (quote.Kind is not { } kind)
        {
            reason = "quote kind was not resolved during type checking";
            return false;
        }

        var entry = SyntaxSchema.Get(kind);
        var origin = CreateOrigin(quote, context);
        var hygiene = CreateHygieneIdentity(quote, origin, entry);
        var tokens = new List<ComptimeSyntaxToken>();
        var tokenOrdinal = 0;
        foreach (var part in quote.Parts)
        {
            switch (part)
            {
                case QuoteTokenPart token:
                    tokens.Add(CreateSourceToken(token, hygiene, tokenOrdinal++));
                    break;
                case QuoteSplicePart splice:
                    if (!TryAppendSplice(splice, entry, context, tokens, ref tokenOrdinal, out reason))
                    {
                        return false;
                    }
                    break;
            }
        }

        if (!context.Resources.TryConsumeSyntaxNodes(tokens.Count, out reason))
        {
            return false;
        }

        if (entry.Cardinality == SyntaxCardinality.Tokens)
        {
            value = new ComptimeTokensValue(tokens, quote.TrailingTrivia, origin, hygiene);
            reason = string.Empty;
            return true;
        }

        if (!TryParseFragments(
                entry,
                tokens,
                quote.TrailingTrivia,
                origin.SourceUri,
                out var parsedFragments,
                out var artifacts,
                out reason))
        {
            return false;
        }

        ApplyLexicalHygiene(tokens, artifacts, context, hygiene);
        if (!TryParseFragments(
                entry,
                tokens,
                quote.TrailingTrivia,
                origin.SourceUri,
                out parsedFragments,
                out _,
                out reason))
        {
            return false;
        }

        if (entry.Cardinality == SyntaxCardinality.Singular)
        {
            value = new ComptimeSyntaxValue(entry.Category, tokens, quote.TrailingTrivia, origin, hygiene);
            reason = string.Empty;
            return true;
        }

        var fragments = parsedFragments
            .Select((fragment, index) => (ComptimeValue)new ComptimeSyntaxValue(
                entry.Category,
                fragment,
                index == parsedFragments.Count - 1 ? quote.TrailingTrivia : string.Empty,
                origin,
                $"{hygiene}:fragment:{index.ToString(CultureInfo.InvariantCulture)}"))
            .ToArray();
        value = new ComptimeSequenceValue(ComptimeSequenceKind.List, fragments);
        reason = string.Empty;
        return true;
    }

    private static bool TryAppendSplice(
        QuoteSplicePart splice,
        SyntaxSchemaEntry target,
        ComptimeEvaluationContext context,
        List<ComptimeSyntaxToken> tokens,
        ref int tokenOrdinal,
        out string reason)
    {
        if (!ComptimeEvaluator.TryEvaluateNode(splice.Value, context, out var spliceValue, out reason))
        {
            return false;
        }

        if (splice.IsMany)
        {
            if (spliceValue is not ComptimeSequenceValue sequence)
            {
                reason = "..$() requires a sequence of typed syntax values";
                return false;
            }

            var leading = splice.LeadingTrivia;
            foreach (var element in sequence.Elements)
            {
                if (element is not ComptimeSyntaxValue syntax)
                {
                    reason = "..$() sequence contains a value that is not meta.Syntax";
                    return false;
                }

                if (!SyntaxSchema.CanEmbed(syntax.Category, target.Category))
                {
                    reason = $"..$() sequence contains meta.Syntax[{syntax.Category}], which cannot be embedded " +
                             $"in quote {target.SourceName}";
                    return false;
                }

                AppendSyntaxTokens(tokens, syntax, leading);
                tokenOrdinal += syntax.Tokens.Count;
                leading = string.Empty;
            }

            reason = string.Empty;
            return true;
        }

        if (spliceValue is ComptimeSequenceValue)
        {
            reason = "$() accepts one value; use ..$() for syntax sequences";
            return false;
        }

        if (spliceValue is ComptimeSyntaxValue syntaxValue)
        {
            if (!SyntaxSchema.CanEmbed(syntaxValue.Category, target.Category))
            {
                reason = $"meta.Syntax[{syntaxValue.Category}] cannot be embedded in quote {target.SourceName}";
                return false;
            }

            AppendSyntaxTokens(tokens, syntaxValue, splice.LeadingTrivia);
            tokenOrdinal += syntaxValue.Tokens.Count;
            reason = string.Empty;
            return true;
        }

        if (spliceValue is ComptimeTokensValue tokenTree)
        {
            if (target.Category != SyntaxCategory.Tokens)
            {
                reason = "meta.Tokens may only be spliced into quote tokens";
                return false;
            }

            AppendTokens(tokens, tokenTree.Tokens, tokenTree.TrailingTrivia, splice.LeadingTrivia);
            tokenOrdinal += tokenTree.Tokens.Count;
            reason = string.Empty;
            return true;
        }

        if (!TryReifyValue(spliceValue, splice.Span, splice.LeadingTrivia, tokens, ref tokenOrdinal, out reason))
        {
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryReifyValue(
        ComptimeValue value,
        SourceSpan span,
        string leadingTrivia,
        List<ComptimeSyntaxToken> tokens,
        ref int tokenOrdinal,
        out string reason)
    {
        switch (value)
        {
            case ComptimeUnitValue:
                AddGenerated(tokens, SyntaxKind.PtLParen, "lparen", "(", leadingTrivia, span, null);
                AddGenerated(tokens, SyntaxKind.PtRParen, "rparen", ")", string.Empty, span, null);
                tokenOrdinal += 2;
                reason = string.Empty;
                return true;
            case ComptimeBoolValue scalar:
                AddGenerated(tokens, SyntaxKind.BooleanLiteral, "booleanLiteral", scalar.Value ? "true" : "false", leadingTrivia, span, null);
                tokenOrdinal++;
                reason = string.Empty;
                return true;
            case ComptimeIntegerValue scalar:
                AddGenerated(tokens, SyntaxKind.NumberLiteral, "numberLiteral", scalar.Value.ToString(CultureInfo.InvariantCulture), leadingTrivia, span, null);
                tokenOrdinal++;
                reason = string.Empty;
                return true;
            case ComptimeFloatValue scalar:
                AddGenerated(tokens, SyntaxKind.NumberLiteral, "numberLiteral", scalar.Value.ToString("R", CultureInfo.InvariantCulture), leadingTrivia, span, null);
                tokenOrdinal++;
                reason = string.Empty;
                return true;
            case ComptimeCharValue scalar:
                AddGenerated(tokens, SyntaxKind.CharLiteral, "charLiteral", QuoteChar(scalar.Value), leadingTrivia, span, null);
                tokenOrdinal++;
                reason = string.Empty;
                return true;
            case ComptimeStringValue scalar:
                AddGenerated(tokens, SyntaxKind.StringLiteral, "stringLiteral", QuoteString(scalar.Value), leadingTrivia, span, null);
                tokenOrdinal++;
                reason = string.Empty;
                return true;
            case ComptimeDeclValue declaration:
                AddGenerated(
                    tokens,
                    SyntaxKind.Identifier,
                    "identifier",
                    declaration.Name,
                    leadingTrivia,
                    span,
                    new ComptimeSyntaxIdentity(
                        ComptimeSyntaxIdentityKind.Declaration,
                        declaration.StableIdentity,
                        declaration.SymbolId,
                        TypeId.None));
                tokenOrdinal++;
                reason = string.Empty;
                return true;
            case ComptimeTypeValue type:
                AppendType(type.TypeRef, leadingTrivia, span, tokens, ref tokenOrdinal);
                reason = string.Empty;
                return true;
            case ComptimeAdtValue adt:
                return TryAppendAdt(adt, span, leadingTrivia, tokens, ref tokenOrdinal, out reason);
            case ComptimeMetaObjectValue { SchemaKind: "identifier" } identifier when
                identifier.TryGet("spelling", out var spellingValue) &&
                spellingValue is ComptimeStringValue spelling &&
                identifier.TryGet("category", out var categoryValue) &&
                categoryValue is ComptimeStringValue category &&
                identifier.TryGet("identity", out var identityValue) &&
                identityValue is ComptimeStringValue stableIdentity:
                AddGenerated(
                    tokens,
                    SyntaxKind.Identifier,
                    "identifier",
                    spelling.Value,
                    leadingTrivia,
                    span,
                    new ComptimeSyntaxIdentity(
                        ComptimeSyntaxIdentityKind.Identifier,
                        stableIdentity.Value,
                        SymbolId.None,
                        TypeId.None,
                        category.Value));
                tokenOrdinal++;
                reason = string.Empty;
                return true;
            case ComptimeMetaObjectValue metaObject when
                metaObject.TryGet("name", out var nameValue) && nameValue is ComptimeStringValue name:
                var handle = metaObject.Properties.Select(static property => property.Value)
                    .OfType<ComptimeDeclValue>()
                    .FirstOrDefault();
                AddGenerated(
                    tokens,
                    SyntaxKind.Identifier,
                    "identifier",
                    name.Value,
                    leadingTrivia,
                    span,
                    handle == null
                        ? null
                        : new ComptimeSyntaxIdentity(
                            ComptimeSyntaxIdentityKind.Declaration,
                            handle.StableIdentity,
                            handle.SymbolId,
                            TypeId.None));
                tokenOrdinal++;
                reason = string.Empty;
                return true;
            default:
                reason = $"comptime value '{value.GetType().Name}' cannot be reified losslessly into quote syntax";
                return false;
        }
    }

    private static bool TryAppendAdt(
        ComptimeAdtValue adt,
        SourceSpan span,
        string leadingTrivia,
        List<ComptimeSyntaxToken> tokens,
        ref int tokenOrdinal,
        out string reason)
    {
        AddGenerated(
            tokens,
            SyntaxKind.Identifier,
            "identifier",
            adt.ConstructorName,
            leadingTrivia,
            span,
            adt.ConstructorId.IsValid
                ? new ComptimeSyntaxIdentity(
                    ComptimeSyntaxIdentityKind.Declaration,
                    $"constructor:{adt.ConstructorName}",
                    adt.ConstructorId,
                    TypeId.None)
                : null);
        AddGenerated(tokens, SyntaxKind.PtLParen, "lparen", "(", string.Empty, span, null);
        tokenOrdinal += 2;
        var first = true;
        foreach (var positional in adt.PositionalValues)
        {
            if (!first)
            {
                AddGenerated(tokens, SyntaxKind.PtComma, "comma", ",", string.Empty, span, null);
                tokenOrdinal++;
            }

            if (!TryReifyValue(positional, span, string.Empty, tokens, ref tokenOrdinal, out reason))
            {
                return false;
            }
            first = false;
        }

        foreach (var named in adt.NamedValues)
        {
            if (!first)
            {
                AddGenerated(tokens, SyntaxKind.PtComma, "comma", ",", string.Empty, span, null);
                tokenOrdinal++;
            }

            AddGenerated(tokens, SyntaxKind.Identifier, "identifier", named.Name, string.Empty, span, null);
            AddGenerated(tokens, SyntaxKind.PtColon, "colon", ":", string.Empty, span, null);
            tokenOrdinal += 2;
            if (!TryReifyValue(named.Value, span, string.Empty, tokens, ref tokenOrdinal, out reason))
            {
                return false;
            }
            first = false;
        }

        AddGenerated(tokens, SyntaxKind.PtRParen, "rparen", ")", string.Empty, span, null);
        tokenOrdinal++;
        reason = string.Empty;
        return true;
    }

    private static void AppendType(
        MetaTypeRef type,
        string leadingTrivia,
        SourceSpan span,
        List<ComptimeSyntaxToken> tokens,
        ref int tokenOrdinal)
    {
        AddGenerated(
            tokens,
            SyntaxKind.Identifier,
            "identifier",
            type.Name,
            leadingTrivia,
            span,
            new ComptimeSyntaxIdentity(
                ComptimeSyntaxIdentityKind.Type,
                type.StableIdentity,
                type.SymbolId,
                type.TypeId));
        tokenOrdinal++;
        if (type.Arguments.Count == 0)
        {
            return;
        }

        AddGenerated(tokens, SyntaxKind.PtLBrack, "lbrack", "[", string.Empty, span, null);
        tokenOrdinal++;
        for (var index = 0; index < type.Arguments.Count; index++)
        {
            if (index > 0)
            {
                AddGenerated(tokens, SyntaxKind.PtComma, "comma", ",", string.Empty, span, null);
                tokenOrdinal++;
            }
            AppendType(type.Arguments[index], string.Empty, span, tokens, ref tokenOrdinal);
        }
        AddGenerated(tokens, SyntaxKind.PtRBrack, "rbrack", "]", string.Empty, span, null);
        tokenOrdinal++;
    }

    private static ComptimeSyntaxToken CreateSourceToken(QuoteTokenPart token, string hygiene, int ordinal)
    {
        var identity = token.TokenKind is SyntaxKind.Identifier or SyntaxKind.OperatorIdentifier
            ? new ComptimeSyntaxIdentity(
                ComptimeSyntaxIdentityKind.Hygiene,
                $"{hygiene}:token:{ordinal.ToString(CultureInfo.InvariantCulture)}",
                SymbolId.None,
                TypeId.None)
            : null;
        return new ComptimeSyntaxToken(
            token.TokenKind,
            token.TerminalName,
            token.TerminalFlags,
            token.Spelling,
            token.LeadingTrivia,
            string.Empty,
            token.Span,
            identity);
    }

    private static void ApplyLexicalHygiene(
        List<ComptimeSyntaxToken> tokens,
        ComptimeSyntaxParseArtifacts artifacts,
        ComptimeEvaluationContext context,
        string hygiene)
    {
        if (tokens.Count == 0)
        {
            return;
        }

        var parents = new Dictionary<EidosAstNode, EidosAstNode>(ReferenceEqualityComparer.Instance);
        var allNodes = new List<EidosAstNode>();
        foreach (var root in artifacts.Nodes)
        {
            var pending = new Stack<EidosAstNode>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                allNodes.Add(node);
                foreach (var child in AstStableNodeTraversal.GetStructuralChildren(node))
                {
                    parents.TryAdd(child, node);
                    pending.Push(child);
                }
            }
        }

        var binders = new List<QuoteHygieneBinder>();
        foreach (var node in allNodes)
        {
            if (!TryGetHygieneBinderName(node, out var name) ||
                !TryFindRawTokenAtSpan(tokens, artifacts.TokenPositions, node.Span, hygiene, name, out var tokenIndex))
            {
                continue;
            }

            var identity = $"{hygiene}:binder:{tokenIndex.ToString(CultureInfo.InvariantCulture)}";
            tokens[tokenIndex] = tokens[tokenIndex] with
            {
                Identity = new ComptimeSyntaxIdentity(
                    ComptimeSyntaxIdentityKind.Hygiene,
                    identity,
                    SymbolId.None,
                    TypeId.None)
            };
            var (scopeStart, scopeEnd, requiresFollowing) = GetHygieneBinderScope(
                node,
                parents,
                artifacts.Nodes,
                artifacts.SourceText.Length);
            binders.Add(new QuoteHygieneBinder(
                name,
                tokenIndex,
                identity,
                scopeStart,
                scopeEnd,
                requiresFollowing));
        }

        ApplyDefinitionSitePathIdentities(tokens, artifacts.TokenPositions, allNodes, binders, context, hygiene);

        for (var tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
        {
            var token = tokens[tokenIndex];
            if (!IsRawQuoteIdentifier(token, hygiene) || binders.Any(binder => binder.TokenIndex == tokenIndex))
            {
                continue;
            }

            var position = artifacts.TokenPositions[tokenIndex].Start;
            var binder = binders
                .Where(candidate =>
                    string.Equals(candidate.Name, token.Spelling, StringComparison.Ordinal) &&
                    position >= candidate.ScopeStart &&
                    position <= candidate.ScopeEnd &&
                    (!candidate.RequiresFollowingReference || position >= artifacts.TokenPositions[candidate.TokenIndex].End))
                .OrderBy(candidate => candidate.ScopeEnd - candidate.ScopeStart)
                .ThenByDescending(candidate => artifacts.TokenPositions[candidate.TokenIndex].Start)
                .FirstOrDefault();
            if (binder != null)
            {
                tokens[tokenIndex] = token with
                {
                    Identity = new ComptimeSyntaxIdentity(
                        ComptimeSyntaxIdentityKind.Hygiene,
                        binder.Identity,
                        SymbolId.None,
                        TypeId.None)
                };
                continue;
            }

            var syntaxRoles = allNodes
                .Where(node =>
                    position >= node.Span.Position &&
                    artifacts.TokenPositions[tokenIndex].End <= node.Span.EndPosition)
                .OrderBy(node => node.Span.Length)
                .Select(static node => node switch
                {
                    TypePath => DefinitionSiteSyntaxRole.Type,
                    CtorExpr or CtorPattern => DefinitionSiteSyntaxRole.Constructor,
                    IdentifierExpr or PathExpr => DefinitionSiteSyntaxRole.Value,
                    _ => DefinitionSiteSyntaxRole.Any
                })
                .Where(static role => role != DefinitionSiteSyntaxRole.Any)
                .Distinct()
                .ToArray();
            if (syntaxRoles.Length == 0)
            {
                syntaxRoles = [DefinitionSiteSyntaxRole.Any];
            }

            var definitionSiteSymbols = new List<Symbol>();
            foreach (var syntaxRole in syntaxRoles)
            {
                if (TryResolveDefinitionSiteSymbol(context, [token.Spelling], syntaxRole, out var candidate) &&
                    definitionSiteSymbols.All(existing => existing.Id != candidate.Id))
                {
                    definitionSiteSymbols.Add(candidate);
                }
            }
            if (definitionSiteSymbols.Count == 1)
            {
                AttachDefinitionSiteIdentity(tokens, tokenIndex, definitionSiteSymbols[0], context);
            }
        }

        ApplyQualifiedTokenChainIdentities(tokens, artifacts.TokenPositions, binders, context, hygiene);
    }

    private static void ApplyQualifiedTokenChainIdentities(
        List<ComptimeSyntaxToken> tokens,
        IReadOnlyList<SyntaxTokenPosition> positions,
        IReadOnlyList<QuoteHygieneBinder> binders,
        ComptimeEvaluationContext context,
        string hygiene)
    {
        for (var startIndex = 0; startIndex < tokens.Count; startIndex++)
        {
            if (tokens[startIndex].Kind is not (SyntaxKind.Identifier or SyntaxKind.OperatorIdentifier))
            {
                continue;
            }

            var segmentIndices = new List<int> { startIndex };
            var cursor = startIndex;
            while (cursor + 2 < tokens.Count &&
                   string.Equals(tokens[cursor + 1].Spelling, WellKnownStrings.Punctuation.Dot, StringComparison.Ordinal) &&
                   tokens[cursor + 2].Kind is SyntaxKind.Identifier or SyntaxKind.OperatorIdentifier)
            {
                segmentIndices.Add(cursor + 2);
                cursor += 2;
            }

            if (segmentIndices.Count < 2)
            {
                continue;
            }

            startIndex = cursor;
            var firstPosition = positions[segmentIndices[0]].Start;
            if (binders.Any(candidate =>
                    string.Equals(candidate.Name, tokens[segmentIndices[0]].Spelling, StringComparison.Ordinal) &&
                    firstPosition >= candidate.ScopeStart &&
                    firstPosition <= candidate.ScopeEnd &&
                    (!candidate.RequiresFollowingReference ||
                     firstPosition >= positions[candidate.TokenIndex].End)))
            {
                continue;
            }

            for (var segmentIndex = 0; segmentIndex < segmentIndices.Count; segmentIndex++)
            {
                var tokenIndex = segmentIndices[segmentIndex];
                if (!IsRawQuoteIdentifier(tokens[tokenIndex], hygiene))
                {
                    continue;
                }

                var role = segmentIndex == segmentIndices.Count - 1
                    ? DefinitionSiteSyntaxRole.Any
                    : DefinitionSiteSyntaxRole.Namespace;
                var path = segmentIndices
                    .Take(segmentIndex + 1)
                    .Select(index => tokens[index].Spelling)
                    .ToArray();
                if (TryResolveDefinitionSiteSymbol(context, path, role, out var symbol) ||
                    (segmentIndex > 0 &&
                     tokens[segmentIndices[segmentIndex - 1]].Identity is { SymbolId.IsValid: true } ownerIdentity &&
                     TryResolveDefinitionSiteMember(
                         context,
                         ownerIdentity.SymbolId,
                         tokens[tokenIndex].Spelling,
                         role,
                         out symbol)))
                {
                    AttachDefinitionSiteIdentity(tokens, tokenIndex, symbol, context);
                }
            }
        }
    }

    private static void ApplyDefinitionSitePathIdentities(
        List<ComptimeSyntaxToken> tokens,
        IReadOnlyList<SyntaxTokenPosition> positions,
        IReadOnlyList<EidosAstNode> allNodes,
        IReadOnlyList<QuoteHygieneBinder> binders,
        ComptimeEvaluationContext context,
        string hygiene)
    {
        foreach (var node in allNodes)
        {
            if (!TryGetDefinitionSitePath(node, out var path, out var pathSpan, out var finalRole) || path.Count < 2 ||
                !TryFindPathTokenIndices(tokens, positions, pathSpan, path, out var tokenIndices))
            {
                continue;
            }

            var firstTokenIndex = tokenIndices[0];
            var firstPosition = positions[firstTokenIndex].Start;
            if (binders.Any(candidate =>
                    string.Equals(candidate.Name, path[0], StringComparison.Ordinal) &&
                    firstPosition >= candidate.ScopeStart &&
                    firstPosition <= candidate.ScopeEnd &&
                    (!candidate.RequiresFollowingReference ||
                     firstPosition >= positions[candidate.TokenIndex].End)))
            {
                continue;
            }

            for (var segmentIndex = 0; segmentIndex < tokenIndices.Count; segmentIndex++)
            {
                var tokenIndex = tokenIndices[segmentIndex];
                if (!IsRawQuoteIdentifier(tokens[tokenIndex], hygiene))
                {
                    continue;
                }

                var role = segmentIndex == tokenIndices.Count - 1
                    ? finalRole
                    : DefinitionSiteSyntaxRole.Namespace;
                if (TryResolveDefinitionSiteSymbol(
                        context,
                        path.Take(segmentIndex + 1).ToArray(),
                        role,
                        out var symbol))
                {
                    AttachDefinitionSiteIdentity(tokens, tokenIndex, symbol, context);
                    continue;
                }

                if (segmentIndex > 0 &&
                    tokens[tokenIndices[segmentIndex - 1]].Identity is { SymbolId.IsValid: true } ownerIdentity &&
                    TryResolveDefinitionSiteMember(
                        context,
                        ownerIdentity.SymbolId,
                        path[segmentIndex],
                        role,
                        out symbol))
                {
                    AttachDefinitionSiteIdentity(tokens, tokenIndex, symbol, context);
                }
            }
        }
    }

    private static bool TryResolveDefinitionSiteMember(
        ComptimeEvaluationContext context,
        SymbolId ownerId,
        string memberName,
        DefinitionSiteSyntaxRole role,
        out Symbol symbol)
    {
        symbol = null!;
        if (context.Meta == null || !ownerId.IsValid || string.IsNullOrWhiteSpace(memberName))
        {
            return false;
        }

        var table = context.Meta.SymbolTable;
        IEnumerable<SymbolId> memberIds = table.GetSymbol(ownerId) switch
        {
            ModuleSymbol module => module.Members.Concat(table.Symbols.Values
                .Where(candidate => candidate.DefinitionModuleId == module.Id)
                .Select(static candidate => candidate.Id)),
            AdtSymbol adt => adt.Constructors,
            TraitSymbol trait => trait.Methods.Concat(trait.AssociatedTypes).Concat(trait.AssociatedConsts),
            ImplSymbol impl => impl.Methods.Concat(impl.AssociatedTypes).Concat(impl.AssociatedConsts),
            _ => []
        };

        var candidates = memberIds
            .Select(table.GetSymbol)
            .Where(static candidate => candidate != null)
            .Where(candidate => string.Equals(candidate!.Name, memberName, StringComparison.Ordinal))
            .Where(candidate => MatchesDefinitionSiteRole(candidate!, role))
            .Where(candidate => candidate!.IsPublic || candidate.DefinitionModuleId == context.Meta.Access.CurrentModuleId)
            .DistinctBy(static candidate => candidate!.Id)
            .ToArray();
        if (candidates.Length != 1)
        {
            return false;
        }

        symbol = candidates[0]!;
        return true;
    }

    private static bool MatchesDefinitionSiteRole(Symbol symbol, DefinitionSiteSyntaxRole role) => role switch
    {
        DefinitionSiteSyntaxRole.Type => symbol is AdtSymbol or TraitSymbol or EffectSymbol or TypeParamSymbol or
            AssociatedTypeSymbol,
        DefinitionSiteSyntaxRole.Constructor => symbol is CtorSymbol,
        DefinitionSiteSyntaxRole.Value => symbol is VarSymbol or FuncSymbol or CtorSymbol or ImplSymbol or
            FieldSymbol or AssociatedConstSymbol,
        DefinitionSiteSyntaxRole.Namespace => symbol is ModuleSymbol or AdtSymbol or TraitSymbol,
        _ => true
    };

    private static bool TryGetDefinitionSitePath(
        EidosAstNode node,
        out IReadOnlyList<string> path,
        out SourceSpan pathSpan,
        out DefinitionSiteSyntaxRole role)
    {
        switch (node)
        {
            case PathExpr expression:
                path = expression.Path;
                pathSpan = expression.Span;
                role = DefinitionSiteSyntaxRole.Value;
                return path.Count > 0;
            case TypePath type:
                path = type.ToQualifiedPathParts();
                pathSpan = type.Span;
                role = DefinitionSiteSyntaxRole.Type;
                return path.Count > 0;
            case CtorPattern constructor:
                path = string.IsNullOrWhiteSpace(constructor.PackageAlias)
                    ? [.. constructor.ModulePath, constructor.ConstructorName]
                    : [constructor.PackageAlias, .. constructor.ModulePath, constructor.ConstructorName];
                pathSpan = constructor.Span;
                role = DefinitionSiteSyntaxRole.Constructor;
                return path.Count > 0;
            case MethodCallExpr { Receiver: { } receiver, MethodName.Length: > 0 } method
                when TryGetReceiverDefinitionSitePath(receiver, out var receiverPath):
                path = [.. receiverPath, method.MethodName];
                var rootSpan = GetDefinitionSiteReceiverRootSpan(receiver);
                pathSpan = new SourceSpan(
                    rootSpan.Location,
                    Math.Max(0, method.MemberNameSpan.EndPosition - rootSpan.Position));
                role = DefinitionSiteSyntaxRole.Value;
                return true;
            default:
                path = [];
                pathSpan = SourceSpan.Empty;
                role = DefinitionSiteSyntaxRole.Any;
                return false;
        }
    }

    private static SourceSpan GetDefinitionSiteReceiverRootSpan(EidosAstNode receiver)
    {
        while (receiver is MethodCallExpr { Receiver: { } nestedReceiver })
        {
            receiver = nestedReceiver;
        }
        return receiver.Span;
    }

    private static bool TryGetReceiverDefinitionSitePath(
        EidosAstNode receiver,
        out IReadOnlyList<string> path)
    {
        switch (receiver)
        {
            case IdentifierExpr identifier:
                path = [identifier.Name];
                return !string.IsNullOrWhiteSpace(identifier.Name);
            case PathExpr expression:
                path = expression.Path;
                return path.Count > 0;
            case MethodCallExpr { Receiver: { } nestedReceiver, MethodName.Length: > 0 } method
                when TryGetReceiverDefinitionSitePath(nestedReceiver, out var nestedPath):
                path = [.. nestedPath, method.MethodName];
                return true;
            default:
                path = [];
                return false;
        }
    }

    private static bool TryFindPathTokenIndices(
        IReadOnlyList<ComptimeSyntaxToken> tokens,
        IReadOnlyList<SyntaxTokenPosition> positions,
        SourceSpan span,
        IReadOnlyList<string> path,
        out IReadOnlyList<int> tokenIndices)
    {
        var result = new List<int>(path.Count);
        var nextPosition = span.Position;
        foreach (var segment in path)
        {
            var found = -1;
            for (var tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
            {
                var position = positions[tokenIndex];
                if (position.Start < nextPosition || position.End > span.EndPosition ||
                    tokens[tokenIndex].Kind is not (SyntaxKind.Identifier or SyntaxKind.OperatorIdentifier) ||
                    !string.Equals(tokens[tokenIndex].Spelling, segment, StringComparison.Ordinal))
                {
                    continue;
                }

                found = tokenIndex;
                nextPosition = position.End;
                break;
            }

            if (found < 0)
            {
                tokenIndices = [];
                return false;
            }
            result.Add(found);
        }

        tokenIndices = result;
        return true;
    }

    private static void AttachDefinitionSiteIdentity(
        IList<ComptimeSyntaxToken> tokens,
        int tokenIndex,
        Symbol symbol,
        ComptimeEvaluationContext context)
    {
        tokens[tokenIndex] = tokens[tokenIndex] with
        {
            Identity = new ComptimeSyntaxIdentity(
                symbol.TypeId.IsValid
                    ? ComptimeSyntaxIdentityKind.Type
                    : ComptimeSyntaxIdentityKind.Declaration,
                MetaComptimeIntrinsics.CreateStableIdentity(symbol, context.Meta!.SymbolTable),
                symbol.Id,
                symbol.TypeId)
        };
    }

    private static bool TryGetHygieneBinderName(EidosAstNode node, out string name)
    {
        name = node switch
        {
            AdtDef adt => adt.Name,
            CaseTypeDef caseType => caseType.Name,
            TraitDef trait => trait.Name,
            EffectDef effect => effect.Name,
            InstanceDecl instance => instance.Name,
            FuncDef function => function.Name,
            FuncDecl function => function.Name,
            ModuleDecl module => module.Path.LastOrDefault() ?? string.Empty,
            LetDecl { Pattern: VarPattern variable } => variable.Name,
            Field field => field.Name,
            Constructor constructor => constructor.Name,
            AssociatedTypeDecl associatedType => associatedType.Name,
            AssociatedConstDecl associatedConst => associatedConst.Name,
            TypeParam typeParameter => typeParameter.Name,
            VarPattern variable => variable.Name,
            AsPattern binding => binding.BindingName,
            _ => string.Empty
        };
        return !string.IsNullOrWhiteSpace(name) && name != WellKnownStrings.Punctuation.Underscore;
    }

    private static (int Start, int End, bool RequiresFollowingReference) GetHygieneBinderScope(
        EidosAstNode binder,
        IReadOnlyDictionary<EidosAstNode, EidosAstNode> parents,
        IReadOnlyList<EidosAstNode> roots,
        int sourceLength)
    {
        if (binder is Declaration and not LetDecl || binder is CaseTypeDef or Constructor)
        {
            return (0, sourceLength, false);
        }

        if (binder is Field or AssociatedTypeDecl or AssociatedConstDecl)
        {
            return (binder.Span.Position, binder.Span.EndPosition, false);
        }

        var current = binder;
        while (parents.TryGetValue(current, out var parent))
        {
            if (parent is PatternBranch or LambdaExpr)
            {
                return (parent.Span.Position, parent.Span.EndPosition, false);
            }
            if (parent is BlockExpr)
            {
                return (parent.Span.Position, parent.Span.EndPosition, binder is VarPattern);
            }
            if (parent is Declaration declaration)
            {
                return (declaration.Span.Position, declaration.Span.EndPosition, false);
            }
            current = parent;
        }

        var root = roots.FirstOrDefault(candidate =>
            binder.Span.Position >= candidate.Span.Position &&
            binder.Span.EndPosition <= candidate.Span.EndPosition);
        return root == null
            ? (0, sourceLength, false)
            : (root.Span.Position, root.Span.EndPosition, false);
    }

    private static bool TryFindRawTokenAtSpan(
        IReadOnlyList<ComptimeSyntaxToken> tokens,
        IReadOnlyList<SyntaxTokenPosition> positions,
        SourceSpan span,
        string hygiene,
        string name,
        out int tokenIndex)
    {
        for (var index = 0; index < tokens.Count; index++)
        {
            if (positions[index].Start >= span.Position &&
                positions[index].End <= span.EndPosition &&
                string.Equals(tokens[index].Spelling, name, StringComparison.Ordinal) &&
                IsRawQuoteIdentifier(tokens[index], hygiene))
            {
                tokenIndex = index;
                return true;
            }
        }
        tokenIndex = -1;
        return false;
    }

    private static bool IsRawQuoteIdentifier(ComptimeSyntaxToken token, string hygiene) =>
        token.Identity is
        {
            Kind: ComptimeSyntaxIdentityKind.Hygiene,
            StableIdentity: var identity
        } && identity.StartsWith($"{hygiene}:token:", StringComparison.Ordinal);

    private static bool TryResolveDefinitionSiteSymbol(
        ComptimeEvaluationContext context,
        IReadOnlyList<string> path,
        DefinitionSiteSyntaxRole syntaxRole,
        out Symbol symbol)
    {
        symbol = null!;
        if (context.Meta == null || path.Count == 0)
        {
            return false;
        }

        var lookupKind = syntaxRole switch
        {
            DefinitionSiteSyntaxRole.Type => MetaDefinitionSiteLookupKind.Type,
            DefinitionSiteSyntaxRole.Constructor => MetaDefinitionSiteLookupKind.Constructor,
            DefinitionSiteSyntaxRole.Value => MetaDefinitionSiteLookupKind.Value |
                                             MetaDefinitionSiteLookupKind.Constructor,
            DefinitionSiteSyntaxRole.Namespace => MetaDefinitionSiteLookupKind.Module |
                                                 MetaDefinitionSiteLookupKind.Type,
            _ => MetaDefinitionSiteLookupKind.Any
        };
        if (context.Meta.ResolveDefinitionSite?.Invoke(path, lookupKind) is { } resolved)
        {
            symbol = resolved;
            return true;
        }

        if (path.Count != 1)
        {
            return false;
        }

        var table = context.Meta.SymbolTable;
        var name = path[0];
        var candidates = new HashSet<SymbolId>();
        if (table.BuiltinScope?.LookupType(name) is { IsValid: true } builtinType)
        {
            candidates.Add(builtinType);
        }
        if (table.BuiltinScope?.LookupValue(name) is { IsValid: true } builtinValue)
        {
            candidates.Add(builtinValue);
        }
        var definitionModule = context.Meta.Access.CurrentModuleId;
        if (definitionModule.IsValid && table.Modules.GetModule(definitionModule) is { } module)
        {
            foreach (var memberId in module.Members)
            {
                if (memberId.IsValid &&
                    table.GetSymbol(memberId) is { } member &&
                    string.Equals(member.Name, name, StringComparison.Ordinal))
                {
                    candidates.Add(memberId);
                }
            }
        }

        var resolvedCandidates = candidates
            .Select(table.GetSymbol)
            .Where(static candidate => candidate != null)
            .Where(candidate => MatchesDefinitionSiteRole(candidate!, syntaxRole))
            .DistinctBy(static candidate => candidate!.Id)
            .ToArray();
        if (resolvedCandidates.Length != 1)
        {
            return false;
        }
        symbol = resolvedCandidates[0]!;
        return true;
    }

    private sealed record QuoteHygieneBinder(
        string Name,
        int TokenIndex,
        string Identity,
        int ScopeStart,
        int ScopeEnd,
        bool RequiresFollowingReference);

    private enum DefinitionSiteSyntaxRole
    {
        Any,
        Type,
        Constructor,
        Value,
        Namespace
    }

    private static void AppendSyntaxTokens(
        List<ComptimeSyntaxToken> target,
        ComptimeSyntaxValue syntax,
        string leadingTrivia) =>
        AppendTokens(target, syntax.Tokens, syntax.TrailingTrivia, leadingTrivia);

    private static void AppendTokens(
        List<ComptimeSyntaxToken> target,
        IReadOnlyList<ComptimeSyntaxToken> source,
        string trailingTrivia,
        string leadingTrivia)
    {
        if (source.Count == 0)
        {
            return;
        }

        for (var index = 0; index < source.Count; index++)
        {
            var token = source[index];
            if (index == 0 && leadingTrivia.Length > 0)
            {
                token = token with { LeadingTrivia = leadingTrivia + token.LeadingTrivia };
            }

            if (index == source.Count - 1 && trailingTrivia.Length > 0)
            {
                token = token with { TrailingTrivia = token.TrailingTrivia + trailingTrivia };
            }

            target.Add(token);
        }
    }

    internal static bool TryParseFragments(
        SyntaxSchemaEntry entry,
        IReadOnlyList<ComptimeSyntaxToken> tokens,
        string trailingTrivia,
        string sourcePath,
        out IReadOnlyList<IReadOnlyList<ComptimeSyntaxToken>> fragments,
        out ComptimeSyntaxParseArtifacts artifacts,
        out string reason,
        SyntaxMemberGrammar memberGrammar = SyntaxMemberGrammar.Any)
    {
        var layout = CreateParserTokenLayout(tokens, trailingTrivia, sourcePath);
        if (!SyntaxFragmentParser.TryParse(
                entry,
                layout.ParserTokens,
                sourcePath,
                EidosLanguageVersions.Current,
                layout.SourceText,
                out var parseResult,
                out reason,
                memberGrammar))
        {
            fragments = [];
            artifacts = new ComptimeSyntaxParseArtifacts(
                layout.SourceText,
                layout.TokenPositions,
                parseResult.Nodes,
                parseResult.Diagnostics);
            return false;
        }

        foreach (var node in parseResult.Nodes)
        {
            if (!SyntaxSchema.GetNodeCategories(node.GetType()).Contains(entry.Category))
            {
                fragments = [];
                artifacts = new ComptimeSyntaxParseArtifacts(
                    layout.SourceText,
                    layout.TokenPositions,
                    parseResult.Nodes,
                    parseResult.Diagnostics);
                reason = $"parsed {node.GetType().Name} is not legal in the {entry.Category} syntax category";
                return false;
            }
        }

        artifacts = new ComptimeSyntaxParseArtifacts(
            layout.SourceText,
            layout.TokenPositions,
            parseResult.Nodes,
            parseResult.Diagnostics);

        if (entry.Cardinality == SyntaxCardinality.Singular)
        {
            fragments = [tokens];
            reason = string.Empty;
            return true;
        }

        fragments = SplitAtParsedNodeBoundaries(tokens, layout.TokenPositions, parseResult.Nodes);
        reason = string.Empty;
        return true;
    }

    private static SyntaxParserTokenLayout CreateParserTokenLayout(
        IReadOnlyList<ComptimeSyntaxToken> tokens,
        string trailingTrivia,
        string sourcePath)
    {
        var source = new StringBuilder("\n");
        var parserTokens = new List<Token>(tokens.Count);
        var positions = new List<SyntaxTokenPosition>(tokens.Count);
        var position = 1;
        var line = 1;
        var column = 0;
        for (var index = 0; index < tokens.Count; index++)
        {
            var syntax = tokens[index];
            AppendText(syntax.LeadingTrivia, source, ref position, ref line, ref column);
            var location = new SourceLocation(position, line, column, sourcePath);
            Token parserToken = syntax.Kind == SyntaxKind.Comment
                ? new CommentToken(location, syntax.Spelling)
                : new ContentToken(
                    location,
                    syntax.Kind,
                    new Terminal(0, syntax.TerminalName, syntax.TerminalFlags),
                    syntax.Spelling.GetOrIntern(),
                    syntax.Spelling.Length,
                    syntax.Spelling);
            parserTokens.Add(parserToken);
            var start = position;
            AppendText(syntax.Spelling, source, ref position, ref line, ref column);
            positions.Add(new SyntaxTokenPosition(index, start, position, syntax.Kind));
            AppendText(syntax.TrailingTrivia, source, ref position, ref line, ref column);
        }

        AppendText(trailingTrivia, source, ref position, ref line, ref column);
        return new SyntaxParserTokenLayout(source.ToString(), parserTokens, positions);
    }

    private static void AppendText(
        string text,
        StringBuilder source,
        ref int position,
        ref int line,
        ref int column)
    {
        source.Append(text);
        foreach (var ch in text)
        {
            position++;
            if (ch == '\n')
            {
                line++;
                column = 0;
            }
            else
            {
                column++;
            }
        }
    }

    private static IReadOnlyList<IReadOnlyList<ComptimeSyntaxToken>> SplitAtParsedNodeBoundaries(
        IReadOnlyList<ComptimeSyntaxToken> tokens,
        IReadOnlyList<SyntaxTokenPosition> positions,
        IReadOnlyList<EidosAstNode> nodes)
    {
        if (nodes.Count == 0)
        {
            return [];
        }

        var starts = new int[nodes.Count];
        starts[0] = 0;
        for (var nodeIndex = 1; nodeIndex < nodes.Count; nodeIndex++)
        {
            var nodeStart = nodes[nodeIndex].Span.Position;
            var start = 0;
            while (start < positions.Count && positions[start].End <= nodeStart)
            {
                start++;
            }

            var previousNodeEnd = nodes[nodeIndex - 1].Span.EndPosition;
            while (start > 0 &&
                   positions[start - 1].Kind == SyntaxKind.Comment &&
                   positions[start - 1].Start >= previousNodeEnd)
            {
                start--;
            }

            starts[nodeIndex] = Math.Max(starts[nodeIndex - 1], start);
        }

        var fragments = new List<IReadOnlyList<ComptimeSyntaxToken>>(nodes.Count);
        for (var index = 0; index < starts.Length; index++)
        {
            var end = index + 1 < starts.Length ? starts[index + 1] : tokens.Count;
            fragments.Add(tokens.Skip(starts[index]).Take(end - starts[index]).ToArray());
        }

        return fragments;
    }

    private sealed record SyntaxParserTokenLayout(
        string SourceText,
        IReadOnlyList<Token> ParserTokens,
        IReadOnlyList<SyntaxTokenPosition> TokenPositions);

    internal readonly record struct SyntaxTokenPosition(
        int Index,
        int Start,
        int End,
        SyntaxKind Kind);

    private static ComptimeSyntaxOrigin CreateOrigin(QuoteExpr quote, ComptimeEvaluationContext context)
    {
        var sourceUri = MetaComptimeIntrinsics.CreatePublicSourceUri(quote.Span, context.Meta?.SymbolTable);
        return new ComptimeSyntaxOrigin(
            sourceUri,
            quote.Span.Position,
            quote.Span.Location.Line,
            quote.Span.Location.Column,
            quote.Span.Length,
            context.Meta?.ExpansionTrace ?? string.Empty);
    }

    private static string CreateHygieneIdentity(
        QuoteExpr quote,
        ComptimeSyntaxOrigin origin,
        SyntaxSchemaEntry entry)
    {
        var material = $"quote:{SyntaxSchema.Version}:{entry.SourceName}:{origin.CanonicalText}:{quote.Parts.Count}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private static void AddGenerated(
        List<ComptimeSyntaxToken> tokens,
        SyntaxKind kind,
        string terminalName,
        string spelling,
        string leadingTrivia,
        SourceSpan span,
        ComptimeSyntaxIdentity? identity)
    {
        tokens.Add(new ComptimeSyntaxToken(
            kind,
            terminalName,
            kind.IsPunctuation() ? TerminalFlag.IsPunctuation : TerminalFlag.None,
            spelling,
            leadingTrivia,
            string.Empty,
            span,
            identity));
    }

    private static string QuoteString(string value) =>
        $"\"{Escape(value, '\"')}\"";

    private static string QuoteChar(char value) =>
        $"'{Escape(value.ToString(), '\'')}'";

    private static string Escape(string value, char quote)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(ch switch
            {
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '\0' => "\\0",
                _ when ch == quote => $"\\{quote}",
                _ => ch.ToString()
            });
        }
        return builder.ToString();
    }
}

internal sealed record ComptimeSyntaxParseArtifacts(
    string SourceText,
    IReadOnlyList<ComptimeSyntaxEvaluator.SyntaxTokenPosition> TokenPositions,
    IReadOnlyList<EidosAstNode> Nodes,
    IReadOnlyList<Diagnostic.Diagnostic> Diagnostics);
