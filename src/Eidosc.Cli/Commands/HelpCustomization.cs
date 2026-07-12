using System.CommandLine;
using System.CommandLine.Help;
using Eidosc.Cli.Resources;

namespace Eidosc.Cli.Commands;

/// <summary>
/// 自定义帮助输出：为各命令的枚举选项添加完整说明、Examples 和 Notes 区域。
/// </summary>
internal static class HelpCustomization
{
    /// <summary>
    /// 对给定的 HelpBuilder 应用自定义描述和布局。
    /// </summary>
    public static void Apply(HelpBuilder builder)
    {
        CustomizeBuildCommand(builder);
        CustomizeAnalyzeCommand(builder);
        CustomizeDebugCommand(builder);
        CustomizeIdeCommand(builder);
        builder.CustomizeLayout(CustomLayout);
    }

    // ------------------------------------------------------------------
    // 各命令选项的详细说明
    // ------------------------------------------------------------------

    private static void CustomizeBuildCommand(HelpBuilder builder)
    {
        BuildCommand.CustomizeHelp(builder);
    }

    private static void CustomizeAnalyzeCommand(HelpBuilder builder)
    {
        // AnalyzeCommand 的选项没有暴露引用，但可以通过遍历子命令来查找。
        // 更简洁的做法：直接用 CustomizeSymbol + 遍历子命令选项。
        // 但 System.CommandLine 的 CustomizeSymbol 按 symbol 引用匹配，
        // 所以我们需要从 AnalyzeCommand 拿到引用——类似 BuildCommand 的做法。
        // 目前 analyze/debug/ide 的枚举选项较少，通过 layout 的 Notes 来补充。
    }

    private static void CustomizeDebugCommand(HelpBuilder builder)
    {
    }

    private static void CustomizeIdeCommand(HelpBuilder builder)
    {
    }

    // ------------------------------------------------------------------
    // 自定义布局：在默认区域后追加额外信息
    // ------------------------------------------------------------------

    private static IEnumerable<HelpSectionDelegate> CustomLayout(HelpContext ctx)
    {
        // 保留所有默认区域
        foreach (var section in HelpBuilder.Default.GetLayout())
        {
            yield return section;
        }

        // 根命令：追加子命令帮助提示
        if (ctx.Command is RootCommand)
        {
            yield return new HelpSectionDelegate(WriteRootHint);
        }
        else
        {
            var name = ctx.Command.Name;
            if (name == "new")
            {
                yield return new HelpSectionDelegate(WriteNewExamples);
                yield return new HelpSectionDelegate(WriteNewNotes);
            }
            else if (name == "build")
            {
                yield return new HelpSectionDelegate(WriteBuildExamples);
                yield return new HelpSectionDelegate(WriteBuildNotes);
            }
            else if (name == "run")
            {
                yield return new HelpSectionDelegate(WriteRunExamples);
                yield return new HelpSectionDelegate(WriteRunNotes);
            }
            else if (name == "analyze")
            {
                yield return new HelpSectionDelegate(WriteAnalyzeExamples);
                yield return new HelpSectionDelegate(WriteAnalyzeNotes);
            }
            else if (name == "debug")
            {
                yield return new HelpSectionDelegate(WriteDebugNotes);
            }
            else if (name == "ide")
            {
                yield return new HelpSectionDelegate(WriteIdeNotes);
            }
        }
    }

    // ------------------------------------------------------------------
    // Root Command Hint
    // ------------------------------------------------------------------

    private static void WriteRootHint(HelpContext ctx)
    {
        ctx.Output.WriteLine();
        ctx.Output.WriteLine(CliMessages.HelpRootHint);
    }

    // ------------------------------------------------------------------
    // Build Examples
    // ------------------------------------------------------------------

    private static void WriteNewExamples(HelpContext ctx)
    {
        WriteResourceBlock(ctx, CliMessages.HelpNewExamples);
    }

    private static void WriteNewNotes(HelpContext ctx)
    {
        WriteResourceBlock(ctx, CliMessages.HelpNewNotes);
    }

    private static void WriteBuildExamples(HelpContext ctx)
    {
        WriteResourceBlock(ctx, CliMessages.HelpBuildExamples);
    }

    // ------------------------------------------------------------------
    // Build Notes
    // ------------------------------------------------------------------

    private static void WriteBuildNotes(HelpContext ctx)
    {
        WriteResourceBlock(ctx, CliMessages.HelpBuildNotes);
    }

    private static void WriteRunExamples(HelpContext ctx)
    {
        WriteResourceBlock(ctx, CliMessages.HelpRunExamples);
    }

    private static void WriteRunNotes(HelpContext ctx)
    {
        WriteResourceBlock(ctx, CliMessages.HelpRunNotes);
    }

    // ------------------------------------------------------------------
    // Analyze Examples & Notes
    // ------------------------------------------------------------------

    private static void WriteAnalyzeExamples(HelpContext ctx)
    {
        WriteResourceBlock(ctx, CliMessages.HelpAnalyzeExamples);
    }

    private static void WriteAnalyzeNotes(HelpContext ctx)
    {
        WriteResourceBlock(ctx, CliMessages.HelpAnalyzeNotes);
    }

    // ------------------------------------------------------------------
    // Debug Notes
    // ------------------------------------------------------------------

    private static void WriteDebugNotes(HelpContext ctx)
    {
        WriteResourceBlock(ctx, CliMessages.HelpDebugNotes);
    }

    // ------------------------------------------------------------------
    // IDE Notes
    // ------------------------------------------------------------------

    private static void WriteIdeNotes(HelpContext ctx)
    {
        WriteResourceBlock(ctx, CliMessages.HelpIdeNotes);
    }

    private static void WriteResourceBlock(HelpContext ctx, string text)
    {
        ctx.Output.WriteLine();
        ctx.Output.WriteLine(text);
    }
}
