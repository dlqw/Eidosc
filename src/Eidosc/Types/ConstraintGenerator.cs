using Eidosc.Symbols;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Semantic;

namespace Eidosc.Types;

/// <summary>
/// 约束生成器 - 从表达式生成 Trait 约束
/// </summary>
public sealed class ConstraintGenerator
{
    private readonly SymbolTable _symbolTable;
    private readonly Substitution _substitution;
    private readonly ConstraintSet _constraints = new();

    /// <summary>
    /// 生成的约束集合
    /// </summary>
    public ConstraintSet Constraints => _constraints;

    public ConstraintGenerator(SymbolTable symbolTable, Substitution substitution)
    {
        _symbolTable = symbolTable;
        _substitution = substitution;
    }

    /// <summary>
    /// 清空约束集合
    /// </summary>
    public void Clear()
    {
        _constraints.Clear();
    }

    /// <summary>
    /// 从表达式生成约束
    /// </summary>
    public void Generate(EidosAstNode node)
    {
        GenerateConstraints(node);
    }

    /// <summary>
    /// 从类型参数收集 Trait 约束
    /// </summary>
    public void CollectTypeParamConstraints(
        TypeParam typeParam,
        Type typeVar,
        Func<TypeNode, Type>? traitTypeArgConverter = null)
    {
        foreach (var traitRef in typeParam.TraitConstraints)
        {
            var traitName = traitRef.TraitName;
            SymbolId? traitId = null;
            var traitArgs = traitTypeArgConverter == null
                ? []
                : traitRef.TypeArgs.Select(traitTypeArgConverter).ToList();

            // 首先尝试从 TraitRef 的 SymbolId 获取
            if (traitRef.SymbolId.IsValid)
            {
                traitId = traitRef.SymbolId;
            }
            else
            {
                // 尝试从符号表查找
                traitId = _symbolTable.LookupType(traitName);
            }

            // 如果符号表中没有，检查是否是内置 Trait
            if (traitId == null || !traitId.Value.IsValid)
            {
                // 对于内置 Trait，即使没有注册也添加约束
                var builtinTraitId = BuiltinTraits.GetBuiltinTraitSymbolId(traitName);
                if (builtinTraitId.IsValid)
                {
                    _constraints.AddTrait(typeVar, builtinTraitId, traitName, typeParam.Span, traitArgs);
                    continue;
                }
            }

            if (traitId != null && traitId.Value.IsValid)
            {
                if (_symbolTable.GetSymbol(traitId.Value) is not TraitSymbol)
                {
                    continue;
                }

                _constraints.AddTrait(typeVar, traitId.Value, traitName, typeParam.Span, traitArgs);
            }
        }

        if (typeParam.KindAnnotation != null)
        {
            _constraints.Add(new KindConstraint
            {
                Type = typeVar,
                ExpectedKind = typeParam.GetKindText(),
                Span = typeParam.KindAnnotation.Span
            });
        }
    }

    private void GenerateConstraints(EidosAstNode node)
    {
        switch (node)
        {
            case BinaryExpr binary:
                GenerateBinaryConstraints(binary);
                break;

            case UnaryExpr unary:
                GenerateUnaryConstraints(unary);
                break;

            case CallExpr call:
                GenerateCallConstraints(call);
                break;

            case MethodCallExpr method:
                GenerateMethodCallConstraints(method);
                break;

            case BlockExpr block:
                GenerateBlockConstraints(block);
                break;

            case IfExpr ifExpr:
                GenerateIfConstraints(ifExpr);
                break;

            case IfLetExpr ifLetExpr:
                GenerateIfLetConstraints(ifLetExpr);
                break;

            case WhileLetExpr whileLetExpr:
                GenerateWhileLetConstraints(whileLetExpr);
                break;

            case LoopExpr loopExpr:
                GenerateLoopConstraints(loopExpr);
                break;

            case BreakExpr breakExpr:
                if (breakExpr.Value != null)
                {
                    GenerateConstraints(breakExpr.Value);
                }
                break;

            case MatchExpr match:
                GenerateMatchConstraints(match);
                break;

            case PatternBranch patternBranch:
                GeneratePatternBranchConstraints(patternBranch);
                break;

            case LambdaExpr lambda:
                GenerateLambdaConstraints(lambda);
                break;

            case TupleExpr tuple:
                GenerateTupleConstraints(tuple);
                break;

            case ListExpr list:
                GenerateListConstraints(list);
                break;

            case ListComprehension comp:
                GenerateListComprehensionConstraints(comp);
                break;

            case LetDecl letDecl:
                GenerateLetDeclConstraints(letDecl);
                break;

            case LetQuestionDecl letQuestionDecl:
                GenerateLetQuestionDeclConstraints(letQuestionDecl);
                break;

            case FuncDef func:
                GenerateFuncDefConstraints(func);
                break;

            case PatternGuardExpr patternGuard:
                GeneratePatternGuardConstraints(patternGuard);
                break;

            case SequentialGuardExpr sequentialGuard:
                GenerateSequentialGuardConstraints(sequentialGuard);
                break;

            case DoExpr doExpr:
                GenerateDoExprConstraints(doExpr);
                break;

            case GivenExpr given:
                if (given.Target != null)
                {
                    GenerateConstraints(given.Target);
                }
                break;

            case AssociatedConstExpr associatedConst:
                if (associatedConst.ImplementationValue != null)
                {
                    GenerateConstraints(associatedConst.ImplementationValue);
                }
                break;
        }
    }

