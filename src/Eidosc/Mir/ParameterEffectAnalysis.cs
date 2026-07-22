namespace Eidosc.Mir;

/// <summary>
/// Describes how a function parameter is used within its body.
/// Read = parameter is only used in read-only positions (MirCopy source, MirLoad source, comparisons)
/// Consume = parameter is used in at least one consuming position (MirMove source, MirBinOp Concat, return value)
/// </summary>
public enum ParameterEffect
{
    Read,
    Consume
}

/// <summary>
/// Maps function names to their parameter effect lists.
/// Index i in the list corresponds to the i-th parameter (0-based).
/// </summary>
public sealed class ParameterEffectMap
{
    private readonly Dictionary<string, List<ParameterEffect>> _effectsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<int, List<ParameterEffect>> _effectsBySymbolId = new();

    public IReadOnlyDictionary<string, IReadOnlyList<ParameterEffect>> EffectsByName =>
        _effectsByName.ToDictionary(
            static entry => entry.Key,
            static entry => (IReadOnlyList<ParameterEffect>)entry.Value.ToArray(),
            StringComparer.Ordinal);

    public IReadOnlyDictionary<int, IReadOnlyList<ParameterEffect>> EffectsBySymbolId =>
        _effectsBySymbolId.ToDictionary(
            static entry => entry.Key,
            static entry => (IReadOnlyList<ParameterEffect>)entry.Value.ToArray());

    public void Add(string functionName, int symbolId, List<ParameterEffect> effects)
    {
        if (!string.IsNullOrEmpty(functionName))
        {
            _effectsByName[functionName] = effects;
        }

        if (symbolId > 0)
        {
            _effectsBySymbolId[symbolId] = effects;
        }
    }

    public bool TryGetEffects(string? functionName, int symbolId, out List<ParameterEffect>? effects)
    {
        if (symbolId > 0 && _effectsBySymbolId.TryGetValue(symbolId, out effects))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(functionName) && _effectsByName.TryGetValue(functionName, out effects))
        {
            return true;
        }

        // Try matching specialized function names by stripping the specialization suffix.
        // Specialized names have format: OriginalName__spec_HASH
        if (!string.IsNullOrEmpty(functionName))
        {
            var specMarker = WellKnownStrings.InternalNames.SpecializationMarker;
            var specIdx = functionName.IndexOf(specMarker, StringComparison.Ordinal);
            if (specIdx > 0)
            {
                var baseName = functionName[..specIdx];
                if (_effectsByName.TryGetValue(baseName, out effects))
                {
                    return true;
                }
            }
        }

        effects = null;
        return false;
    }

    public bool IsReadOnlyArgument(string? functionName, int symbolId, int argumentIndex)
    {
        if (!TryGetEffects(functionName, symbolId, out var effects) || effects == null)
        {
            return false;
        }

        return argumentIndex < effects.Count && effects[argumentIndex] == ParameterEffect.Read;
    }
}

/// <summary>
/// Analyzes each function in a MIR module to determine whether each parameter
/// is consumed (moved into another value) or merely read (used in copy positions).
///
/// A parameter is "Read" if all of its uses are through MirCopy (not MirMove),
/// read-only builtins, or comparison operations. Otherwise it is "Consume".
///
/// This replaces the hardcoded whitelist approach — any function whose parameters
/// are only used in read-only ways will automatically get MirCopy semantics.
/// </summary>
public sealed class ParameterEffectAnalysis
{
    private readonly MirModule _module;

    public ParameterEffectMap Results { get; } = new();

    public ParameterEffectAnalysis(MirModule module)
    {
        _module = module;
    }

    public void Analyze()
    {
        foreach (var function in _module.Functions)
        {
            AnalyzeFunction(function);
        }
    }

    private void AnalyzeFunction(MirFunc function)
    {
        var parameterIndices = new Dictionary<LocalId, int>();
        var paramIndex = 0;
        foreach (var local in function.Locals)
        {
            if (local.IsParameter)
            {
                parameterIndices[local.Id] = paramIndex;
                paramIndex++;
            }
        }

        if (parameterIndices.Count == 0)
        {
            return;
        }

        var effects = new ParameterEffect[parameterIndices.Count];
        Array.Fill(effects, ParameterEffect.Read);

        foreach (var block in function.BasicBlocks)
        {
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                AnalyzeInstruction(block.Instructions[i], parameterIndices, effects);
            }

            if (block.Terminator != null)
            {
                AnalyzeTerminator(block.Terminator, parameterIndices, effects);
            }
        }

