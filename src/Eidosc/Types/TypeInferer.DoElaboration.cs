using Eidosc.Ast;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Eidosc.Symbols;
using Eidosc.Utils;

namespace Eidosc.Types;

public sealed partial class TypeInferer
{
    private bool TryMergeDoContainerShape(Type valueType, ref DoContainerShape? containerShape, SourceSpan span)
    {
        var resolved = _substitution.Apply(valueType);
        if (!TryGetDoContainerShape(resolved, out var current))
        {
            AddError(span, DiagnosticMessages.DoBindExpectsMonadicValue(resolved));
            return false;
        }

        if (containerShape == null)
        {
            containerShape = current;
            return true;
        }

        var expected = ReplaceDoContainerElement(containerShape, current.ElementType);
        if (!TryUnifyOperatorType(
                expected,
                current.AppliedType,
                span,
                "All effectful items in a do expression must use the same unary type constructor"))
        {
            return false;
        }

        containerShape = containerShape with
        {
            AppliedType = _substitution.Apply(expected) as TyCon ?? containerShape.AppliedType,
            ElementType = _substitution.Apply(current.ElementType)
        };
        return true;
    }

    private bool TryGetDoContainerShape(Type type, out DoContainerShape shape)
    {
        if (type is not TyCon { Args.Count: > 0 } constructor)
        {
            shape = null!;
            return false;
        }

        if (TryPromoteClosedCaseToRoot(constructor, out var rootType))
        {
            constructor = rootType;
        }

        if (PreludeCoreImageRegistry.TryDecomposeInstanceApplication(
                "Monad",
                constructor,
                out var decomposition))
        {
            var typeConstructor = decomposition.TypeConstructor;
            var constructorSymbol = string.Equals(typeConstructor.Name, constructor.Name, StringComparison.Ordinal) &&
                                    constructor.Symbol.IsValid
                ? constructor.Symbol
                : _symbolTable.LookupType(typeConstructor.Name) ?? SymbolId.None;
            if (constructorSymbol.IsValid)
            {
                var constructorTypeId = _symbolTable.GetSymbol<AdtSymbol>(constructorSymbol)?.TypeId ?? TypeId.None;
                typeConstructor = typeConstructor with
                {
                    Symbol = constructorSymbol,
                    Id = constructorTypeId
                };
            }

            shape = new DoContainerShape(
                decomposition.AppliedType,
                typeConstructor,
                decomposition.ElementType,
                decomposition.ElementTypeArgumentIndex);
            return true;
        }

        var elementIndex = constructor.Args.Count - 1;
        shape = new DoContainerShape(
            constructor,
            constructor with { Args = constructor.Args.Take(elementIndex).ToList() },
            constructor.Args[elementIndex],
            elementIndex);
        return true;
    }

    private static TyCon ReplaceDoContainerElement(DoContainerShape shape, Type elementType)
    {
        var arguments = shape.AppliedType.Args.ToList();
        arguments[shape.ElementTypeArgumentIndex] = elementType;
        return shape.AppliedType with { Args = arguments };
    }

    private sealed record DoContainerShape(
        TyCon AppliedType,
        TyCon TypeConstructor,
        Type ElementType,
        int ElementTypeArgumentIndex);

    private void AddDoTraitConstraint(Type typeConstructor, SymbolId traitId, string traitName, SourceSpan span)
    {
        _constraintGenerator.Constraints.AddTrait(
            typeConstructor,
            traitId,
            traitName,
            span,
            [typeConstructor]);
    }

    private SymbolId ResolveDoTrait(CompilerSemanticRole role, string fallbackName)
    {
        var owners = _symbolTable.Symbols.Values
            .OfType<FuncSymbol>()
            .Where(function => function.CompilerSemanticRole == role && function.OwnerTrait is { } owner && owner.IsValid)
            .Select(function => function.OwnerTrait!.Value)
            .Distinct()
            .ToArray();
        if (owners.Length == 1)
        {
            return owners[0];
        }

        return _symbolTable.LookupTrait(fallbackName) ?? SymbolId.None;
    }