    private void GenerateBinaryConstraints(BinaryExpr binary)
    {
        // 先处理子表达式
        if (binary.Left != null)
        {
            GenerateConstraints(binary.Left);
        }
        if (binary.Right != null)
        {
            GenerateConstraints(binary.Right);
        }

        // 根据运算符类型生成约束
        var op = binary.Operator;
        string? traitName = GetOperatorTraitName(op);

        if (traitName != null && binary.Left != null)
        {
            var leftType = GetNodeType(binary.Left);
            if (leftType != null)
            {
                var traitId = LookupTrait(traitName);
                if (traitId != null)
                {
                    _constraints.AddTrait(leftType, traitId.Value, traitName, binary.Span);
                }
            }
        }
    }

    /// <summary>
    /// 根据二元运算符获取对应的 Trait 名称
    /// </summary>
    private static string? GetOperatorTraitName(BinaryOp op)
    {
        return op switch
        {
            // 算术运算符 -> Num
            BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply or
            BinaryOp.Divide or BinaryOp.Modulo => BuiltinTraits.TraitNames.Num,

            // 相等比较 -> Eq
            BinaryOp.Equal or BinaryOp.NotEqual => BuiltinTraits.TraitNames.Eq,

            // 有序比较 -> Ord
            BinaryOp.Less or BinaryOp.Greater or
            BinaryOp.LessEqual or BinaryOp.GreaterEqual => BuiltinTraits.TraitNames.Ord,

            // 其他运算符暂不生成约束
            _ => null
        };
    }

    private void GenerateUnaryConstraints(UnaryExpr unary)
    {
        if (unary.Operand != null)
        {
            GenerateConstraints(unary.Operand);
        }

        // 一元负号可能需要 Num trait
        if (unary.Operator == UnaryOp.Negate && unary.Operand != null)
        {
            var operandType = GetNodeType(unary.Operand);
            if (operandType != null)
            {
                var traitId = LookupTrait(BuiltinTraits.TraitNames.Num);
                if (traitId != null)
                {
                    _constraints.AddTrait(operandType, traitId.Value, BuiltinTraits.TraitNames.Num, unary.Span);
                }
            }
        }
    }

    private void GenerateCallConstraints(CallExpr call)
    {
        if (call.Function != null)
        {
            GenerateConstraints(call.Function);
        }

        foreach (var arg in call.PositionalArgs)
        {
            GenerateConstraints(arg);
        }

        foreach (var arg in call.NamedArgs)
        {
            if (arg.Value != null)
                GenerateConstraints(arg.Value);
        }
    }

    private void GenerateMethodCallConstraints(MethodCallExpr method)
    {
        if (!method.ResolvedAsStaticPath && method.Receiver != null)
        {
            GenerateConstraints(method.Receiver);
        }

        foreach (var arg in method.PositionalArgs)
        {
            GenerateConstraints(arg);
        }

        foreach (var arg in method.NamedArgs)
        {
            if (arg.Value != null)
                GenerateConstraints(arg.Value);
        }

        // Trait 方法调用会生成约束
        // 但这需要在类型推断后才能确定
    }

    private void GenerateBlockConstraints(BlockExpr block)
    {
        foreach (var stmt in block.Statements)
        {
            GenerateConstraints(stmt);
        }
    }

    private void GenerateIfConstraints(IfExpr ifExpr)
    {
        if (ifExpr.Condition != null)
            GenerateConstraints(ifExpr.Condition);
        if (ifExpr.ThenBranch != null)
            GenerateConstraints(ifExpr.ThenBranch);
        if (ifExpr.ElseBranch != null)
            GenerateConstraints(ifExpr.ElseBranch);
    }