        var effectList = effects.ToList();
        Results.Add(
            function.Name,
            function.SymbolId.IsValid ? function.SymbolId.Value : 0,
            effectList);
    }

    private void AnalyzeInstruction(
        MirInstruction instruction,
        Dictionary<LocalId, int> parameterIndices,
        ParameterEffect[] effects)
    {
        switch (instruction)
        {
            // MirMove consumes its source
            case MirMove move:
                MarkIfParameter(move.Source, parameterIndices, effects, ParameterEffect.Consume);
                break;

            // MirCopy reads its source — parameter stays Read
            case MirCopy:
                break;

            // MirLoad reads from source — parameter stays Read
            case MirLoad:
                break;

            // MirBinOp Concat consumes both operands; other ops are read-only
            case MirBinOp binOp when binOp.Operator == BinaryOp.Concat:
                MarkIfParameter(binOp.Left, parameterIndices, effects, ParameterEffect.Consume);
                MarkIfParameter(binOp.Right, parameterIndices, effects, ParameterEffect.Consume);
                break;

            case MirBinOp:
                break;

            // MirStore consumes the stored value
            case MirStore store:
                MarkIfParameter(store.Value, parameterIndices, effects, ParameterEffect.Consume);
                break;

            // MirDrop consumes the value
            case MirDrop drop:
                MarkIfParameter(drop.Value, parameterIndices, effects, ParameterEffect.Consume);
                break;

            // MirCall — arguments are temps prepared by MirCopy/MirMove before the call.
            // The MirCall itself doesn't directly consume parameters. The preparation
            // instructions (MirMove) are what we track above.
            // However, we need to check if the function operand itself is a parameter
            // (passing a function value).
            case MirCall call:
                MarkIfParameter(call.Function, parameterIndices, effects, ParameterEffect.Consume);
                break;

            case MirAssign assign:
                MarkIfParameter(assign.Source, parameterIndices, effects, ParameterEffect.Consume);
                break;

            case MirCaseInject injection:
                MarkIfParameter(injection.Operand, parameterIndices, effects, ParameterEffect.Consume);
                break;

            default:
                break;
        }
    }

    private void AnalyzeTerminator(
        MirTerminator terminator,
        Dictionary<LocalId, int> parameterIndices,
        ParameterEffect[] effects)
    {
        if (terminator is MirReturn ret && ret.Value != null)
        {
            MarkIfParameter(ret.Value, parameterIndices, effects, ParameterEffect.Consume);
        }
    }

    private static void MarkIfParameter(
        MirOperand? operand,
        Dictionary<LocalId, int> parameterIndices,
        ParameterEffect[] effects,
        ParameterEffect effect)
    {
        if (operand is MirPlace { Kind: PlaceKind.Local, Local: var localId } &&
            parameterIndices.TryGetValue(localId, out var paramIdx))
        {
            effects[paramIdx] = effect;
        }
    }

    /// <summary>
    /// Post-processing pass: converts MirMove to MirCopy for parameters that
    /// are determined to be read-only by the analysis.
    /// </summary>
    public static void ApplyReadOnlyParameterFix(MirModule module, ParameterEffectMap effectMap)
    {
        foreach (var function in module.Functions)
        {
            if (!effectMap.TryGetEffects(function.Name, function.SymbolId.IsValid ? function.SymbolId.Value : 0, out var effects) ||
                effects == null)
            {
                continue;
            }

            var parameterIndices = new Dictionary<LocalId, int>();
            var paramIndex = 0;
            foreach (var local in function.Locals)
            {
                if (local.IsParameter)
                {
                    parameterIndices[local.Id] = paramIndex;
                    paramIndex++;
                }
            }

            foreach (var block in function.BasicBlocks)
            {
                for (var i = 0; i < block.Instructions.Count; i++)
                {
                    if (block.Instructions[i] is MirMove move &&
                        move.Source is { Kind: PlaceKind.Local, Local: var srcLocal } &&
                        parameterIndices.TryGetValue(srcLocal, out var pIdx) &&
                        pIdx < effects.Count &&
                        effects[pIdx] == ParameterEffect.Read)
                    {
                        block.Instructions[i] = new MirCopy
                        {
                            Target = move.Target,
                            Source = move.Source,
                            Span = move.Span
                        };
                    }
                }
            }
        }
    }

    /// <summary>
    /// Caller-side fixup: converts argument-preparation MirMove to MirCopy
    /// when the callee's corresponding parameter is determined to be Read-only.
    /// Must run BEFORE DropInsertionPass so drops are correctly inserted.
    /// </summary>
    public static void ApplyCallSiteEffectFixup(MirModule module, ParameterEffectMap effectMap)
    {
        foreach (var function in module.Functions)
        {
            foreach (var block in function.BasicBlocks)
            {
                for (var i = 0; i < block.Instructions.Count; i++)
                {
                    if (block.Instructions[i] is not MirCall call) continue;

                    var calleeName = (call.Function as MirFunctionRef)?.Name;
                    var calleeSymbolId = (call.Function as MirFunctionRef)?.SymbolId ?? SymbolId.None;

                    if (!effectMap.TryGetEffects(calleeName, calleeSymbolId.IsValid ? calleeSymbolId.Value : 0, out var effects) ||
                        effects == null)
                    {
                        continue;
                    }

                    for (var argIdx = 0; argIdx < call.Arguments.Count; argIdx++)
                    {
                        if (argIdx >= effects.Count) break;
                        if (effects[argIdx] != ParameterEffect.Read) continue;

                        var arg = call.Arguments[argIdx];
                        if (arg is not MirPlace { Kind: PlaceKind.Local, Local: var argLocal }) continue;

                        for (var j = i - 1; j >= 0; j--)
                        {
                            if (block.Instructions[j] is MirMove move &&
                                move.Target is MirPlace { Kind: PlaceKind.Local, Local: var targetLocal } &&
                                targetLocal.Equals(argLocal))
                            {
                                block.Instructions[j] = new MirCopy
                                {
                                    Target = move.Target,
                                    Source = move.Source,
                                    Span = move.Span
                                };
                                break;
                            }
                        }
                    }
                }
            }
        }
    }
}
