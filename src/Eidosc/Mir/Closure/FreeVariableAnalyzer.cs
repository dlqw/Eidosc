using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;

namespace Eidosc.Mir.Closure;

/// <summary>
/// 自由变量分析器 - 分析 Lambda 表达式的自由变量
/// 为闭包转换做准备
/// </summary>
public sealed class FreeVariableAnalyzer
{
    /// <summary>
    /// 分析 Lambda 表达式的自由变量
    /// </summary>
    /// <param name="lambda">Lambda 表达式</param>
    /// <returns>自由变量名称集合</returns>
    public HashSet<string> Analyze(LambdaExpr lambda)
    {
        var boundVars = new HashSet<string>();
        var freeVars = new HashSet<string>();

        // 收集参数绑定的变量
        foreach (var param in lambda.Parameters)
        {
            CollectPatternBindings(param, boundVars);
        }

        // 分析 Lambda 体
        if (lambda.Body != null)
        {
            AnalyzeNode(lambda.Body, boundVars, freeVars);
        }

        return freeVars;
    }

    /// <summary>
    /// 分析任意 AST 节点的自由变量
    /// </summary>
    public HashSet<string> AnalyzeExpression(EidosAstNode node)
    {
        var boundVars = new HashSet<string>();
        var freeVars = new HashSet<string>();
        AnalyzeNode(node, boundVars, freeVars);
        return freeVars;
    }

