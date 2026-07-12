using Eidosc.Symbols;
using Eidosc;
using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Eidosc.Types;
using Xunit;
using EidosDiagnostic = Eidosc.Diagnostic.Diagnostic;
using EidosDiagnosticLevel = Eidosc.Diagnostic.DiagnosticLevel;

namespace Eidosc.Tests.Unit.Pipeline;

public sealed class SpecializationLoopDiagnosticsTests
{
    [Fact]
    public void CollectSpecializerDiagnostics_PreservesDiagnosticsAcrossClearedRuns()
    {
        var specializer = new MirGenericSpecializer();
        var diagnostics = new List<EidosDiagnostic>();
        var diagnosticKeys = new HashSet<string>(StringComparer.Ordinal);

        specializer.Diagnostics.Add(EidosDiagnostic.Warning("generic specialization was rejected", "E5310"));
        CompilationPipeline.CollectSpecializerDiagnostics(specializer, diagnostics, diagnosticKeys);

        specializer.Diagnostics.Clear();
        specializer.Diagnostics.Add(EidosDiagnostic.Warning("generic specialization was rejected", "E5310"));
        CompilationPipeline.CollectSpecializerDiagnostics(specializer, diagnostics, diagnosticKeys);

        Assert.Single(diagnostics, diagnostic => diagnostic.Code == "E5310");
    }

