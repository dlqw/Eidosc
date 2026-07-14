using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Diagnostic;
using Eidosc.Utils;

namespace Eidosc.Parsing.Handwritten;

public sealed partial class ExprParser(ParserContext ctx, PatternParser patternParser, TypeParser typeParser)
{
    public ExprParser(ParserContext ctx) : this(ctx, new PatternParser(ctx), new TypeParser(ctx)) { }

    private bool _allowLambda = true;
    private readonly Stack<HashSet<string>> _mutableBlockBindings = new();

    public EidosAstNode ParseExpr()
    {
        return ParseExpr(0);
    }

    public EidosAstNode ParseExpr(int minPrec)
    {
        return ParseExpr(minPrec, _allowLambda);
    }

    public EidosAstNode ParseExpr(int minPrec, bool allowLambda)
    {
        var prevLambda = _allowLambda;
        _allowLambda = allowLambda;
        try
        {
            var startToken = ctx.Current;
            var left = ParsePrefix();

            while (true)
            {
                if (!ctx.IsNameFirstSyntax && ctx.Check(":=") && Precedence.Assign >= minPrec)
                {
                    ctx.Advance();
                    var value = ParseExpr(Precedence.Assign);
                    var assignment = new Assignment();
                    assignment.SetSpan(ctx.SpanFrom(startToken));
                    assignment.SetTargetExpression(left);
                    assignment.SetValue(value);
                    left = assignment;
                    continue;
                }

                var text = ctx.GetText();

                // Check built-in operators before custom symbolic operators; some built-ins
                // are tokenized through the operatorIdentifier terminal for compatibility.
                var builtinPrec = Precedence.TryGetBinary(text);
                var isCustomOperator = builtinPrec == null && TokenKind.IsOperatorIdentifier(ctx.Current);
                var prec = builtinPrec ?? (isCustomOperator
                    ? new PrecEntry(Precedence.Additive, Assoc.Left)
                    : null);
                if (prec == null || prec.Value.Level < minPrec)
                    break;

                var (level, assoc) = prec.Value;
                ctx.Advance(); // consume operator

                var nextMinPrec = assoc == Assoc.Right ? level : level + 1;
                var right = ParseExpr(nextMinPrec);

                if (isCustomOperator)
                {
                    var infixCall = new InfixCallExpr();
                    infixCall.SetSpan(ctx.SpanFrom(startToken));
                    infixCall.SetLeft(left);
                    infixCall.SetFunctionName(text);
                    infixCall.SetRight(right);
                    left = infixCall;
                    continue;
                }

                var binary = new BinaryExpr();
                binary.SetSpan(ctx.SpanFrom(startToken));
                binary.SetLeft(left);
                binary.SetOperator(MapBinaryOp(text));
                binary.SetRight(right);
                left = binary;
            }

            return left;
        }
        finally
        {
            _allowLambda = prevLambda;
        }
    }

    public EidosAstNode ParseExprNoLambda()
    {
        return ParseExpr(0, false);
    }

    private EidosAstNode ParsePrefix()
    {
        var startToken = ctx.Current;
        var text = ctx.GetText();

        // Prefix unary operators
        var prec = Precedence.TryGetPrefix(text);
        if (prec != null)
        {
            ctx.Advance(); // consume prefix op
            var operand = ParseExpr(prec.Value.Level);
            var unary = new UnaryExpr();
            unary.SetSpan(ctx.SpanFrom(startToken));
            unary.SetOperator(MapUnaryOp(text));
            unary.SetOperand(operand);
            return unary;
        }

        var primary = ParsePrimary();
        return ParsePostfix(primary);
    }

    private EidosAstNode ParsePostfix(EidosAstNode left)
    {
        while (true)
        {
            // Function call: expr(args)
            if (ctx.Check("("))
            {
                left = ParseCall(left);
                continue;
            }

            // Type-arg application: expr[Types]
            if (ctx.Check("[") && IsTypeArgLookahead())
            {
                left = ParseTypeArgAccess(left);
                continue;
            }

            // Index access: expr[index]
            if (ctx.Check("["))
            {
                left = ParseIndexAccess(left);
                continue;
            }

            // Short record update: expr.{ field: value }
            if (ctx.Check(".") && ctx.CheckPeek(1, "{"))
            {
                left = ParseRecordUpdate(left);
                continue;
            }

            // Field access / method call: expr.ident
            if (ctx.Check(".") && TokenKind.IsAnyIdentifier(ctx.Peek(1)))
            {
                if (ctx.IsNameFirstSyntax && TryParseAssociatedConstExpr(left, out var associatedConst))
                {
                    left = associatedConst;
                    continue;
                }

                left = ParseFieldAccess(left);
                continue;
            }

            // Infix backtick: left `func_name` right
            if (ctx.Check("`"))
            {
                left = ParseInfixBacktick(left);
                continue;
            }

            if (ctx.Check("with"))
            {
                ctx.Error(DiagnosticMessages.ParserWithClauseSyntaxRemoved);
                ctx.Advance();
                if (ctx.Check("handler"))
                {
                    _ = ParseRemovedHandlerExpression();
                }
                else if (ctx.Check("{"))
                {
                    SkipBalancedBlock();
                }
                else if (!ctx.IsEof)
                {
                    ctx.Advance();
                }
                continue;
            }

            if (ctx.IsNameFirstSyntax && ctx.Check("given"))
            {
                left = ParseGivenExpr(left);
                continue;
            }

            // Pattern lambda: CtorExpr => body  or  (pat1, pat2) => body
            if (_allowLambda && ctx.Check("=>") && IsPatternLike(left))
            {
                var lambdaStart = left.Span.Position >= 0 ? ctx.Peek(-1) : ctx.Current;
                ctx.Expect("=>");
                var body = ParseExpr();
                var lambda = new LambdaExpr();
                lambda.SetSpan(ctx.SpanFrom(lambdaStart));
                var paramPat = ExprToPattern(left);
                if (paramPat != null)
                    lambda.AddParameter(paramPat);
                lambda.SetBody(body);
                left = lambda;
                continue;
            }

            break;
        }

        return left;
    }

    private bool TryParseAssociatedConstExpr(EidosAstNode left, out AssociatedConstExpr associatedConst)
    {
        associatedConst = null!;
        if (left is not PathExpr { IsTypePath: true } path)
        {
            return false;
        }

        var startToken = ctx.Current;
        ctx.Expect(".");
        var memberName = ctx.GetText();
        ctx.Advance();

        associatedConst = new AssociatedConstExpr();
        associatedConst.SetSpan(ctx.SpanFrom(startToken));
        associatedConst.SetTarget(CreateTypePathFromPathExpr(path));
        associatedConst.SetMemberName(memberName);
        return true;
    }

