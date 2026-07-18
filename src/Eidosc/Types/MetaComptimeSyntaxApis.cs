using System.Globalization;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Parsing.Lexer;
using Eidosc.Symbols;
using Eidosc.Syntax;
using Eidosc.Utils;

namespace Eidosc.Types;

internal static partial class MetaComptimeIntrinsics
{
    private static bool TryCreateIdentifier(
        IReadOnlyList<ComptimeValue> arguments,
        SourceSpan invocationSpan,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 2 ||
            arguments[0] is not ComptimeStringValue spelling ||
            arguments[1] is not ComptimeTypeValue category)
        {
            return Fail(
                "meta.identifier expects (String, meta.IdentifierCategory)",
                out value,
                out reason);
        }

        if (ReservedInternalNames.TryMatch(spelling.Value, out var reservedPrefix))
        {
            return Fail(
                $"meta.identifier cannot create reserved name '{spelling.Value}' with prefix '{reservedPrefix}'",
                out value,
                out reason);
        }

        if (!TryValidateIdentifierSpelling(spelling.Value, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        var categoryName = category.TypeRef.Name;
        if (!IsIdentifierCategoryName(categoryName) ||
            !MatchesIdentifierCategoryCasing(spelling.Value, categoryName))
        {
            return Fail(
                $"identifier '{spelling.Value}' does not satisfy meta.IdentifierCategory.{categoryName}",
                out value,
                out reason);
        }

        if (HasTargetScopeCollision(meta, spelling.Value, categoryName, out var collision))
        {
            return Fail(
                $"meta.identifier cannot create '{spelling.Value}' because target scope already contains {collision}",
                out value,
                out reason);
        }

        var targetIdentity = TryGetTargetDeclaration(meta.DeriveInput, out var target)
            ? target.StableIdentity
            : "no-target";
        var stableIdentity = Hash(
            $"identifier|{WellKnownStrings.Meta.SchemaVersion}|{targetIdentity}|" +
            $"{categoryName}|{spelling.Value}|{invocationSpan.Position}|{meta.ExpansionTrace}");
        value = Obj(
            "identifier",
            ("spelling", spelling),
            ("category", new ComptimeStringValue(categoryName)),
            ("identity", new ComptimeStringValue(stableIdentity)),
            ("mode", new ComptimeStringValue("public"))) with
        {
            StaticType = MetaSchemaRegistry.MetaType(
                WellKnownStrings.Meta.Types.Identifier,
                WellKnownTypeIds.MetaIdentifierId)
        };
        reason = string.Empty;
        return true;
    }

    private static bool TryCreateSite(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 1 ||
            !TryGetSiteBoundary(arguments[0], out var boundary, out var category, out var span))
        {
            return Fail("meta.site_of expects a target or declaration handle", out value, out reason);
        }

        value = CreateSiteValue(boundary, category, span, meta.SymbolTable, []);
        reason = string.Empty;
        return true;
    }

    internal static ComptimeMetaObjectValue CreateSiteValue(
        ComptimeDeclValue boundary,
        string category,
        SourceSpan span,
        SymbolTable symbolTable,
        IReadOnlyList<IReadOnlyList<Symbol>> scopeLayers)
    {
        var layers = scopeLayers.Select((symbols, depth) => (ComptimeValue)Obj(
            "scope-layer",
            ("depth", new ComptimeIntegerValue(depth)),
            ("declarations", new ComptimeSequenceValue(
                ComptimeSequenceKind.List,
                symbols
                    .DistinctBy(static symbol => symbol.Id)
                    .OrderBy(symbol => CreateStableIdentity(symbol, symbolTable), StringComparer.Ordinal)
                    .Select(symbol => (ComptimeValue)CreateDeclValue(symbol, symbolTable))
                    .ToArray())))).ToArray();
        return Obj(
            "site",
            ("boundary", boundary),
            ("category", new ComptimeStringValue(category)),
            ("span", CreateSpan(span, symbolTable)),
            ("scopeLayers", new ComptimeSequenceValue(ComptimeSequenceKind.List, layers)),
            ("identity", new ComptimeStringValue(Hash(
                $"site|{boundary.StableIdentity}|{category}|{span.Position}|{span.Length}")))) with
        {
            StaticType = MetaSchemaRegistry.MetaType(
                WellKnownStrings.Meta.Types.Site,
                WellKnownTypeIds.MetaSiteId)
        };
    }

