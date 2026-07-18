using Eidosc.Ast.Patterns;
using Eidosc.Diagnostic;
using Eidosc.Utils;

namespace Eidosc.Parsing.Handwritten;

public sealed class PatternParser(ParserContext ctx)
{
    public Pattern ParsePattern()
    {
        return ParseOrPattern();
    }

    private Pattern ParseOrPattern()
    {
        var startToken = ctx.Current;
        var left = ParseAndPattern();

        if (!ctx.Check("|"))
            return left;

        var alternatives = new List<Pattern> { left };
        while (ctx.Match("|"))
        {
            if (IsPatternBoundary())
            {
                break;
            }

            alternatives.Add(ParseAndPattern());
        }

        return new OrPattern
        {
            Alternatives = alternatives,
            Span = ctx.SpanFrom(startToken)
        };
    }

    private Pattern ParseAndPattern()
    {
        var startToken = ctx.Current;
        var left = ParseAsPattern();

        if (!ctx.Check("&"))
            return left;

        var conjuncts = new List<Pattern> { left };
        while (ctx.Match("&"))
        {
            if (IsPatternBoundary())
            {
                break;
            }

            conjuncts.Add(ParseAsPattern());
        }

        return new AndPattern
        {
            Conjuncts = conjuncts,
            Span = ctx.SpanFrom(startToken)
        };
    }

    private Pattern ParseAsPattern()
    {
        var startToken = ctx.Current;
        var inner = ParseNotPattern();

        if (!ctx.Check("as"))
            return inner;

        ctx.Advance(); // consume "as"

        // Optional binding mode: as mut name / as ref name / as mref name
        var bindingMode = PatternBindingMode.ByValue;
        var isMutableBinding = false;
        if (ctx.Check("mut"))
        {
            isMutableBinding = true;
            ctx.Advance();
        }

        if (ctx.Check("ref"))
        {
            bindingMode = PatternBindingMode.SharedBorrow;
            isMutableBinding = false;
            ctx.Advance();
        }
        else if (ctx.Check("mref"))
        {
            bindingMode = PatternBindingMode.MutableBorrow;
            isMutableBinding = false;
            ctx.Advance();
        }

        var bindingName = "";
        if (!IsPatternBoundary())
        {
            if (!InternalNameParser.TryParseLeadingDoubleUnderscoreName(ctx, out bindingName))
            {
                bindingName = ctx.GetText();
                ctx.Advance();
            }
        }

        return new AsPattern
        {
            InnerPattern = inner,
            BindingName = bindingName,
            BindingMode = bindingMode,
            IsMutableBinding = isMutableBinding && bindingMode == PatternBindingMode.ByValue,
            Span = ctx.SpanFrom(startToken)
        };
    }

    private Pattern ParseNotPattern()
    {
        var startToken = ctx.Current;
        if (!ctx.Check("!"))
            return ParseRangePattern();

        ctx.Advance(); // consume "!"
        var inner = IsPatternBoundary() ? null : ParseRangePattern();
        return new NotPattern
        {
            InnerPattern = inner,
            Span = ctx.SpanFrom(startToken)
        };
    }

    private Pattern ParseRangePattern()
    {
        var startToken = ctx.Current;
        var left = ParsePatternTerm();

        if (!ctx.Check(".."))
            return left;

        if (left is not LiteralPattern startLiteral)
        {
            ctx.Error(DiagnosticMessages.ParserRangePatternStartMustBeLiteral);
            return left;
        }

        ctx.Advance(); // consume ".."
        var endLiteral = IsPatternBoundary()
            ? null
            : ParsePatternTerm() as LiteralPattern;

        return new RangePattern
        {
            Start = startLiteral,
            End = endLiteral,
            Span = ctx.SpanFrom(startToken)
        };
    }

    private bool IsPatternBoundary()
    {
        if (ctx.IsEof)
        {
            return true;
        }

        return ctx.GetText() is "=>" or "," or ")" or "]" or "}" or "|" or "&" or "when";
    }