    private bool IsIrrefutableDoPattern(Pattern pattern) => pattern switch
    {
        VarPattern => true,
        WildcardPattern => true,
        TuplePattern tuple => tuple.Elements.All(IsIrrefutableDoPattern),
        AsPattern { InnerPattern: not null } asPattern => IsIrrefutableDoPattern(asPattern.InnerPattern),
        CtorPattern constructor => IsIrrefutableDoConstructorPattern(constructor),
        _ => false
    };

    private bool IsIrrefutableDoConstructorPattern(CtorPattern pattern)
    {
        if (!pattern.SymbolId.IsValid ||
            _symbolTable.GetSymbol<CtorSymbol>(pattern.SymbolId) is not { } constructor ||
            _symbolTable.GetSymbol<AdtSymbol>(constructor.OwnerAdt) is not { } owner)
        {
            return false;
        }

        var rootId = _symbolTable.GetClosedCaseRoot(owner.Id);
        if (_symbolTable.GetSymbol<AdtSymbol>(rootId) is not { } root)
        {
            return false;
        }

        var hasSingleConstructor = root.DirectCases.Count > 0
            ? _symbolTable.GetClosedCaseLeafCases(root.Id).Count == 1
            : root.Constructors.Count == 1;
        if (!hasSingleConstructor)
        {
            return false;
        }

        return pattern.PositionalPatterns.All(IsIrrefutableDoPattern) &&
               pattern.NamedPatterns.All(field => field.Pattern == null || IsIrrefutableDoPattern(field.Pattern));
    }

    private void FinalizeDoElaborationPlans()
    {
        foreach (var doExpression in _doExpressions)
        {
            if (doExpression.ElaborationDraft is not { } draft ||
                !TryGetProvenDoTraitEvidence(
                    _constraintSolver,
                    draft.MonadTrait,
                    "Monad",
                    doExpression.Span,
                    out var monadEvidence))
            {
                continue;
            }

            var functorTrait = ResolveDoTrait(CompilerSemanticRole.FunctorMap, "Functor");
            var applicativeTrait = ResolveDoTrait(CompilerSemanticRole.ApplicativeApply, "Applicative");
            ObligationResult? functorEvidence = null;
            ObligationResult? applicativeEvidence = null;
            var hasFunctorEvidence = functorTrait.IsValid &&
                                     TryProbeDoTrait(
                                         draft.TypeConstructor,
                                         functorTrait,
                                         "Functor",
                                         doExpression.Span,
                                         out functorEvidence);
            var hasApplicativeTraitEvidence = applicativeTrait.IsValid &&
                                              TryProbeDoTrait(
                                                  draft.TypeConstructor,
                                                  applicativeTrait,
                                                  "Applicative",
                                                  doExpression.Span,
                                                  out applicativeEvidence);
            var hasApplicativeEvidence = hasFunctorEvidence && hasApplicativeTraitEvidence;
            var dependencyEdges = new List<DoDependencyEdge>();
            var segments = BuildDoElaborationSegments(doExpression, hasApplicativeEvidence, dependencyEdges);
            var refutableBindingIndices = doExpression.Bindings
                .Select((binding, index) => (binding, index))
                .Where(item => item.binding.Kind == DoBindingKind.Bind &&
                               item.binding.Pattern != null &&
                               !IsIrrefutableDoPattern(item.binding.Pattern))
                .Select(static item => item.index)
                .ToHashSet();
            var constructorIdentity = ImplLookupCanonicalizer.BuildTypeRefKey(
                _symbolTable,
                draft.TypeConstructor,
                type => _substitution.Apply(type)).ToString();
            var evidence = new List<DoElaborationEvidence>
            {
                CreateDoElaborationEvidence("Monad", draft.MonadTrait, monadEvidence)
            };
            if (hasFunctorEvidence)
            {
                evidence.Add(CreateDoElaborationEvidence("Functor", functorTrait, functorEvidence!));
            }
            if (hasApplicativeTraitEvidence)
            {
                evidence.Add(CreateDoElaborationEvidence("Applicative", applicativeTrait, applicativeEvidence!));
            }
            if (draft.HasRefutablePattern &&
                TryGetProvenDoTraitEvidence(
                    _constraintSolver,
                    draft.AlternativeTrait,
                    "Alternative",
                    doExpression.Span,
                    out var alternativeEvidence))
            {
                evidence.Add(CreateDoElaborationEvidence(
                    "Alternative",
                    draft.AlternativeTrait,
                    alternativeEvidence));
            }

            var steps = doExpression.Bindings
                .Select((binding, index) => new DoElaborationStep(
                    index,
                    binding.Kind,
                    binding.Value?.InferredType is Type input
                        ? _substitution.Apply(input).ToString() ?? input.GetType().Name
                        : "Unit",
                    binding.Pattern?.InferredType is Type output
                        ? _substitution.Apply(output).ToString() ?? output.GetType().Name
                        : binding.Value?.InferredType is Type valueType
                            ? _substitution.Apply(valueType).ToString() ?? valueType.GetType().Name
                            : "Unit",
                    binding.Value?.InferredEffects?.ToString() ?? EffectRow.Pure.ToString()))
                .ToArray();
            doExpression.ElaborationPlan = new DoElaborationPlan(
                constructorIdentity,
                _substitution.Apply(draft.TypeConstructor),
                draft.MonadTrait,
                functorTrait,
                applicativeTrait,
                draft.AlternativeTrait,
                draft.ElementTypeArgumentIndex,
                evidence,
                steps,
                dependencyEdges,
                segments,
                refutableBindingIndices,
                draft.HasRefutablePattern,
                hasApplicativeEvidence,
                DoElaborationPlanFingerprint.Create(doExpression));

            IncrementProfilingCounter("Types.doElaboration.plans");
            IncrementProfilingCounter(
                "Types.doElaboration.applicativeSegments",
                segments.Count(static segment => segment.Strategy == DoElaborationStrategy.ApplicativeThenJoin));
            IncrementProfilingCounter(
                "Types.doElaboration.monadSegments",
                segments.Count(static segment => segment.Strategy == DoElaborationStrategy.Monad));
        }
    }

