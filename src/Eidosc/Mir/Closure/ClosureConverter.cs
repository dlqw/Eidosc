using Eidosc.Symbols;
using Eidosc.Ast;
using Eidosc.Ast.Expressions;
using Eidosc.Semantic;

namespace Eidosc.Mir.Closure;

/// <summary>
/// 闭包转换器 - 将 Lambda 表达式转换为 MIR 闭包表示
/// </summary>
public sealed class ClosureConverter
{
    private readonly SymbolTable _symbolTable;
    private readonly FreeVariableAnalyzer _freeVariableAnalyzer;
    private readonly ClosureEnvironmentGenerator _closureEnvGenerator;
    private int _closureCounter;

    public ClosureConverter(SymbolTable symbolTable)
    {
        _symbolTable = symbolTable;
        _freeVariableAnalyzer = new FreeVariableAnalyzer();
        _closureEnvGenerator = new ClosureEnvironmentGenerator(symbolTable);
    }

    /// <summary>
    /// 转换 Lambda 表达式为闭包
    /// </summary>
    /// <param name="lambda">Lambda 表达式</param>
    /// <returns>闭包转换结果</returns>
    public ClosureConversionResult Convert(LambdaExpr lambda)
    {
        // 1. 分析自由变量
        var freeVariables = _freeVariableAnalyzer.Analyze(lambda);

        // 2. 生成闭包环境
        var envInfo = _closureEnvGenerator.GenerateEnvironment(lambda, freeVariables);

        // 3. 生成闭包函数名
        var closureName = $"{WellKnownStrings.InternalNames.ClosurePrefix}{_closureCounter++}";

        // 4. 创建转换结果
        return new ClosureConversionResult
        {
            ClosureName = closureName,
            EnvironmentInfo = envInfo,
            FreeVariables = freeVariables,
            OriginalLambda = lambda
        };
    }

    /// <summary>
    /// 检查表达式是否包含需要闭包转换的 Lambda
    /// </summary>
    public bool ContainsClosure(EidosAstNode? node)
    {
        if (node == null)
        {
            return false;
        }

        return node switch
        {
            LambdaExpr => true,
            BinaryExpr binary => ContainsClosure(binary.Left) || ContainsClosure(binary.Right),
            UnaryExpr unary => ContainsClosure(unary.Operand),
            CallExpr call => ContainsClosure(call.Function) ||
                             call.PositionalArgs.Any(ContainsClosure) ||
                             call.NamedArgs.Any(a => a.Value != null && ContainsClosure(a.Value)),
            IfExpr ifExpr => ContainsClosure(ifExpr.Condition) ||
                            (ifExpr.ThenBranch != null && ContainsClosure(ifExpr.ThenBranch)) ||
                            (ifExpr.ElseBranch != null && ContainsClosure(ifExpr.ElseBranch)),
            IfLetExpr ifLetExpr => (ifLetExpr.MatchedExpression != null && ContainsClosure(ifLetExpr.MatchedExpression)) ||
                                   (ifLetExpr.ThenBranch != null && ContainsClosure(ifLetExpr.ThenBranch)) ||
                                   (ifLetExpr.ElseBranch != null && ContainsClosure(ifLetExpr.ElseBranch)),
            WhileLetExpr whileLetExpr => (whileLetExpr.MatchedExpression != null && ContainsClosure(whileLetExpr.MatchedExpression)) ||
                                         (whileLetExpr.Body != null && ContainsClosure(whileLetExpr.Body)),
            LoopExpr loop => loop.Body != null && ContainsClosure(loop.Body),
            BreakExpr breakExpr => breakExpr.Value != null && ContainsClosure(breakExpr.Value),
            BlockExpr block => block.Statements.Any(ContainsClosure) ||
                              (block.ResultExpression != null && ContainsClosure(block.ResultExpression)),
            TupleExpr tuple => tuple.Elements.Any(ContainsClosure),
            ListExpr list => list.Elements.Any(ContainsClosure),
            MatchExpr match => (match.MatchedExpression != null && ContainsClosure(match.MatchedExpression)) ||
                              match.Branches.Any(b =>
                                  (b.Guard != null && ContainsClosure(b.Guard)) ||
                                  (b.Expression != null && ContainsClosure(b.Expression))),
            PatternGuardExpr patternGuard => patternGuard.SourceExpression != null && ContainsClosure(patternGuard.SourceExpression),
            _ => false
        };
    }

    /// <summary>
    /// 收集表达式中的所有 Lambda
    /// </summary>
    public List<LambdaExpr> CollectLambdas(EidosAstNode node)
    {
        var lambdas = new List<LambdaExpr>();
        CollectLambdasRecursive(node, lambdas);
        return lambdas;
    }