    private static TypePath CreateTypePathFromPathExpr(PathExpr path)
    {
        var typePath = new TypePath();
        typePath.SetPackageAlias(path.PackageAlias);
        typePath.ModulePath = [.. path.ModulePath];
        typePath.SetTypeName(path.Name);
        typePath.TypeArgs = [.. path.TypeArgs];
        typePath.SetSpan(path.Span);
        return typePath;
    }

    private GivenExpr ParseGivenExpr(EidosAstNode target)
    {
        var startToken = ctx.Current;
        ctx.Expect("given");
        var evidencePath = new List<string>();
        if (!TokenKind.IsAnyIdentifier(ctx.Current))
        {
            ctx.Error(DiagnosticMessages.ParserExpectedIdentifierAfterQualifiedSeparator);
        }
        else
        {
            evidencePath = QualifiedPathParser
                .ParseItemPath(
                    ctx,
                    TokenKind.IsAnyIdentifier,
                    DiagnosticMessages.ParserExpectedIdentifierAfterQualifiedSeparator)
                .ToPathParts();
        }

        var given = new GivenExpr();
        given.SetSpan(ctx.SpanFrom(startToken));
        given.SetTarget(target);
        given.SetEvidencePath(evidencePath);
        return given;
    }

    private IndexExpr ParseIndexAccess(EidosAstNode target)
    {
        var startToken = ctx.Current;
        ctx.Expect("[");

        EidosAstNode? index = null;
        if (ctx.Check("]"))
        {
            ctx.Error(DiagnosticMessages.ParserIndexExpressionRequiresIndex);
        }
        else
        {
            index = ParseExpr();
        }

        ctx.Expect("]");

        var indexExpr = new IndexExpr();
        indexExpr.SetSpan(ctx.SpanFrom(startToken));
        indexExpr.SetObject(target);
        if (index != null)
        {
            indexExpr.SetIndex(index);
        }
        else
        {
            indexExpr.MarkRecoveredMissingIndex();
        }

        return indexExpr;
    }

    private EidosAstNode ParsePrimary()
    {
        var startToken = ctx.Current;

        if (ctx.Check("loop"))
        {
            return ParseLoopExpr();
        }

        if (ctx.Check("break"))
        {
            return ParseBreakExpr();
        }

        if (ctx.Check("continue"))
        {
            return ParseContinueExpr();
        }

        if (ctx.Check("return"))
        {
            return ParseReturnExpr();
        }

        if (ctx.Check(WellKnownStrings.Keywords.Unreachable))
        {
            ctx.Advance();
            var unreachable = new UnreachableExpr();
            unreachable.Span = ctx.SpanFrom(startToken);
            return unreachable;
        }

        // Integer / float literal
        if (TokenKind.IsNumber(ctx.Current))
        {
            var text = ctx.GetText();
            ctx.Advance();
            var lit = new LiteralExpr();
            lit.SetSpan(ctx.SpanFrom(startToken));
            lit.SetLiteral(text);
            return lit;
        }

        // String literal
        if (TokenKind.IsString(ctx.Current))
        {
            var text = ctx.GetLiteralRawText();
            ctx.Advance();
            var lit = new LiteralExpr();
            lit.SetSpan(ctx.SpanFrom(startToken));
            lit.SetLiteral(text);
            return lit;
        }

        // Char literal
        if (TokenKind.IsChar(ctx.Current))
        {
            var text = ctx.GetLiteralRawText();
            ctx.Advance();
            var lit = new LiteralExpr();
            lit.SetSpan(ctx.SpanFrom(startToken));
            lit.SetLiteral(text);
            return lit;
        }

        // Boolean literal
        if (ctx.Check("true") || ctx.Check("false"))
        {
            var text = ctx.GetText();
            ctx.Advance();
            var lit = new LiteralExpr();
            lit.SetSpan(ctx.SpanFrom(startToken));
            lit.SetLiteral(text);
            return lit;
        }

        // fn — lambda
        if (ctx.Check("fn"))
        {
            return ParseLambda();
        }

        // if expression
        if (ctx.Check("if"))
        {
            if (ctx.CheckPeek(1, "let"))
            {
                return ParseIfLetExpr();
            }

            return ParseIfExpr();
        }

        // decision table expression
        if (ctx.Check(WellKnownStrings.Keywords.Decide))
        {
            return ParseDecideExpr();
        }

        // while-let expression
        if (ctx.Check("while") && ctx.CheckPeek(1, "let"))
        {
            return ParseWhileLetExpr();
        }

        if (ctx.Check("handler"))
        {
            return ParseRemovedHandlerExpression();
        }

        if (ctx.Check("do"))
        {
            return ParseDoExpr();
        }

        // match expression
        if (ctx.Check("match"))
        {
            return ParseMatchExpr();
        }

        // parenthesized / tuple / unit
        if (ctx.Check("("))
        {
            return ParseParenExpr();
        }

        // Block expression: { stmt; stmt; expr }
        if (ctx.Check("{"))
        {
            return ParseBlock();
        }

        // list literal
        if (ctx.Check("["))
        {
            return ParseListExpr();
        }

        // Contextual record literal: .{ field: value }
        if (ctx.Check(".") && ctx.CheckPeek(1, "{"))
        {
            return ParseContextualRecordLiteral();
        }

        // Mutable anonymous lambda parameter: mut x => expr
        if (_allowLambda &&
            ctx.Check(WellKnownStrings.Keywords.Mut) &&
            TokenKind.IsIdentifier(ctx.Peek(1)) &&
            ctx.CheckPeek(2, "=>"))
        {
            return ParseMutableIdentLambda();
        }

        // TypeIdentifier — could be constructor call Some(x) or bare
        if (TokenKind.IsTypeIdentifier(ctx.Current))
        {
            return ParsePathOrCtorExpr();
        }

        // identifier — could be path expression, anonymous lambda, or simple var
        if (TokenKind.IsIdentifier(ctx.Current))
        {
            return ParseIdentOrLambda();
        }

        // Wildcard lambda: _ => expr
        if (_allowLambda && ctx.Check("_") && ctx.CheckPeek(1, "=>"))
        {
            ctx.Advance(); // consume "_"
            ctx.Expect("=>");
            var body = ParseExpr();
            var lambda = new LambdaExpr();
            lambda.SetSpan(ctx.SpanFrom(startToken));
            var paramPat = new VarPattern();
            paramPat.SetSpan(ctx.SpanFrom(startToken));
            paramPat.SetName("_");
            lambda.AddParameter(paramPat);
            lambda.SetBody(body);
            return lambda;
        }

        // Wildcard as expression
        if (ctx.Match("_"))
        {
            var ident = new IdentifierExpr();
            ident.SetSpan(ctx.SpanFrom(startToken));
            ident.SetName("_");
            return ident;
        }

        ctx.Error(DiagnosticMessages.ParserExpectedExpression(ctx.GetText()));
        ctx.Advance(); // error recovery: skip unexpected token
        var errorLit = new LiteralExpr();
        errorLit.SetSpan(new SourceSpan(startToken.Location, 0));
        errorLit.SetLiteral("0");
        errorLit.MarkRecoveredError(AstRecoveryReasons.ParserExpectedExpression);
        return errorLit;
    }

