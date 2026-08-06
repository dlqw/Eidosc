using System.Reflection;
using System.Text.Json;
using Eidosc.CodeGen.Llvm;
using Eidosc.Mir;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Symbols;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;
using ReflectionType = System.Type;

namespace Eidosc.Tests.Unit.Pipeline;

public sealed class ModuleMirStatePayloadTests
{
    [Fact]
    public void Create_CoversAndRestoresEveryConcreteMirShape()
    {
        var module = CreateAllShapesModule();
        var originalTypes = CollectMirObjectTypes(module);

        AssertConcreteCoverage(typeof(MirInstruction), originalTypes);
        AssertConcreteCoverage(typeof(MirOperand), originalTypes);
        AssertConcreteCoverage(typeof(MirTerminator), originalTypes);
        AssertConcreteCoverage(typeof(MirConstantValue), originalTypes);

        var payload = ModuleMirStatePayload.Create(module);

        Assert.True(payload.IsRestorable, string.Join(Environment.NewLine, payload.UnsupportedNodeKinds));
        Assert.True(payload.TryRestore(out var restored));
        AssertEquivalentFingerprints(module, restored);

        var originalPlan = module.Functions
            .SelectMany(static function => function.BasicBlocks)
            .Select(static block => block.Terminator)
            .OfType<MirSwitch>()
            .Select(static terminator => terminator.DecisionPlan)
            .First(static plan => plan != null);
        var restoredPlan = restored.Functions
            .SelectMany(static function => function.BasicBlocks)
            .Select(static block => block.Terminator)
            .OfType<MirSwitch>()
            .Select(static terminator => terminator.DecisionPlan)
            .First(static plan => plan != null);
        Assert.Equal(originalPlan, restoredPlan);

        var restoredTypes = CollectMirObjectTypes(restored);
        Assert.Empty(originalTypes.Except(restoredTypes).Select(static type => type.Name));
    }

    [Fact]
    public void Create_RoundTripsEveryConcreteMirShapeThroughJson()
    {
        var module = CreateAllShapesModule();
        var payload = ModuleMirStatePayload.Create(module);

        var json = JsonSerializer.Serialize(payload);
        var roundTripped = JsonSerializer.Deserialize<ModuleMirStatePayload>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(payload.Hash, roundTripped!.Hash);
        Assert.True(roundTripped.TryRestore(out var restored));
        AssertEquivalentFingerprints(module, restored);

        var restoredTypes = CollectMirObjectTypes(restored);
        AssertConcreteCoverage(typeof(MirInstruction), restoredTypes);
        AssertConcreteCoverage(typeof(MirOperand), restoredTypes);
        AssertConcreteCoverage(typeof(MirTerminator), restoredTypes);
        AssertConcreteCoverage(typeof(MirConstantValue), restoredTypes);
    }

    [Fact]
    public void Create_RestoresStructuredOwnershipContract()
    {
        var valueType = new TypeId(BaseTypes.StringId);
        var sharedType = new TypeId(9001);
        var descriptors = new Dictionary<int, TypeDescriptor>
        {
            [valueType.Value] = new TypeDescriptor.Builtin(valueType.Value),
            [sharedType.Value] = new TypeDescriptor.Ref(valueType)
        };
        var contract = OwnershipContract.Create(
            new SymbolId(41),
            "borrow_text",
            [("value", valueType)],
            sharedType,
            descriptors);
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Terminator = new MirReturn()
        };
        var module = new MirModule
        {
            Name = "ownership_contract_restore",
            TypeDescriptors = descriptors,
            Functions =
            [
                new MirFunc
                {
                    Name = "borrow_text",
                    SymbolId = new SymbolId(41),
                    EntryBlockId = block.Id,
                    BasicBlocks = [block],
                    OwnershipContract = contract
                }
            ]
        };

        var payload = ModuleMirStatePayload.Create(module);
        var json = JsonSerializer.Serialize(payload);
        var roundTripped = JsonSerializer.Deserialize<ModuleMirStatePayload>(json);