    private static bool TryGetProvenDoTraitEvidence(
        ConstraintSolver solver,
        SymbolId trait,
        string traitName,
        SourceSpan span,
        out ObligationResult evidence)
    {
        evidence = solver.ObligationResults.LastOrDefault(result =>
            result.State == ObligationState.Proven &&
            result.Goal is ImplementsGoal goal &&
            goal.Trait == trait &&
            string.Equals(goal.TraitName, traitName, StringComparison.Ordinal) &&
            goal.Span.Equals(span))!;
        return evidence != null;
    }

    private bool TryProbeDoTrait(
        Type typeConstructor,
        SymbolId trait,
        string traitName,
        SourceSpan span,
        out ObligationResult? evidence)
    {
        var constraints = new ConstraintSet();
        constraints.AddTrait(typeConstructor, trait, traitName, span, [typeConstructor]);
        var probe = new ConstraintSolver(_symbolTable, _substitution, _typeConstructorKindsBySymbol);
        if (probe.Solve(constraints) &&
            probe.ObligationResults.All(static result => result.State == ObligationState.Proven) &&
            TryGetProvenDoTraitEvidence(probe, trait, traitName, span, out var proven))
        {
            evidence = proven;
            return true;
        }

        evidence = null;
        return false;
    }

    private static DoElaborationEvidence CreateDoElaborationEvidence(
        string traitName,
        SymbolId trait,
        ObligationResult result)
    {
        var traitEvidence = result.Evidence as TraitObligationEvidence;
        return new DoElaborationEvidence(
            traitName,
            trait,
            result.CanonicalKey,
            traitEvidence?.InstanceIdentity ?? string.Empty,
            traitEvidence?.IsBuiltin == true,
            traitEvidence?.IsSupertrait == true);
    }

