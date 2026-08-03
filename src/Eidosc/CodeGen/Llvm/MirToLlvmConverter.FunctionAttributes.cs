using Eidosc.Mir;
using Eidosc.Borrow;

namespace Eidosc.CodeGen.Llvm;

public sealed partial class MirToLlvmConverter
{
    private HashSet<string> AnalyzeMirNounwindEligibleFunctions(MirModule module)
    {
        var functions = module.Functions
            .Where(function =>
                !function.IsExternal &&
                function.BasicBlocks.Count > 0 &&
                !IsIntrinsicDeclaration(function) &&
                (function.IsRuntimeWordAbi || !IsGenericSignature(function)))
            .ToList();
        var llvmNameByFunction = functions.ToDictionary(
            static function => function,
            ResolveFunctionLlvmName);
        var eligible = functions.ToHashSet();

        bool changed;
        do
        {
            changed = false;
            foreach (var function in eligible.ToArray())
            {
                if (MirCallsPotentiallyUnwindingTarget(function, eligible))
                {
                    eligible.Remove(function);
                    changed = true;
                }
            }
        }
        while (changed);

        return eligible
            .Select(function => llvmNameByFunction[function])
            .ToHashSet(StringComparer.Ordinal);
    }

    private bool MirCallsPotentiallyUnwindingTarget(
        MirFunc function,
        IReadOnlySet<MirFunc> nounwindEligibleFunctions)
    {
        foreach (var call in function.BasicBlocks
                     .SelectMany(static block => block.Instructions)
                     .OfType<MirCall>())
        {
            if (call.Function is not MirFunctionRef functionRef)
            {
                return true;
            }

            if (TryGetExternalFfiSymbolName(functionRef.Name, functionRef.SymbolId, out _))
            {
                return true;
            }

            if (TryGetRuntimeFunctionType(functionRef, out _, out _) ||
                _cstructAccessors.ContainsKey(functionRef.Name) ||
                TypeSemantics.IsAdtConstructorCall(functionRef))
            {
                continue;
            }

            if (!TryResolveMirFunction(functionRef, out var callee) ||
                !nounwindEligibleFunctions.Contains(callee))
            {
                return true;
            }
        }

        return false;
    }
}
