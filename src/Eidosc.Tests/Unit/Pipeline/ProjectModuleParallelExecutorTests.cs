using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Pipeline;

public sealed class ProjectModuleParallelExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_RunsSameLayerModulesInParallelAndPreservesLayerBarrier()
    {
        var plan = new ProjectModuleExecutionPlan(
            [
                new ProjectModuleExecutionLayer(
                    0,
                    [
                        CreateItem("A"),
                        CreateItem("B")
                    ],
                    CompileCount: 2,
                    RestoreCount: 0,
                    ReadyArtifactCount: 0),
                new ProjectModuleExecutionLayer(
                    1,
                    [CreateItem("C", dependencies: ["A", "B"])],
                    CompileCount: 1,
                    RestoreCount: 0,
                    ReadyArtifactCount: 0)
            ],
            TotalModules: 3,
            CompileModules: 3,
            RestoreModules: 0,
            ReadyArtifactModules: 0,
            MaxCompileParallelWidth: 2,
            MaxRestoreParallelWidth: 0,
            MaxReadyArtifactParallelWidth: 0);
        var executor = new ProjectModuleParallelExecutor(maxDegreeOfParallelism: 2);
        var layerZeroRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var layerZeroStarted = 0;
        var completed = new List<string>();
        var lockObj = new object();

        var task = executor.ExecuteAsync(
            plan,
            async (item, _) =>
            {
                if (item.ModuleKey is "A" or "B")
                {
                    Interlocked.Increment(ref layerZeroStarted);
                    await layerZeroRelease.Task.ConfigureAwait(false);
                }
                else
                {
                    Assert.Equal(2, Volatile.Read(ref layerZeroStarted));
                    lock (lockObj)
                    {
                        Assert.Contains("A", completed);
                        Assert.Contains("B", completed);
                    }
                }

                lock (lockObj)
                {
                    completed.Add(item.ModuleKey);
                }

                return ProjectModuleExecutionItemResult.Completed;
            });

        SpinWait.SpinUntil(() => Volatile.Read(ref layerZeroStarted) == 2, TimeSpan.FromSeconds(5));
        Assert.Equal(2, Volatile.Read(ref layerZeroStarted));
        layerZeroRelease.SetResult();
        var snapshot = await task;

        Assert.Equal(3, snapshot.CompletedModules);
        Assert.Equal(0, snapshot.FailedModules);
        Assert.Equal(2, snapshot.MaxObservedParallelism);
        Assert.Equal(2, snapshot.Layers[0].ObservedParallelism);
        Assert.Equal(1, snapshot.Layers[1].ObservedParallelism);
        Assert.Equal(["A", "B"], snapshot.Layers[0].Modules.Select(static item => item.ModuleKey));
        Assert.Equal(["C"], snapshot.Layers[1].Modules.Select(static item => item.ModuleKey));
    }

    [Fact]
    public async Task ExecuteAsync_RespectsMaxDegreeOfParallelism()
    {
        var plan = new ProjectModuleExecutionPlan(
            [
                new ProjectModuleExecutionLayer(
                    0,
                    [
                        CreateItem("A"),
                        CreateItem("B"),
                        CreateItem("C")
                    ],
                    CompileCount: 3,
                    RestoreCount: 0,
                    ReadyArtifactCount: 0)
            ],
            TotalModules: 3,
            CompileModules: 3,
            RestoreModules: 0,
            ReadyArtifactModules: 0,
            MaxCompileParallelWidth: 3,
            MaxRestoreParallelWidth: 0,
            MaxReadyArtifactParallelWidth: 0);
        var executor = new ProjectModuleParallelExecutor(maxDegreeOfParallelism: 2);
        var running = 0;
        var observed = 0;

        var snapshot = await executor.ExecuteAsync(
            plan,
            async (_, _) =>
            {
                var current = Interlocked.Increment(ref running);
                UpdateMax(ref observed, current);
                await Task.Delay(25);
                Interlocked.Decrement(ref running);
                return ProjectModuleExecutionItemResult.Completed;
            });

        Assert.Equal(3, snapshot.CompletedModules);
        Assert.Equal(2, snapshot.MaxObservedParallelism);
        Assert.Equal(2, observed);
    }

    [Fact]
    public async Task ExecuteAsync_StopsBeforeNextLayerWhenLayerFails()
    {
        var plan = new ProjectModuleExecutionPlan(
            [
                new ProjectModuleExecutionLayer(
                    0,
                    [CreateItem("A")],
                    CompileCount: 1,
                    RestoreCount: 0,
                    ReadyArtifactCount: 0),
                new ProjectModuleExecutionLayer(
                    1,
                    [CreateItem("B", dependencies: ["A"])],
                    CompileCount: 1,
                    RestoreCount: 0,
                    ReadyArtifactCount: 0)
            ],
            TotalModules: 2,
            CompileModules: 2,
            RestoreModules: 0,
            ReadyArtifactModules: 0,
            MaxCompileParallelWidth: 1,
            MaxRestoreParallelWidth: 0,
            MaxReadyArtifactParallelWidth: 0);
        var executor = new ProjectModuleParallelExecutor(maxDegreeOfParallelism: 2);
        var visited = new List<string>();

        var snapshot = await executor.ExecuteAsync(
            plan,
            (item, _) =>
            {
                visited.Add(item.ModuleKey);
                return ValueTask.FromResult(item.ModuleKey == "A"
                    ? ProjectModuleExecutionItemResult.Failed("boom")
                    : ProjectModuleExecutionItemResult.Completed);
            });

        Assert.Equal(["A"], visited);
        Assert.Equal(0, snapshot.CompletedModules);
        Assert.Equal(1, snapshot.FailedModules);
        Assert.Single(snapshot.Layers);
        Assert.Equal(ProjectModuleExecutionItemStatus.Failed, Assert.Single(snapshot.Layers[0].Modules).Status);
        Assert.Equal("boom", Assert.Single(snapshot.Layers[0].Modules).Message);
    }

    [Fact]
    public async Task ExecuteAsync_OrdersLayerResultsByModuleKey()
    {
        var plan = new ProjectModuleExecutionPlan(
            [
                new ProjectModuleExecutionLayer(
                    0,
                    [
                        CreateItem("C"),
                        CreateItem("A"),
                        CreateItem("B")
                    ],
                    CompileCount: 3,
                    RestoreCount: 0,
                    ReadyArtifactCount: 0)
            ],
            TotalModules: 3,
            CompileModules: 3,
            RestoreModules: 0,
            ReadyArtifactModules: 0,
            MaxCompileParallelWidth: 3,
            MaxRestoreParallelWidth: 0,
            MaxReadyArtifactParallelWidth: 0);
        var executor = new ProjectModuleParallelExecutor(maxDegreeOfParallelism: 3);

        var snapshot = await executor.ExecuteAsync(
            plan,
            async (item, _) =>
            {
                await Task.Delay(item.ModuleKey == "A" ? 30 : 1);
                return ProjectModuleExecutionItemResult.Completed;
            });

        Assert.Equal(["A", "B", "C"], snapshot.Layers[0].Modules.Select(static item => item.ModuleKey));
    }

    private static ProjectModuleExecutionItem CreateItem(
        string moduleKey,
        IReadOnlyList<string>? dependencies = null) =>
        new(moduleKey, ProjectModuleExecutionAction.Compile, [$"{moduleKey}.eidos"], dependencies ?? [], []);

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
