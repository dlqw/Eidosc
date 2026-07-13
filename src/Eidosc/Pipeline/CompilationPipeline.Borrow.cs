using Eidosc.Symbols;
using System.Collections.Frozen;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Types;
using Eidosc.Borrow;
using Eidosc.Diagnostic;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Pipeline;

public sealed partial class CompilationPipeline
{
    private bool RunBorrowChecker()
    {
        using (MeasureSubphase(CompilationPhase.Borrow, "create_result"))
        {
            _borrowCheckResult = new ModuleBorrowCheckResult();
        }

        MirModule borrowModule;
        using (MeasureSubphase(CompilationPhase.Borrow, "resolve_borrow_module"))
        {
            borrowModule = _borrowMirModule ?? _mirModule!;
            borrowModule = CreateBorrowAnalysisModule(borrowModule);
        }
        if (TryRestoreBorrowDiagnosticsForBorrowStop())
        {
            return true;
        }

        if (TryRestoreBorrowCodegenHintsForFullCodegen())
        {
            return true;
        }
        if (_debugContext.IsEnabled)
        {
            using (MeasureSubphase(CompilationPhase.Borrow, "debug_emit_borrow_mir"))
            {
                _debugContext.Emit("borrow_mir", MirFormatter.FormatMir(borrowModule));
            }
        }
        LoanSignatureCache signatureCache;
        Dictionary<string, LoanSignature> inferredSignatures;
        List<(MirFunc Func, LoanSignatureInferer Inferer)> inferers;
        Dictionary<MirFunc, LoanSignatureInferer> infererByFunc;
        BorrowModuleAnalysisContext borrowAnalysisContext;
        bool runStackPromotionHints;
        using (MeasureSubphase(CompilationPhase.Borrow, "create_shared_analysis_state"))
        {
            signatureCache = new LoanSignatureCache();
            inferredSignatures = new Dictionary<string, LoanSignature>();
            inferers = new List<(MirFunc Func, LoanSignatureInferer Inferer)>();
            infererByFunc = new Dictionary<MirFunc, LoanSignatureInferer>();
            borrowAnalysisContext = new BorrowModuleAnalysisContext(borrowModule);
            runStackPromotionHints = ShouldRunStackPromotionHints();
            SetProfilingCounter("Borrow.stack_promotion_hints.enabled", runStackPromotionHints ? 1 : 0);
        }

        // C3: Build CFG once per function and share across all borrow analyzers
        var cfgByFunc = new Dictionary<MirFunc, ControlFlowGraph>();

        foreach (var func in borrowModule.Functions)
        {
            var profileName = string.IsNullOrWhiteSpace(func.Name) ? $"func:{func.SymbolId.Value}" : $"func:{func.Name}";
            using (MeasureSubphase(CompilationPhase.Borrow, $"{profileName}.signature_preinfer"))
            {
                var cfg = new ControlFlowGraph(func);
                cfgByFunc[func] = cfg;
                var inferer = new LoanSignatureInferer(func, signatureCache, _symbolTable!, borrowModule.DynamicTypeKeys, cfg);
                inferers.Add((func, inferer));
                infererByFunc[func] = inferer;
                inferer.Infer(includeCallConstraints: false, force: true);
            }
        }

        foreach (var (func, inferer) in inferers)
        {
            var profileName = string.IsNullOrWhiteSpace(func.Name) ? $"func:{func.SymbolId.Value}" : $"func:{func.Name}";
            using (MeasureSubphase(CompilationPhase.Borrow, $"{profileName}.signature_finalize"))
            {
                var signature = inferer.Infer(includeCallConstraints: true, force: true);
                if (!string.IsNullOrEmpty(func.Name))
                {
                    inferredSignatures[borrowAnalysisContext.GetStableKey(func)] = signature;
                }
            }
        }

        Dictionary<SymbolId, BorrowCapabilitySnapshot> capabilitySnapshots;
        using (MeasureSubphase(CompilationPhase.Borrow, "build_capability_snapshots"))
        {
            capabilitySnapshots = BuildBorrowCapabilitySnapshots();
        }

        IReadOnlyDictionary<string, FieldEscapeSummary> fieldEscapeSummaries = new Dictionary<string, FieldEscapeSummary>();
        if (runStackPromotionHints)
        {
            using (MeasureSubphase(CompilationPhase.Borrow, "module_field_escape_analyze"))
            {
                var moduleFieldEscapeAnalyzer = new ModuleFieldEscapeAnalyzer(borrowModule, borrowAnalysisContext);
                moduleFieldEscapeAnalyzer.Analyze();
                fieldEscapeSummaries = moduleFieldEscapeAnalyzer.Summaries;
                AddModuleFieldEscapeStats(moduleFieldEscapeAnalyzer.Stats);
            }
        }
        else
        {
            AddProfilingCounter("Borrow.module_field_escape.skipped_by_gating", 1);
        }

        var restoredBorrowDiagnostics = CreateBorrowDiagnosticRestoreMap(
            borrowModule,
            inferredSignatures,
            signatureCache,
            capabilitySnapshots);
        var restoredBorrowCodegenHints = CreateBorrowCodegenHintRestoreMap(
            borrowModule,
            fieldEscapeSummaries,
            restoredBorrowDiagnostics);
        var currentBorrowSnapshotIdentity = _mirFunctionFingerprints == null
            ? null
            : BorrowSnapshotFunctionIdentity.Create(_mirFunctionFingerprints);

        foreach (var func in borrowModule.Functions)
        {
            var profileName = string.IsNullOrWhiteSpace(func.Name) ? $"func:{func.SymbolId.Value}" : $"func:{func.Name}";
            if (TryRestoreBorrowFunction(
                    func,
                    profileName,
                    currentBorrowSnapshotIdentity,
                    restoredBorrowDiagnostics,
                    restoredBorrowCodegenHints,
                    out var restoredResult))
            {
                _borrowCheckResult.AddResult(restoredResult);
                continue;
            }

            capabilitySnapshots.TryGetValue(func.SymbolId, out var capabilitySnapshot);
            var captureBorrowPointStates = _debugContext.IsEnabled;
            var sharedCfg = cfgByFunc.GetValueOrDefault(func);

            VariableUsageAnalyzer usageAnalyzer;
            using (MeasureSubphase(CompilationPhase.Borrow, $"{profileName}.usage_analyze"))
            {
                usageAnalyzer = new VariableUsageAnalyzer(func);
                usageAnalyzer.Analyze();
            }

            LivenessAnalyzer livenessAnalyzer;
            using (MeasureSubphase(CompilationPhase.Borrow, $"{profileName}.liveness_analyze"))
            {
                livenessAnalyzer = new LivenessAnalyzer(func, usageAnalyzer, sharedCfg);
                livenessAnalyzer.Analyze();
            }

            AffineTypeChecker affineChecker;
            using (MeasureSubphase(CompilationPhase.Borrow, $"{profileName}.affine_check"))
            {
                affineChecker = new AffineTypeChecker(func, usageAnalyzer, captureBorrowPointStates, borrowModule.DynamicTypeKeys, sharedCfg);
                affineChecker.Check();
            }

            BorrowChecker borrowChecker;
            using (MeasureSubphase(CompilationPhase.Borrow, $"{profileName}.borrow_check"))
            {
                borrowChecker = new BorrowChecker(
                    func,
                    livenessAnalyzer,
                    signatureCache,
                    _symbolTable,
                    capabilitySnapshot,
                    captureBorrowPointStates,
                    borrowModule.DynamicTypeKeys,
                    sharedCfg);
                borrowChecker.Check();
            }

            PerceusAnalyzer perceusAnalyzer;
            using (MeasureSubphase(CompilationPhase.Borrow, $"{profileName}.perceus_analyze"))
            {
                perceusAnalyzer = new PerceusAnalyzer(func, livenessAnalyzer, usageAnalyzer);
                perceusAnalyzer.Analyze();
            }

            ReuseAnalyzer? reuseAnalyzer = null;
            using (MeasureSubphase(CompilationPhase.Borrow, $"{profileName}.reuse_analyze"))
            {
                reuseAnalyzer = new ReuseAnalyzer(func, perceusAnalyzer.Hints);
                reuseAnalyzer.Analyze();
            }

            StackPromotionAnalyzer? stackPromotionAnalyzer = null;
            using (MeasureSubphase(CompilationPhase.Borrow, $"{profileName}.stack_promotion_analyze"))
            {
                if (runStackPromotionHints)
                {
                    stackPromotionAnalyzer = new StackPromotionAnalyzer(func);
                    if (StackPromotionAnalyzer.MayHavePromotableConstructorCalls(func))
                    {
                        stackPromotionAnalyzer.Analyze();
                        AddProfilingCounter("Borrow.stack_promotion.analyzed_functions", 1);
                    }
                    else
                    {
                        AddProfilingCounter("Borrow.stack_promotion.skipped_functions", 1);
                    }
                }
                else
                {
                    AddProfilingCounter("Borrow.stack_promotion.skipped_by_gating", 1);
                }
            }

            UnifiedStackPromotionAnalyzer? unifiedStackPromotionAnalyzer = null;
            using (MeasureSubphase(CompilationPhase.Borrow, $"{profileName}.unified_stack_promotion_analyze"))
            {
                if (runStackPromotionHints)
                {
                    unifiedStackPromotionAnalyzer = new UnifiedStackPromotionAnalyzer(func, fieldEscapeSummaries, borrowAnalysisContext);
                    if (UnifiedStackPromotionAnalyzer.MayHavePromotableAllocations(func, borrowAnalysisContext))
                    {
                        unifiedStackPromotionAnalyzer.Analyze();
                        AddProfilingCounter("Borrow.unified_stack_promotion.analyzed_functions", 1);
                        AddUnifiedStackPromotionStats(unifiedStackPromotionAnalyzer.Stats);
                    }
                    else
                    {
                        AddProfilingCounter("Borrow.unified_stack_promotion.skipped_functions", 1);
                    }
                }
                else
                {
                    AddProfilingCounter("Borrow.unified_stack_promotion.skipped_by_gating", 1);
                }
            }

            LoanConstraintVerifier loanVerifier;
            List<LoanConstraintResult> loanResults;
            using (MeasureSubphase(CompilationPhase.Borrow, $"{profileName}.loan_verify"))
            {
                loanVerifier = new LoanConstraintVerifier(
                    signatureCache,
                    _symbolTable!,
                    capabilitySnapshot,
                    captureBorrowPointStates,
                    borrowModule.DynamicTypeKeys);
                loanResults = loanVerifier.VerifyFunction(func, sharedCfg);
            }

            LoanSignature? loanSignature = null;
            using (MeasureSubphase(CompilationPhase.Borrow, $"{profileName}.resolve_signature"))
            {
                if (func.SymbolId.IsValid)
                {
                    loanSignature = signatureCache.GetSignature(func.SymbolId);
                }

                if (loanSignature == null && !string.IsNullOrEmpty(func.Name))
                {
                    inferredSignatures.TryGetValue(borrowAnalysisContext.GetStableKey(func), out loanSignature);
                }
            }

            BorrowCheckResult funcResult;
            using (MeasureSubphase(CompilationPhase.Borrow, $"{profileName}.assemble_result"))
            {
                funcResult = new BorrowCheckResult
                {
                    FunctionName = func.Name,
                    FunctionSymbolId = func.SymbolId,
                    LivenessAnalyzer = livenessAnalyzer,
                    AffineTypeChecker = affineChecker,
                    BorrowChecker = borrowChecker,
                    LoanSignature = loanSignature,
                    LoanConstraintVerifier = loanVerifier,
                    LoanConstraintResults = loanResults,
                    PerceusAnalyzer = perceusAnalyzer,
                    PerceusHints = perceusAnalyzer.Hints,
                    ReuseAnalyzer = reuseAnalyzer,
                    ReuseHints = reuseAnalyzer?.Hints,
                    StackPromotionAnalyzer = stackPromotionAnalyzer,
                    StackPromotionHints = stackPromotionAnalyzer?.Hints,
                    UnifiedStackPromotionAnalyzer = unifiedStackPromotionAnalyzer,
                    UnifiedStackPromotionHints = unifiedStackPromotionAnalyzer?.Hints
                };

                _borrowCheckResult.AddResult(funcResult);
            }

            using (MeasureSubphase(CompilationPhase.Borrow, $"{profileName}.append_diagnostics"))
            {
                AppendBorrowPhaseDiagnostics(func.Name, infererByFunc[func], affineChecker, borrowChecker, loanVerifier);
            }

            if (_debugContext.IsEnabled)
            {
                using (MeasureSubphase(CompilationPhase.Borrow, $"{profileName}.debug_emit"))
                {
                    _debugContext.Emit($"{func.Name}_liveness", BorrowFormatter.FormatLiveness(livenessAnalyzer));
                    _debugContext.Emit($"{func.Name}_variable_states", BorrowFormatter.FormatVariableStates(affineChecker));

                    if (affineChecker.Diagnostics.Count > 0)
                    {
                        _debugContext.Emit($"{func.Name}_affine_errors", BorrowFormatter.FormatAffineErrors(affineChecker));
                    }

                    _debugContext.Emit($"{func.Name}_active_borrows", BorrowFormatter.FormatActiveBorrows(borrowChecker));
                    _debugContext.Emit($"{func.Name}_borrow_aliases", BorrowFormatter.FormatBorrowAliasStates(borrowChecker));

                    if (borrowChecker.Diagnostics.Count > 0)
                    {
                        _debugContext.Emit($"{func.Name}_borrow_errors", BorrowFormatter.FormatBorrowErrors(borrowChecker));
                    }

                    if (loanSignature != null)
                    {
                        _debugContext.Emit($"{func.Name}_loan_signature", BorrowFormatter.FormatLoanSignature(loanSignature));
                    }

                    if (infererByFunc[func].Diagnostics.Count > 0)
                    {
                        _debugContext.Emit(
                            $"{func.Name}_loan_signature_errors",
                            BorrowFormatter.FormatBorrowDiagnostics(
                                infererByFunc[func].Diagnostics,
                                PipelineMessages.LoanSignatureInferenceErrorsTitle));
                    }

                    if (loanVerifier.Diagnostics.Count > 0)
                    {
                        _debugContext.Emit($"{func.Name}_loan_constraint_errors", BorrowFormatter.FormatLoanConstraintErrors(loanVerifier));
                    }

                    _debugContext.Emit($"{func.Name}_loan_constraint_states", BorrowFormatter.FormatLoanConstraintStates(loanVerifier));
                    if (capabilitySnapshot != null)
                    {
                        _debugContext.Emit($"{func.Name}_borrow_capabilities", capabilitySnapshot.ToDebugString());
                    }

                    _debugContext.Emit($"{func.Name}_perceus_hints", BorrowFormatter.FormatPerceusHints(perceusAnalyzer));
                }
            }
        }

        _borrowDiagnosticDependencyHash ??= ComputeBorrowDiagnosticDependencyHash(
            borrowModule,
            inferredSignatures,
            signatureCache,
            capabilitySnapshots);
        _borrowCodegenDependencyHash ??= ComputeBorrowCodegenDependencyHash(
            _borrowDiagnosticDependencyHash,
            fieldEscapeSummaries);
        BuildBorrowDiagnosticSnapshot();
        BuildBorrowCodegenHintsSnapshot();
        return !_borrowCheckResult.HasErrors;
    }

