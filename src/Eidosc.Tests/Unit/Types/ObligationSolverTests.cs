using Eidosc.Semantic;
using Eidosc.Symbols;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

public sealed class ObligationSolverTests
{
    private static readonly SourceSpan TestSpan = new(new SourceLocation(0, 0, 0), 0);

    [Fact]
    public void Solve_DuplicateClosedGoals_UsesCanonicalTableEntry()
    {
        var solver = new ConstraintSolver(new SymbolTable(), new Substitution());
        var constraints = new ConstraintSet();
        constraints.AddTrait(BaseTypes.Int, SymbolId.None, BuiltinTraits.TraitNames.Eq, TestSpan);
        constraints.AddTrait(BaseTypes.Int, SymbolId.None, BuiltinTraits.TraitNames.Eq, TestSpan);

        var success = solver.Solve(constraints);

        Assert.True(success);
        var result = Assert.Single(solver.ObligationResults);
        Assert.Equal(ObligationState.Proven, result.State);
        var evidence = Assert.IsType<TraitObligationEvidence>(result.Evidence);
        Assert.True(evidence.IsBuiltin);
        var counters = solver.GetProfilingCounters();
        Assert.Equal(2, counters["Types.obligations.rootGoals"]);
        Assert.Equal(1, counters["Types.obligations.tableEntries"]);
        Assert.Equal(1, counters["Types.obligations.tableHits"]);
    }

    [Fact]
    public void Solve_ClosedGoal_PreservesCanonicalKeyShape()
    {
        var solver = new ConstraintSolver(new SymbolTable(), new Substitution());
        var constraints = new ConstraintSet();
        constraints.Add(new EqualityConstraint
        {
            Left = BaseTypes.Int,
            Right = BaseTypes.Bool,
            Span = TestSpan
        });

        Assert.False(solver.Solve(constraints));

        var result = Assert.Single(solver.ObligationResults);
        Assert.Equal(
            "equal(con(Int;t=[];v=[];e=[]),con(Bool;t=[];v=[];e=[]));ctx=t[],c[],e[]",
            result.CanonicalKey);
    }

    [Fact]
    public void Solve_DistinctOpenVariables_DoNotShareAnswers()
    {
        var solver = new ConstraintSolver(new SymbolTable(), new Substitution());
        var constraints = new ConstraintSet();
        constraints.AddTrait(
            new TyVar { Index = 7 },
            SymbolId.None,
            BuiltinTraits.TraitNames.Eq,
            TestSpan);
        constraints.AddTrait(
            new TyVar { Index = 8 },
            SymbolId.None,
            BuiltinTraits.TraitNames.Eq,
            TestSpan);

        var success = solver.Solve(constraints);

        Assert.True(success);
        Assert.Equal(2, solver.ObligationResults.Count);
        Assert.All(solver.ObligationResults, result => Assert.Equal(ObligationState.Ambiguous, result.State));
    }

    [Fact]
    public void Solve_DeferredTraitGoal_BecomesProvenAfterEqualityBindsVariable()
    {
        var substitution = new Substitution();
        var solver = new ConstraintSolver(new SymbolTable(), substitution);
        var constraints = new ConstraintSet();
        var variable = new TyVar { Index = 7 };
        constraints.AddTrait(variable, SymbolId.None, BuiltinTraits.TraitNames.Eq, TestSpan);
        constraints.Add(new EqualityConstraint
        {
            Left = variable,
            Right = BaseTypes.Int,
            Span = TestSpan
        });

        var success = solver.Solve(constraints);

        Assert.True(success);
        var traitResult = Assert.Single(solver.ObligationResults, result => result.Goal is ImplementsGoal);
        Assert.Equal(ObligationState.Proven, traitResult.State);
        var evidence = Assert.IsType<TraitObligationEvidence>(traitResult.Evidence);
        Assert.True(evidence.IsBuiltin);
        Assert.Equal(BaseTypes.IntId, evidence.Type.Id.Value);
    }

    [Fact]
    public void Solve_UserInstance_RecordsSelectedInstanceEvidence()
    {
        var symbols = new SymbolTable();
        var traitId = symbols.DeclareTrait("Marker", TestSpan);
        var boxId = symbols.DeclareAdt("Box", TestSpan);
        var box = Assert.IsType<AdtSymbol>(symbols.GetSymbol(boxId));
        var instanceId = symbols.DeclareImpl(
            traitId,
            box.TypeId,
            TestSpan,
            implementingTypeDisplay: "Box",
            canonicalImplementingType: "Box");
        var boxType = new TyCon
        {
            Name = "Box",
            Symbol = boxId,
            Id = box.TypeId
        };
        var solver = new ConstraintSolver(symbols, new Substitution());
        var constraints = new ConstraintSet();
        constraints.AddTrait(boxType, traitId, "Marker", TestSpan);

        var success = solver.Solve(constraints);

        Assert.True(success);
        var result = Assert.Single(solver.ObligationResults);
        var evidence = Assert.IsType<TraitObligationEvidence>(result.Evidence);
        Assert.False(evidence.IsBuiltin);
        Assert.Equal(instanceId, evidence.Instance);
    }

