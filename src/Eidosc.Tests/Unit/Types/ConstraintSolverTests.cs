using Eidosc.Symbols;
using System;
using Eidosc;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

public class ConstraintSolverTests
{
    private static readonly SourceSpan TestSpan = new(new SourceLocation(0, 0, 0), 0);

    [Fact]
    public void Solve_TupleTraitConstraint_AllElementsSatisfyTrait_Succeeds()
    {
        var symbolTable = new SymbolTable();
        var solver = new ConstraintSolver(symbolTable, new Substitution());
        var constraints = new ConstraintSet();

        constraints.AddTrait(
            new TyTuple { Elements = [BaseTypes.Int, BaseTypes.Bool] },
            SymbolId.None,
            BuiltinTraits.TraitNames.Eq,
            TestSpan);

        var success = solver.Solve(constraints);

        Assert.True(success);
        Assert.Empty(solver.Diagnostics);
    }

    [Fact]
    public void Solve_TupleTraitConstraint_ElementViolatesTrait_FailsWithElementIndex()
    {
        var symbolTable = new SymbolTable();
        var solver = new ConstraintSolver(symbolTable, new Substitution());
        var constraints = new ConstraintSet();

        var nonEqElement = new TyFun
        {
            Params = [BaseTypes.Int],
            Result = BaseTypes.Int
        };

        constraints.AddTrait(
            new TyTuple { Elements = [BaseTypes.Int, nonEqElement] },
            SymbolId.None,
            BuiltinTraits.TraitNames.Eq,
            TestSpan);

        var success = solver.Solve(constraints);

        Assert.False(success);
        var diagnostic = Assert.Single(solver.Diagnostics);
        Assert.Contains("Tuple element #2", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Solve_DeferredTraitConstraintFailure_ReturnsFalse()
    {
        var symbolTable = new SymbolTable();
        var substitution = new Substitution();
        var solver = new ConstraintSolver(symbolTable, substitution);
        var constraints = new ConstraintSet();
        var typeVar = new TyVar { Index = 7 };
        var functionType = new TyFun
        {
            Params = [BaseTypes.Int],
            Result = BaseTypes.Int
        };

        constraints.AddTrait(
            typeVar,
            SymbolId.None,
            BuiltinTraits.TraitNames.Eq,
            TestSpan);
        constraints.Add(new EqualityConstraint
        {
            Left = typeVar,
            Right = functionType,
            Span = TestSpan
        });

        var success = solver.Solve(constraints);

        Assert.False(success);
        Assert.Contains(
            solver.Diagnostics,
            diagnostic => diagnostic.Message.Contains(BuiltinTraits.TraitNames.Eq, StringComparison.Ordinal));
    }

    [Fact]
    public void Solve_TraitConstraint_InvalidTraitIdButNameResolvable_UsesResolvedTraitImpl()
    {
        var symbolTable = new SymbolTable();

        var eqTraitId = symbolTable.DeclareTrait(BuiltinTraits.TraitNames.Eq, TestSpan);
        var boxSymbolId = symbolTable.DeclareAdt("Box", TestSpan);
        var boxSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(boxSymbolId));
        symbolTable.DeclareImpl(eqTraitId, boxSymbol.TypeId, TestSpan);

        var boxType = new TyCon
        {
            Name = "Box",
            Symbol = boxSymbolId,
            Id = boxSymbol.TypeId
        };

        var solver = new ConstraintSolver(symbolTable, new Substitution());
        var constraints = new ConstraintSet();
        constraints.AddTrait(boxType, SymbolId.None, BuiltinTraits.TraitNames.Eq, TestSpan);

        var success = solver.Solve(constraints);

        Assert.True(success);
        Assert.Empty(solver.Diagnostics);
    }

    [Fact]
    public void Solve_KindConstraints_OnSameTypeVariable_ConflictingKinds_Fails()
    {
        var symbolTable = new SymbolTable();
        var solver = new ConstraintSolver(symbolTable, new Substitution());
        var constraints = new ConstraintSet();
        var typeVar = new TyVar { Index = 7 };

        constraints.Add(new KindConstraint
        {
            Type = typeVar,
            ExpectedKind = "kind2",
            Span = TestSpan
        });

        constraints.Add(new KindConstraint
        {
            Type = typeVar,
            ExpectedKind = "kind1",
            Span = TestSpan
        });

        var success = solver.Solve(constraints);

        Assert.False(success);
        Assert.Contains(
            solver.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Kind mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Solve_TraitConstraint_WithTraitArgs_MatchingImpl_Succeeds()
    {
        var symbolTable = new SymbolTable();

        var traitTypeParam = symbolTable.DeclareTypeParameter("F", TestSpan, "kind2");
        var functorTraitId = symbolTable.DeclareTrait("Functor", TestSpan, [traitTypeParam]);

        var boxTypeParam = symbolTable.DeclareTypeParameter("A", TestSpan);
        var boxId = symbolTable.DeclareAdt("Box", TestSpan, [boxTypeParam]);
        var boxSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(boxId));

        var personId = symbolTable.DeclareAdt("Person", TestSpan);
        var personSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(personId));

        symbolTable.DeclareImpl(functorTraitId, personSymbol.TypeId, TestSpan, ["Box"]);

        var solver = new ConstraintSolver(symbolTable, new Substitution());
        var constraints = new ConstraintSet();
        constraints.AddTrait(
            new TyCon { Name = "Person", Symbol = personId, Id = personSymbol.TypeId },
            functorTraitId,
            "Functor",
            TestSpan,
            [new TyCon { Name = "Box", Symbol = boxId, Id = boxSymbol.TypeId }]);

        var success = solver.Solve(constraints);

        Assert.True(success);
        Assert.Empty(solver.Diagnostics);
    }

    [Fact]
    public void Solve_TraitConstraint_UserImplOnUnit_UsesCanonicalUnitHead()
    {
        var symbolTable = new SymbolTable();
        var markerTraitId = symbolTable.DeclareTrait("Marker", TestSpan);

        symbolTable.DeclareImpl(
            markerTraitId,
            new TypeId(BaseTypes.UnitId),
            TestSpan,
            implementingTypeDisplay: "Unit",
            canonicalImplementingType: "Unit");

        var solver = new ConstraintSolver(symbolTable, new Substitution());
        var constraints = new ConstraintSet();
        constraints.AddTrait(BaseTypes.Unit, markerTraitId, "Marker", TestSpan);

        var success = solver.Solve(constraints);

        Assert.True(success);
        Assert.Empty(solver.Diagnostics);
    }

    [Fact]
    public void Solve_TraitConstraint_WithTraitArgs_DifferentImplArgs_Fails()
    {
        var symbolTable = new SymbolTable();

        var traitTypeParam = symbolTable.DeclareTypeParameter("F", TestSpan, "kind2");
        var functorTraitId = symbolTable.DeclareTrait("Functor", TestSpan, [traitTypeParam]);

        var boxTypeParam = symbolTable.DeclareTypeParameter("A", TestSpan);
        var boxId = symbolTable.DeclareAdt("Box", TestSpan, [boxTypeParam]);
        Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(boxId));

        var bagTypeParam = symbolTable.DeclareTypeParameter("B", TestSpan);
        var bagId = symbolTable.DeclareAdt("Bag", TestSpan, [bagTypeParam]);
        var bagSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(bagId));

