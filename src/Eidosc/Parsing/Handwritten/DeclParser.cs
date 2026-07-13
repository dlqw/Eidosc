using System.Text;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Diagnostic;
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
        _typeParser.ApplyGenericWhereClause(typeParams ?? []);

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
                var value = _exprParser.ParseExpr();
                ctx.Match(";");
                return CreateNameFirstValueBinding(attrs, isExport, startToken, name, typeAnnotation: null, value, isComptime: true);
            }

            ctx.Error("comptime generic static value bindings are not supported in 0.5.0-alpha.1 phase 1.");
        }

        if ((typeParams is null || typeParams.Count == 0) && CanStartNameFirstValueExpr(ctx.Current))
        {
            var value = _exprParser.ParseExpr();
            ctx.Match(";");
            return CreateNameFirstValueBinding(attrs, isExport, startToken, name, typeAnnotation: null, value);
        }

        var signature = _typeParser.ParseType();
        if ((typeParams is null || typeParams.Count == 0) && ctx.Match("="))
        {
            var value = _exprParser.ParseExpr();
            ctx.Match(";");
            return CreateNameFirstValueBinding(attrs, isExport, startToken, name, signature, value);
        }

        return ParseTopLevelNameFirstCallableAfterSignature(attrs, startToken, name, typeParams ?? [], signature, isExport);
    }

    private List<AstAttribute> ParseAttributes()
    {
        var attrs = new List<AstAttribute>();
        while (ctx.Check("@"))
        {
            var startToken = ctx.Current;
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

            attrs.Add(attr);
        }
        return attrs;
    }

    private string ReadAttributeArg()
    {
        var parts = new List<string>();
        int depth = 0;
        while (!ctx.IsEof)
        {
            var text = ctx.GetText();
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
        var requiredAbilities = ParseFunctionClauseGroup(typeParams);

        List<PatternBranch>? body = null;
        if (ctx.Check("{"))
        {
            ctx.Advance();
            body = ParsePatternBranches();
            ctx.Expect("}");
        }
        requiredAbilities.AddRange(ParseFunctionClauseGroup(typeParams));

        var func = new FuncDef();
        func.SetSpan(ctx.SpanFrom(startToken));
        func.SetName(name);
        func.SetTypeParams(typeParams);
        func.SetSignature(signature);
        func.SetRequiredAbilities(MergeSignatureEffects(signature, requiredAbilities));
        func.SetBody(body ?? []);
        func.SetAttributes(attrs);
        func.SetExported(isExport);
        return func;
    }

    private LetDecl CreateNameFirstValueBinding(
        List<AstAttribute> attrs,
        bool isExport,
        Token startToken,
        string name,
        TypeNode? typeAnnotation,
        EidosAstNode value,
        bool isComptime = false)
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
        decl.SetExported(isExport);
        return decl;
    }

    private bool CanStartNameFirstValueExpr(Token token)
    {
        if (TokenKind.IsAnyLiteral(token))
        {
            return true;
        }

        if (TokenKind.IsIdentifier(token))
        {
            if (QualifiedPathParser.IsPackageQualifiedItemLookahead(ctx, TokenKind.IsTypeIdentifier))
            {
                return false;
            }

            return true;
        }

        if (TokenKind.IsTypeIdentifier(token) && ctx.GetText(ctx.Peek(1)) is "(" or "{")
        {
            return true;
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
        var requiredAbilities = ParseFunctionClauseGroup(typeParams);

        var func = new FuncDecl();
        func.SetSpan(ctx.SpanFrom(startToken));
        func.SetName(name);
        func.SetTypeParams(typeParams);
        func.SetSignature(signature);
        func.SetRequiredAbilities(MergeSignatureEffects(signature, requiredAbilities));
        func.SetComptime(isComptime);
        func.SetAttributes([]);
        return func;
    }

    private List<EffectRequirementNode> ParseFunctionClauseGroup(IReadOnlyList<TypeParam> typeParams)
    {
        var requiredAbilities = new List<EffectRequirementNode>();
        while (!ctx.IsEof)
        {
            if (ctx.Check(WellKnownStrings.Keywords.Where))
            {
                _typeParser.ApplyGenericWhereClause(typeParams);
                continue;
            }

            if (ctx.Check(WellKnownStrings.Keywords.Need))
            {
                requiredAbilities.AddRange(ParseNeedClause());
                continue;
            }

            break;
        }

        return requiredAbilities;
    }

    private void ParseGenericWhereClauseGroup(IReadOnlyList<TypeParam> typeParams)
    {
        while (ctx.Check(WellKnownStrings.Keywords.Where))
        {
            _typeParser.ApplyGenericWhereClause(typeParams);
        }
    }

    private AdtDef ParseNameFirstType(
        List<AstAttribute> attrs,
        bool isExport,
        Token startToken,
        string name,
        List<TypeParam> typeParams)
    {
        _typeParser.ApplyGenericWhereClause(typeParams);
        ParseGenericWhereClauseGroup(typeParams);
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
            alias.SetExported(isExport);
            return alias;
        }

        ctx.Expect("{");
        ParseAdtBody(out var constructors, out var fields);

        ctx.Expect("}");
        var adt = new AdtDef();
        adt.SetSpan(ctx.SpanFrom(startToken));
        adt.SetName(name);
        adt.SetTypeParams(typeParams);
        adt.SetConstructors(constructors);
        adt.SetFields(fields);
        adt.SetAttributes(attrs);
        adt.SetExported(isExport);
        return adt;
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

        _typeParser.ApplyGenericWhereClause(typeParams);
        ParseGenericWhereClauseGroup(typeParams);
        var funcs = new List<FuncDef>();
        var associatedTypes = new List<AssociatedTypeDecl>();
        var associatedConsts = new List<AssociatedConstDecl>();
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
                else if (IsNameFirstDeclarationStart())
                {
                    var member = ParseNameFirstTraitMember(funcAttrs);
                    switch (member)
                    {
                        case FuncDef funcDef:
                            funcs.Add(funcDef);
                            break;
                        case FuncDecl funcDecl:
                            funcs.Add(WrapFuncDecl(funcDecl));
                            break;
                        case AssociatedTypeDecl associatedType:
                            associatedTypes.Add(associatedType);
                            break;
                        case AssociatedConstDecl associatedConst:
                            associatedConsts.Add(associatedConst);
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
        ParseGenericWhereClauseGroup(typeParams);

        ctx.Match(";");
        var def = new TraitDef();
        def.SetSpan(ctx.SpanFrom(startToken));
        def.SetName(name);
        def.SetTypeParams(typeParams);
        def.SetSuperTraits(superTraits);
        def.SetMethods(funcs);
        def.SetAssociatedTypes(associatedTypes);
        def.SetAssociatedConsts(associatedConsts);
        def.SetAttributes(attrs);
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
        var requiredAbilities = ParseFunctionClauseGroup(typeParams);

        List<PatternBranch>? body = null;
        if (ctx.Check("{"))
        {
            ctx.Advance();
            body = ParsePatternBranches();
            ctx.Expect("}");
        }
        requiredAbilities.AddRange(ParseFunctionClauseGroup(typeParams));

        var func = new FuncDef();
        func.SetSpan(ctx.SpanFrom(startToken));
        func.SetName(name);
        func.SetTypeParams(typeParams);
        func.SetSignature(signature);
        func.SetRequiredAbilities(MergeSignatureEffects(signature, requiredAbilities));
        func.SetComptime(isComptime);
        func.SetBody(body ?? []);
        func.SetAttributes(attrs);
        func.SetExported(isExport);
        return func;
    }

    private Declaration ParseTopLevelNameFirstCallableAfterSignature(
        List<AstAttribute> attrs,
        Token startToken,
        string name,
        List<TypeParam> typeParams,
        TypeNode signature,
        bool isExport = false)
    {
        var requiredAbilities = ParseFunctionClauseGroup(typeParams);
        if (ctx.Match(";"))
        {
            var decl = new FuncDecl();
            decl.SetSpan(ctx.SpanFrom(startToken));
            decl.SetName(name);
            decl.SetTypeParams(typeParams);
            decl.SetSignature(signature);
            decl.SetRequiredAbilities(MergeSignatureEffects(signature, requiredAbilities));
            decl.SetAttributes(attrs);
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
        requiredAbilities.AddRange(ParseFunctionClauseGroup(typeParams));

        var func = new FuncDef();
        func.SetSpan(ctx.SpanFrom(startToken));
        func.SetName(name);
        func.SetTypeParams(typeParams);
        func.SetSignature(signature);
        func.SetRequiredAbilities(MergeSignatureEffects(signature, requiredAbilities));
        func.SetBody(body ?? []);
        func.SetAttributes(attrs);
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

        if (ctx.Check("{"))
        {
            ctx.Error(DiagnosticMessages.ParserEffectTagMembersNotSupported);
            SkipBalancedBlock();
        }
        else if (!ctx.Match(";"))
        {
            ctx.Error(DiagnosticMessages.ParserEffectTagRequiresSemicolon);
        }

        var def = new EffectDef();
        def.SetSpan(ctx.SpanFrom(startToken));
        def.SetName(name);
        def.SetAttributes(attrs);
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

            var bridgeDef = new InstanceDecl();
            bridgeDef.SetSpan(ctx.SpanFrom(startToken));
            bridgeDef.SetName(name);
            bridgeDef.SetTypeParams(typeParams);
            bridgeDef.SetTrait(traitRef);
            bridgeDef.SetTargetType(targetType);
            bridgeDef.SetUsesConstructorBridge(true);
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

        ctx.Expect("{");

        var methods = new List<FuncDef>();
        var associatedTypes = new List<AssociatedTypeDecl>();
        var associatedConsts = new List<AssociatedConstDecl>();
        while (!ctx.Check("}") && !ctx.IsEof)
        {
            var methodAttrs = ParseAttributes();
            if (ctx.Check(WellKnownStrings.Keywords.Func))
            {
                methods.Add(ParseFuncDef(methodAttrs, false));
            }
            else if (IsNameFirstDeclarationStart())
            {
                var member = ParseNameFirstInstanceMember(methodAttrs);
                switch (member)
                {
                    case FuncDef funcDef:
                        methods.Add(funcDef);
                        break;
                    case AssociatedTypeDecl associatedType:
                        associatedTypes.Add(associatedType);
                        break;
                    case AssociatedConstDecl associatedConst:
                        associatedConsts.Add(associatedConst);
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
        def.SetAttributes(attrs);
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
        var ctorName = ParseCompileTimeDeclarationName("constructor bridge fact");
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
        decl.SetSpan(ctx.SpanFrom(startToken));
        decl.SetPath(modulePath);
        decl.SetDeclarations(innerDecls);
        decl.SetAttributes(attrs);
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
        if (HasCurriedGuardBeforeNextArrow())
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

    private bool HasCurriedGuardBeforeNextArrow()
    {
        var depth = 0;
        var sawWhen = false;
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
                if (text == "when")
                {
                    sawWhen = true;
                }
                else if (text == "=>")
                {
                    return sawWhen;
                }
                else if (text is "," or "}")
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
        if (!TokenKind.IsTypeIdentifier(ctx.Current))
        {
            ctx.Error(DiagnosticMessages.ParserNeedClauseRequiresEffectPath);
            return required;
        }

        required.Add(ParseEffectRequirement());
        while (ctx.Match(","))
        {
            if (!TokenKind.IsTypeIdentifier(ctx.Current))
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
            TokenKind.IsTypeIdentifier,
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

    private string ParseRuntimeDeclarationName(string declarationKind)
    {
        if (InternalNameParser.TryParseLeadingDoubleUnderscoreName(ctx, out var internalName))
        {
            return internalName;
        }

        var name = ctx.GetText();
        if (!TokenKind.IsIdentifier(ctx.Current))
        {
            ctx.Error(DiagnosticMessages.ParserRuntimeDeclarationNameMustStartWithLowercase(declarationKind));
        }

        if (!ctx.IsEof)
        {
            ctx.Advance();
        }

        return name;
    }

    private string ParseCompileTimeDeclarationName(string declarationKind)
    {
        if (InternalNameParser.TryParseLeadingDoubleUnderscoreName(ctx, out var internalName))
        {
            return internalName;
        }

        var name = ctx.GetText();
        if (!TokenKind.IsTypeIdentifier(ctx.Current))
        {
            ctx.Error(DiagnosticMessages.ParserCompileTimeDeclarationNameMustStartWithUppercase(declarationKind));
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
        var name = ParseCompileTimeDeclarationName("type");

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
        string? separator = null;
        while (!ctx.Check("}") && !ctx.IsEof)
        {
            if (parsedConstructor)
            {
                var nextSeparator = TryParseAdtConstructorSeparator();
                if (nextSeparator == null)
                {
                    ctx.Error("Expected ',' between ADT constructors");
                }
                else
                {
                    separator = AcceptAdtConstructorSeparator(separator, nextSeparator);
                }

                if (ctx.Check("}"))
                {
                    break;
                }
            }
            else
            {
                var leadingSeparator = TryParseAdtConstructorSeparator();
                if (leadingSeparator != null)
                {
                    separator = AcceptAdtConstructorSeparator(separator, leadingSeparator);
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

    private string? TryParseAdtConstructorSeparator()
    {
        if (ctx.Match(","))
        {
            return ",";
        }

        if (ctx.Match("|"))
        {
            return "|";
        }

        return null;
    }

    private string AcceptAdtConstructorSeparator(string? current, string next)
    {
        if (current != null && !string.Equals(current, next, StringComparison.Ordinal))
        {
            ctx.Error("ADT constructor lists cannot mix ',' and '|' separators");
        }

        return current ?? next;
    }

    private bool IsFieldLookahead()
    {
        return TokenKind.IsIdentifier(ctx.Current) && ctx.CheckPeek(1, ":");
    }

    private Constructor ParseConstructor()
    {
        var startToken = ctx.Current;
        var name = ParseCompileTimeDeclarationName("constructor");
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

    private Field ParseField()
    {
        var startToken = ctx.Current;
        var name = ctx.GetText();
        ctx.Advance();
        ctx.Expect(":");
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
        var name = ParseCompileTimeDeclarationName("ability");
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
        var name = ParseCompileTimeDeclarationName("trait");

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
            if (!TokenKind.IsTypeIdentifier(firstToken))
            {
                ctx.Error(DiagnosticMessages.ParserModulePathSegmentMustStartWithUppercase);
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

        if (ctx.Match("::"))
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
            if (decl.Kind == ImportKind.Module && !TokenKind.IsTypeIdentifier(ctx.Current))
            {
                ctx.Error(DiagnosticMessages.ParserModuleAliasMustStartWithUppercase);
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

        if (ctx.Match("::"))
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
                // 0.5.0-alpha.1 name-first mode: module alias imports must use the
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

        ctx.Match(";");
        decl.SetAttributes(attrs);
        decl.SetExported(isExport);
        decl.SetSpan(ctx.SpanFrom(startToken));
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
            ctx.Error(DiagnosticMessages.ParserModuleAliasMustStartWithUppercase);
        }

        var (packageAlias, parts) = ParseNameFirstImportModulePath();

        var decl = new ImportDecl();
        decl.SetPackageAlias(packageAlias);
        decl.SetModulePath(parts);
        decl.SetAlias(alias);

        // The binding form `Alias :: import Module.Path;` only names a module alias;
        // selective / wildcard tails are not part of this surface.
        if (ctx.Check("::") || ctx.Match("as"))
        {
            ctx.Error(DiagnosticMessages.ParserImportBindingExpectsModulePath);
        }

        ctx.Match(";");
        decl.SetAttributes(attrs);
        decl.SetExported(isExport);
        decl.SetSpan(ctx.SpanFrom(startToken));
        return decl;
    }

    private bool IsValidatedModuleAliasName(string alias)
    {
        // Module alias names are compile-time module names and must start upper-case.
        return alias.Length > 0 && char.IsUpper(alias[0]);
    }

    /// <summary>
    /// Parses a 0.5.0-alpha.1 import module path: an optional <c>packageAlias::</c> prefix
    /// followed by dot- or slash-separated module path segments. Returns the package
    /// alias (null when absent) and the module path segments.
    /// </summary>
    private (string? PackageAlias, List<string> Parts) ParseNameFirstImportModulePath()
    {
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
            if (!TokenKind.IsTypeIdentifier(firstToken))
            {
                ctx.Error(DiagnosticMessages.ParserModulePathSegmentMustStartWithUppercase);
            }

            parts.Add(firstSegment);
        }

        while (ctx.Match(".") || ctx.Match("/"))
        {
            parts.Add(ParseModulePathSegment());
        }

        return (packageAlias, parts);
    }

    private string ParseModulePathSegment()
    {
        var segment = ctx.GetText();
        if (!TokenKind.IsTypeIdentifier(ctx.Current))
        {
            ctx.Error(DiagnosticMessages.ParserModulePathSegmentMustStartWithUppercase);
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
