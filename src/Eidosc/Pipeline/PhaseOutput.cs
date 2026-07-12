using Eidosc.Mir;
using System.Text;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.CodeGen.Llvm;

namespace Eidosc.Pipeline;

/// <summary>
/// 阶段输出格式化工具 — 解析阶段摘要与 LLVM IR 输出。
/// 模块级格式化已下沉至各模块 Formatter 类 (A3)。
/// </summary>
public static class PhaseOutput
{
    public static string FormatTokens(IReadOnlyList<Token> tokens)
    {
        var sb = new StringBuilder();
        sb.AppendLine(PipelineMessages.TokenListHeader);
        sb.AppendLine(PipelineMessages.TotalCount(tokens.Count));
        sb.AppendLine();

        foreach (var token in tokens)
        {
            var loc = token.Location;
            var line = loc.Line + 1;
            var col = loc.Column + 1;

            string tokenInfo;
            if (token is ContentToken contentToken)
            {
                var text = contentToken.ToString() ?? "";
                var terminalName = contentToken.Terminal?.DebugName ?? "?";
                var flags = contentToken.Terminal?.Flags ?? TerminalFlag.None;
                var flagStr = flags != TerminalFlag.None ? $" [{flags}]" : "";
                tokenInfo = $"{terminalName}{flagStr} \"{EscapeString(text)}\"";
            }
            else if (token is ErrorToken errToken)
            {
                tokenInfo = PipelineMessages.TokenError(errToken.Message);
            }
            else if (token is EofToken)
            {
                tokenInfo = PipelineMessages.TokenEof;
            }
            else if (token is CommentToken commentToken)
            {
                tokenInfo = PipelineMessages.TokenComment(EscapeString(commentToken.Comment));
            }
            else
            {
                tokenInfo = token.ToString() ?? "?";
            }

            sb.AppendLine($"[{line:D4}:{col:D3}] {tokenInfo}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 格式化 CST 结构
    /// </summary>


    public static string FormatCst(ConcreteSyntaxNode node)
    {
        var sb = new StringBuilder();
        sb.AppendLine(PipelineMessages.CstHeader);
        sb.AppendLine();
        FormatCstInternal(node, 0, sb);
        return sb.ToString();
    }


    private static void FormatCstInternal(ConcreteSyntaxNode node, int indent, StringBuilder sb)
    {
        var prefix = new string(' ', indent * 2);

        if (node is TerminalCstNode term)
        {
            var text = term.Token is ContentToken ct ? ct.ToString() : term.Token.GetType().Name;
            sb.AppendLine($"{prefix}[T] {term.Terminal?.DebugName ?? "?"}: \"{EscapeString(text)}\"");
        }
        else if (node is NonTerminalCstNode nt)
        {
            sb.AppendLine($"{prefix}[NT] {nt.NonTerminal.DebugName}");
        }

        foreach (var child in node.Children)
        {
            FormatCstInternal(child, indent + 1, sb);
        }
    }

    /// <summary>
    /// 格式化 AST 结构
    /// </summary>


    public static string FormatAst(ModuleDecl module)
    {
        var sb = new StringBuilder();
        sb.AppendLine(PipelineMessages.AstHeader);
        sb.AppendLine();
        FormatAstNode(module, 0, sb);
        return sb.ToString();
    }


    private static void FormatAstNode(EidosAstNode node, int indent, StringBuilder sb)
    {
        var prefix = new string(' ', indent * 2);
        var details = MirFormatter.GetAstNodeDetails(node);
        var typeInfo = node.InferredType != null ? $" : {node.InferredType}" : "";

        sb.AppendLine($"{prefix}{node.GetType().Name}{typeInfo}: {details}");

        // 遍历子节点
        foreach (var child in MirFormatter.GetAstChildren(node))
        {
            FormatAstNode(child, indent + 1, sb);
        }
    }

    /// <summary>
    /// 格式化符号表
    /// </summary>


    public static string FormatSummary(CompilationResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(PipelineMessages.CompilationSummaryHeader);
        var status = result.Success
            ? PipelineMessages.StatusSuccess
            : PipelineMessages.StatusFailed;
        sb.AppendLine(PipelineMessages.SummaryStatus(status));
        sb.AppendLine(PipelineMessages.CompletedPhase(result.CompletedPhase));
        sb.AppendLine(PipelineMessages.TotalTimeMs(result.TotalTime.TotalMilliseconds));
        sb.AppendLine(PipelineMessages.ErrorsWarnings(result.ErrorCount, result.WarningCount));
        sb.AppendLine();

        if (result.PhaseTimes.Count > 0)
        {
            sb.AppendLine(PipelineMessages.PhaseTimingsHeader);
            foreach (var (phase, time) in result.PhaseTimes)
            {
                if (result.PhaseAllocations.TryGetValue(phase, out var allocatedBytes))
                {
                    sb.AppendLine($"//   {phase}: {time.TotalMilliseconds:F2}ms, {FormatBytes(allocatedBytes)}");
                }
                else
                {
                    sb.AppendLine($"//   {phase}: {time.TotalMilliseconds:F2}ms");
                }
            }
        }

        if (result.SubphaseMetrics.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(PipelineMessages.SubphaseTimingsHeader);
            foreach (var phaseGroup in result.SubphaseMetrics
                         .OrderBy(metric => metric.Phase)
                         .ThenByDescending(metric => metric.Elapsed)
                         .GroupBy(metric => metric.Phase))
            {
                sb.AppendLine($"//   [{phaseGroup.Key}]");
                foreach (var metric in phaseGroup)
                {
                    sb.AppendLine(
                        $"//     {metric.Name}: {metric.Elapsed.TotalMilliseconds:F2}ms, alloc {FormatBytes(metric.AllocatedBytes)}, heap Δ {FormatSignedBytes(metric.ManagedBytesDelta)}, GC {metric.Gen0Collections}/{metric.Gen1Collections}/{metric.Gen2Collections}");
                }
            }
        }

        return sb.ToString();
    }


    private static string FormatBytes(long bytes)
    {
        const long kib = 1024;
        const long mib = kib * 1024;
        const long gib = mib * 1024;

        return bytes switch
        {
            >= gib => $"{bytes / (double)gib:F2} GiB",
            >= mib => $"{bytes / (double)mib:F2} MiB",
            >= kib => $"{bytes / (double)kib:F2} KiB",
            _ => $"{bytes} B"
        };
    }


    private static string FormatSignedBytes(long bytes)
    {
        if (bytes == 0)
        {
            return "0 B";
        }

        var prefix = bytes > 0 ? WellKnownStrings.Operators.Add : WellKnownStrings.Operators.Subtract;
        return prefix + FormatBytes(Math.Abs(bytes));
    }

    /// <summary>
    /// 格式化类型代换
    /// </summary>


    public static string FormatLlvm(LlvmModule module)
    {
        var sb = new StringBuilder();
        sb.AppendLine(PipelineMessages.LlvmModuleHeader);
        sb.AppendLine(PipelineMessages.ModuleNameLine(module.Name));
        sb.AppendLine(PipelineMessages.FunctionCount(module.Functions.Count));
        sb.AppendLine(PipelineMessages.GlobalVariableCount(module.Globals.Count));
        sb.AppendLine(PipelineMessages.ExternalDeclarationCount(module.Declarations.Count));
        sb.AppendLine();

        // 函数签名摘要
        if (module.Functions.Count > 0)
        {
            sb.AppendLine(PipelineMessages.FunctionSignaturesHeader);
            sb.AppendLine(Separator('-'));
            sb.AppendLine($"{PipelineMessages.NameColumn,-30} | {PipelineMessages.ReturnTypeColumn,-15} | {PipelineMessages.ParameterCountColumn,-8}");
            sb.AppendLine(Separator('-'));

            foreach (var func in module.Functions)
            {
                var name = func.Name.Length > 28 ? func.Name[..28] + WellKnownStrings.Punctuation.DotDot : func.Name;
                var retType = func.ReturnType.ToIrString();
                sb.AppendLine($"{name,-30} | {retType,-15} | {func.Parameters.Count}");
            }

            sb.AppendLine();
        }

        // 外部声明摘要
        if (module.Declarations.Count > 0)
        {
            sb.AppendLine(PipelineMessages.ExternalDeclarationsHeader);
            foreach (var decl in module.Declarations)
            {
                sb.AppendLine($"//   {decl.Name}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// 格式化 LLVM IR 文本
    /// </summary>


    public static string FormatLlvmIr(string llvmIr)
    {
        var sb = new StringBuilder();
        sb.AppendLine(PipelineMessages.LlvmIrTextHeader);
        sb.AppendLine(Separator('='));
        sb.AppendLine();
        sb.Append(llvmIr);
        return sb.ToString();
    }

    private static string Separator(char c) => new string(c, 80);

    private static string EscapeString(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Replace("\\", "\\\\")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t")
                .Replace("\"", "\\\"");
    }
}
