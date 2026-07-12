using System.Reflection;
using Eidosc.Ide;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public partial class IdeSemanticSnapshotTests
{
    [Fact]
    public void Build_BorrowPhase_BorrowTaggedEffectDoesNotGrantBorrowCapabilities()
    {
        const string source = """
@borrow(read)
Reader :: effect;

keep :: Int -> Int need Reader
{
    _ => 0
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_snapshot_borrow_tagged.eidos",
            StopAtPhase = CompilationPhase.Borrow,
                UseColors = false
        }).Run();

        Assert.Equal(CompilationPhase.Borrow, result.CompletedPhase);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var keep = Assert.Single(snapshot.BorrowCapabilities, entry => entry.FunctionName == "keep");
        Assert.False(keep.HasSnapshot);
        Assert.False(keep.IsEnforced);
        Assert.Empty(keep.GlobalCapabilities);
        Assert.Empty(keep.Providers);
    }

    [Fact]
    public void Build_BorrowPhase_WithUntaggedEffect_ReportsNoBorrowSnapshot()
    {
        const string source = """
Reader :: effect;

keep :: Int -> Int need Reader
{
    _ => 0
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_snapshot_borrow_untagged.eidos",
            StopAtPhase = CompilationPhase.Borrow,
                UseColors = false
        }).Run();

        Assert.Equal(CompilationPhase.Borrow, result.CompletedPhase);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var keep = Assert.Single(snapshot.BorrowCapabilities, entry => entry.FunctionName == "keep");
        Assert.False(keep.HasSnapshot);
        Assert.False(keep.IsEnforced);
        Assert.Empty(keep.GlobalCapabilities);
        Assert.Empty(keep.Providers);
    }
}
