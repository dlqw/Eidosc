using Eidosc.Ast.Types;
using Eidosc.Ast.Declarations;
using Eidosc.Diagnostic;
using Eidosc.Types;
using Eidosc.Utils;
using AstKind = Eidosc.Ast.Types.Kind;

namespace Eidosc.Parsing.Handwritten;

public sealed class TypeParser(ParserContext ctx)
{
    public TypeNode ParseType()
    {
        var startToken = ctx.Current;
        var left = ParseFunctionTypeHead();

        if (!ctx.Check("->"))
            return left;

        ctx.Advance(); // consume "->"

        if (ctx.Check("{"))
        {
            ctx.Error(DiagnosticMessages.ParserEffectfulTypeSyntaxRemoved);
            SkipRemovedEffectSet();
            var recoveredRight = ctx.Match("->")
                ? ParseType()
                : new TypePath { Span = ctx.SpanFrom(startToken) };
            if (recoveredRight is TypePath recoveredPath && string.IsNullOrWhiteSpace(recoveredPath.TypeName))
            {
                recoveredPath.SetTypeName(WellKnownStrings.BuiltinTypes.Unit);
            }

            return CreateArrowType(startToken, left, recoveredRight);
        }

        // Arrow: -> type
        var right = ParseType();
        var arrow = CreateArrowType(startToken, left, right);
        if (ctx.Match(WellKnownStrings.Keywords.Need))
        {
            arrow.SetRequiredEffects(ParseEffectRequirements());
        }

        return arrow;
    }

    private ArrowType CreateArrowType(Token startToken, TypeNode left, TypeNode right)
    {
        var arrow = new ArrowType();
        arrow.SetSpan(ctx.SpanFrom(startToken));
        arrow.SetParamType(left);
        arrow.SetReturnType(right);
        return arrow;
    }