    private Pattern ParsePatternTerm()
    {
        var startToken = ctx.Current;

        // View pattern: (expr -> pattern) — parsed as parenthesized, detected inside
        if (ctx.Check("("))
        {
            var saved = ctx.SavePosition();
            ctx.Advance(); // consume "("

            // Empty parens => unit pattern (tuple with 0 elements)
            if (ctx.Match(")"))
            {
                var tuple = new TuplePattern();
                tuple.SetSpan(ctx.SpanFrom(startToken));
                return tuple;
            }

            if (HasTopLevelViewArrow())
            {
                var viewExpression = new ExprParser(ctx, this, new TypeParser(ctx)).ParseExpr();
                ctx.Advance(); // consume "->"
                var viewInnerPattern = ParsePattern();
                ctx.Expect(")", DiagnosticMessages.ParserExpectedRightParenToCloseViewPattern);
                return new ViewPattern
                {
                    ViewExpression = viewExpression,
                    InnerPattern = viewInnerPattern,
                    Span = ctx.SpanFrom(startToken)
                };
            }

            var inner = ParsePattern();

            // Check for tuple: (p1, p2, ...)
            if (ctx.Check(","))
            {
                var tuple = new TuplePattern();
                tuple.AddElement(inner);
                while (ctx.Match(","))
                {
                    if (ctx.Check(".."))
                    {
                        AddListRestDiagnostic(DiagnosticMessages.ParserListRestStandaloneNote);
                        ctx.Advance();
                        if (!ctx.Check(")"))
                        {
                            _ = ParsePattern();
                        }
                        break;
                    }

                    tuple.AddElement(ParsePattern());
                }
                ctx.Expect(")");
                tuple.SetSpan(ctx.SpanFrom(startToken));
                return tuple;
            }

            ctx.Expect(")");
            return inner;
        }

        // List pattern: [p1, p2, ..rest]
        if (ctx.Check("["))
        {
            return ParseListPattern();
        }

        return ParsePatternAtom();
    }

