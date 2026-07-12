namespace Eidosc.Pipeline;

public static class ProjectModuleArtifactRestoreExecutor
{
    public static ProjectModuleArtifactRestoreExecutionSnapshot Execute(
        ProjectModuleArtifactRestorePlan plan,
        ProjectModuleArtifactRestorePayloadSnapshot? payload = null,
        ProjectModuleDependencySignatureSnapshot? currentDependencySignatures = null,
        ProjectModuleDependencySignatureSnapshot? previousDependencySignatures = null,
        ProjectModuleDependencySignatureRequirement dependencyRequirement =
            ProjectModuleDependencySignatureRequirement.SemanticTypedMemberAndMir)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var effectivePlan = payload == null
            ? plan
            : plan.GateWithPayload(payload);
        effectivePlan = effectivePlan.GateWithDependencySignatures(
            currentDependencySignatures,
            previousDependencySignatures,
            dependencyRequirement);
        return ProjectModuleArtifactRestoreExecutionSnapshot.FromRestorePlan(effectivePlan);
    }

    public static async Task<ProjectModuleArtifactRestoreExecutionSnapshot> ExecuteAsync(
        ProjectModuleArtifactRestorePlan plan,
        Func<ProjectModuleArtifactRestoreItem, CancellationToken, ValueTask<ProjectModuleExecutionItemResult>> restore,
        Func<ProjectModuleArtifactRestoreItem, CancellationToken, ValueTask<ProjectModuleExecutionItemResult>> compile,
        ProjectModuleArtifactRestorePayloadSnapshot? payload = null,
        ProjectModuleDependencySignatureSnapshot? currentDependencySignatures = null,
        ProjectModuleDependencySignatureSnapshot? previousDependencySignatures = null,
        ProjectModuleDependencySignatureRequirement dependencyRequirement =
            ProjectModuleDependencySignatureRequirement.SemanticTypedMemberAndMir,
        int maxDegreeOfParallelism = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(restore);
        ArgumentNullException.ThrowIfNull(compile);

        var effectivePlan = payload == null
            ? plan
            : plan.GateWithPayload(payload);
        effectivePlan = effectivePlan.GateWithDependencySignatures(
            currentDependencySignatures,
            previousDependencySignatures,
            dependencyRequirement);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var layers = new List<ProjectModuleArtifactRestoreExecutionLayer>(effectivePlan.Layers.Count);
        var maxParallelism = Math.Max(1, maxDegreeOfParallelism);

        foreach (var layer in effectivePlan.Layers.OrderBy(static layer => layer.Index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var layerSnapshot = await ExecuteLayerAsync(
                layer,
                restore,
                compile,
                maxParallelism,
                cancellationToken).ConfigureAwait(false);
            layers.Add(layerSnapshot);

            if (layerSnapshot.FailedCount > 0)
            {
                break;
            }
        }

        stopwatch.Stop();
        return CreateSnapshot(
            layers,
            hasRealTaskExecution: true,
            stopwatch.Elapsed.TotalMilliseconds,
            maxParallelism);
    }

    private static async Task<ProjectModuleArtifactRestoreExecutionLayer> ExecuteLayerAsync(
        ProjectModuleArtifactRestoreLayer layer,
        Func<ProjectModuleArtifactRestoreItem, CancellationToken, ValueTask<ProjectModuleExecutionItemResult>> restore,
        Func<ProjectModuleArtifactRestoreItem, CancellationToken, ValueTask<ProjectModuleExecutionItemResult>> compile,
        int maxDegreeOfParallelism,
        CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var semaphore = new SemaphoreSlim(maxDegreeOfParallelism);
        var running = 0;
        var observedParallelism = 0;
        var tasks = layer.Modules
            .OrderBy(static module => module.ModuleKey, StringComparer.Ordinal)
            .Select(async module =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                var current = Interlocked.Increment(ref running);
                UpdateMax(ref observedParallelism, current);
                try
                {
                    return await Task.Run(
                            async () => await ExecuteItemAsync(module, restore, compile, cancellationToken).ConfigureAwait(false),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref running);
                    semaphore.Release();
                }
            })
            .ToArray();

        var items = await Task.WhenAll(tasks).ConfigureAwait(false);
        stopwatch.Stop();
        var orderedItems = items
            .OrderBy(static item => item.ModuleKey, StringComparer.Ordinal)
            .ToArray();
        return new ProjectModuleArtifactRestoreExecutionLayer(
            layer.Index,
            orderedItems,
            orderedItems.Count(static item => item.Action == ProjectModuleArtifactRestoreExecutionAction.Restored),
            orderedItems.Count(static item => item.Action == ProjectModuleArtifactRestoreExecutionAction.Blocked),
            orderedItems.Count(static item => item.Action == ProjectModuleArtifactRestoreExecutionAction.Compiled),
            orderedItems.Count(static item => item.Action == ProjectModuleArtifactRestoreExecutionAction.ReadyArtifact),
            orderedItems.Count(static item => item.Status == ProjectModuleExecutionItemStatus.Failed),
            orderedItems.Count(static item => item.Status == ProjectModuleExecutionItemStatus.Skipped),
            stopwatch.Elapsed.TotalMilliseconds,
            observedParallelism);
    }

    private static async ValueTask<ProjectModuleArtifactRestoreExecutionItem> ExecuteItemAsync(
        ProjectModuleArtifactRestoreItem module,
        Func<ProjectModuleArtifactRestoreItem, CancellationToken, ValueTask<ProjectModuleExecutionItemResult>> restore,
        Func<ProjectModuleArtifactRestoreItem, CancellationToken, ValueTask<ProjectModuleExecutionItemResult>> compile,
        CancellationToken cancellationToken)
    {
        try
        {
            var action = ToExecutionAction(module.Action);
            var result = module.Action switch
            {
                ProjectModuleArtifactRestoreAction.Restore => await restore(module, cancellationToken).ConfigureAwait(false),
                ProjectModuleArtifactRestoreAction.Compile => await compile(module, cancellationToken).ConfigureAwait(false),
                ProjectModuleArtifactRestoreAction.ReadyArtifact =>
                    ProjectModuleExecutionItemResult.Skipped("ready artifact"),
                _ => ProjectModuleExecutionItemResult.Skipped("blocked")
            };

            return CreateItem(module, action, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return CreateItem(
                module,
                ToExecutionAction(module.Action),
                ProjectModuleExecutionItemResult.Failed(exception.Message));
        }
    }

    private static ProjectModuleArtifactRestoreExecutionSnapshot CreateSnapshot(
        IReadOnlyList<ProjectModuleArtifactRestoreExecutionLayer> layers,
        bool hasRealTaskExecution,
        double elapsedMs,
        int maxDegreeOfParallelism) =>
        new(
            ProjectModuleArtifactRestoreExecutionSnapshot.CurrentSchemaVersion,
            layers,
            layers.Sum(static layer => layer.Modules.Count),
            layers.Sum(static layer => layer.RestoredCount),
            layers.Sum(static layer => layer.BlockedCount),
            layers.Sum(static layer => layer.CompiledCount),
            layers.Sum(static layer => layer.ReadyArtifactCount),
            layers.Count == 0 ? 0 : layers.Max(static layer => layer.RestoredCount),
            layers.Count == 0 ? 0 : layers.Max(static layer => layer.CompiledCount),
            layers.Sum(static layer => layer.FailedCount),
            layers.Sum(static layer => layer.SkippedCount),
            hasRealTaskExecution,
            elapsedMs,
            layers.Count == 0 ? 0 : layers.Max(static layer => layer.ObservedParallelism),
            maxDegreeOfParallelism);

    private static ProjectModuleArtifactRestoreExecutionItem CreateItem(
        ProjectModuleArtifactRestoreItem module,
        ProjectModuleArtifactRestoreExecutionAction action,
        ProjectModuleExecutionItemResult result) =>
        new(
            module.ModuleKey,
            action,
            module.SemanticReady,
            module.TypedSemanticReady,
            module.MirReady,
            result.Status,
            result.Message);

    private static ProjectModuleArtifactRestoreExecutionAction ToExecutionAction(
        ProjectModuleArtifactRestoreAction action) =>
        action switch
        {
            ProjectModuleArtifactRestoreAction.Restore => ProjectModuleArtifactRestoreExecutionAction.Restored,
            ProjectModuleArtifactRestoreAction.Blocked => ProjectModuleArtifactRestoreExecutionAction.Blocked,
            ProjectModuleArtifactRestoreAction.ReadyArtifact => ProjectModuleArtifactRestoreExecutionAction.ReadyArtifact,
            _ => ProjectModuleArtifactRestoreExecutionAction.Compiled
        };

    private static void UpdateMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
            {
                return;
            }
        } while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}
