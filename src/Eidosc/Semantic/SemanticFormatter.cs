using Eidosc.Symbols;
using System.Text;
using Eidosc.Pipeline;

namespace Eidosc.Semantic;

/// <summary>
/// Formatting utilities extracted from PhaseOutput (A3).
/// </summary>
public static class SemanticFormatter
{
    public static string FormatSymbols(SymbolTable symbolTable)
    {
        var sb = new StringBuilder();
        sb.AppendLine(PipelineMessages.SymbolsHeader);

        var symbols = symbolTable.Symbols;
        sb.AppendLine(PipelineMessages.SymbolCount(symbols.Count));
        sb.AppendLine();

        foreach (var (id, symbol) in symbols)
        {
            sb.AppendLine($"[{id.Value:D4}] {symbol.Kind,-12} {symbol.Name}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 格式化作用域树
    /// </summary>


    public static string FormatScopes(SymbolTable symbolTable)
    {
        var sb = new StringBuilder();
        sb.AppendLine(PipelineMessages.ScopesHeader);
        sb.AppendLine();

        // 从当前作用域向上遍历到根
        var currentScope = symbolTable.CurrentScope;
        if (currentScope != null)
        {
            FormatScope(currentScope, 0, sb);
        }

        return sb.ToString();
    }


    private static void FormatScope(Scope scope, int indent, StringBuilder sb)
    {
        var prefix = new string(' ', indent * 2);
        sb.AppendLine($"{prefix}Scope({scope.Kind}):");

        var bindings = scope.GetLocalBindings();
        foreach (var (name, id) in bindings)
        {
            sb.AppendLine($"{prefix}  {name} -> [{id.Value}]");
        }

        // Scope 没有直接的 Children 属性，        // 因为 Scope 没有直接的 Children 属性，我们需要从符号表重建
    }

    /// <summary>
    /// 格式化类型推断结果
    /// </summary>

}
