using System.Text;
using System.Diagnostics;
using Eidosc.CodeGen.Llvm;
using Eidosc.Diagnostic;

namespace Eidosc.CodeGen;

public sealed partial class LlvmCompiler
{
    public CodeGenResult CompileRestoredFragmentsToExecutableWithObjectGroups(
        LlvmModuleEnvelopeSnapshot envelope,
        LlvmFunctionFragmentSnapshot fragments,
        LlvmCodegenUnitPlanSnapshot codegenUnitPlan,
        string outputPath,
        int maxObjectGroups = 0,
        LlvmObjectGroupRestorePlanSnapshot? restorePlan = null)
    {
        _profile?.Record(
            "llvm",
            "native_object_groups_restore_from_previous_fragments",
            "eidosc",
            TimeSpan.Zero,
            success: true,
            cacheHit: true,
            metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["functions"] = fragments.Functions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["objectGroups"] = codegenUnitPlan.ObjectGroups.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["envelopeFingerprint"] = envelope.EnvelopeFingerprint,
                ["fragmentFingerprint"] = fragments.ModuleFingerprint
            });
        var plannedGroups = CoalesceObjectGroups(codegenUnitPlan.ObjectGroups, maxObjectGroups, restorePlan);
        var effectiveRestorePlan = restorePlan == null
            ? null
            : RebuildObjectGroupRestorePlanForGroups(plannedGroups, restorePlan);
        var tempObjectDirectory = CreateTemporaryPath("groups_", "");
        var tempEnvelopeObjectPath = CreateTemporaryPath("envelope_", _targetInfo.ObjectExtension);
        var tempEntrySourcePath = CreateTemporaryPath("entry_", ".c");
        var tempEntryObjPath = CreateTemporaryPath("entry_", _targetInfo.ObjectExtension);
        var tempNativeObjects = new List<string>();
        var tempRuntimeObjects = new List<string>();

