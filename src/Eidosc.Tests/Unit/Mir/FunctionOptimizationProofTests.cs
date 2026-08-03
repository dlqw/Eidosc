using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public sealed class FunctionOptimizationProofTests
{
    private static readonly TypeId IntType = new(BaseTypes.IntId);

    [Fact]
    public void Analyze_RecursivePureFunction_SeparatesSemanticAndStructuralProofs()
    {
        var function = CreateRecursiveFunction();
        var module = new MirModule { Name = "Main", Functions = [function] };
        var effects = new Dictionary<SymbolId, FunctionEffectSummary>
        {
            [function.SymbolId] = new(EffectRow.Pure, EffectRow.Pure)
        };

        var proofs = FunctionOptimizationProofAnalyzer.Analyze(module, effects);

        Assert.True(proofs.IsRecursive(function));
        Assert.False(proofs.Allows(
            function,
            FunctionOptimizationCapability.EliminateUnusedCall));
        Assert.False(proofs.Allows(
            function,
            FunctionOptimizationCapability.ReuseCallResult));
        Assert.True(proofs.Allows(
            function,
            FunctionOptimizationCapability.ReassociatePureCalls));
        Assert.True(proofs.Allows(
            function,
            FunctionOptimizationCapability.FoldConstantCall));
        Assert.True(proofs.Allows(
            function,
            FunctionOptimizationCapability.InlineBody));
        Assert.False(proofs.Allows(
            function,
            FunctionOptimizationCapability.ReorderSequenceCallback));
    }

    [Fact]
    public void ReorderSequenceCallback_RequiresStrictObservableFreedom()
    {
        Assert.True(FunctionOptimizationSummary.Pure.Allows(
            FunctionOptimizationCapability.ReorderSequenceCallback));

        var rejected = new[]
        {
            FunctionOptimizationSummary.Pure with { IsTrusted = false },
            FunctionOptimizationSummary.Pure with
            {
                Effects = new EffectRow([new EffectTag(new SymbolId(901), "io")])
            },
            FunctionOptimizationSummary.Pure with { Memory = FunctionMemoryBehavior.Read },
            FunctionOptimizationSummary.Pure with { MayPanic = true },
            FunctionOptimizationSummary.Pure with { MayDiverge = true },
            FunctionOptimizationSummary.Pure with { MaySuspend = true },
            FunctionOptimizationSummary.Pure with { MayBlock = true },
            FunctionOptimizationSummary.Pure with { MayAllocate = true },
            FunctionOptimizationSummary.Pure with { MaySynchronize = true },
            FunctionOptimizationSummary.Pure with { Determinism = FunctionDeterminism.Nondeterministic }
        };

        Assert.All(rejected, summary => Assert.False(summary.Allows(
            FunctionOptimizationCapability.ReorderSequenceCallback)));
    }

    [Fact]
    public void Optimize_ConsecutiveProofConsumers_ReuseSameSnapshotProofs()
    {
        var first = new CaptureProofPass("First");
        var second = new CaptureProofPass("Second");
        var proofAnalysisCount = 0;
        var optimizer = new MirOptimizer(name =>
        {
            if (name == "proofs.analyze")
            {
                proofAnalysisCount++;
            }

            return EmptyDisposable.Instance;
        });
        optimizer.RegisterPass(first);
        optimizer.RegisterPass(second);

        optimizer.Optimize(new MirModule { Name = "Main" });

        Assert.Equal(1, proofAnalysisCount);
        Assert.Same(first.Proofs, second.Proofs);
    }

    [Fact]
    public void Optimize_ChangedSnapshot_RebuildsProofsForNextConsumer()
    {
        var first = new CaptureProofPass("First");
        var second = new CaptureProofPass("Second");
        var proofAnalysisCount = 0;
        var optimizer = new MirOptimizer(name =>
        {
            if (name == "proofs.analyze")
            {
                proofAnalysisCount++;
            }

            return EmptyDisposable.Instance;
        });
        optimizer.RegisterPass(first);
        optimizer.RegisterPass(new FreshSnapshotPass());
        optimizer.RegisterPass(second);

        optimizer.Optimize(new MirModule { Name = "Main" });

        Assert.Equal(2, proofAnalysisCount);
        Assert.NotSame(first.Proofs, second.Proofs);
    }

    private static MirFunc CreateRecursiveFunction()
    {
        var functionId = new FunctionId
        {
            SymbolId = new SymbolId(801),
            Name = "recur",
            QualifiedName = "Main.recur"
        };
        var parameter = new MirLocal
        {
            Id = new LocalId { Value = 1 },
            Name = "value",
            TypeId = IntType,
            IsParameter = true
        };
        var result = new MirLocal
        {
            Id = new LocalId { Value = 2 },
            Name = "result",
            TypeId = IntType
        };
        var parameterPlace = LocalPlace(parameter.Id);
        var resultPlace = LocalPlace(result.Id);

        return new MirFunc
        {
            Name = "recur",
            SymbolId = functionId.SymbolId,
            FunctionId = functionId,
            Locals = [parameter, result],
            EntryBlockId = new BlockId { Value = 1 },
            ReturnType = IntType,
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirCall
                        {
                            Target = resultPlace,
                            Function = new MirFunctionRef
                            {
                                Name = "recur",
                                SymbolId = functionId.SymbolId,
                                FunctionId = functionId
                            },
                            Arguments = [parameterPlace]
                        }
                    ],
                    Terminator = new MirReturn { Value = resultPlace }
                }
            ]
        };
    }

    private static MirPlace LocalPlace(LocalId localId) => new()
    {
        Kind = PlaceKind.Local,
        Local = localId,
        TypeId = IntType
    };

    private sealed class CaptureProofPass(string name) :
        IMirOptimizationPass,
        IFunctionOptimizationProofConsumer
    {
        public string Name { get; } = name;

        public FunctionOptimizationProofIndex? Proofs { get; private set; }

        FunctionOptimizationProofIndex IFunctionOptimizationProofConsumer.FunctionProofs
        {
            set
            {
                if (!ReferenceEquals(value, FunctionOptimizationProofIndex.Empty))
                {
                    Proofs = value;
                }
            }
        }

        public MirModule Run(MirModule module) => module;
    }

    private sealed class FreshSnapshotPass : IMirOptimizationPass
    {
        public string Name => "FreshSnapshot";

        public MirModule Run(MirModule module) => module.WithFunctions(module.Functions.ToList());
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static EmptyDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
