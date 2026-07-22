using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

public partial class TypeInferencePipelineTests
{
    [Fact]
    public void Types_BareProductType_ConstructsWithNamedFields()
    {
        const string source = """
GameState :: type {
    snake:: Int,
    dir:: Int,
    tick:: Int
}

init :: Unit -> GameState
{
    _ => GameState { snake: 0, dir: 1, tick: 0 }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_BareProductType_SupportsRecordUpdateSpread()
    {
        const string source = """
GameState :: type {
    snake:: Int,
    dir:: Int,
    tick:: Int
}

reset_tick :: GameState -> GameState
{
    state => { GameState { ..state, tick: 0 } }
}

read_state :: GameState -> Int
{
    state => {
        updated := reset_tick(state);
        updated.snake + updated.dir + updated.tick
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Borrow);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_BareProductType_SupportsRecordPattern()
    {
        const string source = """
GameState :: type {
    snake:: Int,
    dir:: Int,
    tick:: Int
}

read_dir :: GameState -> Int
{
    GameState { snake: _, dir: d, tick: _ } => d
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_BareProductType_FieldAccess()
    {
        const string source = """
Point :: type {
    x:: Int,
    y:: Int
}

make :: Int -> Int -> Point
{
    x => y => Point { x: x, y: y }
}

sum_x :: Point -> Int
{
    p => p.x + p.y
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }
}
