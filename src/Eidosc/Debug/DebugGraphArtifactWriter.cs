using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Eidosc.Debug;

internal static partial class DebugGraphArtifactWriter
{
    private const int SvgWidth = 1400;
    private const int BlockWidth = 150;
    private const int BlockHeight = 62;
    private const int BlockGapX = 30;
    private const int BlockGapY = 26;
    private const int BlocksPerRow = 7;

    public static void WriteArtifacts(
        string textFilePath,
        string phase,
        string fileName,
        string content,
        DebugGraphFormat format)
    {
        if (format == DebugGraphFormat.None || string.IsNullOrWhiteSpace(content))
        {
            DeleteGraphArtifacts(textFilePath);
            return;
        }

        DeleteGraphArtifacts(textFilePath);
        var graph = TryBuildGraph(phase, fileName, content);
        if (graph == null)
        {
            return;
        }

        WriteMirFunctionArtifacts(textFilePath, graph, format);
    }

    private static void WriteMirFunctionArtifacts(string textFilePath, MirCfgGraph graph, DebugGraphFormat format)
    {
        var sequence = 1;
        var sequenceWidth = Math.Max(3, graph.Functions.Count.ToString().Length);

        foreach (var function in graph.Functions.Where(function => function.Blocks.Count > 0))
        {
            var functionGraph = new MirCfgGraph($"{graph.Title}/{function.Name}", [function]);
            var d2Content = ToD2(functionGraph);
            var artifactBasePath = BuildFunctionArtifactBasePath(textFilePath, sequence, sequenceWidth, function.Name);

            if (format is DebugGraphFormat.D2 or DebugGraphFormat.Both)
            {
                File.WriteAllText(artifactBasePath + ".d2", d2Content);
            }

            if (format is DebugGraphFormat.Svg or DebugGraphFormat.Both)
            {
                var svgPath = artifactBasePath + ".svg";
                if (!TryWriteSvgWithD2(d2Content, svgPath))
                {
                    File.WriteAllText(svgPath, ToSvg(functionGraph));
                }
            }

            sequence++;
        }
    }