    /// <summary>
    /// 递归分析 AST 节点
    /// </summary>
    private void AnalyzeNode(EidosAstNode? node, HashSet<string> boundVars, HashSet<string> freeVars)
    {
        if (node == null)
        {
            return;
        }

        switch (node)
        {
            case IdentifierExpr identifier:
                AnalyzeIdentifier(identifier, boundVars, freeVars);
                break;

            case LambdaExpr lambda:
                AnalyzeLambda(lambda, boundVars, freeVars);
                break;

            case LetDecl letDecl:
                AnalyzeNode(letDecl.Value, boundVars, freeVars);
                if (letDecl.Pattern != null)
                {
                    AnalyzePatternExpressions(letDecl.Pattern, boundVars, freeVars);
                    CollectPatternBindings(letDecl.Pattern, boundVars);
                }
                break;

            case LetQuestionDecl letQuestionDecl:
                AnalyzeNode(letQuestionDecl.Value, boundVars, freeVars);
                if (letQuestionDecl.Pattern != null)
                {
                    AnalyzePatternExpressions(letQuestionDecl.Pattern, boundVars, freeVars);
                    CollectPatternBindings(letQuestionDecl.Pattern, boundVars);
                }
                break;

            case Assignment assignment:
                if (!string.IsNullOrWhiteSpace(assignment.Target) &&
                    !boundVars.Contains(assignment.Target))
                {
                    freeVars.Add(assignment.Target);
                }

                AnalyzeNode(assignment.Value, boundVars, freeVars);
                break;

            case BinaryExpr binary:
                AnalyzeNode(binary.Left, boundVars, freeVars);
                AnalyzeNode(binary.Right, boundVars, freeVars);
                break;

            case UnaryExpr unary:
                AnalyzeNode(unary.Operand, boundVars, freeVars);
                break;

            case CallExpr call:
                if (call.Function != null)
                {
                    AnalyzeNode(call.Function, boundVars, freeVars);
                }
                foreach (var arg in call.PositionalArgs)
                {
                    AnalyzeNode(arg, boundVars, freeVars);
                }
                foreach (var arg in call.NamedArgs)
                {
                    if (arg.Value != null)
                    {
                        AnalyzeNode(arg.Value, boundVars, freeVars);
                    }
                }
                break;

            case MethodCallExpr methodCall:
                AnalyzeNode(methodCall.Receiver, boundVars, freeVars);
                foreach (var arg in methodCall.PositionalArgs)
                {
                    AnalyzeNode(arg, boundVars, freeVars);
                }
                foreach (var arg in methodCall.NamedArgs)
                {
                    AnalyzeNode(arg.Value, boundVars, freeVars);
                }
                break;

            case InfixCallExpr infixCall:
                AnalyzeNode(infixCall.Left, boundVars, freeVars);
                AnalyzeNode(infixCall.Right, boundVars, freeVars);
                break;

            case IndexExpr index:
                AnalyzeNode(index.Object, boundVars, freeVars);
                AnalyzeNode(index.Index, boundVars, freeVars);
                break;

            case CtorExpr ctor:
                AnalyzeNode(ctor.UpdateBase, boundVars, freeVars);
                foreach (var arg in ctor.PositionalArgs)
                {
                    AnalyzeNode(arg, boundVars, freeVars);
                }
                foreach (var arg in ctor.NamedArgs)
                {
                    AnalyzeNode(arg.Value, boundVars, freeVars);
                }
                break;

            case RecordUpdateExpr recordUpdate:
                AnalyzeNode(recordUpdate.Base, boundVars, freeVars);
                foreach (var arg in recordUpdate.NamedArgs)
                {
                    AnalyzeNode(arg.Value, boundVars, freeVars);
                }

                AnalyzeNode(recordUpdate.DesugaredCtor, boundVars, freeVars);
                AnalyzeNode(recordUpdate.DesugaredMatch, boundVars, freeVars);
                break;

            case ContextualRecordLiteralExpr contextualRecord:
                foreach (var arg in contextualRecord.NamedArgs)
                {
                    AnalyzeNode(arg.Value, boundVars, freeVars);
                }

                AnalyzeNode(contextualRecord.DesugaredCtor, boundVars, freeVars);
                break;

            case IfExpr ifExpr:
                if (ifExpr.Condition != null)
                {
                    AnalyzeNode(ifExpr.Condition, boundVars, freeVars);
                }
                if (ifExpr.ThenBranch != null)
                {
                    AnalyzeNode(ifExpr.ThenBranch, boundVars, freeVars);
                }
                if (ifExpr.ElseBranch != null)
                {
                    AnalyzeNode(ifExpr.ElseBranch, boundVars, freeVars);
                }
                break;

            case IfLetExpr ifLetExpr:
                if (ifLetExpr.MatchedExpression != null)
                {
                    AnalyzeNode(ifLetExpr.MatchedExpression, boundVars, freeVars);
                }

                var thenBoundVars = new HashSet<string>(boundVars);
                if (ifLetExpr.Pattern != null)
                {
                    AnalyzePatternExpressions(ifLetExpr.Pattern, boundVars, freeVars);
                    CollectPatternBindings(ifLetExpr.Pattern, thenBoundVars);
                }

                if (ifLetExpr.ThenBranch != null)
                {
                    AnalyzeNode(ifLetExpr.ThenBranch, thenBoundVars, freeVars);
                }

                if (ifLetExpr.ElseBranch != null)
                {
                    AnalyzeNode(ifLetExpr.ElseBranch, boundVars, freeVars);
                }
                break;

            case WhileLetExpr whileLetExpr:
                if (whileLetExpr.MatchedExpression != null)
                {
                    AnalyzeNode(whileLetExpr.MatchedExpression, boundVars, freeVars);
                }

                var bodyBoundVars = new HashSet<string>(boundVars);
                if (whileLetExpr.Pattern != null)
                {
                    AnalyzePatternExpressions(whileLetExpr.Pattern, boundVars, freeVars);
                    CollectPatternBindings(whileLetExpr.Pattern, bodyBoundVars);
                }

                if (whileLetExpr.Body != null)
                {
                    AnalyzeNode(whileLetExpr.Body, bodyBoundVars, freeVars);
                }
                break;

            case LoopExpr loop:
                if (loop.Body != null)
                {
                    AnalyzeNode(loop.Body, boundVars, freeVars);
                }
                break;

            case BreakExpr breakExpr:
                if (breakExpr.Value != null)
                {
                    AnalyzeNode(breakExpr.Value, boundVars, freeVars);
                }
                break;

            case ContinueExpr:
                break;

            case ReturnExpr returnExpr:
                AnalyzeNode(returnExpr.Value, boundVars, freeVars);
                break;

            case BlockExpr block:
                var blockBoundVars = new HashSet<string>(boundVars);
                foreach (var stmt in block.Statements)
                {
                    AnalyzeNode(stmt, blockBoundVars, freeVars);
                }
                if (block.ResultExpression != null)
                {
                    AnalyzeNode(block.ResultExpression, blockBoundVars, freeVars);
                }
                break;

            case TupleExpr tuple:
                foreach (var elem in tuple.Elements)
                {
                    AnalyzeNode(elem, boundVars, freeVars);
                }
                break;

            case ListExpr list:
                foreach (var elem in list.Elements)
                {
                    AnalyzeNode(elem, boundVars, freeVars);
                }
                break;

            case ListComprehension comprehension:
                var comprehensionBoundVars = new HashSet<string>(boundVars);
                foreach (var qualifier in comprehension.Qualifiers)
                {
                    if (qualifier.GeneratorExpression != null)
                    {
                        AnalyzeNode(qualifier.GeneratorExpression, comprehensionBoundVars, freeVars);
                    }

                    if (qualifier.GuardExpression != null)
                    {
                        AnalyzeNode(qualifier.GuardExpression, comprehensionBoundVars, freeVars);
                    }

                    if (qualifier.GeneratorPattern != null)
                    {
                        AnalyzePatternExpressions(qualifier.GeneratorPattern, comprehensionBoundVars, freeVars);
                        CollectPatternBindings(qualifier.GeneratorPattern, comprehensionBoundVars);
                    }
                }

                AnalyzeNode(comprehension.Output, comprehensionBoundVars, freeVars);
                break;

            case MatchExpr match:
                if (match.MatchedExpression != null)
                {
                    AnalyzeNode(match.MatchedExpression, boundVars, freeVars);
                }
                foreach (var branch in match.Branches)
                {
                    var branchBoundVars = new HashSet<string>(boundVars);
                    if (branch.Pattern != null)
                    {
                        AnalyzePatternExpressions(branch.Pattern, boundVars, freeVars);
                        CollectPatternBindings(branch.Pattern, branchBoundVars);
                    }

                    if (branch.Guard != null)
                    {
                        AnalyzeNode(branch.Guard, branchBoundVars, freeVars);
                    }

                    if (branch.Expression != null)
                    {
                        AnalyzeNode(branch.Expression, branchBoundVars, freeVars);
                    }
                }
                break;

            case SequentialGuardExpr sequentialGuard:
                var sequentialBoundVars = new HashSet<string>(boundVars);
                foreach (var guard in sequentialGuard.Guards)
                {
                    AnalyzeNode(guard, sequentialBoundVars, freeVars);
                }
                break;

            case PatternGuardExpr patternGuard:
                if (patternGuard.SourceExpression != null)
                {
                    AnalyzeNode(patternGuard.SourceExpression, boundVars, freeVars);
                }

                if (patternGuard.Pattern != null)
                {
                    AnalyzePatternExpressions(patternGuard.Pattern, boundVars, freeVars);
                    CollectPatternBindings(patternGuard.Pattern, boundVars);
                }
                break;

            case DoExpr doExpr:
                AnalyzeDoExpression(doExpr, boundVars, freeVars);
                break;

            case GivenExpr given:
                AnalyzeNode(given.Target, boundVars, freeVars);
                break;

            case AssociatedConstExpr associatedConst:
                AnalyzeNode(associatedConst.ImplementationValue, boundVars, freeVars);
                break;

            case LiteralExpr:
            case PathExpr:
                // 字面量和路径没有自由变量
                break;

            default:
                // 对于其他类型的节点，尝试反射遍历
                break;
        }
    }

