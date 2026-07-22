using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Diagnostic;
using Eidosc.Utils;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private readonly record struct PatternBindingOccurrence(
        string Path,
        string SlotPath,
        string Name,
        PatternBindingMode BindingMode);

    private void ResolvePatternGuardReferences(PatternGuardExpr patternGuard)
    {
        if (patternGuard.SourceExpression != null)
        {
            ResolveExpressionReferences(patternGuard.SourceExpression);
        }

        if (patternGuard.Pattern != null)
        {
            using var context = PushPatternDiagnosticContext("pattern-guard");
            patternGuard.SetPattern(ResolvePatternBindings(patternGuard.Pattern));
        }
        else
        {
            AddError(patternGuard.Span, DiagnosticMessages.PatternGuardRequiresPattern);
        }
    }

    private void ResolveSequentialGuardReferences(SequentialGuardExpr sequentialGuard)
    {
        foreach (var guard in sequentialGuard.Guards)
        {
            ResolveExpressionReferences(guard);
        }
    }

    private void ResolvePatternBranchReferences(
        PatternBranch branch,
        int branchIndex,
        bool isParameterBranch = false,
        SymbolId preferredPatternAdt = default)
    {
        using var _ = _symbolTable.PushScopeGuard(ScopeKind.PatternBranch);
        SymbolId boundPatternValue = SymbolId.None;

        if (branch.Pattern != null)
        {
            using var context = PushPatternDiagnosticContext($"branch#{branchIndex}");
            branch.SetPattern(ResolvePatternBindings(branch.Pattern, isParameter: isParameterBranch));
            if (preferredPatternAdt.IsValid && branch.Pattern is VarPattern { SymbolId.IsValid: true } variable)
            {
                boundPatternValue = variable.SymbolId;
                _patternValueAdtBindings[boundPatternValue] = preferredPatternAdt;
            }
        }
        try
        {
            if (branch.Guard != null)
            {
                ResolveExpressionReferences(branch.Guard);
            }

            if (branch.Expression != null)
            {
                ResolveExpressionReferences(branch.Expression);
            }
        }
        finally
        {
            if (boundPatternValue.IsValid)
            {
                _patternValueAdtBindings.Remove(boundPatternValue);
            }
        }
    }

    private Pattern ResolvePatternBindings(
        Pattern pattern,
        bool isMutableBinding = false,
        bool isComptimeBinding = false,
        bool isParameter = false)
    {
        pattern = ResolveBareNamePattern(pattern);
        switch (pattern)
        {
            case ExpandPattern expansion:
                return ResolveExpandPatternBindings(
                    expansion,
                    isMutableBinding,
                    isComptimeBinding,
                    isParameter);

            case VarPattern varPattern:
                var varId = DeclarePatternVariable(
                    GetSyntaxBindingName(varPattern, varPattern.Name),
                    pattern.Span,
                    isParameter: isParameter,
                    isPatternBound: true,
                    bindingMode: varPattern.BindingMode,
                    isMutable: isMutableBinding || varPattern.IsMutableBinding,
                    isComptime: isComptimeBinding,
                    existingSymbolId: varPattern.SymbolId);
                varPattern.SymbolId = varId;
                RegisterSyntaxIdentitySymbol(varPattern, varId);
                break;

            case WildcardPattern:
            case LiteralPattern:
                break;

            case CtorPattern ctorPattern:
                ResolveCtorPatternSymbol(ctorPattern, pattern.Span);

                for (var i = 0; i < ctorPattern.PositionalPatterns.Count; i++)
                {
                    using (PushPatternDiagnosticContext($"positional#{i + 1}"))
                    {
                        ctorPattern.PositionalPatterns[i] = ResolvePatternBindings(
                            ctorPattern.PositionalPatterns[i],
                            isMutableBinding,
                            isComptimeBinding,
                            isParameter);
                    }
                }

                foreach (var fieldPattern in ctorPattern.NamedPatterns)
                {
                    if (fieldPattern.Pattern != null)
                    {
                        var fieldSegment = string.IsNullOrWhiteSpace(fieldPattern.FieldName)
                            ? "field#<unnamed>"
                            : $"field#{fieldPattern.FieldName}";
                        using var context = PushPatternDiagnosticContext(fieldSegment);
                        fieldPattern.SetPattern(ResolvePatternBindings(
                            fieldPattern.Pattern,
                            isMutableBinding,
                            isComptimeBinding,
                            isParameter));
                    }
                }
                break;

            case TuplePattern tuplePattern:
                for (var i = 0; i < tuplePattern.Elements.Count; i++)
                {
                    using (PushPatternDiagnosticContext($"element#{i + 1}"))
                    {
                        tuplePattern.Elements[i] = ResolvePatternBindings(
                            tuplePattern.Elements[i],
                            isMutableBinding,
                            isComptimeBinding,
                            isParameter);
                    }
                }
                break;

            case ListPattern listPattern:
                for (var i = 0; i < listPattern.Elements.Count; i++)
                {
                    using (PushPatternDiagnosticContext($"element#{i + 1}"))
                    {
                        listPattern.Elements[i] = ResolvePatternBindings(
                            listPattern.Elements[i],
                            isMutableBinding,
                            isComptimeBinding,
                            isParameter);
                    }
                }

                if (listPattern.RestPattern != null)
                {
                    using (PushPatternDiagnosticContext("rest"))
                    {
                        listPattern.RestPattern = ResolvePatternBindings(
                            listPattern.RestPattern,
                            isMutableBinding,
                            isComptimeBinding,
                            isParameter);
                    }
                }

                for (var i = 0; i < listPattern.SuffixElements.Count; i++)
                {
                    using (PushPatternDiagnosticContext($"suffix#{i + 1}"))
                    {
                        listPattern.SuffixElements[i] = ResolvePatternBindings(
                            listPattern.SuffixElements[i],
                            isMutableBinding,
                            isComptimeBinding);
                    }
                }
                break;

            case NotPattern notPattern:
            {
                using var context = PushPatternDiagnosticContext("not-pattern");
                ResolveNotPatternBindings(notPattern);
                break;
            }

            case OrPattern orPattern:
            {
                using var context = PushPatternDiagnosticContext("or-pattern");
                ResolveOrPatternBindings(orPattern);
                break;
            }

            case AndPattern andPattern:
            {
                using var context = PushPatternDiagnosticContext("and-pattern");
                ResolveAndPatternBindings(andPattern);
                break;
            }

            case RangePattern rangePattern:
                if (rangePattern.Start != null)
                {
                    using (PushPatternDiagnosticContext("start"))
                    {
                        ResolvePatternBindings(rangePattern.Start, isMutableBinding, isComptimeBinding, isParameter);
                    }
                }

                if (rangePattern.End != null)
                {
                    using (PushPatternDiagnosticContext("end"))
                    {
                        ResolvePatternBindings(rangePattern.End, isMutableBinding, isComptimeBinding, isParameter);
                    }
                }
                break;

            case ViewPattern viewPattern:
                if (viewPattern.ViewExpression != null)
                {
                    ResolveExpressionReferences(viewPattern.ViewExpression);
                    viewPattern.SetTransparentIdentityView(
                        IsTransparentIdentityViewExpression(viewPattern.ViewExpression));
                }

                if (viewPattern.InnerPattern != null)
                {
                    using (PushPatternDiagnosticContext("view-inner"))
                    {
                        viewPattern.InnerPattern = ResolvePatternBindings(
                            viewPattern.InnerPattern,
                            isMutableBinding,
                            isComptimeBinding,
                            isParameter);
                    }
                }
                break;

            case AsPattern asPattern:
                var asId = DeclarePatternVariable(
                    GetSyntaxBindingName(asPattern, asPattern.BindingName),
                    pattern.Span,
                    isParameter: isParameter,
                    isPatternBound: true,
                    bindingMode: asPattern.BindingMode,
                    isMutable: isMutableBinding || asPattern.IsMutableBinding,
                    isComptime: isComptimeBinding,
                    existingSymbolId: asPattern.SymbolId);
                asPattern.SymbolId = asId;
                RegisterSyntaxIdentitySymbol(asPattern, asId);

                if (asPattern.InnerPattern != null)
                {
                    using (PushPatternDiagnosticContext("as-inner"))
                    {
                        asPattern.InnerPattern = ResolvePatternBindings(
                            asPattern.InnerPattern,
                            isMutableBinding,
                            isComptimeBinding,
                            isParameter);
                    }
                }
                break;
        }

        return pattern;
    }

    private Pattern ResolveBareNamePattern(Pattern pattern)
    {
        if (pattern is not VarPattern { MayResolveToConstructor: true } unresolved)
        {
            return pattern;
        }

        var ctorId = _symbolTable.LookupConstructor(unresolved.Name);
        if (ctorId is not { IsValid: true } resolvedCtorId ||
            !IsZeroFieldConstructor(resolvedCtorId))
        {
            unresolved.SetMayResolveToConstructor(false);
            return unresolved;
        }

        var constructor = new CtorPattern();
        constructor.SetSpan(unresolved.Span);
        constructor.SetConstructorName(unresolved.Name);
        constructor.SymbolId = resolvedCtorId;
        ValidateCtorPatternShape(constructor, resolvedCtorId);
        return constructor;
    }

    private bool IsZeroFieldConstructor(SymbolId ctorId)
    {
        return !_ctorPatternShapes.TryGetValue(ctorId, out var shape) ||
               shape.PositionalArity == 0 && shape.NamedFields.Count == 0;
    }

    private void ValidateCtorPatternShape(CtorPattern ctorPattern, SymbolId ctorId)
    {
        if (!_ctorPatternShapes.TryGetValue(ctorId, out var shape))
        {
            return;
        }

        var positionalCount = ctorPattern.PositionalPatterns.Count;
        var namedCount = ctorPattern.NamedPatterns.Count;
        var constructorName = string.IsNullOrWhiteSpace(ctorPattern.ConstructorName)
            ? $"ctor#{ctorId.Value}"
            : ctorPattern.ConstructorName;

        var seenFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fieldPattern in ctorPattern.NamedPatterns)
        {
            var fieldName = fieldPattern.FieldName?.Trim() ?? string.Empty;
            if (fieldName.Length == 0)
            {
                AddPatternError(fieldPattern.Span, DiagnosticMessages.NamedFieldPatternRequiresFieldName);
                continue;
            }

            if (!seenFields.Add(fieldName))
            {
                AddPatternError(
                    fieldPattern.Span,
                    DiagnosticMessages.DuplicateNamedFieldInConstructorPattern(fieldName, constructorName));
                continue;
            }

            if (shape.IsShapeKnown &&
                shape.NamedFields.Count > 0 &&
                !shape.NamedFields.Contains(fieldName))
            {
                AddPatternError(
                    fieldPattern.Span,
                    DiagnosticMessages.ConstructorPatternHasNoNamedField(constructorName, fieldName));
            }
        }

        if (!shape.IsShapeKnown)
        {
            return;
        }

        var hasDefinedNamedFields = shape.NamedFields.Count > 0;
        if (shape.PositionalArity == 0 && hasDefinedNamedFields && positionalCount > 0)
        {
            AddPatternError(
                ctorPattern.Span,
                DiagnosticMessages.ConstructorPatternDisallowsPositionalForNamedForm(constructorName));
        }
        else if (shape.PositionalArity > 0 && !hasDefinedNamedFields && namedCount > 0)
        {
            AddPatternError(
                ctorPattern.Span,
                DiagnosticMessages.ConstructorPatternDisallowsNamedForPositionalForm(constructorName));
        }

        if (positionalCount != shape.PositionalArity)
        {
            AddPatternError(
                ctorPattern.Span,
                DiagnosticMessages.ConstructorPatternExpectsPositionalCount(
                    constructorName,
                    shape.PositionalArity,
                    positionalCount));
        }
    }

    private Pattern ResolvePatternReferencesWithoutBinding(Pattern pattern)
    {
        pattern = ResolveBareNamePattern(pattern);
        switch (pattern)
        {
            case WildcardPattern:
            case LiteralPattern:
            case VarPattern:
                return pattern;

            case CtorPattern ctorPattern:
                ResolveCtorPatternSymbol(ctorPattern, pattern.Span);

                for (var i = 0; i < ctorPattern.PositionalPatterns.Count; i++)
                {
                    using (PushPatternDiagnosticContext($"positional#{i + 1}"))
                    {
                        ctorPattern.PositionalPatterns[i] = ResolvePatternReferencesWithoutBinding(
                            ctorPattern.PositionalPatterns[i]);
                    }
                }

                foreach (var named in ctorPattern.NamedPatterns)
                {
                    if (named.Pattern != null)
                    {
                        var fieldSegment = string.IsNullOrWhiteSpace(named.FieldName)
                            ? "field#<unnamed>"
                            : $"field#{named.FieldName}";
                        using var context = PushPatternDiagnosticContext(fieldSegment);
                        named.SetPattern(ResolvePatternReferencesWithoutBinding(named.Pattern));
                    }
                }
                return pattern;

            case TuplePattern tuplePattern:
                for (var i = 0; i < tuplePattern.Elements.Count; i++)
                {
                    using (PushPatternDiagnosticContext($"element#{i + 1}"))
                    {
                        tuplePattern.Elements[i] = ResolvePatternReferencesWithoutBinding(tuplePattern.Elements[i]);
                    }
                }
                return pattern;

            case ListPattern listPattern:
                for (var i = 0; i < listPattern.Elements.Count; i++)
                {
                    using (PushPatternDiagnosticContext($"element#{i + 1}"))
                    {
                        listPattern.Elements[i] = ResolvePatternReferencesWithoutBinding(listPattern.Elements[i]);
                    }
                }

                if (listPattern.RestPattern != null)
                {
                    using (PushPatternDiagnosticContext("rest"))
                    {
                        listPattern.RestPattern = ResolvePatternReferencesWithoutBinding(listPattern.RestPattern);
                    }
                }

                for (var i = 0; i < listPattern.SuffixElements.Count; i++)
                {
                    using (PushPatternDiagnosticContext($"suffix#{i + 1}"))
                    {
                        listPattern.SuffixElements[i] = ResolvePatternReferencesWithoutBinding(
                            listPattern.SuffixElements[i]);
                    }
                }
                return pattern;

            case OrPattern orPattern:
                for (var i = 0; i < orPattern.Alternatives.Count; i++)
                {
                    orPattern.Alternatives[i] = ResolvePatternReferencesWithoutBinding(orPattern.Alternatives[i]);
                }
                return pattern;

            case AndPattern andPattern:
                for (var i = 0; i < andPattern.Conjuncts.Count; i++)
                {
                    andPattern.Conjuncts[i] = ResolvePatternReferencesWithoutBinding(andPattern.Conjuncts[i]);
                }
                return pattern;

            case NotPattern notPattern:
                if (notPattern.InnerPattern != null)
                {
                    notPattern.InnerPattern = ResolvePatternReferencesWithoutBinding(notPattern.InnerPattern);
                }
                return pattern;

            case RangePattern rangePattern:
                if (rangePattern.Start != null)
                {
                    using (PushPatternDiagnosticContext("start"))
                    {
                        ResolvePatternReferencesWithoutBinding(rangePattern.Start);
                    }
                }

                if (rangePattern.End != null)
                {
                    using (PushPatternDiagnosticContext("end"))
                    {
                        ResolvePatternReferencesWithoutBinding(rangePattern.End);
                    }
                }
                return pattern;

            case ViewPattern viewPattern:
                if (viewPattern.ViewExpression != null)
                {
                    ResolveExpressionReferences(viewPattern.ViewExpression);
                    viewPattern.SetTransparentIdentityView(
                        IsTransparentIdentityViewExpression(viewPattern.ViewExpression));
                }

                if (viewPattern.InnerPattern != null)
                {
                    using (PushPatternDiagnosticContext("view-inner"))
                    {
                        viewPattern.InnerPattern = ResolvePatternReferencesWithoutBinding(viewPattern.InnerPattern);
                    }
                }
                return pattern;

            case AsPattern asPattern:
                if (asPattern.InnerPattern != null)
                {
                    using (PushPatternDiagnosticContext("as-inner"))
                    {
                        asPattern.InnerPattern = ResolvePatternReferencesWithoutBinding(asPattern.InnerPattern);
                    }
                }
                return pattern;
        }

        return pattern;
    }

    private void ResolveCtorPatternSymbol(CtorPattern ctorPattern, SourceSpan span)
    {
        if (!string.IsNullOrWhiteSpace(ctorPattern.PackageAlias) || ctorPattern.ModulePath.Count > 0)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(ctorPattern.PackageAlias))
            {
                parts.Add(ctorPattern.PackageAlias);
            }

            parts.AddRange(ctorPattern.ModulePath);
            parts.Add(ctorPattern.ConstructorName);

            var result = !string.IsNullOrWhiteSpace(ctorPattern.PackageAlias)
                ? ResolvePackageQualifiedPath(ctorPattern.PackageAlias, ctorPattern.ModulePath.Concat([ctorPattern.ConstructorName]).ToList())
                : ResolvePathWithImports(parts);
            if (!result.IsSuccess && !string.IsNullOrWhiteSpace(ctorPattern.PackageAlias))
            {
                result = ResolvePathWithImports(parts);
            }

            if (result.IsSuccess)
            {
                ctorPattern.SymbolId = result.SymbolId;
                ValidateCtorPatternShape(ctorPattern, result.SymbolId);
                return;
            }

            AddPatternError(span, result.ErrorMessage ?? DiagnosticMessages.UndefinedConstructor(ctorPattern.ConstructorName));
            return;
        }

        var ctorSymbol = _symbolTable.LookupConstructor(ctorPattern.ConstructorName);
        if (ctorSymbol != null)
        {
            ctorPattern.SymbolId = ctorSymbol.Value;
            ValidateCtorPatternShape(ctorPattern, ctorSymbol.Value);
            return;
        }

        AddPatternError(span, DiagnosticMessages.UndefinedConstructor(ctorPattern.ConstructorName));
    }

}
