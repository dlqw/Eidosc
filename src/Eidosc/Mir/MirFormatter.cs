using Eidosc.Symbols;
using Eidosc.Ast.Types;
using Eidosc.Ast.Patterns;
using System.Text;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Eidosc.Types;

namespace Eidosc.Mir;

/// <summary>
/// Formatting utilities extracted from PhaseOutput (A3).
/// </summary>
public static class MirFormatter
{
    public static string FormatMir(MirModule module)
    {
        var sb = new StringBuilder();
        sb.AppendLine(PipelineMessages.MirHeader);
        sb.AppendLine(PipelineMessages.ModuleLine(module.Name));
        sb.AppendLine(PipelineMessages.FunctionCount(module.Functions.Count));
        foreach (var (typeId, key) in module.DynamicTypeKeys.OrderBy(static pair => pair.Key))
        {
            if (typeId >= 4400 && typeId <= 4520)
            {
                sb.AppendLine($"type {typeId}: {key}");
            }
        }
        sb.AppendLine();

        foreach (var func in module.Functions)
        {
            FormatMirFunc(func, sb);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 格式化 MIR 优化摘要
    /// </summary>


    public static string FormatMirOptimization(
        bool enabled,
        bool applied,
        IReadOnlyList<string> passNames,
        MirModule? before,
        MirModule after,
        string? skipReason = null,
        int specializerRunCount = 0,
        int specializerChangedIterationCount = 0,
        int optimizerChangedIterationCount = 0,
        string? specializationLoopConvergenceReason = null)
    {
        var sb = new StringBuilder();
        var beforeStats = before is null ? default : GetMirStats(before);
        var afterStats = GetMirStats(after);

        sb.AppendLine(PipelineMessages.MirOptimizationSummaryHeader);
        sb.AppendLine($"enabled: {enabled}");
        sb.AppendLine($"applied: {applied}");
        sb.AppendLine($"passes: {(passNames.Count == 0 ? "(none)" : string.Join(", ", passNames))}");
        sb.AppendLine($"specializer_runs: {specializerRunCount}");
        sb.AppendLine($"specializer_changed_iterations: {specializerChangedIterationCount}");
        sb.AppendLine($"optimizer_changed_iterations: {optimizerChangedIterationCount}");
        if (!string.IsNullOrWhiteSpace(specializationLoopConvergenceReason))
        {
            sb.AppendLine($"specialization_loop_convergence: {specializationLoopConvergenceReason}");
        }

        if (!string.IsNullOrWhiteSpace(skipReason))
        {
            sb.AppendLine($"skip_reason: {skipReason}");
        }

        if (before is not null)
        {
            sb.AppendLine($"before: funcs={beforeStats.Functions}, blocks={beforeStats.Blocks}, instrs={beforeStats.Instructions}");
        }

        sb.AppendLine($"after: funcs={afterStats.Functions}, blocks={afterStats.Blocks}, instrs={afterStats.Instructions}");

        if (before is not null)
        {
            sb.AppendLine($"delta: funcs={afterStats.Functions - beforeStats.Functions}, blocks={afterStats.Blocks - beforeStats.Blocks}, instrs={afterStats.Instructions - beforeStats.Instructions}");
        }

        sb.Append(Eidosc.Mir.Optimize.RecursiveCallAnalysis.Format(
            Eidosc.Mir.Optimize.RecursiveCallAnalysis.Analyze(after)));

        return sb.ToString();
    }


    private static void FormatMirFunc(MirFunc func, StringBuilder sb)
    {
        sb.AppendLine($"func {func.Name} {{");

        // 局部变量
        if (func.Locals.Count > 0)
        {
            sb.AppendLine("  locals:");
            foreach (var local in func.Locals)
            {
                sb.AppendLine($"    {FormatMirLocal(local)}");
            }
        }

        // 基本块
        foreach (var block in func.BasicBlocks)
        {
            sb.AppendLine($"  bb{block.Id.Value}:");
            foreach (var instr in block.Instructions)
            {
                sb.AppendLine($"    {instr}");
            }
            if (block.Terminator != null)
            {
                sb.AppendLine($"    {block.Terminator}");
            }
        }

        sb.AppendLine("}");
        sb.AppendLine();
    }



    private static string EscapeString(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Replace("\\", "\\\\")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t")
                .Replace("\"", "\\\"");
    }


    private static string FormatMirLocal(MirLocal local)
    {
        var head = local.IsParameter
            ? $"param %{local.Id.Value}: {local.Name}"
            : $"local %{local.Id.Value}: {local.Name}";
        head = $"{head} type={local.TypeId.Value}";

        var bindingMode = local.BindingMode.ToDisplayText();
        return bindingMode == "value"
            ? head
            : $"{head} [{bindingMode}]";
    }

    private static MirStats GetMirStats(MirModule module)
    {
        var blocks = module.Functions.Sum(function => function.BasicBlocks.Count);
        var instructions = module.Functions.Sum(function => function.BasicBlocks.Sum(block => block.Instructions.Count));
        return new MirStats(module.Functions.Count, blocks, instructions);
    }

    private readonly record struct MirStats(int Functions, int Blocks, int Instructions);


    internal static string GetAstNodeDetails(EidosAstNode node)
    {
        return node switch
        {
            // 声明
            ModuleDecl m => $"path=\"{string.Join(WellKnownStrings.Operators.Divide, m.Path)}\" decls={m.Declarations.Count}",
            LetDecl => "let-pattern",
            FuncDef f => $"name=\"{f.Name}\" typeParams={f.TypeParams.Count} signature={f.Signature.Count} branches={f.Body.Count}",
            FuncDecl f => $"name=\"{f.Name}\"",
            AdtDef a => $"name=\"{a.Name}\" typeParams={a.TypeParams.Count} constructors={a.Constructors.Count}",
            TraitDef t => $"name=\"{t.Name}\" typeParams={t.TypeParams.Count} methods={t.Methods.Count}",
            ImportDecl i => $"path=\"{string.Join(WellKnownStrings.Separators.Path, i.ModulePath)}\"",
            Assignment a => $"target=\"{a.Target}\"",

            // 表达式
            LiteralExpr l => $"value=\"{EscapeString(l.Value?.ToString() ?? "")}\" kind={l.Kind}",
            IdentifierExpr i => $"name=\"{i.Name}\"",
            PathExpr p => $"path=\"{(p.ModulePath.Count > 0 ? string.Join(WellKnownStrings.Separators.Path, p.ModulePath) + WellKnownStrings.Separators.Path : "")}{p.Name}\"{(p.TypeArgs.Count > 0 ? $" args={p.TypeArgs.Count}" : "")}",
            BinaryExpr b => $"op={b.Operator} ({b.Operator.ToSymbol()})",
            UnaryExpr u => $"op={u.Operator} ({u.Operator.ToSymbol()})",
            CallExpr c => $"args={c.PositionalArgs.Count}",
            MethodCallExpr m => $"method=\"{m.MethodName}\" args={m.PositionalArgs.Count}",
            LambdaExpr l => $"params={l.Parameters.Count}",
            IfExpr i => $"hasElse={i.ElseBranch != null}",
            IfLetExpr i => $"hasElse={i.ElseBranch != null}",
            WhileLetExpr => "while-let",
            MatchExpr m => $"branches={m.Branches.Count}",
            BlockExpr b => $"stmts={b.Statements.Count}",
            CtorExpr c => $"ctor=\"{c.ConstructorPath?.TypeName ?? "?"}\"",
            ListExpr l => $"elements={l.Elements.Count}",
            TupleExpr t => $"elements={t.Elements.Count}",
            LoopExpr => "loop",
            ReturnExpr r => r.Value != null ? WellKnownStrings.Keywords.Return : "return void",
            BreakExpr => WellKnownStrings.AdditionalKeywords.Break,
            ContinueExpr => WellKnownStrings.AdditionalKeywords.Continue,
            UnreachableExpr => WellKnownStrings.Keywords.Unreachable,
            IndexExpr i => i.IsTypeApplication ? $"typeApply args={i.TypeArgs.Count}" : "index",
            ListComprehension => "listComprehension",

            // 模式
            PatternBranch b => "branch",
            VarPattern vp => $"name=\"{vp.Name}\" bindingMode={vp.BindingMode.ToDisplayText()}",
            WildcardPattern => "_",
            LiteralPattern lp => $"value=\"{lp.Value}\"",
            CtorPattern cp => $"ctor=\"{cp.ConstructorName}\"",
            TuplePattern tp => $"elements={tp.Elements.Count}",
            ListPattern lp => $"elements={lp.Elements.Count} hasRest={lp.HasRestMarker}",
            OrPattern op => $"alternatives={op.Alternatives.Count}",
            AndPattern andp => $"conjuncts={andp.Conjuncts.Count}",
            NotPattern => "not",
            RangePattern => "range",
            ViewPattern => "view",
            AsPattern asp => $"binding=\"{asp.BindingName}\" bindingMode={asp.BindingMode.ToDisplayText()}",
            FieldPattern fp => $"field=\"{fp.FieldName}\"",

            // 类型
            TypePath tp => $"name=\"{tp.TypeName}\"{(tp.TypeArgs.Count > 0 ? $" args={tp.TypeArgs.Count}" : "")}",
            ArrowType => "arrow",
            EffectfulType => "effectful",
            TupleType tt => $"elements={tt.Elements.Count}",
            WildcardType => "_",
            TypeParam tp => $"name=\"{tp.Name}\"{(tp.TraitConstraints.Count > 0 ? $" traits=[{string.Join(", ", tp.TraitConstraints.Select(t => t.TraitName))}]" : "")}{(tp.KindAnnotation != null ? $" kind={tp.GetKindText()}" : "")}",
            TraitRef tr => $"name=\"{tr.TraitName}\" typeArgs={tr.TypeArgs.Count}",

            // 辅助节点
            FieldInit fi => $"field=\"{fi.FieldName}\"",
            Constructor c => $"name=\"{c.Name}\" positional={c.PositionalArgs.Count} named={c.NamedArgs.Count}",
            Field f => $"name=\"{f.Name}\"",

            _ => ""
        };
    }

    internal static IEnumerable<EidosAstNode> GetAstChildren(EidosAstNode node)
    {
        return node switch
        {
            ModuleDecl m => m.Declarations,
            LetDecl l => (l.Pattern != null ? [l.Pattern] : Enumerable.Empty<EidosAstNode>())
                .Concat(l.Value != null ? [l.Value] : Enumerable.Empty<EidosAstNode>()),
            FuncDef f => f.TypeParams.Cast<EidosAstNode>().Concat(f.Signature).Concat(f.Body),
            TraitDef t => t.TypeParams.Cast<EidosAstNode>().Concat(t.Methods),
            BlockExpr b => b.Statements,
            IfExpr i => (i.Condition != null ? [i.Condition] : Enumerable.Empty<EidosAstNode>())
                .Concat(i.ThenBranch != null ? [i.ThenBranch] : Enumerable.Empty<EidosAstNode>())
                .Concat(i.ElseBranch != null ? [i.ElseBranch] : Enumerable.Empty<EidosAstNode>()),
            IfLetExpr i => (i.Pattern != null ? [i.Pattern] : Enumerable.Empty<EidosAstNode>())
                .Concat(i.MatchedExpression != null ? [i.MatchedExpression] : Enumerable.Empty<EidosAstNode>())
                .Concat(i.ThenBranch != null ? [i.ThenBranch] : Enumerable.Empty<EidosAstNode>())
                .Concat(i.ElseBranch != null ? [i.ElseBranch] : Enumerable.Empty<EidosAstNode>()),
            WhileLetExpr w => (w.Pattern != null ? [w.Pattern] : Enumerable.Empty<EidosAstNode>())
                .Concat(w.MatchedExpression != null ? [w.MatchedExpression] : Enumerable.Empty<EidosAstNode>())
                .Concat(w.Body != null ? [w.Body] : Enumerable.Empty<EidosAstNode>()),
            MatchExpr m => (m.MatchedExpression != null ? [m.MatchedExpression] : Enumerable.Empty<EidosAstNode>())
                .Concat(m.Branches),
            LoopExpr l => l.Body != null ? [l.Body] : Enumerable.Empty<EidosAstNode>(),
            ReturnExpr r => r.Value != null ? [r.Value] : Enumerable.Empty<EidosAstNode>(),
            BreakExpr b => b.Value != null ? [b.Value] : Enumerable.Empty<EidosAstNode>(),
            UnreachableExpr => Enumerable.Empty<EidosAstNode>(),
            LambdaExpr l => l.Parameters.Cast<EidosAstNode>()
                .Concat(l.Body != null ? [l.Body] : Enumerable.Empty<EidosAstNode>()),
            CallExpr c => (c.Function != null ? [c.Function] : Enumerable.Empty<EidosAstNode>())
                .Concat(c.PositionalArgs)
                .Concat(c.NamedArgs.Where(a => a.Value != null).Select(a => a.Value!)),
            MethodCallExpr m => (m.Receiver != null ? [m.Receiver] : Enumerable.Empty<EidosAstNode>())
                .Concat(m.PositionalArgs)
                .Concat(m.NamedArgs.Where(a => a.Value != null).Select(a => a.Value!)),
            BinaryExpr b => (b.Left != null ? [b.Left] : Enumerable.Empty<EidosAstNode>())
                .Concat(b.Right != null ? [b.Right] : Enumerable.Empty<EidosAstNode>()),
            UnaryExpr u => u.Operand != null ? [u.Operand] : Enumerable.Empty<EidosAstNode>(),
            ListExpr l => l.Elements,
            TupleExpr t => t.Elements,
            CtorExpr c => c.PositionalArgs.Cast<EidosAstNode>()
                .Concat(c.NamedArgs.Where(a => a.Value != null).Select(a => a.Value!)),
            PathExpr p => p.TypeArgs.Cast<EidosAstNode>(),
            IndexExpr i => (i.Object != null ? [i.Object] : Enumerable.Empty<EidosAstNode>())
                .Concat(i.Index != null ? [i.Index] : Enumerable.Empty<EidosAstNode>())
                .Concat(i.TypeArgs.Cast<EidosAstNode>()),
            PatternBranch b => (b.Pattern != null ? [b.Pattern] : Enumerable.Empty<EidosAstNode>())
                .Concat(b.Guard != null ? [b.Guard] : Enumerable.Empty<EidosAstNode>())
                .Concat(b.Expression != null ? [b.Expression] : Enumerable.Empty<EidosAstNode>()),
            PatternGuardExpr g => (g.Pattern != null ? [g.Pattern] : Enumerable.Empty<EidosAstNode>())
                .Concat(g.SourceExpression != null ? [g.SourceExpression] : Enumerable.Empty<EidosAstNode>()),
            OrPattern op => op.Alternatives,
            AndPattern andp => andp.Conjuncts,
            NotPattern np => np.InnerPattern != null ? [np.InnerPattern] : Enumerable.Empty<EidosAstNode>(),
            RangePattern rp => CollectNonNullNodes(rp.Start, rp.End),
            ListPattern lp => CollectListPatternChildren(lp),
            ViewPattern vp => CollectNonNullNodes(vp.ViewExpression, vp.InnerPattern),
            AsPattern ap => ap.InnerPattern != null ? [ap.InnerPattern] : Enumerable.Empty<EidosAstNode>(),
            _ => Enumerable.Empty<EidosAstNode>()
        };
    }

    private static IEnumerable<EidosAstNode> CollectListPatternChildren(ListPattern pattern)
    {
        foreach (var element in pattern.Elements)
        {
            yield return element;
        }

        if (pattern.RestPattern != null)
        {
            yield return pattern.RestPattern;
        }

        foreach (var element in pattern.SuffixElements)
        {
            yield return element;
        }
    }

    private static IEnumerable<EidosAstNode> CollectNonNullNodes(params EidosAstNode?[] nodes)
    {
        foreach (var node in nodes)
        {
            if (node != null)
            {
                yield return node;
            }
        }
    }



    /// <summary>
    /// 格式化活性分析结果
    /// </summary>

}