    private void AnalyzeDoExpression(DoExpr doExpr, HashSet<string> boundVars, HashSet<string> freeVars)
    {
        var doBoundVars = new HashSet<string>(boundVars);
        foreach (var binding in doExpr.Bindings)
        {
            AnalyzeNode(binding.Value, doBoundVars, freeVars);
            switch (binding.Kind)
            {
                case DoBindingKind.Bind when binding.Pattern != null:
                    AnalyzePatternExpressions(binding.Pattern, doBoundVars, freeVars);
                    CollectPatternBindings(binding.Pattern, doBoundVars);
                    break;

                case DoBindingKind.Let when !string.IsNullOrWhiteSpace(binding.VarName):
                    doBoundVars.Add(binding.VarName);
                    break;
            }
        }
    }

    /// <summary>
    /// 分析标识符表达式
    /// </summary>
    private void AnalyzeIdentifier(IdentifierExpr identifier, HashSet<string> boundVars, HashSet<string> freeVars)
    {
        var name = identifier.Name;
        if (!string.IsNullOrEmpty(name) && !boundVars.Contains(name))
        {
            freeVars.Add(name);
        }
    }

    /// <summary>
    /// 分析 Lambda 表达式（处理嵌套 Lambda）
    /// </summary>
    private void AnalyzeLambda(LambdaExpr lambda, HashSet<string> boundVars, HashSet<string> freeVars)
    {
        // 为嵌套 Lambda 创建新的作用域
        var nestedBoundVars = new HashSet<string>(boundVars);

        // 添加参数到绑定变量
        foreach (var param in lambda.Parameters)
        {
            CollectPatternBindings(param, nestedBoundVars);
        }

        // 分析 Lambda 体
        if (lambda.Body != null)
        {
            AnalyzeNode(lambda.Body, nestedBoundVars, freeVars);
        }
    }

