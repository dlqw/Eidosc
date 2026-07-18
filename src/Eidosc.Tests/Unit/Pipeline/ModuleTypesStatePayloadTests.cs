using Eidosc.Ast.Expressions;
using Eidosc.Ast.Declarations;
using Eidosc.Hir;
using Eidosc.Mir;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Pipeline;

public sealed class ModuleTypesStatePayloadTests
{
    [Fact]
    public void MetaTypePayload_RoundTripsTypedKindAndGenericDomainWithStableTokens()
    {
        var effectArgument = new MetaGenericArgumentRef(
            MetaGenericArgumentDomain.EffectRow,
            "io",
            "effect:io",
            SymbolId.None,
            null);
        var original = new MetaTypeRef(
            MetaTypeKind.TypeParameter,
            "T",
            "type-parameter:0",
            new SymbolId(41),
            new TypeId(42),
            [],
            GenericArguments: [effectArgument]);

        var payload = MetaTypeRefPayload.Create(original);

        Assert.Equal("type-parameter", payload.Kind);
        Assert.Equal("effect-row", Assert.Single(payload.GenericArguments!).Domain);
        Assert.True(payload.TryRestore(remapper: null, out var restored));
        Assert.Equal(MetaTypeKind.TypeParameter, restored.Kind);
        Assert.Equal(MetaGenericArgumentDomain.EffectRow, Assert.Single(restored.GenericArguments!).Domain);
        Assert.Equal(original.CanonicalText, restored.CanonicalText);
        Assert.StartsWith("type-parameter:", restored.CanonicalText, StringComparison.Ordinal);
        Assert.Contains("<[effect-row:", restored.CanonicalText, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(MetaTypeKind.TypeParameter), restored.CanonicalText, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(MetaGenericArgumentDomain.EffectRow), restored.CanonicalText, StringComparison.Ordinal);
    }

    [Fact]
    public void MetaTypePayload_RejectsUnknownKindAndGenericDomainTokens()
    {
        var unknownKind = new MetaTypeRefPayload(
            "future-kind",
            "T",
            "future:T",
            0,
            0,
            []);
        var unknownDomain = new MetaTypeRefPayload(
            "nominal",
            "Box",
            "nominal:Box",
            0,
            0,
            [],
            [new MetaGenericArgumentRefPayload("future-domain", "T", "type:T", 0, null)]);

        Assert.False(unknownKind.TryRestore(remapper: null, out _));
        Assert.False(unknownDomain.TryRestore(remapper: null, out _));
    }

    [Fact]
    public void AstTypesStatePayload_RestoresNonStructuralTypesAttachments()
    {
        var implementation = new LiteralExpr();
        implementation.SetLiteral("7");
        var call = new CallExpr();
        call.MarkSyntheticUnitArguments(1);
        var method = new MethodCallExpr();
        method.MarkSyntheticUnitArguments(1);
        method.MarkResolvedAsFieldAccess(new SymbolId(201));
        method.MarkResolvedAsCStructAccess("point_x", new SymbolId(202));
        var methodProjection = new AssociatedConstExpr();
        methodProjection.SetImplementationValue(implementation);
        method.SetResolvedStaticExpression(methodProjection);
        var infix = new InfixCallExpr { FunctionSymbolId = new SymbolId(203) };
        var letQuestion = new LetQuestionDecl();
        letQuestion.SetFailureBindingSymbol(new SymbolId(206));
        letQuestion.SetDesugaring(
            LetQuestionBindingKind.Result,
            new SymbolId(204),
            new SymbolId(205),
            BaseTypes.Int,
            BaseTypes.String,
            BaseTypes.Bool);
        var associatedConst = new AssociatedConstExpr();
        associatedConst.SetImplementationValue(implementation);
        var selectedIdentifier = new IdentifierExpr { SymbolId = new SymbolId(207) };
        selectedIdentifier.SetName("selected");
        var original = CreateAstStateModule(
            implementation,
            call,
            method,
            infix,
            letQuestion,
            associatedConst,
            selectedIdentifier);
        var payload = AstTypesStatePayload.Create(original);

        var restoredImplementation = new LiteralExpr();
        restoredImplementation.SetLiteral("7");
        var restoredCall = new CallExpr();
        var restoredMethod = new MethodCallExpr();
        restoredMethod.SetResolvedStaticExpression(new AssociatedConstExpr());
        var restoredInfix = new InfixCallExpr();
        var restoredLetQuestion = new LetQuestionDecl();
        var restoredAssociatedConst = new AssociatedConstExpr();
        var restoredSelectedIdentifier = new IdentifierExpr();
        restoredSelectedIdentifier.SetName("selected");
        var restoredAst = CreateAstStateModule(
            restoredImplementation,
            restoredCall,
            restoredMethod,
            restoredInfix,
            restoredLetQuestion,
            restoredAssociatedConst,
            restoredSelectedIdentifier);

        var restore = AstTypesStateRestorer.Restore(restoredAst, payload, remapper: null, symbolTable: null);

        Assert.True(restore.Applied, string.Join(Environment.NewLine, restore.Failures));
        Assert.Equal(1, restoredCall.SynthesizedUnitArgumentCount);
        Assert.Equal(1, restoredMethod.SynthesizedUnitArgumentCount);
        Assert.True(restoredMethod.ResolvedAsFieldAccess);
        Assert.Equal(new SymbolId(201), restoredMethod.FieldSymbolId);
        Assert.Equal("point_x", restoredMethod.CStructGetterName);
        Assert.Equal(new SymbolId(202), restoredMethod.CStructGetterSymbolId);
        Assert.Same(
            restoredImplementation,
            Assert.IsType<AssociatedConstExpr>(restoredMethod.ResolvedStaticExpression).ImplementationValue);
        Assert.Equal(new SymbolId(203), restoredInfix.FunctionSymbolId);
        Assert.Equal(LetQuestionBindingKind.Result, restoredLetQuestion.BindingKind);
        Assert.Equal(new SymbolId(204), restoredLetQuestion.SuccessConstructorSymbolId);
        Assert.Equal(new SymbolId(205), restoredLetQuestion.FailureConstructorSymbolId);
        Assert.Equal(new SymbolId(206), restoredLetQuestion.FailureBindingSymbolId);
        Assert.Equal(BaseTypes.Int.ToString(), restoredLetQuestion.SuccessPayloadType?.ToString());
        Assert.Equal(BaseTypes.String.ToString(), restoredLetQuestion.FailurePayloadType?.ToString());
        Assert.Equal(BaseTypes.Bool.ToString(), restoredLetQuestion.ShortCircuitReturnType?.ToString());
        Assert.Same(restoredImplementation, restoredAssociatedConst.ImplementationValue);
        Assert.Equal(new SymbolId(207), restoredSelectedIdentifier.SymbolId);
    }