        var personId = symbolTable.DeclareAdt("Person", TestSpan);
        var personSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(personId));

        symbolTable.DeclareImpl(functorTraitId, personSymbol.TypeId, TestSpan, ["Box"]);

        var solver = new ConstraintSolver(symbolTable, new Substitution());
        var constraints = new ConstraintSet();
        constraints.AddTrait(
            new TyCon { Name = "Person", Symbol = personId, Id = personSymbol.TypeId },
            functorTraitId,
            "Functor",
            TestSpan,
            [new TyCon { Name = "Bag", Symbol = bagId, Id = bagSymbol.TypeId }]);

        var success = solver.Solve(constraints);

        Assert.False(success);
        var diagnostic = Assert.Single(solver.Diagnostics);
        Assert.Contains("does not implement trait 'Functor'", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Solve_TraitConstraint_WithTraitArgs_MultipleImpls_SelectsMatchingImpl()
    {
        var symbolTable = new SymbolTable();

        var traitTypeParam = symbolTable.DeclareTypeParameter("F", TestSpan, "kind2");
        var functorTraitId = symbolTable.DeclareTrait("Functor", TestSpan, [traitTypeParam]);

        var boxTypeParam = symbolTable.DeclareTypeParameter("A", TestSpan);
        var boxId = symbolTable.DeclareAdt("Box", TestSpan, [boxTypeParam]);
        var boxSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(boxId));

        var bagTypeParam = symbolTable.DeclareTypeParameter("B", TestSpan);
        var bagId = symbolTable.DeclareAdt("Bag", TestSpan, [bagTypeParam]);
        var bagSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(bagId));

        var personId = symbolTable.DeclareAdt("Person", TestSpan);
        var personSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(personId));

        symbolTable.DeclareImpl(functorTraitId, personSymbol.TypeId, TestSpan, ["Box"]);
        symbolTable.DeclareImpl(functorTraitId, personSymbol.TypeId, TestSpan, ["Bag"]);

        var solver = new ConstraintSolver(symbolTable, new Substitution());
        var constraints = new ConstraintSet();
        constraints.AddTrait(
            new TyCon { Name = "Person", Symbol = personId, Id = personSymbol.TypeId },
            functorTraitId,
            "Functor",
            TestSpan,
            [new TyCon { Name = "Box", Symbol = boxId, Id = boxSymbol.TypeId }]);
        constraints.AddTrait(
            new TyCon { Name = "Person", Symbol = personId, Id = personSymbol.TypeId },
            functorTraitId,
            "Functor",
            TestSpan,
            [new TyCon { Name = "Bag", Symbol = bagId, Id = bagSymbol.TypeId }]);

        var success = solver.Solve(constraints);

        Assert.True(success);
        Assert.Empty(solver.Diagnostics);
    }

    [Fact]
    public void Solve_TraitConstraint_WithSameTraitArgTextButDifferentTypeIdentity_RequiresMatchingIdentity()
    {
        var symbolTable = new SymbolTable();

        var traitTypeParam = symbolTable.DeclareTypeParameter("F", TestSpan, "kind2");
        var functorTraitId = symbolTable.DeclareTrait("Functor", TestSpan, [traitTypeParam]);

        var containerId = symbolTable.DeclareAdt("Container", TestSpan);
        var containerSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(containerId));
        var firstBoxTypeParam = symbolTable.DeclareTypeParameter("A", TestSpan);
        var firstBoxId = symbolTable.DeclareAdt("Box", TestSpan, [firstBoxTypeParam]);
        var secondBoxTypeParam = symbolTable.DeclareTypeParameter("B", TestSpan);
        var secondBoxId = symbolTable.DeclareAdt("Box", TestSpan, [secondBoxTypeParam]);
        var firstBoxSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(firstBoxId));
        var secondBoxSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(secondBoxId));

        symbolTable.DeclareImpl(
            functorTraitId,
            containerSymbol.TypeId,
            TestSpan,
            traitTypeArgs: ["Box"],
            traitTypeArgKeys:
            [
                new ImplTypeRefKey(firstBoxId, firstBoxSymbol.TypeId, "Box", [])
            ]);

        var matchingSolver = new ConstraintSolver(symbolTable, new Substitution());
        var matchingConstraints = new ConstraintSet();
        matchingConstraints.AddTrait(
            new TyCon { Name = "Container", Symbol = containerId, Id = containerSymbol.TypeId },
            functorTraitId,
            "Functor",
            TestSpan,
            [new TyCon { Name = "Box", Symbol = firstBoxId, Id = firstBoxSymbol.TypeId }]);

        var matchingSuccess = matchingSolver.Solve(matchingConstraints);

        Assert.True(matchingSuccess);
        Assert.Empty(matchingSolver.Diagnostics);

        var mismatchedSolver = new ConstraintSolver(symbolTable, new Substitution());
        var mismatchedConstraints = new ConstraintSet();
        mismatchedConstraints.AddTrait(
            new TyCon { Name = "Container", Symbol = containerId, Id = containerSymbol.TypeId },
            functorTraitId,
            "Functor",
            TestSpan,
            [new TyCon { Name = "Box", Symbol = secondBoxId, Id = secondBoxSymbol.TypeId }]);

        var mismatchedSuccess = mismatchedSolver.Solve(mismatchedConstraints);

        Assert.False(mismatchedSuccess);
        var diagnostic = Assert.Single(mismatchedSolver.Diagnostics);
        Assert.Contains("does not implement trait 'Functor'", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Solve_TraitConstraint_WithConditionalImplRequirementTraitArgIdentity_RequiresMatchingIdentity()
    {
        var symbolTable = new SymbolTable();

        var traitTypeParam = symbolTable.DeclareTypeParameter("F", TestSpan, "kind2");
        var functorTraitId = symbolTable.DeclareTrait("Functor", TestSpan, [traitTypeParam]);
        var eqTraitId = symbolTable.DeclareTrait("Eq", TestSpan);

        var optionTypeParam = symbolTable.DeclareTypeParameter("T", TestSpan);
        var optionId = symbolTable.DeclareAdt("Option", TestSpan, [optionTypeParam]);
        var optionSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(optionId));
        var matchingHolderId = symbolTable.DeclareAdt("MatchingHolder", TestSpan);
        var matchingHolderSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(matchingHolderId));
        var mismatchedHolderId = symbolTable.DeclareAdt("MismatchedHolder", TestSpan);
        var mismatchedHolderSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(mismatchedHolderId));

        var firstBoxTypeParam = symbolTable.DeclareTypeParameter("A", TestSpan);
        var firstBoxId = symbolTable.DeclareAdt("Box", TestSpan, [firstBoxTypeParam]);
        var secondBoxTypeParam = symbolTable.DeclareTypeParameter("B", TestSpan);
        var secondBoxId = symbolTable.DeclareAdt("Box", TestSpan, [secondBoxTypeParam]);
        var firstBoxSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(firstBoxId));
        var secondBoxSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(secondBoxId));

        var requiredBoxKey = new ImplTypeRefKey(firstBoxId, firstBoxSymbol.TypeId, "Box", []);
        symbolTable.DeclareImpl(
            eqTraitId,
            optionSymbol.TypeId,
            TestSpan,
            implementingTypeRequirements:
            [
                new ImplTypeArgTraitRequirement
                {
                    TypeArgIndex = 0,
                    Trait = functorTraitId,
                    TraitName = "Functor",
                    TraitTypeArgs = ["Box"],
                    TraitTypeArgKeys = [requiredBoxKey]
                }
            ]);

        symbolTable.DeclareImpl(
            functorTraitId,
            matchingHolderSymbol.TypeId,
            TestSpan,
            traitTypeArgs: ["Box"],
            traitTypeArgKeys: [requiredBoxKey]);
        symbolTable.DeclareImpl(
            functorTraitId,
            mismatchedHolderSymbol.TypeId,
            TestSpan,
            traitTypeArgs: ["Box"],
            traitTypeArgKeys:
            [
                new ImplTypeRefKey(secondBoxId, secondBoxSymbol.TypeId, "Box", [])
            ]);

        var matchingSolver = new ConstraintSolver(symbolTable, new Substitution());
        var matchingConstraints = new ConstraintSet();
        matchingConstraints.AddTrait(
            new TyCon
            {
                Name = "Option",
                Symbol = optionId,
                Id = optionSymbol.TypeId,
                Args =
                [
                    new TyCon
                    {
                        Name = "MatchingHolder",
                        Symbol = matchingHolderId,
                        Id = matchingHolderSymbol.TypeId
                    }
                ]
            },
            eqTraitId,
            "Eq",
            TestSpan);

        var matchingSuccess = matchingSolver.Solve(matchingConstraints);

        Assert.True(matchingSuccess);
        Assert.Empty(matchingSolver.Diagnostics);

        var mismatchedSolver = new ConstraintSolver(symbolTable, new Substitution());
        var mismatchedConstraints = new ConstraintSet();
        mismatchedConstraints.AddTrait(
            new TyCon
            {
                Name = "Option",
                Symbol = optionId,
                Id = optionSymbol.TypeId,
                Args =
                [
                    new TyCon
                    {
                        Name = "MismatchedHolder",
                        Symbol = mismatchedHolderId,
                        Id = mismatchedHolderSymbol.TypeId
                    }
                ]
            },
            eqTraitId,
            "Eq",
            TestSpan);

        var mismatchedSuccess = mismatchedSolver.Solve(mismatchedConstraints);

        Assert.False(mismatchedSuccess);
        var diagnostic = Assert.Single(mismatchedSolver.Diagnostics);
        Assert.Contains("Type argument #1 of 'Option'", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Functor[Box]", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckTrait_WithoutTraitArgs_DoesNotMatchTraitArgSpecificImpl()
    {
        var symbolTable = new SymbolTable();

        var traitTypeParam = symbolTable.DeclareTypeParameter("F", TestSpan, "kind2");
        var functorTraitId = symbolTable.DeclareTrait("Functor", TestSpan, [traitTypeParam]);

        var boxTypeParam = symbolTable.DeclareTypeParameter("A", TestSpan);
        symbolTable.DeclareAdt("Box", TestSpan, [boxTypeParam]);

        var personId = symbolTable.DeclareAdt("Person", TestSpan);
        var personSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(personId));

        symbolTable.DeclareImpl(functorTraitId, personSymbol.TypeId, TestSpan, ["Box"]);

        var solver = new ConstraintSolver(symbolTable, new Substitution());
        var personType = new TyCon
        {
            Name = "Person",
            Symbol = personId,
            Id = personSymbol.TypeId
        };

        var satisfied = solver.CheckTrait(personType, functorTraitId);

        Assert.False(satisfied);
    }

    [Fact]
    public void Solve_TraitConstraint_WithEffectSymbol_SucceedsForEffectPolymorphism()
    {
        var symbolTable = new SymbolTable();
        var abilityId = symbolTable.DeclareEffect("Writer", TestSpan);
        var personId = symbolTable.DeclareAdt("Person", TestSpan);
        var personSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(personId));

        var solver = new ConstraintSolver(symbolTable, new Substitution());
        var constraints = new ConstraintSet();
        constraints.AddTrait(
            new TyCon { Name = "Person", Symbol = personId, Id = personSymbol.TypeId },
            abilityId,
            "Writer",
            TestSpan);

        var success = solver.Solve(constraints);

        // Effects are now allowed as type-parameter constraints for effect polymorphism.
        Assert.True(success);
        Assert.Empty(solver.Diagnostics);
    }

    [Fact]
    public void Solve_TraitConstraint_WithConditionalImplRequirementSatisfied_Succeeds()
    {
        var symbolTable = new SymbolTable();
        var eqTraitId = symbolTable.DeclareTrait("Eq", TestSpan);
        var optionTypeParam = symbolTable.DeclareTypeParameter("T", TestSpan);
        var optionId = symbolTable.DeclareAdt("Option", TestSpan, [optionTypeParam]);
        var optionSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(optionId));

        symbolTable.DeclareImpl(
            eqTraitId,
            optionSymbol.TypeId,
            TestSpan,
            implementingTypeRequirements:
            [
                new ImplTypeArgTraitRequirement
                {
                    TypeArgIndex = 0,
                    Trait = eqTraitId,
                    TraitName = "Eq"
                }
            ]);

        var solver = new ConstraintSolver(symbolTable, new Substitution());
        var constraints = new ConstraintSet();
        constraints.AddTrait(
            new TyCon
            {
                Name = "Option",
                Symbol = optionId,
                Id = optionSymbol.TypeId,
                Args = [BaseTypes.Int]
            },
            eqTraitId,
            "Eq",
            TestSpan);

        var success = solver.Solve(constraints);

        Assert.True(success);
        Assert.Empty(solver.Diagnostics);
    }

    [Fact]
    public void Solve_TraitConstraint_WithConditionalImplRequirementUnsatisfied_Fails()
    {
        var symbolTable = new SymbolTable();
        var eqTraitId = symbolTable.DeclareTrait("Eq", TestSpan);
        var optionTypeParam = symbolTable.DeclareTypeParameter("T", TestSpan);
        var optionId = symbolTable.DeclareAdt("Option", TestSpan, [optionTypeParam]);
        var optionSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(optionId));

        symbolTable.DeclareImpl(
            eqTraitId,
            optionSymbol.TypeId,
            TestSpan,
            implementingTypeRequirements:
            [
                new ImplTypeArgTraitRequirement
                {
                    TypeArgIndex = 0,
                    Trait = eqTraitId,
                    TraitName = "Eq"
                }
            ]);

        var solver = new ConstraintSolver(symbolTable, new Substitution());
        var constraints = new ConstraintSet();
        constraints.AddTrait(
            new TyCon
            {
                Name = "Option",
                Symbol = optionId,
                Id = optionSymbol.TypeId,
                Args =
                [
                    new TyFun
                    {
                        Params = [BaseTypes.Int],
                        Result = BaseTypes.Int
                    }
                ]
            },
            eqTraitId,
            "Eq",
            TestSpan);

        var success = solver.Solve(constraints);

        Assert.False(success);
        var diagnostic = Assert.Single(solver.Diagnostics);
        Assert.Contains("Type argument #1 of 'Option'", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("does not implement trait 'Eq'", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Solve_DeferredTraitConstraint_TriggeredOnUnify_FailsIfUnsatisfied()
    {
        var symbolTable = new SymbolTable();
        var substitution = new Substitution();
        var solver = new ConstraintSolver(symbolTable, substitution);
        var constraints = new ConstraintSet();

        var typeVar = substitution.FreshTypeVariable();

        constraints.AddTrait(
            typeVar,
            SymbolId.None,
            BuiltinTraits.TraitNames.Eq,
            TestSpan);

        var success = solver.Solve(constraints);
        Assert.True(success);

        var nonEqType = new TyFun
        {
            Params = [BaseTypes.Int],
            Result = BaseTypes.Int
        };

        substitution.Unify(typeVar, nonEqType);
        substitution.Apply(typeVar);

        Assert.Single(solver.Diagnostics);
        var diagnostic = solver.Diagnostics[0];
        Assert.Contains("does not implement trait 'Eq'", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Solve_DeferredTraitConstraint_TriggeredOnUnify_SucceedsIfSatisfied()
    {
        var symbolTable = new SymbolTable();
        var substitution = new Substitution();
        var solver = new ConstraintSolver(symbolTable, substitution);
        var constraints = new ConstraintSet();

        var typeVar = substitution.FreshTypeVariable();

        constraints.AddTrait(
            typeVar,
            SymbolId.None,
            BuiltinTraits.TraitNames.Eq,
            TestSpan);

        var success = solver.Solve(constraints);
        Assert.True(success);

        substitution.Unify(typeVar, BaseTypes.Int);
        substitution.Apply(typeVar);

        Assert.Empty(solver.Diagnostics);
    }

    [Fact]
    public void Solve_DeferredTupleElementTraitConstraint_TriggeredOnUnify_FailsIfUnsatisfied()
    {
        var symbolTable = new SymbolTable();
        var substitution = new Substitution();
        var solver = new ConstraintSolver(symbolTable, substitution);
        var constraints = new ConstraintSet();

        var typeVar = substitution.FreshTypeVariable();
        constraints.AddTrait(
            new TyTuple { Elements = [typeVar, BaseTypes.Int] },
            SymbolId.None,
            BuiltinTraits.TraitNames.Eq,
            TestSpan);

        var success = solver.Solve(constraints);
        Assert.True(success);

        var nonEqType = new TyFun
        {
            Params = [BaseTypes.Int],
            Result = BaseTypes.Int
        };

        substitution.Unify(typeVar, nonEqType);
        substitution.Apply(typeVar);

        var diagnostic = Assert.Single(solver.Diagnostics);
        Assert.Contains("does not implement trait 'Eq'", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Solve_DeferredConditionalImplRequirement_TriggeredOnUnify_FailsIfUnsatisfied()
    {
        var symbolTable = new SymbolTable();
        var substitution = new Substitution();
        var eqTraitId = symbolTable.DeclareTrait("Eq", TestSpan);
        var optionTypeParam = symbolTable.DeclareTypeParameter("T", TestSpan);
        var optionId = symbolTable.DeclareAdt("Option", TestSpan, [optionTypeParam]);
        var optionSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(optionId));

        symbolTable.DeclareImpl(
            eqTraitId,
            optionSymbol.TypeId,
            TestSpan,
            implementingTypeRequirements:
            [
                new ImplTypeArgTraitRequirement
                {
                    TypeArgIndex = 0,
                    Trait = eqTraitId,
                    TraitName = "Eq"
                }
            ]);

        var typeVar = substitution.FreshTypeVariable();
        var solver = new ConstraintSolver(symbolTable, substitution);
        var constraints = new ConstraintSet();
        constraints.AddTrait(
            new TyCon
            {
                Name = "Option",
                Symbol = optionId,
                Id = optionSymbol.TypeId,
                Args = [typeVar]
            },
            eqTraitId,
            "Eq",
            TestSpan);

        var success = solver.Solve(constraints);
        Assert.True(success);

        var nonEqType = new TyFun
        {
            Params = [BaseTypes.Int],
            Result = BaseTypes.Int
        };

        substitution.Unify(typeVar, nonEqType);
        substitution.Apply(typeVar);

        var diagnostic = Assert.Single(solver.Diagnostics);
        Assert.Contains("does not implement trait 'Eq'", diagnostic.Message, StringComparison.Ordinal);
    }
}
