using Eidosc.Hir;
using Eidosc.Mir;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Symbols;
using Eidosc.Types;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class ComptimeBindingTests
{
    [Fact]
    public void Namer_NameFirstComptimeBinding_RegistersComptimeSymbol()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime 64;
            main :: Unit -> Int { _ => 0 }
            """,
            CompilationPhase.Namer);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var symbolId = symbolTable.LookupValue("DefaultCapacity");
        Assert.True(symbolId.HasValue);
        var symbol = Assert.IsType<VarSymbol>(symbolTable.GetSymbol(symbolId.Value));
        Assert.True(symbol.IsComptime);
        Assert.False(symbol.IsMutable);
    }

    [Fact]
    public void Namer_NameFirstComptimeFunction_RegistersComptimeSymbol()
    {
        var result = RunNameFirst(
            """
            fieldCount :: comptime Unit -> Int { _ => 1 }
            main :: Unit -> Int { _ => 0 }
            """,
            CompilationPhase.Namer);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var symbolId = symbolTable.LookupValue("fieldCount");
        Assert.True(symbolId.HasValue);
        var symbol = Assert.IsType<FuncSymbol>(symbolTable.GetSymbol(symbolId.Value));
        Assert.True(symbol.IsComptime);
    }

    [Fact]
    public void Types_RuntimeCallToComptimeFunction_ReportsPhaseBoundary()
    {
        var result = RunNameFirst(
            """
            fieldCount :: comptime Unit -> Int { _ => 1 }
            main :: Unit -> Int { _ => fieldCount(()) }
            """,
            CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Cannot use comptime-only function 'fieldCount' from runtime code.", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_ComptimeFunctionWithExplicitEffect_ReportsEffectBoundary()
    {
        var result = RunNameFirst(
            """
            allocate :: comptime Int -> Int need ffi { size => size }
            DefaultCapacity :: comptime allocate(8);
            main :: Unit -> Int { _ => 0 }
            """,
            CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Comptime-only function 'allocate' must be pure", StringComparison.Ordinal) &&
                diagnostic.Message.Contains("comptime boundary", StringComparison.Ordinal));
    }

    [Fact]
    public void Abilities_ComptimeFunctionWithImplicitFfi_ReportsEffectBoundary()
    {
        var result = RunNameFirst(
            """
             malloc :: Int -> RawPtr need ffi extern(c, name: "malloc");
            ;
            allocate :: comptime Int -> RawPtr { size => malloc(size) }
            main :: Unit -> Int { _ => 0 }
            """,
            CompilationPhase.Effects);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Comptime-only function 'allocate' must be pure", StringComparison.Ordinal) &&
                diagnostic.Message.Contains("comptime boundary", StringComparison.Ordinal));
    }

    [Fact]
    public void Namer_ComptimeLocalAssignment_ReportsComptimeAssignment()
    {
        var result = RunNameFirst(
            """
            main :: Unit -> Int {
              _ => {
                comptime size := 32;
                size = 33;
                size
              }
            }
            """,
            CompilationPhase.Namer);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Cannot assign to comptime binding 'size'", StringComparison.Ordinal));
    }

    [Fact]
    public void Hir_ComptimeBinding_PreservesComptimePhase()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime 64;
            main :: Unit -> Int { _ => 0 }
            """,
            CompilationPhase.Hir);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var module = Assert.IsType<HirModule>(result.HirModule);
        var value = Assert.Single(module.Declarations.OfType<HirVal>(), val => val.Name == "DefaultCapacity");
        Assert.True(value.IsComptime);
        Assert.Contains("ComptimeVal DefaultCapacity", HirFormatter.FormatHir(module), StringComparison.Ordinal);
    }

    [Fact]
    public void Types_ModuleComptimeLiteralExpression_Succeeds()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime 40 + 2;
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success);
    }

    [Fact]
    public void Types_LocalComptimeLiteralExpression_Succeeds()
    {
        var result = RunNameFirst(
            """
            main :: Unit -> Int {
              _ => {
                comptime size := 16 * 2;
                size
              }
            }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success);
    }

    [Fact]
    public void Types_ComptimeBindingCanReferenceEarlierComptimeBinding()
    {
        var result = RunNameFirst(
            """
            BaseCapacity :: comptime 40;
            DefaultCapacity :: comptime BaseCapacity + 2;
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeBindingCannotReferenceRuntimeStaticBinding()
    {
        var result = RunNameFirst(
            """
            BaseCapacity :: 40;
            DefaultCapacity :: comptime BaseCapacity + 2;
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("not a previously evaluated comptime binding", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_LocalComptimeBindingCanReferenceEarlierLocalComptimeBinding()
    {
        var result = RunNameFirst(
            """
            main :: Unit -> Int {
              _ => {
                comptime base := 16;
                comptime size := base * 2;
                size
              }
            }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeFunctionCall_EvaluatesArgumentBinding()
    {
        var result = RunNameFirst(
            """
            double :: comptime Int -> Int { value => value * 2 }
            DefaultCapacity :: comptime double(21);
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Mir);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        AssertMirContainsIntConstant(mirModule, 42);
    }

    [Fact]
    public void Types_ComptimeFunctionCall_EvaluatesZeroArgumentUnitCall()
    {
        var result = RunNameFirst(
            """
            answer :: comptime Unit -> Int { _ => 42 }
            DefaultCapacity :: comptime answer();
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeFunctionCall_EvaluatesGroupedMultiArgumentCall()
    {
        var result = RunNameFirst(
            """
            add :: comptime Int -> Int -> Int { left => right => left + right }
            DefaultCapacity :: comptime add(20, 22);
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Mir);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        AssertMirContainsIntConstant(mirModule, 42);
    }

    [Fact]
    public void Types_ComptimeFunctionBodyCanCallComptimeFunction()
    {
        var result = RunNameFirst(
            """
            double :: comptime Int -> Int { value => value * 2 }
            quadruple :: comptime Int -> Int { value => double(double(value)) }
            DefaultCapacity :: comptime quadruple(10) + 2;
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Mir);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        AssertMirContainsIntConstant(mirModule, 42);
    }

    [Fact]
    public void Types_RecursiveComptimeFunction_EvaluatesToScalar()
    {
        var result = RunNameFirst(
            """
            factorial :: comptime Int -> Int {
                0 => 1,
                value => value * factorial(value - 1)
            }
            FactorialFive :: comptime factorial(5);
            main :: Unit -> Int { _ => FactorialFive }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Contains(
            result.TypeInferer!.ComptimeValues.Values,
            value => value is ComptimeIntegerValue { Value: 120 });
    }

    [Fact]
    public void Types_ComptimeFunctionPureBlock_EvaluatesLocalBindings()
    {
        var result = RunNameFirst(
            """
            compute :: comptime Int -> Int {
                value => {
                    doubled := value * 2;
                    offset := 2;
                    doubled + offset
                }
            }
            Answer :: comptime compute(20);
            main :: Unit -> Int { _ => Answer }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Contains(
            result.TypeInferer!.ComptimeValues.Values,
            value => value is ComptimeIntegerValue { Value: 42 });
    }

    [Fact]
    public void Types_ComptimeAdtConstructorAndPattern_EvaluatesSelectedPayload()
    {
        var result = RunNameFirst(
            """
            MaybeInt :: type { Some:: type(Int), None :: type {} }
            unwrap :: comptime MaybeInt -> Int {
                Some(value) => value,
                None() => 0
            }
            Answer :: comptime unwrap(Some(42));
            main :: Unit -> Int { _ => Answer }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Contains(
            result.TypeInferer!.ComptimeValues.Values,
            value => value is ComptimeIntegerValue { Value: 42 });
    }

    [Fact]
    public void Types_ComptimeNamedAdtConstructorAndPattern_EvaluatesFields()
    {
        var result = RunNameFirst(
            """
            Point :: type { x:: Int, y:: Int }
            sumPoint :: comptime Point -> Int {
                Point { x: x, y: y } => x + y
            }
            Answer :: comptime sumPoint(Point { x: 20, y: 22 });
            main :: Unit -> Int { _ => Answer }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Contains(
            result.TypeInferer!.ComptimeValues.Values,
            value => value is ComptimeIntegerValue { Value: 42 });
    }

    [Fact]
    public void Hir_ComptimeTupleReference_ReifiesTypedAggregate()
    {
        var result = RunNameFirst(
            """
            Pair :: comptime (20, 22);
            main :: Unit -> (Int, Int) { _ => Pair }
            """,
            CompilationPhase.Hir);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var module = Assert.IsType<HirModule>(result.HirModule);
        var main = Assert.Single(module.Declarations.OfType<HirFunc>(), function => function.Name == "main");
        var tuple = Assert.IsType<HirTuple>(main.Body);

        Assert.True(tuple.TypeId.IsValid);
        Assert.Collection(
            tuple.Elements,
            element => Assert.Equal(new TypeId(BaseTypes.IntId), Assert.IsType<HirLiteral>(element).TypeId),
            element => Assert.Equal(new TypeId(BaseTypes.IntId), Assert.IsType<HirLiteral>(element).TypeId));
    }

    [Fact]
    public void Hir_ComptimeListReference_ReifiesTypedElements()
    {
        var result = RunNameFirst(
            """
            Values :: comptime [20, 22];
            main :: Unit -> Seq[Int] { _ => Values }
            """,
            CompilationPhase.Hir);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var module = Assert.IsType<HirModule>(result.HirModule);
        var main = Assert.Single(module.Declarations.OfType<HirFunc>(), function => function.Name == "main");
        var list = Assert.IsType<HirList>(main.Body);

        Assert.True(list.TypeId.IsValid);
        Assert.All(
            list.Elements,
            element => Assert.Equal(new TypeId(BaseTypes.IntId), Assert.IsType<HirLiteral>(element).TypeId));
    }

    [Fact]
    public void Hir_ComptimeNestedGenericAdtReference_ReifiesEveryConcreteType()
    {
        var result = RunNameFirst(
            """
            Box[T] :: type { Box:: type(T) }
            Nested :: comptime Box(Box(42));
            main :: Unit -> Box[Box[Int]] { _ => Nested }
            """,
            CompilationPhase.Hir);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var module = Assert.IsType<HirModule>(result.HirModule);
        var main = Assert.Single(module.Declarations.OfType<HirFunc>(), function => function.Name == "main");
        var outerInjection = Assert.IsType<HirCaseInject>(main.Body);
        var outer = Assert.IsType<HirCall>(outerInjection.Operand);
        var inner = Assert.IsType<HirCall>(Assert.Single(outer.Arguments));
        var scalar = Assert.IsType<HirLiteral>(Assert.Single(inner.Arguments));

        Assert.True(outerInjection.SourceCase.IsValid);
        Assert.True(outerInjection.TargetAncestor.IsValid);
        Assert.True(outerInjection.SourceTypeId.IsValid);
        Assert.Equal(CallConvention.Constructor, outer.Convention);
        Assert.Equal(CallConvention.Constructor, inner.Convention);
        Assert.True(outer.TypeId.IsValid);
        Assert.True(inner.TypeId.IsValid);
        Assert.NotEqual(outer.TypeId, inner.TypeId);
        Assert.Equal(new TypeId(BaseTypes.IntId), scalar.TypeId);
    }

    [Fact]
    public void Mir_ComptimeNestedGenericAdtReference_LowersWithoutRuntimeBindingGetter()
    {
        var result = RunNameFirst(
            """
            Box[T] :: type { Box:: type(T) }
            Nested :: comptime Box(Box(42));
            main :: Unit -> Box[Box[Int]] { _ => Nested }
            """,
            CompilationPhase.Mir);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var module = Assert.IsType<MirModule>(result.MirModule);

        Assert.DoesNotContain(module.Functions, function => function.Name.Contains("__module_val__", StringComparison.Ordinal));
        Assert.Contains("Box", MirFormatter.FormatMir(module), StringComparison.Ordinal);
    }

    [Fact]
    public void Hir_ComptimeNamedAdtReference_UsesDeclarationFieldOrder()
    {
        var result = RunNameFirst(
            """
            Point :: type { x:: Int, y:: Int }
            Value :: comptime Point { y: 22, x: 20 };
            main :: Unit -> Point { _ => Value }
            """,
            CompilationPhase.Hir);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var module = Assert.IsType<HirModule>(result.HirModule);
        var main = Assert.Single(module.Declarations.OfType<HirFunc>(), function => function.Name == "main");
        var call = Assert.IsType<HirCall>(main.Body);

        Assert.Collection(
            call.Arguments,
            argument => Assert.Equal(20L, Assert.IsType<HirLiteral>(argument).Value),
            argument => Assert.Equal(22L, Assert.IsType<HirLiteral>(argument).Value));
    }

    [Fact]
    public void Types_RuntimeFunctionBodyCannotCallComptimeFunction()
    {
        var result = RunNameFirst(
            """
            fieldCount :: comptime Unit -> Int { _ => 1 }
            runtime :: Unit -> Int { _ => fieldCount(()) }
            main :: Unit -> Int { _ => runtime(()) }
            """,
            CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Cannot use comptime-only function 'fieldCount' from runtime code.", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_ComptimeIfExpression_EvaluatesSelectedThenBranch()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime if true then { 40 + 2 } else { 0 };
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Mir);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        AssertMirContainsIntConstant(mirModule, 42);
    }

    [Fact]
    public void Types_ComptimeIfExpression_EvaluatesSelectedElseBranch()
    {
        var result = RunNameFirst(
            """
            UseDefault :: comptime false;
            DefaultCapacity :: comptime if UseDefault then { 0 } else { 40 + 2 };
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeIfExpression_DoesNotEvaluateUnselectedBranch()
    {
        var result = RunNameFirst(
            """
            compute :: Unit -> Int { _ => 0 }
            DefaultCapacity :: comptime if true then { 42 } else { compute() };
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeIfExpression_RejectsNonBoolCondition()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime if 1 then { 42 } else { 0 };
            main :: Unit -> Int { _ => 0 }
            """,
            CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("if condition must evaluate to a comptime bool", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_ComptimeMatchExpression_EvaluatesLiteralBranch()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime match 2 { 1 => 10, 2 => 42, _ => 0 };
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Mir);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        AssertMirContainsIntConstant(mirModule, 42);
    }

    [Fact]
    public void Types_ComptimeMatchExpression_DoesNotEvaluateUnselectedBranch()
    {
        var result = RunNameFirst(
            """
            compute :: Unit -> Int { _ => 0 }
            DefaultCapacity :: comptime match 2 { 1 => compute(), 2 => 42 };
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeMatchExpression_EvaluatesOrRangeAndWildcardPatterns()
    {
        var result = RunNameFirst(
            """
            Tier :: comptime 4;
            DefaultCapacity :: comptime match Tier {
              0 | 1 => 16,
              2..4 => 42,
              _ => 0
            };
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeMatchExpression_EvaluatesTuplePattern()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime match (1, 2) { (1, 2) => 42, _ => 0 };
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeMatchExpression_EvaluatesTupleBindingPattern()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime match (40, 2) { (base, extra) => base + extra, _ => 0 };
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeMatchExpression_EvaluatesListPattern()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime match [1, 2, 3] { [1, 2, 3] => 42, _ => 0 };
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeMatchExpression_EvaluatesListBindingPattern()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime match [40, 2] { [base, extra] => base + extra, _ => 0 };
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeMatchExpression_EvaluatesListRestDiscardPattern()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime match [1, 2, 3] { [1, .._] => 42, _ => 0 };
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeMatchExpression_EvaluatesListRestBindingPattern()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime match [1, 2, 3] {
              [1, ..tail] => match tail { [2, 3] => 42, _ => 0 },
              _ => 0
            };
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeMatchExpression_EvaluatesGuardWithBinding()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime match 2 {
              value when value > 10 => 0,
              value when value > 1 => value + 40,
              _ => 0
            };
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeMatchExpression_EvaluatesPatternGuardBinding()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime match 0 {
              _ when [base, extra] <- [40, 2] => base + extra,
              _ => 0
            };
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeMatchExpression_RejectsNonBoolGuard()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime match 2 { value when value => 42, _ => 0 };
            main :: Unit -> Int { _ => 0 }
            """,
            CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("match guard must evaluate to a comptime bool", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_ComptimeMatchExpression_EvaluatesScalarBindingPattern()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime match 2 { value => value + 40, _ => 0 };
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeMatchExpression_RejectsNoMatchingBranch()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime match 2 { 1 => 10 };
            main :: Unit -> Int { _ => 0 }
            """,
            CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("no matching branch", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_ComptimeCallExpression_EvaluatesPureOrdinaryFunction()
    {
        var result = RunNameFirst(
            """
            compute :: Unit -> Int { _ => 42 }
            DefaultCapacity :: comptime compute();
            main :: Unit -> Int { _ => 0 }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeDivisionByZero_Fails()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime 1 / 0;
            main :: Unit -> Int { _ => 0 }
            """,
            CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("integer division by zero", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_ComptimeLogicalOperators_ShortCircuitUnselectedOperands()
    {
        var result = RunNameFirst(
            """
            ShortCircuitAnd :: comptime false && (1 / 0 == 0);
            ShortCircuitOr :: comptime true || (1 / 0 == 0);
            main :: Unit -> Int { _ => 0 }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(result.TypeInferer);
        var andSymbol = Assert.NotNull(symbolTable.LookupValue("ShortCircuitAnd"));
        var orSymbol = Assert.NotNull(symbolTable.LookupValue("ShortCircuitOr"));
        Assert.False(Assert.IsType<ComptimeBoolValue>(inferer.ComptimeValues[new SymbolId(andSymbol.Value)]).Value);
        Assert.True(Assert.IsType<ComptimeBoolValue>(inferer.ComptimeValues[new SymbolId(orSymbol.Value)]).Value);
    }

    [Fact]
    public void ComptimeAdtValues_UseStableConstructorIdentityInCanonicalPayloads()
    {
        var left = new ComptimeAdtValue(SymbolId.None, "Leaf", [], [])
        {
            ConstructorIdentity = "pkg.left.Root.Leaf"
        };
        var right = new ComptimeAdtValue(SymbolId.None, "Leaf", [], [])
        {
            ConstructorIdentity = "pkg.right.Root.Leaf"
        };

        Assert.False(left.StructuralEquals(right));
        Assert.NotEqual(left.CanonicalHash, right.CanonicalHash);
        Assert.True(ComptimeValuePayload.TryCreate(left, out var payload));
        Assert.Equal(left.ConstructorIdentity, payload.ConstructorIdentity);
        Assert.True(payload.TryRestoreValue(remapper: null, out var restored));
        Assert.Equal(left.CanonicalText, restored.CanonicalText);
    }

    [Fact]
    public void Mir_ModuleComptimeBinding_IsInlinedAndDoesNotEmitModuleValueGetter()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime 40 + 2;
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Mir);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        Assert.DoesNotContain(mirModule.Functions, function => function.Name.Contains("__module_val__", StringComparison.Ordinal));

        AssertMirContainsIntConstant(mirModule, 42);
    }

    [Fact]
    public void Mir_LocalComptimeBindings_DoNotCreateRuntimeLocals()
    {
        var result = RunNameFirst(
            """
            main :: Unit -> Int {
              _ => {
                comptime base := 16;
                comptime size := base * 2;
                0
              }
            }
            """,
            CompilationPhase.Mir);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        var main = Assert.Single(mirModule.Functions, function => function.Name == "main");

        Assert.DoesNotContain(main.Locals, local => local.Name == "base");
        Assert.DoesNotContain(main.Locals, local => local.Name == "size");
    }

    private static void AssertMirContainsIntConstant(MirModule module, long expected)
    {
        Assert.Contains($"return {expected}", MirFormatter.FormatMir(module), StringComparison.Ordinal);
    }

    private static CompilationResult RunNameFirst(string source, CompilationPhase stopAtPhase)
    {
        return new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "comptime_binding.eidos",
            StopAtPhase = stopAtPhase,
            LanguageVersion = EidosLanguageVersions.Current,
            EnableMirOptimizations = false,
            UseColors = false
        }).Run();
    }
}
