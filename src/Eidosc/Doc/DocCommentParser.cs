using System.Text;

namespace Eidosc.Doc;

/// <summary>
/// 解析原始文档注释文本为结构化 DocComment。
/// 支持标签：@param, @return, @example, @deprecated
/// </summary>
public static class DocCommentParser
{
    public static DocComment Parse(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return DocComment.Empty;

        var lines = rawText.Split('\n');
        var summaryBuilder = new StringBuilder();
        var paramTags = new List<DocParamTag>();
        string? returns = null;
        var examples = new List<string>();
        string? deprecated = null;

        // 每行去掉前导 /// 或 * 前缀和空白
        var cleanedLines = new List<string>();
        foreach (var line in lines)
        {
            var cleaned = CleanLine(line);
            if (cleaned != null)
                cleanedLines.Add(cleaned);
        }

        // 第一阶段：收集摘要（到第一个标签之前）
        var i = 0;
        while (i < cleanedLines.Count && !IsTagLine(cleanedLines[i]))
        {
            summaryBuilder.AppendLine(cleanedLines[i].Trim());
            i++;
        }

        // 第二阶段：收集标签
        while (i < cleanedLines.Count)
        {
            var line = cleanedLines[i];
            if (line.StartsWith("@param", StringComparison.Ordinal))
            {
                var (name, desc) = ParseParamTag(line);
                // 收集后续非标签行作为描述续行
                i++;
                while (i < cleanedLines.Count && !IsTagLine(cleanedLines[i]))
                {
                    desc += " " + cleanedLines[i].Trim();
                    i++;
                }
                paramTags.Add(new DocParamTag { Name = name, Description = desc.Trim() });
            }
            else if (line.StartsWith("@return", StringComparison.Ordinal))
            {
                var desc = line["@return".Length..].TrimStart();
                i++;
                while (i < cleanedLines.Count && !IsTagLine(cleanedLines[i]))
                {
                    desc += " " + cleanedLines[i].Trim();
                    i++;
                }
                returns = desc.Trim();
            }
            else if (line.StartsWith("@example", StringComparison.Ordinal))
            {
                var desc = line["@example".Length..].TrimStart();
                i++;
                while (i < cleanedLines.Count && !IsTagLine(cleanedLines[i]))
                {
                    desc += "\n" + cleanedLines[i].Trim();
                    i++;
                }
                examples.Add(desc.Trim());
            }
            else if (line.StartsWith("@deprecated", StringComparison.Ordinal))
            {
                var desc = line["@deprecated".Length..].TrimStart();
                i++;
                while (i < cleanedLines.Count && !IsTagLine(cleanedLines[i]))
                {
                    desc += " " + cleanedLines[i].Trim();
                    i++;
                }
                deprecated = desc.Trim();
            }
            else
            {
                // 不认识的标签，附加到摘要
                summaryBuilder.AppendLine(line.Trim());
                i++;
            }
        }

        var summary = summaryBuilder.ToString().Trim();
        if (string.IsNullOrEmpty(summary) && paramTags.Count == 0 && returns == null)
            return DocComment.Empty;

        return new DocComment
        {
            Summary = summary,
            Params = paramTags,
            Returns = returns,
            Examples = examples,
            Deprecated = deprecated,
            RawText = rawText
        };
    }

    private static string? CleanLine(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("///", StringComparison.Ordinal))
        {
            var rest = trimmed[3..];
            return rest.Length > 0 && rest[0] == ' ' ? rest[1..] : rest;
        }
        if (trimmed.StartsWith("/**", StringComparison.Ordinal))
        {
            // 多行块注释开始行：/** 内容 */ 或 /** 内容
            var rest = trimmed[3..];
            if (rest.EndsWith("*/", StringComparison.Ordinal))
                rest = rest[..^2];
            return rest.TrimStart();
        }
        if (trimmed.StartsWith("*/", StringComparison.Ordinal))
            return null; // 块注释结束标记
        if (trimmed.StartsWith("*", StringComparison.Ordinal))
        {
            var rest = trimmed[1..];
            return rest.Length > 0 && rest[0] == ' ' ? rest[1..] : rest;
        }
        return trimmed;
    }

    private static bool IsTagLine(string line)
    {
        return line.StartsWith("@param", StringComparison.Ordinal)
               || line.StartsWith("@return", StringComparison.Ordinal)
               || line.StartsWith("@example", StringComparison.Ordinal)
               || line.StartsWith("@deprecated", StringComparison.Ordinal);
    }

    private static (string name, string desc) ParseParamTag(string line)
    {
        var rest = line["@param".Length..].TrimStart();
        var spaceIdx = rest.IndexOf(' ');
        if (spaceIdx < 0)
            return (rest, "");
        return (rest[..spaceIdx], rest[(spaceIdx + 1)..].TrimStart());
    }
}
