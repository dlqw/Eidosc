using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public class RecursiveCallAnalysisTests
{
    [Fact]
    public void Analyze_MutualRecursiveTailOnlyComponent_ReportsTailEdges()
    {
        var even = Function(
            "even",
            10,
            new MirCall
            {
                Function = FunctionRef("odd", 11),
                IsTailCall = true
            });
        var odd = Function(
            "odd",
            11,
            new MirCall
            {
                Function = FunctionRef("even", 10),
                IsTailCall = true
            });

        var result = RecursiveCallAnalysis.Analyze(new MirModule
        {
            Name = "Test",
            Functions = [even, odd]
        });

        var component = Assert.Single(result.Components);
        Assert.False(component.IsSelfRecursiveOnly);
        Assert.False(component.HasNonTailEdges);
        Assert.Equal(2, component.TailEdgeCount);
        Assert.Equal(["even", "odd"], component.FunctionNames);
    }

    [Fact]
    public void Analyze_SelfRecursiveNonTailCall_ReportsNonTailEdge()
    {
        var recursive = Function(
            "factorial",
            20,
            new MirCall
            {
                Target = Place(1),
                Function = FunctionRef("factorial", 20)
            },
            new MirBinOp
            {
                Target = Place(2),
                Operator = BinaryOp.Mul,
                Left = Place(1),
                Right = Place(3)
            });

        var result = RecursiveCallAnalysis.Analyze(new MirModule
        {
            Name = "Test",
            Functions = [recursive]
        });

        var component = Assert.Single(result.Components);
        Assert.True(component.IsSelfRecursiveOnly);
        Assert.True(component.HasNonTailEdges);
        var edge = Assert.Single(component.Edges);
        Assert.False(edge.IsTailCall);
        Assert.Equal("factorial", edge.CallerName);
        Assert.Equal("factorial", edge.CalleeName);
    }

    [Fact]
    public void Format_IncludesNonTailRecursiveSummary()
    {
        var recursive = Function(
            "parse_type_in_env",
            30,
            new MirCall
            {
                Target = Place(1),
                Function = FunctionRef("parse_type_in_env", 30)
            });

        var formatted = RecursiveCallAnalysis.Format(RecursiveCallAnalysis.Analyze(new MirModule
        {
            Name = "Test",
            Functions = [recursive]
        }));

        Assert.Contains("recursive_call_analysis:", formatted, StringComparison.Ordinal);
        Assert.Contains("non_tail_edges: 1", formatted, StringComparison.Ordinal);
        Assert.Contains("self non-tail-present: parse_type_in_env", formatted, StringComparison.Ordinal);
    }

    private static MirFunc Function(string name, int symbolId, params MirInstruction[] instructions)
    {
        return new MirFunc
        {
            Name = name,
            SymbolId = new SymbolId(symbolId),
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = new LocalId { Value = 1 }, Name = "result" },
                new MirLocal { Id = new LocalId { Value = 2 }, Name = "combined" },
                new MirLocal { Id = new LocalId { Value = 3 }, Name = "argument", IsParameter = true }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions = instructions.ToList(),
                    Terminator = new MirReturn()
                }
            ]
        };
    }

    private static MirFunctionRef FunctionRef(string name, int symbolId)
    {
        return new MirFunctionRef
        {
            Name = name,
            SymbolId = new SymbolId(symbolId)
        };
    }

    private static MirPlace Place(int localId)
    {
        return new MirPlace
        {
            Kind = PlaceKind.Local,
            Local = new LocalId { Value = localId }
        };
    }
}
