using Eidosc.Cli.Resources;
using Eidosc.Cli.Lsp;
using Eidosc.Debug;
using Eidosc.Diagnostic;
using Eidosc.Doc;
using Eidosc.Interpreter;
using Eidosc.Pipeline;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class LspLocalizationResourceTests
{
    [Fact]
    public void CliResources_AreEmbeddedInCliAssembly()
    {
        var resources = typeof(LspServer).Assembly.GetManifestResourceNames();

        Assert.Contains("Eidosc.Cli.Resources.CliResources.resources", resources);
    }

    [Fact]
    public void DiagnosticResources_AreEmbeddedInCompilerAssembly()
    {
        var resources = typeof(IdeLocalizedText).Assembly.GetManifestResourceNames();

        Assert.Contains("Eidosc.Diagnostic.DiagnosticResources.resources", resources);
    }

    [Fact]
    public void DocResources_AreEmbeddedInCompilerAssembly()
    {
        var resources = typeof(MarkdownDocRenderer).Assembly.GetManifestResourceNames();

        Assert.Contains("Eidosc.Doc.DocResources.resources", resources);
    }

    [Fact]
    public void DebugResources_AreEmbeddedInCompilerAssembly()
    {
        var resources = typeof(ConsoleDebugEmitter).Assembly.GetManifestResourceNames();

        Assert.Contains("Eidosc.Debug.DebugResources.resources", resources);
    }

    [Fact]
    public void IdeLocalizedText_ResolvesDefaultDocumentation()
    {
        Assert.Equal("Proof `safe_dir_rejects_reverse`.", IdeLocalizedText.ProofDocumentation("safe_dir_rejects_reverse"));
        Assert.Equal("Value `score`.", IdeLocalizedText.ValueDocumentation("score"));
    }

    [Fact]
    public void CliMessages_ResolveCliIdeAndReplResources()
    {
        Assert.Equal("Eidos 语言编译器", CliMessages.CliRootDescription);
        Assert.Equal("输出 IDE 使用的语义快照（JSON）", CliMessages.IdeCommandDescription);
        Assert.Equal("编译 Eidos 源代码", CliMessages.BuildCommandDescription);
        Assert.Equal("分析源代码并输出诊断信息", CliMessages.AnalyzeCommandDescription);
        Assert.Equal("Compilation error: boom", CliMessages.ReplCompilationError("boom"));
        Assert.Equal(
            "选项 '--target-name' 只能与 '--project'、项目路径输入或当前项目目录一起使用。",
            CliMessages.ProjectTargetNameRequiresProjectInput);
        Assert.Equal("项目配置 eidos.toml", CliMessages.ProjectConfigSummary("eidos.toml"));
        Assert.Equal("找不到源文件 'missing.eidos'", CliMessages.SourceFileNotFound("missing.eidos"));
        Assert.Equal("phase Mir, target Native", CliMessages.PhaseTargetDetails("Mir", "Native"));
        Assert.Equal("Checking", CliMessages.IdeCheckingAction);
        Assert.Equal("None", CliMessages.PhaseNone);
        Assert.Equal("显示编译器信息", CliMessages.InfoCommandDescription);
        Assert.Equal("版本: 1.2.3", CliMessages.InfoVersionLine("1.2.3"));
        Assert.Equal("Add a dependency to eidos.toml", CliMessages.PkgAddCommandDescription);
        Assert.Equal("Added dependency: Std.Json", CliMessages.PkgAddedDependency("Std.Json"));
        Assert.Contains("eidosc build src/main.eidos --emit-llvm", CliMessages.HelpBuildExamples);
        Assert.Equal("Package management", CliMessages.PkgCommandDescription);
        Assert.Equal("创建新的 Eidos 项目目录", CliMessages.NewCommandDescription);
        Assert.Equal("package in C:\\tmp\\demo", CliMessages.ProjectPackageInSubject("C:\\tmp\\demo"));
        Assert.Equal("编译并运行 Eidos 可执行目标", CliMessages.RunCommandDescription);
        Assert.Equal("生成详细的调试输出", CliMessages.DebugCommandDescription);
        Assert.Equal("格式化 Eidos 源代码", CliMessages.FmtCommandDescription);
        Assert.Equal("从 Eidos 源码生成 API 文档", CliMessages.DocCommandDescription);
        Assert.Equal("批量运行固定基准集 profiling 并输出汇总表", CliMessages.ProfileBatchCommandDescription);
        Assert.Equal("目标 'app' 不是 executable，不能 run。", CliMessages.RunTargetNotExecutable("app"));
        Assert.Equal("fmt: source file not found: missing.eidos", CliMessages.FmtSourceFileNotFound("missing.eidos"));
        Assert.Equal("文档已生成: api.md", CliMessages.DocGeneratedStatus("api.md"));
        Assert.Equal("case 数量 3, 采样轮数 5, 预热轮数 1", CliMessages.ProfileBatchCaseCountStatus(3, 5, 1));
        Assert.Equal("[cyan]选择操作[/]", CliMessages.TuiSelectActionTitle);
        Assert.Equal("📁 src", CliMessages.TuiDirectoryChoice("src"));
        Assert.Equal("eidos.log (2026-05-31 09:30)", CliMessages.TuiLogFileChoice("eidos.log", new DateTime(2026, 5, 31, 9, 30, 0)));
        Assert.Equal("最小 (Minimal)", CliMessages.TuiDebugLevelMinimalChoice);
        Assert.Equal("词法分析 (lexer)", CliMessages.TuiAnalyzePhaseLexerChoice);
        Assert.Equal("TUI workspace C:\\tmp\\demo", CliMessages.TuiWorkspaceSubject("C:\\tmp\\demo"));
        Assert.Equal("Finished", CliMessages.FinishedAction);
        Assert.Equal("LLVM IR", CliMessages.ArtifactKindLlvmIr);
        Assert.Equal("profile snapshot", CliMessages.ArtifactKindProfileSnapshot);
        Assert.Equal("phase Mir", CliMessages.PhaseFinishedDetail("Mir"));
        Assert.Equal("build in 42ms (ok)", CliMessages.CommandFinishedMessageWithDetails("build", "42ms", "ok"));
    }

    [Fact]
    public void DiagnosticMessages_ResolveCoreCompilerResources()
    {
        Assert.Equal(
            "Send check failed in main: spawn argument type does not implement Send: Ref[Int]",
            DiagnosticMessages.SendCheckFailed(
                "main",
                DiagnosticMessages.SpawnArgumentTypeMustImplementSend("Ref[Int]")));
        Assert.Contains("zero-capture function", DiagnosticMessages.CfnFromCapturedClosureUnsupported);
        Assert.Equal("Module 'Std.Missing' not found", DiagnosticMessages.ModuleNotFound("Std.Missing"));
        Assert.Equal(
            "Trait 'Show' expects 1 type argument(s) in an impl clause, got 0",
            DiagnosticMessages.TraitExpectsTypeArgumentsInImpl("Show", 1, 0));
        Assert.Equal(
            "`ref` can only borrow from a stable place. Temporary expressions are not borrowable.",
            DiagnosticMessages.BorrowRequiresStablePlace("ref"));
        Assert.Equal("Tuple size mismatch: 2 vs 3", DiagnosticMessages.TupleSizeMismatch(2, 3));
        Assert.Equal("Undefined type 'Missing'", DiagnosticMessages.UndefinedType("Missing"));
        Assert.Equal("Undefined trait 'Eq'", DiagnosticMessages.UndefinedTrait("Eq"));
        Assert.Equal("Undefined effect 'Logger'", DiagnosticMessages.UndefinedEffect("Logger"));
        Assert.Equal(
            "Undefined effect 'App.Logger'. Available effects named 'Logger': Std.Logger",
            DiagnosticMessages.UndefinedEffectWithCandidates("App.Logger", "Logger", "Std.Logger"));
        Assert.Equal(
            "Ambiguous effect 'Logger'. Candidates: Std.Logger, App.Logger. Use a module-qualified effect path.",
            DiagnosticMessages.AmbiguousEffectWithCandidates("Logger", "Std.Logger, App.Logger"));
        Assert.Equal(
            "Symbol 'map' not found in imported module",
            DiagnosticMessages.SymbolNotFoundInImportedModule("map"));
        Assert.Equal("note", DiagnosticMessages.DiagnosticLevelLabel(DiagnosticLevel.Note));
        Assert.Equal("<memory>", DiagnosticMessages.DiagnosticMemoryFilePath);
        Assert.Equal("see: https://example.test/help", DiagnosticMessages.DiagnosticSuggestionHelpUrl("https://example.test/help"));
        Assert.Equal(
            "Open dynamic type 'T0' reached LLVM lowering as 'T42'.",
            DiagnosticMessages.OpenDynamicTypeReachedLlvmLowering("T0", new TypeId(42)));
        Assert.Equal("dependency phase is incomplete", DiagnosticMessages.DependencyPhaseIncomplete);
    }

    [Fact]
    public void DebugMessages_ResolveDebugOutputResources()
    {
        Assert.Equal("[12:4] ", DebugMessages.SourceSpanPrefix(12, 4));
        Assert.Equal(
            $"[2026-05-31 09:30:00.000] Phase started: Types{Environment.NewLine}",
            DebugMessages.PhaseStartedLogLine("2026-05-31 09:30:00.000", "Types"));
        Assert.Equal("Finished phase: Mir", DebugMessages.FinishedPhase("Mir"));
    }

    [Fact]
    public void PipelineMessages_ResolveProfilingResources()
    {
        Assert.Equal("Error: boom", PipelineMessages.TokenError("boom"));
        Assert.Equal("<EOF>", PipelineMessages.TokenEof);
        Assert.Equal("Comment \"note\"", PipelineMessages.TokenComment("note"));
        Assert.Equal("## Hotspot Summary", PipelineMessages.ProfilingHotspotSummaryHeader);
        Assert.Equal("| Phase | Current | Baseline | Δ ms | Δ % |", PipelineMessages.ProfilingComparisonTimeTableHeader);
        Assert.Equal(
            "- Worst phase time regression: `Types` +12.30 ms (+4.50%).",
            PipelineMessages.ProfilingWorstPhaseTimeRegressionLine("Types", "+12.30", "+4.50%"));
        Assert.Equal("无法解析 profiling 快照: baseline.json", PipelineMessages.ProfilingSnapshotParseFailed("baseline.json"));
    }

    [Fact]
    public void DocMessages_ResolveDocumentationRendererResources()
    {
        Assert.Equal("# Module `Std.Seq`", DocMessages.MarkdownModuleHeader("Std.Seq"));
        Assert.Equal("#### Fields", DocMessages.MarkdownFieldsHeader);
        Assert.Equal("Std.Seq — Documentation", DocMessages.HtmlTitle("Std.Seq"));
        Assert.Equal("Module <code>Std.Seq</code>", DocMessages.HtmlModuleHeader("Std.Seq"));
    }

    [Fact]
    public void InterpreterMessages_ResolveRuntimeDisplayResources()
    {
        Assert.Equal("<function(x, y)>", InterpreterMessages.RuntimeFunctionDisplay("x, y"));
        Assert.Equal("<builtin:print_int>", InterpreterMessages.RuntimeBuiltinFunctionDisplay("print_int"));
    }
}