        Assert.NotNull(roundTripped);
        Assert.True(roundTripped!.TryRestore(out var restored));
        var restoredContract = Assert.Single(restored.Functions).OwnershipContract;
        Assert.Equal(contract.SchemaVersion, restoredContract.SchemaVersion);
        Assert.Equal(contract.CanonicalIdentity, restoredContract.CanonicalIdentity);
        Assert.Equal(OwnershipPassingKind.ByValue, restoredContract.GetParameter(0).Projection.Kind);
        Assert.Equal(OwnershipPassingKind.SharedBorrow, restoredContract.Result.Projection.Kind);
    }

    [Fact]
    public void Create_RestoresKnownUniqueRecordUpdateProof()
    {
        var recordType = new TypeId(9010);
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
                        IsKnownUnique = true
                    }
                }
            ],
            Terminator = new MirReturn { Value = result }
        };
        var module = new MirModule
        {
            Name = "known_unique_record_update",
            Functions =
            [
                new MirFunc
                {
                    Name = "update",
                    ReturnType = recordType,
                    Locals =
                    [
                        new MirLocal { Id = source.Local, Name = "source", TypeId = recordType, IsParameter = true },
                        new MirLocal { Id = result.Local, Name = "result", TypeId = recordType }
                    ],
                    EntryBlockId = block.Id,
                    BasicBlocks = [block]
                }
            ]
        };

        var json = JsonSerializer.Serialize(ModuleMirStatePayload.Create(module));
        var payload = Assert.IsType<ModuleMirStatePayload>(
            JsonSerializer.Deserialize<ModuleMirStatePayload>(json));

        Assert.True(payload.TryRestore(out var restored));
        var call = Assert.IsType<MirCall>(Assert.Single(restored.Functions[0].BasicBlocks[0].Instructions));
        Assert.True(call.RecordUpdate!.IsKnownUnique);
    }

    [Fact]
    public void Create_RestoresCallerOwnedAggregateAbi()
    {
        var resultType = new TypeId(9011);
        var arrayType = new TypeId(9012);
        var local = new LocalId { Value = 1 };
        var arrayLocal = new LocalId { Value = 2 };
        var arrayStorage = new MirCallerOwnedArrayStorage
        {
            Key = "test:caller_owned|array:2",
            ArrayLocal = arrayLocal,
            ArrayTypeId = arrayType,
            Capacity = 3,
            ElementSize = 16,
            StorageBytes = 112,
            PromoteInline = true
        };
        var function = new MirFunc
        {
            Name = "caller_owned",
            ReturnType = resultType,
            EntryBlockId = new BlockId { Value = 1 },
            Locals = [new MirLocal { Id = local, Name = "result", TypeId = resultType }],
            CallerOwnedAggregateAbi = new MirCallerOwnedAggregateAbi
            {
                OutReturnType = resultType,
                OutReturnLocals = new HashSet<LocalId> { local },
                OutArrayStorages = [arrayStorage],
                LocalArrayStorages = [arrayStorage with { Key = "test:local|array:2" }],
                LocalGroups =
                [
                    new MirCallerOwnedAggregateGroup
                    {
                        CanonicalLocal = local,
                        TypeId = resultType,
                        Locals = new HashSet<LocalId> { local },
                        ArrayStorages = [arrayStorage],
                        ParameterIndex = -1
                    }
                ]
            },
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Terminator = new MirReturn { Value = new MirPlace { Kind = PlaceKind.Local, Local = local, TypeId = resultType } }
                }
            ]
        };
        var payload = ModuleMirStatePayload.Create(new MirModule { Name = "caller_owned", Functions = [function] });

        Assert.True(payload.TryRestore(out var restored));
        var abi = Assert.Single(restored.Functions).CallerOwnedAggregateAbi;
        Assert.Equal(resultType, abi.OutReturnType);
        Assert.Contains(local, abi.OutReturnLocals);
        var restoredOutStorage = Assert.Single(abi.OutArrayStorages);
        Assert.Equal(arrayStorage, restoredOutStorage);
        var restoredLocalStorage = Assert.Single(abi.LocalArrayStorages);
        Assert.Equal(arrayStorage with { Key = "test:local|array:2" }, restoredLocalStorage);
        var group = Assert.Single(abi.LocalGroups);
        Assert.Equal(local, group.CanonicalLocal);
        Assert.Contains(local, group.Locals);
        var restoredGroupStorage = Assert.Single(group.ArrayStorages);
        Assert.Equal(arrayStorage, restoredGroupStorage);
    }

    [Fact]
    public void Create_FromCompiledMir_RestoresEquivalentFingerprints()
    {
        var result = new CompilationPipeline("""
Main :: module {
    export main :: Unit -> Unit
    {
        _ => ()
    }
}
""", new CompilationOptions
        {
            InputFile = "module_mir_state_payload.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Mir,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            UseColors = false
        }).Run();

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var module = Assert.IsType<MirModule>(result.MirModule);

        var payload = ModuleMirStatePayload.Create(module);

        Assert.True(payload.IsRestorable, string.Join(Environment.NewLine, payload.UnsupportedNodeKinds));
        Assert.True(payload.TryRestore(out var restored));
        AssertEquivalentFingerprints(module, restored);
    }

    [Fact]
    public void Create_RestoredSmallCopyRecord_PreservesInlineLlvmValueAbi()
    {
        var result = new CompilationPipeline("""
Main :: module {
    @[derive(Copy)]
    Point :: type { Point :: type(Int, Int) }

    sum :: Point -> Int
    {
        Point(x, y) => x + y
    }
}
""", new CompilationOptions
        {
            InputFile = "Main.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Mir,
            NoImplicitPrelude = false,
            EnableDetailedProfiling = true,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var module = Assert.IsType<MirModule>(result.MirModule);
        Assert.Contains(
            module.ConstructorLayouts,
            entry => module.CopyLikeTypeIds.Contains(entry.Key) &&
                     entry.Value.Any(static layout =>
                         string.Equals(layout.TypeName, "Point", StringComparison.Ordinal)));

        var payload = ModuleMirStatePayload.Create(module);
        var json = JsonSerializer.Serialize(payload);
        var roundTripped = JsonSerializer.Deserialize<ModuleMirStatePayload>(json);

        Assert.NotNull(roundTripped);
        Assert.True(roundTripped!.TryRestore(out var restored));
        var llvmModule = new MirToLlvmConverter().Convert(restored);
        var llvmIr = new LlvmEmitter().Emit(llvmModule);
        var sumDefinition = Assert.Single(
            llvmIr.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries),
            static line =>
                line.StartsWith("define external i64 @", StringComparison.Ordinal) &&
                line.Contains("_Function_u0000_sum_", StringComparison.Ordinal) &&
                !line.Contains("__eidos_prelude_core__", StringComparison.Ordinal));

        Assert.Contains("(%struct.eidos_Point ", sumDefinition, StringComparison.Ordinal);
        Assert.DoesNotContain("(ptr ", sumDefinition, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_WithIncrementalCompilation_EmitsModuleMirStatePayloadsAfterMir()
    {
        var result = new CompilationPipeline("""
Main :: module {
    Box :: type { Box:: type(Int) }

    id :: Int -> Int
    {
        value => value
    }
}
""", new CompilationOptions
        {
            InputFile = "Main.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Mir,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            EnableIncrementalCompilation = true,
            UseColors = false
        }).Run();

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var typed = Assert.IsType<ProjectModuleTypedSemanticSnapshot>(result.ModuleTypedSemanticSnapshot);
        var payloads = Assert.IsAssignableFrom<IReadOnlyList<ModuleMirStateArtifactPayload>>(result.ModuleMirStatePayloads);
        Assert.Equal(typed.Nodes.Count, payloads.Count);

        var typedNode = Assert.Single(typed.Nodes);
        var payload = Assert.Single(payloads, candidate => candidate.ModuleKey == typedNode.ModuleKey);
        Assert.Equal(ModuleMirStateArtifactPayload.CurrentSchemaVersion, payload.SchemaVersion);
        Assert.Equal(typedNode.TypedSemanticHash, payload.TypedSemantic.TypedSemanticHash);
        Assert.True(payload.IsModuleLocal, FormatPayloads(payloads));
        Assert.True(payload.ModuleLocalFunctionCount > 0, FormatPayloads(payloads));
        Assert.True(payload.HasValidPayloadHash());
        Assert.True(payload.MirState.IsRestorable, string.Join(Environment.NewLine, payload.MirState.UnsupportedNodeKinds));
        Assert.True(payload.MirState.TryRestore(out var restored));
        Assert.NotEmpty(restored.Functions);
        Assert.Equal(
            CompilationPipeline.CreateMirModuleFingerprint(result.MirModule!),
            CompilationPipeline.CreateMirModuleFingerprint(restored));
    }

    [Fact]
    public void Run_WithIncrementalCompilation_EmitsDistinctModuleLocalMirPayloads()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_module_mir_payloads_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var entryFile = Path.Combine(tempDir, "Main.eidos");
            var libFile = Path.Combine(tempDir, "Utils.eidos");
            File.WriteAllText(entryFile, """
Main :: module {
    import Utils

    main :: Int -> Int
    {
        value => helper(value)
    }
}
""");
            File.WriteAllText(libFile, """
Utils :: module {
    export helper :: Int -> Int
    {
        value => value
    }
}
""");

            var result = new CompilationPipeline(File.ReadAllText(entryFile), new CompilationOptions
            {
                InputFile = entryFile,
                LanguageVersion = EidosLanguageVersions.Current,
                StopAtPhase = CompilationPhase.Mir,
                NoImplicitPrelude = true,
                EnableDetailedProfiling = true,
                EnableIncrementalCompilation = true,
                UseColors = false
            }).Run();

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            var payloads = Assert.IsAssignableFrom<IReadOnlyList<ModuleMirStateArtifactPayload>>(result.ModuleMirStatePayloads);
            var mainPayload = Assert.Single(payloads, static payload => payload.ModuleKey == "Main");
            var utilsPayload = Assert.Single(payloads, static payload => payload.ModuleKey == "Utils");
            Assert.All(payloads, payload =>
            {
                Assert.True(payload.IsModuleLocal, payload.ModuleKey);
                Assert.True(payload.ModuleLocalFunctionCount > 0, payload.ModuleKey);
                Assert.True(payload.HasValidPayloadHash(), payload.ModuleKey);
                Assert.True(payload.MirState.IsRestorable, payload.ModuleKey);
            });

            Assert.NotEqual(mainPayload.PayloadHash, utilsPayload.PayloadHash);
            Assert.NotEqual(mainPayload.MirState.Hash, utilsPayload.MirState.Hash);

            Assert.True(mainPayload.MirState.TryRestore(out var mainModule));
            Assert.True(utilsPayload.MirState.TryRestore(out var utilsModule));
            Assert.NotEmpty(mainModule.Functions);
            Assert.NotEmpty(utilsModule.Functions);

            var mainFunctionModules = mainModule.Functions
                .Select(static function => function.FunctionId.ModuleIdentityKey)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var utilsFunctionModules = utilsModule.Functions
                .Select(static function => function.FunctionId.ModuleIdentityKey)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            Assert.All(mainFunctionModules, module => Assert.Contains(".Main", module, StringComparison.Ordinal));
            Assert.All(utilsFunctionModules, module => Assert.Contains(".Utils", module, StringComparison.Ordinal));
            Assert.DoesNotContain(mainModule.Functions, static function => function.FunctionId.ModuleIdentityKey.Contains(".Utils", StringComparison.Ordinal));
            Assert.DoesNotContain(utilsModule.Functions, static function => function.FunctionId.ModuleIdentityKey.Contains(".Main", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Create_ModuleWithoutLocalMirFunctions_StoresEmptyMirState()
    {
        var module = CreateAllShapesModule();
        var typed = new ProjectModuleTypedSemanticSnapshot(
            ProjectModuleTypedSemanticSnapshot.CurrentSchemaVersion,
            [
                new ProjectModuleTypedSemanticNode(
                    "Other",
                    [],
                    [],
                    "typed-surface",
                    "typed-deps",
                    "typed-other")
            ]);

        var payload = ModuleMirStateArtifactPayload.Create("Other", typed, module);

        Assert.False(payload.IsModuleLocal);
        Assert.Equal(0, payload.ModuleLocalFunctionCount);
        Assert.True(payload.HasValidPayloadHash());
        Assert.False(payload.MirState.IsRestorable);
        Assert.Empty(payload.MirState.FunctionFingerprints);
        Assert.False(payload.MirState.TryRestore(out var restored));
        Assert.Empty(restored.Functions);
    }

    [Fact]
    public void Create_ReExportedForeignFunction_DoesNotEnterModuleLocalMirState()
    {
        var module = new MirModule
        {
            Functions =
            [
                new MirFunc
                {
                    Name = "Owner__value",
                    SourceName = "value",
                    SymbolId = new SymbolId(42),
                    FunctionId = new FunctionId
                    {
                        SymbolId = new SymbolId(42),
                        Kind = SymbolKind.Function,
                        Module = "Owner",
                        ModuleIdentityKey = "pkg.Owner",
                        Name = "value",
                        QualifiedName = "Owner.value"
                    },
                    ReturnType = new TypeId(BaseTypes.IntId),
                    EntryBlockId = new BlockId { Value = 1 },
                    BasicBlocks =
                    [
                        new MirBasicBlock
                        {
                            Id = new BlockId { Value = 1 },
                            IsEntry = true,
                            Terminator = new MirReturn
                            {
                                Value = new MirConstant
                                {
                                    TypeId = new TypeId(BaseTypes.IntId),
                                    Value = new MirConstantValue.IntValue(1)
                                }
                            }
                        }
                    ]
                }
            ]
        };
        var typed = new ProjectModuleTypedSemanticSnapshot(
            ProjectModuleTypedSemanticSnapshot.CurrentSchemaVersion,
            [
                new ProjectModuleTypedSemanticNode(
                    "ReExport",
                    [],
                    [CreateTypedDeclaration(42)],
                    "typed-surface",
                    "typed-deps",
                    "typed-reexport")
            ]);

        var payload = ModuleMirStateArtifactPayload.Create("ReExport", typed, module);

        Assert.False(payload.IsModuleLocal);
        Assert.Equal(0, payload.ModuleLocalFunctionCount);
        Assert.False(payload.MirState.IsRestorable);
        Assert.Empty(payload.MirState.FunctionFingerprints);
    }

    [Fact]
    public void Create_ModuleKeyCaseDiffersFromFunctionModule_StillCapturesLocalFunctions()
    {
        var module = new MirModule
        {
            Functions =
            [
                new MirFunc
                {
                    Name = "Main__id",
                    SourceName = "id",
                    SymbolId = new SymbolId(42),
                    FunctionId = new FunctionId
                    {
                        SymbolId = new SymbolId(42),
                        Kind = SymbolKind.Function,
                        Module = "Main",
                        ModuleIdentityKey = "pkg.Main",
                        Name = "id",
                        QualifiedName = "Main.id"
                    },
                    ReturnType = new TypeId(BaseTypes.IntId),
                    EntryBlockId = new BlockId { Value = 1 },
                    BasicBlocks =
                    [
                        new MirBasicBlock
                        {
                            Id = new BlockId { Value = 1 },
                            IsEntry = true,
                            Terminator = new MirReturn
                            {
                                Value = new MirConstant
                                {
                                    TypeId = new TypeId(BaseTypes.IntId),
                                    Value = new MirConstantValue.IntValue(1)
                                }
                            }
                        }
                    ]
                }
            ]
        };
        var typed = new ProjectModuleTypedSemanticSnapshot(
            ProjectModuleTypedSemanticSnapshot.CurrentSchemaVersion,
            [
                new ProjectModuleTypedSemanticNode(
                    "main",
                    [],
                    [CreateTypedDeclaration(42)],
                    "typed-surface",
                    "typed-deps",
                    "typed-main")
            ]);

        var payload = ModuleMirStateArtifactPayload.Create("main", typed, module);

        Assert.True(payload.IsModuleLocal);
        Assert.Equal(1, payload.ModuleLocalFunctionCount);
        Assert.True(payload.MirState.IsRestorable, string.Join(Environment.NewLine, payload.MirState.UnsupportedNodeKinds));
        Assert.True(payload.MirState.TryRestore(out var restored));
        Assert.Single(restored.Functions);
    }

    [Fact]
    public void Run_WithPreviousMirPayloads_RestoresMirWithoutRunningMirBuilder()
    {
        const string source = """
Main :: module {
    Box :: type { Box:: type(Int) }

    id :: Int -> Int
    {
        value => value
    }
}
""";
        var first = RunMirRestoreSource(source, options => { });

        Assert.True(first.Success, FormatDiagnostics(first));
        Assert.NotNull(first.ModuleSemanticSignatureSnapshot);
        Assert.NotNull(first.ModuleTypedSemanticSnapshot);
        Assert.NotNull(first.ModuleDependencySignatureSnapshot);
        var payloads = Assert.IsAssignableFrom<IReadOnlyList<ModuleMirStateArtifactPayload>>(first.ModuleMirStatePayloads);
        var payloadByModule = payloads.ToDictionary(static payload => payload.ModuleKey, StringComparer.Ordinal);

        var second = RunMirRestoreSource(source, options =>
        {
            options.PreviousModuleSemanticSignatureSnapshot = first.ModuleSemanticSignatureSnapshot;
            options.PreviousModuleTypedSemanticSnapshot = first.ModuleTypedSemanticSnapshot;
            options.PreviousModuleDependencySignatureSnapshot = first.ModuleDependencySignatureSnapshot;
            options.PreviousModuleMirStatePayloads = payloads;
            options.ModuleArtifactAvailability = (moduleKey, kind, _, _) => kind switch
            {
                ProjectModuleArtifactKinds.SemanticSignature => true,
                ProjectModuleArtifactKinds.TypedSemanticSignature => true,
                ProjectModuleArtifactKinds.MirStatePayload => payloadByModule.ContainsKey(moduleKey),
                _ => false
            };
            options.ModuleMirStatePayloadLoader = (moduleKey, kind, _, _) =>
                kind == ProjectModuleArtifactKinds.MirStatePayload &&
                payloadByModule.TryGetValue(moduleKey, out var payload)
                    ? payload
                    : null;
        });

        Assert.True(second.Success, FormatDiagnostics(second));
        Assert.Equal(1, second.ProfilingCounters["Mir.moduleRestore.applied"]);
        Assert.Equal(1, second.ProfilingCounters["Build.moduleStage.Mir.realTaskExecution"]);
        Assert.Equal(1, second.ProfilingCounters["Build.moduleStage.Mir.restoredModules"]);
        Assert.Equal(0, second.ProfilingCounters["Build.moduleStage.Mir.compiledModules"]);
        Assert.Equal(0, second.ProfilingCounters["Build.moduleStage.Mir.blockedModules"]);
        Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault("Mir.build_mir.calls"));
        Assert.NotNull(second.MirModule);
        Assert.Equal(
            CompilationPipeline.CreateMirModuleFingerprint(first.MirModule!),
            CompilationPipeline.CreateMirModuleFingerprint(second.MirModule!));
    }

    [Fact]
    public void Run_WithPreviousMirPayloads_ForLlvmTarget_RestoresMirBeforeLlvm()
    {
        const string source = """
Main :: module {
    Box :: type { Box:: type(Int) }

    main :: Int -> Int
    {
        value => value
    }
}
""";
        var first = RunMirRestoreSource(source, CompilationPhase.Llvm, options => { });

        Assert.True(first.Success, FormatDiagnostics(first));
        var payloads = Assert.IsAssignableFrom<IReadOnlyList<ModuleMirStateArtifactPayload>>(first.ModuleMirStatePayloads);
        var payloadByModule = payloads.ToDictionary(static payload => payload.ModuleKey, StringComparer.Ordinal);

        var second = RunMirRestoreSource(source, CompilationPhase.Llvm, options =>
        {
            options.PreviousModuleSemanticSignatureSnapshot = first.ModuleSemanticSignatureSnapshot;
            options.PreviousModuleTypedSemanticSnapshot = first.ModuleTypedSemanticSnapshot;
            options.PreviousModuleDependencySignatureSnapshot = first.ModuleDependencySignatureSnapshot;
            options.PreviousModuleMirStatePayloads = payloads;
            options.ModuleArtifactAvailability = (moduleKey, kind, _, _) => kind switch
            {
                ProjectModuleArtifactKinds.SemanticSignature => true,
                ProjectModuleArtifactKinds.TypedSemanticSignature => true,
                ProjectModuleArtifactKinds.MirStatePayload => payloadByModule.ContainsKey(moduleKey),
                _ => false
            };
            options.ModuleMirStatePayloadLoader = (moduleKey, kind, _, _) =>
                kind == ProjectModuleArtifactKinds.MirStatePayload &&
                payloadByModule.TryGetValue(moduleKey, out var payload)
                    ? payload
                    : null;
        });

        Assert.True(second.Success, FormatDiagnostics(second));
        Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault("Mir.moduleRestore.applied"));
        Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault("Build.moduleStage.Mir.realTaskExecution"));
        Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault("Build.moduleStage.Mir.restoredModules"));
        Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault("Build.moduleStage.Mir.compiledModules"));
        Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault("Build.moduleStage.Mir.blockedModules"));
        Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault("Mir.build.output.functions"));
        Assert.NotNull(second.LlvmIrText);
        Assert.Equal(first.LlvmIrText, second.LlvmIrText);
    }

    private static MirModule CreateAllShapesModule()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var local = new MirPlace
        {
            Kind = PlaceKind.Local,
            Local = new LocalId { Value = 1 },
            TypeId = intType,
            Span = Span(1)
        };
        var field = new MirPlace
        {
            Kind = PlaceKind.Field,
            Base = local,
            FieldName = "value",
            TypeId = intType,
            Span = Span(2)
        };
        var index = new MirPlace
        {
            Kind = PlaceKind.Index,
            Base = local,
            Index = ConstInt(0, intType),
            IndexAccessKind = MirIndexAccessKind.RuntimeArray,
            TypeId = intType,
            Span = Span(3)
        };
        var deref = new MirPlace
        {
            Kind = PlaceKind.Deref,
            Base = field,
            TypeId = intType,
            Span = Span(4)
        };
        var temp = new MirTemp
        {
            Id = new TempId { Value = 7 },
            TypeId = intType,
            Span = Span(5)
        };
        var functionId = new FunctionId
        {
            SymbolId = new SymbolId(101),
            Kind = SymbolKind.Function,
            Module = "Main",
            ModuleIdentityKey = "pkg:main.Main",
            Name = "callee",
            QualifiedName = "Main.callee",
            MangledName = "eidos_Main__callee"
        };
        var functionRef = new MirFunctionRef
        {
            SymbolId = new SymbolId(101),
            Name = "callee",
            SymbolKind = SymbolKind.Function,
            FunctionId = functionId,
            TypeId = intType,
            SignatureTypeId = new TypeId(600),
            TypeArgumentIds = [new TypeId(601), new TypeId(602)],
            TraitOwnerId = new SymbolId(701),
            TraitSelfPosition = SelfPosition.Both,
            TraitSelfParameterIndices = [0, 2],
            TraitSelfInResult = true,
            CompilerSemanticRole = CompilerSemanticRole.SequenceMap,
            Span = Span(6)
        };

        return new MirModule
        {
            Name = "all_shapes",
            PackageAlias = "Pkg",
            PackageInstanceKey = "pkg-key",
            Path = ["Pkg", "AllShapes"],
            Span = Span(10),
            DynamicTypeKeys =
            {
                [900] = "dyn:tuple",
                [901] = "dyn:function"
            },
            TypeDescriptors =
            {
                [1] = new TypeDescriptor.Builtin(1),
                [2] = new TypeDescriptor.Function([new TypeId(10), new TypeId(11)], new TypeId(12), "IO"),
                [3] = new TypeDescriptor.Tuple([new TypeId(13), new TypeId(14)]),
                [4] = new TypeDescriptor.TyCon(new TypeConstructorKey(TypeConstructorKeyKind.Symbol, 15), [new TypeId(16)])
                {
                    ValueArgs =
                    [
                        new GenericValueArgumentDescriptor(
                            0,
                            "typed:496e74:int:8",
                            "hash-8",
                            "8",
                            new TypeId(BaseTypes.IntId),
                            ReferencedParameterIndex: 0,
                            ValueVariableIndex: 9)
                    ],
                    EffectArgs =
                    [
                        new GenericEffectArgumentDescriptor(1, "symbol:89", new TypeId(89))
                    ]
                },
                [5] = new TypeDescriptor.Ref(new TypeId(17)),
                [6] = new TypeDescriptor.MutRef(new TypeId(18)),
                [7] = new TypeDescriptor.Shared(new TypeId(19)),
                [9] = new TypeDescriptor.TypeVar(0)
            },
            LinkLibraries = ["c", "m"],
            CStructAccessors =
            {
                ["point_x"] = new CStructAccessorInfo { FieldOffset = 8, FieldTypeId = intType.Value, IsGetter = true }
            },
            ConstructorLayouts =
            {
                [1000] =
                [
                    new ConstructorTypeLayout
                    {
                        TypeName = "Option_Int",
                        ConstructorName = "Some",
                        TagValue = 1,
                        RuntimeTypeId = 77,
                        FieldTypeIds = [intType]
                    }
                ]
            },
            TraitImpls =
            [
                new ImplSymbol
                {
                    Id = new SymbolId(801),
                    Name = "impl Show for Box[Int]",
                    Span = Span(11),
                    IsTypeResolved = true,
                    IsModuleLevel = true,
                    IsPublic = true,
                    TypeId = new TypeId(802),
                    Trait = new SymbolId(803),
                    ImplementingType = new TypeId(804),
                    CanonicalImplementingType = "Box[Int]",
                    ImplementingTypeDisplay = "Box[Int]",
                    ImplementingTypeKey = new ImplTypeRefKey(new SymbolId(805), new TypeId(804), "Box", [ImplTypeRefKey.FromText("Int")]),
                    Methods = [new SymbolId(806)],
                    TraitMethodImplementations = { [new SymbolId(807)] = new SymbolId(806) },
                    TraitTypeArgs = ["Int"],
                    TraitTypeArgKeys = [ImplTypeRefKey.FromText("Int")],
                    CanonicalTraitTypeArgs = ["Int"],
                    CanonicalTraitTypeArgKeys = [ImplTypeRefKey.FromCanonicalText("Int")],
                    TraitTypeArgShapes =
                    [
                        new ImplConstructorShapeNode("Box", [new ImplVariableShapeNode("T")]) { SymbolId = new SymbolId(808), TypeId = new TypeId(809) },
                        new ImplTupleShapeNode([ImplWildcardShapeNode.Instance, new ImplVariableShapeNode("U")]),
                        new ImplArrowShapeNode(new ImplVariableShapeNode("A"), new ImplVariableShapeNode("B")),
                        new ImplEffectfulShapeNode(new ImplVariableShapeNode("Input"), ["Console"], new ImplVariableShapeNode("Output"))
                    ],
                    ImplementingTypeShape = new ImplConstructorShapeNode("Box", [new ImplVariableShapeNode("T")]),
                    TypeArguments = { [new TypeId(810)] = new TypeId(811) },
                    ImplementingTypeRequirements =
                    [
                        new ImplTypeArgTraitRequirement
                        {
                            TypeArgIndex = 0,
                            Trait = new SymbolId(812),
                            TraitName = "Show",
                            TraitTypeArgs = ["Int"],
                            TraitTypeArgKeys = [ImplTypeRefKey.FromText("Int")]
                        }
                    ],
                    IsAutoDerived = true
                }
            ],
            TraitInfos =
            [
                new MirTraitInfo
                {
                    TraitId = new SymbolId(820),
                    TypeParameterCount = 1,
                    TypeParameterIds = [new SymbolId(821)],
                    SelfPosition = SelfPosition.Both,
                    HasMethodDispatchMetadata = true,
                    ParentTraits = [new SymbolId(822)],
                    Methods =
                    [
                        new MirTraitMethodInfo
                        {
                            TraitId = new SymbolId(820),
                            MethodId = new SymbolId(823),
                            Name = "eq",
                            SelfPosition = SelfPosition.Both,
                            SelfParameterIndices = [0, 1],
                            SelfInResult = true,
                            MethodRole = CompilerSemanticRole.Equality,
                            HasDefaultImplementation = true
                        }
                    ]
                }
            ],
            TypeAliases =
            [
                new MirTypeAliasInfo
                {
                    AliasId = new SymbolId(830),
                    Name = "Alias",
                    TypeId = new TypeId(831),
                    AliasTarget = new TypeId(832),
                    TypeParameterIds = [new SymbolId(833)]
                }
            ],
            TypeConstructors =
            [
                new MirTypeConstructorInfo
                {
                    SymbolId = new SymbolId(840),
                    Name = "Box",
                    TypeId = new TypeId(841),
                    TypeParameterIds = [new SymbolId(842)]
                }
            ],
            SpecializationFailures =
            [
                new MirSpecializationFailureInfo
                {
                    Reason = "recursive",
                    TemplateKey = "sym:101",
                    TemplateName = "callee",
                    SignatureKey = "Int",
                    SignatureDisplay = "Int",
                    PreviewName = "callee__Int"
                }
            ],
            Functions =
            [
                new MirFunc
                {
                    Name = "main",
                    SourceName = "main",
                    SymbolId = new SymbolId(100),
                    FunctionId = new FunctionId
                    {
                        SymbolId = new SymbolId(100),
                        Kind = SymbolKind.Function,
                        Module = "Main",
                        ModuleIdentityKey = "pkg:main.Main",
                        Name = "main",
                        QualifiedName = "Main.main",
                        MangledName = "eidos_Main__main"
                    },
                    ReturnType = intType,
                    GenericParameterCount = 2,
                    GenericParameters =
                    [
                        new MirGenericParameter
                        {
                            ParameterIndex = 0,
                            SymbolId = new SymbolId(501),
                            Name = "T",
                            ParameterKind = GenericParameterKind.Type,
                            TypeId = new TypeId(501)
                        },
                        new MirGenericParameter
                        {
                            ParameterIndex = 1,
                            SymbolId = new SymbolId(502),
                            Name = "N",
                            ParameterKind = GenericParameterKind.Value,
                            TypeId = intType
                        }
                    ],
                    GenericTypeParameterIds = [new TypeId(501)],
                    IsRuntimeWordAbi = true,
                    IsEntry = true,
                    IsExternal = true,
                    ExternalSymbolName = "main_c",
                    ExternalLibrary = "c",
                    IntrinsicName = "intrinsic.main",
                    BuiltinIntrinsicRole = BuiltinIntrinsicRole.SharedClone,
                    EntryBlockId = new BlockId { Value = 1 },
                    TraitInvokeHelper = TraitInvokeHelperKind.EqValue,
                    TraitInvokeHelperTraitId = new SymbolId(701),
                    Span = Span(20),
                    Locals =
                    [
                        new MirLocal
                        {
                            Id = new LocalId { Value = 1 },
                            Name = "x",
                            TypeId = intType,
                            IsMutable = true,
                            IsParameter = true,
                            BindingMode = PatternBindingMode.MutableBorrow,
                            Span = Span(21)
                        }
                    ],
                    BasicBlocks =
                    [
                        new MirBasicBlock
                        {
                            Id = new BlockId { Value = 1 },
                            IsEntry = true,
                            Span = Span(30),
                            Instructions =
                            [
                                new MirAssign { Target = local, Source = ConstInt(42, intType), Span = Span(31) },
                                new MirCaseInject
                                {
                                    Target = local,
                                    Operand = ConstInt(1, intType),
                                    SourceCase = new SymbolId(901),
                                    TargetAncestor = new SymbolId(902),
                                    SourceTypeId = new TypeId(903),
                                    TargetTypeId = intType,
                                    Span = Span(31)
                                },
                                new MirAssign { Target = local, Source = ConstChar('x'), Span = Span(31) },
                                new MirAssign { Target = local, Source = ConstUnit(unitType), Span = Span(31) },
                                new MirAssign
                                {
                                    Target = local,
                                    Source = new MirConstGenericValue
                                    {
                                        SymbolId = new SymbolId(502),
                                        Name = "N",
                                        ParameterIndex = 1,
                                        TypeId = intType,
                                        Span = Span(31)
                                    },
                                    Span = Span(31)
                                },
                                new MirCall { Target = field, Function = functionRef, Arguments = [ConstString("arg"), temp, deref], IsTailCall = true, BorrowedArgumentIndices = new HashSet<int> { 0, 2 }, Span = Span(32) },
                                new MirBinOp { Target = temp, Operator = BinaryOp.Add, Left = ConstInt(1, intType), Right = ConstInt(2, intType), Span = Span(38) },
                                new MirUnaryOp { Target = temp, Operator = UnaryOp.Neg, Operand = ConstInt(3, intType), Span = Span(39) },
                                new MirSelect { Target = local, Condition = ConstBool(true, boolType), TrueValue = ConstInt(4, intType), FalseValue = ConstInt(5, intType), Span = Span(39) },
                                new MirLoad { Target = local, Source = index, IsMutableBorrow = true, CreatesBorrowAlias = false, MovesOutOfSource = true, Span = Span(40) },
                                new MirStore { Target = local, Value = ConstFloat(3.5), Span = Span(41) },
                                new MirDrop { Value = new MirPoison { Reason = "synthetic", TypeId = unitType, Span = Span(42) }, Span = Span(43) },
                                new MirCopy { Target = local, Source = field, Span = Span(44) },
                                new MirMove { Target = field, Source = local, Span = Span(45) },
                                new MirAlloc { Target = local, TypeId = intType, Span = Span(46) }
                            ],
                            Terminator = new MirSwitch
                            {
                                Discriminant = ConstBool(true, boolType),
                                Branches =
                                [
                                    new MirSwitchBranch
                                    {
                                        Value = ConstBool(true, boolType),
                                        Target = new BlockId { Value = 2 },
                                        BoundVariable = new LocalId { Value = 1 }
                                    }
                                ],
                                DefaultTarget = new BlockId { Value = 3 },
                                DecisionPlan = new MirDecisionPlan(
                                    "Match",
                                    2,
                                    HasGuards: true,
                                    HasBindings: true,
                                    IsExhaustive: false),
                                Span = Span(47)
                            }
                        },
                        new MirBasicBlock
                        {
                            Id = new BlockId { Value = 2 },
                            Span = Span(50),
                            Terminator = new MirGoto { Target = new BlockId { Value = 4 }, Span = Span(51) }
                        },
                        new MirBasicBlock
                        {
                            Id = new BlockId { Value = 3 },
                            Span = Span(52),
                            Terminator = new MirUnreachable { Span = Span(53) }
                        },
                        new MirBasicBlock
                        {
                            Id = new BlockId { Value = 4 },
                            Span = Span(54),
                            Terminator = new MirReturn { Value = ConstRawString("done"), Span = Span(55) }
                        }
                    ]
                }
            ]
        };
    }

    private static MirConstant ConstInt(long value, TypeId typeId) =>
        new()
        {
            TypeId = typeId,
            Value = new MirConstantValue.IntValue(value),
            Span = Span(100)
        };

    private static MirConstant ConstFloat(double value) =>
        new()
        {
            TypeId = new TypeId(BaseTypes.FloatId),
            Value = new MirConstantValue.FloatValue(value),
            Span = Span(101)
        };

    private static MirConstant ConstString(string value) =>
        new()
        {
            TypeId = new TypeId(BaseTypes.StringId),
            Value = new MirConstantValue.StringValue(value),
            Span = Span(102)
        };

    private static MirConstant ConstRawString(string value) =>
        new()
        {
            TypeId = new TypeId(BaseTypes.StringId),
            Value = new MirConstantValue.RawStringValue(value),
            Span = Span(103)
        };

    private static MirConstant ConstChar(char value) =>
        new()
        {
            TypeId = new TypeId(BaseTypes.CharId),
            Value = new MirConstantValue.CharValue(value),
            Span = Span(104)
        };

    private static MirConstant ConstBool(bool value, TypeId typeId) =>
        new()
        {
            TypeId = typeId,
            Value = new MirConstantValue.BoolValue(value),
            Span = Span(105)
        };

    private static MirConstant ConstUnit(TypeId typeId) =>
        new()
        {
            TypeId = typeId,
            Value = new MirConstantValue.UnitValue(),
            Span = Span(106)
        };

    private static SourceSpan Span(int position) =>
        new(new SourceLocation(position, 1, position + 1, "module_mir_state_payload.eidos"), 1);

    private static ProjectModuleTypedSemanticDeclaration CreateTypedDeclaration(int symbolId) =>
        new(
            "Function",
            "value",
            "Owner.Function:value",
            "Int",
            symbolId,
            BaseTypes.IntId,
            IsPublic: true,
            [],
            $"decl-{symbolId}");

    private static CompilationResult RunMirRestoreSource(
        string source,
        CompilationPhase stopAtPhase,
        Action<CompilationOptions> configure)
    {
        var options = new CompilationOptions
        {
            InputFile = "Main.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = stopAtPhase,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            EnableIncrementalCompilation = true,
            UseColors = false
        };
        configure(options);
        return new CompilationPipeline(source, options).Run();
    }

    private static CompilationResult RunMirRestoreSource(
        string source,
        Action<CompilationOptions> configure) =>
        RunMirRestoreSource(source, CompilationPhase.Mir, configure);

    private static string FormatDiagnostics(CompilationResult result)
    {
        return string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
    }

    private static string FormatCounters(CompilationResult result)
    {
        return string.Join(
            Environment.NewLine,
            result.ProfilingCounters
                .Where(static counter => counter.Key.Contains("Mir.", StringComparison.Ordinal) ||
                                         counter.Key.Contains("moduleStage.Mir", StringComparison.Ordinal))
                .OrderBy(static counter => counter.Key, StringComparer.Ordinal)
                .Select(static counter => $"{counter.Key}={counter.Value}"));
    }

    private static string FormatPayloads(IReadOnlyList<ModuleMirStateArtifactPayload> payloads) =>
        string.Join(
            Environment.NewLine,
            payloads.Select(static payload =>
                $"{payload.ModuleKey}: local={payload.IsModuleLocal}, count={payload.ModuleLocalFunctionCount}, restorable={payload.MirState.IsRestorable}"));

    private static void AssertEquivalentFingerprints(MirModule expected, MirModule actual)
    {
        Assert.Equal(
            CompilationPipeline.CreateMirModuleFingerprint(expected),
            CompilationPipeline.CreateMirModuleFingerprint(actual));

        var expectedFunctions = MirFunctionFingerprintSnapshot.FromModule(expected);
        var actualFunctions = MirFunctionFingerprintSnapshot.FromModule(actual);
        Assert.Equal(expectedFunctions.ModuleFingerprint, actualFunctions.ModuleFingerprint);
        Assert.Equal(expectedFunctions.Functions, actualFunctions.Functions);
    }

    private static void AssertConcreteCoverage(ReflectionType baseType, IReadOnlySet<ReflectionType> actualTypes)
    {
        var missing = baseType.Assembly
            .GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && baseType.IsAssignableFrom(type))
            .Except(actualTypes)
            .Select(static type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(missing);
    }

    private static HashSet<ReflectionType> CollectMirObjectTypes(MirModule module)
    {
        var types = new HashSet<ReflectionType>();
        foreach (var function in module.Functions)
        {
            foreach (var block in function.BasicBlocks)
            {
                foreach (var instruction in block.Instructions)
                {
                    types.Add(instruction.GetType());
                    CollectInstructionOperandTypes(instruction, types);
                }

                if (block.Terminator != null)
                {
                    types.Add(block.Terminator.GetType());
                    CollectTerminatorOperandTypes(block.Terminator, types);
                }
            }
        }

        return types;
    }

    private static void CollectInstructionOperandTypes(MirInstruction instruction, HashSet<ReflectionType> types)
    {
        switch (instruction)
        {
            case MirAssign assign:
                CollectOperandTypes(assign.Target, types);
                CollectOperandTypes(assign.Source, types);
                break;
            case MirCaseInject injection:
                CollectOperandTypes(injection.Target, types);
                CollectOperandTypes(injection.Operand, types);
                break;
            case MirCall call:
                CollectOperandTypes(call.Target, types);
                CollectOperandTypes(call.Function, types);
                CollectOperandTypes(call.Arguments, types);
                break;
            case MirBinOp binOp:
                CollectOperandTypes(binOp.Target, types);
                CollectOperandTypes(binOp.Left, types);
                CollectOperandTypes(binOp.Right, types);
                break;
            case MirUnaryOp unaryOp:
                CollectOperandTypes(unaryOp.Target, types);
                CollectOperandTypes(unaryOp.Operand, types);
                break;
            case MirSelect select:
                CollectOperandTypes(select.Target, types);
                CollectOperandTypes(select.Condition, types);
                CollectOperandTypes(select.TrueValue, types);
                CollectOperandTypes(select.FalseValue, types);
                break;
            case MirLoad load:
                CollectOperandTypes(load.Target, types);
                CollectOperandTypes(load.Source, types);
                break;
            case MirStore store:
                CollectOperandTypes(store.Target, types);
                CollectOperandTypes(store.Value, types);
                break;
            case MirDrop drop:
                CollectOperandTypes(drop.Value, types);
                break;
            case MirCopy copy:
                CollectOperandTypes(copy.Target, types);
                CollectOperandTypes(copy.Source, types);
                break;
            case MirMove move:
                CollectOperandTypes(move.Target, types);
                CollectOperandTypes(move.Source, types);
                break;
            case MirAlloc alloc:
                CollectOperandTypes(alloc.Target, types);
                break;
        }
    }

    private static void CollectTerminatorOperandTypes(MirTerminator terminator, HashSet<ReflectionType> types)
    {
        switch (terminator)
        {
            case MirReturn ret:
                CollectOperandTypes(ret.Value, types);
                break;
            case MirSwitch @switch:
                CollectOperandTypes(@switch.Discriminant, types);
                foreach (var branch in @switch.Branches)
                {
                    CollectOperandTypes(branch.Value, types);
                }

                break;
        }
    }

    private static void CollectOperandTypes(IEnumerable<MirOperand> operands, HashSet<ReflectionType> types)
    {
        foreach (var operand in operands)
        {
            CollectOperandTypes(operand, types);
        }
    }

    private static void CollectOperandTypes(MirOperand? operand, HashSet<ReflectionType> types)
    {
        if (operand == null)
        {
            return;
        }

        types.Add(operand.GetType());
        if (operand is MirConstant constant)
        {
            types.Add(constant.Value.GetType());
        }

        if (operand is MirPlace place)
        {
            CollectOperandTypes(place.Base, types);
            CollectOperandTypes(place.Index, types);
        }
    }
}
