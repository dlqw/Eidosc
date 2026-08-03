namespace Eidosc.CodeGen.Llvm;

internal static class LlvmFunctionAttributeInference
{
    public static void Apply(
        LlvmModule module,
        IReadOnlySet<string>? additionalKnownNounwindTargets = null)
    {
        ApplyScalarParameterContracts(module.Functions);
        ApplyNounwind(module, additionalKnownNounwindTargets);
    }

    private static void ApplyScalarParameterContracts(IEnumerable<LlvmFunction> functions)
    {
        foreach (var function in functions)
        {
            if (function.BasicBlocks.Count == 0 || function.SuppressScalarNoundefParameters)
            {
                continue;
            }

            foreach (var parameter in function.Parameters)
            {
                if (parameter.Type is LlvmIntType or LlvmFloatType &&
                    !parameter.Attributes.Contains(LlvmParameterAttribute.Noundef))
                {
                    parameter.Attributes.Add(LlvmParameterAttribute.Noundef);
                }
            }
        }
    }

    private static void ApplyNounwind(
        LlvmModule module,
        IReadOnlySet<string>? additionalKnownNounwindTargets)
    {
        var definitions = module.Functions
            .Where(static function => function.BasicBlocks.Count > 0)
            .GroupBy(static function => function.Name, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        if (definitions.Count == 0)
        {
            return;
        }

        var knownNounwindDeclarations = module.Declarations
            .Where(static declaration =>
                declaration.Origin is LlvmDeclarationOrigin.RuntimeIntrinsic or LlvmDeclarationOrigin.LlvmIntrinsic)
            .Select(static declaration => declaration.Name)
            .ToHashSet(StringComparer.Ordinal);
        var nounwindFunctions = definitions.Keys.ToHashSet(StringComparer.Ordinal);
        var knownNounwindTargets = additionalKnownNounwindTargets == null
            ? knownNounwindDeclarations
            : knownNounwindDeclarations
                .Concat(additionalKnownNounwindTargets.Where(target => !definitions.ContainsKey(target)))
                .ToHashSet(StringComparer.Ordinal);

        bool changed;
        do
        {
            changed = false;
            foreach (var functionName in nounwindFunctions.ToArray())
            {
                if (CallsPotentiallyUnwindingTarget(
                        definitions[functionName],
                        nounwindFunctions,
                        knownNounwindTargets))
                {
                    nounwindFunctions.Remove(functionName);
                    changed = true;
                }
            }
        }
        while (changed);

        var nounwindAttributeId = new LlvmAttributeGroupRegistry(module).GetOrAdd("nounwind");
        foreach (var function in definitions.Values)
        {
            function.AttributeIds.RemoveAll(id => id == nounwindAttributeId);
        }

        foreach (var functionName in nounwindFunctions)
        {
            var function = definitions[functionName];
            if (!function.AttributeIds.Contains(nounwindAttributeId))
            {
                function.AttributeIds.Add(nounwindAttributeId);
                function.AttributeIds.Sort();
            }
        }
    }

    private static bool CallsPotentiallyUnwindingTarget(
        LlvmFunction function,
        IReadOnlySet<string> nounwindFunctions,
        IReadOnlySet<string> knownNounwindDeclarations)
    {
        foreach (var call in function.BasicBlocks
                     .SelectMany(static block => block.Instructions)
                     .OfType<LlvmCall>())
        {
            if (call.Function is not LlvmGlobal directTarget ||
                (!nounwindFunctions.Contains(directTarget.Name) &&
                 !knownNounwindDeclarations.Contains(directTarget.Name)))
            {
                return true;
            }
        }

        return false;
    }
}
