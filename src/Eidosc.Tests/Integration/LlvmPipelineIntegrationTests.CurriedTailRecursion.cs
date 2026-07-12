using Eidosc.Diagnostic;
using Eidosc.Mir;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    // Regression for TCO of curried self-tail-recursion. Before the curried-call
    // flattening fix, `count_down(n)(acc)` lowered to two nested single-arg MirCalls,
    // so the self-recursive edge was never in tail position and TCO no-op'd.
    // After the fix, the call flattens to one multi-arg MirCall that TCO rewrites
    // into a loop (back-edge MirGoto, no remaining self MirCall).
    [Fact]
    public void MirOpt_CurriedSelfTailRecursion_FlattensAndConvertsToLoop()
    {
        const string Source = """
            count_down :: Int -> Int -> Int
            {
                n => acc => if n == 0 then { acc } else { count_down(n - 1)(acc + 1) }
            }
            """;

        var result = RunSourceAtLlvmWithDefaultMirOpt(Source, "tco_curried_count_down.eidos");

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);

        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        var countDown = Assert.Single(mirModule.Functions,
            function => function.Name.StartsWith("count_down", StringComparison.Ordinal));

        // The self-recursive MirCall must be gone: TCO converts it to a back-edge MirGoto.
        Assert.DoesNotContain(countDown.BasicBlocks, block =>
            block.Instructions.Any(instruction =>
                instruction is MirCall call &&
                call.Function is MirFunctionRef functionRef &&
                    functionRef.Name.StartsWith("count_down", StringComparison.Ordinal)));

        // A loop back-edge (MirGoto targeting a non-exit block) must be present.
        Assert.Contains(countDown.BasicBlocks, block =>
            block.Terminator is MirGoto gotoTerminator &&
            gotoTerminator.Target.Value != countDown.EntryBlockId.Value);
    }

    // End-to-end stack-safety check at large depth. The curried-call flattening fix
    // made TCO convert this to a loop, and the alloca-hoisting fix made the loop body
    // stack-static (the param-pair box alloc no longer grows the stack each iteration).
    // Together they let a curried self-tail-recursion run at arbitrary depth: 5,000,000
    // is ~100x the pre-alloca-fix ceiling (~80k) and ~500x the pre-TCO-fix ceiling (~10k).
    [Fact]
    public void Native_CurriedSelfTailRecursion_CompletesWithoutStackOverflow()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string Source = """
            count_down :: Int -> Int -> Int
            {
                n => acc => if n == 0 then { acc } else { count_down(n - 1)(acc + 1) }
            }

            main :: Unit -> Int need FFI, IO
            {
                _ => {
                    print_string("result=");
                    print_int(count_down(5000000)(0));
                    print_newline();
                    0
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            Source,
            inputFile: "tco_curried_count_down_native.eidos",
            executableBaseName: "tco_curried_count_down_native");

        // Exit 0 == no stack overflow / no crash.
        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("result=5000000", execution.StandardOutput);
    }

    // Regression for the constructor stack-promotion alloca site: a constructor
    // built inside a curried self-tail-recursion must not grow the stack each
    // iteration. Before the alloca-hoisting fix, the stack-promo `alloca` for the
    // constructed record landed in the loop body and overflowed at depth ~80k.
    [Fact]
    public void Native_ConstructorInCurriedTailRecursion_CompletesWithoutStackOverflow()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string Source = """
            Acc :: type {
                Acc(Int)
            }

            acc_value :: Acc -> Int
            {
                Acc(v) => v
            }

            // Each iteration constructs an Acc record (exercises the constructor
            // stack-promotion alloca path) inside the tail-recursive loop body.
            build_acc :: Int -> Int -> Int
            {
                n => total => if n == 0 then { total } else { build_acc(n - 1)(total + acc_value(Acc(n))) }
            }

            main :: Unit -> Int need FFI, IO
            {
                _ => {
                    print_string("result=");
                    print_int(build_acc(200000)(0));
                    print_newline();
                    0
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            Source,
            inputFile: "tco_constructor_in_loop_native.eidos",
            executableBaseName: "tco_constructor_in_loop_native");

        // Exit 0 == no stack overflow / no crash. Sum 1..200000 = 20000100000.
        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("result=20000100000", execution.StandardOutput);
    }
}