        try
        {
            Directory.CreateDirectory(tempObjectDirectory);
            var envelopeResult = CompileModuleEnvelopeObject(envelope, tempEnvelopeObjectPath);
            if (!envelopeResult.Success)
            {
                return envelopeResult;
            }

            var groupCompileSw = Stopwatch.StartNew();
            var groupResults = CompileObjectGroups(
                envelope,
                fragments,
                plannedGroups,
                tempObjectDirectory,
                effectiveRestorePlan);
            groupCompileSw.Stop();
            var failedGroup = groupResults.FirstOrDefault(static result => !result.Result.Success);
            if (failedGroup is { Result: { Success: false } groupFailure })
            {
                return groupFailure;
            }

            RecordObjectGroupSummary(groupResults, groupCompileSw.Elapsed, maxObjectGroups);
            RecordObjectGroupRestoreSummary(effectiveRestorePlan, groupResults);

            var objectFiles = new List<string> { tempEnvelopeObjectPath };
            objectFiles.AddRange(groupResults.Select(static result => result.ObjectPath));
            var entryResult = TryCompileEntryShim(GetFunctionNames(fragments), tempEntrySourcePath, tempEntryObjPath);
            if (!entryResult.Success)
            {
                return entryResult;
            }

            if (File.Exists(tempEntryObjPath))
            {
                objectFiles.Add(tempEntryObjPath);
            }

            var nativeResults = CompileNativeSources(envelope.NativeSources, envelope.NativeIncludePaths);
            tempNativeObjects.AddRange(nativeResults.Select(static result => result.ObjectPath));
            foreach (var nativeResult in nativeResults)
            {
                if (!nativeResult.Result.Success)
                {
                    return nativeResult.Result;
                }

                objectFiles.Add(nativeResult.ObjectPath);
            }

            var runtimeResolveResult = TryResolveRuntimeLinkInputs(out var runtimeLinkInputs, tempRuntimeObjects);
            if (!runtimeResolveResult.Success)
            {
                return runtimeResolveResult;
            }

            objectFiles.AddRange(runtimeLinkInputs);
            var linkLibraries = envelope.LinkLibraries.Count > 0 ? envelope.LinkLibraries.ToArray() : null;
            var linkLibraryPaths = envelope.LinkLibraryPaths.Count > 0 ? envelope.LinkLibraryPaths.ToArray() : null;
            var linkerFlags = envelope.LinkerFlags.Count > 0 ? envelope.LinkerFlags.ToArray() : null;
            return LinkExecutable(objectFiles.ToArray(), outputPath, linkLibraries, linkLibraryPaths, linkerFlags);
        }
        finally
        {
            if (File.Exists(tempEnvelopeObjectPath))
            {
                File.Delete(tempEnvelopeObjectPath);
            }

            if (Directory.Exists(tempObjectDirectory))
            {
                Directory.Delete(tempObjectDirectory, recursive: true);
            }

            if (File.Exists(tempEntryObjPath))
            {
                File.Delete(tempEntryObjPath);
            }

            if (File.Exists(tempEntrySourcePath))
            {
                File.Delete(tempEntrySourcePath);
            }

            foreach (var tempNativeObject in tempNativeObjects)
            {
                if (File.Exists(tempNativeObject))
                {
                    File.Delete(tempNativeObject);
                }
            }

            foreach (var tempRuntimeObject in tempRuntimeObjects)
            {
                if (File.Exists(tempRuntimeObject))
                {
                    File.Delete(tempRuntimeObject);
                }
            }
        }
    }

    public CodeGenResult CompileToExecutableWithObjectGroups(
        LlvmModule module,
        string outputPath,
        int maxObjectGroups = 0,
        LlvmFunctionFragmentSnapshot? restoredFragments = null,
        LlvmObjectGroupRestorePlanSnapshot? restorePlan = null)
    {
        var envelope = LlvmModuleEnvelopeSnapshot.FromModule(module, _targetInfo.DataLayout, _targetInfo.Triple);
        var fragments = LlvmFunctionFragmentSnapshot.FromModule(module);
        var plan = LlvmCodegenUnitPlanSnapshot.Create(envelope, module, fragments, _backendConfiguration);
        var plannedGroups = CoalesceObjectGroups(plan.ObjectGroups, maxObjectGroups, restorePlan);
        var effectiveFragments = restoredFragments ?? fragments;
        var effectiveRestorePlan = restorePlan == null
            ? null
            : RebuildObjectGroupRestorePlanForGroups(plannedGroups, restorePlan);
        var tempObjectDirectory = CreateTemporaryPath("groups_", "");
        var tempEnvelopeObjectPath = CreateTemporaryPath("envelope_", _targetInfo.ObjectExtension);
        var tempEntrySourcePath = CreateTemporaryPath("entry_", ".c");
        var tempEntryObjPath = CreateTemporaryPath("entry_", _targetInfo.ObjectExtension);
        var tempNativeObjects = new List<string>();
        var tempRuntimeObjects = new List<string>();

        try
        {
            Directory.CreateDirectory(tempObjectDirectory);
            var envelopeResult = CompileModuleEnvelopeObject(envelope, tempEnvelopeObjectPath);
            if (!envelopeResult.Success)
            {
                return envelopeResult;
            }

            var groupCompileSw = Stopwatch.StartNew();
            var groupResults = CompileObjectGroups(
                envelope,
                effectiveFragments,
                plannedGroups,
                tempObjectDirectory,
                effectiveRestorePlan);
            groupCompileSw.Stop();
            var failedGroup = groupResults.FirstOrDefault(static result => !result.Result.Success);
            if (failedGroup is { Result: { Success: false } groupFailure })
            {
                return groupFailure;
            }

            RecordObjectGroupSummary(groupResults, groupCompileSw.Elapsed, maxObjectGroups);
            RecordObjectGroupRestoreSummary(effectiveRestorePlan, groupResults);

            var objectFiles = new List<string> { tempEnvelopeObjectPath };
            objectFiles.AddRange(groupResults.Select(static result => result.ObjectPath));
            var entryResult = TryCompileEntryShim(module, tempEntrySourcePath, tempEntryObjPath);
            if (!entryResult.Success)
            {
                return entryResult;
            }

            if (File.Exists(tempEntryObjPath))
            {
                objectFiles.Add(tempEntryObjPath);
            }

            var nativeResults = CompileNativeSources(module.NativeSources, module.NativeIncludePaths);
            tempNativeObjects.AddRange(nativeResults.Select(static result => result.ObjectPath));
            foreach (var nativeResult in nativeResults)
            {
                if (!nativeResult.Result.Success)
                {
                    return nativeResult.Result;
                }

                objectFiles.Add(nativeResult.ObjectPath);
            }

            var runtimeResolveResult = TryResolveRuntimeLinkInputs(out var runtimeLinkInputs, tempRuntimeObjects);
            if (!runtimeResolveResult.Success)
            {
                return runtimeResolveResult;
            }

            objectFiles.AddRange(runtimeLinkInputs);
            var linkLibraries = module.LinkLibraries.Count > 0 ? module.LinkLibraries.ToArray() : null;
            var linkLibraryPaths = module.LinkLibraryPaths.Count > 0 ? module.LinkLibraryPaths.ToArray() : null;
            var linkerFlags = module.LinkerFlags.Count > 0 ? module.LinkerFlags.ToArray() : null;
            return LinkExecutable(objectFiles.ToArray(), outputPath, linkLibraries, linkLibraryPaths, linkerFlags);
        }
        finally
        {
            if (File.Exists(tempEnvelopeObjectPath))
            {
                File.Delete(tempEnvelopeObjectPath);
            }

            if (Directory.Exists(tempObjectDirectory))
            {
                Directory.Delete(tempObjectDirectory, recursive: true);
            }

            if (File.Exists(tempEntryObjPath))
            {
                File.Delete(tempEntryObjPath);
            }

            if (File.Exists(tempEntrySourcePath))
            {
                File.Delete(tempEntrySourcePath);
            }

            foreach (var tempNativeObject in tempNativeObjects)
            {
                if (File.Exists(tempNativeObject))
                {
                    File.Delete(tempNativeObject);
                }
            }

            foreach (var tempRuntimeObject in tempRuntimeObjects)
            {
                if (File.Exists(tempRuntimeObject))
                {
                    File.Delete(tempRuntimeObject);
                }
            }
        }
    }

    private static LlvmObjectGroupRestorePlanSnapshot RebuildObjectGroupRestorePlanForGroups(
        IReadOnlyList<LlvmCodegenUnitPlanObjectGroup> groups,
        LlvmObjectGroupRestorePlanSnapshot restorePlan)
    {
        var restorableFunctions = restorePlan.Groups
            .Where(static group => group.Action == LlvmObjectGroupRestoreAction.Restore)
            .SelectMany(static group => group.MemberFunctionKeys)
            .ToHashSet(StringComparer.Ordinal);
        var entries = groups
            .Select(group =>
            {
                var restoreFunctions = group.MemberFunctionKeys.Count(restorableFunctions.Contains);
                var rebuildFunctions = group.MemberFunctionKeys.Count - restoreFunctions;
                return new LlvmObjectGroupRestorePlanEntry(
                    group.GroupKey,
                    group.RootFunctionKey,
                    rebuildFunctions == 0 && group.MemberFunctionKeys.Count > 0
                        ? LlvmObjectGroupRestoreAction.Restore
                        : LlvmObjectGroupRestoreAction.Rebuild,
                    group.MemberFunctionKeys,
                    restoreFunctions,
                    rebuildFunctions,
                    group.TotalIrBytes);
            })
            .OrderBy(static entry => entry.RootFunctionKey, StringComparer.Ordinal)
            .ToArray();

        return new LlvmObjectGroupRestorePlanSnapshot(
            "llvm-object-group-restore-plan-snapshot-v1",
            entries);
    }

    public CodeGenResult CompileToExecutableWithObjectGroups(
        LlvmModule module,
        string outputPath,
        int maxObjectGroups,
        LlvmFunctionFragmentRestoreExecution restoreExecution,
        LlvmObjectGroupRestorePlanSnapshot restorePlan)
    {
        if (!restoreExecution.Result.Applied)
        {
            return CompileToExecutableWithObjectGroups(module, outputPath, maxObjectGroups);
        }

        return CompileToExecutableWithObjectGroups(
            module,
            outputPath,
            maxObjectGroups,
            restoreExecution.Fragments,
            restorePlan);
    }

    private static IReadOnlyList<LlvmCodegenUnitPlanObjectGroup> CoalesceObjectGroups(
        IReadOnlyList<LlvmCodegenUnitPlanObjectGroup> groups,
        int maxObjectGroups,
        LlvmObjectGroupRestorePlanSnapshot? restorePlan = null)
    {
        if (maxObjectGroups <= 0 || groups.Count <= maxObjectGroups)
        {
            return groups;
        }

        if (restorePlan != null)
        {
            return CoalesceObjectGroupsPreservingRestoreActions(groups, maxObjectGroups, restorePlan);
        }

        var chunkSize = (int)Math.Ceiling(groups.Count / (double)maxObjectGroups);
        var merged = new List<LlvmCodegenUnitPlanObjectGroup>(maxObjectGroups);
        for (var start = 0; start < groups.Count; start += chunkSize)
        {
            var chunk = groups.Skip(start).Take(chunkSize).ToArray();
            var memberKeys = chunk
                .SelectMany(static group => group.MemberFunctionKeys)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static key => key, StringComparer.Ordinal)
                .ToArray();
            var referencedSymbols = chunk
                .SelectMany(static group => group.ReferencedSymbols)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static symbol => symbol, StringComparer.Ordinal)
                .ToArray();
            var referencedTypeNames = chunk
                .SelectMany(static group => group.ReferencedTypeNames)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static typeName => typeName, StringComparer.Ordinal)
                .ToArray();
            var rootKeys = chunk.Select(static group => group.RootFunctionKey).ToArray();
            var groupKey = Pipeline.ModuleArtifactHash.ComputeJsonHash(new
            {
                schema = "llvm-codegen-object-group-merged-v1",
                roots = rootKeys,
                members = memberKeys,
                refs = referencedSymbols,
                types = referencedTypeNames
            });

            merged.Add(new LlvmCodegenUnitPlanObjectGroup(
                groupKey,
                $"merged:{ShortenCacheKey(groupKey)}",
                memberKeys,
                referencedSymbols,
                referencedTypeNames,
                chunk.Sum(static group => group.TotalIrBytes),
                memberKeys.Length));
        }

        return merged;
    }

    private static IReadOnlyList<LlvmCodegenUnitPlanObjectGroup> CoalesceObjectGroupsPreservingRestoreActions(
        IReadOnlyList<LlvmCodegenUnitPlanObjectGroup> groups,
        int maxObjectGroups,
        LlvmObjectGroupRestorePlanSnapshot restorePlan)
    {
        var actionByGroupKey = restorePlan.Groups.ToDictionary(
            static group => group.GroupKey,
            static group => group.Action,
            StringComparer.Ordinal);
        var restoreGroups = groups
            .Where(group =>
                actionByGroupKey.TryGetValue(group.GroupKey, out var action) &&
                action == LlvmObjectGroupRestoreAction.Restore)
            .ToArray();
        var rebuildGroups = groups
            .Where(group =>
                !actionByGroupKey.TryGetValue(group.GroupKey, out var action) ||
                action == LlvmObjectGroupRestoreAction.Rebuild)
            .ToArray();

        if (restoreGroups.Length == 0 || rebuildGroups.Length == 0)
        {
            return CoalesceObjectGroups(groups, maxObjectGroups);
        }

        var restoreBudget = Math.Max(1, (int)Math.Round(maxObjectGroups * (restoreGroups.Length / (double)groups.Count)));
        restoreBudget = Math.Min(restoreGroups.Length, restoreBudget);
        var rebuildBudget = Math.Max(1, maxObjectGroups - restoreBudget);
        rebuildBudget = Math.Min(rebuildGroups.Length, rebuildBudget);
        while (restoreBudget + rebuildBudget > maxObjectGroups)
        {
            if (restoreBudget >= rebuildBudget && restoreBudget > 1)
            {
                restoreBudget--;
            }
            else if (rebuildBudget > 1)
            {
                rebuildBudget--;
            }
            else
            {
                break;
            }
        }

        return CoalesceObjectGroups(restoreGroups, restoreBudget)
            .Concat(CoalesceObjectGroups(rebuildGroups, rebuildBudget))
            .OrderBy(static group => group.RootFunctionKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> GetFunctionNames(LlvmFunctionFragmentSnapshot fragments)
    {
        return fragments.Functions
            .Select(static fragment => fragment.FunctionKey)
            .Where(static key => key.StartsWith("name:", StringComparison.Ordinal))
            .Select(static key => key["name:".Length..])
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private void RecordObjectGroupSummary(
        IReadOnlyList<LlvmObjectGroupCompileResult> results,
        TimeSpan compileBatchElapsed,
        int maxObjectGroups)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["groups"] = results.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["functions"] = results.Sum(static result => result.FunctionCount).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["irBytes"] = results.Sum(static result => result.IrBytes).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["maxGroupIrBytes"] = (results.Count == 0 ? 0 : results.Max(static result => result.IrBytes)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["objects"] = results.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["successfulObjects"] = results.Count(static result => result.Result.Success).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["cacheHits"] = results.Count(static result => result.Result.CacheHit).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["stableCacheHits"] = results.Count(static result => result.StableCacheHit).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["recomposedGroups"] = results.Count(static result => result.Recomposed).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["compileBatchElapsedMs"] = compileBatchElapsed.TotalMilliseconds.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            ["maxObjectGroups"] = maxObjectGroups.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["coalesced"] = (maxObjectGroups > 0).ToString()
        };
        _profile?.Record(
            "object",
            "llvm_object_group_summary",
            "eidosc",
            TimeSpan.Zero,
            success: results.All(static result => result.Result.Success),
            metadata: metadata);
    }

    internal void RecordObjectGroupRestoreSummaryForTesting(
        LlvmObjectGroupRestorePlanSnapshot restorePlan,
        IReadOnlyList<LlvmObjectGroupCompileResult> results)
    {
        RecordObjectGroupRestoreSummary(restorePlan, results);
    }

    internal void RecordObjectGroupSummaryForTesting(
        IReadOnlyList<LlvmObjectGroupCompileResult> results,
        TimeSpan compileBatchElapsed,
        int maxObjectGroups)
    {
        RecordObjectGroupSummary(results, compileBatchElapsed, maxObjectGroups);
    }

    internal static LlvmObjectGroupRestorePlanSnapshot CoalesceObjectGroupRestorePlanForTesting(
        IReadOnlyList<LlvmCodegenUnitPlanObjectGroup> groups,
        int maxObjectGroups,
        LlvmObjectGroupRestorePlanSnapshot restorePlan)
    {
        var plannedGroups = CoalesceObjectGroups(groups, maxObjectGroups, restorePlan);
        return RebuildObjectGroupRestorePlanForGroups(plannedGroups, restorePlan);
    }

    private void RecordObjectGroupRestoreSummary(
        LlvmObjectGroupRestorePlanSnapshot? restorePlan,
        IReadOnlyList<LlvmObjectGroupCompileResult> results)
    {
        if (restorePlan == null)
        {
            return;
        }

        var resultByGroup = results.ToDictionary(static result => result.GroupKey, StringComparer.Ordinal);
        var restorableGroups = restorePlan.Count(LlvmObjectGroupRestoreAction.Restore);
        var rebuildGroups = restorePlan.Count(LlvmObjectGroupRestoreAction.Rebuild);
        var restoreFunctions = restorePlan.Groups.Sum(static group => group.RestoreFunctions);
        var rebuildFunctions = restorePlan.Groups.Sum(static group => group.RebuildFunctions);
        var restoredObjectCacheHits = restorePlan.Groups.Count(group =>
            group.Action == LlvmObjectGroupRestoreAction.Restore &&
            resultByGroup.TryGetValue(group.GroupKey, out var result) &&
            result.Result.CacheHit);
        var rebuiltObjectCacheHits = restorePlan.Groups.Count(group =>
            group.Action == LlvmObjectGroupRestoreAction.Rebuild &&
            resultByGroup.TryGetValue(group.GroupKey, out var result) &&
            result.Result.CacheHit);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["groups"] = restorePlan.Groups.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["restorableGroups"] = restorableGroups.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["rebuildGroups"] = rebuildGroups.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["restoredObjectCacheHits"] = restoredObjectCacheHits.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["rebuiltObjectCacheHits"] = rebuiltObjectCacheHits.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["restoreFunctions"] = restoreFunctions.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["rebuildFunctions"] = rebuildFunctions.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["mixed"] = (restorableGroups > 0 && rebuildGroups > 0).ToString(),
            ["allRestorableGroupsHitObjectCache"] = (restorableGroups > 0 && restoredObjectCacheHits == restorableGroups).ToString(),
            ["restorableIrBytes"] = restorePlan.Groups
                .Where(static group => group.Action == LlvmObjectGroupRestoreAction.Restore)
                .Sum(static group => group.TotalIrBytes)
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["rebuildIrBytes"] = restorePlan.Groups
                .Where(static group => group.Action == LlvmObjectGroupRestoreAction.Rebuild)
                .Sum(static group => group.TotalIrBytes)
                .ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        _profile?.Record(
            "object",
            "llvm_object_group_restore_summary",
            "eidosc",
            TimeSpan.Zero,
            success: results.All(static result => result.Result.Success),
            cacheHit: restorableGroups > 0 && restoredObjectCacheHits == restorableGroups,
            metadata: metadata);
    }

    /// <summary>
    /// 将重组后的 LLVM object group 编译为目标文件。
    /// </summary>
    public CodeGenResult CompileObjectGroup(LlvmRecomposedObjectGroupSnapshot objectGroup, string outputPath)
    {
        var stableCacheKey = ComputeObjectGroupStableCacheKey(objectGroup, outputPath);
        var result = CompileIrToObject(
            objectGroup.IrText,
            outputPath,
            "llvm-object-group-v1",
            "object",
            "object_cache.llvm_object_group",
            [
                $"backendConfig={_backendConfiguration.StableHash}",
                $"schema={objectGroup.SchemaVersion}",
                $"group={objectGroup.GroupKey}",
                $"root={objectGroup.RootFunctionKey}",
                $"functions={objectGroup.FunctionCount}"
            ]);
        if (result.Success)
        {
            StoreObjectCache(
                stableCacheKey,
                outputPath,
                "object",
                "object_cache.llvm_object_group_stable_alias");
        }

        return result;
    }

    /// <summary>
    /// 将 LLVM module envelope 编译为承载全局定义的目标文件。
    /// </summary>
    public CodeGenResult CompileModuleEnvelopeObject(LlvmModuleEnvelopeSnapshot envelope, string outputPath)
    {
        return CompileIrToObject(
            LlvmFunctionFingerprintBuilder.RecomposeModule(
                envelope,
                new LlvmFunctionFragmentSnapshot(LlvmFunctionFragmentSnapshot.CurrentSchemaVersion, [])).IrText,
            outputPath,
            "llvm-envelope-object-v1",
            "object",
            "object_cache.llvm_envelope",
            [
                $"backendConfig={_backendConfiguration.StableHash}",
                $"schema={envelope.SchemaVersion}",
                $"envelope={envelope.EnvelopeFingerprint}"
            ]);
    }

    /// <summary>
    /// 以稳定输入顺序并行编译 LLVM object groups。
    /// </summary>
    public IReadOnlyList<LlvmObjectGroupCompileResult> CompileObjectGroups(
        IReadOnlyList<LlvmRecomposedObjectGroupSnapshot> objectGroups,
        string outputDirectory)
    {
        if (objectGroups.Count == 0)
        {
            return [];
        }

        Directory.CreateDirectory(outputDirectory);
        var results = new LlvmObjectGroupCompileResult[objectGroups.Count];
        Parallel.For(
            0,
            objectGroups.Count,
            CreateBoundedObjectCompileParallelOptions(),
            index =>
            {
                var group = objectGroups[index];
                var objectPath = Path.Combine(
                    outputDirectory,
                    $"{WellKnownStrings.Mangling.Prefix}group_{index:000000}_{ShortenCacheKey(group.GroupKey)}{_targetInfo.ObjectExtension}");
                results[index] = new LlvmObjectGroupCompileResult(
                    group.GroupKey,
                    group.RootFunctionKey,
                    objectPath,
                    CompileObjectGroup(group, objectPath),
                    group.FunctionCount,
                    group.IrBytes,
                    Recomposed: true,
                    StableCacheHit: false);
            });

        return results;
    }

    public IReadOnlyList<LlvmObjectGroupCompileResult> CompileObjectGroups(
        LlvmModuleEnvelopeSnapshot envelope,
        LlvmFunctionFragmentSnapshot fragments,
        IReadOnlyList<LlvmCodegenUnitPlanObjectGroup> objectGroups,
        string outputDirectory,
        LlvmObjectGroupRestorePlanSnapshot? restorePlan)
    {
        if (objectGroups.Count == 0)
        {
            return [];
        }

        Directory.CreateDirectory(outputDirectory);
        var restoreActionByGroup = restorePlan?.Groups.ToDictionary(
            static group => group.GroupKey,
            static group => group.Action,
            StringComparer.Ordinal);
        var results = new LlvmObjectGroupCompileResult[objectGroups.Count];
        Parallel.For(
            0,
            objectGroups.Count,
            CreateBoundedObjectCompileParallelOptions(),
            index =>
            {
                var group = objectGroups[index];
                var objectPath = Path.Combine(
                    outputDirectory,
                    $"{WellKnownStrings.Mangling.Prefix}group_{index:000000}_{ShortenCacheKey(group.GroupKey)}{_targetInfo.ObjectExtension}");
                if (restoreActionByGroup != null &&
                    restoreActionByGroup.TryGetValue(group.GroupKey, out var action) &&
                    action == LlvmObjectGroupRestoreAction.Restore)
                {
                    var stableCacheKey = ComputeObjectGroupStableCacheKey(group, objectPath);
                    var stableCacheResult = TryCopyCachedObject(
                        stableCacheKey,
                        objectPath,
                        "object",
                        "object_cache.llvm_object_group_stable_restore");
                    if (stableCacheResult != null)
                    {
                        results[index] = new LlvmObjectGroupCompileResult(
                            group.GroupKey,
                            group.RootFunctionKey,
                            objectPath,
                            stableCacheResult,
                            group.FunctionCount,
                            group.TotalIrBytes,
                            Recomposed: false,
                            StableCacheHit: true);
                        return;
                    }
                }

                var recomposedGroup = LlvmFunctionFingerprintBuilder.RecomposeObjectGroup(envelope, fragments, group);
                results[index] = new LlvmObjectGroupCompileResult(
                    group.GroupKey,
                    group.RootFunctionKey,
                    objectPath,
                    CompileObjectGroup(recomposedGroup, objectPath),
                    recomposedGroup.FunctionCount,
                    recomposedGroup.IrBytes,
                    Recomposed: true,
                    StableCacheHit: false);
            });

        return results;
    }

    private string ComputeObjectGroupStableCacheKey(
        LlvmRecomposedObjectGroupSnapshot objectGroup,
        string outputPath)
    {
        _ = outputPath;
        return ComputeStableObjectIdentityCacheKey(
            "llvm-object-group-stable-v1",
            [
                $"backendConfig={_backendConfiguration.StableHash}",
                $"schema={objectGroup.SchemaVersion}",
                $"group={objectGroup.GroupKey}",
                $"root={objectGroup.RootFunctionKey}"
            ]);
    }

    private string ComputeObjectGroupStableCacheKey(
        LlvmCodegenUnitPlanObjectGroup group,
        string outputPath)
    {
        _ = outputPath;
        return ComputeStableObjectIdentityCacheKey(
            "llvm-object-group-stable-v1",
            [
                $"backendConfig={_backendConfiguration.StableHash}",
                "schema=llvm-recomposed-object-group-snapshot-v1",
                $"group={group.GroupKey}",
                $"root={group.RootFunctionKey}"
            ]);
    }

    public CodeGenResult LinkRelocatableObject(string[] objectFiles, string outputPath)
    {
        var clangPath = FindTool("clang");
        if (clangPath == null)
        {
            return new CodeGenResult
            {
                Success = false,
                ErrorMessage = DiagnosticMessages.ClangNotFoundForNativeFfi
            };
        }

        var arguments = new StringBuilder();
        arguments.Append($"-target {_targetInfo.Triple} ");
        arguments.Append("-r -nostdlib ");
        foreach (var objectFile in objectFiles)
        {
            arguments.Append($"\"{objectFile}\" ");
        }

        arguments.Append($"-o \"{outputPath}\"");
        var result = RunProcess(clangPath, arguments.ToString(), "link", "clang_link_relocatable_object");
        return result.Success
            ? new CodeGenResult
            {
                Success = true,
                Output = result.Output,
                ErrorMessage = result.ErrorMessage,
                ExitCode = result.ExitCode,
                OutputPath = outputPath
            }
            : result;
    }
}

public sealed record LlvmObjectGroupCompileResult(
    string GroupKey,
    string RootFunctionKey,
    string ObjectPath,
    CodeGenResult Result,
    int FunctionCount,
    int IrBytes,
    bool Recomposed,
    bool StableCacheHit);
