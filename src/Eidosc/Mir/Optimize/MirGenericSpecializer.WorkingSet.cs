namespace Eidosc.Mir.Optimize;

public sealed partial class MirGenericSpecializer
{
    private void RewriteTraitDispatchOnly(List<MirFunc> workingFunctions)
    {
        var queue = CreateInitialRewriteQueue(workingFunctions);
        while (queue.Count > 0)
        {
            _stats.RewriteQueueMaxDepth = Math.Max(_stats.RewriteQueueMaxDepth, queue.Count);
            _stats.RewriteQueueDequeues++;
            var item = queue.Dequeue();
            var current = EnsureClonedWorkingFunction(workingFunctions, item.FunctionIndex);
            RewriteFunctionCalls(current, item.Summary, workingFunctions, queue);
        }
    }

    private void RewriteLateTraitDispatch(List<MirFunc> workingFunctions)
    {
        var queue = new Queue<RewriteQueueItem>();
        for (var i = 0; i < workingFunctions.Count; i++)
        {
            if (!AnyFunctionRef(workingFunctions[i], FunctionRefRequiresLateTraitDispatch))
            {
                continue;
            }

            queue.Enqueue(new RewriteQueueItem(
                i,
                new FunctionRewriteSummary(
                    NeedsRewrite: true,
                    NeedsFullFunctionScan: true,
                    CandidateBlockCount: 0,
                    CandidateInstructionCount: 0,
                    CanUseCandidateBlockScan: false,
                    CandidateBlockIndices: [],
                    CandidateInstructionSites: [])));
        }

        while (queue.Count > 0)
        {
            _stats.RewriteQueueMaxDepth = Math.Max(_stats.RewriteQueueMaxDepth, queue.Count);
            _stats.RewriteQueueDequeues++;
            var item = queue.Dequeue();
            var current = EnsureClonedWorkingFunction(workingFunctions, item.FunctionIndex);
            RewriteFunctionCalls(current, item.Summary, workingFunctions, queue);
        }
    }

    private Queue<RewriteQueueItem> CreateInitialRewriteQueue(IReadOnlyList<MirFunc> functions)
    {
        var queue = new Queue<RewriteQueueItem>(functions.Count);
        for (var i = 0; i < functions.Count; i++)
        {
            var function = functions[i];
            if (ShouldSkipInitialOriginalTemplateRewrite(function, i))
            {
                continue;
            }

            if (function.TraitInvokeHelper != TraitInvokeHelperKind.None)
            {
                queue.Enqueue(new RewriteQueueItem(
                    i,
                    new FunctionRewriteSummary(
                        NeedsRewrite: true,
                        NeedsFullFunctionScan: true,
                        CandidateBlockCount: 0,
                        CandidateInstructionCount: 0,
                        CanUseCandidateBlockScan: false,
                        CandidateBlockIndices: [],
                        CandidateInstructionSites: [])));
                _stats.DirtyRewriteQueueEntries++;
                continue;
            }

            if (FunctionHasReferenceRequiringSpecialization(function))
            {
                queue.Enqueue(new RewriteQueueItem(
                    i,
                    new FunctionRewriteSummary(
                        NeedsRewrite: true,
                        NeedsFullFunctionScan: false,
                        CandidateBlockCount: 0,
                        CandidateInstructionCount: 0,
                        CanUseCandidateBlockScan: false,
                        CandidateBlockIndices: [],
                        CandidateInstructionSites: [])));
                _stats.DirtyRewriteQueueEntries++;
            }
        }

        _stats.InitialRewriteQueueEntries += queue.Count;
        _stats.RewriteQueueMaxDepth = Math.Max(_stats.RewriteQueueMaxDepth, queue.Count);
        return queue;
    }

    private bool ShouldSkipInitialOriginalTemplateRewrite(MirFunc function, int functionIndex)
    {
        return IsGenericSignature(function) &&
               _templateKeyByFunctionIndex.ContainsKey(functionIndex) &&
               !IsExecutableEntryFunction(function);
    }

    private MirFunc EnsureClonedWorkingFunction(List<MirFunc> workingFunctions, int functionIndex)
    {
        if (!_clonedWorkingFunctionIndices.Add(functionIndex))
        {
            return workingFunctions[functionIndex];
        }

        var clonedFunction = CloneFunction(workingFunctions[functionIndex]);
        workingFunctions[functionIndex] = clonedFunction;
        _stats.ClonedWorkingFunctions++;

        if (_templateKeyByFunctionIndex.TryGetValue(functionIndex, out var templateKey) &&
            _templateRegistry.ByKeyDict.TryGetValue(templateKey, out var template))
        {
            _templateRegistry.ByKeyDict[templateKey] = template with
            {
                OriginalWorkingFunction = clonedFunction
            };
        }

        return clonedFunction;
    }
}