    [Fact]
    public void CollectSpecializerDiagnostics_PreservesSameMessageWithDifferentMetadata()
    {
        var specializer = new MirGenericSpecializer();
        var diagnostics = new List<EidosDiagnostic>();
        var diagnosticKeys = new HashSet<string>(StringComparer.Ordinal);

        specializer.Diagnostics.Add(
            EidosDiagnostic.Warning("generic specialization was rejected", "E5310")
                .WithMetadata("templateKey", "trait:1:map")
                .WithMetadata("signatureKey", "T10|T11"));
        specializer.Diagnostics.Add(
            EidosDiagnostic.Warning("generic specialization was rejected", "E5310")
                .WithMetadata("templateKey", "trait:1:map")
                .WithMetadata("signatureKey", "T20|T21"));
        CompilationPipeline.CollectSpecializerDiagnostics(specializer, diagnostics, diagnosticKeys);

        specializer.Diagnostics.Clear();
        specializer.Diagnostics.Add(
            EidosDiagnostic.Warning("generic specialization was rejected", "E5310")
                .WithMetadata("signatureKey", "T10|T11")
                .WithMetadata("templateKey", "trait:1:map"));
        CompilationPipeline.CollectSpecializerDiagnostics(specializer, diagnostics, diagnosticKeys);

        Assert.Equal(2, diagnostics.Count(diagnostic => diagnostic.Code == "E5310"));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Metadata["signatureKey"] == "T10|T11");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Metadata["signatureKey"] == "T20|T21");
    }

    [Fact]
    public void RunSpecializationLoop_NonGenericModuleWithoutOptimizer_RunsSpecializerOnce()
    {
        var unitType = new TypeId(BaseTypes.UnitId);
        var module = new MirModule
        {
            Name = "non_generic",
            Functions =
            [
                new MirFunc
                {
                    Name = "main",
                    ReturnType = unitType,
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
                                    TypeId = unitType,
                                    Value = new MirConstantValue.UnitValue()
                                }
                            }
                        }
                    ]
                }
            ]
        };

        var result = CompilationPipeline.RunSpecializationLoop(
            module,
            new MirGenericSpecializer(),
            optimizer: null);

        Assert.Equal(1, result.SpecializerRunCount);
        Assert.Equal(0, result.SpecializerChangedIterationCount);
        Assert.Equal(0, result.OptimizerChangedIterationCount);
        Assert.Equal("fixed-point", result.ConvergenceReason);
    }

    [Fact]
    public void RunSpecializationLoop_NonGenericModuleWithNoOpOptimizer_RunsSpecializerOnce()
    {
        var module = CreateIntReturnModule(1);
        var optimizer = new MirOptimizer();
        optimizer.RegisterPass(new NoOpOptimizationPass());

        var result = CompilationPipeline.RunSpecializationLoop(
            module,
            new MirGenericSpecializer(),
            optimizer);

        Assert.Equal(1, result.SpecializerRunCount);
        Assert.Equal(0, result.SpecializerChangedIterationCount);
        Assert.Equal(0, result.OptimizerChangedIterationCount);
        Assert.Equal("fixed-point", result.ConvergenceReason);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void RunSpecializationLoop_NonGenericModuleWithDefaultOptimizer_RunsSpecializerOnce()
    {
        var module = CreateIntReturnModule(1);
        var optimizer = MirOptimizer.CreateDefault();

        var result = CompilationPipeline.RunSpecializationLoop(
            module,
            new MirGenericSpecializer(),
            optimizer);

        Assert.Equal(1, result.SpecializerRunCount);
        Assert.Equal(0, result.SpecializerChangedIterationCount);
        Assert.Equal(0, result.OptimizerChangedIterationCount);
        Assert.Equal("fixed-point", result.ConvergenceReason);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void RunSpecializationLoop_OneShotLocalOptimizerRewrite_DoesNotRerunSpecializer()
    {
        var module = CreateIntReturnModule(1);
        var optimizer = new MirOptimizer();
        optimizer.RegisterPass(new OneShotConstantRewriteOptimizationPass());

        var result = CompilationPipeline.RunSpecializationLoop(
            module,
            new MirGenericSpecializer(),
            optimizer);

        Assert.Equal(1, result.SpecializerRunCount);
        Assert.Equal(0, result.SpecializerChangedIterationCount);
        Assert.Equal(1, result.OptimizerChangedIterationCount);
        Assert.Equal("dirty-worklist-local-optimizer", result.ConvergenceReason);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void RunSpecializationLoop_WhenFingerprintDoesNotConverge_ReportsDiagnostic()
    {
        var module = CreateSingleCallModule(new SymbolId(101), "callee_101");
        var optimizer = new MirOptimizer();
        optimizer.RegisterPass(new NonConvergingCallGraphOptimizationPass());

        var result = CompilationPipeline.RunSpecializationLoop(
            module,
            new MirGenericSpecializer(),
            optimizer);

        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "E5311");
        Assert.Equal(EidosDiagnosticLevel.Error, diagnostic.Level);
        Assert.Equal(10, result.SpecializerRunCount);
        Assert.Equal(0, result.SpecializerChangedIterationCount);
        Assert.Equal(10, result.OptimizerChangedIterationCount);
        Assert.Equal("max-iterations", result.ConvergenceReason);
        Assert.Equal("10", diagnostic.Metadata["maxIterations"]);
        Assert.Equal("10", diagnostic.Metadata["specializerRunCount"]);
        Assert.Equal("0", diagnostic.Metadata["specializerChangedIterationCount"]);
        Assert.Equal("10", diagnostic.Metadata["optimizerChangedIterationCount"]);
        Assert.Equal("specialization-loop-not-converged", diagnostic.Metadata["reason"]);
    }

    [Fact]
    public void CreateMirModuleFingerprint_FunctionRefChangedWithoutFunctionCountChange_ChangesFingerprint()
    {
        var first = CreateSingleCallModule(new SymbolId(101), "trait_method");
        var second = CreateSingleCallModule(new SymbolId(202), "impl_method");

        var firstFingerprint = CompilationPipeline.CreateMirModuleFingerprint(first);
        var secondFingerprint = CompilationPipeline.CreateMirModuleFingerprint(second);

        Assert.NotEqual(firstFingerprint, secondFingerprint);
        Assert.Equal(first.Functions.Count, second.Functions.Count);
    }

    [Fact]
    public void CreateMirModuleFingerprint_TraitImplStructuredMetadataChanged_ChangesFingerprint()
    {
        var first = CreateTraitImplMetadataModule(
            new ImplTypeRefKey(SymbolId.None, new TypeId(BaseTypes.IntId), "Int", []));
        var second = CreateTraitImplMetadataModule(
            new ImplTypeRefKey(SymbolId.None, new TypeId(BaseTypes.StringId), "String", []));

        var firstFingerprint = CompilationPipeline.CreateMirModuleFingerprint(first);
        var secondFingerprint = CompilationPipeline.CreateMirModuleFingerprint(second);

        Assert.NotEqual(firstFingerprint, secondFingerprint);
    }

    [Fact]
    public void CreateMirModuleFingerprint_ImplTypeRefKeySameTypeIdDifferentSymbol_IsStable()
    {
        var first = CreateTraitImplMetadataModule(
            new ImplTypeRefKey(new SymbolId(401), new TypeId(501), "AliasBox", []));
        var second = CreateTraitImplMetadataModule(
            new ImplTypeRefKey(new SymbolId(402), new TypeId(501), "Box", []));

        var firstFingerprint = CompilationPipeline.CreateMirModuleFingerprint(first);
        var secondFingerprint = CompilationPipeline.CreateMirModuleFingerprint(second);

        Assert.Equal(firstFingerprint, secondFingerprint);
    }

    [Fact]
    public void CreateMirModuleFingerprint_ImplShapeSameTypeIdDifferentSymbol_IsStable()
    {
        var first = CreateTraitImplShapeMetadataModule(
            new ImplConstructorShapeNode("AliasBox", [])
            {
                SymbolId = new SymbolId(401),
                TypeId = new TypeId(501)
            });
        var second = CreateTraitImplShapeMetadataModule(
            new ImplConstructorShapeNode("Box", [])
            {
                SymbolId = new SymbolId(402),
                TypeId = new TypeId(501)
            });

        var firstFingerprint = CompilationPipeline.CreateMirModuleFingerprint(first);
        var secondFingerprint = CompilationPipeline.CreateMirModuleFingerprint(second);

        Assert.Equal(firstFingerprint, secondFingerprint);
    }

    [Fact]
    public void CreateMirModuleFingerprint_StructuredImplMetadataIgnoresDisplayTextNoise()
    {
        var key = new ImplTypeRefKey(new SymbolId(401), new TypeId(501), "Box", []);
        var traitArgKey = new ImplTypeRefKey(SymbolId.None, new TypeId(BaseTypes.IntId), "Int", []);
        var first = CreateTraitImplDisplayMetadataModule(
            key,
            traitArgKey,
            implementingDisplay: "AliasBox[Int]",
            canonicalImplementingType: "Box[Int]",
            traitArgDisplay: "AliasInt",
            canonicalTraitArgDisplay: "Int");
        var second = CreateTraitImplDisplayMetadataModule(
            key,
            traitArgKey,
            implementingDisplay: "RenamedAlias[Int]",
            canonicalImplementingType: "CanonicalNoise[Int]",
            traitArgDisplay: "RenamedInt",
            canonicalTraitArgDisplay: "CanonicalIntNoise");

        var firstFingerprint = CompilationPipeline.CreateMirModuleFingerprint(first);
        var secondFingerprint = CompilationPipeline.CreateMirModuleFingerprint(second);

        Assert.Equal(firstFingerprint, secondFingerprint);
    }

    private static MirModule CreateSingleCallModule(SymbolId calleeSymbolId, string calleeName)
    {
        var unitType = new TypeId(BaseTypes.UnitId);
        var target = new MirPlace
        {
            Kind = PlaceKind.Local,
            Local = new LocalId { Value = 1 },
            TypeId = unitType
        };

        return new MirModule
        {
            Name = "same_count",
            Functions =
            [
                new MirFunc
                {
                    Name = "main",
                    ReturnType = unitType,
                    EntryBlockId = new BlockId { Value = 1 },
                    Locals =
                    [
                        new MirLocal
                        {
                            Id = new LocalId { Value = 1 },
                            Name = "result",
                            TypeId = unitType
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
                                new MirCall
                                {
                                    Target = target,
                                    Function = new MirFunctionRef
                                    {
                                        SymbolId = calleeSymbolId,
                                        Name = calleeName,
                                        TypeId = unitType,
                                        SignatureTypeId = unitType
                                    },
                                    Arguments = []
                                }
                            ],
                            Terminator = new MirReturn
                            {
                                Value = target
                            }
                        }
                    ]
                }
            ]
        };
    }

    private static MirModule CreateTraitImplMetadataModule(ImplTypeRefKey traitTypeArgKey)
    {
        return new MirModule
        {
            Name = "impl_metadata_fingerprint",
            TraitImpls =
            [
                new ImplSymbol
                {
                    Id = new SymbolId(301),
                    Name = "impl Functor[Int] for Carrier",
                    Trait = new SymbolId(302),
                    ImplementingType = new TypeId(303),
                    ImplementingTypeKey = new ImplTypeRefKey(new SymbolId(304), new TypeId(303), "Carrier", []),
                    TraitTypeArgKeys = [traitTypeArgKey],
                    Methods = [new SymbolId(305)]
                }
            ]
        };
    }

    private static MirModule CreateTraitImplShapeMetadataModule(ImplTypeShapeNode traitTypeArgShape)
    {
        return new MirModule
        {
            Name = "impl_shape_metadata_fingerprint",
            TraitImpls =
            [
                new ImplSymbol
                {
                    Id = new SymbolId(301),
                    Name = "impl Functor[Box] for Carrier",
                    Trait = new SymbolId(302),
                    ImplementingType = new TypeId(303),
                    ImplementingTypeKey = new ImplTypeRefKey(new SymbolId(304), new TypeId(303), "Carrier", []),
                    TraitTypeArgShapes = [traitTypeArgShape],
                    Methods = [new SymbolId(305)]
                }
            ]
        };
    }

    private static MirModule CreateTraitImplDisplayMetadataModule(
        ImplTypeRefKey implementingTypeKey,
        ImplTypeRefKey traitTypeArgKey,
        string implementingDisplay,
        string canonicalImplementingType,
        string traitArgDisplay,
        string canonicalTraitArgDisplay)
    {
        return new MirModule
        {
            Name = "impl_display_metadata_fingerprint",
            TraitImpls =
            [
                new ImplSymbol
                {
                    Id = new SymbolId(301),
                    Name = "impl Functor[Int] for Carrier",
                    Trait = new SymbolId(302),
                    ImplementingType = new TypeId(303),
                    ImplementingTypeDisplay = implementingDisplay,
                    CanonicalImplementingType = canonicalImplementingType,
                    ImplementingTypeKey = implementingTypeKey,
                    TraitTypeArgs = [traitArgDisplay],
                    TraitTypeArgKeys = [traitTypeArgKey],
                    CanonicalTraitTypeArgs = [canonicalTraitArgDisplay],
                    CanonicalTraitTypeArgKeys = [traitTypeArgKey],
                    Methods = [new SymbolId(305)]
                }
            ]
        };
    }

    private static MirModule CreateIntReturnModule(int value)
    {
        var intType = new TypeId(BaseTypes.IntId);
        return new MirModule
        {
            Name = "int_return",
            Functions =
            [
                new MirFunc
                {
                    Name = "main",
                    ReturnType = intType,
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
                                    TypeId = intType,
                                    Value = new MirConstantValue.IntValue(value)
                                }
                            }
                        }
                    ]
                }
            ]
        };
    }

    private sealed class NoOpOptimizationPass : IMirOptimizationPass
    {
        public string Name => "no-op-test-pass";

        public MirModule Run(MirModule module) => module;
    }

    private sealed class OneShotConstantRewriteOptimizationPass : IMirOptimizationPass
    {
        private bool _applied;

        public string Name => "one-shot-constant-rewrite-test-pass";

        public MirModule Run(MirModule module)
        {
            if (_applied)
            {
                return module;
            }

            _applied = true;
            return CreateIntReturnModule(2);
        }
    }

    private sealed class NonConvergingCallGraphOptimizationPass : IMirOptimizationPass
    {
        private int _runCount;

        public string Name => "non-converging-test-pass";

        public MirModule Run(MirModule module)
        {
            _runCount++;
            var calleeSymbolId = _runCount % 2 == 0
                ? new SymbolId(202)
                : new SymbolId(303);
            var calleeName = $"callee_{calleeSymbolId.Value}";

            return new MirModule
            {
                Name = module.Name,
                Path = module.Path,
                Functions =
                [
                    RewriteFirstCall(module.Functions[0], calleeSymbolId, calleeName)
                ],
                DynamicTypeKeys = module.DynamicTypeKeys,
                TypeDescriptors = module.TypeDescriptors,
                LinkLibraries = module.LinkLibraries,
                Span = module.Span,
                CStructAccessors = module.CStructAccessors,
                ConstructorLayouts = module.ConstructorLayouts,
                TraitImpls = module.TraitImpls,
                TraitInfos = module.TraitInfos,
                TypeAliases = module.TypeAliases,
                SpecializationFailures = module.SpecializationFailures
            };
        }

        private static MirFunc RewriteFirstCall(MirFunc function, SymbolId calleeSymbolId, string calleeName)
        {
            var blocks = function.BasicBlocks.ToList();
            var entry = blocks[0];
            var instructions = entry.Instructions.ToList();
            var call = (MirCall)instructions[0];
            instructions[0] = call with
            {
                Function = ((MirFunctionRef)call.Function) with
                {
                    SymbolId = calleeSymbolId,
                    Name = calleeName
                }
            };
            blocks[0] = new MirBasicBlock
            {
                Id = entry.Id,
                Instructions = instructions,
                Terminator = entry.Terminator,
                Span = entry.Span,
                IsEntry = entry.IsEntry
            };

            return new MirFunc
            {
                Name = function.Name,
                SourceName = function.SourceName,
                Locals = function.Locals,
                BasicBlocks = blocks,
                EntryBlockId = function.EntryBlockId,
                ReturnType = function.ReturnType,
                GenericParameterCount = function.GenericParameterCount,
                GenericTypeParameterIds = function.GenericTypeParameterIds,
                IsRuntimeWordAbi = function.IsRuntimeWordAbi,
                IsEntry = function.IsEntry,
                IsExternal = function.IsExternal,
                ExternalSymbolName = function.ExternalSymbolName,
                ExternalLibrary = function.ExternalLibrary,
                IntrinsicName = function.IntrinsicName,
                BuiltinIntrinsicRole = function.BuiltinIntrinsicRole,
                Span = function.Span,
                SymbolId = function.SymbolId,
                FunctionId = function.FunctionId,
                TraitInvokeHelper = function.TraitInvokeHelper,
                TraitInvokeHelperTraitId = function.TraitInvokeHelperTraitId
            };
        }
    }
}
