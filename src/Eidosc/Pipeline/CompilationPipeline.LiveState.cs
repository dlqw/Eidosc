namespace Eidosc.Pipeline;

public sealed partial class CompilationPipeline
{
    private bool TryRestoreLiveState(CompilationPhase phase)
    {
        if (!CanUseLiveStateCache() ||
            _ast == null)
        {
            return false;
        }

        var ast = _ast;

        var key = CreateLiveStateCacheKey(phase);
        if (!CompilationLiveStateCache.TryGet(key, out var snapshot))
        {
            SetProfilingCounter($"Build.liveState.{phase}.misses", 1);
            _comptimeExecution.Trace.Record(
                "pipeline.cache",
                "cache",
                $"live-state:{phase}",
                "miss",
                key.FlagsHash,
                ast.Span,
                0);
            return false;
        }

        ApplyLiveStateSnapshot(snapshot);
        if (phase == CompilationPhase.Namer)
        {
            foreach (var diagnostic in Semantic.DeclarationClauseBinder.BindTree(
                         ast,
                         _options.LanguageVersion,
                         Semantic.CompilerOwnedSourceGrant.Create(_compilerOwnedSourcePaths)))
            {
                _diagnostics.Add(Diagnostic.Diagnostic.Error(diagnostic.Message, "E3000")
                    .WithLabel(diagnostic.Span, diagnostic.Message));
            }
        }
        SetProfilingCounter($"Build.liveState.{phase}.hits", 1);
        _comptimeExecution.Trace.Record(
            "pipeline.cache",
            "cache",
            $"live-state:{phase}",
            "hit",
            key.FlagsHash,
            ast.Span,
            0);
        return true;
    }

    private void StoreLiveState(CompilationPhase phase)
    {
        if (!CanUseLiveStateCache() ||
            _ast == null)
        {
            return;
        }

        CompilationLiveStateCache.Store(
            CreateLiveStateCacheKey(phase),
            CreateLiveStateSnapshot());
        SetProfilingCounter($"Build.liveState.{phase}.stores", 1);
        _comptimeExecution.Trace.Record(
            "pipeline.cache",
            "cache",
            $"live-state:{phase}",
            "store",
            GetLiveStateFlagsHash(),
            _ast.Span,
            0);
    }

    private bool CanUseLiveStateCache()
    {
        return _options.EnableLiveStateCache &&
               _options.PreviousModuleSemanticSignatureSnapshot == null &&
               _options.PreviousModuleTypedSemanticSnapshot == null &&
               _options.PreviousModuleMemberIndexSnapshot == null &&
               _options.PreviousModuleNamerStatePayloads == null &&
               _options.PreviousModuleTypesStatePayloads == null &&
               _options.PreviousModuleHirStatePayloads == null &&
               _options.PreviousModuleMirStatePayloads == null &&
               _options.PreviousModuleDependencySignatureSnapshot == null &&
               _options.PreviousImplOverlapCheckSnapshot == null &&
               _options.PreviousMirFunctionFingerprintSnapshot == null &&
               _options.PreviousLlvmFunctionFingerprintSnapshot == null &&
               _options.PreviousLlvmFunctionFragmentSnapshot == null &&
               _options.PreviousLlvmModuleEnvelopeSnapshot == null &&
               _options.PreviousLlvmCodegenUnitPlanSnapshot == null &&
               _options.PreviousTypeDirectedCallableResolutionSnapshot == null &&
               _options.PreviousAssociatedTypeProjectionSnapshot == null &&
               _options.PreviousAssociatedConstProjectionSnapshot == null &&
               _options.PreviousSendAnalysisSnapshot == null &&
               _options.PreviousBorrowDiagnosticSnapshot == null &&
               _options.PreviousBorrowCodegenHintsSnapshot == null &&
               _options.PreviousTraitCheckSnapshot == null &&
               _options.ModuleArtifactAvailability == null &&
               _options.ModuleSemanticArtifactLoader == null &&
               _options.ModuleNamerStatePayloadLoader == null &&
               _options.ModuleTypesStatePayloadLoader == null &&
               _options.ModuleHirStatePayloadLoader == null &&
               _options.ModuleMirStatePayloadLoader == null &&
               _options.ModuleTypedSemanticArtifactLoader == null &&
               _options.ModuleMirArtifactLoader == null;
    }

