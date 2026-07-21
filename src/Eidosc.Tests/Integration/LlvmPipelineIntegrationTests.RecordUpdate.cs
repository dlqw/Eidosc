using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void RecordUpdateShorthand_NativeSmoke_PreservesFieldOrderAndRetainsCopiedFields()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
import std.Text

Direction :: type {
    North :: type {} ,
    East :: type {}
}

State :: type {
    label:: String,
    dir:: Direction,
    food:: String,
    score:: Int,
    alive:: Bool,
    tick:: Int
}

update_state :: State -> State
{
    State{
        label: label,
        dir: _,
        food: food,
        score: score,
        alive: alive,
        tick: _
    } => State{
        label: label,
        dir: East(),
        food: food,
        score: score,
        alive: alive,
        tick: 7
    }
}

main :: Unit -> Int
{
    _ => {
        state := State {
            label: "snake",
            dir: North(),
            food: "apple",
            score: 3,
            alive: true,
            tick: 0
        };
        updated := update_state(state);
        Text.len(Text.clone(ref updated.label)) +
            Text.len(Text.clone(ref updated.food)) +
            updated.score + updated.tick
    }
}
""";

        var execution = CompileAndRunSourceAtNative(
            source,
            "native_record_update_copied_fields.eidos",
            "native_record_update_copied_fields");

        Assert.Equal(20, execution.ExitCode);
    }
}
