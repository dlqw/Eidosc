using System.Reflection;
using Eidosc.Cli.Lsp;
using Eidosc.Ide;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public partial class IdeSemanticSnapshotTests
{
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

    [Fact]
    public void Build_MirPhase_ExposesStructuredOwnershipContractForFunction()
    {
        const string source = """
borrow_value :: Ref[Int] -> MRef[String]
{
    _ => unreachable
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ide_snapshot_ownership.eidos",
            AllowVirtualInputFile = true,
            StopAtPhase = CompilationPhase.Mir,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var function = Assert.Single(snapshot.Symbols, entry => entry.Name == "borrow_value" && entry.Kind == "function");
        var contract = Assert.IsType<IdeOwnershipContractEntry>(function.OwnershipContract);
        Assert.Equal("ownership-contract-v1", contract.SchemaVersion);
        Assert.Collection(
            contract.Parameters,
            parameter => Assert.Equal("sharedBorrow", parameter.Kind));
        Assert.Equal("mutableBorrow", contract.Result.Kind);

        var hover = LspSemanticMapper.MapHover(snapshot, line: 0, character: 2);
        Assert.NotNull(hover);
        var markup = Assert.IsType<LspMarkupContent>(hover.Contents);
        Assert.Contains("ownership: (sharedBorrow) -> mutableBorrow", markup.Value, StringComparison.Ordinal);
    }
}