    private bool TryRestoreBorrowDiagnosticsForBorrowStop()
    {
        if (!_options.EnableDetailedProfiling ||
            _options.StopAtPhase != CompilationPhase.Borrow ||
            _mirModule == null ||
            _options.PreviousBorrowDiagnosticSnapshot == null)
        {
            return false;
        }

        if (_mirFunctionFingerprints == null)
        {
            _mirFunctionFingerprints = MirFunctionFingerprintSnapshot.FromModule(_mirModule);
        }

        var previous = _options.PreviousBorrowDiagnosticSnapshot;
        if (!string.Equals(
                previous.SchemaVersion,
                BorrowDiagnosticSnapshot.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            SetProfilingCounter("Borrow.previous_build.diagnostic_restore_schema_match", 0);
            return false;
        }

        if (!string.Equals(
                previous.MirModuleFingerprint,
                _mirFunctionFingerprints.ModuleFingerprint,
                StringComparison.Ordinal))
        {
            SetProfilingCounter("Borrow.previous_build.diagnostic_restore_module_fingerprint_match", 0);
            return false;
        }

        var diagnosticCount = previous.Functions.Sum(static function => function.Diagnostics.Count);
        var loanConstraintFailures = previous.Functions.Sum(static function => function.LoanConstraintFailures);
        if (diagnosticCount != 0 || loanConstraintFailures != 0)
        {
            SetProfilingCounter("Borrow.previous_build.diagnostic_restore_clean_snapshot", 0);
            return false;
        }

        using (MeasureSubphase(CompilationPhase.Borrow, "restore_borrow_diagnostics_from_previous_snapshot"))
        {
            _borrowDiagnosticSnapshot = previous;
        }

        SetProfilingCounter("Borrow.previous_build.diagnostic_restore_hits", 1);
        SetProfilingCounter("Borrow.previous_build.diagnostic_restore_schema_match", 1);
        SetProfilingCounter("Borrow.previous_build.diagnostic_restore_module_fingerprint_match", 1);
        SetProfilingCounter("Borrow.previous_build.diagnostic_restore_clean_snapshot", 1);
        SetProfilingCounter("Borrow.previous_build.diagnostic_restore_functions", previous.Functions.Count);
        SetProfilingCounter("Borrow.previous_build.diagnostic_restore_diagnostics", diagnosticCount);
        SetProfilingCounter("Borrow.previous_build.diagnostic_restore_loanConstraintFailures", loanConstraintFailures);
        SetProfilingCounter("Borrow.diagnosticSnapshot.functions", previous.Functions.Count);
        SetProfilingCounter("Borrow.diagnosticSnapshot.diagnostics", diagnosticCount);
        SetProfilingCounter("Borrow.diagnosticSnapshot.loanConstraintFailures", loanConstraintFailures);
        return true;
    }

    private Dictionary<string, BorrowDiagnosticFunctionSnapshot>? CreateBorrowDiagnosticRestoreMap(
        MirModule borrowModule,
        IReadOnlyDictionary<string, LoanSignature> inferredSignatures,
        LoanSignatureCache signatureCache,
        IReadOnlyDictionary<SymbolId, BorrowCapabilitySnapshot> capabilitySnapshots)
    {
        if (!_options.EnableDetailedProfiling ||
            _options.StopAtPhase is not (CompilationPhase.Borrow or CompilationPhase.Llvm) ||
            _debugContext.IsEnabled ||
            _mirModule == null ||
            _options.PreviousBorrowDiagnosticSnapshot == null)
        {
            return null;
        }

        if (_mirFunctionFingerprints == null)
        {
            _mirFunctionFingerprints = MirFunctionFingerprintSnapshot.FromModule(_mirModule);
        }

        _borrowDiagnosticDependencyHash = ComputeBorrowDiagnosticDependencyHash(
            borrowModule,
            inferredSignatures,
            signatureCache,
            capabilitySnapshots);

        var previous = _options.PreviousBorrowDiagnosticSnapshot;
        var schemaMatch = string.Equals(
            previous.SchemaVersion,
            BorrowDiagnosticSnapshot.CurrentSchemaVersion,
            StringComparison.Ordinal);
        SetProfilingCounter("Borrow.previous_build.diagnostic_mixed_restore_schema_match", schemaMatch ? 1 : 0);
        if (!schemaMatch)
        {
            return null;
        }

        var dependencyHashMatch = string.Equals(
            previous.BorrowDependencyHash,
            _borrowDiagnosticDependencyHash,
            StringComparison.Ordinal);
        SetProfilingCounter("Borrow.previous_build.diagnostic_mixed_restore_dependency_hash_match", dependencyHashMatch ? 1 : 0);
        if (!dependencyHashMatch)
        {
            return null;
        }

        var previousByFunctionKey = previous.Functions.ToDictionary(
            static function => function.FunctionKey,
            StringComparer.Ordinal);
        SetProfilingCounter("Borrow.previous_build.diagnostic_mixed_restore_candidates", previousByFunctionKey.Count);
        return previousByFunctionKey;
    }

    private Dictionary<string, BorrowCodegenHintsFunctionSnapshot>? CreateBorrowCodegenHintRestoreMap(
        MirModule borrowModule,
        IReadOnlyDictionary<string, FieldEscapeSummary> fieldEscapeSummaries,
        IReadOnlyDictionary<string, BorrowDiagnosticFunctionSnapshot>? previousDiagnosticsByFunctionKey)
    {
        if (!_options.EnableDetailedProfiling ||
            _options.StopAtPhase != CompilationPhase.Llvm ||
            _debugContext.IsEnabled ||
            _mirModule == null ||
            _options.PreviousBorrowDiagnosticSnapshot == null ||
            _options.PreviousBorrowCodegenHintsSnapshot == null ||
            previousDiagnosticsByFunctionKey == null)
        {
            return null;
        }

        _borrowCodegenDependencyHash = ComputeBorrowCodegenDependencyHash(
            _borrowDiagnosticDependencyHash ?? "",
            fieldEscapeSummaries);

        var hints = _options.PreviousBorrowCodegenHintsSnapshot;
        var schemaMatch = string.Equals(
            hints.SchemaVersion,
            BorrowCodegenHintsSnapshot.CurrentSchemaVersion,
            StringComparison.Ordinal);
        SetProfilingCounter("Borrow.previous_build.codegen_hint_mixed_restore_schema_match", schemaMatch ? 1 : 0);
        if (!schemaMatch)
        {
            return null;
        }

        var dependencyHashMatch = string.Equals(
            hints.BorrowCodegenDependencyHash,
            _borrowCodegenDependencyHash,
            StringComparison.Ordinal);
        SetProfilingCounter("Borrow.previous_build.codegen_hint_mixed_restore_dependency_hash_match", dependencyHashMatch ? 1 : 0);
        if (!dependencyHashMatch)
        {
            return null;
        }

        var previousByFunctionKey = hints.Functions.ToDictionary(
            static function => function.FunctionKey,
            StringComparer.Ordinal);
        SetProfilingCounter("Borrow.previous_build.codegen_hint_mixed_restore_candidates", previousByFunctionKey.Count);
        return previousByFunctionKey;
    }

    private bool TryRestoreBorrowFunction(
        MirFunc func,
        string profileName,
        BorrowSnapshotFunctionIdentity? currentIdentity,
        IReadOnlyDictionary<string, BorrowDiagnosticFunctionSnapshot>? previousByFunctionKey,
        IReadOnlyDictionary<string, BorrowCodegenHintsFunctionSnapshot>? previousHintsByFunctionKey,
        out BorrowCheckResult result)
    {
        result = null!;
        if (previousByFunctionKey == null || _mirFunctionFingerprints == null || currentIdentity == null)
        {
            return false;
        }

        var restoreCodegenHints = _options.StopAtPhase == CompilationPhase.Llvm;
        var functionKey = currentIdentity.ResolveFunctionKey(func);
        var currentFingerprint = _mirFunctionFingerprints.Functions.FirstOrDefault(
            fingerprint => string.Equals(fingerprint.FunctionKey, functionKey, StringComparison.Ordinal));
        if (currentFingerprint == null ||
            !previousByFunctionKey.TryGetValue(functionKey, out var previous) ||
            !string.Equals(previous.BodyHash, currentFingerprint.BodyHash, StringComparison.Ordinal) ||
            previous.Diagnostics.Count != 0 ||
            previous.LoanConstraintFailures != 0)
        {
            AddProfilingCounter("Borrow.previous_build.diagnostic_mixed_restore_rebuild_functions", 1);
            if (restoreCodegenHints)
            {
                AddProfilingCounter("Borrow.previous_build.codegen_hint_mixed_restore_rebuild_functions", 1);
            }
            return false;
        }

        BorrowCodegenHintsFunctionSnapshot? previousHints = null;
        if (restoreCodegenHints &&
            (previousHintsByFunctionKey == null ||
             !previousHintsByFunctionKey.TryGetValue(functionKey, out previousHints) ||
             !string.Equals(previousHints.BodyHash, currentFingerprint.BodyHash, StringComparison.Ordinal)))
        {
            AddProfilingCounter("Borrow.previous_build.codegen_hint_mixed_restore_rebuild_functions", 1);
            return false;
        }

        using (MeasureSubphase(CompilationPhase.Borrow, $"{profileName}.restore_diagnostics"))
        {
            result = previousHints == null
                ? new BorrowCheckResult
                {
                    FunctionName = func.Name,
                    FunctionSymbolId = func.SymbolId
                }
                : previousHints.ToBorrowCheckResult(func.Name, func.SymbolId);
        }

        if (previousHints != null)
        {
            AddProfilingCounter("Borrow.previous_build.codegen_hint_mixed_restore_functions", 1);
            AddProfilingCounter("Borrow.previous_build.codegen_hint_mixed_restore_hits", 1);
            if (previousHints.Perceus != null)
            {
                AddProfilingCounter("Borrow.previous_build.codegen_hint_mixed_restore_perceus_functions", 1);
            }

            if (previousHints.Reuse != null)
            {
                AddProfilingCounter("Borrow.previous_build.codegen_hint_mixed_restore_reuse_functions", 1);
            }

            if (previousHints.StackPromotion != null)
            {
                AddProfilingCounter("Borrow.previous_build.codegen_hint_mixed_restore_stackPromotion_functions", 1);
            }

            if (previousHints.UnifiedStackPromotion != null)
            {
                AddProfilingCounter("Borrow.previous_build.codegen_hint_mixed_restore_unifiedStackPromotion_functions", 1);
            }
        }

        AddProfilingCounter("Borrow.previous_build.diagnostic_mixed_restore_functions", 1);
        AddProfilingCounter("Borrow.previous_build.diagnostic_mixed_restore_hits", 1);
        return true;
    }

    private bool TryRestoreBorrowCodegenHintsForFullCodegen()
    {
        if (!_options.EnableDetailedProfiling ||
            _options.StopAtPhase != CompilationPhase.Llvm ||
            _mirModule == null ||
            _options.PreviousBorrowDiagnosticSnapshot == null ||
            _options.PreviousBorrowCodegenHintsSnapshot == null)
        {
            return false;
        }

        if (_mirFunctionFingerprints == null)
        {
            _mirFunctionFingerprints = MirFunctionFingerprintSnapshot.FromModule(_mirModule);
        }

        var diagnostics = _options.PreviousBorrowDiagnosticSnapshot;
        var hints = _options.PreviousBorrowCodegenHintsSnapshot;
        if (!string.Equals(
                diagnostics.SchemaVersion,
                BorrowDiagnosticSnapshot.CurrentSchemaVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                hints.SchemaVersion,
                BorrowCodegenHintsSnapshot.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            SetProfilingCounter("Borrow.previous_build.codegen_hint_restore_schema_match", 0);
            return false;
        }

        if (!string.Equals(
                diagnostics.MirModuleFingerprint,
                _mirFunctionFingerprints.ModuleFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                hints.MirModuleFingerprint,
                _mirFunctionFingerprints.ModuleFingerprint,
                StringComparison.Ordinal))
        {
            SetProfilingCounter("Borrow.previous_build.codegen_hint_restore_module_fingerprint_match", 0);
            return false;
        }

        var diagnosticCount = diagnostics.Functions.Sum(static function => function.Diagnostics.Count);
        var loanConstraintFailures = diagnostics.Functions.Sum(static function => function.LoanConstraintFailures);
        if (diagnosticCount != 0 || loanConstraintFailures != 0)
        {
            SetProfilingCounter("Borrow.previous_build.codegen_hint_restore_clean_snapshot", 0);
            return false;
        }

        var dependencyHashPresent = !string.IsNullOrEmpty(hints.BorrowCodegenDependencyHash);
        SetProfilingCounter("Borrow.previous_build.codegen_hint_restore_dependency_hash_present", dependencyHashPresent ? 1 : 0);
        if (!dependencyHashPresent)
        {
            return false;
        }

        using (MeasureSubphase(CompilationPhase.Borrow, "restore_borrow_codegen_hints_from_previous_snapshot"))
        {
            _borrowCheckResult = hints.ToBorrowCheckResult();
            _borrowDiagnosticSnapshot = diagnostics;
            _borrowCodegenHintsSnapshot = hints;
        }

        SetProfilingCounter("Borrow.previous_build.codegen_hint_restore_hits", 1);
        SetProfilingCounter("Borrow.previous_build.codegen_hint_restore_schema_match", 1);
        SetProfilingCounter("Borrow.previous_build.codegen_hint_restore_module_fingerprint_match", 1);
        SetProfilingCounter("Borrow.previous_build.codegen_hint_restore_clean_snapshot", 1);
        SetProfilingCounter("Borrow.previous_build.codegen_hint_restore_functions", hints.Functions.Count);
        SetProfilingCounter("Borrow.previous_build.codegen_hint_restore_perceus_functions", hints.Functions.Count(static function => function.Perceus != null));
        SetProfilingCounter("Borrow.previous_build.codegen_hint_restore_reuse_functions", hints.Functions.Count(static function => function.Reuse != null));
        SetProfilingCounter("Borrow.previous_build.codegen_hint_restore_stackPromotion_functions", hints.Functions.Count(static function => function.StackPromotion != null));
        SetProfilingCounter("Borrow.previous_build.codegen_hint_restore_unifiedStackPromotion_functions", hints.Functions.Count(static function => function.UnifiedStackPromotion != null));
        SetBorrowDiagnosticCounters(diagnostics);
        SetBorrowCodegenHintsCounters(hints);
        return true;
    }

    private void BuildBorrowDiagnosticSnapshot()
    {
        if (!_options.EnableDetailedProfiling ||
            _borrowCheckResult == null ||
            _mirModule == null)
        {
            return;
        }

        if (_mirFunctionFingerprints == null)
        {
            _mirFunctionFingerprints = MirFunctionFingerprintSnapshot.FromModule(_mirModule);
        }

        using (MeasureSubphase(CompilationPhase.Borrow, "borrow_diagnostic_snapshot"))
        {
            _borrowDiagnosticSnapshot = BorrowDiagnosticSnapshot.Create(
                _mirFunctionFingerprints,
                _borrowDiagnosticDependencyHash ?? "",
                _borrowCheckResult);
        }

        SetProfilingCounter("Borrow.diagnosticSnapshot.functions", _borrowDiagnosticSnapshot.Functions.Count);
        SetBorrowDiagnosticCounters(_borrowDiagnosticSnapshot);
    }

    private void BuildBorrowCodegenHintsSnapshot()
    {
        if (!_options.EnableDetailedProfiling ||
            _borrowCheckResult == null ||
            _mirModule == null)
        {
            return;
        }

        if (_mirFunctionFingerprints == null)
        {
            _mirFunctionFingerprints = MirFunctionFingerprintSnapshot.FromModule(_mirModule);
        }

        using (MeasureSubphase(CompilationPhase.Borrow, "borrow_codegen_hints_snapshot"))
        {
            _borrowCodegenHintsSnapshot = BorrowCodegenHintsSnapshot.Create(
                _mirFunctionFingerprints,
                _borrowCodegenDependencyHash ?? "",
                _borrowCheckResult);
        }

        SetBorrowCodegenHintsCounters(_borrowCodegenHintsSnapshot);
    }

    private void SetBorrowDiagnosticCounters(BorrowDiagnosticSnapshot snapshot)
    {
        SetProfilingCounter("Borrow.diagnosticSnapshot.functions", snapshot.Functions.Count);
        SetProfilingCounter(
            "Borrow.diagnosticSnapshot.diagnostics",
            snapshot.Functions.Sum(static function => function.Diagnostics.Count));
        SetProfilingCounter(
            "Borrow.diagnosticSnapshot.loanConstraintFailures",
            snapshot.Functions.Sum(static function => function.LoanConstraintFailures));
    }

    private void SetBorrowCodegenHintsCounters(BorrowCodegenHintsSnapshot snapshot)
    {
        SetProfilingCounter("Borrow.codegenHintsSnapshot.functions", snapshot.Functions.Count);
        SetProfilingCounter(
            "Borrow.codegenHintsSnapshot.perceusFunctions",
            snapshot.Functions.Count(static function => function.Perceus != null));
        SetProfilingCounter(
            "Borrow.codegenHintsSnapshot.reuseFunctions",
            snapshot.Functions.Count(static function => function.Reuse != null));
        SetProfilingCounter(
            "Borrow.codegenHintsSnapshot.stackPromotionFunctions",
            snapshot.Functions.Count(static function => function.StackPromotion != null));
        SetProfilingCounter(
            "Borrow.codegenHintsSnapshot.unifiedStackPromotionFunctions",
            snapshot.Functions.Count(static function => function.UnifiedStackPromotion != null));
    }

    private bool ShouldRunStackPromotionHints()
    {
        return !_options.StopAtPhase.HasValue ||
               _options.StopAtPhase.Value == CompilationPhase.Llvm;
    }

    private string ComputeBorrowDiagnosticDependencyHash(
        MirModule module,
        IReadOnlyDictionary<string, LoanSignature> inferredSignatures,
        LoanSignatureCache signatureCache,
        IReadOnlyDictionary<SymbolId, BorrowCapabilitySnapshot> capabilitySnapshots)
    {
        var context = new BorrowModuleAnalysisContext(module);
        var payload = new
        {
            Schema = "borrow-diagnostic-dependency-v1",
            TypeDescriptors = module.TypeDescriptors
                .OrderBy(static pair => pair.Key)
                .Select(static pair => new
                {
                    Id = pair.Key,
                    Descriptor = pair.Value.ToString()
                })
                .ToArray(),
            DynamicTypeKeys = module.DynamicTypeKeys
                .OrderBy(static pair => pair.Key)
                .ToArray(),
            Functions = module.Functions
                .Select(function =>
                {
                    var functionKey = MirFunctionIdentity.GetStableKey(function);
                    var loanSignature = function.SymbolId.IsValid
                        ? signatureCache.GetSignature(function.SymbolId)
                        : null;
                    if (loanSignature == null)
                    {
                        inferredSignatures.TryGetValue(context.GetStableKey(function), out loanSignature);
                    }

                    capabilitySnapshots.TryGetValue(function.SymbolId, out var capabilitySnapshot);
                    return new
                    {
                        Key = functionKey,
                        function.ReturnType.Value,
                        Parameters = function.Locals
                            .Where(static local => local.IsParameter)
                            .OrderBy(static local => local.Id.Value)
                            .Select(static local => new
                            {
                                Id = local.Id.Value,
                                TypeId = local.TypeId.Value,
                                local.IsMutable,
                                BindingMode = (int)local.BindingMode
                            })
                            .ToArray(),
                        LoanSignature = loanSignature?.ToString() ?? "",
                        Capabilities = capabilitySnapshot?.ToDebugString() ?? ""
                    };
                })
                .OrderBy(static item => item.Key, StringComparer.Ordinal)
                .ToArray()
        };

        return ModuleArtifactHash.ComputeJsonHash(payload);
    }

    private static string ComputeBorrowCodegenDependencyHash(
        string borrowDiagnosticDependencyHash,
        IReadOnlyDictionary<string, FieldEscapeSummary> fieldEscapeSummaries)
    {
        var payload = new
        {
            Schema = "borrow-codegen-dependency-v1",
            BorrowDiagnosticDependencyHash = borrowDiagnosticDependencyHash,
            FieldEscapeSummaries = fieldEscapeSummaries
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => new
                {
                    FunctionKey = pair.Key,
                    pair.Value.FunctionName,
                    FunctionSymbolId = pair.Value.FunctionSymbolId.Value,
                    pair.Value.IsRecursive,
                    ParamEscapes = pair.Value.ParamEscapes
                        .OrderBy(static escape => escape.Key)
                        .Select(static escape => new
                        {
                            Parameter = escape.Key,
                            escape.Value.FullyEscapes,
                            Fields = escape.Value.FieldEscapes
                                .Where(static field => field.Value)
                                .Select(static field => field.Key)
                                .Order()
                                .ToArray()
                        })
                        .ToArray()
                })
                .ToArray()
        };

        return ModuleArtifactHash.ComputeJsonHash(payload);
    }

    private MirModule CreateBorrowAnalysisModule(MirModule borrowModule)
    {
        if (_mirModule == null ||
            ReferenceEquals(borrowModule, _mirModule) ||
            borrowModule.Functions.Count == 0)
        {
            AddProfilingCounter("Borrow.function_filter.input_functions", borrowModule.Functions.Count);
            AddProfilingCounter("Borrow.function_filter.output_functions", borrowModule.Functions.Count);
            AddProfilingCounter("Borrow.function_filter.skipped_trusted_precompiled", 0);
            return borrowModule;
        }

        var functionByStableKey = new Dictionary<string, int>(borrowModule.Functions.Count, StringComparer.Ordinal);
        for (var i = 0; i < borrowModule.Functions.Count; i++)
        {
            functionByStableKey[MirFunctionIdentity.GetStableKey(borrowModule.Functions[i])] = i;
        }

        var finalFunctionKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var function in _mirModule.Functions)
        {
            finalFunctionKeys.Add(MirFunctionIdentity.GetStableKey(function));
        }

        var keptIndices = new HashSet<int>();
        var pending = new Queue<int>();
        for (var i = 0; i < borrowModule.Functions.Count; i++)
        {
            var function = borrowModule.Functions[i];
            var stableKey = MirFunctionIdentity.GetStableKey(function);
            if (!IsTrustedPrecompiledBorrowFunction(function) ||
                finalFunctionKeys.Contains(stableKey))
            {
                if (keptIndices.Add(i))
                {
                    pending.Enqueue(i);
                }
            }
        }

        while (pending.Count > 0)
        {
            var function = borrowModule.Functions[pending.Dequeue()];
            VisitBorrowFunctionRefs(
                function,
                functionRef =>
                {
                    var stableKey = MirFunctionIdentity.GetStableKey(functionRef);
                    if (functionByStableKey.TryGetValue(stableKey, out var calleeIndex) &&
                        keptIndices.Add(calleeIndex))
                    {
                        pending.Enqueue(calleeIndex);
                    }
                });
        }

        if (keptIndices.Count == borrowModule.Functions.Count)
        {
            AddProfilingCounter("Borrow.function_filter.input_functions", borrowModule.Functions.Count);
            AddProfilingCounter("Borrow.function_filter.output_functions", borrowModule.Functions.Count);
            AddProfilingCounter("Borrow.function_filter.skipped_trusted_precompiled", 0);
            return borrowModule;
        }

        var filteredFunctions = new List<MirFunc>(keptIndices.Count);
        for (var i = 0; i < borrowModule.Functions.Count; i++)
        {
            if (keptIndices.Contains(i))
            {
                filteredFunctions.Add(borrowModule.Functions[i]);
            }
        }

        AddProfilingCounter("Borrow.function_filter.input_functions", borrowModule.Functions.Count);
        AddProfilingCounter("Borrow.function_filter.output_functions", filteredFunctions.Count);
        AddProfilingCounter("Borrow.function_filter.skipped_trusted_precompiled", borrowModule.Functions.Count - filteredFunctions.Count);

        return new MirModule
        {
            Name = borrowModule.Name,
            PackageAlias = borrowModule.PackageAlias,
            PackageInstanceKey = borrowModule.PackageInstanceKey,
            Path = borrowModule.Path.ToList(),
            Functions = filteredFunctions,
            DynamicTypeKeys = new Dictionary<int, string>(borrowModule.DynamicTypeKeys),
            TypeDescriptors = new Dictionary<int, TypeDescriptor>(borrowModule.TypeDescriptors),
            LinkLibraries = borrowModule.LinkLibraries.ToList(),
            Span = borrowModule.Span,
            CStructAccessors = new Dictionary<string, CStructAccessorInfo>(borrowModule.CStructAccessors),
            ConstructorLayouts = borrowModule.ConstructorLayouts.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.ToList()),
            TraitImpls = borrowModule.TraitImpls.ToList(),
            TraitInfos = borrowModule.TraitInfos.ToList(),
            TypeAliases = borrowModule.TypeAliases.ToList(),
            TypeConstructors = borrowModule.TypeConstructors.ToList(),
            SpecializationFailures = borrowModule.SpecializationFailures.ToList()
        };
    }

    private static bool IsTrustedPrecompiledBorrowFunction(MirFunc function)
    {
        var filePath = function.Span.FilePath;
        return Eidosc.Semantic.PrecompiledModuleRegistry.IsStdlibSourcePath(filePath);
    }

    private static void VisitBorrowFunctionRefs(MirFunc function, Action<MirFunctionRef> visitor)
    {
        foreach (var block in function.BasicBlocks)
        {
            foreach (var instruction in block.Instructions)
            {
                VisitBorrowFunctionRefs(instruction, visitor);
            }

            if (block.Terminator != null)
            {
                VisitBorrowFunctionRefs(block.Terminator, visitor);
            }
        }
    }

    private static void VisitBorrowFunctionRefs(MirInstruction instruction, Action<MirFunctionRef> visitor)
    {
        switch (instruction)
        {
            case MirAssign assign:
                VisitBorrowFunctionRefs(assign.Target, visitor);
                VisitBorrowFunctionRefs(assign.Source, visitor);
                break;
            case MirCall call:
                VisitBorrowFunctionRefs(call.Function, visitor);
                if (call.Target != null)
                {
                    VisitBorrowFunctionRefs(call.Target, visitor);
                }
                VisitBorrowFunctionRefs(call.Arguments, visitor);
                break;
            case MirBinOp binOp:
                VisitBorrowFunctionRefs(binOp.Target, visitor);
                VisitBorrowFunctionRefs(binOp.Left, visitor);
                VisitBorrowFunctionRefs(binOp.Right, visitor);
                break;
            case MirUnaryOp unaryOp:
                VisitBorrowFunctionRefs(unaryOp.Target, visitor);
                VisitBorrowFunctionRefs(unaryOp.Operand, visitor);
                break;
            case MirLoad load:
                VisitBorrowFunctionRefs(load.Target, visitor);
                VisitBorrowFunctionRefs(load.Source, visitor);
                break;
            case MirStore store:
                VisitBorrowFunctionRefs(store.Target, visitor);
                VisitBorrowFunctionRefs(store.Value, visitor);
                break;
            case MirDrop drop:
                VisitBorrowFunctionRefs(drop.Value, visitor);
                break;
            case MirCopy copy:
                VisitBorrowFunctionRefs(copy.Target, visitor);
                VisitBorrowFunctionRefs(copy.Source, visitor);
                break;
            case MirMove move:
                VisitBorrowFunctionRefs(move.Target, visitor);
                VisitBorrowFunctionRefs(move.Source, visitor);
                break;
            case MirAlloc alloc:
                VisitBorrowFunctionRefs(alloc.Target, visitor);
                break;
        }
    }

    private static void VisitBorrowFunctionRefs(MirTerminator terminator, Action<MirFunctionRef> visitor)
    {
        switch (terminator)
        {
            case MirReturn { Value: not null } ret:
                VisitBorrowFunctionRefs(ret.Value, visitor);
                break;
            case MirSwitch sw:
                VisitBorrowFunctionRefs(sw.Discriminant, visitor);
                foreach (var branch in sw.Branches)
                {
                    VisitBorrowFunctionRefs(branch.Value, visitor);
                }
                break;
        }
    }

    private static void VisitBorrowFunctionRefs(IReadOnlyList<MirOperand> operands, Action<MirFunctionRef> visitor)
    {
        for (var i = 0; i < operands.Count; i++)
        {
            VisitBorrowFunctionRefs(operands[i], visitor);
        }
    }

    private static void VisitBorrowFunctionRefs(MirOperand operand, Action<MirFunctionRef> visitor)
    {
        switch (operand)
        {
            case MirFunctionRef functionRef:
                visitor(functionRef);
                break;
            case MirPlace place:
                VisitBorrowFunctionRefs(place, visitor);
                break;
        }
    }

    private static void VisitBorrowFunctionRefs(MirPlace place, Action<MirFunctionRef> visitor)
    {
        if (place.Base != null)
        {
            VisitBorrowFunctionRefs(place.Base, visitor);
        }

        if (place.Index != null)
        {
            VisitBorrowFunctionRefs(place.Index, visitor);
        }
    }

    private void AddModuleFieldEscapeStats(ModuleFieldEscapeAnalysisStats stats)
    {
        AddProfilingCounter("Borrow.module_field_escape.functions", stats.Functions);
        AddProfilingCounter("Borrow.module_field_escape.call_edges", stats.CallEdges);
        AddProfilingCounter("Borrow.module_field_escape.self_recursive_functions", stats.SelfRecursiveFunctions);
        AddProfilingCounter("Borrow.module_field_escape.recursive_functions", stats.RecursiveFunctions);
        AddProfilingCounter("Borrow.module_field_escape.scc_count", stats.SccCount);
        AddProfilingCounter("Borrow.module_field_escape.recursive_scc_count", stats.RecursiveSccCount);
        AddProfilingCounter("Borrow.module_field_escape.summaries", stats.Summaries);
        AddProfilingCounter("Borrow.module_field_escape.param_escape_entries", stats.ParamEscapeEntries);
        AddProfilingCounter("Borrow.module_field_escape.alias_edges", stats.AliasEdges);
        AddProfilingCounter("Borrow.module_field_escape.fully_escaped_locals", stats.FullyEscapedLocals);
        AddProfilingCounter("Borrow.module_field_escape.field_escaped_locals", stats.FieldEscapedLocals);
    }

    private void AddUnifiedStackPromotionStats(UnifiedStackPromotionAnalysisStats stats)
    {
        AddProfilingCounter("Borrow.unified_stack_promotion.instructions_scanned", stats.InstructionsScanned);
        AddProfilingCounter("Borrow.unified_stack_promotion.constructor_candidates", stats.ConstructorCandidates);
        AddProfilingCounter("Borrow.unified_stack_promotion.closure_lookups", stats.ClosureLookups);
        AddProfilingCounter("Borrow.unified_stack_promotion.closure_lookup_misses", stats.ClosureLookupMisses);
        AddProfilingCounter("Borrow.unified_stack_promotion.closure_candidates", stats.ClosureCandidates);
        AddProfilingCounter("Borrow.unified_stack_promotion.alias_edges", stats.AliasEdges);
        AddProfilingCounter("Borrow.unified_stack_promotion.escaped_locals", stats.EscapedLocals);
        AddProfilingCounter("Borrow.unified_stack_promotion.promoted_allocations", stats.PromotedAllocations);
        AddProfilingCounter("Borrow.unified_stack_promotion.managed_field_checks", stats.ManagedFieldChecks);
    }

    private bool RunSendCheck()
    {
        if (_mirModule == null) return false;

        bool hasErrors = false;
        var currentFingerprints = _mirFunctionFingerprints;
        if (_options.EnableDetailedProfiling && currentFingerprints == null)
        {
            currentFingerprints = MirFunctionFingerprintSnapshot.FromModule(_mirModule);
            _mirFunctionFingerprints = currentFingerprints;
        }

        var sendDependencyHash = SendAnalysisSnapshot.ComputeDependencyHash(_mirModule);
        var fingerprintByFunctionKey = currentFingerprints == null
            ? null
            : CreateUniqueMirFingerprintLookup(currentFingerprints.Functions);
        var previousSnapshot = _options.PreviousSendAnalysisSnapshot;
        var previousByFunctionKey = previousSnapshot == null
            ? null
            : CreateUniqueSendAnalysisLookup(previousSnapshot.Functions);
        var canRestoreFromPrevious = _options.EnableDetailedProfiling &&
                                     currentFingerprints != null &&
                                     previousSnapshot != null &&
                                     previousByFunctionKey != null &&
                                     string.Equals(
                                         previousSnapshot.SchemaVersion,
                                         SendAnalysisSnapshot.CurrentSchemaVersion,
                                         StringComparison.Ordinal) &&
                                     string.Equals(
                                         previousSnapshot.SendDependencyHash,
                                         sendDependencyHash,
                                         StringComparison.Ordinal);
        var snapshotFunctions = _options.EnableDetailedProfiling
            ? new List<SendAnalysisFunctionSnapshot>(_mirModule.Functions.Count)
            : null;

        SetProfilingCounter("Send.previous_build.cache_available", canRestoreFromPrevious ? 1 : 0);
        SetProfilingCounter("Send.previous_build.dependency_hash_match", canRestoreFromPrevious ? 1 : 0);

        foreach (var func in _mirModule.Functions)
        {
            var functionKey = MirFunctionIdentity.GetStableKey(func);
            var bodyHash = fingerprintByFunctionKey != null &&
                           fingerprintByFunctionKey.TryGetValue(functionKey, out var fingerprint)
                ? fingerprint.BodyHash
                : MirFunctionFingerprintBuilder.Compute(func).BodyHash;
            IReadOnlyList<SendAnalysisErrorSnapshot> errors;

            if (canRestoreFromPrevious &&
                previousByFunctionKey!.TryGetValue(functionKey, out var previousFunction) &&
                string.Equals(previousFunction.BodyHash, bodyHash, StringComparison.Ordinal))
            {
                errors = previousFunction.Errors;
                AddProfilingCounter("Send.previous_build.restore_functions", 1);
                AddProfilingCounter("Send.previous_build.cache_hits", 1);
                AddProfilingCounter("Send.previous_build.restored_errors", errors.Count);
            }
            else
            {
                var checker = new SendChecker(func, _mirModule);
                checker.Check();
                errors = checker.Errors
                    .Select(SendAnalysisErrorSnapshot.FromError)
                    .ToArray();
                AddProfilingCounter("Send.previous_build.rebuild_functions", 1);
                AddProfilingCounter("Send.previous_build.cache_misses", 1);
            }

            snapshotFunctions?.Add(new SendAnalysisFunctionSnapshot(functionKey, bodyHash, errors));

            if (errors.Count > 0)
            {
                hasErrors = true;
                foreach (var err in errors)
                {
                    _diagnostics.Add(new Diagnostic.Diagnostic(
                        Diagnostic.DiagnosticLevel.Error,
                        DiagnosticMessages.SendCheckFailed(func.Name, err.Message),
                        "E0200"));
                }
            }
        }

        if (_options.EnableDetailedProfiling && currentFingerprints != null && snapshotFunctions != null)
        {
            _sendAnalysisSnapshot = SendAnalysisSnapshot.Create(
                currentFingerprints,
                sendDependencyHash,
                snapshotFunctions
                    .OrderBy(static entry => entry.FunctionKey, StringComparer.Ordinal)
                    .ToArray());
            SetProfilingCounter("Send.snapshot.functions", _sendAnalysisSnapshot.Functions.Count);
            SetProfilingCounter("Send.snapshot.errors", _sendAnalysisSnapshot.Functions.Sum(static entry => entry.Errors.Count));
        }

        return !hasErrors;
    }

    private static IReadOnlyDictionary<string, MirFunctionFingerprint> CreateUniqueMirFingerprintLookup(
        IEnumerable<MirFunctionFingerprint> fingerprints) =>
        fingerprints
            .GroupBy(static fingerprint => fingerprint.FunctionKey, StringComparer.Ordinal)
            .Where(static group => group.Count() == 1)
            .ToDictionary(
                static group => group.Key,
                static group => group.Single(),
                StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, SendAnalysisFunctionSnapshot> CreateUniqueSendAnalysisLookup(
        IEnumerable<SendAnalysisFunctionSnapshot> functions) =>
        functions
            .GroupBy(static function => function.FunctionKey, StringComparer.Ordinal)
            .Where(static group => group.Count() == 1)
            .ToDictionary(
                static group => group.Key,
                static group => group.Single(),
                StringComparer.Ordinal);

    private Dictionary<SymbolId, BorrowCapabilitySnapshot> BuildBorrowCapabilitySnapshots()
    {
        return [];
    }

    private void AppendBorrowPhaseDiagnostics(
        string functionName,
        LoanSignatureInferer inferer,
        AffineTypeChecker affineChecker,
        BorrowChecker borrowChecker,
        LoanConstraintVerifier loanVerifier)
    {
        foreach (var diagnostic in affineChecker.Diagnostics.Select(diag => ConvertAffineDiagnostic(functionName, diag)))
        {
            _diagnostics.Add(diagnostic);
        }

        foreach (var diagnostic in inferer.Diagnostics)
        {
            _diagnostics.Add(ConvertBorrowDiagnostic(functionName, diagnostic));
        }

        var borrowCheckerDedupKeys = new HashSet<string>();
        var borrowCheckerConflictLocations = new HashSet<string>();

        foreach (var diagnostic in borrowChecker.Diagnostics)
        {
            borrowCheckerDedupKeys.Add(BuildBorrowDedupKey(diagnostic));
            if (IsBorrowConflictKind(diagnostic.Kind))
            {
                borrowCheckerConflictLocations.Add(GetMirLocationKey(diagnostic.Location));
            }
            _diagnostics.Add(ConvertBorrowDiagnostic(functionName, diagnostic));
        }

        foreach (var diagnostic in loanVerifier.Diagnostics)
        {
            if (borrowCheckerDedupKeys.Contains(BuildBorrowDedupKey(diagnostic)))
            {
                continue;
            }

            if (IsBorrowConflictKind(diagnostic.Kind) &&
                borrowCheckerConflictLocations.Contains(GetMirLocationKey(diagnostic.Location)))
            {
                continue;
            }

            _diagnostics.Add(ConvertBorrowDiagnostic(functionName, diagnostic));
        }
    }

    private static Diagnostic.Diagnostic ConvertAffineDiagnostic(string functionName, AffineDiagnostic diagnostic)
    {
        var converted = Diagnostic.Diagnostic.Error(diagnostic.Message, MapAffineCode(diagnostic.Kind))
            .WithNote(DiagnosticMessages.FunctionNote(functionName));

        if (diagnostic.Variable.IsValid)
        {
            converted.WithNote(DiagnosticMessages.LocalNote(diagnostic.Variable.Value));
        }

        if (diagnostic.FirstLocation.Block.IsValid)
        {
            converted.WithNote(DiagnosticMessages.FirstMirLocationNote(
                diagnostic.FirstLocation.Block.Value,
                diagnostic.FirstLocation.Index));
        }

        if (diagnostic.SecondLocation.Block.IsValid)
        {
            converted.WithNote(DiagnosticMessages.SecondMirLocationNote(
                diagnostic.SecondLocation.Block.Value,
                diagnostic.SecondLocation.Index));
        }

        if (HasSpan(diagnostic.Span))
        {
            converted.WithLabel(diagnostic.Span, diagnostic.Kind.ToString());
        }

        if (diagnostic.RelatedSpan is { } relatedSpan && HasSpan(relatedSpan))
        {
            converted.WithRelated(
                Diagnostic.Diagnostic.Note(DiagnosticMessages.RelatedAffineOperationNote)
                    .WithLabel(relatedSpan, DiagnosticMessages.RelatedLabel));
        }

        return converted;
    }

    private static Diagnostic.Diagnostic ConvertBorrowDiagnostic(string functionName, BorrowDiagnostic diagnostic)
    {
        var converted = Diagnostic.Diagnostic.Error(diagnostic.Message, MapBorrowCode(diagnostic.Kind))
            .WithNote(DiagnosticMessages.FunctionNote(functionName))
            .WithNote(DiagnosticMessages.MirLocationShortNote(
                diagnostic.Location.Block.Value,
                diagnostic.Location.Index));

        if (HasSpan(diagnostic.Span))
        {
            converted.WithLabel(diagnostic.Span, diagnostic.Kind.ToString());
        }

        if (diagnostic.RelatedLocation.Block.IsValid)
        {
            converted.WithNote(DiagnosticMessages.RelatedMirLocationNote(
                diagnostic.RelatedLocation.Block.Value,
                diagnostic.RelatedLocation.Index));
        }

        if (diagnostic.RelatedSpan is { } relatedSpan && HasSpan(relatedSpan))
        {
            converted.WithRelated(
                Diagnostic.Diagnostic.Note(DiagnosticMessages.RelatedBorrowNote)
                    .WithLabel(relatedSpan, DiagnosticMessages.RelatedLabel));
        }

        if (diagnostic.RelatedAliasTrace.Count > 0)
        {
            if (!string.IsNullOrEmpty(diagnostic.RelatedAliasTraceId))
            {
                converted.WithNote(DiagnosticMessages.AliasTraceIdNote(diagnostic.RelatedAliasTraceId));
                converted.WithNote(DiagnosticMessages.AliasStateLookupNote(
                    diagnostic.RelatedAliasTraceId,
                    functionName));
            }
            converted.WithNote(DiagnosticMessages.AliasTraceNote(
                string.Join(" => ", diagnostic.RelatedAliasTrace)));
        }

        if (!string.IsNullOrEmpty(diagnostic.Hint))
        {
            converted.WithHelp(diagnostic.Hint);
        }

        return converted;
    }

    private static bool HasSpan(SourceSpan span)
    {
        return span.Length > 0 ||
               span.Location.Position > 0 ||
               span.Location.Line > 0 ||
               span.Location.Column > 0;
    }

    private static bool IsBorrowConflictKind(BorrowErrorKind kind)
    {
        return kind is BorrowErrorKind.MultipleMutableBorrows
            or BorrowErrorKind.ReborrowAsMutable
            or BorrowErrorKind.MutableWhileImmutableBorrowed
            or BorrowErrorKind.ImmutableWhileMutableBorrowed
            or BorrowErrorKind.MutateWhileBorrowed;
    }

    private static string BuildBorrowDedupKey(BorrowDiagnostic diagnostic)
    {
        return $"{diagnostic.Kind}:{diagnostic.Location.Block.Value}:{diagnostic.Location.Index}:{diagnostic.RelatedLocation.Block.Value}:{diagnostic.RelatedLocation.Index}";
    }

    private static string GetMirLocationKey((BlockId Block, int Index) location)
    {
        return $"{location.Block.Value}:{location.Index}";
    }

    private static readonly FrozenDictionary<BorrowErrorKind, string> BorrowCodeMapping = new Dictionary<BorrowErrorKind, string>
    {
        [BorrowErrorKind.UseAfterMove] = "E1001",
        [BorrowErrorKind.DoubleMove] = "E1001",
        [BorrowErrorKind.AffineReuse] = "E1001",
        [BorrowErrorKind.MultipleMutableBorrows] = "E1002",
        [BorrowErrorKind.ReborrowAsMutable] = "E1002",
        [BorrowErrorKind.MutableWhileImmutableBorrowed] = "E1002",
        [BorrowErrorKind.ImmutableWhileMutableBorrowed] = "E1002",
        [BorrowErrorKind.MutateWhileBorrowed] = "E1002",
        [BorrowErrorKind.LifetimeTooShort] = "E1004",
        [BorrowErrorKind.LifetimeTooLong] = "E1004",
        [BorrowErrorKind.BorrowedWhileReturned] = "E1004",
        [BorrowErrorKind.ReadCapabilityDenied] = "E1011",
        [BorrowErrorKind.WriteCapabilityDenied] = "E1012",
        [BorrowErrorKind.MoveCapabilityDenied] = "E1013",
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<AffineErrorKind, string> AffineCodeMapping = new Dictionary<AffineErrorKind, string>
    {
        [AffineErrorKind.UseAfterMove] = "E1001",
        [AffineErrorKind.DoubleMove] = "E1001",
        [AffineErrorKind.AffineReuse] = "E1001",
    }.ToFrozenDictionary();

    private static string MapBorrowCode(BorrowErrorKind kind) =>
        BorrowCodeMapping.GetValueOrDefault(kind, "E1003");

    private static string MapAffineCode(AffineErrorKind kind) =>
        AffineCodeMapping.GetValueOrDefault(kind, "E1003");
}
