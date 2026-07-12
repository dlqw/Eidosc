using System.Diagnostics;

namespace Eidosc.Pipeline;

public sealed class ProjectModuleParallelExecutor
{
    private readonly int _maxDegreeOfParallelism;

    public ProjectModuleParallelExecutor(int maxDegreeOfParallelism)
    {
        _maxDegreeOfParallelism = Math.Max(1, maxDegreeOfParallelism);
    }

    public async Task<ProjectModuleParallelExecutionSnapshot> ExecuteAsync(
        ProjectModuleExecutionPlan plan,
        Func<ProjectModuleExecutionItem, CancellationToken, ValueTask<ProjectModuleExecutionItemResult>> execute,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(execute);

        var stopwatch = Stopwatch.StartNew();
        var layers = new List<ProjectModuleParallelExecutionLayer>(plan.Layers.Count);
        var completedModules = 0;
        var failedModules = 0;
        var skippedModules = 0;
        var maxObservedParallelism = 0;

        foreach (var layer in plan.Layers.OrderBy(static layer => layer.Index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var layerResult = await ExecuteLayerAsync(
                layer,
                execute,
                cancellationToken).ConfigureAwait(false);
            layers.Add(layerResult);
            completedModules += layerResult.CompletedModules;
            failedModules += layerResult.FailedModules;
            skippedModules += layerResult.SkippedModules;
            maxObservedParallelism = Math.Max(maxObservedParallelism, layerResult.ObservedParallelism);

            if (layerResult.FailedModules > 0)
            {
                break;
            }
        }

        stopwatch.Stop();
        return new ProjectModuleParallelExecutionSnapshot(
            ProjectModuleParallelExecutionSnapshot.CurrentSchemaVersion,
            layers,
            plan.TotalModules,
            completedModules,
            failedModules,
            skippedModules,
            maxObservedParallelism,
            _maxDegreeOfParallelism,
            stopwatch.Elapsed.TotalMilliseconds);
    }

    private async Task<ProjectModuleParallelExecutionLayer> ExecuteLayerAsync(
        ProjectModuleExecutionLayer layer,
        Func<ProjectModuleExecutionItem, CancellationToken, ValueTask<ProjectModuleExecutionItemResult>> execute,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var semaphore = new SemaphoreSlim(_maxDegreeOfParallelism);
        var running = 0;
        var observedParallelism = 0;
        var tasks = layer.Modules
            .OrderBy(static item => item.ModuleKey, StringComparer.Ordinal)
            .Select(async item =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                var current = Interlocked.Increment(ref running);
                UpdateMax(ref observedParallelism, current);
                try
                {
                    var result = await execute(item, cancellationToken).ConfigureAwait(false);
                    return ProjectModuleParallelExecutionItem.From(item, result);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    return ProjectModuleParallelExecutionItem.FromException(item, exception);
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
        return new ProjectModuleParallelExecutionLayer(
            layer.Index,
            orderedItems,
            orderedItems.Count(static item => item.Status == ProjectModuleExecutionItemStatus.Completed),
            orderedItems.Count(static item => item.Status == ProjectModuleExecutionItemStatus.Failed),
            orderedItems.Count(static item => item.Status == ProjectModuleExecutionItemStatus.Skipped),
            observedParallelism,
            stopwatch.Elapsed.TotalMilliseconds);
    }

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

public sealed record ProjectModuleExecutionItemResult(
    ProjectModuleExecutionItemStatus Status,
    string? Message = null)
{
    public static ProjectModuleExecutionItemResult Completed { get; } =
        new(ProjectModuleExecutionItemStatus.Completed);

    public static ProjectModuleExecutionItemResult Skipped(string? message = null) =>
        new(ProjectModuleExecutionItemStatus.Skipped, message);

    public static ProjectModuleExecutionItemResult Failed(string? message = null) =>
        new(ProjectModuleExecutionItemStatus.Failed, message);
}

public sealed record ProjectModuleParallelExecutionSnapshot(
    string SchemaVersion,
    IReadOnlyList<ProjectModuleParallelExecutionLayer> Layers,
    int TotalModules,
    int CompletedModules,
    int FailedModules,
    int SkippedModules,
    int MaxObservedParallelism,
    int MaxDegreeOfParallelism,
    double ElapsedMs)
{
    public const string CurrentSchemaVersion = "project-module-parallel-execution-v1";
}

public sealed record ProjectModuleParallelExecutionLayer(
    int Index,
    IReadOnlyList<ProjectModuleParallelExecutionItem> Modules,
    int CompletedModules,
    int FailedModules,
    int SkippedModules,
    int ObservedParallelism,
    double ElapsedMs);

public sealed record ProjectModuleParallelExecutionItem(
    string ModuleKey,
    ProjectModuleExecutionAction Action,
    ProjectModuleExecutionItemStatus Status,
    string? Message)
{
    public static ProjectModuleParallelExecutionItem From(
        ProjectModuleExecutionItem item,
        ProjectModuleExecutionItemResult result) =>
        new(item.ModuleKey, item.Action, result.Status, result.Message);

    public static ProjectModuleParallelExecutionItem FromException(
        ProjectModuleExecutionItem item,
        Exception exception) =>
        new(item.ModuleKey, item.Action, ProjectModuleExecutionItemStatus.Failed, exception.Message);
}

public enum ProjectModuleExecutionItemStatus
{
    Completed,
    Failed,
    Skipped
}