    private List<EffectRequirementNode> ParseEffectRequirements()
    {
        var requirements = new List<EffectRequirementNode>();
        if (!TokenKind.IsTypeIdentifier(ctx.Current))
        {
            ctx.Error(DiagnosticMessages.ParserNeedClauseRequiresEffectPath);
            return requirements;
        }

        requirements.Add(ParseEffectRequirement());
        while (ctx.Match(","))
        {
            if (!TokenKind.IsTypeIdentifier(ctx.Current))
            {
                ctx.Error(DiagnosticMessages.ParserNeedClauseRequiresEffectPath);
                break;
            }

            requirements.Add(ParseEffectRequirement());
        }

        return requirements;
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

    private TypeNode ParseFunctionTypeHead()
    {
        return ParsePostfixType(ParseTypeAtom());
    }

    private TypeNode ParseTypeAtom()
    {
        if (ctx.Check("("))
        {
            var savedPos = ctx.SavePosition();
            ctx.Advance(); // consume "("

            if (ctx.Match(")"))
            {
                // Unit type "()" — treat as empty tuple / TypePath("Unit")
                return new TupleType { Span = ctx.SpanFrom(ctx.Peek(-2)) };
            }

            var first = ParseType();

            if (ctx.Match(","))
            {
                var elements = new List<TypeNode> { first };
                do { elements.Add(ParseType()); }
                while (ctx.Match(","));
                ctx.Expect(")");
                return new TupleType { Elements = elements, Span = ctx.SpanFrom(ctx.Peek(-1)) };
            }

            ctx.Expect(")");
            return first;
        }

        return ParsePrimaryType();
    }

    private TypeNode ParsePostfixType(TypeNode type)
    {
        while (ctx.Check(WellKnownStrings.Operators.OptionSuffix) ||
               ctx.Check(WellKnownStrings.Operators.Coalesce) ||
               ctx.Check("."))
        {
            if (ctx.Match("."))
            {
                type = ParseAssociatedTypeProjection(type);
                continue;
            }

            var suffixText = ctx.GetText();
            var suffixToken = ctx.Current;
            ctx.Advance();

            type = WrapOptionType(type, suffixToken);
            if (suffixText == WellKnownStrings.Operators.Coalesce)
            {
                type = WrapOptionType(type, suffixToken);
            }
        }

        return type;
    }

    private TypeNode ParseAssociatedTypeProjection(TypeNode target)
    {
        var memberToken = ctx.Current;
        var memberName = ctx.GetText();
        if (!TokenKind.IsTypeIdentifier(ctx.Current))
        {
            ctx.Error(DiagnosticMessages.ParserExpectedTypeIdentifierAfterQualifiedSeparator);
        }
        else
        {
            ctx.Advance();
        }

        var projection = new AssociatedTypeProjection();
        projection.SetTarget(target);
        projection.SetMemberName(memberName);
        projection.SetSpan(target.Span.Position >= 0
            ? new SourceSpan(target.Span.Location, Math.Max(0, memberToken.Location.Position + memberToken.Length - target.Span.Position))
            : ctx.SpanFrom(memberToken));
        return projection;
    }

    private static TypePath WrapOptionType(TypeNode innerType, Token suffixToken)
    {
        var optionType = new TypePath
        {
            ModulePath = ["Option"],
            TypeArgs = [innerType]
        };
        optionType.SetPackageAlias("Std");
        optionType.SetTypeName("Option");

        var length = innerType.Span.Position >= 0
            ? Math.Max(0, suffixToken.Location.Position + suffixToken.Length - innerType.Span.Position)
            : suffixToken.Length;
        optionType.SetSpan(new SourceSpan(innerType.Span.Location, length));
        return optionType;
    }

    private TypeNode ParsePrimaryType()
    {
        if (ctx.Match("_"))
        {
            return new WildcardType { Span = ctx.SpanFrom(ctx.Peek(-1)) };
        }

        if (TokenKind.IsTypeIdentifier(ctx.Current) ||
            (TokenKind.IsAnyIdentifier(ctx.Current) && QualifiedPathParser.IsPackageQualifiedItemLookahead(ctx)))
        {
            return ParseTypePath();
        }

        ctx.Error(DiagnosticMessages.ParserExpectedType(ctx.GetText()));
        return new WildcardType { Span = new SourceSpan(ctx.Current.Location, 0) };
    }

    private TypePath ParseTypePath()
    {
        var startToken = ctx.Current;
        var parsedPath = QualifiedPathParser.ParseItemPath(
            ctx,
            TokenKind.IsTypeIdentifier,
            DiagnosticMessages.ParserExpectedTypeIdentifierAfterQualifiedSeparator);

        var genericArguments = TryParseGenericArguments();

        var path = new TypePath();
        path.SetSpan(ctx.SpanFrom(startToken));
        path.SetPackageAlias(parsedPath.PackageAlias);
        path.SetTypeName(parsedPath.Name);
        path.ModulePath = parsedPath.ModulePath;
        if (genericArguments != null)
        {
            path.SetGenericArguments(genericArguments);
        }
        return path;
    }

    public List<TypeNode>? TryParseTypeArgs()
    {
        var arguments = TryParseGenericArguments();
        if (arguments == null || arguments.Any(static argument => argument is not UnresolvedGenericArgumentNode { TypeCandidate: not null }))
        {
            return null;
        }

        return arguments
            .Cast<UnresolvedGenericArgumentNode>()
            .Select(static argument => argument.TypeCandidate!)
            .ToList();
    }

    public List<GenericArgumentNode>? TryParseGenericArguments()
    {
        if (!ctx.Match("["))
        {
            return null;
        }

        var arguments = new List<GenericArgumentNode>();
        if (!ctx.Check("]"))
        {
            arguments.Add(ParseUnresolvedGenericArgument());
            while (ctx.Match(","))
            {
                arguments.Add(ParseUnresolvedGenericArgument());
            }
        }

        ctx.Expect("]");
        return arguments.Count > 0 ? arguments : null;
    }

    private GenericArgumentNode ParseUnresolvedGenericArgument()
    {
        var startToken = ctx.Current;
        if (IsClearlyValueGenericArgument())
        {
            var expression = new ExprParser(ctx).ParseExpr();
            return new UnresolvedGenericArgumentNode
            {
                ValueCandidate = expression,
                Span = ctx.SpanFrom(startToken)
            };
        }

        var type = ParseType();
        return new UnresolvedGenericArgumentNode
        {
            TypeCandidate = type,
            Span = type.Span
        };
    }

    private bool IsClearlyValueGenericArgument()
    {
        var token = ctx.Current;
        if (TokenKind.IsAnyLiteral(token) || TokenKind.IsIdentifier(token) ||
            ctx.Check("-") || ctx.Check("!") || ctx.Check("[") || ctx.Check("{") || ctx.Check("ref") || ctx.Check("mref"))
        {
            return true;
        }

        if (!TokenKind.IsTypeIdentifier(token) && !ctx.Check("("))
        {
            return true;
        }

        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (var offset = 0; ; offset++)
        {
            var current = ctx.Peek(offset);
            if (current is EofToken)
            {
                return false;
            }

            var text = ctx.GetText(current);
            if (offset > 0 && parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && text is "," or "]")
            {
                return false;
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
                case "+" or "-" or "*" or "/" or "%" or "==" or "!=" or "<" or ">" or "<=" or ">=" or "&&" or "||" or "??" or "|>" or ">>=" or "++" or "<>" or "+:" or ":+":
                    return true;
            }
        }
    }

    private sealed record ParsedEffectPath(List<string> Path, SourceSpan Span);

    private void SkipRemovedEffectSet()
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
                continue;
            }

            if (ctx.Match("}"))
            {
                depth--;
                continue;
            }

