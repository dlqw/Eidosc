using Eidosc.Pipeline;

namespace Eidosc.Cli.Commands;

public static partial class BuildCommand
{
    private static T? TryLoadModuleArtifactNode<T>(
        FullBuildArtifact artifact,
        string moduleKey,
        string kind,
        string sourceHash,
        string dependencySignatureHash)
    {
        var key = CreateModuleArtifactKey(artifact, moduleKey, sourceHash, dependencySignatureHash);
        return artifact.Cache.TryReadArtifactJson<T>(key, kind, out var payload)
            ? payload
            : default;
    }

    private static ModuleNamerStatePayload? TryLoadModuleNamerStatePayloadArtifact(
        FullBuildArtifact artifact,
        string moduleKey,
        string kind,
        string sourceHash,
        string dependencySignatureHash)
    {
        var payload = TryLoadModuleArtifactNode<ModuleNamerStatePayload>(
            artifact,
            moduleKey,
            kind,
            sourceHash,
            dependencySignatureHash);
        return payload is
               {
                   SchemaVersion: ModuleNamerStatePayload.CurrentSchemaVersion
               } &&
               payload.HasValidPayloadHash()
            ? payload
            : null;
    }

    internal static ModuleTypesStatePayload? TryLoadModuleTypesStatePayloadArtifact(
        FullBuildArtifact artifact,
        string moduleKey,
        string kind,
        string sourceHash,
        string dependencySignatureHash)
    {
        var payload = TryLoadModuleArtifactNode<ModuleTypesStatePayload>(
            artifact,
            moduleKey,
            kind,
            sourceHash,
            dependencySignatureHash);
        return payload is
               {
                   SchemaVersion: ModuleTypesStatePayload.CurrentSchemaVersion
               } &&
               payload.HasValidPayloadHash()
            ? payload
            : null;
    }

    internal static ModuleHirStateArtifactPayload? TryLoadModuleHirStatePayloadArtifact(
        FullBuildArtifact artifact,
        string moduleKey,
        string kind,
        string sourceHash,
        string dependencySignatureHash)
    {
        var payload = TryLoadModuleArtifactNode<ModuleHirStateArtifactPayload>(
            artifact,
            moduleKey,
            kind,
            sourceHash,
            dependencySignatureHash);
        return payload is
               {
                   SchemaVersion: ModuleHirStateArtifactPayload.CurrentSchemaVersion
               } &&
               payload.HasValidPayloadHash()
            ? payload
            : null;
    }

    internal static ModuleMirStateArtifactPayload? TryLoadModuleMirStatePayloadArtifact(
        FullBuildArtifact artifact,
        string moduleKey,
        string kind,
        string sourceHash,
        string dependencySignatureHash)
    {
        var payload = TryLoadModuleArtifactNode<ModuleMirStateArtifactPayload>(
            artifact,
            moduleKey,
            kind,
            sourceHash,
            dependencySignatureHash);
        return payload is
               {
                   SchemaVersion: ModuleMirStateArtifactPayload.CurrentSchemaVersion
               } &&
               payload.HasValidPayloadHash()
            ? payload
            : null;
    }

    internal static void StoreLatestModuleArtifactRestorePlanSnapshotArtifact(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null || result.ModuleArtifactRestorePlan == null)
        {
            return;
        }