    private static bool TryResolveAt(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 2 ||
            arguments[0] is not ComptimeMetaObjectValue { SchemaKind: "site" } site ||
            arguments[1] is not ComptimeStringValue name ||
            !site.TryGet("boundary", out var boundaryValue) ||
            boundaryValue is not ComptimeDeclValue boundary)
        {
            return Fail("meta.resolve_at expects (meta.Site[K], String)", out value, out reason);
        }

        if (!TryValidateIdentifierSpelling(name.Value, out var identifierReason))
        {
            value = CreateResultFailure("NotFound", identifierReason, WellKnownStrings.Meta.Types.ResolveFailure);
            reason = string.Empty;
            return true;
        }

        var candidates = new HashSet<SymbolId>();
        var hasLexicalCandidates = AddLexicalSiteCandidates(site, name.Value, candidates);
        if (meta.SymbolTable.GetSymbol(boundary.SymbolId) is { } boundarySymbol &&
            !hasLexicalCandidates &&
            string.Equals(boundarySymbol.Name, name.Value, StringComparison.Ordinal))
        {
            candidates.Add(boundarySymbol.Id);
        }

        if (!hasLexicalCandidates)
        {
            AddDirectSiteMemberCandidates(boundary.SymbolId, name.Value, meta.SymbolTable, candidates);
        }

        if (!hasLexicalCandidates &&
            meta.SymbolTable.Modules.TryGetOwningModule(boundary.SymbolId, out var module))
        {
            foreach (var binding in meta.SymbolTable.Modules.GetAccessibleBindingsByName(
                         module.Id,
                         name.Value,
                         module.Id))
            {
                candidates.Add(binding.SymbolId);
            }
        }

        var resolved = candidates
            .Select(meta.SymbolTable.GetSymbol)
            .Where(static symbol => symbol != null)
            .Select(static symbol => symbol!)
            .OrderBy(symbol => CreateStableIdentity(symbol, meta.SymbolTable), StringComparer.Ordinal)
            .ToArray();
        if (resolved.Length == 0)
        {
            value = CreateResultFailure(
                "NotFound",
                $"'{name.Value}' was not found at site '{boundary.StableIdentity}'",
                WellKnownStrings.Meta.Types.ResolveFailure);
            reason = string.Empty;
            return true;
        }

        if (resolved.Length > 1)
        {
            value = CreateResultFailure(
                "Ambiguous",
                $"'{name.Value}' is ambiguous at site '{boundary.StableIdentity}'",
                WellKnownStrings.Meta.Types.ResolveFailure);
            reason = string.Empty;
            return true;
        }