    [Fact]
    public void Run_HirTargetWithTypesAstState_RestoresCallsInfixAndAssociatedConst()
    {
        const string source = """
CacheBounded[T] :: trait
{
    Min :: T
}

CacheBoundedInt :: instance CacheBounded[Int]
{
    Min :: Int = 7
}

ping :: Unit -> Int
{
    _ => 1
}

inc :: Int -> Int
{
    value => value
}

join :: Int -> Int -> Int
{
    left => right => left
}

empty :: ping();
method :: 3.inc;
infixed :: 1 `join` 2;

minimum :: Unit -> Int
{
    _ => CacheBounded[Int].Min
}
""";
        var first = RunVirtualPhase(source, CompilationPhase.Hir, static _ => { });

        Assert.True(first.Success, FormatDiagnostics(first));
        var payloads = Assert.IsAssignableFrom<IReadOnlyList<ModuleTypesStatePayload>>(first.ModuleTypesStatePayloads);
        var payloadByModule = payloads.ToDictionary(static payload => payload.ModuleKey, StringComparer.Ordinal);
        var second = RunVirtualPhase(source, CompilationPhase.Hir, options =>
            ConfigurePreviousTypesPayloads(options, first, payloads, payloadByModule));

        Assert.True(second.Success, FormatDiagnostics(second));
        Assert.True(
            second.ProfilingCounters.GetValueOrDefault("Types.moduleRestore.applied") == 1,
            FormatCounters(second));
        Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault("Types.step.infer_module_declarations.calls"));
        Assert.Equal(
            HirFormatter.FormatHir(Assert.IsType<HirModule>(first.HirModule)),
            HirFormatter.FormatHir(Assert.IsType<HirModule>(second.HirModule)));