            ctx.Advance();
        }
    }

    private List<ParsedEffectPath> ParseEffectSet()
    {
        var result = new List<ParsedEffectPath>();
        if (ctx.Check("}")) return result;

        result.Add(ParseEffectPath());
        while (ctx.Match(","))
            result.Add(ParseEffectPath());

        return result;
    }

    private ParsedEffectPath ParseEffectPath()
    {
        var startToken = ctx.Current;
        if (!TokenKind.IsAnyIdentifier(ctx.Current))
        {
            return new ParsedEffectPath([], new SourceSpan(startToken.Location, 0));
        }

        var path = QualifiedPathParser.Parse(
            ctx,
            TokenKind.IsAnyIdentifier,
            DiagnosticMessages.ParserExpectedIdentifierAfterQualifiedSeparator);
        return new ParsedEffectPath(path, ctx.SpanFrom(startToken));
    }

    public AstKind ParseKind()
    {
        var startToken = ctx.Current;
        var kindText = BuildKindText();
        var span = ctx.SpanFrom(startToken);

        if (!KindParser.TryParse(kindText, out var kind, out _))
        {
            return new AstKind { KindText = "kind1", IsStar = true, Span = span };
        }

        var result = new AstKind
        {
            IsStar = true,
            KindText = KindParser.ToKindText(kind),
            Span = span
        };
        var arity = KindParser.GetTopLevelArity(kind);
        for (var i = 0; i < arity; i++)
        {
            result.Parameters.Add(new AstKind { IsStar = true, KindText = "kind1", Span = span });
        }
        return result;
    }

    private string BuildKindText()
    {
        return ParseKindAtom();
    }

    private string ParseKindAtom()
    {
        if (IsCompactKindToken(ctx.Current))
        {
            var text = ctx.GetText();
            ctx.Advance();
            while (ctx.Match("->"))
            {
                text += " -> " + ParseKindAtom();
            }
            return text;
        }

        if (ctx.Match("("))
        {
            var inner = ParseKindAtom();
            ctx.Expect(")");
            var text = $"({inner})";
            while (ctx.Match("->"))
            {
                text += " -> " + ParseKindAtom();
            }
            return text;
        }

        ctx.Error(DiagnosticMessages.ParserExpectedKind(ctx.GetText()));
        return "kind1";
    }

    private bool IsCompactKindToken(Token token)
    {
        return KindParser.IsCompactKindName(ctx.GetText(token));
    }

    public TypeParam ParseTypeParam()
    {
        var startToken = ctx.Current;
        var isComptime = ctx.Match(WellKnownStrings.Keywords.Comptime);
        string name;
        if (!InternalNameParser.TryParseLeadingDoubleUnderscoreName(ctx, out name))
        {
            name = ctx.GetText();
            if (!TokenKind.IsTypeIdentifier(ctx.Current))
            {
                ctx.Error(DiagnosticMessages.ParserTypeParameterNameMustStartWithUppercase);
            }

            if (!ctx.IsEof)
            {
                ctx.Advance();
            }
        }

        AstKind? kindAnnotation = null;
        var isEffectSet = false;
        TypeNode? comptimeTypeAnnotation = null;
        var traitConstraints = new List<TraitRef>();

        if (ctx.Match(":"))
        {
            if (ctx.Match(WellKnownStrings.Keywords.Effects))
            {
                isEffectSet = true;
            }
            else if (ctx.Match(WellKnownStrings.Keywords.Comptime))
            {
                isComptime = true;
                comptimeTypeAnnotation = ParseType();
            }
            // Distinguish: T: kindN (kind) vs T: Trait::Eq (trait constraint)
            else if (isComptime)
            {
                comptimeTypeAnnotation = ParseType();
            }
            else if (IsKindStart(ctx.Current))
            {
                kindAnnotation = ParseKind();
                // After kind, optional trait constraints with second ":"
                if (ctx.Match(":"))
                {
                    traitConstraints.Add(ParseTraitRef());
                    while (ctx.Match("+"))
                        traitConstraints.Add(ParseTraitRef());
                }
            }
            else
            {
                // Trait constraint: T: TraitName
                traitConstraints.Add(ParseTraitRef());
                while (ctx.Match("+"))
                    traitConstraints.Add(ParseTraitRef());
            }
        }

        return new TypeParam
        {
            Name = name,
            KindAnnotation = kindAnnotation,
            IsEffectSet = isEffectSet,
            IsComptime = isComptime,
            ComptimeTypeAnnotation = comptimeTypeAnnotation,
            TraitConstraints = traitConstraints,
            Span = ctx.SpanFrom(startToken)
        };
    }

    public void ApplyGenericWhereClause(IReadOnlyList<TypeParam> typeParams)
    {
        if (!ctx.Match(WellKnownStrings.Keywords.Where))
        {
            return;
        }

        var typeParamByName = new Dictionary<string, TypeParam>(StringComparer.Ordinal);
        foreach (var typeParam in typeParams)
        {
            typeParamByName.TryAdd(typeParam.Name, typeParam);
        }

        do
        {
            var targetToken = ctx.Current;
            if (!TokenKind.IsAnyIdentifier(targetToken))
            {
                ctx.Error(DiagnosticMessages.ParserUnexpectedToken(ctx.GetText()));
                if (!ctx.IsEof)
                {
                    ctx.Advance();
                }
                return;
            }

            var targetName = ctx.GetText();
            if (!TokenKind.IsTypeIdentifier(targetToken))
            {
                ctx.Error(DiagnosticMessages.ParserGenericWhereTargetMustStartWithUppercase);
            }

            ctx.Advance();
            ctx.Expect(":");

            typeParamByName.TryGetValue(targetName, out var typeParam);
            if (IsKindStart(ctx.Current))
            {
                var kindAnnotation = ParseKind();
                if (typeParam != null)
                {
                    typeParam.KindAnnotation = kindAnnotation;
                }

                if (ctx.Match(":"))
                {
                    ApplyTraitConstraints(typeParam, ParseTraitConstraintList());
                }
            }
            else
            {
                ApplyTraitConstraints(typeParam, ParseTraitConstraintList());
            }
        }
        while (ctx.Match(","));
    }

    private bool IsKindStart(Token token)
    {
        return ctx.Check("(") || IsCompactKindToken(token);
    }

    private List<TraitRef> ParseTraitConstraintList()
    {
        var constraints = new List<TraitRef> { ParseTraitRef() };
        while (ctx.Match("+"))
        {
            constraints.Add(ParseTraitRef());
        }

        return constraints;
    }

    private static void ApplyTraitConstraints(TypeParam? typeParam, IReadOnlyList<TraitRef> constraints)
    {
        if (typeParam == null)
        {
            return;
        }

        foreach (var constraint in constraints)
        {
            if (typeParam.TraitConstraints.Any(existing => TypeParamTraitConstraintEquals(existing, constraint)))
            {
                continue;
            }

            typeParam.TraitConstraints.Add(constraint);
        }
    }

    private static bool TypeParamTraitConstraintEquals(TraitRef left, TraitRef right)
    {
        return string.Equals(left.TraitName, right.TraitName, StringComparison.Ordinal) &&
               left.ModulePath.SequenceEqual(right.ModulePath, StringComparer.Ordinal) &&
               left.TypeArgs.Count == right.TypeArgs.Count;
    }

    public List<TypeParam>? TryParseTypeParams()
    {
        if (!ctx.Check("[")) return null;

        ctx.Advance(); // consume "["
        var parameters = new List<TypeParam>();
        if (!ctx.Check("]"))
        {
            parameters.Add(ParseTypeParam());
            while (ctx.Match(","))
                parameters.Add(ParseTypeParam());
        }
        ctx.Expect("]");
        return parameters.Count > 0 ? parameters : null;
    }

    public TraitRef ParseTraitRef()
    {
        var startToken = ctx.Current;
        var parts = new List<string>();

        if (TokenKind.IsTypeIdentifier(ctx.Current) ||
            (TokenKind.IsAnyIdentifier(ctx.Current) && QualifiedPathParser.IsPackageQualifiedItemLookahead(ctx, TokenKind.IsTypeIdentifier)))
        {
            var parsedPath = QualifiedPathParser.ParseItemPath(
                ctx,
                TokenKind.IsTypeIdentifier,
                DiagnosticMessages.ParserExpectedTypeIdentifierAfterQualifiedSeparator);
            parts = parsedPath.ToPathParts();
        }
        else
        {
            ctx.Error(DiagnosticMessages.ParserExpectedTypeIdentifierAfterQualifiedSeparator);
            if (!ctx.IsEof)
            {
                ctx.Advance();
            }
        }

        var genericArguments = TryParseGenericArguments();

        var traitRef = new TraitRef();
        traitRef.SetSpan(ctx.SpanFrom(startToken));
        if (parts.Count > 0)
        {
            traitRef.SetTraitName(parts[^1]);
            if (parts.Count > 1)
                traitRef.ModulePath = parts.Take(parts.Count - 1).ToList();
        }
        if (genericArguments != null)
            traitRef.SetGenericArguments(genericArguments);
        return traitRef;
    }

    public List<TraitRef>? TryParseTraitConstraints()
    {
        if (!ctx.Check(":")) return null;

        ctx.Advance();
        var constraints = new List<TraitRef>();
        constraints.Add(ParseTraitRef());
        while (ctx.Match("+"))
            constraints.Add(ParseTraitRef());
        return constraints;
    }
}