    private CompilationLiveStateCacheKey CreateLiveStateCacheKey(CompilationPhase phase)
    {
        var sourceHash = ModuleArtifactHash.ComputeSourceHash(_sourceCode);
        var normalizedInput = SourcePathNormalizer.NormalizeForCacheKey(_options.InputFile ?? "");
        return new CompilationLiveStateCacheKey(
            sourceHash,
            normalizedInput,
            _options.LanguageVersion,
            GetLiveStateFlagsHash(),
            phase);
    }

    private string GetLiveStateFlagsHash()
    {
        return _liveStateFlagsHash ??= ModuleArtifactHash.ComputeFlagsHash([
            $"entry:{_options.EntryFunctionName ?? ""}",
            $"stop:{_options.StopAtPhase?.ToString() ?? ""}",
            $"mir-opt:{_options.EnableMirOptimizations}",
            $"comptime-fuel:{_options.ComptimeFuelBudget}",
            $"comptime-bytes:{_options.ComptimeAllocatedValueBytesBudget}",
            $"comptime-diagnostics:{_options.ComptimeDiagnosticBudget}",
            $"no-prelude:{_options.NoImplicitPrelude}",
            $"imports:{string.Join('|', _options.ImportSearchRoots.Select(SourcePathNormalizer.NormalizeForCacheKey))}",
            $"packages:{string.Join('|', _options.PackageImportRoots.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={string.Join(',', pair.Value.Select(SourcePathNormalizer.NormalizeForCacheKey))}"))}",
            $"clause-schema:{Ast.Declarations.ClauseSchema.Version}",
            $"compiler-source-grants:{string.Join('|', _compilerOwnedSourcePaths.Select(SourcePathNormalizer.NormalizeForCacheKey).Order(StringComparer.Ordinal))}",
            $"meta-configuration:{_options.MetaConfiguration?.Fingerprint ?? string.Empty}"
        ]);
    }

    private CompilationLiveStateSnapshot CreateLiveStateSnapshot() =>
        new(
            _ast,
            _symbolTable,
            _nameResolver,
            _typeInferer,
            _abilityInferer,
            _hirModule,
            _mirModule,
            _borrowMirModule,
            _hirParameterEffects,
            _hirCopyLikeTypeIds,
            _hirDynamicTypeKeys,
            _hirTypeDescriptors,
            _hirConstructorLayouts,
            _moduleMemberIndexSnapshot,
            _implOverlapCheckSnapshot,
            _typeDirectedCallableResolutionSnapshot,
            _associatedTypeProjectionSnapshot,
            _associatedConstProjectionSnapshot,
            _traitCheckSnapshot,
            _mirFunctionFingerprints,
            _moduleMirArtifactSnapshot);

    private void RefreshCompilationLiveStatePayload(CompilationPhase phase)
    {
        var payload = CompilationLiveStatePayload.Create(
            _sourceCode,
            GetLiveStateFlagsHash(),
            _symbolTable,
            _typeInferer,
            _ast,
            _hirModule,
            _hirParameterEffects,
            _hirCopyLikeTypeIds,
            _hirDynamicTypeKeys,
            _hirTypeDescriptors,
            _hirConstructorLayouts,
            _mirModule);

        var validation = _compilationLiveStatePayload?.ValidateAgainst(payload);
        _compilationLiveStatePayload = payload;

        SetProfilingCounter($"Build.liveStatePayload.{phase}.present", 1);
        SetProfilingCounter($"Build.liveStatePayload.{phase}.hash", StableCounterFromHash(payload.PayloadHash));
        SetProfilingCounter($"Build.liveStatePayload.{phase}.symbolHash", StableCounterFromHash(payload.SymbolTable.Hash));
        SetProfilingCounter($"Build.liveStatePayload.{phase}.moduleRegistryHash", StableCounterFromHash(payload.ModuleRegistry.Hash));
        SetProfilingCounter($"Build.liveStatePayload.{phase}.typeSubstitutionHash", StableCounterFromHash(payload.TypeSubstitution.Hash));
        SetProfilingCounter($"Build.liveStatePayload.{phase}.astInferredTypesHash", StableCounterFromHash(payload.AstInferredTypes.Hash));
        SetProfilingCounter($"Build.liveStatePayload.{phase}.metaQueryHash", StableCounterFromHash(payload.MetaQueries.Hash));
        SetProfilingCounter($"Build.liveStatePayload.{phase}.metaQueryCacheEntries", payload.MetaQueries.CacheEntries.Count);
        SetProfilingCounter($"Build.liveStatePayload.{phase}.metaQueryDependencies", payload.MetaQueries.Dependencies.Count);
        SetProfilingCounter($"Build.liveStatePayload.{phase}.hirGraphHash", StableCounterFromHash(payload.HirGraph.Hash));
        SetProfilingCounter($"Build.liveStatePayload.{phase}.mirGraphHash", StableCounterFromHash(payload.MirGraph.Hash));
        SetProfilingCounter($"Build.liveStatePayload.{phase}.mirStateHash", StableCounterFromHash(payload.MirState.Hash));
        SetProfilingCounter($"Build.liveStatePayload.{phase}.remapIdentity", payload.RemapPlan.IsIdentity ? 1 : 0);

        if (validation != null)
        {
            SetProfilingCounter($"Build.liveStatePayload.{phase}.validated", validation.IsValid ? 1 : 0);
            SetProfilingCounter($"Build.liveStatePayload.{phase}.restorable", validation.IsRestorable ? 1 : 0);
            SetProfilingCounter($"Build.liveStatePayload.{phase}.validationFailures", validation.Failures.Count);
        }
    }

    private void ApplyLiveStateSnapshot(CompilationLiveStateSnapshot snapshot)
    {
        _ast = snapshot.Ast;
        _symbolTable = snapshot.SymbolTable;
        _nameResolver = snapshot.NameResolver;
        _typeInferer = snapshot.TypeInferer;
        _abilityInferer = snapshot.EffectInferer;
        _hirModule = snapshot.HirModule;
        _mirModule = snapshot.MirModule;
        _borrowMirModule = snapshot.BorrowMirModule;
        _hirParameterEffects = snapshot.HirParameterEffects;
        _hirCopyLikeTypeIds = snapshot.HirCopyLikeTypeIds;
        _hirDynamicTypeKeys = snapshot.HirDynamicTypeKeys;
        _hirTypeDescriptors = snapshot.HirTypeDescriptors;
        _hirConstructorLayouts = snapshot.HirConstructorLayouts;
        _moduleMemberIndexSnapshot = snapshot.ModuleMemberIndexSnapshot;
        _implOverlapCheckSnapshot = snapshot.ImplOverlapCheckSnapshot;
        _typeDirectedCallableResolutionSnapshot = snapshot.TypeDirectedCallableResolutionSnapshot;
        _associatedTypeProjectionSnapshot = snapshot.AssociatedTypeProjectionSnapshot;
        _associatedConstProjectionSnapshot = snapshot.AssociatedConstProjectionSnapshot;
        _traitCheckSnapshot = snapshot.TraitCheckSnapshot;
        _mirFunctionFingerprints = snapshot.MirFunctionFingerprints;
        _moduleMirArtifactSnapshot = snapshot.ModuleMirArtifactSnapshot;
    }
}
