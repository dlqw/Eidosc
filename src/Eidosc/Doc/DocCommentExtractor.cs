namespace Eidosc.Doc;

/// <summary>
/// 从源码文本中提取文档注释（/// 和 /** */），按源码行位置映射到后续声明。
/// </summary>
public static class DocCommentExtractor
{
    /// <summary>
    /// 提取文档注释，返回 key 为声明起始行号（0-based），value 为结构化 DocComment。
    /// </summary>
    public static Dictionary<int, DocComment> Extract(string sourceText)
    {
        var result = new Dictionary<int, DocComment>();
        if (string.IsNullOrEmpty(sourceText))
            return result;

        var lines = sourceText.Split('\n');
        var docLines = new List<(int lineNumber, string text)>();

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();

            if (trimmed.StartsWith("///", StringComparison.Ordinal))
            {
                docLines.Add((i, lines[i]));
            }
            else if (trimmed.StartsWith("/**", StringComparison.Ordinal))
            {
                // 收集整个块注释
                var blockBuilder = new System.Text.StringBuilder();
                var startLine = i;
                var currentLine = trimmed;

                // 处理单行块注释 /** 内容 */
                if (currentLine.EndsWith("*/", StringComparison.Ordinal))
                {
                    docLines.Add((startLine, currentLine));
                }
                else
                {
                    blockBuilder.AppendLine(currentLine);
                    i++;
                    while (i < lines.Length)
                    {
                        currentLine = lines[i];
                        blockBuilder.AppendLine(currentLine);
                        if (currentLine.TrimStart().EndsWith("*/", StringComparison.Ordinal))
                            break;
                        i++;
                    }
                    docLines.Add((startLine, blockBuilder.ToString()));
                }
            }
            else if (docLines.Count > 0)
            {
                // 非文档行且前面有文档注释 → 将文档附加到此行
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    var rawDoc = string.Join("\n", docLines.Select(l => l.text));
                    var doc = DocCommentParser.Parse(rawDoc);
                    if (doc != DocComment.Empty)
                        result[i] = doc;
                }
                docLines.Clear();
            }
            else if (trimmed.Length > 0 && !trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                // 非空白非注释行，清空文档收集
                docLines.Clear();
            }
        }

        return result;
    }
}