    private EidosAstNode ParseParenExpr()
    {
        var startToken = ctx.Current;
        ctx.Expect("(");

        if (TokenKind.IsOperatorIdentifier(ctx.Current) && ctx.CheckPeek(1, ")"))
        {
            var ident = new IdentifierExpr();
            ident.SetSpan(ctx.SpanFrom(startToken));
            ident.SetName(ctx.GetText());
            ctx.Advance();
            ctx.Expect(")");
            return ident;
        }

        // Unit: ()
        if (ctx.Match(")"))
        {
            var lit = new LiteralExpr();
            lit.SetSpan(ctx.SpanFrom(startToken));
            lit.SetLiteral("()");
            return lit;
        }

        var first = ParseExpr();

        // Tuple: (a, b, ...)
        if (ctx.Check(","))
        {
            var tuple = new TupleExpr();
            tuple.Elements = [first];
            while (ctx.Match(","))
                tuple.Elements.Add(ParseExpr());
            ctx.Expect(")");
            return tuple;
        }

        ctx.Expect(")");
        return first;
    }

    private EidosAstNode ParseListExpr()
    {
        var startToken = ctx.Current;
        ctx.Expect("[");

        if (ctx.Check("]"))
        {
            ctx.Advance();
            var list = new ListExpr();
            list.SetSpan(ctx.SpanFrom(startToken));
            return list;
        }

        var first = ParseExpr();

        // Seq comprehension: [expr | pat <- list, filter, ...]
        if (ctx.Match("|"))
        {
            var comp = new ListComprehension();
            comp.Span = ctx.SpanFrom(startToken);
            comp.SetOutput(first);

            // Parse qualifiers
            comp.AddQualifier(ParseQualifier());
            while (ctx.Match(","))
            {
                if (ctx.Check("]")) break;
                comp.AddQualifier(ParseQualifier());
            }

            ctx.Expect("]");
            comp.Span = ctx.SpanFrom(startToken);
            return comp;
        }

        // Regular list: [a, b, c] or [head, ..tail]
        var regularList = new ListExpr();
        regularList.SetSpan(ctx.SpanFrom(startToken));
        regularList.AddElement(first);
        while (ctx.Match(","))
        {
            if (ctx.Check(".."))
            {
                ctx.Advance(); // consume ".."
                if (!ctx.Check("]"))
                {
                    regularList.AddElement(ParseExpr());
                }

                regularList.SetHasRest(true);
                break;
            }
            if (ctx.Check("]")) break;
            regularList.AddElement(ParseExpr());
        }

        ctx.Expect("]");
        regularList.SetSpan(ctx.SpanFrom(startToken));
        return regularList;
    }

    private Qualifier ParseQualifier()
    {
        var startToken = ctx.Current;

        if (!HasTopLevelGeneratorArrowBeforeQualifierBoundary())
        {
            var guardExpr = ParseExpr();
            var guard = new Qualifier();
            guard.Span = ctx.SpanFrom(startToken);
            guard.Kind = QualifierKind.Guard;
            guard.GuardExpression = guardExpr;
            return guard;
        }

        var pat = patternParser.ParsePattern();
        ctx.Expect("<-");

        var source = ParseExpr();
        var qual = new Qualifier();
        qual.Span = ctx.SpanFrom(startToken);
        qual.Kind = QualifierKind.Generator;
        qual.GeneratorPattern = pat;
        qual.GeneratorExpression = source;
        return qual;
    }

