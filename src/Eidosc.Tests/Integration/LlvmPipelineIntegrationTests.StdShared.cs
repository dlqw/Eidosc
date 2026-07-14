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
import Std.Shared

main :: Unit -> Int
{
    _ => {
        first := Shared.new((40, 2))
        second := Shared.clone(first)
        same := Shared.ptr_eq(first)(second)
        borrowed := Shared.borrow(second)
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
}