    /// <summary>
    /// 分析模式中携带的表达式（目前主要是 ViewPattern）
    /// </summary>
    private void AnalyzePatternExpressions(Pattern pattern, HashSet<string> boundVars, HashSet<string> freeVars)
    {
        switch (pattern)
        {
            case ViewPattern viewPattern:
                if (viewPattern.ViewExpression != null)
                {
                    AnalyzeNode(viewPattern.ViewExpression, boundVars, freeVars);
                }

                if (viewPattern.InnerPattern != null)
                {
                    AnalyzePatternExpressions(viewPattern.InnerPattern, boundVars, freeVars);
                }
                return;

            case TuplePattern tuplePattern:
                foreach (var element in tuplePattern.Elements)
                {
                    AnalyzePatternExpressions(element, boundVars, freeVars);
                }
                return;

            case ListPattern listPattern:
                foreach (var element in listPattern.Elements)
                {
                    AnalyzePatternExpressions(element, boundVars, freeVars);
                }

                if (listPattern.RestPattern != null)
                {
                    AnalyzePatternExpressions(listPattern.RestPattern, boundVars, freeVars);
                }

                foreach (var element in listPattern.SuffixElements)
                {
                    AnalyzePatternExpressions(element, boundVars, freeVars);
                }
                return;

            case CtorPattern ctorPattern:
                foreach (var positional in ctorPattern.PositionalPatterns)
                {
                    AnalyzePatternExpressions(positional, boundVars, freeVars);
                }

                foreach (var field in ctorPattern.NamedPatterns)
                {
                    if (field.Pattern != null)
                    {
                        AnalyzePatternExpressions(field.Pattern, boundVars, freeVars);
                    }
                }
                return;

            case OrPattern orPattern:
                foreach (var alternative in orPattern.Alternatives)
                {
                    AnalyzePatternExpressions(alternative, boundVars, freeVars);
                }
                return;

            case AndPattern andPattern:
                foreach (var conjunct in andPattern.Conjuncts)
                {
                    AnalyzePatternExpressions(conjunct, boundVars, freeVars);
                }
                return;

            case NotPattern notPattern:
                if (notPattern.InnerPattern != null)
                {
                    AnalyzePatternExpressions(notPattern.InnerPattern, boundVars, freeVars);
                }
                return;

            case RangePattern rangePattern:
                if (rangePattern.Start != null)
                {
                    AnalyzePatternExpressions(rangePattern.Start, boundVars, freeVars);
                }

                if (rangePattern.End != null)
                {
                    AnalyzePatternExpressions(rangePattern.End, boundVars, freeVars);
                }
                return;

            case AsPattern asPattern:
                if (asPattern.InnerPattern != null)
                {
                    AnalyzePatternExpressions(asPattern.InnerPattern, boundVars, freeVars);
                }
                return;
        }
    }

