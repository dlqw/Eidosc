using Eidosc.Symbols;
using Eidosc.Diagnostic;
using Eidosc.Mir;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    public void SourceEqOperator_ForAdt_RewritesMirToTraitImpl()
    {
        const string source = """
Eq :: trait {
    eq :: Self -> Self -> Bool
}

Direction :: type {
    North :: type {} , South :: type {} , East :: type {} , West :: type {}
}


EqDirection :: instance Eq {
    eq :: Direction -> Direction -> Bool {
        North() => North() => true,
        South() => South() => true,
        East() => East() => true,
        West() => West() => true,
        _ => _ => false
    }
}

main :: Unit -> Int
{
    _ => {
        east: Direction := East();
        if east == east then { 0 } else { 11 }
    }
}
""";

        var result = RunSourceAtMir(source, "mir_adt_eq_operator_source.eidos");

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.Equal(CompilationPhase.Mir, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);

        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var eqTraitIdLookup = symbolTable.LookupType("Eq");
        Assert.True(eqTraitIdLookup.HasValue);
        var directionSymbolIdLookup = symbolTable.LookupType("Direction");
        Assert.True(directionSymbolIdLookup.HasValue);

        var directionTypeId = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(directionSymbolIdLookup.Value)).TypeId;
        var directionEqImpl = symbolTable.LookupImplForTrait(directionTypeId, eqTraitIdLookup.Value);
        Assert.NotNull(directionEqImpl);
        var directionEqMethodId = Assert.Single(directionEqImpl!.Methods);

        var main = Assert.Single(mirModule.Functions, function => function.Name == "main");
        var mainInstructions = main.BasicBlocks.SelectMany(block => block.Instructions).ToList();
        Assert.Contains(
            mainInstructions.OfType<MirCall>(),
            call => call.Function is MirFunctionRef { SymbolId: var symbolId } &&
                    symbolId == directionEqMethodId);
        Assert.DoesNotContain(
            mainInstructions.OfType<MirBinOp>(),
            binOp => binOp.Operator is BinaryOp.Eq or BinaryOp.Ne &&
                     (binOp.Left.TypeId == directionTypeId || binOp.Right.TypeId == directionTypeId));
    }

    [Fact]
    public void SourceEqOperator_ForAdt_NativeSmoke_UsesTraitImpl()
    {
        const string source = """
Eq :: trait {
    eq :: Self -> Self -> Bool
}

Direction :: type {
    North :: type {} , South :: type {} , East :: type {} , West :: type {}
}


EqDirection :: instance Eq {
    eq :: Direction -> Direction -> Bool {
        North() => North() => true,
        South() => South() => true,
        East() => East() => true,
        West() => West() => true,
        _ => _ => false
    }
}

Box :: type { dir:: Direction }

keep :: Box -> Box
{
    Box { dir: d } => Box { dir: d }
}

main :: Unit -> Int
{
    _ => {
        east: Direction := East();
        direct := east == east;
        notEqual := east != east;
        boxed := keep(Box { dir: East() });
        match boxed {
            Box { dir: d } =>
                if direct && !notEqual && d == East() then { 0 }
                else if !direct then { 11 }
                else if notEqual then { 12 }
                else { 22 }
        }
    }
}
""";

        var execution = CompileAndRunSourceAtNative(
            source,
            "native_adt_eq_operator_source.eidos",
            "native_adt_eq_operator_source");

        Assert.Equal(0, execution.ExitCode);
    }

    [Fact]
    public void SourceEqOperator_WithMultipleAdtImpls_NativeSmoke_UsesDistinctLlvmFunctions()
    {
        const string source = """
import std.Seq
import std.Traits

Direction :: type {
    North :: type {} , East :: type {}
}


EqDirection :: instance Eq {
    eq :: Direction -> Direction -> Bool {
        North() => North() => true,
        East() => East() => true,
        _ => _ => false
    }
}

Pos :: type { x:: Int, y:: Int }


EqPos :: instance Eq {
    eq :: Pos -> Pos -> Bool {
        Pos { x: ax, y: ay } => other => match other {
            Pos { x: bx, y: by } => ax == bx && ay == by
        }
    }
}

contains_pos :: Seq[Pos] -> Pos -> Bool
{
    xs => p => Seq.any(xs)(candidate => candidate == p)
}

main :: Unit -> Int
{
    _ => {
        snake_for_hit := [Pos { x: 2, y: 1 }, Pos { x: 1, y: 1 }, Pos { x: 0, y: 1 }];
        snake_for_miss := [Pos { x: 2, y: 1 }, Pos { x: 1, y: 1 }, Pos { x: 0, y: 1 }];
        north: Direction := North();
        directionOk := north == north;
        hit := contains_pos(snake_for_hit)(Pos { x: 1, y: 1 });
        miss := contains_pos(snake_for_miss)(Pos { x: 3, y: 1 });
        if directionOk && hit && !miss then { 0 } else { 31 }
    }
}
""";

        var execution = CompileAndRunSourceAtNative(
            source,
            "native_multiple_adt_eq_impls.eidos",
            "native_multiple_adt_eq_impls");

        Assert.Equal(0, execution.ExitCode);
    }

    [Fact]
    public void SourceEqOperator_ForExactCase_PrefersCaseImplOverParentImpl()
    {
        const string source = """
Eq :: trait {
    eq :: Self -> Self -> Bool
}

Anim :: type {
    Dog :: type {},
    Cat :: type {},
}

EqAnim :: instance Eq {
    eq :: Anim -> Anim -> Bool {
        _ => _ => false
    }
}

EqDog :: instance Eq {
    eq :: Anim.Dog -> Anim.Dog -> Bool {
        _ => _ => true
    }
}

main :: Unit -> Int
{
    _ => if Dog() == Dog() then { 0 } else { 47 }
}
""";

        var execution = CompileAndRunSourceAtNative(
            source,
            "native_exact_case_eq_impl_precedence.eidos",
            "native_exact_case_eq_impl_precedence");

        Assert.Equal(0, execution.ExitCode);
    }
}
