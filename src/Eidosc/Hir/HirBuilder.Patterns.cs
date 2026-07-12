using Eidosc.Ast;
using Eidosc.Diagnostic;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Hir;

/// <summary>
/// Pattern conversion: AST patterns to HIR patterns.
/// </summary>
public sealed partial class HirBuilder
{
    private HirPattern ConvertPattern(Ast.Patterns.Pattern? pattern)
    {
        if (pattern == null)
        {
            Diagnostics.Add(AddHirFallbackMetadata(
                Diagnostic.Diagnostic.Error(DiagnosticMessages.MissingPatternDuringHirLowering, "E5111"),
                fallbackKind: "pattern",
                reason: "missing-pattern",
                hirNodeKind: nameof(HirErrorPattern)));
            return new HirErrorPattern
            {
                Reason = DiagnosticMessages.MissingPatternDuringHirLowering
            };
        }

        if (pattern.IsRecovered)
        {
            return new HirErrorPattern
            {
                Span = pattern.Span,
                TypeId = GetTypeId(pattern),
                Reason = pattern.RecoveryReason ?? AstRecoveryReasons.ParserRecoveredLiteral,
                IsRecovered = true
            };
        }

        return pattern switch
        {
            Ast.Patterns.VarPattern varPat => new HirVarPattern
            {
                Name = varPat.Name,
                SymbolId = varPat.SymbolId,
                BindingMode = varPat.BindingMode,
                IsMutableBinding = varPat.IsMutableBinding,
                Span = varPat.Span,
                TypeId = GetTypeId(varPat)
            },
            Ast.Patterns.WildcardPattern => new HirVarPattern
            {
                IsWildcard = true,
                Span = pattern.Span,
                TypeId = GetTypeId(pattern)
            },
            Ast.Patterns.LiteralPattern literalPattern => ConvertLiteralPattern(literalPattern),
            Ast.Patterns.TuplePattern tuplePattern => new HirTuplePattern
            {
                Elements = tuplePattern.Elements.Select(ConvertPattern).ToList(),
                Span = tuplePattern.Span,
                TypeId = GetTypeId(tuplePattern)
            },
            Ast.Patterns.ListPattern listPattern => ConvertListPattern(listPattern),
            Ast.Patterns.CtorPattern ctorPattern => ConvertCtorPattern(ctorPattern),
            Ast.Patterns.NotPattern notPattern => ConvertNotPattern(notPattern),
            Ast.Patterns.OrPattern orPattern => ConvertOrPattern(orPattern),
            Ast.Patterns.AndPattern andPattern => ConvertAndPattern(andPattern),
            Ast.Patterns.RangePattern rangePattern => ConvertRangePattern(rangePattern),
            Ast.Patterns.ViewPattern viewPattern => ConvertViewPattern(viewPattern),
            Ast.Patterns.AsPattern asPattern => new HirAsPattern
            {
                InnerPattern = asPattern.InnerPattern != null
                    ? ConvertPattern(asPattern.InnerPattern)
                    : new HirVarPattern { IsWildcard = true, Span = asPattern.Span },
                Name = asPattern.BindingName,
                SymbolId = asPattern.SymbolId,
                BindingMode = asPattern.BindingMode,
                IsMutableBinding = asPattern.IsMutableBinding,
                Span = asPattern.Span,
                TypeId = GetTypeId(asPattern)
            },
            _ => ReportUnsupportedPattern(pattern)
        };
    }

    private HirPattern ConvertOrPattern(Ast.Patterns.OrPattern orPattern)
    {
        if (orPattern.Alternatives.Count == 0)
        {
            return new HirVarPattern
            {
                IsWildcard = true,
                Span = orPattern.Span,
                TypeId = GetTypeId(orPattern)
            };
        }

        if (orPattern.Alternatives.Count == 1)
        {
            return ConvertPattern(orPattern.Alternatives[0]);
        }

        var current = ConvertPattern(orPattern.Alternatives[0]);
        for (var i = 1; i < orPattern.Alternatives.Count; i++)
        {
            current = new HirOrPattern
            {
                Left = current,
                Right = ConvertPattern(orPattern.Alternatives[i]),
                Span = orPattern.Span,
                TypeId = GetTypeId(orPattern)
            };
        }

        return current;
    }

