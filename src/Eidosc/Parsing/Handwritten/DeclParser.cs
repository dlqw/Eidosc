using System.Text;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Diagnostic;
using Eidosc.Semantic;
using Eidosc.Syntax;
using Eidosc.Utils;
using AstAttribute = Eidosc.Ast.Attribute;

namespace Eidosc.Parsing.Handwritten;

public sealed class DeclParser(ParserContext ctx)
{
    private readonly PatternParser _patternParser = new(ctx);
    private readonly TypeParser _typeParser = new(ctx);
    private readonly ExprParser _exprParser = new(ctx, new PatternParser(ctx), new TypeParser(ctx));

    public ExprParser ExprParser => _exprParser;
    public PatternParser PatternParser => _patternParser;
    public TypeParser TypeParser => _typeParser;

    public IReadOnlyList<EidosAstNode> ParseTypeMemberFragments()
    {
        var members = new List<EidosAstNode>();
        var fieldNames = new HashSet<string>(StringComparer.Ordinal);
        while (!ctx.IsEof)
        {
            if (ctx.Match(","))
            {
                continue;
            }

            var start = ctx.Position;
            if (ctx.Check("expand"))
            {
                members.Add(ParseExpandDeclaration(SyntaxCategory.Member));
            }
            else if (IsClosedCaseLookahead())
            {
                members.Add(ParseCaseType(fieldNames));
            }
            else if (IsNewFieldLookahead())
            {
                var field = ParseField("::");
                if (!fieldNames.Add(field.Name))
                {
                    ctx.Error($"duplicate field member '{field.Name}'", field.Span.Location);
                }
                members.Add(field);
            }
            else
            {
                ctx.Error("expected a field declaration 'name :: Type' or a case type 'Case :: type'");
                ctx.Advance();
            }

            ctx.Match(",");
            if (ctx.Position == start)
            {
                ctx.Advance();
            }
        }
        return members;
    }

    public IReadOnlyList<EidosAstNode> ParseTraitMemberFragments() =>
        ParseAssociatedMemberFragments(requireAssociatedValues: false);

    public IReadOnlyList<EidosAstNode> ParseInstanceMemberFragments() =>
        ParseAssociatedMemberFragments(requireAssociatedValues: true);

    private IReadOnlyList<EidosAstNode> ParseAssociatedMemberFragments(bool requireAssociatedValues)
    {
        var members = new List<EidosAstNode>();
        while (!ctx.IsEof)
        {
            if (ctx.Match(",") || ctx.Match(";"))
            {
                continue;
            }

            var start = ctx.Position;
            var attributes = ParseAttributes();
            members.Add(ctx.Check("expand")
                ? ParseExpandDeclaration(SyntaxCategory.Member)
                : ParseNameFirstMember(attributes, requireAssociatedValues));
            ctx.Match(";");
            if (ctx.Position == start)
            {
                ctx.Advance();
            }
        }
        return members;
    }

    public List<EidosAstNode> ParseProgram()
    {
        var nodes = new List<EidosAstNode>();
        while (!ctx.IsEof)
        {
            var pos = ctx.Position;
            nodes.Add(ParseTopLevel());
            if (ctx.Position == pos)
            {
                ctx.Error(DiagnosticMessages.ParserUnexpectedToken(ctx.GetText()));
                ctx.Advance();
            }
        }
        return nodes;
    }

    public EidosAstNode ParseTopLevel()
    {
        var attrs = ParseAttributes();
        var isExport = ctx.Match("export");

        var text = ctx.GetText();
        if (ctx.IsNameFirstSyntax && text == WellKnownStrings.Keywords.LegacyAbility)
        {
            ctx.Error(DiagnosticMessages.ParserLegacyEffectSyntaxRemoved);
            return ParseEffectDef(attrs, isExport);
        }

        if (ctx.IsNameFirstSyntax && IsNameFirstDeclarationStart())
        {
            return ParseNameFirstDeclaration(attrs, isExport);
        }

        return text switch
        {
            "expand"  => ParseExpandDeclaration(SyntaxCategory.Item),
            "module"  => ParseModuleDef(attrs, isExport),
            "func"    => ParseFuncDef(attrs, isExport),
            "let"     => ctx.CheckPeek(1, "?") ? ParseLetQuestionDecl(attrs) : ParseLetDecl(attrs, isExport),
            "type"    => ParseTypeDef(attrs, isExport),
            "ability" => ParseEffectDef(attrs, isExport),
            "trait"   => ParseTraitDef(attrs, isExport),
            // "proof" removed during migration
            "import"  => ParseImportDecl(attrs, isExport),
            _ => _exprParser.ParseExpr()
        };
    }

    private ExpandDeclaration ParseExpandDeclaration(SyntaxCategory category)
    {
        var start = ctx.Current;
        ctx.Expect("expand");
        var invocation = MetaInvocationSyntaxParser.Parse(
            ctx,
            () => _exprParser.ParseExpr(),
            $"{category.ToString().ToLowerInvariant()} expand");
        ctx.Expect(";");

        var expansion = new ExpandDeclaration();
        expansion.SetInvocation(invocation);
        expansion.SetSiteCategory(category);
        expansion.SetSpan(ctx.SpanFrom(start));
        return expansion;
    }

    private bool IsNameFirstDeclarationStart()
    {
        if (IsNameFirstOperatorDeclarationStart())
        {
            return true;
        }

        if (!TokenKind.IsAnyIdentifier(ctx.Current))
        {
            return false;
        }

        var depth = 0;
        for (var offset = 1; offset < 64; offset++)
        {
            var token = ctx.Peek(offset);
            if (token is EofToken)
            {
                return false;
            }

            var text = ctx.GetText(token);
            if (depth == 0 && text == "::")
            {
                return true;
            }

            if (depth == 0 && text is ";" or "{" or "}" or "=>")
            {
                return false;
            }

            depth += text switch
            {
                "[" or "(" => 1,
                "]" or ")" => -1,
                _ => 0
            };
            depth = Math.Max(0, depth);
        }

        return false;
    }

    private bool IsNameFirstOperatorDeclarationStart()
    {
        return ctx.Check("(") &&
               TokenKind.IsOperatorIdentifier(ctx.Peek(1)) &&
               ctx.CheckPeek(2, ")") &&
               ctx.CheckPeek(3, "::");
    }

    private Declaration ParseNameFirstDeclaration(List<AstAttribute> attrs, bool isExport)
    {
        var startToken = ctx.Current;
        var isOperatorDeclaration = IsNameFirstOperatorDeclarationStart();
        var name = isOperatorDeclaration ? ParseFunctionName() : ctx.GetText();
        var modulePath = new List<string> { name };
        if (!isOperatorDeclaration)
        {
            ctx.Advance();

            while (ctx.Match("."))
            {
                modulePath.Add(ctx.GetText());
                ctx.Advance();
            }
        }

        var typeParams = !isOperatorDeclaration && modulePath.Count == 1
            ? _typeParser.TryParseTypeParams()
            : null;

        ctx.Expect("::");

        if (ctx.Match("import"))
        {
            return ParseNameFirstImportBinding(attrs, isExport, startToken, name);
        }

        if (ctx.Match("module"))
        {
            return ParseNameFirstModule(attrs, isExport, startToken, modulePath);
        }

        if (ctx.Match("type"))
        {
            return ParseNameFirstType(attrs, isExport, startToken, name, typeParams ?? []);
        }

        if (ctx.Match("trait"))
        {
            return ParseNameFirstTrait(attrs, isExport, startToken, name, typeParams ?? []);
        }

        if (ctx.Match(WellKnownStrings.Keywords.Effect))
        {
            return ParseNameFirstEffect(attrs, isExport, startToken, name, typeParams ?? []);
        }

        if (ctx.Match(WellKnownStrings.Keywords.LegacyAbility))
        {
            ctx.Error(DiagnosticMessages.ParserLegacyEffectSyntaxRemoved);
            return ParseNameFirstEffect(attrs, isExport, startToken, name, typeParams ?? []);
        }

        if (ctx.Match("instance"))
        {
            return ParseNameFirstInstance(attrs, isExport, startToken, name, typeParams ?? []);
        }

        if (ctx.Match("comptime"))
        {
            if (CanParseComptimeFunctionSignature())
            {
                var comptimeSignature = _typeParser.ParseType();
                return ParseNameFirstFuncDefAfterSignature(
                    attrs,
                    startToken,
                    name,
                    typeParams ?? [],
                    comptimeSignature,
                    isExport,
                    isComptime: true);
            }

            if (typeParams is null || typeParams.Count == 0)
            {
                var clauses = ParseDeclarationClauseZone([]);
                if (clauses.Count > 0)
                {
                    ctx.Expect("=");
                    var clauseTargetValue = _exprParser.ParseExpr();
                    ctx.Match(";");
                    return CreateNameFirstValueBinding(
                        attrs,
                        isExport,
                        startToken,
                        name,
                        typeAnnotation: null,
                        value: clauseTargetValue,
                        isComptime: true,
                        clauses: clauses);
                }

                var value = _exprParser.ParseExpr();
                ctx.Match(";");
                return CreateNameFirstValueBinding(attrs, isExport, startToken, name, typeAnnotation: null, value, isComptime: true);
            }

            ctx.Error("comptime generic static value bindings are not supported in 0.6.0-alpha.1 phase 1.");
        }

        if ((typeParams is null || typeParams.Count == 0) && CanStartNameFirstValueExpr(ctx.Current))
        {
            var value = _exprParser.ParseExpr();
            ctx.Match(";");
            return CreateNameFirstValueBinding(attrs, isExport, startToken, name, typeAnnotation: null, value);
        }

        var signature = _typeParser.ParseType();
        var signatureClauses = new List<DeclarationClause>();
        AddSignatureNeedClause(signature, signatureClauses);
        var requiredAbilities = ParseFunctionClauseGroup(typeParams ?? [], signatureClauses);
        if ((typeParams is null || typeParams.Count == 0) && ctx.Match("="))
        {
            if (ctx.Check(";") && PrecompiledModuleRegistry.IsStdlibSourcePath(ctx.SourcePath))
            {
                ctx.Advance();
                return CreateNameFirstValueBinding(
                    attrs,
                    isExport,
                    startToken,
                    name,
                    signature,
                    value: null,
                    clauses: signatureClauses);
            }

            var value = _exprParser.ParseExpr();
            ctx.Match(";");
            return CreateNameFirstValueBinding(attrs, isExport, startToken, name, signature, value, clauses: signatureClauses);
        }

        return ParseTopLevelNameFirstCallableAfterSignature(
            attrs,
            startToken,
            name,
            typeParams ?? [],
            signature,
            signatureClauses,
            requiredAbilities,
            isExport);
    }

    private List<AstAttribute> ParseAttributes()
    {
        var attrs = new List<AstAttribute>();
        while (ctx.Check("@"))
        {
            if (ctx.SupportsTypedClauses && ctx.CheckPeek(1, "["))
            {
                attrs.AddRange(ParseTypedTagGroup());
                continue;
            }

            var startToken = ctx.Current;
            if (ctx.SupportsTypedClauses)
            {
                ctx.Error("standalone '@attribute' syntax was removed; use '@[...]' typed declaration tags");
            }
            ctx.Advance(); // consume "@"
            var name = ctx.GetText();
            ctx.Advance();

            var attr = new AstAttribute();
            attr.SetSpan(ctx.SpanFrom(startToken));
            attr.SetName(name);

            if (ctx.Check("("))
            {
                ctx.Advance();
                if (!ctx.Check(")"))
                {
                    attr.AddArgumentText(ReadAttributeArg());
                    while (ctx.Match(","))
                    {
                        if (ctx.Check(")")) break;
                        attr.AddArgumentText(ReadAttributeArg());
                    }
                }
                ctx.Expect(")");
            }

            if (!ctx.SupportsTypedClauses)
            {
                attrs.Add(attr);
            }
        }
        return attrs;
    }