    private IReadOnlyList<DoElaborationSegment> BuildDoElaborationSegments(
        DoExpr doExpression,
        bool hasApplicativeEvidence,
        List<DoDependencyEdge> dependencyEdges)
    {
        var segments = new List<DoElaborationSegment>();
        var segmentStart = -1;
        var segmentCount = 0;
        var boundSymbols = new HashSet<SymbolId>();
        var bindingOwners = new Dictionary<SymbolId, int>();

        for (var index = 0; index < doExpression.Bindings.Count; index++)
        {
            var binding = doExpression.Bindings[index];
            if (binding.Kind != DoBindingKind.Bind || binding.Pattern == null || binding.Value == null)
            {
                FlushDoSegment();
                continue;
            }

            var referencedSymbols = CollectDoValueReferences(binding.Value);
            foreach (var referencedSymbol in referencedSymbols)
            {
                if (bindingOwners.TryGetValue(referencedSymbol, out var producerIndex))
                {
                    dependencyEdges.Add(new DoDependencyEdge(producerIndex, index, referencedSymbol));
                }
            }
            if (segmentCount > 0 && referencedSymbols.Overlaps(boundSymbols))
            {
                FlushDoSegment();
            }

            if (segmentStart < 0)
            {
                segmentStart = index;
            }

            segmentCount++;
            CollectDoPatternBindings(binding.Pattern, boundSymbols);
            foreach (var symbol in boundSymbols)
            {
                bindingOwners.TryAdd(symbol, index);
            }
        }

        FlushDoSegment();
        return segments;

        void FlushDoSegment()
        {
            if (segmentCount == 0)
            {
                segmentStart = -1;
                boundSymbols.Clear();
                return;
            }

            var bindings = doExpression.Bindings.Skip(segmentStart).Take(segmentCount).ToArray();
            var allPure = bindings.All(static binding => binding.Value?.InferredEffects?.IsPure != false);
            var allCopy = bindings.All(binding => binding.Pattern != null && IsDoApplicativeSafePattern(binding.Pattern));
            var allIrrefutable = bindings.All(binding => binding.Pattern != null && IsIrrefutableDoPattern(binding.Pattern));
            var useApplicative = segmentCount >= 2 &&
                                 hasApplicativeEvidence &&
                                 allPure &&
                                 allCopy &&
                                 allIrrefutable;
            var reason = useApplicative
                ? "proven-independent-pure-copy-bindings"
                : GetDoMonadFallbackReason(segmentCount, hasApplicativeEvidence, allPure, allCopy, allIrrefutable);
            segments.Add(new DoElaborationSegment(
                segmentStart,
                segmentCount,
                useApplicative ? DoElaborationStrategy.ApplicativeThenJoin : DoElaborationStrategy.Monad,
                reason));
            segmentStart = -1;
            segmentCount = 0;
            boundSymbols.Clear();
        }
    }

    private static string GetDoMonadFallbackReason(
        int count,
        bool hasApplicativeEvidence,
        bool allPure,
        bool allCopy,
        bool allIrrefutable)
    {
        if (count < 2)
            return "single-binding";
        if (!hasApplicativeEvidence)
            return "missing-applicative-evidence";
        if (!allPure)
            return "effect-order-not-proven";
        if (!allCopy)
            return "ownership-reuse-not-proven";
        if (!allIrrefutable)
            return "refutable-pattern";
        return "data-dependency";
    }

    private static HashSet<SymbolId> CollectDoValueReferences(EidosAstNode root)
    {
        var references = new HashSet<SymbolId>();
        Traverse(root, node =>
        {
            if (node is IdentifierExpr or PathExpr && node.SymbolId.IsValid)
            {
                references.Add(node.SymbolId);
            }
        });
        return references;
    }

    private static void CollectDoPatternBindings(Pattern root, HashSet<SymbolId> bindings)
    {
        Traverse(root, node =>
        {
            if (node is VarPattern or AsPattern && node.SymbolId.IsValid)
            {
                bindings.Add(node.SymbolId);
            }
        });
    }

    private static void Traverse(EidosAstNode root, Action<EidosAstNode> visit)
    {
        var stack = new Stack<EidosAstNode>();
        var visited = new HashSet<EidosAstNode>(ReferenceEqualityComparer.Instance);
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            visit(current);
            foreach (var child in AstStableNodeTraversal.GetStructuralChildren(current))
            {
                stack.Push(child);
            }
        }
    }

    private bool IsDoApplicativeSafePattern(Pattern pattern)
    {
        return pattern.InferredType is Type type && IsDoApplicativeSafeType(_substitution.Apply(type));
    }

    private bool IsDoApplicativeSafeType(Type type) => type switch
    {
        TyCon constructor => BuiltinTraits.HasTrait(constructor, BuiltinTraits.TraitNames.Copy),
        TyTuple tuple => tuple.Elements.All(IsDoApplicativeSafeType),
        TyRef => true,
        _ => false
    };
}