        value = CreateResultSuccess(CreateDeclValue(resolved[0], meta.SymbolTable));
        reason = string.Empty;
        return true;
    }

    private static bool AddLexicalSiteCandidates(
        ComptimeMetaObjectValue site,
        string name,
        ISet<SymbolId> candidates)
    {
        if (!site.TryGet("scopeLayers", out var layerValue) ||
            layerValue is not ComptimeSequenceValue layers)
        {
            return false;
        }

        foreach (var layerValueEntry in layers.Elements)
        {
            if (layerValueEntry is not ComptimeMetaObjectValue { SchemaKind: "scope-layer" } layer ||
                !layer.TryGet("declarations", out var declarationsValue) ||
                declarationsValue is not ComptimeSequenceValue declarations)
            {
                continue;
            }

            foreach (var declaration in declarations.Elements.OfType<ComptimeDeclValue>())
            {
                if (string.Equals(declaration.Name, name, StringComparison.Ordinal))
                {
                    candidates.Add(declaration.SymbolId);
                }
            }

            if (candidates.Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void AddDirectSiteMemberCandidates(
        SymbolId boundaryId,
        string name,
        SymbolTable symbolTable,
        ISet<SymbolId> candidates)
    {
        var memberIds = symbolTable.GetSymbol(boundaryId) switch
        {
            AdtSymbol type => type.Fields
                .Concat(type.DirectCases)
                .Concat(type.Constructors)
                .Concat(type.TypeParams),
            TraitSymbol trait => trait.Methods
                .Concat(trait.AssociatedTypes)
                .Concat(trait.AssociatedConsts)
                .Concat(trait.TypeParams),
            ImplSymbol implementation => implementation.Methods
                .Concat(implementation.AssociatedTypes)
                .Concat(implementation.AssociatedConsts),
            FuncSymbol function => function.Parameters.Concat(function.TypeParams),
            CtorSymbol constructor => constructor.NamedFields.Concat(constructor.TypeParams),
            _ => []
        };

        foreach (var memberId in memberIds)
        {
            if (symbolTable.GetSymbol(memberId) is { } member &&
                string.Equals(member.Name, name, StringComparison.Ordinal))
            {
                candidates.Add(member.Id);
            }
        }
    }

    private static bool TryCreateOrigin(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 1 || !TryGetOrigin(arguments[0], meta, out var origin))
        {
            return Fail("meta.origin_of expects a value with source origin", out value, out reason);
        }

        value = CreateOriginValue(origin);
        reason = string.Empty;
        return true;
    }

    private static bool TryParseTextSyntax(
        IReadOnlyList<ComptimeValue> arguments,
        QuoteKind kind,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 2 ||
            arguments[0] is not ComptimeStringValue source ||
            arguments[1] is not ComptimeMetaObjectValue { SchemaKind: "origin" } originValue ||
            !TryReadOrigin(originValue, out var origin))
        {
            return Fail(
                $"meta.{(kind == QuoteKind.Items ? "parse_items" : "parse_expr")} expects (String, meta.Origin)",
                out value,
                out reason);
        }

        if (!TryLexTextSyntax(source.Value, origin, out var tokens, out var trailingTrivia, out var lexReason))
        {
            value = CreateResultFailure("ParseError", lexReason, WellKnownStrings.Meta.Types.ParseFailure);
            reason = string.Empty;
            return true;
        }

        var schema = SyntaxSchema.Get(kind);
        if (!ComptimeSyntaxEvaluator.TryParseFragments(
                schema,
                tokens,
                trailingTrivia,
                origin.SourceUri,
                out var fragments,
                out _,
                out var parseReason))
        {
            value = CreateResultFailure("ParseError", parseReason, WellKnownStrings.Meta.Types.ParseFailure);
            reason = string.Empty;
            return true;
        }

        if (!context.Resources.TryConsumeSyntaxNodes(tokens.Count, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        var hygiene = Hash(
            $"parse|{SyntaxSchema.Version}|{schema.SourceName}|{origin.CanonicalText}|" +
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(source.Value))).ToLowerInvariant());
        ComptimeValue syntaxResult;
        if (schema.Cardinality == SyntaxCardinality.Sequence)
        {
            syntaxResult = new ComptimeSequenceValue(
                ComptimeSequenceKind.List,
                fragments.Select((fragment, index) => (ComptimeValue)new ComptimeSyntaxValue(
                    schema.Category,
                    fragment,
                    index == fragments.Count - 1 ? trailingTrivia : string.Empty,
                    origin,
                    $"{hygiene}:fragment:{index.ToString(CultureInfo.InvariantCulture)}")).ToArray());
        }
        else
        {
            syntaxResult = new ComptimeSyntaxValue(schema.Category, tokens, trailingTrivia, origin, hygiene);
        }

        value = CreateResultSuccess(syntaxResult);
        reason = string.Empty;
        return true;
    }

    private static bool TryLexTextSyntax(
        string source,
        ComptimeSyntaxOrigin origin,
        out IReadOnlyList<ComptimeSyntaxToken> syntaxTokens,
        out string trailingTrivia,
        out string reason)
    {
        var sourcePath = string.IsNullOrWhiteSpace(origin.SourceUri)
            ? "eidos-generated://meta/parsed.eidos"
            : origin.SourceUri;
        var (grammar, scanner) = LexerTableBuilder.Build();
        var stream = new SourceStream(source, 4, new SourceLocation(0, 0, 0, sourcePath));
        var lexer = new LexerContext(stream, scanner, grammar.Terminals);
        Scanner.Init(lexer);
        var result = new List<ComptimeSyntaxToken>();
        var cursor = 0;
        var ordinal = 0;
        Token? token;
        while ((token = Scanner.GetToken(lexer)) != null)
        {
            if (token is ErrorToken error)
            {
                syntaxTokens = [];
                trailingTrivia = string.Empty;
                reason = error.Message;
                return false;
            }

            var start = Math.Clamp(token.Location.Position, cursor, source.Length);
            var end = Math.Clamp(start + token.Length, start, source.Length);
            var leadingTrivia = source[cursor..start];
            var spelling = source[start..end];
            var kind = token is ContentToken content ? content.Kind : SyntaxKind.Comment;
            var terminalName = token is ContentToken terminal ? terminal.Terminal.DebugName : "comment";
            var terminalFlags = token is ContentToken flags ? flags.Terminal.Flags : TerminalFlag.None;
            var identity = kind is SyntaxKind.Identifier or SyntaxKind.OperatorIdentifier
                ? new ComptimeSyntaxIdentity(
                    ComptimeSyntaxIdentityKind.Hygiene,
                    Hash($"parsed-token|{origin.CanonicalText}|{ordinal}|{spelling}"),
                    SymbolId.None,
                    TypeId.None)
                : null;
            result.Add(new ComptimeSyntaxToken(
                kind,
                terminalName,
                terminalFlags,
                spelling,
                leadingTrivia,
                string.Empty,
                new SourceSpan(
                    new SourceLocation(
                        origin.Position + start,
                        origin.Line + token.Location.Line,
                        token.Location.Line == 0 ? origin.Column + token.Location.Column : token.Location.Column,
                        sourcePath),
                    token.Length),
                identity));
            cursor = end;
            ordinal++;
        }

        syntaxTokens = result;
        trailingTrivia = source[cursor..];
        reason = string.Empty;
        return true;
    }

    private static bool TryGetOrigin(
        ComptimeValue source,
        MetaComptimeContext meta,
        out ComptimeSyntaxOrigin origin)
    {
        switch (source)
        {
            case ComptimeSyntaxValue syntax:
                origin = syntax.Origin;
                return true;
            case ComptimeTokensValue tokens:
                origin = tokens.Origin;
                return true;
            case ComptimeDeclValue declaration:
                origin = CreateSyntaxOrigin(declaration.Span, meta);
                return true;
            case ComptimeMetaObjectValue { SchemaKind: "span" } spanValue when
                TryReadSpan(spanValue, out var span):
                origin = CreateSyntaxOrigin(span, meta);
                return true;
            case ComptimeMetaObjectValue target when
                target.TryGet("span", out var targetSpan) &&
                targetSpan is ComptimeMetaObjectValue { SchemaKind: "span" } spanObject &&
                TryReadSpan(spanObject, out var span):
                origin = CreateSyntaxOrigin(span, meta);
                return true;
            default:
                origin = null!;
                return false;
        }
    }

    private static ComptimeSyntaxOrigin CreateSyntaxOrigin(SourceSpan span, MetaComptimeContext meta) => new(
        CreatePublicSourceUri(span, meta.SymbolTable),
        span.Position,
        span.Location.Line,
        span.Location.Column,
        span.Length,
        meta.ExpansionTrace ?? string.Empty);

    private static ComptimeMetaObjectValue CreateOriginValue(ComptimeSyntaxOrigin origin) => Obj(
        "origin",
        ("source", new ComptimeStringValue(origin.SourceUri)),
        ("position", new ComptimeIntegerValue(origin.Position)),
        ("line", new ComptimeIntegerValue(origin.Line)),
        ("column", new ComptimeIntegerValue(origin.Column)),
        ("length", new ComptimeIntegerValue(origin.Length)),
        ("trace", new ComptimeStringValue(origin.ExpansionTrace))) with
    {
        StaticType = MetaSchemaRegistry.MetaType(
            WellKnownStrings.Meta.Types.Origin,
            WellKnownTypeIds.MetaOriginId)
    };

    private static bool TryReadOrigin(
        ComptimeMetaObjectValue value,
        out ComptimeSyntaxOrigin origin)
    {
        if (!value.TryGet("source", out var source) || source is not ComptimeStringValue sourceText ||
            !value.TryGet("position", out var position) || position is not ComptimeIntegerValue positionValue ||
            !value.TryGet("line", out var line) || line is not ComptimeIntegerValue lineValue ||
            !value.TryGet("column", out var column) || column is not ComptimeIntegerValue columnValue ||
            !value.TryGet("length", out var length) || length is not ComptimeIntegerValue lengthValue ||
            !value.TryGet("trace", out var trace) || trace is not ComptimeStringValue traceText ||
            !TryInt32(positionValue.Value, out var positionInt) ||
            !TryInt32(lineValue.Value, out var lineInt) ||
            !TryInt32(columnValue.Value, out var columnInt) ||
            !TryInt32(lengthValue.Value, out var lengthInt))
        {
            origin = null!;
            return false;
        }

        origin = new ComptimeSyntaxOrigin(
            sourceText.Value,
            positionInt,
            lineInt,
            columnInt,
            lengthInt,
            traceText.Value);
        return true;
    }

    private static bool TryGetSiteBoundary(
        ComptimeValue source,
        out ComptimeDeclValue boundary,
        out string category,
        out SourceSpan span)
    {
        if (source is ComptimeDeclValue declaration)
        {
            boundary = declaration;
            category = "item.declaration";
            span = declaration.Span;
            return true;
        }

        if (source is ComptimeMetaObjectValue target &&
            TryGetTargetDeclaration(target, out boundary))
        {
            category = target.TryGet("category", out var categoryValue) &&
                       categoryValue is ComptimeStringValue categoryText
                ? categoryText.Value
                : "item.declaration";
            span = target.TryGet("span", out var spanValue) &&
                   spanValue is ComptimeMetaObjectValue { SchemaKind: "span" } spanObject &&
                   TryReadSpan(spanObject, out var targetSpan)
                ? targetSpan
                : boundary.Span;
            return true;
        }

        boundary = null!;
        category = string.Empty;
        span = SourceSpan.Empty;
        return false;
    }

    private static bool TryGetTargetDeclaration(
        ComptimeMetaObjectValue? target,
        out ComptimeDeclValue declaration)
    {
        if (target != null &&
            target.TryGet("targetDecl", out var declarationValue) &&
            declarationValue is ComptimeDeclValue targetDeclaration)
        {
            declaration = targetDeclaration;
            return true;
        }

        declaration = null!;
        return false;
    }

    private static bool HasTargetScopeCollision(
        MetaComptimeContext meta,
        string name,
        string category,
        out string collision)
    {
        collision = string.Empty;
        if (!TryGetTargetDeclaration(meta.DeriveInput, out var target))
        {
            return false;
        }

        if (meta.DeclarationDefinitions.TryGetValue(target.SymbolId, out var targetDeclaration))
        {
            Eidosc.Ast.EidosAstNode? targetMember = category switch
            {
                "Field" when targetDeclaration is AdtDef adt =>
                    adt.Fields.FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.Ordinal)),
                "Field" when targetDeclaration is CaseTypeDef caseType =>
                    caseType.Fields.FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.Ordinal)),
                "Type" when targetDeclaration is AdtDef adt =>
                    adt.Cases.FirstOrDefault(caseType => string.Equals(caseType.Name, name, StringComparison.Ordinal)),
                "Type" when targetDeclaration is CaseTypeDef caseType =>
                    caseType.Cases.FirstOrDefault(child => string.Equals(child.Name, name, StringComparison.Ordinal)),
                _ => null
            };
            if (targetMember != null)
            {
                collision = $"{targetMember.GetType().Name} '{name}'";
                return true;
            }
        }

        if (!meta.SymbolTable.Modules.TryGetOwningModule(target.SymbolId, out var module))
        {
            return false;
        }

        var bindings = meta.SymbolTable.Modules.GetAccessibleBindingsByName(module.Id, name, module.Id);
        var binding = bindings.FirstOrDefault(candidate =>
        {
            var candidateSymbol = meta.SymbolTable.GetSymbol(candidate.SymbolId);
            if (candidateSymbol == null || IsOutputOfCurrentInvocation(candidateSymbol, meta))
            {
                return false;
            }

            return category != "Function" || candidateSymbol is not FuncSymbol;
        });
        if (binding == null || !binding.SymbolId.IsValid)
        {
            return false;
        }

        var symbol = meta.SymbolTable.GetSymbol(binding.SymbolId);
        collision = symbol == null ? $"symbol {binding.SymbolId.Value}" : $"{symbol.Kind} '{symbol.Name}'";
        return true;
    }

    private static bool IsOutputOfCurrentInvocation(Symbol symbol, MetaComptimeContext meta)
    {
        var origin = symbol.GeneratedOrigin;
        return origin != null &&
               meta.GeneratorSymbolId.IsValid &&
               origin.TargetSymbolId == meta.Access.TargetSymbolId &&
               origin.GeneratorSymbolId == meta.GeneratorSymbolId &&
               string.Equals(
                   origin.ClauseOccurrenceIdentity,
                   meta.InvocationOccurrenceIdentity,
                   StringComparison.Ordinal);
    }

    private static bool TryValidateIdentifierSpelling(string spelling, out string reason)
    {
        if (string.IsNullOrWhiteSpace(spelling))
        {
            reason = "identifier spelling cannot be empty";
            return false;
        }

        var (grammar, scanner) = LexerTableBuilder.Build();
        var stream = new SourceStream(spelling, 4, new SourceLocation(0, 0, 0, "eidos-generated://meta/identifier"));
        var lexer = new LexerContext(stream, scanner, grammar.Terminals);
        Scanner.Init(lexer);
        var tokens = new List<Token>();
        Token? token;
        while ((token = Scanner.GetToken(lexer)) != null)
        {
            tokens.Add(token);
        }

        if (tokens is not [ContentToken { Kind: SyntaxKind.Identifier or SyntaxKind.OperatorIdentifier } identifier] ||
            identifier.Location.Position != 0 ||
            identifier.Length != spelling.Length)
        {
            reason = $"'{spelling}' is not one complete Eidos identifier";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool IsIdentifierCategoryName(string category) => category is
        "Item" or "Member" or "Value" or "Type" or "Function" or "Field" or "Constructor" or
        "Parameter" or "Local" or "Module" or "AssociatedType" or "AssociatedConst";

    private static bool MatchesIdentifierCategoryCasing(string spelling, string category)
    {
        if (spelling.Length == 0)
        {
            return false;
        }

        return category switch
        {
            "Type" or "Constructor" or "Module" or "AssociatedType" => char.IsUpper(spelling[0]),
            "AssociatedConst" => IsScreamingSnakeCase(spelling),
            "Value" or "Function" or "Field" or "Parameter" or "Local" => char.IsLower(spelling[0]),
            _ => char.IsLetter(spelling[0])
        };
    }

    private static bool IsScreamingSnakeCase(string spelling) =>
        spelling.Length > 0 &&
        spelling.All(static character =>
            character == '_' || char.IsDigit(character) || char.IsUpper(character)) &&
        spelling.Any(static character => char.IsUpper(character));

    private static ComptimeAdtValue CreateResultSuccess(ComptimeValue success) => new(
        SymbolId.None,
        "Ok",
        [success],
        []);

    private static ComptimeAdtValue CreateResultFailure(
        string failureKind,
        string message,
        string failureType) => new(
        SymbolId.None,
        "Err",
        [new ComptimeAdtValue(
            SymbolId.None,
            failureKind,
            [new ComptimeStringValue(message)],
            []) with
        {
            StaticType = MetaSchemaRegistry.MetaType(
                failureType,
                failureType == WellKnownStrings.Meta.Types.ParseFailure
                    ? WellKnownTypeIds.MetaParseFailureId
                    : WellKnownTypeIds.MetaResolveFailureId)
        }],
        []);

    private static bool TryInt32(long value, out int result)
    {
        if (value is < int.MinValue or > int.MaxValue)
        {
            result = 0;
            return false;
        }

        result = (int)value;
        return true;
    }
}