    private IReadOnlyList<AstAttribute> ParseTypedTagGroup()
    {
        var tags = new List<AstAttribute>();
        ctx.Advance(); // consume "@"
        ctx.Expect("[");
        while (!ctx.Check("]") && !ctx.IsEof)
        {
            var start = ctx.Current;
            var keyword = ctx.GetText();
            if (!ClauseSchema.TryGet(keyword, out var spec) ||
                spec.Adapter != DeclarationAttachmentAdapterKind.TypedTag)
            {
                ctx.Error($"'{keyword}' is not a typed declaration tag");
                SkipTypedTag();
            }
            else
            {
                ctx.Advance();
                var clause = CreateClause(spec.Kind, keyword, start);
                var attribute = new AstAttribute();
                attribute.SetName(keyword);

                if (ctx.Match("("))
                {
                    if (spec.Arguments == ClauseArgumentGrammar.MetaInvocation)
                    {
                        var invocation = ParseMetaInvocationSyntax();
                        clause.SetMetaInvocation(invocation);
                        clause.AddArgument(invocation.GeneratorDisplayName);
                        attribute.AddArgumentText(invocation.GeneratorDisplayName);
                    }
                    else if (!ctx.Check(")"))
                    {
                        AddTagArgument();
                        while (ctx.Match(","))
                        {
                            if (ctx.Check(")"))
                            {
                                break;
                            }
                            AddTagArgument();
                        }
                    }
                    ctx.Expect(")");
                }

                clause.SetSpan(ctx.SpanFrom(start));
                attribute.SetSpan(clause.Span);
                attribute.SetTypedClause(clause);
                tags.Add(attribute);

                void AddTagArgument()
                {
                    var argument = ReadAttributeArg();
                    clause.AddArgument(argument);
                    attribute.AddArgumentText(argument);
                }
            }

            if (!ctx.Match(","))
            {
                break;
            }
        }
        ctx.Expect("]");
        return tags;

        void SkipTypedTag()
        {
            ctx.Advance();
            if (!ctx.Match("("))
            {
                return;
            }

            var depth = 1;
            while (!ctx.IsEof && depth > 0)
            {
                depth += ctx.GetText() switch
                {
                    "(" => 1,
                    ")" => -1,
                    _ => 0
                };
                ctx.Advance();
            }
        }
    }

    private string ReadAttributeArg()
    {
        var parts = new List<string>();
        int depth = 0;
        while (!ctx.IsEof)
        {
            var text = ctx.GetLiteralRawText();
            if (depth == 0 && (text == "," || text == ")"))
                break;
            if (text == "(" || text == "[" || text == "{")
                depth++;
            else if (text == ")" || text == "]" || text == "}")
                depth--;
            parts.Add(text);
            ctx.Advance();
        }
        return FormatAttributeArgument(parts);
    }

    private static string FormatAttributeArgument(IReadOnlyList<string> parts)
    {
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (part is "]" or ")" or "}" or "::" or "/" or "," || builder.Length == 0)
            {
                AppendCompact(part);
                continue;
            }

            var previous = builder[^1];
            if (char.IsWhiteSpace(previous) ||
                previous is '[' or '(' or '{' or ':' or '/' ||
                part is "[" or "(" or "{")
            {
                AppendCompact(part);
            }
            else
            {
                builder.Append(' ');
                builder.Append(part);
            }
        }

        return builder.ToString();