    private bool HasTopLevelGeneratorArrowBeforeQualifierBoundary()
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        for (var offset = 0; ; offset++)
        {
            var token = ctx.Peek(offset);
            var text = ctx.GetText(token);

            if (text == "<eof>")
            {
                return false;
            }

            if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
            {
                if (text == "<-")
                {
                    return true;
                }

                if (text is "," or "]")
                {
                    return false;
                }
            }

            switch (text)
            {
                case "(":
                    parenDepth++;
                    break;
                case ")" when parenDepth > 0:
                    parenDepth--;
                    break;
                case "[":
                    bracketDepth++;
                    break;
                case "]" when bracketDepth > 0:
                    bracketDepth--;
                    break;
                case "{":
                    braceDepth++;
                    break;
                case "}" when braceDepth > 0:
                    braceDepth--;
                    break;
            }
        }
    }

    private EidosAstNode ParsePathOrCtorExpr()
    {
        var startToken = ctx.Current;
        var path = ParsePathExpr(startToken);

        // Constructor call: Path(args) or Path{fields}
        if ((ctx.Check("(") || ctx.Check("{")) && IsConstructorPath(path))
        {
            return ParseCtorFromPath(path, startToken);
        }

        return path;
    }

    private PathExpr ParsePathExpr(Token startToken)
    {
        var parsedPath = QualifiedPathParser.ParseItemPath(
            ctx,
            TokenKind.IsAnyIdentifier,
            DiagnosticMessages.ParserExpectedIdentifierAfterQualifiedSeparator);

        var path = new PathExpr();
        path.SetSpan(ctx.SpanFrom(startToken));
        path.SetPackageAlias(parsedPath.PackageAlias);
        path.SetName(parsedPath.Name);
        path.SetModulePath(parsedPath.ModulePath);
        path.SetIsTypePath(parsedPath.Name.Length > 0 && char.IsUpper(parsedPath.Name[0]));

        // Optional type args: Path[Int, String]
        if (ctx.Check("[") && (path.IsTypePath || IsTypeArgLookahead()))
        {
            var genericArguments = typeParser.TryParseGenericArguments();
            if (genericArguments != null)
                path.SetGenericArguments(genericArguments);
        }

        return path;
    }

    private EidosAstNode ParseCtorFromPath(PathExpr path, Token startToken)
    {
        var ctor = new CtorExpr();
        ctor.SetConstructorName(path.Name);
        var constructorPath = new TypePath();
        constructorPath.SetTypeName(path.Name);
        constructorPath.SetPackageAlias(path.PackageAlias);
        constructorPath.ModulePath = [..path.ModulePath];
        constructorPath.TypeArgs = [..path.TypeArgs];
        constructorPath.GenericArguments = [..path.GenericArguments];
        constructorPath.SetSpan(path.Span);
        ctor.SetConstructorPath(constructorPath);

        if (ctx.Match("("))
        {
            if (!ctx.Check(")"))
            {
                ctor.AddPositionalArg(ParseExpr());
                while (ctx.Match(","))
                    ctor.AddPositionalArg(ParseExpr());
            }
            ctx.Expect(")");
        }
        else if (ctx.Match("{"))
        {
            var firstField = true;
            while (!ctx.Check("}") && !ctx.IsEof)
            {
                if (ctx.Check(".."))
                {
                    if (!firstField || ctor.UpdateBase != null)
                    {
                        ctx.Error(DiagnosticMessages.ParserRecordUpdateSpreadPosition);
                    }

                    ParseCtorUpdateBase(ctor);
                }
                else
                {
                    ctor.AddNamedArg(ParseFieldInit());
                }

                firstField = false;
                if (!ctx.Match(","))
                {
                    break;
                }
            }
            ctx.Expect("}");
        }

        ctor.SetSpan(ctx.SpanFrom(startToken));
        return ctor;
    }

    private ContextualRecordLiteralExpr ParseContextualRecordLiteral()
    {
        var startToken = ctx.Current;
        ctx.Expect(".");
        ctx.Expect("{");

        var literal = new ContextualRecordLiteralExpr();

        if (!ctx.Check("}"))
        {
            literal.AddNamedArg(ParseFieldInit());
            while (ctx.Match(","))
            {
                if (ctx.Check("}"))
                {
                    break;
                }

                literal.AddNamedArg(ParseFieldInit());
            }
        }

        ctx.Expect("}");
        literal.SetSpan(ctx.SpanFrom(startToken));
        return literal;
    }

    private static bool IsConstructorPath(PathExpr path)
    {
        return path.Name.Length > 0 && char.IsUpper(path.Name[0]);
    }

    private void ParseCtorUpdateBase(CtorExpr ctor)
    {
        ctx.Expect("..");
        ctor.SetUpdateBase(ParseExpr());
    }

    private RecordUpdateExpr ParseRecordUpdate(EidosAstNode baseExpression)
    {
        var startToken = ctx.Current;
        ctx.Expect(".");
        ctx.Expect("{");

        var update = new RecordUpdateExpr();
        update.SetBase(baseExpression);

        if (!ctx.Check("}"))
        {
            update.AddNamedArg(ParseFieldInit());
            while (ctx.Match(","))
            {
                if (ctx.Check("}"))
                {
                    break;
                }

                update.AddNamedArg(ParseFieldInit());
            }
        }

        ctx.Expect("}");
        update.SetSpan(ctx.SpanFrom(startToken));
        return update;
    }

    private FieldInit ParseFieldInit()
    {
        var fieldStart = ctx.Current;
        var fieldName = ctx.GetText();
        ctx.Advance();

        var field = new FieldInit();
        field.SetSpan(ctx.SpanFrom(fieldStart));
        field.SetFieldName(fieldName);

        if (ctx.Match(":"))
            field.SetValue(ParseExpr());
        else
            field.SetValue(new IdentifierExpr { Name = fieldName, IsConstructor = false });

        return field;
    }

    private EidosAstNode ParseIdentOrLambda()
    {
        var startToken = ctx.Current;

        // Anonymous lambda: x => expr
        if (_allowLambda && TokenKind.IsIdentifier(ctx.Current) && ctx.CheckPeek(1, "=>"))
        {
            var param = ctx.GetText();
            ctx.Advance(); // consume identifier
            ctx.Expect("=>");

            var body = ParseExpr();

            var lambda = new LambdaExpr();
            lambda.SetSpan(ctx.SpanFrom(startToken));
            var paramPat = new VarPattern();
            paramPat.SetSpan(ctx.SpanFrom(startToken));
            paramPat.SetName(param);
            lambda.AddParameter(paramPat);
            lambda.SetBody(body);
            return lambda;
        }

        // Namespace path expression: Module.Path.member or package_alias.Module.member.
        if (QualifiedPathParser.IsQualifiedPathLookahead(ctx))
        {
            var path = ParsePathExpr(startToken);
            // Constructor call via path
            if ((ctx.Check("(") || ctx.Check("{")) && IsConstructorPath(path))
                return ParseCtorFromPath(path, startToken);
            return path;
        }

        // Simple identifier
        var name = ctx.GetText();
        ctx.Advance();
        var ident = new IdentifierExpr();
        ident.SetSpan(ctx.SpanFrom(startToken));
        ident.SetName(name);
        return ident;
    }

    private EidosAstNode ParseMutableIdentLambda()
    {
        var startToken = ctx.Current;
        ctx.Expect(WellKnownStrings.Keywords.Mut);
        var param = ctx.GetText();
        ctx.Advance();
        ctx.Expect("=>");

        var body = ParseExpr();

        var lambda = new LambdaExpr();
        lambda.SetSpan(ctx.SpanFrom(startToken));
        var paramPat = new VarPattern();
        paramPat.SetSpan(ctx.SpanFrom(startToken));
        paramPat.SetName(param);
        paramPat.SetMutableBinding(true);
        lambda.AddParameter(paramPat);
        lambda.SetBody(body);
        return lambda;
    }

    private LoopExpr ParseLoopExpr()
    {
        var startToken = ctx.Current;
        ctx.Expect("loop");
        var body = ParseBlock();

        var loop = new LoopExpr();
        loop.Span = ctx.SpanFrom(startToken);
        loop.SetBody(body);
        return loop;
    }

    private BreakExpr ParseBreakExpr()
    {
        var startToken = ctx.Current;
        ctx.Expect("break");

        EidosAstNode? value = null;
        if (!ctx.Check(";") && !ctx.Check("}") && !ctx.Check(")") && !ctx.Check("]") && !ctx.Check(",") && !ctx.IsEof)
        {
            value = ParseExpr();
        }

        var breakExpr = new BreakExpr();
        breakExpr.Span = ctx.SpanFrom(startToken);
        breakExpr.SetValue(value);
        return breakExpr;
    }

    private ContinueExpr ParseContinueExpr()
    {
        var startToken = ctx.Current;
        ctx.Expect("continue");

        var continueExpr = new ContinueExpr();
        continueExpr.Span = ctx.SpanFrom(startToken);
        return continueExpr;
    }

    private ReturnExpr ParseReturnExpr()
    {
        var startToken = ctx.Current;
        ctx.Expect("return");

        var returnExpr = new ReturnExpr();
        returnExpr.Span = ctx.SpanFrom(startToken);
        if (!IsExpressionTerminator())
        {
            returnExpr.SetValue(ParseExpr());
        }

        returnExpr.Span = ctx.SpanFrom(startToken);
        return returnExpr;
    }

    private bool IsExpressionTerminator()
    {
        return ctx.Check(";") ||
               ctx.Check("}") ||
               ctx.Check(")") ||
               ctx.Check("]") ||
               ctx.Check(",") ||
               ctx.IsEof;
    }

    private EidosAstNode ParseLambda()
    {
        var startToken = ctx.Current;
        ctx.Expect("fn");

        var lambda = new LambdaExpr();
        lambda.SetSpan(ctx.SpanFrom(startToken));

        // Parameters: (p1, p2) or single pattern
        if (ctx.Check("("))
        {
            ctx.Advance();
            if (!ctx.Check(")"))
            {
                lambda.AddParameter(patternParser.ParsePattern());
                while (ctx.Match(","))
                    lambda.AddParameter(patternParser.ParsePattern());
            }
            ctx.Expect(")");
        }
        else
        {
            lambda.AddParameter(patternParser.ParsePattern());
        }

        ctx.Match("=>");
        var body = ParseExpr();
        lambda.SetBody(body);
        lambda.SetSpan(ctx.SpanFrom(startToken));
        return lambda;
    }

    private EidosAstNode ParseIfLetExpr()
    {
        var startToken = ctx.Current;
        ctx.Expect("if");
        ctx.Expect("let");

        var pattern = patternParser.ParsePattern();
        ctx.Expect("=");
        var matched = ParseExprNoLambda();
        ctx.Expect("then");

        var ifLet = new IfLetExpr();
        ifLet.SetPattern(pattern);
        ifLet.SetMatchedExpression(matched);
        ifLet.SetThenBranch(ParseBlockOrExpr());

        if (ctx.Match("else"))
        {
            ifLet.SetElseBranch(ParseBlockOrExpr());
        }

        ifLet.SetSpanValue(ctx.SpanFrom(startToken));
        return ifLet;
    }

    private EidosAstNode ParseWhileLetExpr()
    {
        var startToken = ctx.Current;
        ctx.Expect("while");
        ctx.Expect("let");

        var pattern = patternParser.ParsePattern();
        ctx.Expect("=");
        var matched = ParseExprNoLambda();
        ctx.Expect("then");

        var whileLet = new WhileLetExpr();
        whileLet.SetPattern(pattern);
        whileLet.SetMatchedExpression(matched);
        whileLet.SetBody(ParseBlockOrExpr());
        whileLet.SetSpanValue(ctx.SpanFrom(startToken));
        return whileLet;
    }

    private EidosAstNode ParseRemovedHandlerExpression()
    {
        var startToken = ctx.Current;
        ctx.Error(DiagnosticMessages.ParserHandlerSyntaxRemoved);
        ctx.Advance();
        while (!ctx.IsEof && !ctx.Check("{") && !ctx.Check(";") && !ctx.Check("}"))
        {
            ctx.Advance();
        }
        SkipBalancedBlock();

        var recovered = new IdentifierExpr();
        recovered.SetSpan(ctx.SpanFrom(startToken));
        recovered.SetName("handler");
        return recovered;
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
            if (ctx.Match("{")) depth++;
            else if (ctx.Match("}")) depth--;
            else ctx.Advance();
        }
    }

    private DoExpr ParseDoExpr()
    {
        var startToken = ctx.Current;
        ctx.Expect("do");
        ctx.Expect("{", DiagnosticMessages.ParserExpectedLeftBraceAfterDo);

        var doExpr = new DoExpr();
        while (!ctx.Check("}") && !ctx.IsEof)
        {
            var pos = ctx.Position;
            doExpr.Bindings.Add(ParseDoBinding());
            ctx.Match(";");

            if (ctx.Position == pos)
            {
                ctx.Advance();
            }
        }

        ctx.Expect("}", DiagnosticMessages.ParserExpectedRightBraceAfterDoExpression);
        doExpr.Span = ctx.SpanFrom(startToken);
        return doExpr;
    }

    private DoBinding ParseDoBinding()
    {
        if (ctx.IsNameFirstSyntax &&
            TokenKind.IsIdentifier(ctx.Current) &&
            string.Equals(ctx.GetText(ctx.Peek(1)), ":=", StringComparison.Ordinal))
        {
            var name = ctx.GetText();
            ctx.Advance();
            ctx.Expect(":=");
            return DoBinding.CreateLet(name, ParseExpr());
        }

        if (ctx.Match("let"))
        {
            var name = ctx.GetText();
            ctx.Advance();
            ctx.Expect("=", DiagnosticMessages.ParserExpectedEqualInDoLetBinding);
            return DoBinding.CreateLet(name, ParseExpr());
        }

        if (HasTopLevelLeftArrowBeforeDoItemEnd())
        {
            var pattern = patternParser.ParsePattern();
            ctx.Expect("<-", DiagnosticMessages.ParserExpectedLeftArrowInDoBinding);
            return DoBinding.CreateBind(pattern, ParseExpr());
        }

        return DoBinding.CreateExpr(ParseExpr());
    }

    private bool HasTopLevelLeftArrowBeforeDoItemEnd()
    {
        var depth = 0;
        for (var offset = 0; ctx.Peek(offset) is not EofToken; offset++)
        {
            var text = ctx.GetText(ctx.Peek(offset));
            switch (text)
            {
                case "(" or "[" or "{":
                    depth++;
                    break;
                case ")" or "]":
                    depth--;
                    break;
                case "}":
                    if (depth == 0)
                    {
                        return false;
                    }
                    depth--;
                    break;
                case ";" when depth == 0:
                    return false;
                case "<-" when depth == 0:
                    return true;
            }
        }

        return false;
    }

    private string ParseQualifiedName()
    {
        var parts = QualifiedPathParser.Parse(
            ctx,
            TokenKind.IsAnyIdentifier,
            DiagnosticMessages.ParserExpectedIdentifierAfterQualifiedSeparator);
        if (parts.Count <= 1)
        {
            return parts.Count == 0 ? string.Empty : parts[0];
        }

        if (ctx.IsNameFirstSyntax)
        {
            return string.Join(WellKnownStrings.Separators.Path, parts);
        }

        return string.Join(WellKnownStrings.Separators.ModulePath, parts.Take(parts.Count - 1)) +
               WellKnownStrings.Separators.Path +
               parts[^1];
    }

    private EidosAstNode ParseIfExpr()
    {
        var startToken = ctx.Current;
        ctx.Expect("if");

        var ifExpr = new IfExpr();
        ifExpr.SetSpan(ctx.SpanFrom(startToken));

        // Condition expression
        var condition = ParseExpr();
        ifExpr.SetCondition(condition);

        // then block
        ctx.Expect("then");
        var thenBranch = ParseBlockOrExpr();
        ifExpr.SetThenBranch(thenBranch);

        // Optional else
        if (ctx.Match("else"))
        {
            var elseBranch = ParseBlockOrExpr();
            ifExpr.SetElseBranch(elseBranch);
        }

        ifExpr.SetSpan(ctx.SpanFrom(startToken));
        return ifExpr;
    }

    private EidosAstNode ParseMatchExpr()
    {
        var startToken = ctx.Current;
        ctx.Expect("match");

        var matchExpr = new MatchExpr();
        matchExpr.SetSpan(ctx.SpanFrom(startToken));

        var matched = ParseMatchScrutinee();
        matchExpr.SetMatchedExpression(matched);

        ctx.Expect("{");

        while (!ctx.Check("}") && !ctx.IsEof)
        {
            var pos = ctx.Position;
            var branch = ParsePatternBranch();
            matchExpr.AddBranch(branch);
            ctx.Match(","); // optional trailing comma
            if (ctx.Position == pos) { ctx.Advance(); }
        }

        ctx.Expect("}");
        matchExpr.SetSpan(ctx.SpanFrom(startToken));
        return matchExpr;
    }

    private EidosAstNode ParseMatchScrutinee()
    {
        if (!TokenKind.IsTypeIdentifier(ctx.Current) &&
            !(TokenKind.IsAnyIdentifier(ctx.Current) && QualifiedPathParser.IsQualifiedPathLookahead(ctx)))
        {
            return ParseExpr();
        }

        var saved = ctx.SavePosition();
        var startToken = ctx.Current;
        var path = ParsePathExpr(startToken);
        if (ctx.Check("{"))
        {
            return path;
        }

        ctx.RestorePosition(saved);
        return ParseExpr();
    }

    internal PatternBranch ParsePatternBranch()
    {
        var startToken = ctx.Current;
        var branch = new PatternBranch();
        branch.SetSpan(ctx.SpanFrom(startToken));

        // Curried pattern: p1 p2 ... => body
        var patterns = new List<Pattern>();
        patterns.Add(patternParser.ParsePattern());

        var guards = new List<EidosAstNode>();
        while (!ctx.Check("=>") && !ctx.IsEof)
        {
            if (ctx.Check("when"))
            {
                ctx.Advance();
                guards.Add(ParseGuardExpression());
                continue;
            }
            var pos = ctx.Position;
            patterns.Add(patternParser.ParsePattern());
            if (ctx.Position == pos) { ctx.Advance(); break; }
        }

        ctx.Expect("=>");
        var body = ParseExpr();
        branch.SetBody(body);

        branch.SetPatterns(patterns);
        var guard = CombineGuards(guards);
        if (guard != null) branch.SetGuard(guard);
        branch.SetSpan(ctx.SpanFrom(startToken));
        return branch;
    }

    private EidosAstNode ParseGuardExpression()
    {
        if (!GuardExpressionLookahead.HasTopLevelPatternGuardSource(ctx))
        {
            return ParseExprNoLambda();
        }

        var startToken = ctx.Current;
        var pattern = patternParser.ParsePattern();
        if (ctx.Match("<-"))
        {
            var guard = new PatternGuardExpr();
            guard.SetSpanValue(ctx.SpanFrom(startToken));
            guard.SetPattern(pattern);
            if (!IsPatternGuardSourceBoundary())
            {
                var source = ParseExprNoLambda();
                guard.SetSourceExpression(source);
            }
            return guard;
        }

        return ParseExprNoLambda();
    }

    private bool IsPatternGuardSourceBoundary()
    {
        if (ctx.IsEof)
        {
            return true;
        }

        return ctx.GetText() is "=>" or "when" or "," or "}" or ")" or "]";
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

    private EidosAstNode ParseCall(EidosAstNode function)
    {
        var startToken = ctx.Current;
        ctx.Expect("(");

        var call = new CallExpr();
        call.SetSpan(ctx.SpanFrom(startToken));
        call.SetFunction(function);

        if (!ctx.Check(")"))
        {
            // Named arg: name = expr
            if (IsNamedArgLookahead())
            {
                ParseNamedArg(call);
            }
            else
            {
                call.AddPositionalArg(ParseExpr());
            }

            while (ctx.Match(","))
            {
                if (IsNamedArgLookahead())
                    ParseNamedArg(call);
                else
                    call.AddPositionalArg(ParseExpr());
            }
        }

        ctx.Expect(")");
        call.SetSpan(ctx.SpanFrom(startToken));
        return call;
    }

    private void ParseNamedArg(CallExpr call)
    {
        var name = ctx.GetText();
        ctx.Advance();
        ctx.Expect("=");
        var value = ParseExpr();
        call.AddNamedArg(new NamedArg { Name = name, Value = value });
    }

    private bool IsNamedArgLookahead()
    {
        return TokenKind.IsIdentifier(ctx.Current) && ctx.CheckPeek(1, "=");
    }

    private EidosAstNode ParseFieldAccess(EidosAstNode left)
    {
        var startToken = ctx.Current;
        ctx.Expect(".");
        var fieldName = ctx.GetText();
        ctx.Advance();

        var methodCall = new MethodCallExpr();
        methodCall.SetSpan(ctx.SpanFrom(startToken));
        methodCall.SetReceiver(left);
        methodCall.SetMethodName(fieldName);

        if (ctx.Match("("))
        {
            methodCall.MarkExplicitCallSyntax();
            ParseMethodCallArgs(methodCall);
            ctx.Expect(")");
            methodCall.SetSpan(ctx.SpanFrom(startToken));
        }

        return methodCall;
    }

    private void ParseMethodCallArgs(MethodCallExpr methodCall)
    {
        if (ctx.Check(")"))
        {
            return;
        }

        if (IsNamedArgLookahead())
        {
            ParseNamedArg(methodCall);
        }
        else
        {
            methodCall.AddPositionalArg(ParseExpr());
        }

        while (ctx.Match(","))
        {
            if (ctx.Check(")"))
            {
                break;
            }

            if (IsNamedArgLookahead())
                ParseNamedArg(methodCall);
            else
                methodCall.AddPositionalArg(ParseExpr());
        }
    }

    private void ParseNamedArg(MethodCallExpr methodCall)
    {
        var name = ctx.GetText();
        ctx.Advance();
        ctx.Expect("=");
        var value = ParseExpr();
        methodCall.AddNamedArg(new NamedArg { Name = name, Value = value });
    }

    private EidosAstNode ParseTypeArgAccess(EidosAstNode left)
    {
        var startToken = ctx.Current;
        var genericArguments = typeParser.TryParseGenericArguments();
        if (genericArguments == null)
            return left;

        if (left is PathExpr pathExpr)
        {
            pathExpr.SetGenericArguments(genericArguments);
            return pathExpr;
        }

        var typeApplication = new IndexExpr();
        typeApplication.SetSpan(ctx.SpanFrom(startToken));
        typeApplication.SetObject(left);
        typeApplication.SetGenericArguments(genericArguments);
        return typeApplication;
    }

    private EidosAstNode ParseInfixBacktick(EidosAstNode left)
    {
        var startToken = ctx.Current;
        ctx.Expect("`");
        var funcName = ctx.GetText();
        ctx.Advance();
        ctx.Expect("`");

        var right = ParseExpr(Precedence.Additive);

        var infixCall = new InfixCallExpr();
        infixCall.SetSpan(ctx.SpanFrom(startToken));
        infixCall.SetLeft(left);
        infixCall.SetFunctionName(funcName);
        infixCall.SetRight(right);
        return infixCall;
    }

    private EidosAstNode ParseBlockOrExpr()
    {
        if (ctx.Check("{"))
        {
            return ParseBlock();
        }
        return ParseExpr();
    }

    private BlockExpr ParseBlock()
    {
        var startToken = ctx.Current;
        ctx.Expect("{");

        var block = new BlockExpr();
        block.SetSpan(ctx.SpanFrom(startToken));
        EidosAstNode? lastItem = null;
        var lastItemHadSemicolon = false;
        PushMutableBindingScope();

        try
        {
            while (!ctx.Check("}") && !ctx.IsEof)
            {
                var pos = ctx.Position;
                EidosAstNode stmt;

                var text = ctx.GetText();
                if (text == "let")
                    stmt = ParseBlockStatement(text);
                else if (ctx.IsNameFirstSyntax && IsPlaceAssignmentStart())
                    stmt = ParseNameFirstAssignment();
                else if (ctx.IsNameFirstSyntax && IsKnownMutableLocalAssignmentStart())
                    stmt = ParseNameFirstAssignment();
                else if (ctx.IsNameFirstSyntax && IsNameFirstLocalBindingStart())
                    stmt = ParseNameFirstLocalBinding();
                else if (ctx.IsNameFirstSyntax && IsNameFirstAssignmentStart())
                    stmt = ParseNameFirstAssignment();
                else
                    stmt = ParseExpr();

                RegisterMutableBinding(stmt);

                var hasSemicolon = ctx.Match(";");
                block.AddStatement(stmt);
                lastItem = stmt;
                lastItemHadSemicolon = hasSemicolon;
                if (ctx.Position == pos) { ctx.Advance(); }
            }
        }
        finally
        {
            _mutableBlockBindings.Pop();
        }

        if (lastItem != null && !lastItemHadSemicolon && IsBlockResultExpression(lastItem))
        {
            block.SetResultExpression(lastItem);
        }
        ctx.Expect("}");
        block.SetSpan(ctx.SpanFrom(startToken));
        return block;
    }

    private void PushMutableBindingScope()
    {
        _mutableBlockBindings.Push(_mutableBlockBindings.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(_mutableBlockBindings.Peek(), StringComparer.Ordinal));
    }

    private void RegisterMutableBinding(EidosAstNode stmt)
    {
        if (_mutableBlockBindings.Count == 0 ||
            stmt is not LetDecl { IsMutable: true, Pattern: VarPattern pattern } ||
            string.IsNullOrWhiteSpace(pattern.Name) ||
            pattern.Name == "_")
        {
            return;
        }

        _mutableBlockBindings.Peek().Add(pattern.Name);
    }

    private static bool IsBlockResultExpression(EidosAstNode node)
    {
        return node is Expression;
    }

    private EidosAstNode ParseBlockStatement(string keyword)
    {
        var startToken = ctx.Current;
        ctx.Advance(); // consume let

        if (keyword == "let")
        {
            if (ctx.Match("?"))
            {
                var questionPattern = patternParser.ParsePattern();
                ctx.Expect("=");
                var questionInitValue = ParseExpr();
                var questionDecl = new LetQuestionDecl();
                questionDecl.SetSpan(ctx.SpanFrom(startToken));
                questionDecl.SetPattern(questionPattern);
                questionDecl.SetValue(questionInitValue);
                questionDecl.SetAttributes([]);
                return questionDecl;
            }

            var isMutable = ctx.Match(WellKnownStrings.Keywords.Mut);
            var pattern = patternParser.ParsePattern();
            TypeNode? typeAnnotation = null;
            if (ctx.Match(":"))
            {
                typeAnnotation = typeParser.ParseType();
            }

            var initValue = ParseInitializerOrRecovery(startToken);
            var decl = new LetDecl();
            decl.SetSpan(ctx.SpanFrom(startToken));
            decl.SetMutable(isMutable);
            decl.SetPattern(pattern);
            decl.SetTypeAnnotation(typeAnnotation);
            decl.SetValue(initValue);
            decl.SetAttributes([]);
            return decl;
        }

        ctx.Error(DiagnosticMessages.ParserUnexpectedToken(keyword));
        return CreateRecoveredLiteral(startToken);
    }

    private LetDecl ParseNameFirstLocalBinding()
    {
        var startToken = ctx.Current;
        var isComptime = ctx.Match(WellKnownStrings.Keywords.Comptime);
        var isMutable = ctx.Match(WellKnownStrings.Keywords.Mut);
        var pattern = patternParser.ParsePattern();
        TypeNode? typeAnnotation = null;
        if (ctx.Match(":"))
        {
            typeAnnotation = typeParser.ParseType();
        }

        ctx.Expect(":=");
        var initValue = ParseExpr();
        var decl = new LetDecl();
        decl.SetSpan(ctx.SpanFrom(startToken));
        decl.SetComptime(isComptime);
        decl.SetMutable(isMutable);
        decl.SetPattern(pattern);
        decl.SetTypeAnnotation(typeAnnotation);
        decl.SetValue(initValue);
        decl.SetAttributes([]);
        return decl;
    }

    private Assignment ParseNameFirstAssignment()
    {
        var startToken = ctx.Current;
        var target = ParsePrefix();
        if (!ctx.Match(":="))
        {
            ctx.Expect("=");
        }
        var value = ParseExpr();
        var assignment = new Assignment();
        assignment.SetSpan(ctx.SpanFrom(startToken));
        assignment.SetTargetExpression(target);
        assignment.SetValue(value);
        return assignment;
    }

    private bool IsNameFirstLocalBindingStart()
    {
        if (ctx.Check(WellKnownStrings.Keywords.Comptime))
        {
            return true;
        }

        if (ctx.Check(WellKnownStrings.Keywords.Mut))
        {
            return true;
        }

        if ((ctx.Check("ref") ||
             ctx.Check("mref") ||
             ctx.Check("(") ||
             ctx.Check("[") ||
             ctx.Check("_")) &&
            HasTopLevelTokenBeforeTerminator(":="))
        {
            return true;
        }

        return TokenKind.IsIdentifier(ctx.Current) && HasTopLevelTokenBeforeTerminator(":=");
    }

    private bool IsNameFirstAssignmentStart()
    {
        if (!(TokenKind.IsIdentifier(ctx.Current) || ctx.Check("*")))
        {
            return false;
        }

        return HasTopLevelTokenBeforeTerminator(":=") ||
               HasTopLevelTokenBeforeTerminator("=");
    }

    private bool IsPlaceAssignmentStart()
    {
        if (!(TokenKind.IsIdentifier(ctx.Current) || ctx.Check("*")))
        {
            return false;
        }

        if (!HasTopLevelTokenBeforeTerminator(":="))
        {
            return false;
        }

        if (ctx.Check("*"))
        {
            return true;
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
            if (depth == 0)
            {
                if (text == ":=")
                {
                    return false;
                }

                if (text == ":")
                {
                    return false;
                }

                if (text is "." or "[")
                {
                    return true;
                }

                if (text is ";" or "}" or "=>" or ",")
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

    private bool IsKnownMutableLocalAssignmentStart()
    {
        return _mutableBlockBindings.Count > 0 &&
               TokenKind.IsIdentifier(ctx.Current) &&
               _mutableBlockBindings.Peek().Contains(ctx.GetText()) &&
               HasTopLevelTokenBeforeTerminator(":=");
    }

    private bool HasTopLevelTokenBeforeTerminator(string expected)
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
                if (text == expected)
                {
                    if (expected == "=" &&
                        (ctx.CheckPeek(offset + 1, "=") ||
                         (offset > 0 && ctx.CheckPeek(offset - 1, "="))))
                    {
                        continue;
                    }

                    return true;
                }

                if (text is ";" or "}" or "=>" or ",")
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

    private EidosAstNode ParseInitializerOrRecovery(Token startToken)
    {
        if (ctx.Match("="))
        {
            return ParseExpr();
        }

        ctx.Error(DiagnosticMessages.ParserExpectedToken("="));
        return CreateRecoveredLiteral(startToken);
    }

    private static LiteralExpr CreateRecoveredLiteral(Token token)
    {
        var literal = new LiteralExpr();
        literal.SetSpan(new SourceSpan(token.Location, 0));
        literal.SetLiteral("0");
        literal.MarkRecoveredError(AstRecoveryReasons.ParserMissingInitializer);
        return literal;
    }

    private bool IsTypeArgLookahead()
    {
        // Expression indexes such as values[idx] are runtime indexes, not type applications.
        // Type applications start with a type identifier or a parenthesized type such as
        // id[Int], id[Result[T, E]], or id[(Int, Int)].
        if (!ctx.Check("["))
            return false;
        var next = ctx.Peek(1);
        return IsTypeArgStartToken(next) ||
               (ctx.CheckPeek(1, "(") && IsParenthesizedTypeArgLookahead(1)) ||
               HasTopLevelGenericArgumentSeparator();
    }

    private bool HasTopLevelGenericArgumentSeparator()
    {
        var bracketDepth = 0;
        var nestedDepth = 0;
        for (var offset = 0; offset < 64; offset++)
        {
            var token = ctx.Peek(offset);
            if (token is EofToken)
            {
                return false;
            }

            var text = ctx.GetText(token);
            switch (text)
            {
                case "[":
                    bracketDepth++;
                    break;
                case "]":
                    bracketDepth--;
                    if (bracketDepth <= 0)
                    {
                        return false;
                    }
                    break;
                case "(" or "{":
                    nestedDepth++;
                    break;
                case ")" or "}" when nestedDepth > 0:
                    nestedDepth--;
                    break;
                case "," when bracketDepth == 1 && nestedDepth == 0:
                    return true;
            }
        }

        return false;
    }

    private bool IsParenthesizedTypeArgLookahead(int openParenOffset)
    {
        var next = ctx.Peek(openParenOffset + 1);
        return ctx.CheckPeek(openParenOffset + 1, ")") ||
               IsTypeArgStartToken(next) ||
               (ctx.CheckPeek(openParenOffset + 1, "(") &&
                IsParenthesizedTypeArgLookahead(openParenOffset + 1));
    }

    private static bool IsTypeArgStartToken(Token token)
    {
        return TokenKind.IsTypeIdentifier(token);
    }

    private static BinaryOp MapBinaryOp(string op) => op switch
    {
        "|>"  => BinaryOp.Pipe,
        ">>=" => BinaryOp.Bind,
        "??"  => BinaryOp.Coalesce,
        "+"   => BinaryOp.Add,
        "-"   => BinaryOp.Subtract,
        "++"  => BinaryOp.Concat,
        "<>"  => BinaryOp.Append,
        "*"   => BinaryOp.Multiply,
        "/"   => BinaryOp.Divide,
        "%"   => BinaryOp.Modulo,
        "<$>" => BinaryOp.Fmap,
        "<*>" => BinaryOp.Ap,
        "+:"  => BinaryOp.Prepend,
        ":+"  => BinaryOp.AppendLast,
        ">>>" => BinaryOp.ComposeRight,
        "<<<" => BinaryOp.ComposeLeft,
        "=="  => BinaryOp.Equal,
        "!="  => BinaryOp.NotEqual,
        "<"   => BinaryOp.Less,
        ">"   => BinaryOp.Greater,
        "<="  => BinaryOp.LessEqual,
        ">="  => BinaryOp.GreaterEqual,
        "&&"  => BinaryOp.And,
        "||"  => BinaryOp.Or,
        _ => BinaryOp.Add
    };

    private static UnaryOp MapUnaryOp(string op) => op switch
    {
        "-"    => UnaryOp.Negate,
        "!"    => UnaryOp.Not,
        "*"    => UnaryOp.Deref,
        "&"    => UnaryOp.AddressOf,
        "ref"  => UnaryOp.Ref,
        "mref" => UnaryOp.MRef,
        _ => UnaryOp.Negate
    };

    private static bool IsPatternLike(EidosAstNode expr)
    {
        return expr is CtorExpr or TupleExpr or IdentifierExpr or LiteralExpr or ListExpr;
    }

    private static Pattern? ExprToPattern(EidosAstNode expr)
    {
        if (expr is CtorExpr ctor)
        {
            var pat = new CtorPattern();
            pat.SetConstructorName(ctor.ConstructorName);
            foreach (var arg in ctor.PositionalArgs)
            {
                var inner = ExprToPattern(arg);
                if (inner != null) pat.AddPositionalPattern(inner);
            }
            return pat;
        }

        if (expr is TupleExpr tuple)
        {
            var pat = new TuplePattern();
            foreach (var elem in tuple.Elements)
            {
                var inner = ExprToPattern(elem);
                if (inner != null) pat.Elements.Add(inner);
            }
            return pat;
        }

        if (expr is ListExpr list)
        {
            var pat = new ListPattern();
            if (list.HasRest)
            {
                for (int i = 0; i < list.Elements.Count - 1; i++)
                {
                    var inner = ExprToPattern(list.Elements[i]);
                    if (inner != null) pat.Elements.Add(inner);
                }
                if (list.Elements.Count > 0)
                {
                    var restInner = ExprToPattern(list.Elements[^1]);
                    pat.HasRestMarker = true;
                    pat.RestPattern = restInner;
                }
            }
            else
            {
                foreach (var elem in list.Elements)
                {
                    var inner = ExprToPattern(elem);
                    if (inner != null) pat.Elements.Add(inner);
                }
            }
            return pat;
        }

        if (expr is IdentifierExpr ident)
        {
            var pat = new VarPattern();
            pat.SetName(ident.Name);
            return pat;
        }

        if (expr is LiteralExpr lit && lit.RawText == "()")
        {
            return new TuplePattern();
        }

        if (expr is LiteralExpr literal)
        {
            var pat = new LiteralPattern();
            pat.SetSpan(literal.Span);
            pat.SetLiteral(string.IsNullOrWhiteSpace(literal.RawText)
                ? literal.Value?.ToString() ?? string.Empty
                : literal.RawText);
            return pat;
        }

        return null;
    }
}