    private static MirCfgGraph? TryBuildGraph(string phase, string fileName, string content)
    {
        if (!phase.Contains("mir", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(fileName, "mir", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var functions = ParseMirFunctions(content);
        if (functions.Count == 0 || functions.All(function => function.Blocks.Count == 0))
        {
            return null;
        }

        return new MirCfgGraph($"{phase}/{fileName}", functions);
    }

    private static List<MirFunctionGraph> ParseMirFunctions(string content)
    {
        var functions = new List<MirFunctionGraph>();
        MirFunctionGraph? currentFunction = null;
        MirBasicBlockGraph? currentBlock = null;

        foreach (var rawLine in SplitLines(content))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var functionMatch = FunctionHeaderRegex().Match(line);
            if (functionMatch.Success)
            {
                currentFunction = new MirFunctionGraph(functionMatch.Groups["name"].Value);
                functions.Add(currentFunction);
                currentBlock = null;
                continue;
            }

            if (line == "}")
            {
                currentFunction = null;
                currentBlock = null;
                continue;
            }

            if (currentFunction == null)
            {
                continue;
            }

            var blockMatch = BasicBlockRegex().Match(line);
            if (blockMatch.Success)
            {
                currentBlock = new MirBasicBlockGraph(blockMatch.Groups["name"].Value);
                currentFunction.Blocks.Add(currentBlock);
                continue;
            }

            if (currentBlock == null || line.StartsWith("locals:", StringComparison.Ordinal) ||
                line.StartsWith("param ", StringComparison.Ordinal) ||
                line.StartsWith("local ", StringComparison.Ordinal))
            {
                continue;
            }

            currentBlock.InstructionCount++;
            currentBlock.LastInstruction = line;
            foreach (Match target in BasicBlockTargetRegex().Matches(line))
            {
                currentBlock.Successors.Add(target.Groups["name"].Value);
            }
        }

        return functions;
    }

    private static string ToD2(MirCfgGraph graph)
    {
        var sb = new StringBuilder();
        sb.AppendLine("direction: right");
        sb.AppendLine($"title: {EscapeD2(graph.Title)}");
        sb.AppendLine();

        foreach (var function in graph.Functions)
        {
            var functionId = FunctionId(function.Name);
            sb.AppendLine($"{functionId}: {{");
            sb.AppendLine($"  label: {EscapeD2($"func {function.Name}")}");
            foreach (var block in function.Blocks)
            {
                var blockId = BlockId(function.Name, block.Name);
                sb.AppendLine($"  {blockId}: {{");
                sb.AppendLine($"    label: {EscapeD2(BuildBlockLabel(block))}");
                sb.AppendLine("    shape: rectangle");
                sb.AppendLine("    style.fill: \"#ffffff\"");
                sb.AppendLine("    style.stroke: \"#111111\"");
                sb.AppendLine("  }");
            }
            sb.AppendLine("}");
            sb.AppendLine();
        }

        foreach (var function in graph.Functions)
        {
            var blockNames = function.Blocks.Select(block => block.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var block in function.Blocks)
            {
                foreach (var successor in block.Successors.Distinct(StringComparer.Ordinal))
                {
                    if (!blockNames.Contains(successor))
                    {
                        continue;
                    }

                    sb.AppendLine($"{FunctionId(function.Name)}.{BlockId(function.Name, block.Name)} -> {FunctionId(function.Name)}.{BlockId(function.Name, successor)}");
                }
            }
        }

        return sb.ToString();
    }

    private static bool TryWriteSvgWithD2(string d2Content, string svgPath)
    {
        var tempD2Path = Path.Combine(Path.GetTempPath(), $"eidosc_debug_graph_{Guid.NewGuid():N}.d2");
        try
        {
            File.WriteAllText(tempD2Path, d2Content);

            var startInfo = new ProcessStartInfo
            {
                FileName = "d2",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            startInfo.ArgumentList.Add("--sketch");
            startInfo.ArgumentList.Add("--theme");
            startInfo.ArgumentList.Add("0");
            startInfo.ArgumentList.Add("--pad");
            startInfo.ArgumentList.Add("20");
            startInfo.ArgumentList.Add(tempD2Path);
            startInfo.ArgumentList.Add(svgPath);

            using var process = Process.Start(startInfo);

            if (process == null)
            {
                return false;
            }

            if (!process.WaitForExit(milliseconds: 60_000))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            return process.ExitCode == 0 && File.Exists(svgPath);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        finally
        {
            TryDelete(tempD2Path);
        }
    }

    private static string ToSvg(MirCfgGraph graph)
    {
        var positions = CalculatePositions(graph);
        var height = Math.Max(220, positions.Values.Select(position => position.Y + position.Height).DefaultIfEmpty(160).Max() + 60);
        var sb = new StringBuilder();

        sb.AppendLine(FormattableString.Invariant(
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{SvgWidth}\" height=\"{height}\" viewBox=\"0 0 {SvgWidth} {height}\">"));
        sb.AppendLine("  <defs>");
        sb.AppendLine("    <marker id=\"arrow\" markerWidth=\"10\" markerHeight=\"10\" refX=\"8\" refY=\"3\" orient=\"auto\" markerUnits=\"strokeWidth\">");
        sb.AppendLine("      <path d=\"M0,0 L0,6 L9,3 z\" fill=\"#111111\"/>");
        sb.AppendLine("    </marker>");
        sb.AppendLine("  </defs>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    .bg { fill: #ffffff; }");
        sb.AppendLine("    .title { font: 700 16px ui-sans-serif, system-ui, sans-serif; fill: #111111; }");
        sb.AppendLine("    .fn { font: 700 13px ui-sans-serif, system-ui, sans-serif; fill: #111111; }");
        sb.AppendLine("    .block { fill: #ffffff; stroke: #111111; stroke-width: 1.4; rx: 6; }");
        sb.AppendLine("    .block-title { font: 700 11px ui-monospace, SFMono-Regular, Consolas, monospace; fill: #111111; }");
        sb.AppendLine("    .block-text { font: 10px ui-monospace, SFMono-Regular, Consolas, monospace; fill: #111111; }");
        sb.AppendLine("    .edge { stroke: #111111; stroke-width: 1.2; fill: none; marker-end: url(#arrow); }");
        sb.AppendLine("  </style>");
        sb.AppendLine(FormattableString.Invariant(
            $"  <rect class=\"bg\" x=\"0\" y=\"0\" width=\"{SvgWidth}\" height=\"{height}\"/>"));
        sb.AppendLine(FormattableString.Invariant(
            $"  <text class=\"title\" x=\"32\" y=\"34\">{EscapeXml(graph.Title)} CFG</text>"));

        foreach (var function in graph.Functions)
        {
            if (function.Blocks.Count == 0 ||
                !positions.TryGetValue(FunctionTitleKey(function.Name), out var titlePosition))
            {
                continue;
            }

            sb.AppendLine(FormattableString.Invariant(
                $"  <text class=\"fn\" x=\"{titlePosition.X}\" y=\"{titlePosition.Y}\">func {EscapeXml(function.Name)} ({function.Blocks.Count} blocks)</text>"));
        }

        foreach (var function in graph.Functions)
        {
            var blockNames = function.Blocks.Select(block => block.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var block in function.Blocks)
            {
                if (!positions.TryGetValue(BlockKey(function.Name, block.Name), out var from))
                {
                    continue;
                }

                foreach (var successor in block.Successors.Distinct(StringComparer.Ordinal))
                {
                    if (!blockNames.Contains(successor) ||
                        !positions.TryGetValue(BlockKey(function.Name, successor), out var to))
                    {
                        continue;
                    }

                    AppendEdge(sb, from, to);
                }
            }
        }

        foreach (var function in graph.Functions)
        {
            foreach (var block in function.Blocks)
            {
                var position = positions[BlockKey(function.Name, block.Name)];
                sb.AppendLine(FormattableString.Invariant(
                    $"  <rect class=\"block\" x=\"{position.X}\" y=\"{position.Y}\" width=\"{position.Width}\" height=\"{position.Height}\"/>"));
                sb.AppendLine(FormattableString.Invariant(
                    $"  <text class=\"block-title\" x=\"{position.X + 9}\" y=\"{position.Y + 18}\">{EscapeXml(block.Name)}</text>"));
                sb.AppendLine(FormattableString.Invariant(
                    $"  <text class=\"block-text\" x=\"{position.X + 9}\" y=\"{position.Y + 35}\">{block.InstructionCount} instr</text>"));
                sb.AppendLine(FormattableString.Invariant(
                    $"  <text class=\"block-text\" x=\"{position.X + 9}\" y=\"{position.Y + 51}\">{EscapeXml(TrimLabel(block.LastInstruction ?? "empty", 20))}</text>"));
            }
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static Dictionary<string, SvgNodePosition> CalculatePositions(MirCfgGraph graph)
    {
        var positions = new Dictionary<string, SvgNodePosition>(StringComparer.Ordinal);
        var y = 72;
        foreach (var function in graph.Functions)
        {
            if (function.Blocks.Count == 0)
            {
                continue;
            }

            positions[FunctionTitleKey(function.Name)] = new SvgNodePosition(32, y, 400, 20);
            y += 24;
            for (var i = 0; i < function.Blocks.Count; i++)
            {
                var row = i / BlocksPerRow;
                var column = i % BlocksPerRow;
                var x = 32 + column * (BlockWidth + BlockGapX);
                positions[BlockKey(function.Name, function.Blocks[i].Name)] = new SvgNodePosition(
                    x,
                    y + row * (BlockHeight + BlockGapY),
                    BlockWidth,
                    BlockHeight);
            }

            var rows = (int)Math.Ceiling(function.Blocks.Count / (double)BlocksPerRow);
            y += Math.Max(1, rows) * (BlockHeight + BlockGapY) + 34;
        }

        return positions;
    }

    private static void AppendEdge(StringBuilder sb, SvgNodePosition from, SvgNodePosition to)
    {
        var startX = from.X + from.Width;
        var startY = from.Y + from.Height / 2;
        var endX = to.X;
        var endY = to.Y + to.Height / 2;

        if (to.X <= from.X)
        {
            startX = from.X + from.Width / 2;
            startY = from.Y + from.Height;
            endX = to.X + to.Width / 2;
            endY = to.Y;
        }

        sb.AppendLine(FormattableString.Invariant(
            $"  <path class=\"edge\" d=\"M{startX} {startY} C{startX + 28} {startY}, {endX - 28} {endY}, {endX} {endY}\"/>"));
    }

    private static string BuildBlockLabel(MirBasicBlockGraph block)
    {
        return $"{block.Name}\n{block.InstructionCount} instr\n{TrimLabel(block.LastInstruction ?? "empty", 32)}";
    }

    private static string FunctionId(string functionName)
    {
        return "fn_" + SanitizeIdentifier(functionName);
    }

    private static string BlockId(string functionName, string blockName)
    {
        return "block_" + SanitizeIdentifier(functionName) + "_" + SanitizeIdentifier(blockName);
    }

    private static string FunctionTitleKey(string functionName)
    {
        return $"title:{functionName}";
    }

    private static string BlockKey(string functionName, string blockName)
    {
        return $"{functionName}:{blockName}";
    }

    private static string BuildFunctionArtifactBasePath(
        string textFilePath,
        int sequence,
        int sequenceWidth,
        string functionName)
    {
        var directory = Path.GetDirectoryName(textFilePath) ?? Directory.GetCurrentDirectory();
        var stem = Path.GetFileNameWithoutExtension(textFilePath);
        var sequenceText = sequence.ToString($"D{sequenceWidth}");
        var functionSegment = SanitizePathSegment(functionName);
        return Path.Combine(directory, $"{stem}_{sequenceText}_{functionSegment}");
    }

    private static string SanitizeIdentifier(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }

        return sb.Length == 0 ? "unnamed" : sb.ToString();
    }

    private static string SanitizePathSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            sb.Append(invalidChars.Contains(ch) || char.IsWhiteSpace(ch) ? '_' : ch);
        }

        return sb.Length == 0 ? "unnamed" : TrimFileName(sb.ToString(), 72);
    }

    private static string TrimFileName(string value, int maxLength)
    {
        return value.Length <= maxLength
            ? value
            : value[..maxLength];
    }

    private static IEnumerable<string> SplitLines(string content)
    {
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static string TrimLabel(string value, int maxLength)
    {
        return value.Length <= maxLength
            ? value
            : string.Concat(value.AsSpan(0, maxLength - 3), "...");
    }

    private static string EscapeD2(string value)
    {
        return "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
    }

    private static string EscapeXml(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }

    private static void DeleteGraphArtifacts(string textFilePath)
    {
        TryDelete(Path.ChangeExtension(textFilePath, ".d2"));
        TryDelete(Path.ChangeExtension(textFilePath, ".svg"));
        TryDelete(Path.ChangeExtension(textFilePath, ".graph_error.txt"));

        var directory = Path.GetDirectoryName(textFilePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        var stem = Path.GetFileNameWithoutExtension(textFilePath);
        foreach (var path in Directory.EnumerateFiles(directory, $"{stem}_*.d2")
                     .Concat(Directory.EnumerateFiles(directory, $"{stem}_*.svg"))
                     .Concat(Directory.EnumerateFiles(directory, $"{stem}_*.graph_error.txt")))
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex(@"^func\s+(?<name>[^\s{]+)\s*\{")]
    private static partial Regex FunctionHeaderRegex();

    [GeneratedRegex(@"^(?<name>bb\d+):$")]
    private static partial Regex BasicBlockRegex();

    [GeneratedRegex(@"(?:\bgoto\s+|=>\s*)(?<name>bb\d+)\b")]
    private static partial Regex BasicBlockTargetRegex();

    private sealed record MirCfgGraph(string Title, IReadOnlyList<MirFunctionGraph> Functions);

    private sealed class MirFunctionGraph(string name)
    {
        public string Name { get; } = name;
        public List<MirBasicBlockGraph> Blocks { get; } = [];
    }

    private sealed class MirBasicBlockGraph(string name)
    {
        public string Name { get; } = name;
        public int InstructionCount { get; set; }
        public string? LastInstruction { get; set; }
        public List<string> Successors { get; } = [];
    }

    private readonly record struct SvgNodePosition(int X, int Y, int Width, int Height);
}