    private HirPattern ConvertNotPattern(Ast.Patterns.NotPattern notPattern)
    {
        if (notPattern.InnerPattern == null)
        {
            Diagnostics.Add(CreateDiagnostic(
                DiagnosticMessages.NotPatternMissingInnerPattern,
                notPattern.Span,
                "E5111",
                DiagnosticMessages.NotPatternMissingInnerLabel));

            return new HirVarPattern
            {
                IsWildcard = true,
                Span = notPattern.Span,
                TypeId = GetTypeId(notPattern)
            };
        }

        return new HirNotPattern
        {
            InnerPattern = ConvertPattern(notPattern.InnerPattern),
            Span = notPattern.Span,
            TypeId = GetTypeId(notPattern)
        };
    }

    private HirPattern ConvertAndPattern(Ast.Patterns.AndPattern andPattern)
    {
        if (andPattern.Conjuncts.Count == 0)
        {
            return new HirVarPattern
            {
                IsWildcard = true,
                Span = andPattern.Span,
                TypeId = GetTypeId(andPattern)
            };
        }

        if (andPattern.Conjuncts.Count == 1)
        {
            return ConvertPattern(andPattern.Conjuncts[0]);
        }

        var current = ConvertPattern(andPattern.Conjuncts[0]);
        for (var i = 1; i < andPattern.Conjuncts.Count; i++)
        {
            current = new HirAndPattern
            {
                Left = current,
                Right = ConvertPattern(andPattern.Conjuncts[i]),
                Span = andPattern.Span,
                TypeId = GetTypeId(andPattern)
            };
        }

        return current;
    }

    private HirPattern ConvertRangePattern(Ast.Patterns.RangePattern rangePattern)
    {
        if (rangePattern.Start == null || rangePattern.End == null)
        {
            Diagnostics.Add(CreateDiagnostic(
                DiagnosticMessages.RangePatternRequiresStartAndEndLiterals,
                rangePattern.Span,
                "E5111",
                DiagnosticMessages.InvalidRangePatternLabel));

            return new HirVarPattern
            {
                IsWildcard = true,
                Span = rangePattern.Span,
                TypeId = GetTypeId(rangePattern)
            };
        }

        return new HirRangePattern
        {
            Start = ConvertLiteralPattern(rangePattern.Start),
            End = ConvertLiteralPattern(rangePattern.End),
            Span = rangePattern.Span,
            TypeId = GetTypeId(rangePattern)
        };
    }

    private HirPattern ConvertViewPattern(Ast.Patterns.ViewPattern viewPattern)
    {
        if (viewPattern.ViewExpression == null)
        {
            Diagnostics.Add(CreateDiagnostic(
                DiagnosticMessages.ViewPatternMissingViewExpression,
                viewPattern.Span,
                "E5111",
                DiagnosticMessages.ViewPatternMissingViewExpressionLabel));
        }

        if (viewPattern.InnerPattern == null)
        {
            Diagnostics.Add(CreateDiagnostic(
                DiagnosticMessages.ViewPatternMissingInnerPattern,
                viewPattern.Span,
                "E5111",
                DiagnosticMessages.ViewPatternMissingInnerPatternLabel));
        }

        return new HirViewPattern
        {
            View = ConvertExprOrFallback(viewPattern.ViewExpression, "view pattern expression", viewPattern.Span),
            ViewResultTypeId = ResolveViewPatternResultTypeId(viewPattern),
            InnerPattern = viewPattern.InnerPattern != null
                ? ConvertPattern(viewPattern.InnerPattern)
                : new HirVarPattern
                {
                    IsWildcard = true,
                    Span = viewPattern.Span,
                    TypeId = TypeId.None
                },
            Span = viewPattern.Span,
            TypeId = GetTypeId(viewPattern)
        };
    }

    private TypeId ResolveViewPatternResultTypeId(Ast.Patterns.ViewPattern viewPattern)
    {
        if (viewPattern.InnerPattern != null)
        {
            var innerTypeId = GetTypeId(viewPattern.InnerPattern);
            if (innerTypeId.IsValid)
            {
                return innerTypeId;
            }
        }

        if (TryGetResolvedInferredType(viewPattern.ViewExpression, out var resolvedViewType) &&
            resolvedViewType is TyFun viewFunctionType)
        {
            var resultTypeId = GetTypeTypeId(viewFunctionType.Result);
            if (resultTypeId.IsValid)
            {
                return resultTypeId;
            }
        }

        return TypeId.None;
    }

