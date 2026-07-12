namespace Eidosc.Mir.Optimize;

public sealed class CommonSubexpressionElimination : IMirOptimizationPass, IFunctionOptimizationSummaryConsumer
{
    private FunctionOptimizationSummaryIndex? _functionSummaries;
    private IReadOnlyDictionary<string, int> _parameterCounts =
        new Dictionary<string, int>(StringComparer.Ordinal);

    FunctionOptimizationSummaryIndex IFunctionOptimizationSummaryConsumer.FunctionSummaries
    {
        set => _functionSummaries = value;
    }

    public string Name => "CommonSubexpressionElimination";

    public MirModule Run(MirModule module)
    {
        _parameterCounts = MirCallOptimization.BuildParameterCounts(module);
        List<MirFunc>? functions = null;
        for (var i = 0; i < module.Functions.Count; i++)
        {
            var original = module.Functions[i];
            var optimized = OptimizeFunction(original);
            if (functions != null)
            {
                functions.Add(optimized);
            }
            else if (!ReferenceEquals(original, optimized))
            {
                functions = new List<MirFunc>(module.Functions.Count);
                functions.AddRange(module.Functions.Take(i));
                functions.Add(optimized);
            }
        }

        return functions == null ? module : MirOptimizationCloner.WithFunctions(module, functions);
    }

    private MirFunc OptimizeFunction(MirFunc function)
    {
        if (function.IsExternal)
        {
            return function;
        }

        List<MirBasicBlock>? blocks = null;
        for (var i = 0; i < function.BasicBlocks.Count; i++)
        {
            var original = function.BasicBlocks[i];
            var optimized = OptimizeBlock(original);
            if (blocks != null)
            {
                blocks.Add(optimized);
            }
            else if (!ReferenceEquals(original, optimized))
            {
                blocks = new List<MirBasicBlock>(function.BasicBlocks.Count);
                blocks.AddRange(function.BasicBlocks.Take(i));
                blocks.Add(optimized);
            }
        }

        return blocks == null ? function : MirOptimizationCloner.WithBlocks(function, blocks);
    }

    private MirBasicBlock OptimizeBlock(MirBasicBlock block)
    {
        var available = new Dictionary<string, AvailableCall>(StringComparer.Ordinal);
        List<MirInstruction>? instructions = null;

        for (var i = 0; i < block.Instructions.Count; i++)
        {
            var instruction = block.Instructions[i];
            var definedLocal = MirCallOptimization.GetDefinedLocal(instruction);
            if (definedLocal is { } local)
            {
                Invalidate(available, local);
            }

            MirInstruction replacement = instruction;
            if (instruction is MirCall call &&
                MirCallOptimization.TryGetReusableCall(
                    call,
                    _functionSummaries,
                    _parameterCounts,
                    out var function,
                    out var target) &&
                MirCallOptimization.TryCreateCallKey(
                    function,
                    call.Arguments,
                    out var key,
                    out var dependencies))
            {
                if (available.TryGetValue(key, out var previous))
                {
                    replacement = new MirAssign
                    {
                        Target = target,
                        Source = previous.Result,
                        Span = call.Span
                    };
                }
                else
                {
                    dependencies.Add(target.Local);
                    available[key] = new AvailableCall(target, dependencies);
                }
            }

            if (!ReferenceEquals(replacement, instruction) && instructions == null)
            {
                instructions = new List<MirInstruction>(block.Instructions.Count);
                instructions.AddRange(block.Instructions.Take(i));
            }

            instructions?.Add(replacement);
        }

        return instructions == null
            ? block
            : MirOptimizationCloner.WithInstructions(block, instructions);
    }

    private static void Invalidate(Dictionary<string, AvailableCall> available, LocalId local)
    {
        foreach (var key in available
                     .Where(binding => binding.Value.Dependencies.Contains(local))
                     .Select(static binding => binding.Key)
                     .ToList())
        {
            available.Remove(key);
        }
    }

    private sealed record AvailableCall(MirPlace Result, HashSet<LocalId> Dependencies);
}