        var nodes = AstStableNodeTraversal.Enumerate(Assert.IsType<Eidosc.Ast.Declarations.ModuleDecl>(second.Ast))
            .Select(static entry => entry.Node)
            .ToArray();
        var emptyCall = Assert.Single(nodes.OfType<CallExpr>(), static call =>
            call.Function is IdentifierExpr { Name: "ping" });
        Assert.Equal(1, emptyCall.SynthesizedUnitArgumentCount);
        Assert.False(emptyCall.UsesFfiUnitArgumentElision);
        var restoredMethod = Assert.Single(
            nodes.OfType<MethodCallExpr>(),
            static method => string.Equals(method.MethodName, "inc", StringComparison.Ordinal));
        Assert.True(restoredMethod.SymbolId.IsValid);
        Assert.True(Assert.Single(nodes.OfType<InfixCallExpr>()).FunctionSymbolId.IsValid);
        var restoredProjectionMethod = Assert.Single(
            nodes.OfType<MethodCallExpr>(),
            static method => string.Equals(method.MethodName, "Min", StringComparison.Ordinal));
        Assert.NotNull(
            Assert.IsType<AssociatedConstExpr>(restoredProjectionMethod.ResolvedStaticExpression).ImplementationValue);
    }

    [Fact]
    public void Run_MirTargetWithTypesAstState_RestoresLetQuestionDesugaring()
    {
        const string source = """
Option[T] :: type { Some:: type(T) , None :: type {} }
Result[T, E] :: type { Ok:: type(T) , Err:: type(E) }

maybe_inc :: Int -> Option[Int]
{
    value => Some(value + 1)
}

parse_like :: Int -> Result[Int, String]
{
    value => Ok(value + 1)
}

use_option :: Int -> Option[Int]
{
    value => {
        let? next = maybe_inc(value);
        Some(next + 1)
    }
}

use_result :: Int -> Result[Int, String]
{
    value => {
        let? next = parse_like(value);
        Ok(next + 1)
    }
}
""";
        var first = RunVirtualPhase(source, CompilationPhase.Mir, static _ => { });

        Assert.True(first.Success, FormatDiagnostics(first));
        var payloads = Assert.IsAssignableFrom<IReadOnlyList<ModuleTypesStatePayload>>(first.ModuleTypesStatePayloads);
        var payloadByModule = payloads.ToDictionary(static payload => payload.ModuleKey, StringComparer.Ordinal);
        Assert.Equal(
            2,
            payloads.SelectMany(static payload => payload.AstState.Entries)
                .Count(static entry => entry.LetQuestionBindingKind != null));

        var second = RunVirtualPhase(source, CompilationPhase.Mir, options =>
            ConfigurePreviousTypesPayloads(options, first, payloads, payloadByModule));

        Assert.True(second.Success, FormatDiagnostics(second));
        Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault("Types.moduleRestore.applied"));
        Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault("Types.step.infer_module_declarations.calls"));
        Assert.Equal(
            MirFormatter.FormatMir(Assert.IsType<MirModule>(first.MirModule)),
            MirFormatter.FormatMir(Assert.IsType<MirModule>(second.MirModule)));

        var letQuestions = AstStableNodeTraversal
            .Enumerate(Assert.IsType<ModuleDecl>(second.Ast))
            .Select(static entry => entry.Node)
            .OfType<LetQuestionDecl>()
            .OrderBy(static declaration => declaration.BindingKind)
            .ToArray();
        Assert.Equal(2, letQuestions.Length);
        Assert.Equal(LetQuestionBindingKind.Option, letQuestions[0].BindingKind);
        Assert.Equal(LetQuestionBindingKind.Result, letQuestions[1].BindingKind);
        Assert.All(letQuestions, static declaration =>
        {
            Assert.True(declaration.SuccessConstructorSymbolId.IsValid);
            Assert.True(declaration.FailureConstructorSymbolId.IsValid);
            Assert.NotNull(declaration.SuccessPayloadType);
            Assert.NotNull(declaration.ShortCircuitReturnType);
        });
        Assert.True(letQuestions[1].FailureBindingSymbolId.IsValid);
        Assert.NotNull(letQuestions[1].FailurePayloadType);
    }

    [Fact]
    public void Run_ContextualRecordStructuralRewrite_FallsBackToFullInference()
    {
        const string source = """
Pos :: type { x:: Int, y:: Int }
origin :: Pos = .{ x: 0, y: 0 };
""";
        var first = RunVirtualPhase(source, CompilationPhase.Types, static _ => { });

        Assert.True(first.Success, FormatDiagnostics(first));
        var payloads = Assert.IsAssignableFrom<IReadOnlyList<ModuleTypesStatePayload>>(first.ModuleTypesStatePayloads);
        var payloadByModule = payloads.ToDictionary(static payload => payload.ModuleKey, StringComparer.Ordinal);
        Assert.Contains(payloads, static payload => payload.AstState.UnsupportedStructuralRewrites > 0);

        var second = RunVirtualPhase(source, CompilationPhase.Types, options =>
            ConfigurePreviousTypesPayloads(options, first, payloads, payloadByModule));

        Assert.True(second.Success, FormatDiagnostics(second));
        Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault("Types.moduleRestore.applied"));
        Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault("Types.moduleRestore.fallbackFullInfer"));
        Assert.True(second.ProfilingCounters.GetValueOrDefault("Types.step.infer_module_declarations.calls") > 0);
        var literal = Assert.Single(
            AstStableNodeTraversal.Enumerate(Assert.IsType<ModuleDecl>(second.Ast))
                .Select(static entry => entry.Node)
                .OfType<ContextualRecordLiteralExpr>());
        Assert.NotNull(literal.DesugaredCtor);
    }

    [Fact]
    public void Run_WithPreviousTypesPayloads_RestoresSameLayerModulesInParallel()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_module_types_parallel_{Guid.NewGuid():N}");
        try
        {
            var entryFile = WriteTwoModuleProject(tempDir);
            var source = File.ReadAllText(entryFile);
            var first = RunTypes(entryFile, source, options => { });

            Assert.True(first.Success, FormatDiagnostics(first));
            Assert.NotNull(first.ModuleSemanticSignatureSnapshot);
            Assert.NotNull(first.ModuleTypedSemanticSnapshot);
            Assert.NotNull(first.ModuleMemberIndexSnapshot);
            Assert.NotNull(first.ModuleDependencySignatureSnapshot);
            var payloads = Assert.IsAssignableFrom<IReadOnlyList<ModuleTypesStatePayload>>(first.ModuleTypesStatePayloads);
            Assert.Equal(3, payloads.Count);
            var payloadByModule = payloads.ToDictionary(static payload => payload.ModuleKey, StringComparer.Ordinal);

            var second = RunTypes(entryFile, source, options =>
            {
                options.PreviousModuleSemanticSignatureSnapshot = first.ModuleSemanticSignatureSnapshot;
                options.PreviousModuleTypedSemanticSnapshot = first.ModuleTypedSemanticSnapshot;
                options.PreviousModuleMemberIndexSnapshot = first.ModuleMemberIndexSnapshot;
                options.PreviousModuleDependencySignatureSnapshot = first.ModuleDependencySignatureSnapshot;
                options.PreviousModuleTypesStatePayloads = payloads;
                options.ModuleArtifactAvailability = (moduleKey, kind, _, _) => kind switch
                {
                    ProjectModuleArtifactKinds.SemanticSignature => true,
                    ProjectModuleArtifactKinds.TypedSemanticSignature => true,
                    ProjectModuleArtifactKinds.TypesStatePayload => payloadByModule.ContainsKey(moduleKey),
                    _ => false
                };
                options.ModuleTypesStatePayloadLoader = (moduleKey, kind, _, _) =>
                    kind == ProjectModuleArtifactKinds.TypesStatePayload &&
                    payloadByModule.TryGetValue(moduleKey, out var payload)
                        ? payload
                        : null;
            });

            Assert.True(second.Success, FormatDiagnostics(second));
            Assert.True(
                second.ProfilingCounters.GetValueOrDefault("Types.moduleRestore.applied") == 1,
                $"{FormatCounters(second)}{Environment.NewLine}{FormatSymbolState(payloads[0])}");
            Assert.Equal(3, second.ProfilingCounters.GetValueOrDefault("Types.moduleRestore.payloadModules"));
            Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault("Build.moduleStage.Types.realTaskExecution"));
            Assert.Equal(3, second.ProfilingCounters.GetValueOrDefault("Build.moduleStage.Types.restoredModules"));
            Assert.Equal(2, second.ProfilingCounters.GetValueOrDefault("Build.moduleStage.Types.maxDegreeOfParallelism"));
            Assert.Equal(2, second.ProfilingCounters.GetValueOrDefault("Build.moduleStage.Types.maxObservedParallelism"));
            Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault("Types.step.infer_module_declarations.calls"));
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
    public void Run_MultiModulePayloads_StoreOnlyReachableModuleState()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_module_payload_slice_{Guid.NewGuid():N}");
        try
        {
            var entryFile = WriteTwoModuleProject(tempDir);
            var source = File.ReadAllText(entryFile);
            var result = RunTypes(entryFile, source, options => { });

            Assert.True(result.Success, FormatDiagnostics(result));
            var symbolTable = Assert.IsType<Eidosc.Symbols.SymbolTable>(result.SymbolTable);
            var typeInferer = Assert.IsType<TypeInferer>(result.TypeInferer);
            var namerPayloads = Assert.IsAssignableFrom<IReadOnlyList<ModuleNamerStatePayload>>(
                result.ModuleNamerStatePayloads);
            var typesPayloads = Assert.IsAssignableFrom<IReadOnlyList<ModuleTypesStatePayload>>(
                result.ModuleTypesStatePayloads);

            Assert.Equal(3, namerPayloads.Count);
            Assert.Equal(3, typesPayloads.Count);
            Assert.True(
                namerPayloads.Sum(static payload => payload.SymbolTable.Symbols.Count) <
                namerPayloads.Count * symbolTable.Symbols.Count);
            Assert.True(
                typesPayloads.Sum(static payload => payload.TypeEnv.Bindings.Count) <
                typesPayloads.Count * typeInferer.TypeEnvBindings.Count);
            Assert.Contains(
                namerPayloads,
                payload => payload.SymbolTable.Symbols.Count < symbolTable.Symbols.Count);
            Assert.Contains(
                typesPayloads,
                payload => payload.TypeEnv.Bindings.Count < typeInferer.TypeEnvBindings.Count);
            Assert.All(namerPayloads, static payload =>
            {
                Assert.True(payload.SymbolTable.Symbols.Count > 0);
                Assert.True(payload.ModuleRegistry.Modules.Count > 0);
                Assert.True(payload.SymbolTable.Symbols.Count <= payload.SymbolIdentities.Count);
            });
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
    public void Run_WithTypesPayloadLoader_RestoresWithoutLatestPayloadList()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_module_types_loader_{Guid.NewGuid():N}");
        try
        {
            var entryFile = WriteTwoModuleProject(tempDir);
            var source = File.ReadAllText(entryFile);
            var first = RunTypes(entryFile, source, options => { });

            Assert.True(first.Success, FormatDiagnostics(first));
            var payloads = Assert.IsAssignableFrom<IReadOnlyList<ModuleTypesStatePayload>>(first.ModuleTypesStatePayloads);
            var payloadByModule = payloads.ToDictionary(static payload => payload.ModuleKey, StringComparer.Ordinal);

            var second = RunTypes(entryFile, source, options =>
            {
                options.PreviousModuleSemanticSignatureSnapshot = first.ModuleSemanticSignatureSnapshot;
                options.PreviousModuleTypedSemanticSnapshot = first.ModuleTypedSemanticSnapshot;
                options.PreviousModuleMemberIndexSnapshot = first.ModuleMemberIndexSnapshot;
                options.PreviousModuleDependencySignatureSnapshot = first.ModuleDependencySignatureSnapshot;
                options.ModuleArtifactAvailability = (moduleKey, kind, _, _) => kind switch
                {
                    ProjectModuleArtifactKinds.SemanticSignature => true,
                    ProjectModuleArtifactKinds.TypedSemanticSignature => true,
                    ProjectModuleArtifactKinds.TypesStatePayload => payloadByModule.ContainsKey(moduleKey),
                    _ => false
                };
                options.ModuleTypesStatePayloadLoader = (moduleKey, kind, sourceHash, dependencyHash) =>
                    kind == ProjectModuleArtifactKinds.TypesStatePayload &&
                    payloadByModule.TryGetValue(moduleKey, out var payload) &&
                    string.Equals(sourceHash, payload.TypedSemantic.LocalSurfaceHash, StringComparison.Ordinal) &&
                    string.Equals(dependencyHash, payload.TypedSemantic.DependencyTypedSemanticHash, StringComparison.Ordinal)
                        ? payload
                        : null;
            });

            Assert.True(second.Success, FormatDiagnostics(second));
            Assert.True(
                second.ProfilingCounters.GetValueOrDefault("Types.moduleRestore.applied") == 1,
                FormatCounters(second));
            Assert.Equal(3, second.ProfilingCounters.GetValueOrDefault("Build.moduleStage.Types.restoredModules"));
            Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault("Types.step.infer_module_declarations.calls"));
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
    public void Run_HirTargetWithPreviousTypesPayloads_RestoresTypesAndBuildsHir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_module_types_hir_{Guid.NewGuid():N}");
        try
        {
            var entryFile = WriteTwoModuleProject(tempDir);
            var source = File.ReadAllText(entryFile);
            var first = RunPhase(entryFile, source, CompilationPhase.Hir, options => { });

            Assert.True(first.Success, FormatDiagnostics(first));
            Assert.NotNull(first.HirModule);
            var payloads = Assert.IsAssignableFrom<IReadOnlyList<ModuleTypesStatePayload>>(first.ModuleTypesStatePayloads);
            var payloadByModule = payloads.ToDictionary(static payload => payload.ModuleKey, StringComparer.Ordinal);

            var second = RunPhase(entryFile, source, CompilationPhase.Hir, options =>
            {
                ConfigurePreviousTypesPayloads(options, first, payloads, payloadByModule);
            });

            Assert.True(second.Success, FormatDiagnostics(second));
            Assert.NotNull(second.HirModule);
            Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault("Types.moduleRestore.applied"));
            Assert.Equal(3, second.ProfilingCounters.GetValueOrDefault("Build.moduleStage.Types.restoredModules"));
            Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault("Types.step.infer_module_declarations.calls"));
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
    public void Run_MirTargetWithPreviousTypesPayloads_RestoresTypesAndBuildsMir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_module_types_mir_{Guid.NewGuid():N}");
        try
        {
            var entryFile = WriteTwoModuleProject(tempDir);
            var source = File.ReadAllText(entryFile);
            var first = RunPhase(entryFile, source, CompilationPhase.Mir, options => { });

            Assert.True(first.Success, FormatDiagnostics(first));
            Assert.NotNull(first.MirModule);
            var payloads = Assert.IsAssignableFrom<IReadOnlyList<ModuleTypesStatePayload>>(first.ModuleTypesStatePayloads);
            var payloadByModule = payloads.ToDictionary(static payload => payload.ModuleKey, StringComparer.Ordinal);

            var second = RunPhase(entryFile, source, CompilationPhase.Mir, options =>
            {
                ConfigurePreviousTypesPayloads(options, first, payloads, payloadByModule);
            });

            Assert.True(
                second.Success,
                $"{FormatDiagnostics(second)}{Environment.NewLine}{(second.MirModule == null ? "" : MirFormatter.FormatMir(second.MirModule))}");
            Assert.NotNull(second.MirModule);
            Assert.True(
                second.ProfilingCounters.GetValueOrDefault("Types.moduleRestore.applied") == 1,
                $"{FormatCounters(second)}{Environment.NewLine}{FormatSymbolState(payloads[0])}");
            Assert.Equal(3, second.ProfilingCounters.GetValueOrDefault("Build.moduleStage.Types.restoredModules"));
            Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault("Types.step.infer_module_declarations.calls"));
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
    public void Run_BodyOnlySourceChange_CompilesChangedModulesAndRestoresUnaffectedModules()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_module_types_body_change_{Guid.NewGuid():N}");
        try
        {
            var entryFile = WriteTwoModuleProject(tempDir);
            var source = File.ReadAllText(entryFile);
            var first = RunTypes(entryFile, source, options => { });

            Assert.True(first.Success, FormatDiagnostics(first));
            var payloads = Assert.IsAssignableFrom<IReadOnlyList<ModuleTypesStatePayload>>(first.ModuleTypesStatePayloads);
            var payloadByModule = payloads.ToDictionary(static payload => payload.ModuleKey, StringComparer.Ordinal);
            File.WriteAllText(Path.Combine(tempDir, "lib_a.eidos"), """
LibA :: module {
    id :: Int -> Int
    {
        value => value + 1
    }
}
""");

            var second = RunTypes(entryFile, source, options =>
            {
                ConfigurePreviousTypesPayloads(options, first, payloads, payloadByModule);
            });

            Assert.True(second.Success, FormatDiagnostics(second));
            Assert.True(
                second.ProfilingCounters.GetValueOrDefault("Types.moduleRestore.applied") == 1,
                FormatCounters(second));
            Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault("Types.moduleRestore.fallbackFullInfer"));
            Assert.True(second.ProfilingCounters.GetValueOrDefault(
                "Build.moduleStage.Types.compiledModules") > 0);
            Assert.True(second.ProfilingCounters.GetValueOrDefault(
                "Build.moduleStage.Types.restoredModules") > 0);
            Assert.True(second.ProfilingCounters.GetValueOrDefault(
                "Build.moduleStage.Types.maxObservedParallelism") > 1);
            Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault("Types.step.infer_module_declarations.calls"));
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
    public void Run_MixedTypesRestore_PreservesHigherOrderEffectsForAuthorization()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_module_types_effect_restore_{Guid.NewGuid():N}");
        try
        {
            var entryFile = WriteEffectRestoreProject(tempDir);
            var source = File.ReadAllText(entryFile);
            var first = RunPhase(entryFile, source, CompilationPhase.Effects, static _ => { });

            Assert.False(first.Success);
            Assert.Contains(first.Diagnostics, static diagnostic => diagnostic.Code == "E3003");
            var payloads = Assert.IsAssignableFrom<IReadOnlyList<ModuleTypesStatePayload>>(
                first.ModuleTypesStatePayloads);
            var payloadByModule = payloads.ToDictionary(static payload => payload.ModuleKey, StringComparer.Ordinal);
            var effectPayload = Assert.Single(payloads, static payload => payload.ModuleKey.EndsWith("Effects", StringComparison.Ordinal));
            Assert.Contains(
                effectPayload.AstInferredTypes.Entries,
                static entry => entry.ResolvedEffectsShape is { Kind: nameof(EffectRow) });
            Assert.Contains(
                effectPayload.FunctionEffects.Bindings,
                static binding => binding.InferredEffects is
                {
                    Kind: nameof(EffectRow),
                    EffectVariableIds.Count: > 0
                });
            Assert.Contains(
                effectPayload.TypedSemantic.Declarations,
                static declaration => declaration.Name == "unauthorized" &&
                                      declaration.CanonicalFacts.Any(fact =>
                                          fact.StartsWith("inferredEffects:", StringComparison.Ordinal) &&
                                          fact.Contains("io", StringComparison.Ordinal)));

            File.WriteAllText(Path.Combine(tempDir, "changed.eidos"), """
Changed :: module {
    id :: Int -> Int
    {
        value => value + 1
    }
}
""");

            var second = RunPhase(entryFile, source, CompilationPhase.Effects, options =>
            {
                ConfigurePreviousTypesPayloads(options, first, payloads, payloadByModule);
            });

            Assert.False(second.Success);
            Assert.Contains(second.Diagnostics, static diagnostic => diagnostic.Code == "E3003");
            Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault("Types.moduleRestore.fallbackFullInfer"));
            Assert.True(second.ProfilingCounters.GetValueOrDefault("Types.moduleRestore.applied") > 0);
            Assert.True(second.ProfilingCounters.GetValueOrDefault("Types.moduleRestore.restoredInferredTypes") > 0);
            Assert.True(
                second.ProfilingCounters.GetValueOrDefault("Build.moduleStage.Types.restoredModules") > 0,
                FormatCounters(second));
            Assert.Contains(
                AstStableNodeTraversal.Enumerate(Assert.IsType<ModuleDecl>(second.Ast))
                    .Select(static entry => entry.Node),
                static node => node.InferredEffects is { IsPure: false });
            var restoredSummaries = Assert.IsAssignableFrom<IReadOnlyDictionary<FuncDef, FunctionEffectSummary>>(
                second.FunctionEffectSummaries);
            Assert.Contains(
                restoredSummaries,
                static binding => binding.Key.Name == "unauthorized" &&
                                  binding.Value.InferredEffects.ContainsName("io"));
            var restoredApplyFunctions = AstStableNodeTraversal
                .Enumerate(Assert.IsType<ModuleDecl>(second.Ast))
                .Select(static entry => entry.Node)
                .OfType<FuncDef>()
                .Where(static function => function.Name == "apply")
                .ToArray();
            Assert.Equal(2, restoredApplyFunctions.Length);
            var effectVariableIds = restoredApplyFunctions
                .Select(function => EnumerateEffectVariableIds(Assert.IsAssignableFrom<Eidosc.Types.Type>(function.InferredType))
                    .Distinct()
                    .ToArray())
                .ToArray();
            for (var i = 0; i < restoredApplyFunctions.Length; i++)
            {
                Assert.True(
                    effectVariableIds[i].Length == 1,
                    $"{restoredApplyFunctions[i].Span.FilePath}: {restoredApplyFunctions[i].InferredType}");
            }
            Assert.Equal(2, effectVariableIds.SelectMany(static ids => ids).Distinct().Count());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static string WriteTwoModuleProject(string tempDir)
    {
        Directory.CreateDirectory(tempDir);
        var entryFile = Path.Combine(tempDir, "Main.eidos");
        var libAFile = Path.Combine(tempDir, "lib_a.eidos");
        var libBFile = Path.Combine(tempDir, "lib_b.eidos");
        File.WriteAllText(entryFile, """
Main :: module {
    import LibA
    import LibB

    main :: Int -> Int
    {
        value => LibB.inc(LibA.id(value))
    }
}
""");
        File.WriteAllText(libAFile, """
LibA :: module {
    id :: Int -> Int
    {
        value => value
    }
}
""");
        File.WriteAllText(libBFile, """
LibB :: module {
    inc :: Int -> Int
    {
        value => value + 1
    }
}
""");
        return entryFile;
    }

    private static string WriteEffectRestoreProject(string tempDir)
    {
        Directory.CreateDirectory(tempDir);
        var entryFile = Path.Combine(tempDir, "Main.eidos");
        File.WriteAllText(entryFile, """
Main :: module {
    import Changed
    import Effects
    import Effects2

    main :: Int -> Int
    {
        value => Changed.id(value)
    }
}
""");
        File.WriteAllText(Path.Combine(tempDir, "changed.eidos"), """
Changed :: module {
    id :: Int -> Int
    {
        value => value
    }
}
""");
        File.WriteAllText(Path.Combine(tempDir, "effects.eidos"), """
Effects :: module {
    io :: effect;

    write :: Int -> Int need io
    {
        value => value
    }

    apply[A, B, E: effects] :: (A -> B need E) -> A -> B need E
    {
        function => value => function(value)
    }

    unauthorized :: Int -> Int
    {
        value => apply(write, value)
    }
}
""");
        File.WriteAllText(Path.Combine(tempDir, "effects2.eidos"), """
Effects2 :: module {
    apply[A, B, E: effects] :: (A -> B need E) -> A -> B need E
    {
        function => value => function(value)
    }
}
""");
        return entryFile;
    }

    private static IEnumerable<int> EnumerateEffectVariableIds(Eidosc.Types.Type type)
    {
        switch (type)
        {
            case TyFun function:
                foreach (var variable in function.Effects.Variables)
                {
                    yield return variable.Id;
                }

                foreach (var parameter in function.Params)
                {
                    foreach (var variable in EnumerateEffectVariableIds(parameter))
                    {
                        yield return variable;
                    }
                }

                foreach (var variable in EnumerateEffectVariableIds(function.Result))
                {
                    yield return variable;
                }
                break;

            case TyCon constructor:
                foreach (var argument in constructor.Args)
                {
                    foreach (var variable in EnumerateEffectVariableIds(argument))
                    {
                        yield return variable;
                    }
                }
                break;

            case TyTuple tuple:
                foreach (var element in tuple.Elements)
                {
                    foreach (var variable in EnumerateEffectVariableIds(element))
                    {
                        yield return variable;
                    }
                }
                break;
        }
    }

    private static ModuleDecl CreateAstStateModule(
        LiteralExpr implementation,
        CallExpr call,
        MethodCallExpr method,
        InfixCallExpr infix,
        LetQuestionDecl letQuestion,
        AssociatedConstExpr associatedConst,
        IdentifierExpr selectedIdentifier)
    {
        var module = new ModuleDecl();
        module.SetPath(["Main"]);
        module.SetDeclarations([
            WrapValue(implementation),
            WrapValue(call),
            WrapValue(method),
            WrapValue(infix),
            letQuestion,
            WrapValue(associatedConst),
            WrapValue(selectedIdentifier)
        ]);
        return module;
    }

    private static LetDecl WrapValue(Eidosc.Ast.EidosAstNode value)
    {
        var declaration = new LetDecl();
        declaration.SetValue(value);
        return declaration;
    }

    private static CompilationResult RunTypes(
        string entryFile,
        string source,
        Action<CompilationOptions> configure)
    {
        return RunPhase(entryFile, source, CompilationPhase.Types, configure);
    }

    private static CompilationResult RunVirtualPhase(
        string source,
        CompilationPhase stopAtPhase,
        Action<CompilationOptions> configure)
    {
        var options = new CompilationOptions
        {
            InputFile = "module_types_ast_state.eidos",
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

    private static CompilationResult RunPhase(
        string entryFile,
        string source,
        CompilationPhase stopAtPhase,
        Action<CompilationOptions> configure)
    {
        var options = new CompilationOptions
        {
            InputFile = entryFile,
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

    private static void ConfigurePreviousTypesPayloads(
        CompilationOptions options,
        CompilationResult first,
        IReadOnlyList<ModuleTypesStatePayload> payloads,
        IReadOnlyDictionary<string, ModuleTypesStatePayload> payloadByModule)
    {
        options.PreviousModuleSemanticSignatureSnapshot = first.ModuleSemanticSignatureSnapshot;
        options.PreviousModuleTypedSemanticSnapshot = first.ModuleTypedSemanticSnapshot;
        options.PreviousModuleMemberIndexSnapshot = first.ModuleMemberIndexSnapshot;
        options.PreviousModuleDependencySignatureSnapshot = first.ModuleDependencySignatureSnapshot;
        options.PreviousModuleNamerStatePayloads = first.ModuleNamerStatePayloads;
        options.PreviousModuleTypesStatePayloads = payloads;
        options.ModuleArtifactAvailability = (moduleKey, kind, _, _) => kind switch
        {
            ProjectModuleArtifactKinds.SemanticSignature => true,
            ProjectModuleArtifactKinds.TypedSemanticSignature => true,
            ProjectModuleArtifactKinds.TypesStatePayload => payloadByModule.ContainsKey(moduleKey),
            _ => false
        };
        options.ModuleTypesStatePayloadLoader = (moduleKey, kind, sourceHash, dependencyHash) =>
            kind == ProjectModuleArtifactKinds.TypesStatePayload &&
            payloadByModule.TryGetValue(moduleKey, out var payload) &&
            string.Equals(sourceHash, payload.TypedSemantic.LocalSurfaceHash, StringComparison.Ordinal) &&
            string.Equals(dependencyHash, payload.TypedSemantic.DependencyTypedSemanticHash, StringComparison.Ordinal)
                ? payload
                : null;
    }

    private static string FormatDiagnostics(CompilationResult result) =>
        string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}{Environment.NewLine}{string.Join(Environment.NewLine, diagnostic.Notes)}"));

    private static string FormatCounters(CompilationResult result) =>
        string.Join(
            Environment.NewLine,
            result.ProfilingCounters
                .OrderBy(static counter => counter.Key, StringComparer.Ordinal)
                .Select(static counter => $"{counter.Key}={counter.Value}"));

    private static string FormatSymbolState(ModuleTypesStatePayload payload)
    {
        var identities = payload.SymbolIdentities
            .Select(static identity => identity.SymbolId)
            .ToHashSet();
        return string.Join(
            Environment.NewLine,
            payload.SymbolState.Entries.Select(entry =>
                $"TypesSymbolState {entry.SymbolKind}:{entry.SymbolId} stable={identities.Contains(entry.SymbolId)}"));
    }
}
