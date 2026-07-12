using Eidosc.Hir;

namespace Eidosc.Mir;

public sealed class HirParameterEffectAnalysis
{
    private readonly HirModule _module;
    private readonly List<HirFunc> _functions = [];

    public ParameterEffectMap Results { get; } = new();

    public HirParameterEffectAnalysis(HirModule module)
    {
        _module = module;
    }

    public void Analyze()
    {
        CollectFunctions(_module);

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var func in _functions)
            {
                if (func.Body == null) continue;

                var effects = AnalyzeFunction(func);
                var key = func.Name;
                var symId = func.SymbolId.IsValid ? func.SymbolId.Value : 0;

                if (Results.TryGetEffects(key, symId, out var existing) &&
                    existing != null && effects.SequenceEqual(existing))
                {
                    continue;
                }

                Results.Add(key, symId, effects);
                changed = true;
            }
        }
    }

    private void CollectFunctions(HirModule module)
    {
        foreach (var decl in module.Declarations)
        {
            if (decl is HirFunc func)
            {
                _functions.Add(func);
            }
            else if (decl is HirModule nested)
            {
                CollectFunctions(nested);
            }
        }
    }

    private List<ParameterEffect> AnalyzeFunction(HirFunc func)
    {
        var paramSymbols = new Dictionary<SymbolId, int>();
        var paramNames = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < func.Parameters.Count; i++)
        {
            var p = func.Parameters[i];
            if (p.SymbolId.IsValid)
            {
                paramSymbols[p.SymbolId] = i;
            }
            if (!string.IsNullOrEmpty(p.Name))
            {
                paramNames[p.Name] = i;
            }
        }

        if (paramSymbols.Count == 0 && paramNames.Count == 0)
        {
            return Enumerable.Repeat(ParameterEffect.Read, func.Parameters.Count).ToList();
        }

        var consumed = new bool[func.Parameters.Count];

        if (func.Body != null)
        {
            ClassifyConsumingNode(func.Body, paramSymbols, paramNames, consumed);
        }

        var effects = new ParameterEffect[func.Parameters.Count];
        for (var i = 0; i < effects.Length; i++)
        {
            effects[i] = consumed[i] ? ParameterEffect.Consume : ParameterEffect.Read;
        }

        return effects.ToList();
    }

    private void ClassifyNode(
        HirNode node,
        Dictionary<SymbolId, int> paramSymbols,
        Dictionary<string, int> paramNames,
        bool[] consumed)
    {
        switch (node)
        {
            case HirVar:
                return;

            case HirBinOp binOp:
                if (binOp.Operator == Hir.BinaryOp.Concat)
                {
                    ClassifyConsumingNode(binOp.Left, paramSymbols, paramNames, consumed);
                    ClassifyConsumingNode(binOp.Right, paramSymbols, paramNames, consumed);
                }
                else
                {
                    ClassifyNode(binOp.Left, paramSymbols, paramNames, consumed);
                    ClassifyNode(binOp.Right, paramSymbols, paramNames, consumed);
                }
                return;

            case HirCall call:
                ClassifyCallArguments(call, paramSymbols, paramNames, consumed);
                return;

            case HirReturn ret:
                if (ret.Value != null)
                {
                    ClassifyConsumingNode(ret.Value, paramSymbols, paramNames, consumed);
                }
                return;

            case HirIf ifExpr:
                ClassifyNode(ifExpr.Condition, paramSymbols, paramNames, consumed);
                ClassifyNode(ifExpr.ThenBranch, paramSymbols, paramNames, consumed);
                if (ifExpr.ElseBranch != null)
                {
                    ClassifyNode(ifExpr.ElseBranch, paramSymbols, paramNames, consumed);
                }
                return;

            case HirMatch match:
                ClassifyNode(match.Scrutinee, paramSymbols, paramNames, consumed);
                foreach (var branch in match.Branches)
                {
                    ClassifyNode(branch.Body, paramSymbols, paramNames, consumed);
                }
                return;

            case HirBlock block:
                foreach (var stmt in block.Statements)
                {
                    if (stmt is HirAssignStatement assign)
                    {
                        ClassifyConsumingNode(assign.Value, paramSymbols, paramNames, consumed);
                    }
                    else if (stmt is HirDeclStatement declStmt && declStmt.Declaration is HirVarDecl varDecl)
                    {
                        ClassifyConsumingNode(varDecl.Initializer, paramSymbols, paramNames, consumed);
                    }
                }
                if (block.Result != null)
                {
                    ClassifyConsumingNode(block.Result, paramSymbols, paramNames, consumed);
                }
                return;

            case HirLambda lambda:
                ClassifyNode(lambda.Body, paramSymbols, paramNames, consumed);
                return;

            case HirLoop loop:
                ClassifyNode(loop.Body, paramSymbols, paramNames, consumed);
                return;

            case HirUnaryOp unaryOp:
                ClassifyNode(unaryOp.Operand, paramSymbols, paramNames, consumed);
                return;

            case HirFieldAccess fieldAccess:
                ClassifyNode(fieldAccess.Target, paramSymbols, paramNames, consumed);
                return;

            case HirIndexAccess indexAccess:
                ClassifyNode(indexAccess.Target, paramSymbols, paramNames, consumed);
                ClassifyNode(indexAccess.Index, paramSymbols, paramNames, consumed);
                return;

            case HirPatternGuard guard:
                ClassifyNode(guard.SourceExpression, paramSymbols, paramNames, consumed);
                return;

            case HirSequentialGuard seqGuard:
                foreach (var g in seqGuard.Guards)
                {
                    ClassifyNode(g, paramSymbols, paramNames, consumed);
                }
                return;

            case HirBreak brk:
                if (brk.Value != null)
                {
                    ClassifyConsumingNode(brk.Value, paramSymbols, paramNames, consumed);
                }
                return;

            case HirLiteral:
                return;
        }
    }

    private void ClassifyConsumingNode(
        HirNode node,
        Dictionary<SymbolId, int> paramSymbols,
        Dictionary<string, int> paramNames,
        bool[] consumed)
    {
        // In a consuming position (return, store, block result, etc.),
        // a plain parameter variable reference means the parameter is consumed.
        if (node is HirVar v)
        {
            MarkConsumed(v.Name, v.SymbolId, paramSymbols, paramNames, consumed);
            return;
        }

        // For non-variable expressions in consuming position, just recursively classify
        ClassifyNode(node, paramSymbols, paramNames, consumed);
    }

    private void ClassifyCallArguments(
        HirCall call,
        Dictionary<SymbolId, int> paramSymbols,
        Dictionary<string, int> paramNames,
        bool[] consumed)
    {
        // Classify the function expression itself (not relevant for params, but recurse for nested calls)
        ClassifyNode(call.Function, paramSymbols, paramNames, consumed);

        // Determine callee effects
        string? calleeName = null;
        int calleeSymbolId = 0;

        if (call.Function is HirVar funcVar)
        {
            calleeName = funcVar.Name;
            calleeSymbolId = funcVar.SymbolId.IsValid ? funcVar.SymbolId.Value : 0;
        }

        for (var i = 0; i < call.Arguments.Count; i++)
        {
            var arg = call.Arguments[i];
            var calleeEffect = GetCalleeArgumentEffect(calleeName, calleeSymbolId, i);

            if (calleeEffect == ParameterEffect.Consume)
            {
                ClassifyConsumingNode(arg, paramSymbols, paramNames, consumed);
            }
            else
            {
                // Read: the argument is only read, but we still need to recurse
                // for any nested expressions that might consume parameters
                ClassifyNode(arg, paramSymbols, paramNames, consumed);
            }
        }
    }

    private ParameterEffect GetCalleeArgumentEffect(string? calleeName, int calleeSymbolId, int argIndex)
    {
        if (Results.TryGetEffects(calleeName, calleeSymbolId, out var effects) && effects != null)
        {
            if (argIndex < effects.Count)
            {
                return effects[argIndex];
            }
        }

        // Unknown or not-yet-analyzed function — default to Read.
        // The fixed-point iteration will refine this in subsequent rounds.
        return ParameterEffect.Read;
    }

    private static void MarkConsumed(
        string name,
        SymbolId symbolId,
        Dictionary<SymbolId, int> paramSymbols,
        Dictionary<string, int> paramNames,
        bool[] consumed)
    {
        if (symbolId.IsValid && paramSymbols.TryGetValue(symbolId, out var idxSym))
        {
            consumed[idxSym] = true;
            return;
        }

        if (!string.IsNullOrEmpty(name) && paramNames.TryGetValue(name, out var idxName))
        {
            consumed[idxName] = true;
        }
    }
}
