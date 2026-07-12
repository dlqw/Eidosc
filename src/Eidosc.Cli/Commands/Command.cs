namespace Eidosc.Cli.Commands;

/// <summary>
/// 编译目标格式
/// </summary>
public enum CompileTarget
{
    /// <summary>仅词法分析</summary>
    Tokens,
    /// <summary>语法树</summary>
    Ast,
    /// <summary>名称解析后</summary>
    Resolved,
    /// <summary>类型推断后</summary>
    Typed,
    /// <summary>高级中间表示</summary>
    Hir,
    /// <summary>中间中间表示</summary>
    Mir,
    /// <summary>LLVM IR 文本</summary>
    LlvmIr,
    /// <summary>本地可执行文件</summary>
    Native,
    /// <summary>完整编译</summary>
    Cil
}

/// <summary>
/// 编译阶段
/// </summary>
public enum CompilePhase
{
    /// <summary>词法分析</summary>
    Lexer,
    /// <summary>语法分析</summary>
    Parser,
    /// <summary>名称解析</summary>
    Namer,
    /// <summary>类型推断</summary>
    Types,
    /// <summary>效应推断</summary>
    Effects,
    /// <summary>借用检查</summary>
    Borrow,
    /// <summary>HIR 构建</summary>
    Hir,
    /// <summary>MIR 构建</summary>
    Mir,
    /// <summary>LLVM IR 生成</summary>
    Llvm,
    /// <summary>代码生成</summary>
    CodeGen
}

public enum BuildMode
{
    Dev,
    Release
}

/// <summary>
/// 编译配置
/// </summary>
public sealed class CompileOptions
{
    /// <summary>输入文件路径</summary>
    public string InputFile { get; set; } = "";

    /// <summary>输出文件/目录路径</summary>
    public string? OutputPath { get; set; }

    /// <summary>编译目标</summary>
    public CompileTarget Target { get; set; } = CompileTarget.Cil;

    /// <summary>停止阶段</summary>
    public CompilePhase? StopAtPhase { get; set; }

    /// <summary>调试输出目录</summary>
    public string? DebugOutputPath { get; set; }

    /// <summary>调试级别</summary>
    public Eidosc.Debug.DebugLevel DebugLevel { get; set; } = Eidosc.Debug.DebugLevel.Normal;

    /// <summary>是否生成 CFG</summary>
    public bool EmitCfg { get; set; }

    /// <summary>是否使用彩色输出</summary>
    public bool UseColors { get; set; } = true;

    /// <summary>是否显示详细输出</summary>
    public bool Verbose { get; set; }

    /// <summary>显式模块导入搜索根目录</summary>
    public string[] ImportSearchRoots { get; set; } = [];
}
