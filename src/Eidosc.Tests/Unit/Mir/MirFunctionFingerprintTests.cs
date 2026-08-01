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
    public void Compute_CaseInjectionIgnoresTransientSymbolIds()
    {
        var first = MirFunctionFingerprintBuilder.Compute(
            CreateCaseInjectionFunction(new SymbolId(101), new SymbolId(102), new TypeId(201), new TypeId(202)));
        var second = MirFunctionFingerprintBuilder.Compute(
            CreateCaseInjectionFunction(new SymbolId(301), new SymbolId(302), new TypeId(201), new TypeId(202)));

        Assert.Equal(first.BodyHash, second.BodyHash);
    }

    [Fact]
    public void Compute_CaseInjectionChangesWhenNominalTypeIdentityChanges()
    {
        var first = MirFunctionFingerprintBuilder.Compute(
            CreateCaseInjectionFunction(new SymbolId(101), new SymbolId(102), new TypeId(201), new TypeId(202)));
        var second = MirFunctionFingerprintBuilder.Compute(
            CreateCaseInjectionFunction(new SymbolId(101), new SymbolId(102), new TypeId(301), new TypeId(202)));

        Assert.NotEqual(first.BodyHash, second.BodyHash);
    }

    [Fact]
    public void Compute_RecordUpdateUniquenessProofChangesFingerprint()
    {
        var general = MirFunctionFingerprintBuilder.Compute(CreateRecordUpdateFunction(knownUnique: false));
        var unique = MirFunctionFingerprintBuilder.Compute(CreateRecordUpdateFunction(knownUnique: true));

        Assert.NotEqual(general.BodyHash, unique.BodyHash);
    }

    [Fact]
    public void Compute_CallerOwnedAggregateAbiChangesFingerprint()
    {
        var ordinary = CreateFunction(BinaryOp.Add);
        var promoted = CreateFunction(BinaryOp.Add);
        promoted.CallerOwnedAggregateAbi = new MirCallerOwnedAggregateAbi
        {
            OutReturnType = promoted.ReturnType,
            OutReturnLocals = new HashSet<LocalId> { new() { Value = 1 } },
            LocalGroups =
            [
                new MirCallerOwnedAggregateGroup
                {
                    CanonicalLocal = new LocalId { Value = 1 },
                    TypeId = promoted.ReturnType,
                    Locals = new HashSet<LocalId> { new() { Value = 1 } },
                    ParameterIndex = -1
                }
            ]
        };

        Assert.NotEqual(
            MirFunctionFingerprintBuilder.Compute(ordinary).BodyHash,
            MirFunctionFingerprintBuilder.Compute(promoted).BodyHash);
    }

    [Fact]
    public void Compute_CallerOwnedArrayStorageShapeChangesFingerprint()
    {
        var baseline = new MirCallerOwnedArrayStorage
        {
            Key = "array-a",
            ArrayLocal = new LocalId { Value = 2 },
            ArrayTypeId = new TypeId(9100),
            Capacity = 3,
            ElementSize = 8,
            StorageBytes = 88
        };
        var baselineHash = ComputeStorageHash(baseline);
        var variants = new[]
        {
            baseline with { Key = "array-b" },
            baseline with { ArrayLocal = new LocalId { Value = 3 } },
            baseline with { ArrayTypeId = new TypeId(9101) },
            baseline with { Capacity = 4 },
            baseline with { ElementSize = 16 },
            baseline with { StorageBytes = 96 }
        };

        Assert.All(variants, storage => Assert.NotEqual(baselineHash, ComputeStorageHash(storage)));

        static string ComputeStorageHash(MirCallerOwnedArrayStorage storage)
        {
            var function = CreateFunction(BinaryOp.Add);
            function.CallerOwnedAggregateAbi = new MirCallerOwnedAggregateAbi
            {
                OutReturnType = function.ReturnType,
                OutReturnLocals = new HashSet<LocalId> { new() { Value = 1 } },
                OutArrayStorages = [storage]
            };
            return MirFunctionFingerprintBuilder.Compute(function).BodyHash;
        }
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

        Assert.Equal("mir-function-fingerprint-snapshot-v2", first.SchemaVersion);
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

    private static MirFunc CreateRecordUpdateFunction(bool knownUnique)
    {
        var recordType = new TypeId(8110);
        var source = new MirPlace
        {
            Kind = PlaceKind.Local,
            Local = new LocalId { Value = 1 },
            TypeId = recordType
        };
        var result = new MirPlace
        {
            Kind = PlaceKind.Local,
            Local = new LocalId { Value = 2 },
            TypeId = recordType
        };
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirCall
                {
                    Target = result,
                    Function = new MirFunctionRef { Name = "Record", TypeId = recordType },
                    Arguments = [source],
                    RecordUpdate = new MirRecordUpdateInfo
                    {
                        Source = source,
                        UpdatedFieldIndices = [0],
                        IsKnownUnique = knownUnique
                    }
                }
            ],
            Terminator = new MirReturn { Value = result }
        };
        return new MirFunc
        {
            Name = "record_update",
            ReturnType = recordType,
            Locals =
            [
                new MirLocal { Id = source.Local, Name = "source", TypeId = recordType, IsParameter = true },
                new MirLocal { Id = result.Local, Name = "result", TypeId = recordType }
            ],
            EntryBlockId = block.Id,
            BasicBlocks = [block]
        };
    }

    private static MirFunc CreateCaseInjectionFunction(
        SymbolId sourceCase,
        SymbolId targetAncestor,
        TypeId sourceType,
        TypeId targetType)
    {
        var source = new MirPlace
        {
            Kind = PlaceKind.Local,
            Local = new LocalId { Value = 1 },
            TypeId = sourceType
        };
        var target = new MirPlace
        {
            Kind = PlaceKind.Local,
            Local = new LocalId { Value = 2 },
            TypeId = targetType
        };

        return new MirFunc
        {
            Name = "inject",
            ReturnType = targetType,
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = source.Local, Name = "source", TypeId = sourceType },
                new MirLocal { Id = target.Local, Name = "target", TypeId = targetType }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirCaseInject
                        {
                            Target = target,
                            Operand = source,
                            SourceCase = sourceCase,
                            TargetAncestor = targetAncestor,
                            SourceTypeId = sourceType,
                            TargetTypeId = targetType
                        }
                    ],
                    Terminator = new MirReturn { Value = target }
                }
            ]
        };
    }
}
