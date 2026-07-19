using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Types;
using Eidosc.Diagnostic;
using Eidosc.Symbols;
using Eidosc.Types;
using Eidosc.Utils;
using EidoscDiagnostic = Eidosc.Diagnostic.Diagnostic;
using EidoscDiagnosticLevel = Eidosc.Diagnostic.DiagnosticLevel;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private const int MaxDeriveExpansionCount = 4096;
    private const int MaxGeneratedDeclarationCount = 2048;
    private const int MaxMetaDiagnosticCount = 128;
    private const int MaxMetaExpansionRoundCount = 32;

    public bool ProcessDeferredMetaExpansionStage(ModuleDecl root, ClauseStage stage)
    {
        var previousDiagnosticCount = _diagnostics.Count;
        LastMetaExpansionChanged = ProcessMetaExpansions(root, stage);
        if (LastMetaExpansionChanged && stage != ClauseStage.Layout)
        {
            ResolveModuleReferencesRecursive(root, _rootModule);
        }

        return !_diagnostics
            .Skip(previousDiagnosticCount)
            .Any(diagnostic => diagnostic.Level == EidoscDiagnosticLevel.Error);
    }

    private bool ProcessMetaExpansions(ModuleDecl root, ClauseStage maximumStage)
    {
        var changed = false;
        foreach (var stage in Enum.GetValues<ClauseStage>()
                     .Where(stage => stage <= maximumStage)
                     .Order())
        {
            if (_closedMetaExpansionStages.Contains(stage))
            {
                continue;
            }

            var previousDiagnosticCount = _diagnostics.Count;
            changed |= ProcessMetaExpansionStage(root, stage);
            _closedMetaExpansionStages.Add(stage);
            if (_diagnostics
                .Skip(previousDiagnosticCount)
                .Any(static diagnostic => diagnostic.Level == EidoscDiagnosticLevel.Error))
            {
                break;
            }
        }

        return changed;
    }

    private bool ProcessMetaExpansionStage(ModuleDecl root, ClauseStage stage)
    {
        var previousDiagnosticCount = _diagnostics.Count;
        var changed = false;
        var processedExpansionCount = 0;
        var generatedDeclarationCount = 0;
        var emittedDiagnosticCount = 0;
        var canonicalGraphs = new HashSet<string>(StringComparer.Ordinal)
        {
            CreateMetaCanonicalGraphFingerprint(root)
        };

        for (var round = 1; round <= MaxMetaExpansionRoundCount; round++)
        {
            var roundChanged = ProcessMetaExpansionRound(
                root,
                stage,
                ref processedExpansionCount,
                ref generatedDeclarationCount,
                ref emittedDiagnosticCount);
            changed |= roundChanged;
            if (!roundChanged || _diagnostics
                .Skip(previousDiagnosticCount)
                .Any(static diagnostic => diagnostic.Level == EidoscDiagnosticLevel.Error))
            {
                return changed;
            }

            var canonicalGraph = CreateMetaCanonicalGraphFingerprint(root);
            if (!canonicalGraphs.Add(canonicalGraph))
            {
                AddNonConvergentMetaExpansionDiagnostic(stage, round, "canonical graph cycle");
                return changed;
            }
        }

        AddNonConvergentMetaExpansionDiagnostic(
            stage,
            MaxMetaExpansionRoundCount,
            "the canonical graph continued to change");
        return changed;
    }

    private bool ProcessMetaExpansionRound(
        ModuleDecl root,
        ClauseStage stage,
        ref int processedExpansionCount,
        ref int generatedDeclarationCount,
        ref int emittedDiagnosticCount)
    {
        if (_metaInvocationOccurrences.Count == 0)
        {
            return false;
        }

        var functions = _moduleDeclarations.Values
            .SelectMany(EnumerateDeclarations)
            .OfType<FuncDef>()
            .Where(static function => function.SymbolId.IsValid)
            .DistinctBy(static function => function.SymbolId)
            .ToDictionary(static function => function.SymbolId);
        if (!TryOrderMetaInvocationOccurrences(_metaInvocationOccurrences, out var orderedInvocations))
        {
            return false;
        }
        _metaInvocationOccurrences.Clear();
        _metaInvocationOccurrences.AddRange(orderedInvocations);
        var roundOccurrences = _metaInvocationOccurrences
            .Where(occurrence => occurrence.Invocation.Stage == stage)
            .Select(static occurrence => occurrence.Invocation.OccurrenceId)
            .ToHashSet();
        ResolveExpansionComptimeDependencies(root);
        var comptimeValues = EvaluateExpansionComptimeBindings(root, functions);
        var changed = false;
        for (var invocationIndex = 0; invocationIndex < _metaInvocationOccurrences.Count; invocationIndex++)
        {
            var invocation = _metaInvocationOccurrences[invocationIndex];
            if (invocation.Invocation.Stage != stage ||
                !roundOccurrences.Contains(invocation.Invocation.OccurrenceId))
            {
                continue;
            }
            if (!invocation.Target.SymbolId.IsValid ||
                _symbolTable.GetSymbol(invocation.Target.SymbolId) == null)
            {
                continue;
            }

            var inputFingerprint = CreateMetaInvocationInputFingerprint(root, invocation);
            if (_metaInvocationInputFingerprints.TryGetValue(
                    invocation.Invocation.OccurrenceId,
                    out var previousInputFingerprint) &&
                string.Equals(previousInputFingerprint, inputFingerprint, StringComparison.Ordinal))
            {
                continue;
            }
            _metaInvocationInputFingerprints[invocation.Invocation.OccurrenceId] = inputFingerprint;
            _completedMetaInvocations.Add(invocation.Invocation.OccurrenceId);

            if (++processedExpansionCount > MaxDeriveExpansionCount)
            {
                AddMetaExpansionDiagnostic(
                    _metaInvocationOccurrences[invocationIndex].Clause.Span,
                    "meta expansion count exceeded the compiler budget",
                    "E3607");
                break;
            }

            using var moduleScope = PushResolutionModuleScope(invocation.ModuleId);
            using var currentModuleScope = PushCurrentModuleScope(invocation.ModuleId);
            if (invocation.Invocation.Owner == MetaInvocationOwner.CompilerDerive)
            {
                ProcessCompilerOwnedDerive(invocation);
                continue;
            }

            if (!TryResolveMetaGenerator(
                    invocation,
                    out var generator,
                    out var generatorSymbol,
                    out var protocol,
                    out var reason))
            {
                AddMetaExpansionDiagnostic(invocation.Clause.Span, reason, "E3600");
                continue;
            }

            var generatorIdentity = MetaComptimeIntrinsics.CreateStableIdentity(generatorSymbol, _symbolTable);
            var targetSymbol = _symbolTable.GetSymbol(invocation.Target.SymbolId);
            if (targetSymbol == null)
            {
                AddMetaExpansionDiagnostic(invocation.Clause.Span, "meta target has no stable declaration symbol", "E3600");
                continue;
            }

            var targetIdentity = MetaComptimeIntrinsics.CreateStableIdentity(targetSymbol, _symbolTable);
            var cycleKey = $"{generatorIdentity}|{targetIdentity}";
            if (invocation.Ancestors.Contains(cycleKey, StringComparer.Ordinal))
            {
                AddMetaExpansionDiagnostic(
                    invocation.Clause.Span,
                    $"meta expansion cycle detected for generator '{generator.Name}' and target '{invocation.TargetName}'",
                    "E3604");
                continue;
            }

            var target = MetaComptimeIntrinsics.CreateTarget(
                invocation.Target,
                generatorSymbol,
                invocation.Clause.Span,
                invocation.Invocation.OccurrenceId,
                invocation.Invocation.Stage,
                _symbolTable,
                invocation.TargetPath);
            var trace = $"expand {generator.Name} on {invocation.TargetName}";
            var generatorModuleId = GetDeclarationOwnerModuleId(generator, invocation.ModuleId);
            var pendingUserDiagnostics = new List<PendingMetaUserDiagnostic>();
            var metaContext = new MetaComptimeContext(
                _symbolTable,
                _adtDefinitions,
                _traitDefinitions,
                (level, span, message) => pendingUserDiagnostics.Add(new PendingMetaUserDiagnostic(level, span, message)),
                target,
                trace,
                ComptimeExecution.CreateBudget(),
                ComptimeExecution.Trace,
                "namer.meta-expansion",
                _declarationsBySymbol,
                new MetaQueryAccessContext(
                    generatorModuleId,
                    invocation.Invocation.Stage,
                    MetaQueryCapability.CurrentPackagePrivateShapes |
                    MetaQueryCapability.CurrentPackageBodies,
                    targetIdentity,
                    MetaTargetTriple,
                     TargetSymbolId: targetSymbol.Id,
                     RequesterIdentity: _symbolTable.Modules.GetModule(generatorModuleId) is { } generatorModule
                         ? MetaComptimeIntrinsics.CreateStableIdentity(generatorModule, _symbolTable)
                         : string.Empty),
                DefinitionSiteResolver: CreateDefinitionSiteSyntaxResolver(generatorModuleId),
                GeneratorSymbolId: generatorSymbol.Id,
                InvocationOccurrenceIdentity: invocation.Invocation.OccurrenceId.ToString());
            var queryDependencyCursor = metaContext.Queries.CreateDependencyCursor();

            var invocationArguments = new List<ComptimeValue>();
            var explicitArgumentFailed = false;
            foreach (var argument in invocation.Invocation.ExplicitArguments)
            {
                ResolveExpressionReferences(argument);
                if (!ComptimeEvaluator.TryEvaluate(
                        argument,
                        comptimeValues,
                        functions,
                        resolveType: null,
                        metaContext,
                        out var argumentValue,
                        out reason))
                {
                    AddMetaExpansionDiagnostic(
                        argument.Span,
                        $"meta argument for generator '{generator.Name}' failed: {reason}; expansion trace: {trace}",
                        "E3601");
                    explicitArgumentFailed = true;
                    break;
                }
                invocationArguments.Add(argumentValue);
            }
            if (explicitArgumentFailed)
            {
                RecordMetaInvocationQueryDependency(invocation, metaContext.Queries, queryDependencyCursor);
                continue;
            }
            invocationArguments.Add(protocol.Kind switch
            {
                CompilerMetaProtocolKind.Derive => MetaComptimeIntrinsics.CreateTypeValue(
                    targetSymbol,
                    _symbolTable),
                CompilerMetaProtocolKind.BodyTransform => MetaComptimeIntrinsics.CreateFunctionHandle(
                    (FuncSymbol)targetSymbol,
                    _symbolTable),
                _ => target
            });

            if (!ComptimeEvaluator.TryInvoke(
                    generator,
                    invocationArguments,
                    comptimeValues,
                    functions,
                    metaContext,
                    out var expansionValue,
                    out reason))
            {
                RecordMetaInvocationQueryDependency(invocation, metaContext.Queries, queryDependencyCursor);
                AddMetaExpansionDiagnostic(
                    invocation.Clause.Span,
                    $"meta generator '{generator.Name}' failed: {reason}; expansion trace: {trace}",
                    "E3601");
                continue;
            }
            var queryFingerprint = RecordMetaInvocationQueryDependency(
                invocation,
                metaContext.Queries,
                queryDependencyCursor);
            var canonicalInvocationHash = HashIdentity(string.Join(
                "|",
                string.Join("|", invocationArguments.Select(static argument => argument.CanonicalText)),
                queryFingerprint,
                metaContext.Access.Fingerprint));

            var materializer = new MetaExpansionMaterializer(
                _symbolTable,
                invocation.Target,
                invocation.ModuleId,
                invocation.Clause.Span,
                invocation.TargetPath);
            MetaExpansionMaterializationResult materialization;
            bool materializedSuccessfully;
            if (protocol.Kind == CompilerMetaProtocolKind.BodyTransform &&
                expansionValue is ComptimeMetaObjectValue { SchemaKind: "function-handle" } functionHandle &&
                invocation.Target is FuncDef bodyTarget)
            {
                if (!materializer.TryMaterializeFunctionBody(
                        functionHandle,
                        bodyTarget,
                        out var replacement,
                        out var hasReplacement,
                        out reason))
                {
                    materialization = new MetaExpansionMaterializationResult([], []);
                    materializedSuccessfully = false;
                }
                else
                {
                    materialization = hasReplacement
                        ? new MetaExpansionMaterializationResult(
                            [new MaterializedMetaNode(replacement, 0, Placement: MetaDeclarationPlacement.ReplaceTarget)],
                            [])
                        : new MetaExpansionMaterializationResult([], []);
                    materializedSuccessfully = true;
                }
            }
            else if (protocol.Kind == CompilerMetaProtocolKind.BodyTransform &&
                     expansionValue is ComptimeDeclValue)
            {
                materialization = new MetaExpansionMaterializationResult([], []);
                reason = string.Empty;
                materializedSuccessfully = true;
            }
            else
            {
                materializedSuccessfully = protocol.Kind == CompilerMetaProtocolKind.Derive
                    ? materializer.TryMaterializeItems(expansionValue, out materialization, out reason)
                    : materializer.TryMaterialize(expansionValue, out materialization, out reason);
            }
            if (!materializedSuccessfully)
            {
                AddMetaExpansionDiagnostic(
                    invocation.Clause.Span,
                    $"meta generator '{generator.Name}' returned an invalid transformation: {reason}",
                    "E3602");
                continue;
            }

            var ancestorChain = invocation.Ancestors.Concat([cycleKey]).ToArray();
            var knownMetaOccurrences = _metaInvocationOccurrences
                .Select(static occurrence => occurrence.Invocation.OccurrenceId)
                .ToHashSet();
            var diagnosticCount = pendingUserDiagnostics.Count + materialization.Diagnostics.Count;
            if (emittedDiagnosticCount + diagnosticCount > MaxMetaDiagnosticCount)
            {
                AddMetaExpansionDiagnostic(
                    invocation.Clause.Span,
                    "meta expansion diagnostic count exceeded the compiler budget",
                    "E3609");
                return changed;
            }

            if (!TryPrepareMetaTransformation(
                    invocation,
                    generatorSymbol,
                    targetSymbol,
                    canonicalInvocationHash,
                    materialization,
                    generatedDeclarationCount,
                    out var prepared,
                    out reason,
                    out var validationCode))
            {
                AddMetaExpansionDiagnostic(invocation.Clause.Span, reason, validationCode);
                continue;
            }
            emittedDiagnosticCount += diagnosticCount;
            foreach (var diagnostic in pendingUserDiagnostics)
            {
                AddMetaUserDiagnostic(diagnostic.Level, diagnostic.Span, diagnostic.Message, trace);
            }
            foreach (var diagnostic in materialization.Diagnostics)
            {
                AddMaterializedDiagnostic(diagnostic, trace);
            }

            _generatedDeclarationIdentities.CommitBatch(prepared.Identities);
            generatedDeclarationCount += prepared.Identities.Count(static identity =>
                identity.Registration == GeneratedDeclarationIdentityRegistration.Added);

            var targetModule = prepared.TargetModule;
            var beforeInsertIndex = prepared.TargetIndex;
            var afterInsertCount = 0;

            foreach (var preparedNode in prepared.Nodes)
            {
                if (preparedNode.Identity.Registration == GeneratedDeclarationIdentityRegistration.Unchanged)
                {
                    continue;
                }

                var materialized = preparedNode.Materialized;
                var origin = preparedNode.Origin;

                var module = targetModule!;
                if (materialized.Placement == MetaDeclarationPlacement.ReplaceTarget)
                {
                    if (materialized.Node is not Declaration replacement)
                    {
                        AddMetaExpansionDiagnostic(
                            invocation.Clause.Span,
                            $"target replacement produced {materialized.Node.GetType().Name}, not declaration syntax",
                            "E3614");
                        continue;
                    }

                    if (!TryApplyTargetReplacement(
                            invocation,
                            replacement,
                            module,
                            origin,
                            functions,
                            invocationIndex,
                            out reason))
                    {
                        throw new InvalidOperationException(
                            $"validated target replacement failed during atomic commit: {reason}");
                    }
                    else
                    {
                        changed = true;
                    }
                    AttachAncestorChainToNewMetaInvocations(
                        knownMetaOccurrences,
                        ancestorChain,
                        replacement,
                        invocation.OrderingDomainIdentity);
                    if (!TryReorderPendingMetaInvocations(invocationIndex))
                    {
                        return changed;
                    }
                    continue;
                }

                if (materialized.Placement == MetaDeclarationPlacement.Member)
                {
                    if (!TryApplyGeneratedMember(
                            invocation,
                            materialized.Node,
                            origin,
                            functions,
                            out reason))
                    {
                        throw new InvalidOperationException(
                            $"validated generated member failed during atomic commit: {reason}");
                    }

                    changed = true;
                    AttachAncestorChainToNewMetaInvocations(knownMetaOccurrences, ancestorChain);
                    if (!TryReorderPendingMetaInvocations(invocationIndex))
                    {
                        return changed;
                    }
                    continue;
                }

                if (materialized.Node is not Declaration materializedDeclaration)
                {
                    throw new InvalidOperationException(
                        $"validated {materialized.Placement} output produced {materialized.Node.GetType().Name}, not item declaration syntax");
                }

                if (materialized.Placement == MetaDeclarationPlacement.BeforeTarget)
                {
                    module.Declarations.Insert(beforeInsertIndex++, materializedDeclaration);
                }
                else
                {
                    var currentTargetIndex = module.Declarations.IndexOf(invocation.Target);
                    var insertionIndex = currentTargetIndex < 0
                        ? module.Declarations.Count
                        : Math.Min(module.Declarations.Count, currentTargetIndex + 1 + afterInsertCount++);
                    module.Declarations.Insert(insertionIndex, materializedDeclaration);
                }
                changed = true;
                var invocationCountBeforeCollection = _metaInvocationOccurrences.Count;
                using (PushForcedPrivateGeneratedDeclaration(invocation.Invocation.Stage))
                {
                    if (materializedDeclaration is ModuleDecl generatedModule)
                    {
                        DeclareModuleTree(generatedModule, isGeneratedSource: true);
                        if (generatedModule.SymbolId.IsValid)
                        {
                            CollectModuleDeclarationsRecursive(
                                generatedModule,
                                generatedModule.SymbolId,
                                isGeneratedSource: true);
                            ProcessImportsRecursive(generatedModule, generatedModule.SymbolId);
                        }
                    }
                    else
                    {
                        CollectDeclaration(materializedDeclaration, isGeneratedSource: true);
                    }
                }
                AttachGeneratedOriginChain(materializedDeclaration, materializedDeclaration.GeneratedOriginChain);
                System.Diagnostics.Debug.Assert(
                    _metaInvocationOccurrences.Skip(invocationCountBeforeCollection).All(added =>
                        added.Invocation.Stage >= invocation.Invocation.Stage),
                    "Generated meta stage regressions must be rejected during transformation preparation.");
                if (!materializedDeclaration.SymbolId.IsValid)
                {
                    throw new InvalidOperationException(
                        $"validated generated declaration '{GetGeneratedDeclarationName(materializedDeclaration)}' failed during atomic commit");
                }

                _symbolTable.AddMemberToModule(invocation.ModuleId, materializedDeclaration.SymbolId);
                SetGeneratedOriginOnOwnedDeclarationSymbols(materializedDeclaration, origin);

                if (materializedDeclaration is FuncDef generatedFunction && generatedFunction.SymbolId.IsValid)
                {
                    functions[generatedFunction.SymbolId] = generatedFunction;
                }

                AttachAncestorChainToNewMetaInvocations(knownMetaOccurrences, ancestorChain);
                if (!TryReorderPendingMetaInvocations(invocationIndex))
                {
                    return changed;
                }
            }

            if (materialization.RemovesTarget &&
                !TryRemoveAuthorizedTarget(invocation, targetModule!, functions, invocationIndex, out reason))
            {
                throw new InvalidOperationException($"validated target removal failed during atomic commit: {reason}");
            }
            else if (materialization.RemovesTarget)
            {
                changed = true;
            }
        }

        return changed;
    }

    private void AttachAncestorChainToNewMetaInvocations(
        ISet<ClauseOccurrenceId> knownOccurrences,
        IReadOnlyList<string> ancestorChain,
        Declaration? inheritedTarget = null,
        string? inheritedOrderingDomainIdentity = null)
    {
        for (var index = 0; index < _metaInvocationOccurrences.Count; index++)
        {
            var occurrence = _metaInvocationOccurrences[index];
            if (!knownOccurrences.Add(occurrence.Invocation.OccurrenceId))
            {
                continue;
            }

            _metaInvocationOccurrences[index] = occurrence with
            {
                Ancestors = ancestorChain,
                OrderingDomainIdentity = ReferenceEquals(occurrence.Target, inheritedTarget) &&
                                         !string.IsNullOrWhiteSpace(inheritedOrderingDomainIdentity)
                    ? inheritedOrderingDomainIdentity
                    : occurrence.OrderingDomainIdentity
            };
        }
    }

    private bool TryRemoveAuthorizedTarget(
        MetaInvocationOccurrence invocation,
        ModuleDecl module,
        Dictionary<SymbolId, FuncDef> functions,
        int invocationIndex,
        out string reason)
    {
        reason = string.Empty;
        if (invocation.Target is CaseTypeDef caseType)
        {
            return TryRemoveSyntaxCaseTarget(
                caseType,
                module,
                functions,
                invocationIndex,
                out reason);
        }
        if (invocation.Target is FuncDef method && TryFindMethodOwner(module, method, out var methodOwner))
        {
            return TryRemoveSyntaxMethodTarget(
                method,
                methodOwner,
                module.SymbolId,
                functions,
                invocationIndex,
                out reason);
        }
        if (invocation.Target is ImportDecl)
        {
            reason = "import declarations cannot carry an authorized meta target identity";
            return false;
        }

        var symbolId = invocation.Target.SymbolId;
        if (!symbolId.IsValid || !module.Declarations.Remove(invocation.Target))
        {
            reason = "target removal could not find the authorized declaration in its owning module";
            return false;
        }

        var ownedSymbols = CollectOwnedDeclarationSymbolIds(invocation.Target);
        RemovePendingInvocationsForReplacedTarget(invocation.Target, invocationIndex);
        RemoveDeclarationSymbolState(module.SymbolId, ownedSymbols, functions);
        invocation.Target.SymbolId = SymbolId.None;
        return true;
    }

    private bool TryRemoveSyntaxMethodTarget(
        FuncDef method,
        Declaration owner,
        SymbolId moduleId,
        Dictionary<SymbolId, FuncDef> functions,
        int invocationIndex,
        out string reason)
    {
        if (owner is TraitDef trait)
        {
            trait.SetMethods(trait.Methods.Where(candidate => !ReferenceEquals(candidate, method)).ToList());
            if (_symbolTable.GetSymbol<TraitSymbol>(trait.SymbolId) is { } traitSymbol)
            {
                _symbolTable.UpdateSymbol(traitSymbol with
                {
                    Methods = traitSymbol.Methods.Where(id => id != method.SymbolId).ToList()
                });
            }
        }
        else if (owner is InstanceDecl instance)
        {
            instance.SetMethods(instance.Methods.Where(candidate => !ReferenceEquals(candidate, method)).ToList());
            if (_symbolTable.GetSymbol<ImplSymbol>(instance.SymbolId) is { } implSymbol)
            {
                _symbolTable.UpdateSymbol(implSymbol with
                {
                    Methods = implSymbol.Methods.Where(id => id != method.SymbolId).ToList()
                });
            }
        }
        else
        {
            reason = "method removal could not identify a trait or instance owner";
            return false;
        }

        var ownedSymbols = CollectOwnedDeclarationSymbolIds(method);
        RemovePendingInvocationsForReplacedTarget(method, invocationIndex);
        RemoveDeclarationSymbolState(moduleId, ownedSymbols, functions);
        method.SymbolId = SymbolId.None;
        reason = string.Empty;
        return true;
    }

    private bool TryRemoveSyntaxCaseTarget(
        CaseTypeDef caseType,
        ModuleDecl module,
        Dictionary<SymbolId, FuncDef> functions,
        int invocationIndex,
        out string reason)
    {
        if (!TryFindCaseOwner(module, caseType, out var owner, out var root))
        {
            reason = "closed case removal could not locate the authorized case in its owner";
            return false;
        }

        foreach (var leaf in EnumerateLeafCases(caseType).ToArray())
        {
            RemoveLeafConstructor(root, leaf);
        }

        if (_symbolTable.GetSymbol<AdtSymbol>(owner.SymbolId) is { } ownerSymbol)
        {
            _symbolTable.UpdateSymbol(ownerSymbol with
            {
                DirectCases = ownerSymbol.DirectCases.Where(id => id != caseType.SymbolId).ToList()
            });
        }

        if (owner is AdtDef adtOwner)
        {
            adtOwner.SetCases(adtOwner.Cases.Where(candidate => !ReferenceEquals(candidate, caseType)).ToList());
        }
        else
        {
            var caseOwner = (CaseTypeDef)owner;
            caseOwner.SetCases(caseOwner.Cases.Where(candidate => !ReferenceEquals(candidate, caseType)).ToList());
        }

        var ownedSymbols = CollectOwnedDeclarationSymbolIds(caseType);
        RemovePendingInvocationsForReplacedTarget(caseType, invocationIndex);
        RemoveDeclarationSymbolState(module.SymbolId, ownedSymbols, functions);
        caseType.SymbolId = SymbolId.None;
        caseType.ConstructorSymbolId = SymbolId.None;
        reason = string.Empty;
        return true;
    }

    private bool TryApplyTargetReplacement(
        MetaInvocationOccurrence invocation,
        Declaration replacement,
        ModuleDecl module,
        GeneratedDeclarationOrigin origin,
        Dictionary<SymbolId, FuncDef> functions,
        int invocationIndex,
        out string reason)
    {
        reason = string.Empty;
        if (invocation.Invocation.Stage == ClauseStage.Syntax)
        {
            return TryApplySyntaxTargetReplacement(
                invocation,
                replacement,
                module,
                origin,
                functions,
                invocationIndex,
                out reason);
        }

        if (invocation.Target is not FuncDef source || replacement is not FuncDef replacementFunction)
        {
            reason = "contract-preserving target replacement requires function syntax for a function target";
            return false;
        }

        if (!string.Equals(source.Name, replacementFunction.Name, StringComparison.Ordinal) ||
            !string.Equals(CanonicalFunctionContract(source), CanonicalFunctionContract(replacementFunction), StringComparison.Ordinal))
        {
            reason = "Body/Semantic function replacement must preserve the target name, generic parameters, signature, and effects; " +
                     $"expected '{CanonicalFunctionContract(source)}', got '{CanonicalFunctionContract(replacementFunction)}'";
            return false;
        }

        var targetIndex = module.Declarations.IndexOf(source);
        if (targetIndex < 0 || !source.SymbolId.IsValid)
        {
            reason = "function replacement target is no longer present in its owning module";
            return false;
        }

        replacementFunction.SymbolId = source.SymbolId;
        replacementFunction.SetAttributes([.. source.Attributes]);
        replacementFunction.SetClauses([.. source.Clauses]);
        replacementFunction.SetBoundClauses(source.BoundClauses, source.MetaInvocations);
        replacementFunction.SetExported(source.IsExported);
        module.Declarations[targetIndex] = replacementFunction;
        _declarationsBySymbol[source.SymbolId] = replacementFunction;
        functions[source.SymbolId] = replacementFunction;
        RetargetPreservedMetaInvocations(source, replacementFunction, invocationIndex);
        if (_symbolTable.GetSymbol<FuncSymbol>(source.SymbolId) is { } symbol)
        {
            _symbolTable.UpdateSymbol(symbol with { GeneratedOrigin = origin, HasBody = true });
        }

        return true;
    }

    private bool TryApplySyntaxTargetReplacement(
        MetaInvocationOccurrence invocation,
        Declaration replacement,
        ModuleDecl module,
        GeneratedDeclarationOrigin origin,
        Dictionary<SymbolId, FuncDef> functions,
        int invocationIndex,
        out string reason)
    {
        var source = invocation.Target;
        if (!MetaTransformationValidator.HasSameTargetCategory(source, replacement))
        {
            reason = $"target replacement category '{MetaTransformationValidator.GetTargetCategory(replacement)}' " +
                     $"does not match authorized category '{MetaTransformationValidator.GetTargetCategory(source)}'";
            return false;
        }

        if (source is CaseTypeDef sourceCase && replacement is CaseTypeDef replacementCase)
        {
            return TryApplySyntaxCaseReplacement(
                invocation,
                sourceCase,
                replacementCase,
                module,
                origin,
                functions,
                invocationIndex,
                out reason);
        }
        if (source is ModuleDecl sourceModule && replacement is ModuleDecl replacementModule)
        {
            return TryApplySyntaxModuleReplacement(
                invocation,
                sourceModule,
                replacementModule,
                module,
                origin,
                functions,
                invocationIndex,
                out reason);
        }
        if (source is FuncDef sourceMethod &&
            replacement is FuncDef replacementMethod &&
            TryFindMethodOwner(module, sourceMethod, out var methodOwner))
        {
            return TryApplySyntaxMethodReplacement(
                invocation,
                sourceMethod,
                replacementMethod,
                methodOwner,
                origin,
                functions,
                invocationIndex,
                out reason);
        }

        var targetIndex = module.Declarations.IndexOf(source);
        if (targetIndex < 0 || !source.SymbolId.IsValid)
        {
            reason = "Syntax target replacement requires an existing top-level declaration in its owning module";
            return false;
        }

        var replacedSymbols = CollectOwnedDeclarationSymbolIds(source);
        RemovePendingInvocationsForReplacedTarget(source, invocationIndex);
        RemoveDeclarationSymbolState(invocation.ModuleId, replacedSymbols, functions);
        module.Declarations[targetIndex] = replacement;
        source.SymbolId = SymbolId.None;

        CollectDeclaration(replacement, isGeneratedSource: true);
        if (!replacement.SymbolId.IsValid)
        {
            reason = $"replacement declaration '{GetGeneratedDeclarationName(replacement)}' could not be registered";
            return false;
        }

        AttachGeneratedOriginChain(replacement, replacement.GeneratedOriginChain);

        _symbolTable.AddMemberToModule(invocation.ModuleId, replacement.SymbolId);
        SetGeneratedOriginOnOwnedDeclarationSymbols(replacement, origin);

        if (replacement is FuncDef replacementFunction)
        {
            functions[replacementFunction.SymbolId] = replacementFunction;
        }

        reason = string.Empty;
        return true;
    }

    private bool TryApplySyntaxModuleReplacement(
        MetaInvocationOccurrence invocation,
        ModuleDecl source,
        ModuleDecl replacement,
        ModuleDecl containingModule,
        GeneratedDeclarationOrigin origin,
        Dictionary<SymbolId, FuncDef> functions,
        int invocationIndex,
        out string reason)
    {
        var targetIndex = containingModule.Declarations.IndexOf(source);
        if (targetIndex < 0 || !source.SymbolId.IsValid || !containingModule.SymbolId.IsValid)
        {
            reason = "Syntax module replacement could not locate the authorized module in its parent";
            return false;
        }

        var replacedSymbols = CollectOwnedDeclarationSymbolIds(source);
        RemovePendingInvocationsForReplacedTarget(source, invocationIndex);
        RemoveDeclarationSymbolState(containingModule.SymbolId, replacedSymbols, functions);
        containingModule.Declarations[targetIndex] = replacement;
        source.SymbolId = SymbolId.None;

        using var parentCollectionScope = PushCollectionModuleScope(SymbolId.None, containingModule.SymbolId);
        using var parentCurrentModule = PushCurrentModuleScope(containingModule.SymbolId);
        DeclareModuleTree(replacement);
        if (!replacement.SymbolId.IsValid)
        {
            reason = $"replacement module '{GetGeneratedDeclarationName(replacement)}' could not be registered";
            return false;
        }

        CollectModuleDeclarationsRecursive(replacement, replacement.SymbolId);
        EnsureModuleImportsProcessed(replacement.SymbolId);
        AttachGeneratedOriginChain(replacement, replacement.GeneratedOriginChain);
        SetGeneratedOriginOnOwnedDeclarationSymbols(replacement, origin);
        foreach (var function in EnumerateDeclarations(replacement)
                     .OfType<FuncDef>()
                     .Where(static function => function.SymbolId.IsValid))
        {
            functions[function.SymbolId] = function;
        }

        reason = string.Empty;
        return true;
    }

    private bool TryApplySyntaxMethodReplacement(
        MetaInvocationOccurrence invocation,
        FuncDef source,
        FuncDef replacement,
        Declaration owner,
        GeneratedDeclarationOrigin origin,
        Dictionary<SymbolId, FuncDef> functions,
        int invocationIndex,
        out string reason)
    {
        reason = string.Empty;

        var methodIndex = owner switch
        {
            TraitDef trait => trait.Methods.IndexOf(source),
            InstanceDecl instance => instance.Methods.IndexOf(source),
            _ => -1
        };
        if (methodIndex < 0)
        {
            reason = "Syntax method replacement could not locate the authorized method in its owner";
            return false;
        }

        var replacementNode = new MaterializedMetaNode(
            replacement,
            0,
            Placement: MetaDeclarationPlacement.Member);
        var valid = owner switch
        {
            TraitDef trait => TryValidateGeneratedAssociatedMembers(
                trait.Name,
                trait.Methods,
                trait.AssociatedTypes,
                trait.AssociatedConsts,
                [replacementNode],
                out reason,
                source),
            InstanceDecl instance => TryValidateGeneratedAssociatedMembers(
                instance.Name,
                instance.Methods,
                instance.AssociatedTypes,
                instance.AssociatedConsts,
                [replacementNode],
                out reason,
                source),
            _ => false
        };
        if (!valid)
        {
            reason = string.IsNullOrWhiteSpace(reason)
                ? "Syntax method replacement could not validate its declaration owner"
                : reason;
            return false;
        }

        var replacedSymbols = CollectOwnedDeclarationSymbolIds(source);
        if (owner is TraitDef traitOwner)
        {
            traitOwner.SetMethods(traitOwner.Methods.Where(method => !ReferenceEquals(method, source)).ToList());
            if (_symbolTable.GetSymbol<TraitSymbol>(traitOwner.SymbolId) is { } traitSymbol)
            {
                _symbolTable.UpdateSymbol(traitSymbol with
                {
                    Methods = traitSymbol.Methods.Where(id => id != source.SymbolId).ToList()
                });
            }
        }
        else
        {
            var instanceOwner = (InstanceDecl)owner;
            instanceOwner.SetMethods(instanceOwner.Methods.Where(method => !ReferenceEquals(method, source)).ToList());
            if (_symbolTable.GetSymbol<ImplSymbol>(instanceOwner.SymbolId) is { } implSymbol)
            {
                _symbolTable.UpdateSymbol(implSymbol with
                {
                    Methods = implSymbol.Methods.Where(id => id != source.SymbolId).ToList()
                });
            }
        }

        RemovePendingInvocationsForReplacedTarget(source, invocationIndex);
        RemoveDeclarationSymbolState(invocation.ModuleId, replacedSymbols, functions);
        source.SymbolId = SymbolId.None;

        var applied = owner switch
        {
            TraitDef trait => TryApplyGeneratedTraitMember(trait, replacement, origin, functions, out reason),
            InstanceDecl instance => TryApplyGeneratedInstanceMember(instance, replacement, origin, functions, out reason),
            _ => false
        };
        if (!applied)
        {
            return false;
        }

        if (owner is TraitDef reorderedTrait)
        {
            var methods = reorderedTrait.Methods.Where(method => !ReferenceEquals(method, replacement)).ToList();
            methods.Insert(Math.Min(methodIndex, methods.Count), replacement);
            reorderedTrait.SetMethods(methods);
        }
        else
        {
            var reorderedInstance = (InstanceDecl)owner;
            var methods = reorderedInstance.Methods.Where(method => !ReferenceEquals(method, replacement)).ToList();
            methods.Insert(Math.Min(methodIndex, methods.Count), replacement);
            reorderedInstance.SetMethods(methods);
        }

        AttachGeneratedOriginChain(replacement, replacement.GeneratedOriginChain);
        SetGeneratedOriginOnOwnedDeclarationSymbols(replacement, origin);
        return true;
    }

    private static bool TryFindMethodOwner(
        ModuleDecl module,
        FuncDef target,
        out Declaration owner)
    {
        foreach (var declaration in module.Declarations)
        {
            switch (declaration)
            {
                case TraitDef trait when trait.Methods.Any(method => ReferenceEquals(method, target)):
                    owner = trait;
                    return true;
                case InstanceDecl instance when instance.Methods.Any(method => ReferenceEquals(method, target)):
                    owner = instance;
                    return true;
                case ModuleDecl nested when TryFindMethodOwner(nested, target, out owner):
                    return true;
            }
        }

        owner = null!;
        return false;
    }

    private bool TryApplySyntaxCaseReplacement(
        MetaInvocationOccurrence invocation,
        CaseTypeDef source,
        CaseTypeDef replacement,
        ModuleDecl module,
        GeneratedDeclarationOrigin origin,
        Dictionary<SymbolId, FuncDef> functions,
        int invocationIndex,
        out string reason)
    {
        if (!TryFindCaseOwner(module, source, out var owner, out var root))
        {
            reason = "Syntax case replacement could not locate the authorized case in its closed-case owner";
            return false;
        }

        var replacementNode = new MaterializedMetaNode(
            replacement,
            OutputIndex: 0,
            Placement: MetaDeclarationPlacement.Member);
        if (!TryValidateGeneratedTypeMembers(owner, [replacementNode], out reason, source))
        {
            return false;
        }

        var replacedSymbols = CollectOwnedDeclarationSymbolIds(source);
        foreach (var leaf in EnumerateLeafCases(source).ToArray())
        {
            RemoveLeafConstructor(root, leaf);
        }

        if (_symbolTable.GetSymbol<AdtSymbol>(owner.SymbolId) is { } ownerSymbol)
        {
            _symbolTable.UpdateSymbol(ownerSymbol with
            {
                DirectCases = ownerSymbol.DirectCases.Where(id => id != source.SymbolId).ToList()
            });
        }

        if (owner is AdtDef adtOwner)
        {
            adtOwner.SetCases(adtOwner.Cases.Where(candidate => !ReferenceEquals(candidate, source)).ToList());
        }
        else
        {
            var caseOwner = (CaseTypeDef)owner;
            caseOwner.SetCases(caseOwner.Cases.Where(candidate => !ReferenceEquals(candidate, source)).ToList());
        }

        RemovePendingInvocationsForReplacedTarget(source, invocationIndex);
        RemoveDeclarationSymbolState(invocation.ModuleId, replacedSymbols, functions);
        source.SymbolId = SymbolId.None;
        source.ConstructorSymbolId = SymbolId.None;

        if (!TryApplyGeneratedTypeMember(owner, replacement, origin, out reason))
        {
            return false;
        }

        AttachGeneratedOriginChain(replacement, replacement.GeneratedOriginChain);
        SetGeneratedOriginOnOwnedDeclarationSymbols(replacement, origin);
        return true;
    }

    private static bool TryFindCaseOwner(
        ModuleDecl module,
        CaseTypeDef target,
        out Declaration owner,
        out AdtDef root)
    {
        foreach (var declaration in module.Declarations)
        {
            switch (declaration)
            {
                case AdtDef adt when TryFindCaseOwner(adt, adt, target, out owner):
                    root = adt;
                    return true;
                case ModuleDecl nested when TryFindCaseOwner(nested, target, out owner, out root):
                    return true;
            }
        }

        owner = null!;
        root = null!;
        return false;
    }

    private static bool TryFindCaseOwner(
        AdtDef root,
        Declaration currentOwner,
        CaseTypeDef target,
        out Declaration owner)
    {
        var cases = currentOwner switch
        {
            AdtDef adt => adt.Cases,
            CaseTypeDef caseType => caseType.Cases,
            _ => []
        };
        foreach (var candidate in cases)
        {
            if (ReferenceEquals(candidate, target))
            {
                owner = currentOwner;
                return true;
            }

            if (TryFindCaseOwner(root, candidate, target, out owner))
            {
                return true;
            }
        }

        owner = null!;
        return false;
    }

    private void SetGeneratedOriginOnOwnedDeclarationSymbols(
        Declaration declaration,
        GeneratedDeclarationOrigin origin)
    {
        foreach (var symbolId in CollectOwnedDeclarationSymbolIds(declaration))
        {
            if (_symbolTable.GetSymbol(symbolId) is { } symbol)
            {
                _symbolTable.UpdateSymbol(symbol with { GeneratedOrigin = origin });
            }
        }
    }

    private void RemoveDeclarationSymbolState(
        SymbolId moduleId,
        IReadOnlySet<SymbolId> symbolIds,
        Dictionary<SymbolId, FuncDef> functions)
    {
        var removedFunctionNames = symbolIds
            .Select(_symbolTable.GetSymbol)
            .OfType<FuncSymbol>()
            .Select(static function => function.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var declarations in _functionOverloadDeclarations.Values)
        {
            foreach (var name in declarations.Keys.ToArray())
            {
                declarations[name].RemoveAll(candidate => symbolIds.Contains(candidate.SymbolId));
                if (declarations[name].Count == 0)
                {
                    declarations.Remove(name);
                }
            }
        }

        foreach (var identity in _syntaxIdentitySymbols.Keys.ToArray())
        {
            _syntaxIdentitySymbols[identity].RemoveAll(symbolIds.Contains);
            if (_syntaxIdentitySymbols[identity].Count == 0)
            {
                _syntaxIdentitySymbols.Remove(identity);
            }
        }

        foreach (var name in _instanceDeclarations
                     .Where(entry => symbolIds.Contains(entry.Value.SymbolId))
                     .Select(static entry => entry.Key)
                     .ToArray())
        {
            _instanceDeclarations.Remove(name);
        }

        foreach (var symbolId in symbolIds)
        {
            _symbolTable.Modules.RemoveMemberFromModule(moduleId, symbolId);
            if (_symbolTable.GetSymbol(symbolId) is ModuleSymbol)
            {
                _symbolTable.Modules.UnregisterModule(symbolId);
                _moduleDeclarations.Remove(symbolId);
                _moduleScopes.Remove(symbolId);
                _importScopes.Remove(symbolId);
                _importsProcessed.Remove(symbolId);
                _importsProcessing.Remove(symbolId);
            }
            _symbolTable.RemoveSymbol(symbolId);
            _declarationsBySymbol.Remove(symbolId);
            _adtDefinitions.Remove(symbolId);
            _traitDefinitions.Remove(symbolId);
            _genericParameterKindsBySymbol.Remove(symbolId);
            _metaResolvedComptimeSymbols.Remove(symbolId);
            _traitOwnerModules.Remove(symbolId);
            _ctorPatternShapes.Remove(symbolId);
            _traitImplMethodIds?.Remove(symbolId);
            functions.Remove(symbolId);
        }

        foreach (var functionName in removedFunctionNames)
        {
            _customOperators.RemoveByFunctionName(functionName);
        }

        foreach (var binding in _patternValueAdtBindings
                     .Where(entry => symbolIds.Contains(entry.Key) || symbolIds.Contains(entry.Value))
                     .Select(static entry => entry.Key)
                     .ToArray())
        {
            _patternValueAdtBindings.Remove(binding);
        }
    }

    private void RemovePendingInvocationsForReplacedTarget(Declaration source, int invocationIndex)
    {
        for (var index = _metaInvocationOccurrences.Count - 1; index > invocationIndex; index--)
        {
            if (ReferenceEquals(_metaInvocationOccurrences[index].Target, source))
            {
                _metaInvocationOccurrences.RemoveAt(index);
            }
        }
    }

    private void RetargetPreservedMetaInvocations(
        Declaration source,
        Declaration replacement,
        int invocationIndex)
    {
        for (var index = invocationIndex + 1; index < _metaInvocationOccurrences.Count; index++)
        {
            if (!ReferenceEquals(_metaInvocationOccurrences[index].Target, source))
            {
                continue;
            }

            _metaInvocationOccurrences[index] = _metaInvocationOccurrences[index] with
            {
                Target = replacement,
                TargetName = GetMetaTargetName(replacement)
            };
        }
    }

    private static string CanonicalFunctionContract(FuncDef function)
    {
        var document = new XmlDocument();
        return string.Join(
            "|",
            function.TypeParams.Select(parameter => ToSemanticContractXml(parameter, document)).Concat(
                function.Signature.Select(type => ToSemanticContractXml(type, document))).Concat(
                function.RequiredAbilities.Select(static ability => string.Join(".", ability.Path))));
    }

    private static string ToSemanticContractXml(EidosAstNode node, XmlDocument document)
    {
        var element = node.ToXmlElement(document);
        var originElements = element
            .SelectNodes("descendant-or-self::GeneratedOriginChain")?
            .OfType<XmlElement>()
            .ToArray() ?? [];
        foreach (var originElement in originElements)
        {
            originElement.ParentNode?.RemoveChild(originElement);
        }

        NormalizeEmptyElements(element);

        return element.OuterXml;
    }

    private static void NormalizeEmptyElements(XmlElement element)
    {
        foreach (var child in element.ChildNodes.OfType<XmlElement>())
        {
            NormalizeEmptyElements(child);
        }

        if (!element.HasChildNodes)
        {
            element.IsEmpty = true;
        }
    }

    private void ResolveExpansionComptimeDependencies(ModuleDecl root)
    {
        foreach (var declaration in _moduleDeclarations.Values.SelectMany(EnumerateDeclarations))
        {
            if (!declaration.SymbolId.IsValid || _metaResolvedComptimeSymbols.Contains(declaration.SymbolId))
            {
                continue;
            }

            var isComptimeDeclaration = declaration is FuncDef { IsComptime: true } or LetDecl { IsComptime: true };
            if (!isComptimeDeclaration)
            {
                continue;
            }

            var moduleId = GetDeclarationOwnerModuleId(declaration, root.SymbolId);
            using var moduleScope = PushResolutionModuleScope(moduleId);
            using var currentModuleScope = PushCurrentModuleScope(moduleId);
            ResolveDeclarationReferences(declaration);
            _metaResolvedComptimeSymbols.Add(declaration.SymbolId);
        }
    }

    private Dictionary<SymbolId, ComptimeValue> EvaluateExpansionComptimeBindings(
        ModuleDecl root,
        IReadOnlyDictionary<SymbolId, FuncDef> functions)
    {
        var values = new Dictionary<SymbolId, ComptimeValue>();
        foreach (var binding in _moduleDeclarations.Values
                     .SelectMany(EnumerateDeclarations)
                     .OfType<LetDecl>()
                     .DistinctBy(static binding => binding.SymbolId))
        {
            if (!binding.IsComptime || !binding.SymbolId.IsValid || binding.Value == null)
            {
                continue;
            }

            var moduleId = GetDeclarationOwnerModuleId(binding, root.SymbolId);
            var metaContext = new MetaComptimeContext(
                _symbolTable,
                _adtDefinitions,
                _traitDefinitions,
                (level, span, message) => AddMetaUserDiagnostic(level, span, message, "top-level comptime evaluation"),
                ResourceBudget: ComptimeExecution.CreateBudget(),
                Trace: ComptimeExecution.Trace,
                TracePhase: "namer.comptime-binding",
                Declarations: _declarationsBySymbol,
                QueryAccess: new MetaQueryAccessContext(
                    moduleId,
                    ClauseStage.Semantic,
                    MetaQueryCapability.CurrentPackagePrivateShapes,
                     RequesterIdentity: _symbolTable.Modules.GetModule(moduleId) is { } bindingModule
                         ? MetaComptimeIntrinsics.CreateStableIdentity(bindingModule, _symbolTable)
                         : string.Empty),
                DefinitionSiteResolver: CreateDefinitionSiteSyntaxResolver(moduleId));

            if (ComptimeEvaluator.TryEvaluate(
                    binding.Value,
                    values,
                    functions,
                    resolveType: null,
                    metaContext,
                    out var value,
                    out var reason) &&
                ComptimePhaseValueValidator.TryValidate(value, out reason))
            {
                values[binding.SymbolId] = value;
            }
        }

        return values;
    }

    private bool TryResolveMetaGenerator(
        MetaInvocationOccurrence invocation,
        out FuncDef generator,
        out FuncSymbol generatorSymbol,
        out string reason)
    {
        return TryResolveMetaGenerator(
            invocation,
            out generator,
            out generatorSymbol,
            out _,
            out reason);
    }

    private bool TryResolveMetaGenerator(
        MetaInvocationOccurrence invocation,
        out FuncDef generator,
        out FuncSymbol generatorSymbol,
        out CompilerMetaProtocolMatch protocol,
        out string reason)
    {
        generator = null!;
        generatorSymbol = null!;
        protocol = null!;
        reason = string.Empty;
        SymbolId symbolId = SymbolId.None;
        var generatorText = string.Join(WellKnownStrings.Separators.Path, invocation.Invocation.GeneratorPath);
        var path = invocation.Invocation.GeneratorPath;
        if (path.Count == 1)
        {
            var lookup = _lookupService.Lookup(path[0], LookupKind.Value, CreateLookupContext());
            symbolId = lookup.IsSuccess ? lookup.SymbolId : SymbolId.None;
        }
        else if (path.Count > 1)
        {
            var resolved = ResolvePathWithImports(path);
            symbolId = resolved.IsSuccess ? resolved.SymbolId : SymbolId.None;
        }

        if (!symbolId.IsValid ||
            _symbolTable.GetSymbol<FuncSymbol>(symbolId) is not { } symbol ||
            !symbol.IsComptime)
        {
            reason = $"expand {generatorText} must reference a comptime-only function";
            return false;
        }

        generator = _moduleDeclarations.Values
            .SelectMany(EnumerateDeclarations)
            .OfType<FuncDef>()
            .FirstOrDefault(function => function.SymbolId == symbolId)!;
        if (generator == null)
        {
            reason = $"expand {generatorText} cannot execute a signature-only or compiler-internal function";
            return false;
        }

        if (_metaResolvedComptimeSymbols.Add(generator.SymbolId))
        {
            var generatorModuleId = GetDeclarationOwnerModuleId(generator, invocation.ModuleId);
            using var generatorModuleScope = PushResolutionModuleScope(generatorModuleId);
            using var currentGeneratorModuleScope = PushCurrentModuleScope(generatorModuleId);
            ResolveFuncDefReferences(generator);
        }

        if (!CompilerMetaProtocolRegistry.TryClassify(
                generator,
                invocation.Invocation.ExplicitArguments.Count,
                _symbolTable,
                out protocol,
                out reason) ||
            protocol.Kind == CompilerMetaProtocolKind.PureComptime)
        {
            if (!TryGetTargetTransformationStage(
                    generator,
                    invocation.Invocation.ExplicitArguments.Count,
                    out var legacyStage))
            {
                reason = string.IsNullOrWhiteSpace(reason)
                    ? $"'{generator.Name}' is a pure comptime function, not a compiler generator protocol"
                    : $"generator '{generator.Name}' has an invalid protocol: {reason}";
                return false;
            }

            protocol = new CompilerMetaProtocolMatch(
                CompilerMetaProtocolKind.LegacyTransformation,
                legacyStage);
        }

        if (invocation.Invocation.Owner == MetaInvocationOwner.CompilerDerive &&
            protocol.Kind != CompilerMetaProtocolKind.Derive)
        {
            reason = $"derive generator '{generator.Name}' must have type meta.Type -> meta.Items";
            return false;
        }

        var targetMatches = protocol.Kind switch
        {
            CompilerMetaProtocolKind.Derive => invocation.Target is AdtDef or CaseTypeDef,
            CompilerMetaProtocolKind.BodyTransform => invocation.Target is FuncDef,
            CompilerMetaProtocolKind.LegacyTransformation => true,
            _ => false
        };
        if (!targetMatches)
        {
            reason = $"generator protocol '{protocol.Kind}' cannot attach to {invocation.Target.GetType().Name}";
            return false;
        }

        generatorSymbol = symbol;
        return true;
    }

    private bool TryGetTargetTransformationStage(
        FuncDef generator,
        int explicitArgumentCount,
        out ClauseStage stage)
    {
        stage = default;
        if (generator.Signature.Count != 1)
        {
            return false;
        }

        var parameters = new List<TypeNode>();
        var result = generator.Signature[0];
        while (result is ArrowType arrow)
        {
            parameters.Add(arrow.ParamType);
            result = arrow.ReturnType;
        }

        if (parameters.Count != explicitArgumentCount + 1 ||
            parameters[^1] is not TypePath targetPath ||
            !IsMetaType(targetPath, WellKnownTypeIds.MetaTargetId) ||
            !IsMetaType(result, WellKnownTypeIds.MetaTransformationId) ||
            targetPath.TypeArgs.Count != 1 ||
            targetPath.TypeArgs[0] is not TypePath stagePath)
        {
            return false;
        }

        stage = stagePath.TypeName switch
        {
            "Syntax" => ClauseStage.Syntax,
            "Semantic" => ClauseStage.Semantic,
            "Body" => ClauseStage.Body,
            "Layout" => ClauseStage.Layout,
            _ => (ClauseStage)(-1)
        };
        return Enum.IsDefined(stage);
    }

    private bool IsMetaType(TypeNode type, int typeId)
    {
        return type is TypePath path &&
               path.SymbolId.IsValid &&
               _symbolTable.GetSymbol(path.SymbolId)?.TypeId == new TypeId(typeId);
    }

    private bool TryOrderMetaInvocationOccurrences(
        IReadOnlyList<MetaInvocationOccurrence> occurrences,
        out IReadOnlyList<MetaInvocationOccurrence> ordered)
    {
        var normalizedOccurrences = NormalizeMetaInvocationStages(occurrences);
        var result = new List<MetaInvocationOccurrence>(normalizedOccurrences.Count);

        foreach (var stageGroup in normalizedOccurrences
                     .Select((invocation, sourceIndex) => (Invocation: invocation, SourceIndex: sourceIndex))
                     .GroupBy(static entry => entry.Invocation.Invocation.Stage)
                     .OrderBy(static group => group.Key))
        {
            var entries = stageGroup.ToArray();
            var outgoing = Enumerable.Range(0, entries.Length)
                .ToDictionary(static index => index, static _ => new HashSet<int>());
            var incoming = new int[entries.Length];
            var generators = new FuncDef?[entries.Length];
            var generatorIds = new SymbolId[entries.Length];

            for (var index = 0; index < entries.Length; index++)
            {
                var invocation = entries[index].Invocation;
                if (invocation.Invocation.Owner != MetaInvocationOwner.UserExpand)
                {
                    continue;
                }

                using var moduleScope = PushResolutionModuleScope(invocation.ModuleId);
                using var currentModuleScope = PushCurrentModuleScope(invocation.ModuleId);
                if (TryResolveMetaGenerator(invocation, out var generator, out var generatorSymbol, out _))
                {
                    generators[index] = generator;
                    generatorIds[index] = generatorSymbol.Id;
                }
            }

            for (var source = 0; source < entries.Length; source++)
            {
                if (generators[source] is not { } generator)
                {
                    continue;
                }

                if (!AddEdges(generator, DeclarationClauseKind.Requires, source, OrderingEdgeKind.Requires) ||
                    !AddEdges(generator, DeclarationClauseKind.Before, source, OrderingEdgeKind.Before) ||
                    !AddEdges(generator, DeclarationClauseKind.After, source, OrderingEdgeKind.After))
                {
                    ordered = [];
                    return false;
                }
            }

            var ready = new SortedSet<(int SourceIndex, int LocalIndex)>();
            for (var index = 0; index < entries.Length; index++)
            {
                if (incoming[index] == 0)
                {
                    ready.Add((entries[index].SourceIndex, index));
                }
            }

            var emitted = 0;
            while (ready.Count > 0)
            {
                var next = ready.Min;
                ready.Remove(next);
                result.Add(entries[next.LocalIndex].Invocation);
                emitted++;
                foreach (var successor in outgoing[next.LocalIndex])
                {
                    incoming[successor]--;
                    if (incoming[successor] == 0)
                    {
                        ready.Add((entries[successor].SourceIndex, successor));
                    }
                }
            }

            if (emitted != entries.Length)
            {
                var cycleEntry = Enumerable.Range(0, entries.Length).First(index => incoming[index] > 0);
                AddMetaExpansionDiagnostic(
                    entries[cycleEntry].Invocation.Clause.Span,
                    $"meta generator ordering cycle detected in stage '{stageGroup.Key}'",
                    "E3610");
                ordered = [];
                return false;
            }

            bool AddEdges(
                FuncDef generator,
                DeclarationClauseKind kind,
                int source,
                OrderingEdgeKind edgeKind)
            {
                foreach (var dependency in generator.BoundClauses
                             .Where(clause => clause.Kind == kind)
                             .SelectMany(static clause => clause.Arguments))
                {
                    var dependencyId = ResolveOrderingDependency(generator, dependency);
                    var targets = Enumerable.Range(0, entries.Length)
                        .Where(target => dependencyId.IsValid &&
                                         HasSameOrderingDomain(
                                             entries[target].Invocation,
                                             entries[source].Invocation) &&
                                         generatorIds[target] == dependencyId)
                        .ToArray();
                    var hasProcessedTarget = dependencyId.IsValid &&
                                             HasProcessedOrderingDependency(
                                                 entries[source].Invocation,
                                                 dependencyId,
                                                 stageGroup.Key);
                    if (edgeKind == OrderingEdgeKind.Requires && targets.Length == 0 && !hasProcessedTarget)
                    {
                        AddMetaExpansionDiagnostic(
                            entries[source].Invocation.Invocation.Span,
                            $"meta generator '{generator.Name}' requires expansion '{dependency.CanonicalText}' in stage '{stageGroup.Key}'",
                            "E3613");
                        return false;
                    }
                    if (edgeKind == OrderingEdgeKind.Before && hasProcessedTarget && targets.Length == 0)
                    {
                        AddMetaExpansionDiagnostic(
                            entries[source].Invocation.Invocation.Span,
                            $"meta generator '{generator.Name}' cannot run before already completed expansion '{dependency.CanonicalText}' in stage '{stageGroup.Key}'",
                            "E3612");
                        return false;
                    }

                    foreach (var target in targets)
                    {
                        var from = edgeKind == OrderingEdgeKind.Before ? source : target;
                        var to = edgeKind == OrderingEdgeKind.Before ? target : source;
                        if (outgoing[from].Add(to))
                        {
                            incoming[to]++;
                        }
                    }
                }

                return true;
            }
        }

        ordered = result;
        return true;
    }

    private bool HasProcessedOrderingDependency(
        MetaInvocationOccurrence source,
        SymbolId dependencyGeneratorId,
        ClauseStage stage)
    {
        foreach (var candidate in _metaInvocationOccurrences)
        {
            if (!_completedMetaInvocations.Contains(candidate.Invocation.OccurrenceId) ||
                candidate.Invocation.Stage != stage ||
                !HasSameOrderingDomain(candidate, source) ||
                candidate.Invocation.Owner != MetaInvocationOwner.UserExpand)
            {
                continue;
            }

            using var moduleScope = PushResolutionModuleScope(candidate.ModuleId);
            using var currentModuleScope = PushCurrentModuleScope(candidate.ModuleId);
            if (TryResolveMetaGenerator(candidate, out _, out var generatorSymbol, out _) &&
                generatorSymbol.Id == dependencyGeneratorId)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasSameOrderingDomain(
        MetaInvocationOccurrence left,
        MetaInvocationOccurrence right) =>
        ReferenceEquals(left.Target, right.Target) ||
        string.Equals(
            left.OrderingDomainIdentity,
            right.OrderingDomainIdentity,
            StringComparison.Ordinal);

    private IReadOnlyList<MetaInvocationOccurrence> NormalizeMetaInvocationStages(
        IReadOnlyList<MetaInvocationOccurrence> occurrences)
    {
        var normalized = new List<MetaInvocationOccurrence>(occurrences.Count);
        var stageByTarget = new Dictionary<Declaration, Dictionary<ClauseOccurrenceId, ClauseStage>>(
            ReferenceEqualityComparer.Instance);
        foreach (var occurrence in occurrences)
        {
            var stage = occurrence.Invocation.Stage;
            if (occurrence.Invocation.Owner == MetaInvocationOwner.UserExpand)
            {
                using var moduleScope = PushResolutionModuleScope(occurrence.ModuleId);
                using var currentModuleScope = PushCurrentModuleScope(occurrence.ModuleId);
                if (TryResolveMetaGenerator(
                        occurrence,
                        out _,
                        out _,
                        out var protocol,
                        out _))
                {
                    stage = protocol.EarliestStage;
                }
            }

            var normalizedOccurrence = stage == occurrence.Invocation.Stage
                ? occurrence
                : occurrence with { Invocation = occurrence.Invocation with { Stage = stage } };
            normalized.Add(normalizedOccurrence);
            if (!stageByTarget.TryGetValue(occurrence.Target, out var stages))
            {
                stages = [];
                stageByTarget[occurrence.Target] = stages;
            }
            stages[occurrence.Invocation.OccurrenceId] = stage;
        }

        foreach (var (target, stages) in stageByTarget)
        {
            target.SetBoundClauses(
                target.BoundClauses
                    .Select(clause => stages.TryGetValue(clause.OccurrenceId, out var stage)
                        ? clause with { Stage = stage }
                        : clause)
                    .ToArray(),
                target.MetaInvocations
                    .Select(invocation => stages.TryGetValue(invocation.OccurrenceId, out var stage)
                        ? invocation with { Stage = stage }
                        : invocation)
                    .ToArray());
        }

        return normalized;
    }

    private SymbolId ResolveOrderingDependency(FuncDef generator, ClauseArgumentIR dependency)
    {
        var path = dependency.Path.Count > 0
            ? dependency.Path
            : ParsePathText(dependency.CanonicalText);
        if (path.Count == 0)
        {
            return SymbolId.None;
        }

        var moduleId = _symbolTable.Modules.TryGetOwningModuleId(generator.SymbolId, out var ownerModuleId)
            ? ownerModuleId
            : _currentModule;
        using var moduleScope = PushResolutionModuleScope(moduleId);
        using var currentModuleScope = PushCurrentModuleScope(moduleId);
        var symbolId = path.Count == 1
            ? _lookupService.Lookup(path[0], LookupKind.Value, CreateLookupContext()).SymbolId
            : ResolvePathWithImports(path).SymbolId;
        return _symbolTable.GetSymbol<FuncSymbol>(symbolId) is { IsComptime: true }
            ? symbolId
            : SymbolId.None;
    }

    private bool TryReorderPendingMetaInvocations(int processedIndex)
    {
        var pendingStart = processedIndex + 1;
        if (pendingStart >= _metaInvocationOccurrences.Count)
        {
            return true;
        }

        var pending = _metaInvocationOccurrences.Skip(pendingStart).ToArray();
        if (!TryOrderMetaInvocationOccurrences(pending, out var ordered))
        {
            return false;
        }

        _metaInvocationOccurrences.RemoveRange(pendingStart, _metaInvocationOccurrences.Count - pendingStart);
        _metaInvocationOccurrences.AddRange(ordered);
        return true;
    }

    private enum OrderingEdgeKind
    {
        Requires,
        Before,
        After
    }

    private void ProcessCompilerOwnedDerive(MetaInvocationOccurrence invocation)
    {
        if (invocation.Invocation.CompilerGrant == null)
        {
            AddMetaExpansionDiagnostic(
                invocation.Invocation.Span,
                "compiler-owned derive invocation is missing its unforgeable grant",
                "E3606");
            return;
        }

        var traitName = string.Join(WellKnownStrings.Separators.Path, invocation.Invocation.GeneratorPath);
        if (NormalizeBuiltinDeriveTraitName(traitName) is not { } normalizedTrait)
        {
            AddError(invocation.Invocation.Span, DiagnosticMessages.DeriveUnsupportedTrait(traitName));
            return;
        }

        AddCounter("Namer.collect.deriveArgument.count");
        if (invocation.DeriveShape == null)
        {
            AddMetaExpansionDiagnostic(
                invocation.Invocation.Span,
                "compiler-owned derive invocation requires a type or case-type target",
                "E3603");
            return;
        }

        var generated = GenerateDerivedImpl(
            invocation.DeriveShape,
            normalizedTrait,
            invocation.Invocation.Span,
            invocation.TargetPath);
        if (generated != null)
        {
            RegisterGeneratedDerivedInstance(generated);
        }
    }

    private GeneratedDeclarationOrigin CreateGeneratedOrigin(
        MetaInvocationOccurrence invocation,
        FuncSymbol generator,
        Symbol target,
        string canonicalArgumentsHash,
        MaterializedMetaNode materialized)
    {
        var generatorIdentity = MetaComptimeIntrinsics.CreateStableIdentity(generator, _symbolTable);
        var targetIdentity = MetaComptimeIntrinsics.CreateStableIdentity(target, _symbolTable);
        var outputSlotPath = materialized.GenerationSlotIdentity == null
            ? $"default:{invocation.Invocation.OccurrenceId}:{materialized.OutputIndex}:{materialized.NestedIndex}"
            : $"explicit:{materialized.GenerationSlotIdentity}:{materialized.NestedIndex}";
        var generationSlotMaterial = string.Join(
            "|",
            generatorIdentity,
            targetIdentity,
            invocation.Invocation.OccurrenceId,
            invocation.Invocation.Stage,
            outputSlotPath,
            WellKnownStrings.Meta.SchemaVersion);
        var generationSlotIdentity = HashIdentity(generationSlotMaterial);
        var stableIdentity = HashIdentity($"{generationSlotMaterial}|{canonicalArgumentsHash}");
        return new GeneratedDeclarationOrigin
        {
            StableIdentity = stableIdentity,
            GenerationSlotIdentity = generationSlotIdentity,
            GeneratorIdentity = generatorIdentity,
            TargetIdentity = targetIdentity,
            GeneratorSymbolId = generator.Id,
            TargetSymbolId = target.Id,
            ClauseOccurrenceIndex = invocation.Invocation.OccurrenceId.ClauseIndex,
            ClauseOccurrenceIdentity = invocation.Invocation.OccurrenceId.ToString(),
            ClauseArgumentSubIndex = invocation.Invocation.OccurrenceId.ArgumentSubIndex,
            ExpansionOutputIndex = materialized.OutputIndex,
            CanonicalArgumentsHash = canonicalArgumentsHash,
            MetaSchemaVersion = WellKnownStrings.Meta.SchemaVersion,
            ClauseSpan = invocation.Clause.Span,
            VirtualDocumentPath = $"eidos-generated://{stableIdentity}.eidos"
        };
    }

    private static string CreateGeneratedNodePayloadHash(EidosAstNode node)
    {
        var document = new XmlDocument();
        var payload = node.ToXmlElement(document).OuterXml;
        return HashIdentity($"{node.GetType().FullName}|{payload}");
    }

    private static string CreateGeneratedDeclarationPayloadHash(Declaration declaration) =>
        CreateGeneratedNodePayloadHash(declaration);

    private void AddMetaUserDiagnostic(
        MetaDiagnosticLevel level,
        SourceSpan span,
        string message,
        string trace)
    {
        var diagnosticLevel = level == MetaDiagnosticLevel.Error
            ? EidoscDiagnosticLevel.Error
            : EidoscDiagnosticLevel.Warning;
        var code = level == MetaDiagnosticLevel.Error ? "E3610" : "W3610";
        var diagnostic = new EidoscDiagnostic(diagnosticLevel, message, code);
        diagnostic.WithLabel(span, message);
        diagnostic.WithNote($"meta expansion trace: {trace}");
        _diagnostics.Add(diagnostic);
    }

    private void AddMaterializedDiagnostic(MetaExpansionDiagnostic diagnostic, string trace)
    {
        var level = diagnostic.Level == "error" ? EidoscDiagnosticLevel.Error : EidoscDiagnosticLevel.Warning;
        var code = diagnostic.Level == "error" ? "E3611" : "W3611";
        var entry = new EidoscDiagnostic(level, diagnostic.Message, code);
        entry.WithLabel(diagnostic.Span, diagnostic.Message);
        entry.WithNote($"meta expansion trace: {trace}; output index: {diagnostic.OutputIndex}");
        _diagnostics.Add(entry);
    }

    private void AddMetaExpansionDiagnostic(SourceSpan span, string message, string code)
    {
        var diagnostic = new EidoscDiagnostic(EidoscDiagnosticLevel.Error, message, code);
        diagnostic.WithLabel(span, message);
        _diagnostics.Add(diagnostic);
    }

    private string CreateMetaInvocationInputFingerprint(
        ModuleDecl root,
        MetaInvocationOccurrence invocation)
    {
        var occurrenceId = invocation.Invocation.OccurrenceId;
        var queryInput = _queryDependentMetaInvocations.Contains(occurrenceId)
            ? string.Join(
                "|",
                CreateGeneratedNodePayloadHash(invocation.Target),
                CreateMetaCanonicalGraphFingerprint(root),
                _metaInvocationQueryFingerprints.GetValueOrDefault(
                    occurrenceId,
                    "missing-query-fingerprint"))
            : "query-independent";
        return HashIdentity(string.Join(
            "|",
            invocation.Invocation.OccurrenceId,
            invocation.Invocation.Stage,
            invocation.OrderingDomainIdentity,
            invocation.ModuleId.Value,
            string.Join(WellKnownStrings.Separators.Path, invocation.Invocation.GeneratorPath),
            queryInput,
            MetaTargetTriple,
            WellKnownStrings.Meta.SchemaVersion));
    }

    private string RecordMetaInvocationQueryDependency(
        MetaInvocationOccurrence invocation,
        MetaQueryState queryState,
        long cursor)
    {
        var fingerprint = queryState.CreateDependencyFingerprintAfter(cursor);
        _metaInvocationQueryFingerprints[invocation.Invocation.OccurrenceId] = fingerprint;
        if (queryState.HasDependenciesAfter(cursor))
        {
            _queryDependentMetaInvocations.Add(invocation.Invocation.OccurrenceId);
        }
        return fingerprint;
    }

    private string CreateMetaCanonicalGraphFingerprint(ModuleDecl root)
    {
        var document = new XmlDocument();
        var distinctModules = new HashSet<ModuleDecl>(ReferenceEqualityComparer.Instance);
        var modules = _moduleDeclarations.Values
            .Append(root)
            .Where(distinctModules.Add)
            .OrderBy(static module => string.Join(WellKnownStrings.Separators.Path, module.Path), StringComparer.Ordinal)
            .ThenBy(static module => module.Span.FilePath, StringComparer.Ordinal)
            .ThenBy(static module => module.Span.Position)
            .Select(module => module.ToXmlElement(document).OuterXml);
        return HashIdentity(string.Join("\n", modules));
    }

    private void AddNonConvergentMetaExpansionDiagnostic(
        ClauseStage stage,
        int round,
        string reason)
    {
        var producer = _metaInvocationOccurrences
            .LastOrDefault(occurrence => occurrence.Invocation.Stage == stage);
        var producerName = producer == null
            ? "<unknown>"
            : string.Join(WellKnownStrings.Separators.Path, producer.Invocation.GeneratorPath);
        var targetName = producer?.TargetName ?? "<unknown>";
        var originChain = producer?.Target.GeneratedOriginChain.Count > 0
            ? string.Join(" -> ", producer.Target.GeneratedOriginChain.Select(static origin => origin.StableIdentity))
            : "<source>";
        var span = producer?.Clause.Span ?? SourceSpan.Empty;
        AddMetaExpansionDiagnostic(
            span,
            $"meta expansion did not converge in stage '{stage}' at round {round}: {reason}; " +
            $"producer '{producerName}', target '{targetName}', origin chain '{originChain}'",
            "E3617");
    }

    private static string HashIdentity(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static IEnumerable<Declaration> EnumerateDeclarations(ModuleDecl module)
    {
        foreach (var declaration in module.Declarations)
        {
            yield return declaration;
            if (declaration is ModuleDecl nested)
            {
                foreach (var child in EnumerateDeclarations(nested))
                {
                    yield return child;
                }
            }
        }
    }

    private SymbolId GetDeclarationOwnerModuleId(Declaration declaration, SymbolId fallback)
    {
        if (_symbolTable.GetSymbol(declaration.SymbolId) is
            {
                DefinitionModuleId: { IsValid: true } definitionModuleId
            } &&
            _symbolTable.Modules.GetModule(definitionModuleId) != null)
        {
            return definitionModuleId;
        }

        foreach (var module in _moduleDeclarations.Values.DistinctBy(static module => module.SymbolId))
        {
            if (TryFindDirectOwnerModule(module, declaration, out var owner))
            {
                return owner.SymbolId;
            }
        }
        return fallback;
    }

    private static bool TryFindDirectOwnerModule(
        ModuleDecl module,
        Declaration declaration,
        out ModuleDecl owner)
    {
        if (module.Declarations.Any(candidate => ReferenceEquals(candidate, declaration)))
        {
            owner = module;
            return true;
        }
        foreach (var nested in module.Declarations.OfType<ModuleDecl>())
        {
            if (TryFindDirectOwnerModule(nested, declaration, out owner))
            {
                return true;
            }
        }
        owner = null!;
        return false;
    }
}
