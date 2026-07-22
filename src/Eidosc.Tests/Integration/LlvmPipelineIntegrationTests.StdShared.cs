using Eidosc;
using Eidosc.Mir;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void StdShared_NewCloneBorrowPtrEq_NativeSmoke_PreservesManagedPayload()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
import std.Shared

main :: Unit -> Int
{
    _ => {
        first := Shared.new((40, 2))
        second := Shared.clone(ref first)
        same := Shared.ptr_eq(ref first)(ref second)
        borrowed := Shared.borrow(ref second)
        match *borrowed {
            (value, extra) =>
                if same && value == 40 then { 42 } else { 1 }
        }
    }
}
""";

        var execution = CompileAndRunSourceAtNativeWithContext(
            source,
            StdlibListImportInputFile(),
            "std_shared_native_smoke");

        Assert.Equal(42, execution.ExitCode);

        var mirResult = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = StdlibListImportInputFile(),
            StopAtPhase = CompilationPhase.Mir,
            UseColors = false
        }).Run();

        Assert.True(mirResult.Success);
        var mirModule = Assert.IsType<MirModule>(mirResult.MirModule);
        Assert.Contains(
            mirModule.Functions.SelectMany(function => function.BasicBlocks)
                .SelectMany(block => block.Instructions)
                .OfType<MirCall>(),
            call => call.Function is MirFunctionRef functionRef &&
                    MirBuiltinFunctions.TryGetIntrinsicName(functionRef, out var intrinsicName) &&
                    intrinsicName == WellKnownStrings.InternalNames.SharedClone);
        Assert.DoesNotContain(
            mirModule.Functions.Where(function => function.ReturnType != new TypeId(BaseTypes.UnitId)),
            function => function.BasicBlocks.Any(block => block.Terminator is MirReturn { Value: null }));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void StdShared_BorrowedRecursivePayload_NativeSmoke_DoesNotReleaseBorrowedReference()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
import std.Shared

Entry :: type {
    value :: Int,
    left :: Tree,
    right :: Tree
}

Tree :: type {
    Empty :: type {},
    Node :: type(Shared[Entry])
}

read_tree :: Tree -> Int
{
    Empty() => 0,
    Node(ref node) => match *Shared.borrow(node) {
        Entry{value: value, left: _, right: _} => value
    }
}

main :: Unit -> Int
{
    _ => {
        entry := Entry{value: 42, left: Empty(), right: Empty()}
        tree := Node(Shared.new(entry))
        read_tree(tree)
    }
}
""";

        var execution = CompileAndRunSourceAtNativeWithContext(
            source,
            StdlibListImportInputFile(),
            "std_shared_recursive_payload_borrow_native_smoke");

        Assert.Equal(42, execution.ExitCode);
    }
}