    private HirPattern ConvertListPattern(Ast.Patterns.ListPattern listPattern)
    {
        return new HirListPattern
        {
            Elements = listPattern.Elements.Select(ConvertPattern).ToList(),
            HasRest = listPattern.HasRestMarker,
            RestPattern = listPattern.RestPattern != null
                ? ConvertPattern(listPattern.RestPattern)
                : null,
            SuffixElements = listPattern.SuffixElements.Select(ConvertPattern).ToList(),
            Span = listPattern.Span,
            TypeId = GetTypeId(listPattern)
        };
    }

    private HirLiteralPattern ConvertLiteralPattern(Ast.Patterns.LiteralPattern literalPattern)
    {
        return new HirLiteralPattern
        {
            Value = literalPattern.Value,
            Span = literalPattern.Span,
            TypeId = GetLiteralPatternTypeId(literalPattern)
        };
    }

    private HirCtorPattern ConvertCtorPattern(Ast.Patterns.CtorPattern ctorPattern)
    {
        var hirCtorPattern = new HirCtorPattern
        {
            ConstructorName = ctorPattern.ConstructorName,
            ConstructorSymbolId = ctorPattern.SymbolId,
            Span = ctorPattern.Span,
            TypeId = GetTypeId(ctorPattern)
        };

        for (int i = 0; i < ctorPattern.PositionalPatterns.Count; i++)
        {
            var positional = ctorPattern.PositionalPatterns[i];
            hirCtorPattern.Fields.Add(new HirFieldPattern
            {
                FieldName = $"_{i}",
                Pattern = ConvertPattern(positional)
            });
        }

        foreach (var named in ctorPattern.NamedPatterns)
        {
            var fieldPattern = named.Pattern != null
                ? ConvertPattern(named.Pattern)
                : new HirVarPattern
                {
                    Name = named.FieldName,
                    SymbolId = named.SymbolId,
                    Span = named.Span,
                    TypeId = GetTypeId(named)
                };

            hirCtorPattern.Fields.Add(new HirFieldPattern
            {
                FieldName = named.FieldName,
                Pattern = fieldPattern
            });
        }

        return hirCtorPattern;
    }

    private static TypeId GetLiteralPatternTypeId(Ast.Patterns.LiteralPattern pattern)
    {
        return pattern.Type switch
        {
            Ast.Patterns.LiteralType.Integer => new TypeId(BaseTypes.IntId),
            Ast.Patterns.LiteralType.Float => new TypeId(BaseTypes.FloatId),
            Ast.Patterns.LiteralType.String => new TypeId(BaseTypes.StringId),
            Ast.Patterns.LiteralType.Char => new TypeId(BaseTypes.CharId),
            Ast.Patterns.LiteralType.Boolean => new TypeId(BaseTypes.BoolId),
            _ => TypeId.None
        };
    }

    private HirNode ConvertExprOrFallback(EidosAstNode? node, string context, SourceSpan span)
    {
        if (node != null)
        {
            return ConvertExpr(node);
        }

        return ReportAndCreateError(
            DiagnosticMessages.MissingExpressionWhileLowering(context),
            span,
            "E5110",
            fallbackKind: "expression",
            reason: "missing-expression",
            context: context,
            astNodeKind: null);
    }

    private HirNode ReportUnsupportedExpr(EidosAstNode node)
    {
        return ReportAndCreateError(
            DiagnosticMessages.UnsupportedAstExpressionDuringHirLowering(node.GetType().Name),
            node.Span,
            "E5100",
            fallbackKind: "expression",
            reason: "unsupported-ast-expression",
            context: null,
            astNodeKind: node.GetType().Name);
    }

    private HirPattern ReportUnsupportedPattern(Ast.Patterns.Pattern pattern)
    {
        var diag = AddHirFallbackMetadata(
            Diagnostic.Diagnostic.Error(
                DiagnosticMessages.UnsupportedAstPatternDuringHirLowering(pattern.GetType().Name),
                "E5101"),
            fallbackKind: "pattern",
            reason: "unsupported-ast-pattern",
            hirNodeKind: nameof(HirErrorPattern),
            astNodeKind: pattern.GetType().Name);
        if (HasSpan(pattern.Span))
        {
            diag.WithLabel(pattern.Span, DiagnosticMessages.UnsupportedPatternLabel);
        }
        Diagnostics.Add(diag);

        return new HirErrorPattern
        {
            Span = pattern.Span,
            TypeId = GetTypeId(pattern),
            Reason = DiagnosticMessages.UnsupportedAstPatternReason(pattern.GetType().Name)
        };
    }
}
