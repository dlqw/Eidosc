using System.Text;
using System.Text.Json;

namespace Eidosc.Debug;

/// <summary>
/// 调试格式化工具
/// </summary>
public static class DebugFormatter
{
    /// <summary>
    /// 格式化 JSON 对象
    /// </summary>
    public static string FormatJson(object obj)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        return JsonSerializer.Serialize(obj, options);
    }

    /// <summary>
    /// 格式化列表
    /// </summary>
    public static string FormatList<T>(string title, IEnumerable<T> items, Func<T, string> formatter)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== {title} ===");

        var index = 0;
        foreach (var item in items)
        {
            sb.AppendLine($"[{index++}] {formatter(item)}");
        }

        sb.AppendLine($"Total: {index} items");
        return sb.ToString();
    }

    /// <summary>
    /// 格式化键值对列表
    /// </summary>
    public static string FormatKeyValue<TKey, TValue>(
        string title,
        IEnumerable<KeyValuePair<TKey, TValue>> items,
        Func<TKey, string>? keyFormatter = null,
        Func<TValue, string>? valueFormatter = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== {title} ===");

        keyFormatter ??= k => k?.ToString() ?? "(null)";
        valueFormatter ??= v => v?.ToString() ?? "(null)";

        foreach (var (key, value) in items)
        {
            sb.AppendLine($"{keyFormatter(key)}: {valueFormatter(value)}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 格式化树形结构
    /// </summary>
    public static string FormatTree<T>(
        T root,
        Func<T, string> nodeFormatter,
        Func<T, IEnumerable<T>> getChildren,
        string indent = "  ")
    {
        var sb = new StringBuilder();
        FormatTreeRecursive(sb, root, nodeFormatter, getChildren, "", indent);
        return sb.ToString();
    }

    private static void FormatTreeRecursive<T>(
        StringBuilder sb,
        T node,
        Func<T, string> nodeFormatter,
        Func<T, IEnumerable<T>> getChildren,
        string currentIndent,
        string childIndent)
    {
        sb.AppendLine($"{currentIndent}{nodeFormatter(node)}");

        var children = getChildren(node);
        var childList = children.ToList();

        for (var i = 0; i < childList.Count; i++)
        {
            var isLast = i == childList.Count - 1;
            var prefix = isLast ? "└── " : "├── ";
            var nextIndent = isLast ? "    " : "│   ";

            FormatTreeRecursive(
                sb,
                childList[i],
                nodeFormatter,
                getChildren,
                currentIndent + prefix,
                currentIndent + nextIndent);
        }
    }

    /// <summary>
    /// 创建分隔线
    /// </summary>
    public static string Separator(char c = '=', int length = 60)
    {
        return new string(c, length);
    }

    /// <summary>
    /// 格式化标题
    /// </summary>
    public static string Title(string text, char c = '=', int width = 60)
    {
        var sb = new StringBuilder();
        sb.AppendLine(new string(c, width));
        sb.AppendLine(Center(text, width));
        sb.AppendLine(new string(c, width));
        return sb.ToString();
    }

    /// <summary>
    /// 居中文本
    /// </summary>
    public static string Center(string text, int width)
    {
        if (text.Length >= width) return text;

        var padding = (width - text.Length) / 2;
        return new string(' ', padding) + text;
    }

    /// <summary>
    /// 格式化表格
    /// </summary>
    public static string FormatTable<T>(
        string title,
        IEnumerable<T> items,
        params (string Header, Func<T, string> Getter)[] columns)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Title(title));

        var itemList = items.ToList();
        if (itemList.Count == 0)
        {
            sb.AppendLine("(empty)");
            return sb.ToString();
        }

        // 计算列宽
        var widths = new int[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            widths[i] = columns[i].Header.Length;
        }

        foreach (var item in itemList)
        {
            for (var i = 0; i < columns.Length; i++)
            {
                var value = columns[i].Getter(item);
                widths[i] = Math.Max(widths[i], value.Length);
            }
        }

        // 表头
        var headers = columns.Select((c, i) => c.Header.PadRight(widths[i]));
        sb.AppendLine(string.Join(" | ", headers));
        sb.AppendLine(string.Join("-+-", widths.Select(w => new string('-', w))));

        // 数据行
        foreach (var item in itemList)
        {
            var values = columns.Select((c, i) => c.Getter(item).PadRight(widths[i]));
            sb.AppendLine(string.Join(" | ", values));
        }

        return sb.ToString();
    }
}