    private void CollectLambdasRecursive(EidosAstNode? node, List<LambdaExpr> lambdas)
    {
        if (node == null)
        {
            return;
        }

        switch (node)
        {
            case LambdaExpr lambda:
                lambdas.Add(lambda);
                // 递归收集嵌套 Lambda
                if (lambda.Body != null)
                {
                    CollectLambdasRecursive(lambda.Body, lambdas);
                }
                break;

            case BinaryExpr binary:
                CollectLambdasRecursive(binary.Left, lambdas);
                CollectLambdasRecursive(binary.Right, lambdas);
                break;

            case UnaryExpr unary:
                CollectLambdasRecursive(unary.Operand, lambdas);
                break;

            case CallExpr call:
                if (call.Function != null)
                {
                    CollectLambdasRecursive(call.Function, lambdas);
                }
                foreach (var arg in call.PositionalArgs)
                {
                    CollectLambdasRecursive(arg, lambdas);
                }
                foreach (var arg in call.NamedArgs)
                {
                    if (arg.Value != null)
                    {
                        CollectLambdasRecursive(arg.Value, lambdas);
                    }
                }
                break;

            case IfExpr ifExpr:
                if (ifExpr.Condition != null)
                {
                    CollectLambdasRecursive(ifExpr.Condition, lambdas);
                }
                if (ifExpr.ThenBranch != null)
                {
                    CollectLambdasRecursive(ifExpr.ThenBranch, lambdas);
                }
                if (ifExpr.ElseBranch != null)
                {
                    CollectLambdasRecursive(ifExpr.ElseBranch, lambdas);
                }
                break;

            case IfLetExpr ifLetExpr:
                if (ifLetExpr.MatchedExpression != null)
                {
                    CollectLambdasRecursive(ifLetExpr.MatchedExpression, lambdas);
                }
                if (ifLetExpr.ThenBranch != null)
                {
                    CollectLambdasRecursive(ifLetExpr.ThenBranch, lambdas);
                }
                if (ifLetExpr.ElseBranch != null)
                {
                    CollectLambdasRecursive(ifLetExpr.ElseBranch, lambdas);
                }
                break;

            case WhileLetExpr whileLetExpr:
                if (whileLetExpr.MatchedExpression != null)
                {
                    CollectLambdasRecursive(whileLetExpr.MatchedExpression, lambdas);
                }
                if (whileLetExpr.Body != null)
                {
                    CollectLambdasRecursive(whileLetExpr.Body, lambdas);
                }
                break;

            case LoopExpr loop:
                if (loop.Body != null)
                {
                    CollectLambdasRecursive(loop.Body, lambdas);
                }
                break;

            case BreakExpr breakExpr:
                if (breakExpr.Value != null)
                {
                    CollectLambdasRecursive(breakExpr.Value, lambdas);
                }
                break;

            case BlockExpr block:
                foreach (var stmt in block.Statements)
                {
                    CollectLambdasRecursive(stmt, lambdas);
                }
                if (block.ResultExpression != null)
                {
                    CollectLambdasRecursive(block.ResultExpression, lambdas);
                }
                break;

            case TupleExpr tuple:
                foreach (var elem in tuple.Elements)
                {
                    CollectLambdasRecursive(elem, lambdas);
                }
                break;

            case ListExpr list:
                foreach (var elem in list.Elements)
                {
                    CollectLambdasRecursive(elem, lambdas);
                }
                break;

            case MatchExpr match:
                if (match.MatchedExpression != null)
                {
                    CollectLambdasRecursive(match.MatchedExpression, lambdas);
                }
                foreach (var branch in match.Branches)
                {
                    if (branch.Guard != null)
                    {
                        CollectLambdasRecursive(branch.Guard, lambdas);
                    }

                    if (branch.Expression != null)
                    {
                        CollectLambdasRecursive(branch.Expression, lambdas);
                    }
                }
                break;

            case PatternGuardExpr patternGuard:
                if (patternGuard.SourceExpression != null)
                {
                    CollectLambdasRecursive(patternGuard.SourceExpression, lambdas);
                }
                break;

            case GivenExpr given:
                CollectLambdasRecursive(given.Target, lambdas);
                break;
        }
    }
}

/// <summary>
/// 闭包转换结果
/// </summary>
public sealed class ClosureConversionResult
{
    /// <summary>
    /// 闭包函数名称
    /// </summary>
    public required string ClosureName { get; init; }

    /// <summary>
    /// 闭包环境信息
    /// </summary>
    public required ClosureEnvironmentInfo EnvironmentInfo { get; init; }

    /// <summary>
    /// 自由变量集合
    /// </summary>
    public required HashSet<string> FreeVariables { get; init; }

    /// <summary>
    /// 原始 Lambda 表达式
    /// </summary>
    public required LambdaExpr OriginalLambda { get; init; }
}