    private bool HasTopLevelViewArrow()
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
                case ")" when depth == 0:
                    return false;
                case ")" or "]" or "}":
                    depth--;
                    break;
                case "->" when depth == 0:
                    return true;
            }
        }

        return false;
    }

    private Pattern ParseListPattern()
    {
        var startToken = ctx.Current;
        ctx.Expect("[");

        var elements = new List<Pattern>();
        var suffixElements = new List<Pattern>();
        var hasRestMarker = false;
        Pattern? restPattern = null;
        var afterRest = false;

        if (!ctx.Check("]"))
        {
            while (!ctx.Check("]") && !ctx.IsEof)
            {
                if (ctx.Check(".."))
                {
                    if (hasRestMarker)
                    {
                        AddListRestDiagnostic("list rest marker '..' can only appear once");
                        ctx.Advance();
                        continue;
                    }

                    hasRestMarker = true;
                    ctx.Advance(); // consume ".."
                    if (!ctx.Check("]") && !ctx.Check(","))
                    {
                        if (IsSimpleListRestBindingLookahead())
                        {
                            restPattern = ParsePattern();
                        }
                        else
                        {
                            AddListRestDiagnostic("list rest pattern only supports a bare binding such as '..rest' or '.._'");
                        }
                    }

                    afterRest = true;
                }
                else
                {
                    var element = ParsePattern();
                    if (afterRest)
                    {
                        suffixElements.Add(element);
                    }
                    else
                    {
                        elements.Add(element);
                    }
                }

                if (ctx.Check("]"))
                {
                    break;
                }

                if (!ctx.Match(","))
                {
                    AddListRestDiagnostic(DiagnosticMessages.ParserListRestMustBeLastNote);
                    while (!ctx.Check("]") && !ctx.IsEof)
                    {
                        ctx.Advance();
                    }
                    break;
                }

                if (ctx.Check("]"))
                {
                    break;
                }

                afterRest = hasRestMarker;
            }
        }

        ctx.Expect("]");
        return new ListPattern
        {
            Elements = elements,
            HasRestMarker = hasRestMarker,
            RestPattern = restPattern,
            SuffixElements = suffixElements,
            Span = ctx.SpanFrom(startToken)
        };
    }

    private bool IsSimpleListRestBindingLookahead()
    {
        if (ctx.Check("_"))
        {
            return true;
        }

        return TokenKind.IsIdentifier(ctx.Current) &&
               (ctx.CheckPeek(1, ",") || ctx.CheckPeek(1, "]"));
    }

    private Pattern ParsePatternAtom()
    {
        var startToken = ctx.Current;

        if (ctx.Match("expand"))
        {
            var invocation = MetaInvocationSyntaxParser.Parse(
                ctx,
                () => ParsePattern(),
                "pattern expand");
            var expansion = new ExpandPattern();
            expansion.SetInvocation(invocation);
            expansion.SetSpan(new SourceSpan(
                startToken.Location,
                Math.Max(0, invocation.Span.EndPosition - startToken.Location.Position)));
            return expansion;
        }

        if (InternalNameParser.TryParseLeadingDoubleUnderscoreName(ctx, out var internalName))
        {
            var varPat = new VarPattern();
            varPat.SetSpan(ctx.SpanFrom(startToken));
            varPat.SetName(internalName);
            varPat.SetBindingMode(PatternBindingMode.ByValue);
            return varPat;
        }

        // Wildcard: _
        if (ctx.Match("_"))
        {
            return new WildcardPattern { Span = ctx.SpanFrom(startToken) };
        }

        // Boolean literals in patterns
        if (ctx.Check("true") || ctx.Check("false"))
        {
            var text = ctx.GetText();
            ctx.Advance();
            var lit = new LiteralPattern();
            lit.SetSpan(ctx.SpanFrom(startToken));
            lit.SetLiteral(text);
            return lit;
        }

        // Numeric/string/char literals
        if (TokenKind.IsAnyLiteral(ctx.Current) && !TokenKind.IsBoolean(ctx.Current))
        {
            var text = ctx.GetLiteralRawText();
            ctx.Advance();
            var lit = new LiteralPattern();
            lit.SetSpan(ctx.SpanFrom(startToken));
            lit.SetLiteral(text);
            return lit;
        }

        // Binding mode prefix: mut x, ref x, mref x
        if (ctx.Check("mut") || ctx.Check("ref") || ctx.Check("mref"))
        {
            var isMutableBinding = ctx.Check("mut");
            var mode = ctx.Check("mref")
                ? PatternBindingMode.MutableBorrow
                : PatternBindingMode.SharedBorrow;
            if (isMutableBinding)
            {
                mode = PatternBindingMode.ByValue;
            }
            ctx.Advance();

            if (ctx.Check("_") && isMutableBinding)
            {
                ctx.Error("mutable wildcard binding is not allowed");
                ctx.Advance();
                return new WildcardPattern { Span = ctx.SpanFrom(startToken) };
            }

            string name;
            if (!InternalNameParser.TryParseLeadingDoubleUnderscoreName(ctx, out name))
            {
                name = ctx.GetText();
                ctx.Advance();
            }

            var varPat = new VarPattern();
            varPat.SetSpan(ctx.SpanFrom(startToken));
            varPat.SetName(name);
            varPat.SetBindingMode(mode);
            varPat.SetMutableBinding(isMutableBinding);
            return varPat;
        }

        // Constructor syntax is unambiguous when qualified or followed by a
        // positional/record body. Bare names remain unresolved until the
        // expected pattern namespace is available to semantic analysis.
        if (TokenKind.IsAnyIdentifier(ctx.Current) &&
            (ctx.CheckPeek(1, ".") ||
             QualifiedPathParser.IsQualifiedPathLookahead(ctx) ||
             ctx.CheckPeek(1, "(") ||
             ctx.CheckPeek(1, "{")))
        {
            return ParseCtorPattern();
        }

        if (TokenKind.IsAnyIdentifier(ctx.Current))
        {
            var name = ctx.GetText();
            ctx.Advance();
            var varPat = new VarPattern();
            varPat.SetSpan(ctx.SpanFrom(startToken));
            varPat.SetName(name);
            varPat.SetBindingMode(PatternBindingMode.ByValue);
            varPat.SetMayResolveToConstructor(true);
            return varPat;
        }

        ctx.Error(DiagnosticMessages.ParserExpectedPattern(ctx.GetText()));
        ctx.Advance();
        return new WildcardPattern { Span = new SourceSpan(startToken.Location, 0) };
    }

    private Pattern ConsumeInvalidCallLikePattern(Token startToken)
    {
        ctx.Advance(); // consume the invalid lowercase callee

        if (ctx.Match("("))
        {
            var depth = 1;
            while (depth > 0 && !ctx.IsEof)
            {
                if (ctx.Match("("))
                {
                    depth++;
                    continue;
                }

                if (ctx.Match(")"))
                {
                    depth--;
                    continue;
                }

                ctx.Advance();
            }
        }

        return new WildcardPattern { Span = ctx.SpanFrom(startToken) };
    }

    private Pattern ParseCtorPattern()
    {
        var startToken = ctx.Current;
        var parsedPath = QualifiedPathParser.ParseItemPath(
            ctx,
            TokenKind.IsAnyIdentifier,
            DiagnosticMessages.ParserExpectedIdentifierAfterQualifiedSeparator);

        var ctorName = parsedPath.Name;
        var packageAlias = parsedPath.PackageAlias;
        var modulePath = parsedPath.ModulePath;

        // No parentheses => bare constructor name treated as a ctor with no args
        if (!ctx.Check("(") && !ctx.Check("{"))
        {
            var span = ctx.SpanFrom(startToken);
            var ctor = new CtorPattern();
            ctor.SetSpan(span);
            ctor.SetConstructorName(ctorName);
            ctor.SetPackageAlias(packageAlias);
            if (modulePath.Count > 0)
                ctor.SetModulePath(modulePath);
            return ctor;
        }

        // Positional arguments: Ctor(p1, p2, ...)
        if (ctx.Match("("))
        {
            var ctor = new CtorPattern();
            ctor.SetConstructorName(ctorName);
            ctor.SetPackageAlias(packageAlias);
            if (modulePath.Count > 0)
                ctor.SetModulePath(modulePath);

            if (!ctx.Check(")"))
            {
                if (ctx.Check(".."))
                {
                    AddListRestDiagnostic(DiagnosticMessages.ParserListRestStandaloneNote);
                    ctx.Advance();
                    if (!ctx.Check(")"))
                    {
                        _ = ParsePattern();
                    }
                }
                else
                {
                ctor.AddPositionalPattern(ParsePattern());
                while (ctx.Match(","))
                {
                    if (ctx.Check(".."))
                    {
                        AddListRestDiagnostic(DiagnosticMessages.ParserListRestStandaloneNote);
                        ctx.Advance();
                        if (!ctx.Check(")"))
                        {
                            _ = ParsePattern();
                        }
                        break;
                    }

                    ctor.AddPositionalPattern(ParsePattern());
                }
                }
            }
            ctx.Expect(")");

            ctor.SetSpan(ctx.SpanFrom(startToken));
            return ctor;
        }

        // Named fields: Ctor{field1: p1, field2: p2}
        if (ctx.Match("{"))
        {
            var ctor = new CtorPattern();
            ctor.SetConstructorName(ctorName);
            ctor.SetPackageAlias(packageAlias);
            if (modulePath.Count > 0)
                ctor.SetModulePath(modulePath);

            if (!ctx.Check("}"))
            {
                ParseRecordFieldPatternOrRest(ctor);
                while (ctx.Match(","))
                {
                    if (ctx.Check("}"))
                    {
                        break;
                    }

                    ParseRecordFieldPatternOrRest(ctor);
                }
            }
            ctx.Expect("}");

            ctor.SetSpan(ctx.SpanFrom(startToken));
            return ctor;
        }

        // Should not reach here
        var fallbackCtor = new CtorPattern();
        fallbackCtor.SetSpan(ctx.SpanFrom(startToken));
        fallbackCtor.SetConstructorName(ctorName);
        fallbackCtor.SetPackageAlias(packageAlias);
        if (modulePath.Count > 0)
            fallbackCtor.SetModulePath(modulePath);
        return fallbackCtor;
    }

    private void ParseRecordFieldPatternOrRest(CtorPattern ctor)
    {
        if (!ctx.Check(".."))
        {
            if (ctor.HasRecordRest)
            {
                ctx.Error("record pattern rest '..' must be the final field pattern");
            }

            ParseFieldPattern(ctor);
            return;
        }

        if (ctor.HasRecordRest)
        {
            ctx.Error("record pattern rest '..' can only appear once");
        }

        ctor.SetRecordRest(true);
        ctx.Advance();

        if (!ctx.Check("}") && !ctx.Check(","))
        {
            ctx.Error("record pattern rest '..' does not bind a value");
            _ = ParsePattern();
        }
    }

    private void ParseFieldPattern(CtorPattern ctor)
    {
        var fieldStart = ctx.Current;
        var fieldName = ctx.GetText();
        ctx.Advance();

        var fieldPattern = new FieldPattern();
        fieldPattern.SetSpan(ctx.SpanFrom(fieldStart));
        fieldPattern.SetFieldName(fieldName);

        if (ctx.Match(":"))
            fieldPattern.SetPattern(ParsePattern());
        else
            fieldPattern.SetPattern(null); // triggers shorthand

        ctor.AddNamedPattern(fieldPattern);
    }

    private void AddListRestDiagnostic(string note)
    {
        ctx.AddDiagnostic(Diagnostic.Diagnostic.Error(DiagnosticMessages.ParserInvalidListRestPatternUsage, "E4000")
            .WithLabel(
                ctx.Current.Location is { } location ? new SourceSpan(location, Math.Max(1, ctx.Current.Length)) : SourceSpan.Empty,
                DiagnosticMessages.ParserListRestMarkerLabel)
            .WithNote(note)
            .WithHelp(DiagnosticMessages.ParserListRestUseHelp)
            .WithHelp(DiagnosticMessages.ParserListRestValidFormsHelp)
            .WithHelp(DiagnosticMessages.ParserListRestExplicitElementsHelp));
    }
}
