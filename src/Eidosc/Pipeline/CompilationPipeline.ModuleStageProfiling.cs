namespace Eidosc.Pipeline;

public sealed partial class CompilationPipeline
{
    private void SetModuleStageExecutionCounters(
        string stageName,
        ProjectModuleArtifactRestoreExecutionSnapshot snapshot,
        bool hasRestorePayload)
    {
        var prefix = $"Build.moduleStage.{stageName}";
        var hasRealStageExecution = snapshot.HasRealTaskExecution && hasRestorePayload;
        if (!hasRealStageExecution && HasRealModuleStageExecution(prefix))
        {
            return;
        }

        SetProfilingCounter($"{prefix}.realTaskExecution", hasRealStageExecution ? 1 : 0);
        SetProfilingCounter($"{prefix}.modules", snapshot.TotalModules);
        SetProfilingCounter($"{prefix}.failedModules", snapshot.FailedModules);
        SetProfilingCounter($"{prefix}.skippedModules", snapshot.SkippedModules);
        SetProfilingCounter($"{prefix}.maxObservedParallelism", hasRealStageExecution ? snapshot.MaxObservedParallelism : 0);
        SetProfilingCounter($"{prefix}.maxDegreeOfParallelism", hasRealStageExecution ? snapshot.MaxDegreeOfParallelism : 0);
        if (!hasRealStageExecution)
        {
            SetProfilingCounter($"{prefix}.restoredModules", 0);
            SetProfilingCounter($"{prefix}.compiledModules", 0);
            SetProfilingCounter($"{prefix}.blockedModules", 0);
            SetProfilingCounter($"{prefix}.readyArtifactModules", 0);
            return;
        }

        SetProfilingCounter($"{prefix}.restoredModules", snapshot.RestoredModules);
        SetProfilingCounter($"{prefix}.compiledModules", snapshot.CompiledModules);
        SetProfilingCounter($"{prefix}.blockedModules", snapshot.BlockedModules);
        SetProfilingCounter($"{prefix}.readyArtifactModules", snapshot.ReadyArtifactModules);
    }

    private bool HasRealModuleStageExecution(string prefix)
    {
        if (!_options.EnableDetailedProfiling)
        {
            return false;
        }

        lock (_profilingCountersLock)
        {
            return _profilingCounters.GetValueOrDefault($"{prefix}.realTaskExecution") == 1;
        }
    }

    private void EnsureModuleStageCounters(string stageName)
    {
        var prefix = $"Build.moduleStage.{stageName}";
        var modules = _moduleGraphSnapshot?.Nodes.Count ?? 0;
        SetProfilingCounterIfMissing($"{prefix}.realTaskExecution", 0);
        SetProfilingCounterIfMissing($"{prefix}.modules", modules);
        SetProfilingCounterIfMissing($"{prefix}.restoredModules", 0);
        SetProfilingCounterIfMissing($"{prefix}.compiledModules", 0);
        SetProfilingCounterIfMissing($"{prefix}.blockedModules", 0);
        SetProfilingCounterIfMissing($"{prefix}.readyArtifactModules", 0);
        SetProfilingCounterIfMissing($"{prefix}.failedModules", 0);
        SetProfilingCounterIfMissing($"{prefix}.skippedModules", 0);
        SetProfilingCounterIfMissing($"{prefix}.maxObservedParallelism", 0);
        SetProfilingCounterIfMissing($"{prefix}.maxDegreeOfParallelism", 0);
    }

    private void SetProfilingCounterIfMissing(string name, long value)
    {
        if (!_options.EnableDetailedProfiling)
        {
            return;
        }

        lock (_profilingCountersLock)
        {
            _profilingCounters.TryAdd(name, value);
        }
    }
}