        void AppendCompact(string part)
        {
            if (part == ",")
            {
                builder.Append(", ");
                return;
            }

            if (part == "::" && builder.Length > 0 && builder[^1] == ' ')
            {
                builder.Length--;
            }

            builder.Append(part);
        }
    }

    private FuncDef ParseNameFirstFuncDef(
        List<AstAttribute> attrs,
        bool isExport,
        Token startToken,
        string name,
        List<TypeParam> typeParams)
    {
        var signature = _typeParser.ParseType();
        var clauses = new List<DeclarationClause>();
        AddSignatureNeedClause(signature, clauses);
        var requiredAbilities = ParseFunctionClauseGroup(typeParams, clauses);

        List<PatternBranch>? body = null;
        if (ctx.Check("{"))
        {
            ctx.Advance();
            body = ParsePatternBranches();
            ctx.Expect("}");
        }
        RejectPostBodyClauses();

        var func = new FuncDef();
        func.SetSpan(ctx.SpanFrom(startToken));
        func.SetName(name);
        func.SetTypeParams(typeParams);
        func.SetSignature(signature);
        func.SetRequiredAbilities(MergeSignatureEffects(signature, requiredAbilities));
        func.SetBody(body ?? []);
        func.SetAttributes(attrs);
        func.SetClauses(clauses);
        func.SetExported(isExport);
        return func;
    }

    private LetDecl CreateNameFirstValueBinding(
        List<AstAttribute> attrs,
        bool isExport,
        Token startToken,
        string name,
        TypeNode? typeAnnotation,
        EidosAstNode? value,
        bool isComptime = false,
        List<DeclarationClause>? clauses = null)
    {
        var pattern = new VarPattern();
        pattern.SetSpan(new SourceSpan(startToken.Location, startToken.Length));
        pattern.SetName(name);

        var decl = new LetDecl();
        decl.SetSpan(ctx.SpanFrom(startToken));
        decl.SetPattern(pattern);
        decl.SetTypeAnnotation(typeAnnotation);
        decl.SetComptime(isComptime);
        decl.SetValue(value);
        decl.SetAttributes(attrs);
        decl.SetClauses(clauses ?? []);
        decl.SetExported(isExport);
        return decl;
    }

    private bool CanStartNameFirstValueExpr(Token token)
    {
        if (TokenKind.IsAnyLiteral(token))
        {
            return true;
        }

        if (TokenKind.IsAnyIdentifier(token))
        {
            return !LooksLikeNameFirstSignatureOrTypedBinding();
        }

        if (TokenKind.IsOperator(token, "-") || TokenKind.IsOperator(token, "!"))
        {
            return true;
        }

        if (TokenKind.IsPunctuation(token, "("))
        {
            return !LooksLikeParenthesizedFunctionSignature() &&
                   !LooksLikeParenthesizedTypeAnnotation();
        }

        return TokenKind.IsPunctuation(token, "{") ||
               TokenKind.IsPunctuation(token, "[") ||
               TokenKind.IsKeyword(token, "if") ||
               TokenKind.IsKeyword(token, "match") ||
               TokenKind.IsKeyword(token, "handler") ||
               TokenKind.IsKeyword(token, "do") ||
               TokenKind.IsKeyword(token, "fn");
    }

    private bool LooksLikeNameFirstSignatureOrTypedBinding()
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        for (var offset = 0; offset < 256; offset++)
        {
            var token = ctx.Peek(offset);
            if (token is EofToken)
            {
                return false;
            }

            var text = ctx.GetText(token);
            switch (text)
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
            }

            if (parenDepth != 0 || bracketDepth != 0)
            {
                continue;
            }

            if (text is "->" or "=")
            {
                return true;
            }

            if (text is "where" or "need" or "ffi" or "extern" or "calling_convention" or
                "unwind" or "expand" or "internal" or "intrinsic" or "external")
            {
                return true;
            }

            if (text == "{")
            {
                return BraceContainsTopLevelFunctionBranch(offset);
            }

            if (text is ";" or "}")
            {
                return false;
            }
        }

        return false;
    }

    private bool BraceContainsTopLevelFunctionBranch(int openBraceOffset)
    {
        var depth = 0;
        for (var offset = openBraceOffset; offset < openBraceOffset + 256; offset++)
        {
            var token = ctx.Peek(offset);
            if (token is EofToken)
            {
                return false;
            }

            var text = ctx.GetText(token);
            if (text is "{" or "(" or "[")
            {
                depth++;
                continue;
            }

            if (text is "}" or ")" or "]")
            {
                depth--;
                if (depth <= 0)
                {
                    return false;
                }
                continue;
            }

            if (depth == 1 && text == "=>")
            {
                return true;
            }
        }

        return false;
    }

    private bool LooksLikeParenthesizedFunctionSignature()
    {
        if (!ctx.Check("("))
        {
            return false;
        }

        var depth = 0;
        for (var offset = 0; offset < 64; offset++)
        {
            var token = ctx.Peek(offset);
            if (token is EofToken)
            {
                return false;
            }

            var text = ctx.GetText(token);
            depth += text switch
            {
                "(" or "[" => 1,
                ")" or "]" => -1,
                _ => 0
            };

            if (depth == 0 && text == ")")
            {
                return ctx.GetText(ctx.Peek(offset + 1)) == "->";
            }
        }

        return false;
    }

    private bool LooksLikeParenthesizedTypeAnnotation()
    {
        if (!ctx.Check("("))
        {
            return false;
        }

        var depth = 0;
        for (var offset = 0; offset < 64; offset++)
        {
            var token = ctx.Peek(offset);
            if (token is EofToken)
            {
                return false;
            }

            var text = ctx.GetText(token);
            if (text == "(")
            {
                depth++;
            }
            else if (text == ")")
            {
                depth--;
                if (depth == 0)
                {
                    var next = ctx.GetText(ctx.Peek(offset + 1));
                    return next == "=";
                }
            }

            if (depth == 0 && text is ";" or "}" or "=>" or ",")
            {
                return false;
            }
        }

        return false;
    }

    private bool CanParseComptimeFunctionSignature()
    {
        var saved = ctx.SavePosition();
        var depth = 0;
        try
        {
            while (!ctx.IsEof)
            {
                var token = ctx.Current;
                if (TokenKind.IsPunctuation(token, ";") ||
                    TokenKind.IsPunctuation(token, "{") && depth == 0 ||
                    TokenKind.IsPunctuation(token, "=") && depth == 0)
                {
                    return false;
                }

                if (TokenKind.IsPunctuation(token, "(") || TokenKind.IsPunctuation(token, "["))
                {
                    depth++;
                }
                else if (TokenKind.IsPunctuation(token, ")") || TokenKind.IsPunctuation(token, "]"))
                {
                    depth = Math.Max(0, depth - 1);
                }
                else if (TokenKind.IsOperator(token, "->") && depth == 0)
                {
                    return true;
                }

                ctx.Advance();
            }

            return false;
        }
        finally
        {
            ctx.RestorePosition(saved);
        }
    }

    private FuncDecl ParseNameFirstFuncDecl(Token startToken, string name, List<TypeParam> typeParams, bool isComptime = false)
    {
        if (!isComptime)
        {
            isComptime = ctx.Match("comptime");
        }

        var signature = _typeParser.ParseType();
        var clauses = new List<DeclarationClause>();
        AddSignatureNeedClause(signature, clauses);
        var requiredAbilities = ParseFunctionClauseGroup(typeParams, clauses);

        var func = new FuncDecl();
        func.SetSpan(ctx.SpanFrom(startToken));
        func.SetName(name);
        func.SetTypeParams(typeParams);
        func.SetSignature(signature);
        func.SetRequiredAbilities(MergeSignatureEffects(signature, requiredAbilities));
        func.SetComptime(isComptime);
        func.SetAttributes([]);
        func.SetClauses(clauses);
        return func;
    }

    private List<EffectRequirementNode> ParseFunctionClauseGroup(
        IReadOnlyList<TypeParam> typeParams,
        List<DeclarationClause> clauses)
    {
        var requiredAbilities = new List<EffectRequirementNode>();
        while (!ctx.IsEof)
        {
            if (ctx.Check(WellKnownStrings.Keywords.Where))
            {
                clauses.Add(ParseGenericWhereClause(typeParams));
                continue;
            }

            if (ctx.Check(WellKnownStrings.Keywords.Need))
            {
                var start = ctx.Current;
                var requirements = ParseNeedClause();
                requiredAbilities.AddRange(requirements);
                var clause = CreateClause(DeclarationClauseKind.Need, "need", start);
                foreach (var requirement in requirements)
                {
                    clause.AddArgument(string.Join(WellKnownStrings.Separators.Path, requirement.Path));
                }
                clause.SetSpan(ctx.SpanFrom(start));
                clauses.Add(clause);
                continue;
            }

            if (ClauseSchema.TryGetKind(ctx.GetText(), out var kind))
            {
                clauses.Add(ParseDeclarationClause(kind));
                continue;
            }

            if (ctx.Check("operator"))
            {
                ctx.Error("function-level operator clauses were removed; declare the symbolic function name directly");
                SkipRemovedFunctionClause();
                continue;
            }

            break;
        }

        return requiredAbilities;
    }

    private void SkipRemovedFunctionClause()
    {
        ctx.Advance();
        while (!ctx.IsEof &&
               !ctx.Check("{") &&
               !ctx.Check(";") &&
               !ClauseSchema.TryGetKind(ctx.GetText(), out _))
        {
            ctx.Advance();
        }
    }

    private List<DeclarationClause> ParseDeclarationClauseZone(IReadOnlyList<TypeParam> typeParams)
    {
        var clauses = new List<DeclarationClause>();
        while (ClauseSchema.TryGetKind(ctx.GetText(), out var kind))
        {
            if (kind == DeclarationClauseKind.Where)
            {
                clauses.Add(ParseGenericWhereClause(typeParams));
            }
            else
            {
                clauses.Add(ParseDeclarationClause(kind));
            }
        }

        return clauses;
    }

    private DeclarationClause ParseDeclarationClause(DeclarationClauseKind kind)
    {
        var start = ctx.Current;
        var keyword = ctx.GetText();
        ctx.Advance();
        var clause = CreateClause(kind, keyword, start);
        _ = ClauseSchema.TryGet(keyword, out var spec);

        if (kind == DeclarationClauseKind.Extern)
        {
            if (!ctx.Match("("))
            {
                ctx.Error("extern uses the structured form 'extern(c, library: Library, name: \"symbol\")'");
                if (!IsClauseBoundary())
                {
                    clause.AddArgument(ReadClauseArgument());
                }
                clause.SetSpan(ctx.SpanFrom(start));
                return clause;
            }

            if (!ctx.Check(")"))
            {
                clause.AddArgument(ReadAttributeArg());
                while (ctx.Match(","))
                {
                    if (ctx.Check(")"))
                    {
                        break;
                    }
                    clause.AddArgument(ReadAttributeArg());
                }
            }
            ctx.Expect(")");
            clause.SetSpan(ctx.SpanFrom(start));
            return clause;
        }

        if (spec.Arguments == ClauseArgumentGrammar.None)
        {
            clause.SetSpan(ctx.SpanFrom(start));
            return clause;
        }

        if (spec.Arguments == ClauseArgumentGrammar.MetaInvocation)
        {
            var saved = ctx.SavePosition();
            var invocation = ParseMetaInvocationSyntax();
            ctx.RestorePosition(saved);
            clause.AddArgument(ReadMetaInvocationTokens());
            clause.SetMetaInvocation(invocation);
            clause.SetSpan(ctx.SpanFrom(start));
            return clause;
        }

        if (spec.Arguments is ClauseArgumentGrammar.PathList or ClauseArgumentGrammar.IdentifierList)
        {
            clause.AddArgument(ReadClauseArgument());
            while (ctx.Match(","))
            {
                clause.AddArgument(ReadClauseArgument());
            }
            clause.SetSpan(ctx.SpanFrom(start));
            return clause;
        }

        clause.AddArgument(ReadClauseArgument());
        if (spec.Arguments == ClauseArgumentGrammar.TokenIsland && !IsClauseBoundary())
        {
            clause.AddArgument(ReadClauseArgument());
        }
        clause.SetSpan(ctx.SpanFrom(start));
        return clause;
    }

    private MetaInvocationSyntax ParseMetaInvocationSyntax()
    {
        var start = ctx.Current;
        var invocation = new MetaInvocationSyntax();
        var path = new List<string>();
        if (!TokenKind.IsAnyIdentifier(ctx.Current))
        {
            ctx.Error("expand requires a meta generator path");
            invocation.SetSpan(new SourceSpan(start.Location, start.Length));
            return invocation;
        }

        path.Add(ctx.GetText());
        ctx.Advance();
        while (ctx.Match("."))
        {
            if (!TokenKind.IsAnyIdentifier(ctx.Current))
            {
                ctx.Error("expected a generator name after '.'");
                break;
            }
            path.Add(ctx.GetText());
            ctx.Advance();
        }
        invocation.SetGeneratorPath(path);

        if (ctx.Match("("))
        {
            if (!ctx.Check(")"))
            {
                AddMetaArgument();
                while (ctx.Match(","))
                {
                    AddMetaArgument();
                }
            }
            ctx.Expect(")");
        }

        invocation.SetSpan(ctx.SpanFrom(start));
        return invocation;

        void AddMetaArgument()
        {
            var argument = _exprParser.ParseExpr();
            invocation.AddExplicitArgument(argument);
        }
    }

    private string ReadMetaInvocationTokens()
    {
        var parts = new List<string>();
        var depth = 0;
        while (!ctx.IsEof)
        {
            var text = ctx.GetText();
            if (depth == 0 &&
                (text is "{" or "}" or ";" or "=" or "," || ClauseSchema.TryGetKind(text, out _)))
            {
                break;
            }

            depth += text switch
            {
                "(" or "[" => 1,
                ")" or "]" => -1,
                _ => 0
            };
            parts.Add(text);
            ctx.Advance();
            if (depth == 0 && parts.Count > 0 && text == ")")
            {
                break;
            }
        }

        return string.Concat(parts);
    }

    private string ReadClauseArgument()
    {
        if (IsClauseBoundary())
        {
            ctx.Error($"clause '{ctx.GetText(ctx.Peek(-1))}' requires an argument");
            return string.Empty;
        }

        var parts = new List<string> { ctx.GetLiteralRawText() };
        ctx.Advance();
        while (ctx.Check(".") && TokenKind.IsAnyIdentifier(ctx.Peek(1)))
        {
            parts.Add(".");
            ctx.Advance();
            parts.Add(ctx.GetText());
            ctx.Advance();
        }

        if (ctx.Check("["))
        {
            var depth = 0;
            do
            {
                var text = ctx.GetLiteralRawText();
                depth += text switch
                {
                    "[" => 1,
                    "]" => -1,
                    _ => 0
                };
                parts.Add(text);
                ctx.Advance();
            }
            while (!ctx.IsEof && depth > 0);

            if (depth > 0)
            {
                ctx.Error("unterminated generic argument list in declaration clause");
            }
        }

        return string.Concat(parts);
    }

    private bool IsClauseBoundary()
    {
        var text = ctx.GetText();
        return text is "{" or "}" or ";" or "<eof>" || ClauseSchema.TryGetKind(text, out _);
    }

    private static DeclarationClause CreateClause(
        DeclarationClauseKind kind,
        string keyword,
        Token start)
    {
        var clause = new DeclarationClause();
        clause.SetKind(kind, keyword);
        clause.SetSpan(new SourceSpan(start.Location, start.Length));
        return clause;
    }

    private void RejectPostBodyClauses()
    {
        while (ClauseSchema.TryGetKind(ctx.GetText(), out var kind))
        {
            if (kind == DeclarationClauseKind.Expand && IsStandaloneExpandDeclarationLookahead())
            {
                return;
            }

            ctx.Error("declaration clauses must appear before the body");
            _ = ParseDeclarationClause(kind);
        }
    }

    private bool IsStandaloneExpandDeclarationLookahead()
    {
        var offset = 1;
        if (!TokenKind.IsAnyIdentifier(ctx.Peek(offset)))
        {
            return false;
        }

        offset++;
        while (ctx.GetText(ctx.Peek(offset)) == "." &&
               TokenKind.IsAnyIdentifier(ctx.Peek(offset + 1)))
        {
            offset += 2;
        }

        if (ctx.GetText(ctx.Peek(offset)) == "(")
        {
            var depth = 0;
            do
            {
                var token = ctx.Peek(offset);
                if (token is EofToken)
                {
                    return false;
                }

                depth += ctx.GetText(token) switch
                {
                    "(" or "[" or "{" => 1,
                    ")" or "]" or "}" => -1,
                    _ => 0
                };
                offset++;
            }
            while (depth > 0);
        }

        return ctx.GetText(ctx.Peek(offset)) == ";";
    }

    private DeclarationClause ParseGenericWhereClause(IReadOnlyList<TypeParam> typeParams)
    {
        var start = ctx.Current;
        var startPosition = ctx.SavePosition();
        _typeParser.ApplyGenericWhereClause(typeParams);
        var consumed = ReadConsumedTokenRange(startPosition);
        var argument = consumed.StartsWith(WellKnownStrings.Keywords.Where, StringComparison.Ordinal)
            ? consumed[WellKnownStrings.Keywords.Where.Length..]
            : consumed;
        var clause = CreateClause(DeclarationClauseKind.Where, WellKnownStrings.Keywords.Where, start);
        clause.AddArgument(argument);
        clause.SetSpan(ctx.SpanFrom(start));
        return clause;
    }

    private AdtDef ParseNameFirstType(
        List<AstAttribute> attrs,
        bool isExport,
        Token startToken,
        string name,
        List<TypeParam> typeParams)
    {
        var clauses = ParseDeclarationClauseZone(typeParams);
        if (ctx.Match("="))
        {
            var target = _typeParser.ParseType();
            ctx.Match(";");
            var alias = new AdtDef();
            alias.SetSpan(ctx.SpanFrom(startToken));
            alias.SetName(name);
            alias.SetTypeParams(typeParams);
            alias.SetTypeAlias(target);
            alias.SetAttributes(attrs);
            alias.SetClauses(clauses);
            alias.SetExported(isExport);
            return alias;
        }

        ctx.Expect("{");
        List<CaseTypeDef> cases;
        List<Constructor> constructors;
        List<Field> fields;
        List<EidosAstNode> members;
        if (ctx.SupportsTypedClauses)
        {
            ParseClosedTypeBody(out cases, out constructors, out fields, out members);
        }
        else
        {
            ParseAdtBody(out constructors, out fields);
            cases = [];
            members = [.. fields];
        }

        ctx.Expect("}");
        var adt = new AdtDef();
        adt.SetSpan(ctx.SpanFrom(startToken));
        adt.SetName(name);
        adt.SetTypeParams(typeParams);
        adt.SetConstructors(constructors);
        adt.SetFields(fields);
        adt.SetCases(cases);
        adt.SetMembers(members);
        adt.SetAttributes(attrs);
        adt.SetClauses(clauses);
        adt.SetExported(isExport);
        return adt;
    }

    private void ParseClosedTypeBody(
        out List<CaseTypeDef> cases,
        out List<Constructor> constructors,
        out List<Field> fields,
        out List<EidosAstNode> members)
    {
        cases = [];
        constructors = [];
        fields = [];
        members = [];
        var seenCase = false;
        var fieldNames = new HashSet<string>(StringComparer.Ordinal);
        var caseNames = new HashSet<string>(StringComparer.Ordinal);

        while (!ctx.Check("}") && !ctx.IsEof)
        {
            if (ctx.Match(","))
            {
                continue;
            }

            if (ctx.Check("expand"))
            {
                members.Add(ParseExpandDeclaration(SyntaxCategory.Member));
                ctx.Match(",");
                continue;
            }
            if (IsClosedCaseLookahead())
            {
                seenCase = true;
                var caseType = ParseCaseType(fieldNames);
                if (!caseNames.Add(caseType.Name))
                {
                    ctx.Error($"duplicate case type '{caseType.Name}'", caseType.Span.Location);
                }
                cases.Add(caseType);
                members.Add(caseType);
                constructors.AddRange(ClosedCaseConstructorProjection.Create(caseType, fields, []));
            }
            else if (IsNewFieldLookahead())
            {
                if (seenCase)
                {
                    ctx.Error("common fields must be declared before the first case type");
                }

                var field = ParseField("::");
                if (!fieldNames.Add(field.Name))
                {
                    ctx.Error($"duplicate common field '{field.Name}'", field.Span.Location);
                }
                fields.Add(field);
                members.Add(field);
            }
            else if (IsLegacyFieldLookahead())
            {
                ctx.Error("type-body fields use 'name :: Type'; ':' is reserved for labels");
                _ = ParseField(":");
            }
            else
            {
                ctx.Error("expected a field declaration 'name :: Type' or a case type 'Case :: type'");
                ctx.Advance();
            }

            if (!ctx.Check("}") && !ctx.Check(","))
            {
                ctx.Error("expected ',' between type body members");
            }
        }
    }

    private bool IsClosedCaseLookahead()
    {
        if (!TokenKind.IsAnyIdentifier(ctx.Current))
        {
            return false;
        }

        var bindingOffset = 1;
        if (ctx.CheckPeek(bindingOffset, "["))
        {
            var depth = 0;
            do
            {
                var token = ctx.Peek(bindingOffset);
                if (token is EofToken)
                {
                    return false;
                }

                var text = ctx.GetText(token);
                if (text == "[")
                {
                    depth++;
                }
                else if (text == "]")
                {
                    depth--;
                }

                bindingOffset++;
            }
            while (depth > 0);
        }

        return ctx.CheckPeek(bindingOffset, "::") &&
               ctx.CheckPeek(bindingOffset + 1, WellKnownStrings.Keywords.Type);
    }

    private bool IsNewFieldLookahead()
    {
        return TokenKind.IsAnyIdentifier(ctx.Current) &&
               ctx.CheckPeek(1, "::") &&
               !ctx.CheckPeek(2, WellKnownStrings.Keywords.Type);
    }

    private bool IsLegacyFieldLookahead()
    {
        return TokenKind.IsAnyIdentifier(ctx.Current) && ctx.CheckPeek(1, ":");
    }

    private CaseTypeDef ParseCaseType(IReadOnlySet<string> inheritedFieldNames)
    {
        var startToken = ctx.Current;
        var caseType = new CaseTypeDef();
        caseType.SetName(ParseCompileTimeDeclarationName());
        caseType.SetTypeParams(_typeParser.TryParseTypeParams() ?? []);
        ctx.Expect("::");
        ctx.Expect(WellKnownStrings.Keywords.Type);

        var isPositional = ctx.Match("(");
        if (isPositional)
        {
            if (inheritedFieldNames.Count > 0)
            {
                ctx.Error("a positional case type cannot inherit common named fields");
            }
            if (!ctx.Check(")"))
            {
                caseType.AddPositionalField(_typeParser.ParseType());
                while (ctx.Match(","))
                {
                    caseType.AddPositionalField(_typeParser.ParseType());
                }
            }
            ctx.Expect(")");
        }

        var clauses = ParseCaseTypeClauseZone(caseType.TypeParams, out var parentSpecialization);
        caseType.SetClauses(clauses);
        if (!isPositional)
        {
            ctx.Expect("{");
            ParseNestedCaseBody(caseType, inheritedFieldNames);
            ctx.Expect("}");
            RejectPostBodyClauses();
        }

        caseType.SetParentSpecialization(parentSpecialization);
        caseType.SetSpan(ctx.SpanFrom(startToken));
        return caseType;
    }

    private List<DeclarationClause> ParseCaseTypeClauseZone(
        IReadOnlyList<TypeParam> typeParams,
        out TypeNode? parentSpecialization)
    {
        var clauses = new List<DeclarationClause>();
        parentSpecialization = null;
        while (ClauseSchema.TryGetKind(ctx.GetText(), out var kind))
        {
            if (kind == DeclarationClauseKind.Where)
            {
                clauses.Add(ParseGenericWhereClause(typeParams));
                continue;
            }

            if (kind != DeclarationClauseKind.Case)
            {
                clauses.Add(ParseDeclarationClause(kind));
                continue;
            }

            var start = ctx.Current;
            ctx.Advance();
            var argumentStart = ctx.SavePosition();
            var specialization = _typeParser.ParseType();
            var clause = CreateClause(DeclarationClauseKind.Case, "case", start);
            clause.AddArgument(ReadConsumedTokenRange(argumentStart));
            clause.SetSpan(ctx.SpanFrom(start));
            clauses.Add(clause);
            parentSpecialization ??= specialization;
        }

        return clauses;
    }

    private string ReadConsumedTokenRange(int startPosition)
    {
        var endPosition = ctx.SavePosition();
        var parts = new List<string>();
        ctx.RestorePosition(startPosition);
        while (ctx.Position < endPosition && !ctx.IsEof)
        {
            parts.Add(ctx.GetLiteralRawText());
            ctx.Advance();
        }
        ctx.RestorePosition(endPosition);
        return string.Concat(parts);
    }

    private void ParseNestedCaseBody(CaseTypeDef owner, IReadOnlySet<string> inheritedFieldNames)
    {
        var seenCase = false;
        var effectiveFieldNames = new HashSet<string>(inheritedFieldNames, StringComparer.Ordinal);
        var caseNames = new HashSet<string>(StringComparer.Ordinal);

        while (!ctx.Check("}") && !ctx.IsEof)
        {
            if (ctx.Match(","))
            {
                continue;
            }

            if (ctx.Check("expand"))
            {
                owner.AddMemberExpansion(ParseExpandDeclaration(SyntaxCategory.Member));
                ctx.Match(",");
                continue;
            }
            if (IsClosedCaseLookahead())
            {
                seenCase = true;
                var child = ParseCaseType(effectiveFieldNames);
                if (!caseNames.Add(child.Name))
                {
                    ctx.Error($"duplicate case type '{child.Name}'", child.Span.Location);
                }
                owner.AddCase(child);
            }
            else if (IsNewFieldLookahead())
            {
                if (seenCase)
                {
                    ctx.Error("fields must be declared before the first nested case type");
                }

                var field = ParseField("::");
                if (!effectiveFieldNames.Add(field.Name))
                {
                    ctx.Error($"field '{field.Name}' shadows an inherited or earlier field in case type '{owner.Name}'", field.Span.Location);
                }
                owner.AddField(field);
            }
            else if (IsLegacyFieldLookahead())
            {
                ctx.Error("type-body fields use 'name :: Type'; ':' is reserved for labels");
                _ = ParseField(":");
            }
            else
            {
                ctx.Error("expected a field declaration or nested case type");
                ctx.Advance();
            }

            if (!ctx.Check("}") && !ctx.Check(","))
            {
                ctx.Error("expected ',' between type body members");
            }
        }
    }

    private TraitDef ParseNameFirstTrait(
        List<AstAttribute> attrs,
        bool isExport,
        Token startToken,
        string name,
        List<TypeParam> typeParams)
    {
        var superTraits = new List<TraitRef>();
        if (ctx.Match(":"))
        {
            superTraits.Add(_typeParser.ParseTraitRef());
            while (ctx.Match("+"))
            {
                superTraits.Add(_typeParser.ParseTraitRef());
            }
        }

        var clauses = ParseDeclarationClauseZone(typeParams);
        var funcs = new List<FuncDef>();
        var associatedTypes = new List<AssociatedTypeDecl>();
        var associatedConsts = new List<AssociatedConstDecl>();
        var members = new List<EidosAstNode>();
        if (ctx.Check("{"))
        {
            ctx.Advance();
            while (!ctx.Check("}") && !ctx.IsEof)
            {
                var funcAttrs = ParseAttributes();
                if (ctx.Check("expand"))
                {
                    members.Add(ParseExpandDeclaration(SyntaxCategory.Member));
                    continue;
                }
                if (ctx.Check(WellKnownStrings.Keywords.Func))
                {
                    var method = ParseFuncDef(funcAttrs, false);
                    funcs.Add(method);
                    members.Add(method);
                }
                else if (IsNameFirstDeclarationStart())
                {
                    var member = ParseNameFirstTraitMember(funcAttrs);
                    switch (member)
                    {
                        case FuncDef funcDef:
                            funcs.Add(funcDef);
                            members.Add(funcDef);
                            break;
                        case FuncDecl funcDecl:
                            var wrapped = WrapFuncDecl(funcDecl);
                            funcs.Add(wrapped);
                            members.Add(wrapped);
                            break;
                        case AssociatedTypeDecl associatedType:
                            associatedTypes.Add(associatedType);
                            members.Add(associatedType);
                            break;
                        case AssociatedConstDecl associatedConst:
                            associatedConsts.Add(associatedConst);
                            members.Add(associatedConst);
                            break;
                    }
                }
                else
                {
                    ctx.Error(DiagnosticMessages.ParserExpectedTraitBodyMember(ctx.GetText()));
                    ctx.Advance();
                }

                ctx.Match(";");
            }
            ctx.Expect("}");
        }
        RejectPostBodyClauses();

        ctx.Match(";");
        var def = new TraitDef();
        def.SetSpan(ctx.SpanFrom(startToken));
        def.SetName(name);
        def.SetTypeParams(typeParams);
        def.SetSuperTraits(superTraits);
        def.SetMethods(funcs);
        def.SetAssociatedTypes(associatedTypes);
        def.SetAssociatedConsts(associatedConsts);
        def.SetMembers(members);
        def.SetAttributes(attrs);
        def.SetClauses(clauses);
        def.SetExported(isExport);
        return def;
    }

    private EidosAstNode ParseNameFirstTraitMember(List<AstAttribute> attrs)
    {
        return ParseNameFirstMember(attrs, requireAssociatedValues: false);
    }

    private EidosAstNode ParseNameFirstInstanceMember(List<AstAttribute> attrs)
    {
        return ParseNameFirstMember(attrs, requireAssociatedValues: true);
    }

    private EidosAstNode ParseNameFirstMember(List<AstAttribute> attrs, bool requireAssociatedValues)
    {
        var startToken = ctx.Current;
        var name = ctx.GetText();
        ctx.Advance();
        var typeParams = _typeParser.TryParseTypeParams() ?? [];
        ctx.Expect("::");

        if (ctx.Match("type"))
        {
            return ParseAssociatedTypeMember(startToken, name, typeParams, requireAssociatedValues);
        }

        var isComptime = ctx.Match("comptime");
        var signature = _typeParser.ParseType();
        if (ctx.Check("="))
        {
            return ParseAssociatedConstMember(startToken, name, signature, requireValue: true);
        }

        if (!requireAssociatedValues && !IsFunctionSignatureShape(signature))
        {
            return ParseAssociatedConstMember(startToken, name, signature, requireValue: false);
        }

        return ParseNameFirstFuncDefAfterSignature(attrs, startToken, name, typeParams, signature, isComptime: isComptime);
    }

    private static bool IsFunctionSignatureShape(TypeNode signature)
    {
        return signature is ArrowType or EffectfulType;
    }

    private AssociatedTypeDecl ParseAssociatedTypeMember(
        Token startToken,
        string name,
        List<TypeParam> typeParams,
        bool requireValue)
    {
        TypeNode? valueType = null;
        if (requireValue)
        {
            ctx.Expect("=");
            valueType = _typeParser.ParseType();
        }
        else if (ctx.Match("="))
        {
            valueType = _typeParser.ParseType();
        }

        var decl = new AssociatedTypeDecl();
        decl.Span = ctx.SpanFrom(startToken);
        decl.SetName(name);
        decl.SetTypeParams(typeParams);
        decl.SetValueType(valueType);
        return decl;
    }

    private AssociatedConstDecl ParseAssociatedConstMember(
        Token startToken,
        string name,
        TypeNode type,
        bool requireValue)
    {
        EidosAstNode? value = null;
        if (requireValue)
        {
            ctx.Expect("=");
            value = _exprParser.ParseExpr();
        }
        else if (ctx.Match("="))
        {
            value = _exprParser.ParseExpr();
        }

        var decl = new AssociatedConstDecl();
        decl.Span = ctx.SpanFrom(startToken);
        decl.SetName(name);
        decl.SetType(type);
        decl.SetValue(value);
        return decl;
    }

    private FuncDef ParseNameFirstFuncDefAfterSignature(
        List<AstAttribute> attrs,
        Token startToken,
        string name,
        List<TypeParam> typeParams,
        TypeNode signature,
        bool isExport = false,
        bool isComptime = false)
    {
        var clauses = new List<DeclarationClause>();
        AddSignatureNeedClause(signature, clauses);
        var requiredAbilities = ParseFunctionClauseGroup(typeParams, clauses);

        List<PatternBranch>? body = null;
        if (ctx.Check("{"))
        {
            ctx.Advance();
            body = ParsePatternBranches();
            ctx.Expect("}");
        }
        RejectPostBodyClauses();

        var func = new FuncDef();
        func.SetSpan(ctx.SpanFrom(startToken));
        func.SetName(name);
        func.SetTypeParams(typeParams);
        func.SetSignature(signature);
        func.SetRequiredAbilities(MergeSignatureEffects(signature, requiredAbilities));
        func.SetComptime(isComptime);
        func.SetBody(body ?? []);
        func.SetAttributes(attrs);
        func.SetClauses(clauses);
        func.SetExported(isExport);
        return func;
    }

    private Declaration ParseTopLevelNameFirstCallableAfterSignature(
        List<AstAttribute> attrs,
        Token startToken,
        string name,
        List<TypeParam> typeParams,
        TypeNode signature,
        List<DeclarationClause> clauses,
        List<EffectRequirementNode> requiredAbilities,
        bool isExport = false)
    {
        if (ctx.Match(";"))
        {
            var decl = new FuncDecl();
            decl.SetSpan(ctx.SpanFrom(startToken));
            decl.SetName(name);
            decl.SetTypeParams(typeParams);
            decl.SetSignature(signature);
            decl.SetRequiredAbilities(MergeSignatureEffects(signature, requiredAbilities));
            decl.SetAttributes(attrs);
            decl.SetClauses(clauses);
            decl.SetExported(isExport);
            return decl;
        }

        List<PatternBranch>? body = null;
        if (ctx.Check("{"))
        {
            ctx.Advance();
            body = ParsePatternBranches();
            ctx.Expect("}");
        }
        RejectPostBodyClauses();

        var func = new FuncDef();
        func.SetSpan(ctx.SpanFrom(startToken));
        func.SetName(name);
        func.SetTypeParams(typeParams);
        func.SetSignature(signature);
        func.SetRequiredAbilities(MergeSignatureEffects(signature, requiredAbilities));
        func.SetBody(body ?? []);
        func.SetAttributes(attrs);
        func.SetClauses(clauses);
        func.SetExported(isExport);
        return func;
    }

    private static FuncDef WrapFuncDecl(FuncDecl funcDecl)
    {
        var wrapper = new FuncDef();
        wrapper.SetSpan(funcDecl.Span);
        wrapper.SetName(funcDecl.Name);
        wrapper.SetTypeParams(funcDecl.TypeParams);
        if (funcDecl.Signature.Count > 0)
        {
        wrapper.SetSignature(funcDecl.Signature[0]);
        }
        wrapper.SetRequiredAbilities(funcDecl.RequiredAbilities);
        wrapper.SetComptime(funcDecl.IsComptime);
        wrapper.SetBody([]);
        wrapper.SetAttributes(funcDecl.Attributes);
        wrapper.SetClauses(funcDecl.Clauses);
        return wrapper;
    }

    private EffectDef ParseNameFirstEffect(
        List<AstAttribute> attrs,
        bool isExport,
        Token startToken,
        string name,
        List<TypeParam> typeParams)
    {
        if (typeParams.Count > 0)
        {
            ctx.Error(DiagnosticMessages.ParserEffectTagTypeParametersNotSupported);
        }

        var clauses = ParseDeclarationClauseZone(typeParams);
        if (ctx.Check("{"))
        {
            ctx.Error(DiagnosticMessages.ParserEffectTagMembersNotSupported);
            SkipBalancedBlock();
        }
        else if (!ctx.Match(";"))
        {
            ctx.Error(DiagnosticMessages.ParserEffectTagRequiresSemicolon);
        }
        RejectPostBodyClauses();

        var def = new EffectDef();
        def.SetSpan(ctx.SpanFrom(startToken));
        def.SetName(name);
        def.SetAttributes(attrs);
        def.SetClauses(clauses);
        def.SetExported(isExport);
        return def;
    }

    private void SkipBalancedBlock()
    {
        if (!ctx.Match("{"))
        {
            return;
        }

        var depth = 1;
        while (!ctx.IsEof && depth > 0)
        {
            if (ctx.Match("{"))
            {
                depth++;
            }
            else if (ctx.Match("}"))
            {
                depth--;
            }
            else
            {
                ctx.Advance();
            }
        }
    }

    private InstanceDecl ParseNameFirstInstance(
        List<AstAttribute> attrs,
        bool isExport,
        Token startToken,
        string name,
        List<TypeParam> typeParams)
    {
        var traitRef = _typeParser.ParseTraitRef();
        TypeNode? targetType = null;
        var usesConstructorBridge = false;
        if (ctx.Match("for"))
        {
            targetType = _typeParser.ParseType();
            usesConstructorBridge = true;
            var clauses = ParseDeclarationClauseZone(typeParams);

            var bridgeDef = new InstanceDecl();
            bridgeDef.SetSpan(ctx.SpanFrom(startToken));
            bridgeDef.SetName(name);
            bridgeDef.SetTypeParams(typeParams);
            bridgeDef.SetTrait(traitRef);
            bridgeDef.SetTargetType(targetType);
            bridgeDef.SetUsesConstructorBridge(true);
            bridgeDef.SetClauses(clauses);
            if (ctx.Check("{"))
            {
                bridgeDef.SetConstructorBridgeFacts(ParseConstructorBridgeFacts());
            }
            else
            {
                ctx.Expect(";");
            }

            bridgeDef.SetAttributes(attrs);
            bridgeDef.SetExported(isExport);
            return bridgeDef;
        }

        var instanceClauses = ParseDeclarationClauseZone(typeParams);
        ctx.Expect("{");

        var methods = new List<FuncDef>();
        var associatedTypes = new List<AssociatedTypeDecl>();
        var associatedConsts = new List<AssociatedConstDecl>();
        var members = new List<EidosAstNode>();
        while (!ctx.Check("}") && !ctx.IsEof)
        {
            var methodAttrs = ParseAttributes();
            if (ctx.Check("expand"))
            {
                members.Add(ParseExpandDeclaration(SyntaxCategory.Member));
                continue;
            }
            if (ctx.Check(WellKnownStrings.Keywords.Func))
            {
                var method = ParseFuncDef(methodAttrs, false);
                methods.Add(method);
                members.Add(method);
            }
            else if (IsNameFirstDeclarationStart())
            {
                var member = ParseNameFirstInstanceMember(methodAttrs);
                switch (member)
                {
                    case FuncDef funcDef:
                        methods.Add(funcDef);
                        members.Add(funcDef);
                        break;
                    case AssociatedTypeDecl associatedType:
                        associatedTypes.Add(associatedType);
                        members.Add(associatedType);
                        break;
                    case AssociatedConstDecl associatedConst:
                        associatedConsts.Add(associatedConst);
                        members.Add(associatedConst);
                        break;
                }
            }
            else
            {
                ctx.Error(DiagnosticMessages.ParserExpectedTraitBodyMember(ctx.GetText()));
                ctx.Advance();
            }

            ctx.Match(";");
        }

        ctx.Expect("}");
        RejectPostBodyClauses();
        var def = new InstanceDecl();
        def.SetSpan(ctx.SpanFrom(startToken));
        def.SetName(name);
        def.SetTypeParams(typeParams);
        def.SetTrait(traitRef);
        if (targetType != null)
        {
            def.SetTargetType(targetType);
        }
        def.SetUsesConstructorBridge(usesConstructorBridge);
        def.SetMethods(methods);
        def.SetAssociatedTypes(associatedTypes);
        def.SetAssociatedConsts(associatedConsts);
        def.SetMembers(members);
        def.SetAttributes(attrs);
        def.SetClauses(instanceClauses);
        def.SetExported(isExport);
        return def;
    }

    private List<ConstructorBridgeFact> ParseConstructorBridgeFacts()
    {
        var facts = new List<ConstructorBridgeFact>();
        ctx.Expect("{");
        while (!ctx.Check("}") && !ctx.IsEof)
        {
            facts.Add(ParseConstructorBridgeFact());
            ctx.Match("|");
            ctx.Match(",");
            ctx.Match(";");
        }

        ctx.Expect("}");
        return facts;
    }

    private ConstructorBridgeFact ParseConstructorBridgeFact()
    {
        var startToken = ctx.Current;
        var ctorName = ParseCompileTimeDeclarationName();
        ctx.Expect("=>");
        ctx.Expect("{");

        var constants = new List<ConstructorConstant>();
        if (!ctx.Check("}"))
        {
            constants.Add(ParseConstructorConstant());
            while (ctx.Match(","))
            {
                if (ctx.Check("}"))
                {
                    break;
                }

                constants.Add(ParseConstructorConstant());
            }
        }

        ctx.Expect("}");
        var fact = new ConstructorBridgeFact();
        fact.SetSpan(ctx.SpanFrom(startToken));
        fact.SetConstructorName(ctorName);
        fact.SetConstants(constants);
        return fact;
    }

    private ModuleDecl ParseNameFirstModule(
        List<AstAttribute> attrs,
        bool isExport,
        Token startToken,
        List<string> modulePath)
    {
        var decl = new ModuleDecl();
        var clauses = ParseDeclarationClauseZone([]);
        foreach (var segment in modulePath)
        {
            ctx.RegisterNamespaceRoot(segment);
        }
        ctx.Expect("{");
        var innerDecls = new List<Declaration>();
        while (!ctx.Check("}") && !ctx.IsEof)
        {
            if (ParseTopLevel() is Declaration d)
            {
                innerDecls.Add(d);
            }
        }

        ctx.Expect("}");
        RejectPostBodyClauses();
        decl.SetSpan(ctx.SpanFrom(startToken));
        decl.SetPath(modulePath);
        decl.SetDeclarations(innerDecls);
        decl.SetAttributes(attrs);
        decl.SetClauses(clauses);
        decl.SetExported(isExport);
        return decl;
    }

    #region FuncDef

    public FuncDef ParseFuncDef(List<AstAttribute> attrs, bool isExport)
    {
        var startToken = ctx.Current;
        ctx.Expect("func");
        var name = ParseFunctionName();

        var typeParams = _typeParser.TryParseTypeParams();
        _typeParser.ApplyGenericWhereClause(typeParams ?? []);
        ctx.Expect(":", DiagnosticMessages.ParserExpectedColonBeforeSignature);
        var signature = _typeParser.ParseType();
        var requiredAbilities = ParseNeedClause();
        _typeParser.ApplyGenericWhereClause(typeParams ?? []);

        List<PatternBranch>? body = null;
        if (ctx.Check("{"))
        {
            ctx.Advance();
            body = ParsePatternBranches();
            ctx.Expect("}");
        }

        var func = new FuncDef();
        func.SetSpan(ctx.SpanFrom(startToken));
        func.SetName(name);
        func.SetTypeParams(typeParams ?? []);
        func.SetSignature(signature);
        func.SetRequiredAbilities(MergeSignatureEffects(signature, requiredAbilities));
        func.SetBody(body ?? []);
        func.SetAttributes(attrs);
        func.SetExported(isExport);
        return func;
    }

    private List<PatternBranch> ParsePatternBranches()
    {
        var branches = new List<PatternBranch>();
        if (!ctx.Check("}"))
        {
            branches.Add(ParsePatternBranch());
            while (ctx.Match(","))
            {
                if (ctx.Check("}")) break;
                branches.Add(ParsePatternBranch());
            }
        }
        return branches;
    }

    internal PatternBranch ParsePatternBranch()
    {
        var startToken = ctx.Current;
        var patterns = new List<Pattern>();
        patterns.Add(_patternParser.ParsePattern());

        while (!IsBranchTerminator())
        {
            var pos = ctx.Position;
            patterns.Add(_patternParser.ParsePattern());
            if (ctx.Position == pos)
            {
                ctx.Error(DiagnosticMessages.ParserUnexpectedTokenInPattern(ctx.GetText()));
                ctx.Advance();
                break;
            }
        }

        var mainPattern = patterns.Count == 1
            ? patterns[0]
            : new TuplePattern { Elements = patterns };

        var guards = new List<EidosAstNode>();
        while (ctx.Match("when"))
        {
            guards.Add(ParseGuardExpression());
        }

        ctx.Expect("=>");
        while (HasCurriedPatternBeforeNextArrow())
        {
            while (!ctx.Check("when") && !ctx.Check("=>") && !ctx.IsEof)
            {
                var pos = ctx.Position;
                patterns.Add(_patternParser.ParsePattern());
                if (ctx.Position == pos)
                {
                    ctx.Error(DiagnosticMessages.ParserUnexpectedTokenInPattern(ctx.GetText()));
                    ctx.Advance();
                    break;
                }
            }

            while (ctx.Match("when"))
            {
                guards.Add(ParseGuardExpression());
            }

            ctx.Expect("=>");
            mainPattern = patterns.Count == 1
                ? patterns[0]
                : new TuplePattern { Elements = patterns };
        }

        var body = _exprParser.ParseExpr();
        NormalizeCurriedPatternBranch(ref mainPattern, ref body);

        var branch = new PatternBranch();
        branch.SetSpan(ctx.SpanFrom(startToken));
        branch.SetPattern(mainPattern);
        var guard = CombineGuards(guards);
        if (guard is not null)
        {
            branch.SetGuard(guard);
        }
        branch.SetExpression(body);
        return branch;
    }

    private bool HasCurriedPatternBeforeNextArrow()
    {
        var depth = 0;
        for (var offset = 0; offset < 64; offset++)
        {
            var token = ctx.Peek(offset);
            if (token is EofToken)
            {
                return false;
            }

            var text = ctx.GetText(token);
            if (depth == 0)
            {
                if (text == "=>")
                {
                    return true;
                }
                if (text is "," or "}")
                {
                    return false;
                }
            }

            depth += text switch
            {
                "(" or "[" or "{" => 1,
                ")" or "]" or "}" => -1,
                _ => 0
            };
            depth = Math.Max(0, depth);
        }

        return false;
    }

    private static void NormalizeCurriedPatternBranch(ref Pattern mainPattern, ref EidosAstNode body)
    {
        if (body is not LambdaExpr lambda || lambda.Parameters.Count == 0)
        {
            return;
        }

        var patterns = new List<Pattern> { mainPattern };

        var current = lambda;
        while (true)
        {
            patterns.AddRange(current.Parameters);

            if (current.Body is LambdaExpr nested && nested.Parameters.Count > 0)
            {
                current = nested;
                continue;
            }

            if (current.Body is not null)
            {
                body = current.Body;
            }

            break;
        }

        mainPattern = patterns.Count == 1
            ? patterns[0]
            : new TuplePattern { Elements = patterns };
    }

    private EidosAstNode ParseGuardExpression()
    {
        if (!GuardExpressionLookahead.HasTopLevelPatternGuardSource(ctx))
        {
            return _exprParser.ParseExprNoLambda();
        }

        var startToken = ctx.Current;
        var pattern = _patternParser.ParsePattern();
        if (ctx.Match("<-"))
        {
            var source = _exprParser.ParseExprNoLambda();
            var guard = new PatternGuardExpr();
            guard.SetSpanValue(ctx.SpanFrom(startToken));
            guard.SetPattern(pattern);
            guard.SetSourceExpression(source);
            return guard;
        }

        return _exprParser.ParseExprNoLambda();
    }

    private static EidosAstNode? CombineGuards(IReadOnlyList<EidosAstNode> guards)
    {
        if (guards.Count == 0)
        {
            return null;
        }

        if (guards.Count == 1)
        {
            return guards[0];
        }

        var sequential = new SequentialGuardExpr();
        sequential.SetSpanValue(new SourceSpan(guards[0].Span.Location, guards[^1].Span.EndPosition - guards[0].Span.Position));
        foreach (var guard in guards)
        {
            sequential.AddGuard(guard);
        }

        return sequential;
    }

    private bool IsBranchTerminator()
    {
        var text = ctx.GetText();
        return text == "=>" || text == "when" || text == "," || text == "}";
    }

    #endregion

    #region FuncDecl

    private FuncDecl ParseFuncDecl(List<AstAttribute> attrs, bool isExport)
    {
        var startToken = ctx.Current;
        ctx.Expect("func");
        var name = ParseFunctionName();

        var typeParams = _typeParser.TryParseTypeParams();
        _typeParser.ApplyGenericWhereClause(typeParams ?? []);
        ctx.Expect(":", DiagnosticMessages.ParserExpectedColonBeforeSignature);
        var signature = _typeParser.ParseType();
        var requiredAbilities = ParseNeedClause();
        _typeParser.ApplyGenericWhereClause(typeParams ?? []);

        ctx.Match(";");

        var decl = new FuncDecl();
        decl.SetSpan(ctx.SpanFrom(startToken));
        decl.SetName(name);
        decl.SetTypeParams(typeParams ?? []);
        decl.SetSignature(signature);
        decl.SetRequiredAbilities(MergeSignatureEffects(signature, requiredAbilities));
        decl.SetAttributes(attrs);
        decl.SetExported(isExport);
        return decl;
    }

    private List<EffectRequirementNode> ParseNeedClause()
    {
        if (!ctx.Match(WellKnownStrings.Keywords.Need))
        {
            return [];
        }

        var required = new List<EffectRequirementNode>();
        if (!IsEffectIdentifier(ctx.Current))
        {
            ctx.Error(DiagnosticMessages.ParserNeedClauseRequiresEffectPath);
            return required;
        }

        required.Add(ParseEffectRequirement());
        while (ctx.Match(","))
        {
            if (!IsEffectIdentifier(ctx.Current))
            {
                ctx.Error(DiagnosticMessages.ParserNeedClauseRequiresEffectPath);
                break;
            }

            required.Add(ParseEffectRequirement());
        }

        return required;
    }

    private EffectRequirementNode ParseEffectRequirement()
    {
        var startToken = ctx.Current;
        var path = QualifiedPathParser.Parse(
            ctx,
            IsEffectIdentifier,
            DiagnosticMessages.ParserExpectedTypeIdentifierAfterQualifiedSeparator);

        if (ctx.Check("(") || ctx.Check("["))
        {
            ctx.Error(DiagnosticMessages.ParserNeedClauseAllowsEffectPathsOnly);
        }

        return new EffectRequirementNode
        {
            Path = path,
            Span = ctx.SpanFrom(startToken)
        };
    }

    private static bool IsEffectIdentifier(Token token) =>
        TokenKind.IsIdentifier(token) || TokenKind.IsAnyIdentifier(token);

    private static void AddSignatureNeedClause(TypeNode signature, ICollection<DeclarationClause> clauses)
    {
        var effects = new List<EffectRequirementNode>();
        var current = signature;
        while (current is ArrowType arrow)
        {
            foreach (var effect in arrow.RequiredEffects)
            {
                AddEffectIfMissing(effects, effect);
            }

            current = arrow.ReturnType;
        }

        if (effects.Count == 0)
        {
            return;
        }

        var clause = new DeclarationClause();
        clause.SetKind(DeclarationClauseKind.Need, WellKnownStrings.Keywords.Need);
        foreach (var effect in effects)
        {
            clause.AddArgument(string.Join(WellKnownStrings.Separators.Path, effect.Path));
        }

        clause.SetSpan(effects[0].Span);
        clauses.Add(clause);
    }

    private string ParseFunctionName()
    {
        if (InternalNameParser.TryParseLeadingDoubleUnderscoreName(ctx, out var internalName))
        {
            return internalName;
        }

        if (ctx.Check("(") && TokenKind.IsOperatorIdentifier(ctx.Peek(1)) && ctx.CheckPeek(2, ")"))
        {
            ctx.Advance();
            var name = ctx.GetText();
            ctx.Advance();
            ctx.Expect(")", DiagnosticMessages.ParserExpectedRightParenAfterOperatorFunctionName);
            return name;
        }

        if (TokenKind.IsIdentifier(ctx.Current))
        {
            var name = ctx.GetText();
            ctx.Advance();
            return name;
        }

        ctx.Error(DiagnosticMessages.ParserExpectedFunctionName);
        var fallback = ctx.GetText();
        ctx.Advance();
        return fallback;
    }

    private string ParseCompileTimeDeclarationName()
    {
        if (InternalNameParser.TryParseLeadingDoubleUnderscoreName(ctx, out var internalName))
        {
            return internalName;
        }

        var name = ctx.GetText();
        if (!TokenKind.IsAnyIdentifier(ctx.Current))
        {
            ctx.Error(DiagnosticMessages.ParserUnexpectedToken(name));
        }

        if (!ctx.IsEof)
        {
            ctx.Advance();
        }

        return name;
    }

    #endregion


    #region LetDecl

    public LetDecl ParseLetDecl(List<AstAttribute> attrs, bool isExport = false)
    {
        var startToken = ctx.Current;
        ctx.Expect("let");
        var isMutable = ctx.Match(WellKnownStrings.Keywords.Mut);
        var pattern = _patternParser.ParsePattern();
        TypeNode? typeAnnotation = null;
        if (ctx.Match(":"))
        {
            typeAnnotation = _typeParser.ParseType();
        }

        var value = ParseInitializerOrRecovery(startToken);
        ctx.Match(";");

        var decl = new LetDecl();
        decl.SetSpan(ctx.SpanFrom(startToken));
        decl.SetMutable(isMutable);
        decl.SetPattern(pattern);
        decl.SetTypeAnnotation(typeAnnotation);
        decl.SetValue(value);
        decl.SetAttributes(attrs);
        decl.SetExported(isExport);
        return decl;
    }

    private EidosAstNode ParseInitializerOrRecovery(Token startToken)
    {
        if (ctx.Match("="))
        {
            return _exprParser.ParseExpr();
        }

        ctx.Error(DiagnosticMessages.ParserExpectedToken("="));
        return CreateMissingInitializerLiteral(startToken);
    }

    private static LiteralExpr CreateMissingInitializerLiteral(Token token)
    {
        var literal = new LiteralExpr();
        literal.SetSpan(new SourceSpan(token.Location, 0));
        literal.SetLiteral("0");
        literal.MarkRecoveredError(AstRecoveryReasons.ParserMissingInitializer);
        return literal;
    }

    public LetQuestionDecl ParseLetQuestionDecl(List<AstAttribute> attrs)
    {
        var startToken = ctx.Current;
        ctx.Expect("let");
        ctx.Expect("?");
        var pattern = _patternParser.ParsePattern();
        ctx.Expect("=");
        var value = _exprParser.ParseExpr();
        ctx.Match(";");

        var decl = new LetQuestionDecl();
        decl.SetSpan(ctx.SpanFrom(startToken));
        decl.SetPattern(pattern);
        decl.SetValue(value);
        decl.SetAttributes(attrs);
        return decl;
    }

    #endregion

    #region TypeDef (ADT)

    public AdtDef ParseTypeDef(List<AstAttribute> attrs, bool isExport)
    {
        var startToken = ctx.Current;
        ctx.Expect("type");
        var name = ParseCompileTimeDeclarationName();

        var typeParams = _typeParser.TryParseTypeParams();
        _typeParser.ApplyGenericWhereClause(typeParams ?? []);

        // Type alias: type Name = Target
        if (ctx.Check("="))
        {
            ctx.Advance();
            var target = _typeParser.ParseType();
            ctx.Match(";");
            var adt = new AdtDef();
            adt.SetSpan(ctx.SpanFrom(startToken));
            adt.SetName(name);
            adt.SetTypeParams(typeParams ?? []);
            adt.SetTypeAlias(target);
            adt.SetAttributes(attrs);
            adt.SetExported(isExport);
            return adt;
        }

        ctx.Expect("{");

        ParseAdtBody(out var constructors, out var fields);

        ctx.Expect("}");

        if (fields.Count == 0 &&
            constructors.Count == 1 &&
            string.Equals(constructors[0].Name, name, StringComparison.Ordinal) &&
            constructors[0].NamedArgs.Count > 0)
        {
            fields.AddRange(constructors[0].NamedArgs);
        }

        var adtDef = new AdtDef();
        adtDef.SetSpan(ctx.SpanFrom(startToken));
        adtDef.SetName(name);
        adtDef.SetTypeParams(typeParams ?? []);
        adtDef.SetConstructors(constructors);
        adtDef.SetFields(fields);
        adtDef.SetAttributes(attrs);
        adtDef.SetExported(isExport);
        return adtDef;
    }

    private static List<EffectRequirementNode> MergeSignatureEffects(
        TypeNode signature,
        IReadOnlyList<EffectRequirementNode> clauseEffects)
    {
        var merged = new List<EffectRequirementNode>();
        var current = signature;
        while (current is ArrowType arrow)
        {
            foreach (var effect in arrow.RequiredEffects)
            {
                AddEffectIfMissing(merged, effect);
            }

            current = arrow.ReturnType;
        }

        foreach (var effect in clauseEffects)
        {
            AddEffectIfMissing(merged, effect);
        }

        return merged;
    }

    private static void AddEffectIfMissing(
        ICollection<EffectRequirementNode> effects,
        EffectRequirementNode candidate)
    {
        if (!effects.Any(existing => existing.Path.SequenceEqual(candidate.Path, StringComparer.Ordinal)))
        {
            effects.Add(candidate);
        }
    }

    private void ParseAdtBody(out List<Constructor> constructors, out List<Field> fields)
    {
        constructors = [];
        fields = [];

        if (!ctx.Check("}") && IsFieldLookahead())
        {
            fields.Add(ParseField());
            while (ctx.Match(","))
            {
                if (ctx.Check("}"))
                {
                    break;
                }

                fields.Add(ParseField());
            }

            return;
        }

        var parsedConstructor = false;
        while (!ctx.Check("}") && !ctx.IsEof)
        {
            if (parsedConstructor)
            {
                if (!TryParseAdtConstructorSeparator())
                {
                    ctx.Error(DiagnosticMessages.ParserExpectedCommaBetweenAdtConstructors);
                }

                if (ctx.Check("}"))
                {
                    break;
                }
            }
            else
            {
                if (TryParseAdtConstructorSeparator())
                {
                    if (ctx.Check("}"))
                    {
                        break;
                    }
                }
            }

            constructors.Add(ParseConstructor());
            parsedConstructor = true;
        }
    }

    private bool TryParseAdtConstructorSeparator()
    {
        if (ctx.Match(","))
        {
            return true;
        }

        if (ctx.Match("|"))
        {
            if (ctx.UsesDotNamespaces)
            {
                ctx.Error(DiagnosticMessages.ParserAdtPipeSeparatorRemoved, ctx.Peek(-1).Location);
            }
            return true;
        }

        return false;
    }

    private bool IsFieldLookahead()
    {
        return TokenKind.IsIdentifier(ctx.Current) && ctx.CheckPeek(1, ":");
    }

    private Constructor ParseConstructor()
    {
        var startToken = ctx.Current;
        var name = ParseCompileTimeDeclarationName();
        var typeParams = _typeParser.TryParseTypeParams();

        var ctor = new Constructor();
        ctor.SetSpan(ctx.SpanFrom(startToken));
        ctor.SetName(name);
        ctor.SetTypeParams(typeParams ?? []);

        if (ctx.Check("("))
        {
            ctx.Advance();
            if (!ctx.Check(")"))
            {
                ctor.AddPositionalArg(_typeParser.ParseType());
                while (ctx.Match(","))
                    ctor.AddPositionalArg(_typeParser.ParseType());
            }
            ctx.Expect(")");
        }
        else if (ctx.Check("{"))
        {
            ctx.Advance();
            if (!ctx.Check("}"))
            {
                ParseConstructorBlockEntry(ctor);
                while (ctx.Match(","))
                {
                    if (ctx.Check("}"))
                    {
                        break;
                    }

                    ParseConstructorBlockEntry(ctor);
                }
            }
            ctx.Expect("}");
        }

        if (ctx.Match("->"))
        {
            ctor.SetReturnType(_typeParser.ParseType());
        }

        ctor.SetSpan(ctx.SpanFrom(startToken));
        return ctor;
    }

    private void ParseConstructorBlockEntry(Constructor ctor)
    {
        if (!TokenKind.IsIdentifier(ctx.Current))
        {
            ctx.Error(DiagnosticMessages.ParserExpectedIdentifierAfterQualifiedSeparator);
            ctx.Advance();
            return;
        }

        if (ctx.CheckPeek(1, ":"))
        {
            ctor.AddNamedArg(ParseField());
            return;
        }

        if (ctx.CheckPeek(1, "="))
        {
            ctx.Error("constructor named blocks no longer accept 'name = expr'; put trait bridge facts in 'Name :: instance Trait for Type { Ctor => { name = expr } }'");
            _ = ParseConstructorConstant();
            return;
        }

        ctx.Error(DiagnosticMessages.ParserUnexpectedToken(ctx.GetText()));
        ctx.Advance();
    }

    private ConstructorConstant ParseConstructorConstant()
    {
        var startToken = ctx.Current;
        var name = ctx.GetText();
        ctx.Advance();
        ctx.Expect("=");
        var value = _exprParser.ParseExprNoLambda();

        var constant = new ConstructorConstant();
        constant.SetSpan(ctx.SpanFrom(startToken));
        constant.SetName(name);
        constant.SetValue(value);
        return constant;
    }

    private Field ParseField(string separator = ":")
    {
        var startToken = ctx.Current;
        var name = ctx.GetText();
        ctx.Advance();
        ctx.Expect(separator);
        var type = _typeParser.ParseType();

        var field = new Field();
        field.SetSpan(ctx.SpanFrom(startToken));
        field.SetName(name);
        field.SetType(type);
        return field;
    }

    #endregion

    #region EffectDef / TraitDef

    public EffectDef ParseEffectDef(List<AstAttribute> attrs, bool isExport)
    {
        var startToken = ctx.Current;
        ctx.Expect("ability");
        var name = ParseCompileTimeDeclarationName();
        _typeParser.TryParseTypeParams();
        SkipBalancedBlock();

        var def = new EffectDef();
        def.SetSpan(ctx.SpanFrom(startToken));
        def.SetName(name);
        def.SetAttributes(attrs);
        def.SetExported(isExport);
        return def;
    }

    private List<TraitRef> ParseEffectRequires()
    {
        if (!ctx.Match(WellKnownStrings.Keywords.Requires))
        {
            return [];
        }

        var required = new List<TraitRef> { _typeParser.ParseTraitRef() };
        while (ctx.Match("+"))
        {
            required.Add(_typeParser.ParseTraitRef());
        }

        return required;
    }

    public TraitDef ParseTraitDef(List<AstAttribute> attrs, bool isExport)
    {
        var startToken = ctx.Current;
        ctx.Expect("trait");
        var name = ParseCompileTimeDeclarationName();

        var typeParams = _typeParser.TryParseTypeParams();

        // Parse supertrait clause: ": TraitRef + TraitRef*"
        var superTraits = new List<Ast.Types.TraitRef>();
        if (ctx.Check(":") && !IsColonFollowedByTypeParamName())
        {
            ctx.Advance(); // consume ":"
            superTraits.Add(_typeParser.ParseTraitRef());
            while (ctx.Match("+"))
            {
                superTraits.Add(_typeParser.ParseTraitRef());
            }
        }

        _typeParser.ApplyGenericWhereClause(typeParams ?? []);

        var funcs = new List<FuncDef>();
        if (ctx.Check("{"))
        {
            ctx.Advance();
            while (!ctx.Check("}") && !ctx.IsEof)
            {
                var funcAttrs = ParseAttributes();
                if (ctx.Check(WellKnownStrings.Keywords.Func))
                {
                    funcs.Add(ParseFuncDef(funcAttrs, false));
                }
                else
                {
                    ctx.Error(DiagnosticMessages.ParserExpectedTraitBodyMember(ctx.GetText()));
                    ctx.Advance();
                }
                ctx.Match(";");
            }
            ctx.Expect("}");
        }

        ctx.Match(";");

        var def = new TraitDef();
        def.SetSpan(ctx.SpanFrom(startToken));
        def.SetName(name);
        def.SetTypeParams(typeParams ?? []);
        def.SetSuperTraits(superTraits);
        def.SetMethods(funcs);
        // def.SetProofs removed during migration
        def.SetAttributes(attrs);
        def.SetExported(isExport);
        return def;
    }

    /// <summary>
    /// Checks if ":" is followed by a type parameter name (e.g., "T" in "[T: Eq]"),
    /// indicating it's a kind/constraint annotation rather than a supertrait clause.
    /// </summary>
    private bool IsColonFollowedByTypeParamName()
    {
        // If we just finished parsing type params (we're at "]"), the colon is part of
        // a different construct. But TryParseTypeParams already consumed the "]",
        // so if we see ":" here after type params, it's a supertrait clause.
        // This helper exists for edge cases in grammar-based parsing paths.
        return false;
    }

    #endregion

    #region Import / Link / Module

    public ImportDecl ParseImportDecl(List<AstAttribute> attrs, bool isExport)
    {
        var startToken = ctx.Current;
        ctx.Expect("import");
        if (ctx.IsNameFirstSyntax)
        {
            return ParseNameFirstImportDecl(attrs, isExport, startToken);
        }

        var parts = new List<string>();
        string? packageAlias = null;
        var firstToken = ctx.Current;
        var firstSegment = ctx.GetText();
        if (!TokenKind.IsAnyIdentifier(ctx.Current))
        {
            ctx.Error(DiagnosticMessages.ParserExpectedIdentifierAfterQualifiedSeparator);
        }

        if (!ctx.IsEof)
        {
            ctx.Advance();
        }

        if (ctx.Check("::") && TokenKind.IsAnyIdentifier(ctx.Peek(1)))
        {
            ctx.Advance();
            packageAlias = firstSegment;
            parts.Add(ParseModulePathSegment());
        }
        else
        {
            if (!TokenKind.IsAnyIdentifier(firstToken))
            {
                ctx.Error(DiagnosticMessages.ParserExpectedIdentifierAfterQualifiedSeparator);
            }

            parts.Add(firstSegment);
        }

        while (ctx.Match(".") || ctx.Match("/"))
        {
            parts.Add(ParseModulePathSegment());
        }

        var decl = new ImportDecl();
        decl.SetPackageAlias(packageAlias);
        decl.SetModulePath(parts);

        if (ctx.Match(ctx.UsesDotNamespaces ? "." : "::"))
        {
            if (ctx.Match("*"))
            {
                decl.SetImportKind(ImportKind.Wildcard);
            }
            else if (ctx.Check("{"))
            {
                ctx.Advance();
                decl.SetImportKind(ImportKind.Selective);
                if (!ctx.Check("}"))
                {
                    decl.AddSelectiveImport(ParseSelectiveImportItem());
                    while (ctx.Match(","))
                    {
                        if (ctx.Check("}"))
                        {
                            break;
                        }

                        decl.AddSelectiveImport(ParseSelectiveImportItem());
                    }
                }
                ctx.Expect("}");
            }
        }

        if (ctx.Match("as"))
        {
            var alias = ctx.GetText();
            if (decl.Kind == ImportKind.Module && !TokenKind.IsAnyIdentifier(ctx.Current))
            {
                ctx.Error(DiagnosticMessages.ParserExpectedIdentifierAfterQualifiedSeparator);
            }

            if (!ctx.IsEof)
            {
                ctx.Advance();
            }

            decl.SetAlias(alias);
        }

        decl.SetAttributes(attrs);
        decl.SetExported(isExport);
        decl.SetSpan(ctx.SpanFrom(startToken));
        return decl;
    }

    private ImportDecl ParseNameFirstImportDecl(List<AstAttribute> attrs, bool isExport, Token startToken)
    {
        var (packageAlias, parts) = ParseNameFirstImportModulePath();

        var decl = new ImportDecl();
        decl.SetPackageAlias(packageAlias);
        decl.SetModulePath(parts);

        if (ctx.Match(ctx.UsesDotNamespaces ? "." : "::"))
        {
            if (ctx.Match("*"))
            {
                decl.SetImportKind(ImportKind.Wildcard);
            }
            else if (ctx.Check("{"))
            {
                ctx.Advance();
                decl.SetImportKind(ImportKind.Selective);
                if (!ctx.Check("}"))
                {
                    decl.AddSelectiveImport(ParseSelectiveImportItem());
                    while (ctx.Match(","))
                    {
                        if (ctx.Check("}"))
                        {
                            break;
                        }

                        decl.AddSelectiveImport(ParseSelectiveImportItem());
                    }
                }
                ctx.Expect("}");
            }
        }

        if (ctx.Match("as"))
        {
            var alias = ctx.GetText();
            if (decl.Kind == ImportKind.Module)
            {
                // 0.6.0-alpha.1 name-first mode: module alias imports must use the
                // `Alias :: import Module.Path;` binding form, not `import ... as`.
                ctx.Error(DiagnosticMessages.ParserImportAliasUseNameFirstForm);
            }
            else if (!TokenKind.IsAnyIdentifier(ctx.Current))
            {
                ctx.Error(DiagnosticMessages.ParserExpectedIdentifierAfterQualifiedSeparator);
            }

            if (!ctx.IsEof)
            {
                ctx.Advance();
            }

            if (decl.Kind != ImportKind.Module)
            {
                decl.SetAlias(alias);
            }
        }

        var clauses = ParseDeclarationClauseZone([]);
        ctx.Match(";");
        decl.SetAttributes(attrs);
        decl.SetClauses(clauses);
        decl.SetExported(isExport);
        decl.SetSpan(ctx.SpanFrom(startToken));
        RegisterImportedNamespace(decl);
        return decl;
    }

    /// <summary>
    /// Parses a name-first module-alias import binding: <c>Alias :: import Module.Path;</c>.
    /// The binding name and <c>:: import</c> have already been consumed by the caller.
    /// Only plain module paths are accepted; selective/wildcard tails are rejected here
    /// because the binding form is specifically the module-alias case.
    /// </summary>
    private ImportDecl ParseNameFirstImportBinding(List<AstAttribute> attrs, bool isExport, Token startToken, string alias)
    {
        if (!IsValidatedModuleAliasName(alias))
        {
            ctx.Error(DiagnosticMessages.ParserExpectedIdentifierAfterQualifiedSeparator);
        }

        var (packageAlias, parts) = ParseNameFirstImportModulePath();

        var decl = new ImportDecl();
        decl.SetPackageAlias(packageAlias);
        decl.SetModulePath(parts);
        decl.SetAlias(alias);

        // The binding form `Alias :: import Module.Path;` only names a module alias;
        // selective / wildcard tails are not part of this surface.
        if (ctx.Check(ctx.UsesDotNamespaces ? "." : "::") || ctx.Match("as"))
        {
            ctx.Error(DiagnosticMessages.ParserImportBindingExpectsModulePath);
        }

        var clauses = ParseDeclarationClauseZone([]);
        ctx.Match(";");
        decl.SetAttributes(attrs);
        decl.SetClauses(clauses);
        decl.SetExported(isExport);
        decl.SetSpan(ctx.SpanFrom(startToken));
        RegisterImportedNamespace(decl);
        return decl;
    }

    private void RegisterImportedNamespace(ImportDecl decl)
    {
        if (decl.Kind != ImportKind.Module)
        {
            return;
        }

        ctx.RegisterNamespaceRoot(decl.Alias ?? decl.ModulePath.LastOrDefault());
        ctx.RegisterNamespaceRoot(decl.PackageAlias);
        if (decl.ModulePath.Count > 1)
        {
            ctx.RegisterNamespaceRoot(decl.ModulePath[0]);
        }
    }

    private static bool IsValidatedModuleAliasName(string alias) => alias.Length > 0;

    /// <summary>
    /// Parses a 0.6.0-alpha.1 import module path: an optional <c>packageAlias::</c> prefix
    /// followed by dot- or slash-separated module path segments. Returns the package
    /// alias (null when absent) and the module path segments.
    /// </summary>
    private (string? PackageAlias, List<string> Parts) ParseNameFirstImportModulePath()
    {
        if (ctx.UsesDotNamespaces)
        {
            return ParseDotNamespaceImportPath();
        }

        var parts = new List<string>();
        string? packageAlias = null;
        var firstToken = ctx.Current;
        var firstSegment = ctx.GetText();
        if (!TokenKind.IsAnyIdentifier(ctx.Current))
        {
            ctx.Error(DiagnosticMessages.ParserExpectedIdentifierAfterQualifiedSeparator);
        }

        if (!ctx.IsEof)
        {
            ctx.Advance();
        }

        if (ctx.Check("::") && TokenKind.IsAnyIdentifier(ctx.Peek(1)))
        {
            ctx.Advance();
            packageAlias = firstSegment;
            parts.Add(ParseModulePathSegment());
        }
        else
        {
            if (!TokenKind.IsAnyIdentifier(firstToken))
            {
                ctx.Error(DiagnosticMessages.ParserExpectedIdentifierAfterQualifiedSeparator);
            }

            parts.Add(firstSegment);
        }

        while (ctx.Match(".") || ctx.Match("/"))
        {
            parts.Add(ParseModulePathSegment());
        }

        return (packageAlias, parts);
    }

    private (string? PackageAlias, List<string> Parts) ParseDotNamespaceImportPath()
    {
        var parts = new List<string>();
        if (!TokenKind.IsAnyIdentifier(ctx.Current))
        {
            ctx.Error(DiagnosticMessages.ParserExpectedIdentifierAfterQualifiedSeparator);
        }

        if (!ctx.IsEof)
        {
            parts.Add(ctx.GetText());
            ctx.Advance();
        }

        while (true)
        {
            if (ctx.Check(".") && TokenKind.IsAnyIdentifier(ctx.Peek(1)))
            {
                ctx.Advance();
                parts.Add(ParseModulePathSegment());
                continue;
            }

            if (ctx.Check("::") || ctx.Check("/"))
            {
                ctx.Error(DiagnosticMessages.ParserQualifiedDoubleColonRemoved);
                ctx.Advance();
                if (!TokenKind.IsAnyIdentifier(ctx.Current))
                {
                    ctx.Error(DiagnosticMessages.ParserExpectedIdentifierAfterQualifiedSeparator);
                    break;
                }

                parts.Add(ParseModulePathSegment());
                continue;
            }

            break;
        }

        return (null, parts);
    }

    private string ParseModulePathSegment()
    {
        var segment = ctx.GetText();
        if (!TokenKind.IsAnyIdentifier(ctx.Current))
        {
            ctx.Error(DiagnosticMessages.ParserExpectedIdentifierAfterQualifiedSeparator);
        }

        if (!ctx.IsEof)
        {
            ctx.Advance();
        }

        return segment;
    }

    private SelectiveImportNode ParseSelectiveImportItem()
    {
        var name = ctx.GetText();
        if (!TokenKind.IsAnyIdentifier(ctx.Current))
        {
            ctx.Error(DiagnosticMessages.ParserExpectedIdentifierAfterQualifiedSeparator);
        }

        if (!ctx.IsEof)
        {
            ctx.Advance();
        }

        string? alias = null;
        if (ctx.Match("as"))
        {
            alias = ctx.GetText();
            if (!TokenKind.IsAnyIdentifier(ctx.Current))
            {
                ctx.Error(DiagnosticMessages.ParserExpectedIdentifierAfterQualifiedSeparator);
            }

            if (!ctx.IsEof)
            {
                ctx.Advance();
            }
        }

        return new SelectiveImportNode
        {
            Name = name,
            Alias = alias
        };
    }

    public ModuleDecl ParseModuleDef(List<AstAttribute> attrs, bool isExport)
    {
        var startToken = ctx.Current;
        ctx.Expect("module");

        var parts = new List<string>();
        parts.Add(ParseModulePathSegment());

        while (ctx.Match("/"))
        {
            parts.Add(ParseModulePathSegment());
        }

        var decl = new ModuleDecl();
        decl.SetSpan(ctx.SpanFrom(startToken));
        decl.SetPath(parts);

        if (ctx.Check("{"))
        {
            ctx.Advance();
            var innerDecls = new List<Declaration>();
            while (!ctx.Check("}") && !ctx.IsEof)
            {
                if (ParseTopLevel() is Declaration d)
                    innerDecls.Add(d);
            }
            ctx.Expect("}");
            decl.SetDeclarations(innerDecls);
        }

        decl.SetAttributes(attrs);
        decl.SetExported(isExport);
        return decl;
    }

    #endregion
}
