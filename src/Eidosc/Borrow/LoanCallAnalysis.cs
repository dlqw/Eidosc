using Eidosc.Symbols;
using Eidosc.Mir;
using Eidosc.Semantic;
using System.Runtime.CompilerServices;

namespace Eidosc.Borrow;

/// <summary>
/// 调用返回的借用绑定
/// </summary>
internal sealed class ReturnedCallBorrowBinding
{
    public LocalId TargetLocal { get; init; }
    public LocalId Borrowee { get; init; }
    public BorrowTarget BorrowTarget { get; init; }
    public bool IsMutable { get; init; }
    public LifetimeId Lifetime { get; init; }
    public int ArgumentIndex { get; init; }
    public int CalleeParamIndex { get; init; }
}

/// <summary>
/// 调用借用分析辅助方法
/// </summary>
internal static class LoanCallAnalysis
{
    private static readonly ConditionalWeakTable<SymbolTable, IReadOnlyDictionary<string, SymbolId>> FunctionSymbolLookupCache = new();

    public static LoanSignature? TryResolveCalleeSignature(
        MirCall call,
        LoanSignatureCache cache,
        SymbolTable symbolTable)
    {
        if (call.Function is MirFunctionRef funcRef)
        {
            var symbol = funcRef.SymbolId.IsValid
                ? funcRef.SymbolId
                : FindFunctionSymbolByName(funcRef.Name, symbolTable);

            if (symbol.IsValid && cache.HasSignature(symbol))
            {
                return cache.GetSignature(symbol);
            }
        }

        if (call.Function is MirConstant funcConst &&
            funcConst.Value is MirConstantValue.StringValue strVal)
        {
            var symbol = FindFunctionSymbolByName(strVal.Value, symbolTable);
            if (symbol.IsValid && cache.HasSignature(symbol))
            {
                return cache.GetSignature(symbol);
            }
        }

        return null;
    }

    public static List<ReturnedCallBorrowBinding> ResolveReturnedBorrows(
        MirCall call,
        LoanSignature signature,
        Func<LocalId, IReadOnlyList<BorrowTarget>> resolveBorrowTargets)
    {
        if (!signature.ReturnsBorrow() ||
            call.Target is not MirPlace { Kind: PlaceKind.Local, Local: var targetLocal })
        {
            return [];
        }

        var bindings = new List<ReturnedCallBorrowBinding>();
        var seen = new HashSet<string>();

        foreach (var calleeParamIndex in signature.ReturnConstraint.BoundToParams)
        {
            if (calleeParamIndex < 0 || calleeParamIndex >= call.Arguments.Count)
            {
                continue;
            }

            if (call.Arguments[calleeParamIndex] is not MirPlace { Kind: PlaceKind.Local, Local: var argumentLocal })
            {
                continue;
            }

            foreach (var borrowTarget in resolveBorrowTargets(argumentLocal))
            {
                if (!borrowTarget.IsValid)
                {
                    continue;
                }

                var key = $"{targetLocal.Value}:{borrowTarget.StableKey}:{calleeParamIndex}:{signature.ReturnConstraint.IsMutable}";
                if (!seen.Add(key))
                {
                    continue;
                }

                bindings.Add(new ReturnedCallBorrowBinding
                {
                    TargetLocal = targetLocal,
                    Borrowee = borrowTarget.BaseLocal,
                    BorrowTarget = borrowTarget,
                    IsMutable = signature.ReturnConstraint.IsMutable,
                    Lifetime = signature.ReturnConstraint.Lifetime,
                    ArgumentIndex = calleeParamIndex,
                    CalleeParamIndex = calleeParamIndex
                });
            }
        }

        return bindings;
    }

    public static List<(ReturnedCallBorrowBinding Binding, List<TBorrow> Sources)> CollectReturnedBorrowSources<TBorrow>(
        MirCall call,
        LoanSignature signature,
        Func<LocalId, IReadOnlyList<BorrowTarget>> resolveBorrowTargets,
        Func<LocalId, List<TBorrow>> getBorrowsByBorrower,
        Func<TBorrow, BorrowTarget> getBorrowTarget)
        where TBorrow : class
    {
        var returnedBorrowSources = new List<(ReturnedCallBorrowBinding, List<TBorrow>)>();

        foreach (var binding in ResolveReturnedBorrows(call, signature, resolveBorrowTargets))
        {
            var sourceBorrows = call.Arguments[binding.ArgumentIndex] is MirPlace { Kind: PlaceKind.Local, Local: var argumentLocal }
                ? getBorrowsByBorrower(argumentLocal)
                    .Where(borrow => getBorrowTarget(borrow).Equals(binding.BorrowTarget))
                    .ToList()
                : [];

            returnedBorrowSources.Add((binding, sourceBorrows));
        }

        return returnedBorrowSources;
    }

    public static void ForEachOwnedLocalArgument(
        MirCall call,
        LoanSignature signature,
        Action<int, LocalId> onOwnedArgument)
    {
        for (int i = 0; i < call.Arguments.Count && i < signature.ParamRequirements.Count; i++)
        {
            if (signature.ParamRequirements[i].Mode != ParamBorrowMode.Own ||
                call.Arguments[i] is not MirPlace { Kind: PlaceKind.Local, Local: var localId })
            {
                continue;
            }

            onOwnedArgument(i, localId);
        }
    }

    public static void ApplyReturnedBorrowSources<TBorrow>(
        IEnumerable<(ReturnedCallBorrowBinding Binding, List<TBorrow> Sources)> returnedBorrowSources,
        Action<ReturnedCallBorrowBinding> onNoSource,
        Action<ReturnedCallBorrowBinding, TBorrow> onSource)
        where TBorrow : class
    {
        foreach (var (binding, sourceBorrows) in returnedBorrowSources)
        {
            if (sourceBorrows.Count == 0)
            {
                onNoSource(binding);
                continue;
            }

            foreach (var sourceBorrow in sourceBorrows)
            {
                onSource(binding, sourceBorrow);
            }
        }
    }

    private static SymbolId FindFunctionSymbolByName(string name, SymbolTable symbolTable)
    {
        var lookup = FunctionSymbolLookupCache.GetValue(symbolTable, static table =>
        {
            var symbolsByName = new Dictionary<string, SymbolId>(StringComparer.Ordinal);
            foreach (var (id, symbol) in table.Symbols)
            {
                if (symbol is FuncSymbol func && !symbolsByName.ContainsKey(func.Name))
                {
                    symbolsByName[func.Name] = id;
                }
            }

            return symbolsByName;
        });

        return lookup.TryGetValue(name, out var symbolId)
            ? symbolId
            : SymbolId.None;
    }
}