        ArtifactSnapshotStore.StoreJson(
            artifact.Cache,
            CreateLatestModuleArtifactRestorePlanKey(artifact),
            LatestModuleArtifactRestorePlanSnapshotArtifactKind,
            result.ModuleArtifactRestorePlan);
    }

    internal static void StoreLatestModuleTypedArtifactRestorePlanSnapshotArtifact(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null || result.ModuleTypedArtifactRestorePlan == null)
        {
            return;
        }

        ArtifactSnapshotStore.StoreJson(
            artifact.Cache,
            CreateLatestModuleTypedArtifactRestorePlanKey(artifact),
            LatestModuleTypedArtifactRestorePlanSnapshotArtifactKind,
            result.ModuleTypedArtifactRestorePlan);
    }

    internal static void StoreLatestModuleNamerStatePayloadsArtifact(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null || result.ModuleNamerStatePayloads is not { Count: > 0 } payloads)
        {
            return;
        }

        ArtifactSnapshotStore.StoreJson(
            artifact.Cache,
            CreateLatestModuleNamerStatePayloadsKey(artifact),
            LatestModuleNamerStatePayloadsArtifactKind,
            payloads);
    }

    internal static void StoreLatestModuleTypesStatePayloadsArtifact(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null || result.ModuleTypesStatePayloads is not { Count: > 0 } payloads)
        {
            return;
        }

        ArtifactSnapshotStore.StoreJson(
            artifact.Cache,
            CreateLatestModuleTypesStatePayloadsKey(artifact),
            LatestModuleTypesStatePayloadsArtifactKind,
            payloads);
    }

    internal static void StoreLatestModuleHirStatePayloadsArtifact(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null || result.ModuleHirStatePayloads is not { Count: > 0 } payloads)
        {
            return;
        }

        ArtifactSnapshotStore.StoreJson(
            artifact.Cache,
            CreateLatestModuleHirStatePayloadsKey(artifact),
            LatestModuleHirStatePayloadsArtifactKind,
            payloads);
    }

    internal static void StoreLatestModuleMirStatePayloadsArtifact(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null || result.ModuleMirStatePayloads is not { Count: > 0 } payloads)
        {
            return;
        }

        ArtifactSnapshotStore.StoreJson(
            artifact.Cache,
            CreateLatestModuleMirStatePayloadsKey(artifact),
            LatestModuleMirStatePayloadsArtifactKind,
            payloads);
    }

    internal static ProjectModuleArtifactRestorePlan? TryLoadLatestModuleArtifactRestorePlanSnapshot(FullBuildArtifact? artifact)
    {
        return TryLoadModuleArtifactRestorePlanSnapshot(
            artifact,
            CreateLatestModuleArtifactRestorePlanKey,
            LatestModuleArtifactRestorePlanSnapshotArtifactKind);
    }

    internal static ProjectModuleArtifactRestorePlan? TryLoadLatestModuleTypedArtifactRestorePlanSnapshot(FullBuildArtifact? artifact)
    {
        return TryLoadModuleArtifactRestorePlanSnapshot(
            artifact,
            CreateLatestModuleTypedArtifactRestorePlanKey,
            LatestModuleTypedArtifactRestorePlanSnapshotArtifactKind);
    }

    internal static IReadOnlyList<ModuleNamerStatePayload>? TryLoadLatestModuleNamerStatePayloads(FullBuildArtifact? artifact)
    {
        if (artifact == null)
        {
            return null;
        }

        return ArtifactSnapshotStore.TryLoadJson(
            artifact.Cache,
            CreateLatestModuleNamerStatePayloadsKey(artifact),
            LatestModuleNamerStatePayloadsArtifactKind,
            static payloads => payloads is { Count: > 0 } &&
                               payloads.All(static payload =>
                                   string.Equals(
                                       payload.SchemaVersion,
                                       ModuleNamerStatePayload.CurrentSchemaVersion,
                                       StringComparison.Ordinal) &&
                                   payload.HasValidPayloadHash()),
            out IReadOnlyList<ModuleNamerStatePayload>? payloads)
            ? payloads
            : null;
    }

    internal static IReadOnlyList<ModuleTypesStatePayload>? TryLoadLatestModuleTypesStatePayloads(FullBuildArtifact? artifact)
    {
        if (artifact == null)
        {
            return null;
        }

        return ArtifactSnapshotStore.TryLoadJson(
            artifact.Cache,
            CreateLatestModuleTypesStatePayloadsKey(artifact),
            LatestModuleTypesStatePayloadsArtifactKind,
            static payloads => payloads is { Count: > 0 } &&
                               payloads.All(static payload =>
                                   string.Equals(
                                       payload.SchemaVersion,
                                       ModuleTypesStatePayload.CurrentSchemaVersion,
                                       StringComparison.Ordinal) &&
                                   payload.HasValidPayloadHash()),
            out IReadOnlyList<ModuleTypesStatePayload>? payloads)
            ? payloads
            : null;
    }

    internal static IReadOnlyList<ModuleHirStateArtifactPayload>? TryLoadLatestModuleHirStatePayloads(FullBuildArtifact? artifact)
    {
        if (artifact == null)
        {
            return null;
        }

        return ArtifactSnapshotStore.TryLoadJson(
            artifact.Cache,
            CreateLatestModuleHirStatePayloadsKey(artifact),
            LatestModuleHirStatePayloadsArtifactKind,
            static payloads => payloads is { Count: > 0 } &&
                               payloads.All(static payload =>
                                   string.Equals(
                                       payload.SchemaVersion,
                                       ModuleHirStateArtifactPayload.CurrentSchemaVersion,
                                       StringComparison.Ordinal) &&
                                   payload.HasValidPayloadHash()),
            out IReadOnlyList<ModuleHirStateArtifactPayload>? payloads)
            ? payloads
            : null;
    }

    internal static IReadOnlyList<ModuleMirStateArtifactPayload>? TryLoadLatestModuleMirStatePayloads(FullBuildArtifact? artifact)
    {
        if (artifact == null)
        {
            return null;
        }

        return ArtifactSnapshotStore.TryLoadJson(
            artifact.Cache,
            CreateLatestModuleMirStatePayloadsKey(artifact),
            LatestModuleMirStatePayloadsArtifactKind,
            static payloads => payloads is { Count: > 0 } &&
                               payloads.All(static payload =>
                                   string.Equals(
                                       payload.SchemaVersion,
                                       ModuleMirStateArtifactPayload.CurrentSchemaVersion,
                                       StringComparison.Ordinal) &&
                                   payload.HasValidPayloadHash()),
            out IReadOnlyList<ModuleMirStateArtifactPayload>? payloads)
            ? payloads
            : null;
    }

    private static ProjectModuleArtifactRestorePlan? TryLoadModuleArtifactRestorePlanSnapshot(
        FullBuildArtifact? artifact,
        Func<FullBuildArtifact, ModuleArtifactKey> keyFactory,
        string artifactKind)
    {
        if (artifact == null)
        {
            return null;
        }

        return ArtifactSnapshotStore.TryLoadJson(
            artifact.Cache,
            keyFactory(artifact),
            artifactKind,
            static snapshot => snapshot.Layers != null &&
                               string.Equals(
                                   snapshot.SchemaVersion,
                                   ProjectModuleArtifactRestorePlan.CurrentSchemaVersion,
                                   StringComparison.Ordinal),
            out ProjectModuleArtifactRestorePlan? snapshot)
            ? snapshot
            : null;
    }

    private static CompilationResult CreateFullBuildArtifactCacheHitResult(
        FullBuildArtifact artifact,
        TimeSpan elapsed,
        bool outputIndependentHit)
    {
        var counterName = artifact.Target switch
        {
            CompileTarget.Native => "Build.artifactCache.nativeFullBuild.hits",
            CompileTarget.LlvmIr => "Build.artifactCache.llvmIrFullBuild.hits",
            CompileTarget.Typed => "Build.artifactCache.typedAnalysisFullBuild.hits",
            CompileTarget.Resolved => "Build.artifactCache.resolvedAnalysisFullBuild.hits",
            _ => "Build.artifactCache.analysisFullBuild.hits"
        };
        var outputIndependentCounterName = artifact.Target switch
        {
            CompileTarget.Native => "Build.artifactCache.nativeFullBuild.outputIndependentHits",
            CompileTarget.LlvmIr => "Build.artifactCache.llvmIrFullBuild.outputIndependentHits",
            CompileTarget.Typed => "Build.artifactCache.typedAnalysisFullBuild.outputIndependentHits",
            CompileTarget.Resolved => "Build.artifactCache.resolvedAnalysisFullBuild.outputIndependentHits",
            _ => "Build.artifactCache.analysisFullBuild.outputIndependentHits"
        };
        var completedPhase = artifact.Target switch
        {
            CompileTarget.Resolved => CompilationPhase.Namer,
            CompileTarget.Typed => CompilationPhase.Types,
            _ => CompilationPhase.Llvm
        };

        return new CompilationResult
        {
            Success = true,
            CompletedPhase = completedPhase,
            InputFile = artifact.OutputPath,
            TotalTime = elapsed,
            PhaseTimes = new Dictionary<CompilationPhase, TimeSpan>
            {
                [completedPhase] = elapsed
            },
            ProfilingCounters = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                [counterName] = 1,
                [outputIndependentCounterName] = outputIndependentHit ? 1 : 0
            }
        };
    }

    private static CompilationResult CreateFullBuildArtifactCacheDiagnosticResult(
        FullBuildArtifact artifact,
        TimeSpan elapsed,
        bool outputIndependentHit)
    {
        var counterName = artifact.Target switch
        {
            CompileTarget.Native => "Build.artifactCache.nativeFullBuild.hits",
            CompileTarget.LlvmIr => "Build.artifactCache.llvmIrFullBuild.hits",
            CompileTarget.Typed => "Build.artifactCache.typedAnalysisFullBuild.hits",
            CompileTarget.Resolved => "Build.artifactCache.resolvedAnalysisFullBuild.hits",
            _ => "Build.artifactCache.analysisFullBuild.hits"
        };
        var outputIndependentCounterName = artifact.Target switch
        {
            CompileTarget.Native => "Build.artifactCache.nativeFullBuild.outputIndependentHits",
            CompileTarget.LlvmIr => "Build.artifactCache.llvmIrFullBuild.outputIndependentHits",
            CompileTarget.Typed => "Build.artifactCache.typedAnalysisFullBuild.outputIndependentHits",
            CompileTarget.Resolved => "Build.artifactCache.resolvedAnalysisFullBuild.outputIndependentHits",
            _ => "Build.artifactCache.analysisFullBuild.outputIndependentHits"
        };
        var semanticSnapshot = artifact.Target is CompileTarget.Typed or CompileTarget.Resolved
            ? TryLoadModuleSemanticSignatureSnapshot(artifact)
            : TryLoadLatestModuleSemanticSignatureSnapshot(artifact);
        var typedSnapshot = artifact.Target == CompileTarget.Typed
            ? TryLoadModuleTypedSemanticSignatureSnapshot(artifact)
            : TryLoadLatestModuleTypedSemanticSignatureSnapshot(artifact);
        var dependencySignatureSnapshot = TryLoadLatestModuleDependencySignatureSnapshot(artifact);
        var memberIndexSnapshot = TryLoadLatestModuleMemberIndexSnapshot(artifact);
        var memberIndexRestorePlan = TryLoadLatestModuleMemberIndexRestorePlanSnapshot(artifact);
        var memberIndexRestorePayload = ProjectModuleMemberIndexRestorePayloadSnapshot.Load(
            memberIndexRestorePlan,
            memberIndexSnapshot);
        if (memberIndexRestorePlan != null)
        {
            memberIndexRestorePlan = memberIndexRestorePlan.GateWithPayload(memberIndexRestorePayload);
        }
        var implOverlapSnapshot = TryLoadLatestImplOverlapCheckSnapshot(artifact);
        var associatedTypeProjectionSnapshot = artifact.Target is CompileTarget.Typed or CompileTarget.LlvmIr or CompileTarget.Native
            ? TryLoadLatestAssociatedTypeProjectionSnapshot(artifact)
            : null;
        var associatedConstProjectionSnapshot = artifact.Target is CompileTarget.Typed or CompileTarget.LlvmIr or CompileTarget.Native
            ? TryLoadLatestAssociatedConstProjectionSnapshot(artifact)
            : null;
        var traitCheckSnapshot = artifact.Target is CompileTarget.Typed or CompileTarget.LlvmIr or CompileTarget.Native
            ? TryLoadLatestTraitCheckSnapshot(artifact)
            : null;
        var sendAnalysisSnapshot = artifact.Target is CompileTarget.LlvmIr or CompileTarget.Native
            ? TryLoadLatestSendAnalysisSnapshot(artifact)
            : null;
        var borrowDiagnosticSnapshot = artifact.Target is CompileTarget.LlvmIr or CompileTarget.Native
            ? TryLoadLatestBorrowDiagnosticSnapshot(artifact)
            : null;
        var borrowCodegenHintsSnapshot = artifact.Target is CompileTarget.LlvmIr or CompileTarget.Native
            ? TryLoadLatestBorrowCodegenHintsSnapshot(artifact)
            : null;
        var mirSnapshot = artifact.Target is CompileTarget.LlvmIr or CompileTarget.Native
            ? TryLoadLatestModuleMirArtifactSnapshot(artifact)
            : null;
        var restorePlan = TryLoadLatestModuleArtifactRestorePlanSnapshot(artifact);
        var typedRestorePlan = artifact.Target is CompileTarget.LlvmIr or CompileTarget.Native
            ? TryLoadLatestModuleTypedArtifactRestorePlanSnapshot(artifact)
            : null;
        var restorePayload = restorePlan != null && semanticSnapshot != null
            ? ProjectModuleArtifactRestorePayloadSnapshot.LoadSemantic(
                restorePlan,
                semanticSnapshot,
                (moduleKey, kind, sourceHash, dependencyHash) =>
                    TryLoadModuleArtifactNode<ProjectModuleSemanticSignatureNode>(
                        artifact,
                        moduleKey,
                        kind,
                        sourceHash,
                        dependencyHash))
            : null;
        if (restorePlan != null && restorePayload != null)
        {
            restorePlan = restorePlan.GateWithPayload(restorePayload);
        }

        var typedRestorePayload = typedRestorePlan != null &&
                                  semanticSnapshot != null &&
                                  typedSnapshot != null &&
                                  mirSnapshot != null
            ? ProjectModuleArtifactRestorePayloadSnapshot.Load(
                typedRestorePlan,
                semanticSnapshot,
                typedSnapshot,
                mirSnapshot,
                (moduleKey, kind, sourceHash, dependencyHash) =>
                    TryLoadModuleArtifactNode<ProjectModuleSemanticSignatureNode>(
                        artifact,
                        moduleKey,
                        kind,
                        sourceHash,
                        dependencyHash),
                (moduleKey, kind, sourceHash, dependencyHash) =>
                    TryLoadModuleArtifactNode<ProjectModuleTypedSemanticNode>(
                        artifact,
                        moduleKey,
                        kind,
                        sourceHash,
                        dependencyHash),
                (moduleKey, kind, sourceHash, dependencyHash) =>
                    TryLoadModuleArtifactNode<ProjectModuleMirArtifactNode>(
                        artifact,
                        moduleKey,
                        kind,
                        sourceHash,
                        dependencyHash))
            : null;
        if (typedRestorePlan != null && typedRestorePayload != null)
        {
            typedRestorePlan = typedRestorePlan.GateWithPayload(typedRestorePayload);
        }
        var completedPhase = artifact.Target switch
        {
            CompileTarget.Resolved => CompilationPhase.Namer,
            CompileTarget.Typed => CompilationPhase.Types,
            _ => CompilationPhase.Llvm
        };
        var counters = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            [counterName] = 1,
            [outputIndependentCounterName] = outputIndependentHit ? 1 : 0
        };
        if (semanticSnapshot != null)
        {
            counters["Build.artifactCache.moduleSemanticSignatures.modules"] = semanticSnapshot.Nodes.Count;
        }

        if (typedSnapshot != null)
        {
            counters["Build.artifactCache.moduleTypedSemanticSignatures.modules"] = typedSnapshot.Nodes.Count;
        }

        if (dependencySignatureSnapshot != null)
        {
            counters["Build.artifactCache.moduleDependencySignatures.modules"] =
                dependencySignatureSnapshot.Nodes.Count;
            counters["Build.artifactCache.moduleDependencySignatures.semanticAvailableModules"] =
                dependencySignatureSnapshot.Nodes.Count(static node => node.SemanticAvailable);
            counters["Build.artifactCache.moduleDependencySignatures.typedAvailableModules"] =
                dependencySignatureSnapshot.Nodes.Count(static node => node.TypedAvailable);
            counters["Build.artifactCache.moduleDependencySignatures.memberIndexAvailableModules"] =
                dependencySignatureSnapshot.Nodes.Count(static node => node.MemberIndexAvailable);
            counters["Build.artifactCache.moduleDependencySignatures.mirAvailableModules"] =
                dependencySignatureSnapshot.Nodes.Count(static node => node.MirAvailable);
        }

        if (memberIndexSnapshot != null)
        {
            counters["Build.artifactCache.moduleMemberIndex.modules"] = memberIndexSnapshot.Nodes.Count;
            counters["Build.artifactCache.moduleMemberIndex.members"] =
                memberIndexSnapshot.Nodes.Sum(static node => node.Members.Count);
            counters["Build.artifactCache.moduleMemberIndex.accessibleBindings"] =
                memberIndexSnapshot.Nodes.Sum(static node => node.AccessibleBindings.Count);
        }

        if (memberIndexRestorePlan != null)
        {
            counters["Build.artifactCache.moduleMemberIndexRestorePlan.modules"] =
                memberIndexRestorePlan.TotalModules;
            counters["Build.artifactCache.moduleMemberIndexRestorePlan.restoreModules"] =
                memberIndexRestorePlan.RestoreModules;
            counters["Build.artifactCache.moduleMemberIndexRestorePlan.rebuildModules"] =
                memberIndexRestorePlan.RebuildModules;
            counters["Build.artifactCache.moduleMemberIndexRestorePlan.addedModules"] =
                memberIndexRestorePlan.AddedModules;
            counters["Build.artifactCache.moduleMemberIndexRestorePlan.removedModules"] =
                memberIndexRestorePlan.RemovedModules;
            counters["Build.artifactCache.moduleMemberIndexRestorePayload.restoreModules"] =
                memberIndexRestorePayload.RestoreModules;
            counters["Build.artifactCache.moduleMemberIndexRestorePayload.loadedModules"] =
                memberIndexRestorePayload.LoadedModules;
            counters["Build.artifactCache.moduleMemberIndexRestorePayload.validatedModules"] =
                memberIndexRestorePayload.ValidatedModules;
            counters["Build.artifactCache.moduleMemberIndexRestorePayload.staleModules"] =
                memberIndexRestorePayload.StaleModules;
            counters["Build.artifactCache.moduleMemberIndexRestorePayload.missingModules"] =
                memberIndexRestorePayload.MissingModules;
        }

        if (implOverlapSnapshot != null)
        {
            counters["Build.artifactCache.implOverlapChecks.entries"] = implOverlapSnapshot.Entries.Count;
            counters["Build.artifactCache.implOverlapChecks.conflicts"] =
                implOverlapSnapshot.Entries.Count(static entry => entry.HasConflict);
        }

        if (traitCheckSnapshot != null)
        {
            counters["Build.artifactCache.traitCheck.entries"] = traitCheckSnapshot.Entries.Count;
        }

        if (associatedTypeProjectionSnapshot != null)
        {
            counters["Build.artifactCache.associatedTypeProjection.entries"] =
                associatedTypeProjectionSnapshot.Entries.Count;
        }

        if (associatedConstProjectionSnapshot != null)
        {
            counters["Build.artifactCache.associatedConstProjection.entries"] =
                associatedConstProjectionSnapshot.Entries.Count;
        }

        if (sendAnalysisSnapshot != null)
        {
            counters["Build.artifactCache.sendAnalysis.functions"] = sendAnalysisSnapshot.Functions.Count;
            counters["Build.artifactCache.sendAnalysis.errors"] =
                sendAnalysisSnapshot.Functions.Sum(static function => function.Errors.Count);
        }

        if (borrowDiagnosticSnapshot != null)
        {
            counters["Build.artifactCache.borrowDiagnostics.functions"] = borrowDiagnosticSnapshot.Functions.Count;
            counters["Build.artifactCache.borrowDiagnostics.diagnostics"] =
                borrowDiagnosticSnapshot.Functions.Sum(static function => function.Diagnostics.Count);
        }

        if (borrowCodegenHintsSnapshot != null)
        {
            counters["Build.artifactCache.borrowCodegenHints.functions"] = borrowCodegenHintsSnapshot.Functions.Count;
            counters["Build.artifactCache.borrowCodegenHints.perceusFunctions"] =
                borrowCodegenHintsSnapshot.Functions.Count(static function => function.Perceus != null);
            counters["Build.artifactCache.borrowCodegenHints.reuseFunctions"] =
                borrowCodegenHintsSnapshot.Functions.Count(static function => function.Reuse != null);
            counters["Build.artifactCache.borrowCodegenHints.stackPromotionFunctions"] =
                borrowCodegenHintsSnapshot.Functions.Count(static function => function.StackPromotion != null);
            counters["Build.artifactCache.borrowCodegenHints.unifiedStackPromotionFunctions"] =
                borrowCodegenHintsSnapshot.Functions.Count(static function => function.UnifiedStackPromotion != null);
        }

        if (mirSnapshot != null)
        {
            counters["Build.artifactCache.moduleMirArtifacts.modules"] = mirSnapshot.Nodes.Count;
        }

        if (restorePlan != null)
        {
            counters["Build.artifactCache.moduleArtifactRestore.modules"] = restorePlan.TotalModules;
            counters["Build.artifactCache.moduleArtifactRestore.restoreModules"] = restorePlan.RestoreModules;
            counters["Build.artifactCache.moduleArtifactRestore.blockedModules"] = restorePlan.BlockedModules;
            counters["Build.artifactCache.moduleArtifactRestore.readyArtifactModules"] = restorePlan.ReadyArtifactModules;
            counters["Build.artifactCache.moduleArtifactRestore.compileModules"] = restorePlan.CompileModules;
        }

        if (restorePayload != null)
        {
            counters["Build.artifactCache.moduleArtifactRestorePayload.restoreModules"] =
                restorePayload.RestoreModules;
            counters["Build.artifactCache.moduleArtifactRestorePayload.loadedModules"] =
                restorePayload.LoadedModules;
            counters["Build.artifactCache.moduleArtifactRestorePayload.validatedModules"] =
                restorePayload.ValidatedModules;
            counters["Build.artifactCache.moduleArtifactRestorePayload.staleModules"] =
                restorePayload.StaleModules;
            counters["Build.artifactCache.moduleArtifactRestorePayload.missingModules"] =
                restorePayload.MissingModules;
            counters["Build.artifactCache.moduleArtifactRestorePayload.failedModules"] =
                restorePayload.FailedModules;
        }

        if (typedRestorePlan != null)
        {
            counters["Build.artifactCache.moduleTypedArtifactRestore.modules"] = typedRestorePlan.TotalModules;
            counters["Build.artifactCache.moduleTypedArtifactRestore.restoreModules"] = typedRestorePlan.RestoreModules;
            counters["Build.artifactCache.moduleTypedArtifactRestore.blockedModules"] = typedRestorePlan.BlockedModules;
            counters["Build.artifactCache.moduleTypedArtifactRestore.readyArtifactModules"] = typedRestorePlan.ReadyArtifactModules;
            counters["Build.artifactCache.moduleTypedArtifactRestore.compileModules"] = typedRestorePlan.CompileModules;
        }

        if (typedRestorePayload != null)
        {
            counters["Build.artifactCache.moduleTypedArtifactRestorePayload.restoreModules"] =
                typedRestorePayload.RestoreModules;
            counters["Build.artifactCache.moduleTypedArtifactRestorePayload.loadedModules"] =
                typedRestorePayload.LoadedModules;
            counters["Build.artifactCache.moduleTypedArtifactRestorePayload.validatedModules"] =
                typedRestorePayload.ValidatedModules;
            counters["Build.artifactCache.moduleTypedArtifactRestorePayload.staleModules"] =
                typedRestorePayload.StaleModules;
            counters["Build.artifactCache.moduleTypedArtifactRestorePayload.missingModules"] =
                typedRestorePayload.MissingModules;
            counters["Build.artifactCache.moduleTypedArtifactRestorePayload.failedModules"] =
                typedRestorePayload.FailedModules;
        }

        var restoreExecution = restorePlan == null
            ? null
            : ProjectModuleArtifactRestoreExecutor.Execute(restorePlan, restorePayload);
        var typedRestoreExecution = typedRestorePlan == null
            ? null
            : ProjectModuleArtifactRestoreExecutor.Execute(typedRestorePlan, typedRestorePayload);
        if (restoreExecution != null)
        {
            counters["Build.artifactCache.moduleArtifactRestoreExecution.restoredModules"] =
                restoreExecution.RestoredModules;
            counters["Build.artifactCache.moduleArtifactRestoreExecution.blockedModules"] =
                restoreExecution.BlockedModules;
            counters["Build.artifactCache.moduleArtifactRestoreExecution.compiledModules"] =
                restoreExecution.CompiledModules;
            counters["Build.artifactCache.moduleArtifactRestoreExecution.readyArtifactModules"] =
                restoreExecution.ReadyArtifactModules;
            AddModuleStageExecutionCounters(counters, "Namer", restoreExecution);
        }

        if (typedRestoreExecution != null)
        {
            counters["Build.artifactCache.moduleTypedArtifactRestoreExecution.restoredModules"] =
                typedRestoreExecution.RestoredModules;
            counters["Build.artifactCache.moduleTypedArtifactRestoreExecution.blockedModules"] =
                typedRestoreExecution.BlockedModules;
            counters["Build.artifactCache.moduleTypedArtifactRestoreExecution.compiledModules"] =
                typedRestoreExecution.CompiledModules;
            counters["Build.artifactCache.moduleTypedArtifactRestoreExecution.readyArtifactModules"] =
                typedRestoreExecution.ReadyArtifactModules;
            AddModuleStageExecutionCounters(counters, "Types", typedRestoreExecution);
            AddModuleStageExecutionCounters(counters, "Hir", typedRestoreExecution);
            AddModuleStageExecutionCounters(counters, "Mir", typedRestoreExecution);
        }

        return new CompilationResult
        {
            Success = true,
            CompletedPhase = completedPhase,
            InputFile = artifact.OutputPath,
            ModuleSemanticSignatureSnapshot = semanticSnapshot,
            ModuleTypedSemanticSnapshot = typedSnapshot,
            ModuleDependencySignatureSnapshot = dependencySignatureSnapshot,
            ModuleMemberIndexSnapshot = memberIndexSnapshot,
            ModuleMemberIndexRestorePlan = memberIndexRestorePlan,
            ModuleMemberIndexRestorePayload = memberIndexRestorePlan == null ? null : memberIndexRestorePayload,
            ImplOverlapCheckSnapshot = implOverlapSnapshot,
            ModuleMirArtifactSnapshot = mirSnapshot,
            ModuleArtifactRestorePlan = restorePlan,
            ModuleArtifactRestoreExecution = restoreExecution,
            ModuleArtifactRestorePayload = restorePayload,
            ModuleTypedArtifactRestorePlan = typedRestorePlan,
            ModuleTypedArtifactRestoreExecution = typedRestoreExecution,
            ModuleTypedArtifactRestorePayload = typedRestorePayload,
            AssociatedTypeProjectionSnapshot = associatedTypeProjectionSnapshot,
            AssociatedConstProjectionSnapshot = associatedConstProjectionSnapshot,
            TraitCheckSnapshot = traitCheckSnapshot,
            SendAnalysisSnapshot = sendAnalysisSnapshot,
            BorrowDiagnosticSnapshot = borrowDiagnosticSnapshot,
            BorrowCodegenHintsSnapshot = borrowCodegenHintsSnapshot,
            TotalTime = elapsed,
            PhaseTimes = new Dictionary<CompilationPhase, TimeSpan>
            {
                [completedPhase] = elapsed
            },
            ProfilingCounters = counters
        };
    }

    private static void AddModuleStageExecutionCounters(
        Dictionary<string, long> counters,
        string stageName,
        ProjectModuleArtifactRestoreExecutionSnapshot snapshot)
    {
        var prefix = $"Build.moduleStage.{stageName}";
        counters[$"{prefix}.realTaskExecution"] = snapshot.HasRealTaskExecution ? 1 : 0;
        counters[$"{prefix}.modules"] = snapshot.TotalModules;
        counters[$"{prefix}.failedModules"] = snapshot.FailedModules;
        counters[$"{prefix}.skippedModules"] = snapshot.SkippedModules;
        if (!snapshot.HasRealTaskExecution)
        {
            counters[$"{prefix}.restoredModules"] = 0;
            counters[$"{prefix}.compiledModules"] = 0;
            counters[$"{prefix}.blockedModules"] = 0;
            counters[$"{prefix}.readyArtifactModules"] = 0;
            return;
        }

        counters[$"{prefix}.restoredModules"] = snapshot.RestoredModules;
        counters[$"{prefix}.compiledModules"] = snapshot.CompiledModules;
        counters[$"{prefix}.blockedModules"] = snapshot.BlockedModules;
        counters[$"{prefix}.readyArtifactModules"] = snapshot.ReadyArtifactModules;
    }
}