    [Fact]
    public void Solve_PreludeInstance_ReusesResolutionForFingerprintAndEvidence()
    {
        var solver = new ConstraintSolver(new SymbolTable(), new Substitution());
        var constraints = new ConstraintSet();
        constraints.AddTrait(
            new TyCon { Name = "Option" },
            SymbolId.None,
            "Monad",
            TestSpan);

        Assert.True(solver.Solve(constraints));

        var result = Assert.Single(solver.ObligationResults);
        var evidence = Assert.IsType<TraitObligationEvidence>(result.Evidence);
        Assert.Equal("__eidos_prelude_core.Option.MonadOption", evidence.InstanceIdentity);
        var counters = solver.GetProfilingCounters();
        Assert.Equal(1, counters["Types.preludeInstanceResolution.entries"]);
        Assert.Equal(1, counters["Types.preludeInstanceResolution.cacheMisses"]);
        Assert.Equal(2, counters["Types.preludeInstanceResolution.cacheHits"]);
        Assert.Equal(1, counters["Types.preludeInstanceResolution.candidateChecks"]);
    }

    [Fact]
    public void Solve_ConcreteEffectSubset_ProducesInclusionEvidence()
    {
        var solver = new ConstraintSolver(new SymbolTable(), new Substitution());
        var constraints = new ConstraintSet();
        constraints.AddGoal(new EffectSubsetGoal
        {
            Required = new EffectRow([new EffectTag(SymbolId.None, "io")]),
            Allowed = new EffectRow([
                new EffectTag(SymbolId.None, "io"),
                new EffectTag(SymbolId.None, "ffi")]),
            Span = TestSpan
        });

        Assert.True(solver.Solve(constraints));

        var result = Assert.Single(solver.ObligationResults);
        Assert.Equal(ObligationState.Proven, result.State);
        Assert.IsType<EffectInclusionObligationEvidence>(result.Evidence);
    }

    [Fact]
    public void Solve_ConcreteEffectSubsetFailure_ExplainsMissingEffect()
    {
        var solver = new ConstraintSolver(new SymbolTable(), new Substitution());
        var constraints = new ConstraintSet();
        constraints.AddGoal(new EffectSubsetGoal
        {
            Required = new EffectRow([new EffectTag(SymbolId.None, "io")]),
            Allowed = EffectRow.Pure,
            Span = TestSpan
        });

        Assert.False(solver.Solve(constraints));

        var result = Assert.Single(solver.ObligationResults);
        Assert.Equal(ObligationState.Failed, result.State);
        Assert.Contains("not a subset", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Solve_OpenAllowedEffectRow_BindsOnlyRequiredRemainder()
    {
        var substitution = new Substitution();
        var solver = new ConstraintSolver(new SymbolTable(), substitution);
        var open = new EffectVariable { Id = 17 };
        var constraints = new ConstraintSet();
        constraints.AddGoal(new EffectSubsetGoal
        {
            Required = new EffectRow([new EffectTag(SymbolId.None, "io")]),
            Allowed = EffectRow.FromEffectVariable(open),
            Span = TestSpan
        });

        Assert.True(solver.Solve(constraints));

        var applied = substitution.ApplyEffectSubstitution(EffectRow.FromEffectVariable(open));
        Assert.True(applied.ContainsName("io"));
        Assert.Empty(applied.Variables);
    }

    [Fact]
    public void Solve_AllGoal_ComposesProjectionAndEqualityEvidence()
    {
        var solver = new ConstraintSolver(new SymbolTable(), new Substitution());
        var constraints = new ConstraintSet();
        constraints.AddGoal(new AllGoal
        {
            Span = TestSpan,
            Goals =
            [
                new NormalizeProjectionGoal
                {
                    ProjectionIdentity = "Iterator[Int].Item",
                    NormalizedType = BaseTypes.Int,
                    ExpectedType = BaseTypes.Int,
                    Instance = new SymbolId(41),
                    Member = new SymbolId(42),
                    Span = TestSpan
                },
                new EqualGoal
                {
                    Left = BaseTypes.Bool,
                    Right = BaseTypes.Bool,
                    Span = TestSpan
                }
            ]
        });

        Assert.True(solver.Solve(constraints));

        var all = Assert.Single(solver.ObligationResults, result => result.Goal is AllGoal);
        var evidence = Assert.IsType<AllObligationEvidence>(all.Evidence);
        Assert.Collection(
            evidence.Children,
            child => Assert.IsType<ProjectionObligationEvidence>(child),
            child => Assert.IsType<EqualityObligationEvidence>(child));
    }

    [Fact]
    public void Solve_CyclicGoal_RemainsAmbiguousAndRecordsCycleEvidence()
    {
        var children = new List<ObligationGoal>();
        var cycle = new AllGoal { Span = TestSpan, Goals = children };
        children.Add(cycle);
        var constraints = new ConstraintSet();
        constraints.AddGoal(cycle);
        var solver = new ConstraintSolver(new SymbolTable(), new Substitution());

        Assert.True(solver.Solve(constraints));

        var result = Assert.Single(solver.ObligationResults);
        Assert.Equal(ObligationState.Ambiguous, result.State);
        var evidence = Assert.IsType<DeferredObligationEvidence>(result.Evidence);
        Assert.Contains("cycle", evidence.Blocker, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cycle-ref(0)", result.CanonicalKey, StringComparison.Ordinal);
    }

    [Fact]
    public void Solve_DeeplyNestedGoal_StopsAtDepthBudget()
    {
        ObligationGoal nested = new EqualGoal
        {
            Left = BaseTypes.Int,
            Right = BaseTypes.Int,
            Span = TestSpan
        };
        for (var depth = 0; depth < 258; depth++)
        {
            nested = new AllGoal { Span = TestSpan, Goals = [nested] };
        }

        var constraints = new ConstraintSet();
        constraints.AddGoal(nested);
        var solver = new ConstraintSolver(new SymbolTable(), new Substitution());

        Assert.False(solver.Solve(constraints));
        Assert.Contains(
            solver.ObligationResults,
            result => result.State == ObligationState.Overflow &&
                      result.Explanation?.Contains("depth budget", StringComparison.Ordinal) == true);
    }
}