    private void GenerateIfLetConstraints(IfLetExpr ifLetExpr)
    {
        if (ifLetExpr.MatchedExpression != null)
        {
            GenerateConstraints(ifLetExpr.MatchedExpression);
        }

        if (ifLetExpr.ThenBranch != null)
        {
            GenerateConstraints(ifLetExpr.ThenBranch);
        }

        if (ifLetExpr.ElseBranch != null)
        {
            GenerateConstraints(ifLetExpr.ElseBranch);
        }
    }

    private void GenerateWhileLetConstraints(WhileLetExpr whileLetExpr)
    {
        if (whileLetExpr.MatchedExpression != null)
        {
            GenerateConstraints(whileLetExpr.MatchedExpression);
        }

        if (whileLetExpr.Body != null)
        {
            GenerateConstraints(whileLetExpr.Body);
        }
    }

    private void GenerateLoopConstraints(LoopExpr loopExpr)
    {
        if (loopExpr.Body != null)
        {
            GenerateConstraints(loopExpr.Body);
        }
    }

    private void GenerateMatchConstraints(MatchExpr match)
    {
        if (match.MatchedExpression != null)
            GenerateConstraints(match.MatchedExpression);

        foreach (var branch in match.Branches)
        {
            GeneratePatternBranchConstraints(branch);
        }
    }

    private void GeneratePatternBranchConstraints(PatternBranch branch)
    {
        if (branch.Guard != null)
            GenerateConstraints(branch.Guard);
        if (branch.Expression != null)
            GenerateConstraints(branch.Expression);
    }

    private void GeneratePatternGuardConstraints(PatternGuardExpr patternGuard)
    {
        if (patternGuard.SourceExpression != null)
        {
            GenerateConstraints(patternGuard.SourceExpression);
        }
    }

    private void GenerateSequentialGuardConstraints(SequentialGuardExpr sequentialGuard)
    {
        foreach (var guard in sequentialGuard.Guards)
        {
            GenerateConstraints(guard);
        }
    }

    private void GenerateDoExprConstraints(DoExpr doExpr)
    {
        foreach (var binding in doExpr.Bindings)
        {
            if (binding.Value != null)
            {
                GenerateConstraints(binding.Value);
            }
        }
    }

    private void GenerateLambdaConstraints(LambdaExpr lambda)
    {
        if (lambda.Body != null)
            GenerateConstraints(lambda.Body);
    }

    private void GenerateTupleConstraints(TupleExpr tuple)
    {
        foreach (var elem in tuple.Elements)
        {
            GenerateConstraints(elem);
        }
    }

    private void GenerateListConstraints(ListExpr list)
    {
        foreach (var elem in list.Elements)
        {
            GenerateConstraints(elem);
        }
    }

    private void GenerateListComprehensionConstraints(ListComprehension comp)
    {
        foreach (var qualifier in comp.Qualifiers)
        {
            if (qualifier.Kind == QualifierKind.Generator && qualifier.GeneratorExpression != null)
            {
                GenerateConstraints(qualifier.GeneratorExpression);
            }
            else if (qualifier.Kind == QualifierKind.Guard && qualifier.GuardExpression != null)
            {
                GenerateConstraints(qualifier.GuardExpression);
            }
        }

        if (comp.Output != null)
        {
            GenerateConstraints(comp.Output);
        }
    }

    private void GenerateLetDeclConstraints(LetDecl letDecl)
    {
        if (letDecl.Value != null)
        {
            GenerateConstraints(letDecl.Value);
        }
    }

    private void GenerateLetQuestionDeclConstraints(LetQuestionDecl letQuestionDecl)
    {
        if (letQuestionDecl.Value != null)
        {
            GenerateConstraints(letQuestionDecl.Value);
        }
    }

    private void GenerateFuncDefConstraints(FuncDef func)
    {
        // 收集类型参数的约束
        // 这在 TypeInferer 中处理，因为需要类型变量映射

        foreach (var branch in func.Body)
        {
            GeneratePatternBranchConstraints(branch);
        }
    }

    /// <summary>
    /// 获取节点的推断类型
    /// </summary>
    private Type? GetNodeType(EidosAstNode node)
    {
        if (node.InferredType is Type type)
        {
            return _substitution.Apply(type);
        }
        return null;
    }

    /// <summary>
    /// 查找 Trait
    /// </summary>
    private SymbolId? LookupTrait(string traitName)
    {
        return _symbolTable.LookupType(traitName);
    }

    /// <summary>
    /// 解析 Trait 引用
    /// </summary>
    private SymbolId? ResolveTraitRef(TraitRef traitRef)
    {
        if (traitRef.SymbolId.IsValid)
        {
            return traitRef.SymbolId;
        }

        // 尝试按名称查找
        return _symbolTable.LookupType(traitRef.TraitName);
    }
}