    /// <summary>
    /// 从模式中收集绑定的变量名
    /// </summary>
    private void CollectPatternBindings(Pattern pattern, HashSet<string> boundVars)
    {
        switch (pattern)
        {
            case VarPattern varPattern:
                if (!string.IsNullOrEmpty(varPattern.Name))
                {
                    boundVars.Add(varPattern.Name);
                }
                break;

            case TuplePattern tuplePattern:
                foreach (var elem in tuplePattern.Elements)
                {
                    CollectPatternBindings(elem, boundVars);
                }
                break;

            case ListPattern listPattern:
                foreach (var elem in listPattern.Elements)
                {
                    CollectPatternBindings(elem, boundVars);
                }

                if (listPattern.RestPattern != null)
                {
                    CollectPatternBindings(listPattern.RestPattern, boundVars);
                }

                foreach (var elem in listPattern.SuffixElements)
                {
                    CollectPatternBindings(elem, boundVars);
                }
                break;

            case OrPattern orPattern:
                foreach (var alternative in orPattern.Alternatives)
                {
                    CollectPatternBindings(alternative, boundVars);
                }
                break;

            case AndPattern andPattern:
                foreach (var conjunct in andPattern.Conjuncts)
                {
                    CollectPatternBindings(conjunct, boundVars);
                }
                break;

            case NotPattern:
                // not-pattern 不引入绑定
                break;

            case RangePattern rangePattern:
                if (rangePattern.Start != null)
                {
                    CollectPatternBindings(rangePattern.Start, boundVars);
                }

                if (rangePattern.End != null)
                {
                    CollectPatternBindings(rangePattern.End, boundVars);
                }
                break;

            case CtorPattern ctorPattern:
                // 处理位置参数模式
                foreach (var posPattern in ctorPattern.PositionalPatterns)
                {
                    CollectPatternBindings(posPattern, boundVars);
                }
                // 处理命名字段模式
                foreach (var field in ctorPattern.NamedPatterns)
                {
                    if (field.Pattern != null)
                    {
                        CollectPatternBindings(field.Pattern, boundVars);
                    }
                    else if (field.IsShorthand && !string.IsNullOrEmpty(field.FieldName))
                    {
                        // 简写形式: name 等价于 name: name
                        boundVars.Add(field.FieldName);
                    }
                }
                break;

            case AsPattern asPattern:
                if (!string.IsNullOrEmpty(asPattern.BindingName))
                {
                    boundVars.Add(asPattern.BindingName);
                }
                if (asPattern.InnerPattern != null)
                {
                    CollectPatternBindings(asPattern.InnerPattern, boundVars);
                }
                break;

            case ViewPattern viewPattern:
                if (viewPattern.InnerPattern != null)
                {
                    CollectPatternBindings(viewPattern.InnerPattern, boundVars);
                }
                break;

            case WildcardPattern:
            case LiteralPattern:
                // 通配符和字面量模式不绑定变量
                break;
        }
    }
}
