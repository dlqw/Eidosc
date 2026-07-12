using Eidosc.Mir;
using Eidosc.Pipeline;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public sealed class MirFunctionFingerprintTests
{
    [Fact]
    public void Compute_IsStableForEquivalentFunction()
    {
        var first = MirFunctionFingerprintBuilder.Compute(CreateFunction(BinaryOp.Add));
        var second = MirFunctionFingerprintBuilder.Compute(CreateFunction(BinaryOp.Add));

        Assert.Equal(first.BodyHash, second.BodyHash);
        Assert.Equal(first.FunctionKey, second.FunctionKey);
    }

    [Fact]
    public void Compute_ChangesWhenInstructionShapeChanges()
    {
        var first = MirFunctionFingerprintBuilder.Compute(CreateFunction(BinaryOp.Add));
        var second = MirFunctionFingerprintBuilder.Compute(CreateFunction(BinaryOp.Sub));

        Assert.NotEqual(first.BodyHash, second.BodyHash);
    }

    [Fact]
    public void ComputeModule_SortsByFunctionKey()
    {
        var module = new MirModule
        {
            Functions =
            [
                CreateFunction(BinaryOp.Add, name: "z"),
                CreateFunction(BinaryOp.Add, name: "a")
            ]
        };

        var fingerprints = MirFunctionFingerprintBuilder.ComputeModule(module);

        Assert.Equal(["name:a", "name:z"], fingerprints.Select(static fingerprint => fingerprint.FunctionKey));
    }

    [Fact]
    public void Snapshot_FromModule_HasStableModuleFingerprint()
    {
        var first = MirFunctionFingerprintSnapshot.FromModule(new MirModule
        {
            Functions = [CreateFunction(BinaryOp.Add)]
        });
        var second = MirFunctionFingerprintSnapshot.FromModule(new MirModule
        {
            Functions = [CreateFunction(BinaryOp.Add)]
        });

        Assert.Equal("mir-function-fingerprint-snapshot-v1", first.SchemaVersion);
        Assert.Equal(first.ModuleFingerprint, second.ModuleFingerprint);
        Assert.NotEmpty(first.ModuleFingerprint);
    }

    [Fact]
    public void Snapshot_ModuleFingerprintChangesWhenFunctionChanges()
    {
        var first = MirFunctionFingerprintSnapshot.FromModule(new MirModule
        {
            Functions = [CreateFunction(BinaryOp.Add)]
        });
        var second = MirFunctionFingerprintSnapshot.FromModule(new MirModule
        {
            Functions = [CreateFunction(BinaryOp.Sub)]
        });

        Assert.NotEqual(first.ModuleFingerprint, second.ModuleFingerprint);
    }

    [Fact]
    public void ModuleMirArtifactSnapshot_UsesTypedSurfaceAndMirFingerprint()
    {
        var typed = new ProjectModuleTypedSemanticSnapshot(
            ProjectModuleTypedSemanticSnapshot.CurrentSchemaVersion,
            [
                new ProjectModuleTypedSemanticNode(
                    "Main",
                    ["Lib"],
                    [],
                    "typed-surface",
                    "typed-deps",
                    "typed-main")
            ]);
        var fingerprints = MirFunctionFingerprintSnapshot.FromModule(new MirModule
        {
            Functions = [CreateFunction(BinaryOp.Add)]
        });

        var snapshot = ProjectModuleMirArtifactSnapshot.Create(typed, fingerprints);

        Assert.Equal("module-mir-artifact-snapshot-v1", snapshot.SchemaVersion);
        var node = Assert.Single(snapshot.Nodes);
        Assert.Equal("Main", node.ModuleKey);
        Assert.Equal(["Lib"], node.Dependencies);
        Assert.Equal("typed-main", node.TypedSemanticHash);
        Assert.Equal(fingerprints.ModuleFingerprint, node.MirFunctionModuleFingerprint);
        Assert.NotEmpty(node.MirArtifactHash);
    }

    private static MirFunc CreateFunction(BinaryOp op, string name = "main")
    {
        var intType = new TypeId(1);
        var result = new MirPlace
        {
            Kind = PlaceKind.Local,
            Local = new LocalId { Value = 1 },
            TypeId = intType
        };
        var left = new MirConstant
        {
            TypeId = intType,
            Value = new MirConstantValue.IntValue(1)
        };
        var right = new MirConstant
        {
            TypeId = intType,
            Value = new MirConstantValue.IntValue(2)
        };

        return new MirFunc
        {
            Name = name,
            ReturnType = intType,
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "result",
                    TypeId = intType,
                    IsMutable = true
                }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirBinOp
                        {
                            Target = result,
                            Operator = op,
                            Left = left,
                            Right = right
                        }
                    ],
                    Terminator = new MirReturn { Value = result }
                }
            ]
        };
    }
}
