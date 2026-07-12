namespace Eidosc.CodeGen.Llvm;

/// <summary>
/// LLVM IR 模块
/// </summary>
public sealed class LlvmModule
{
    /// <summary>
    /// 模块名称
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 源文件名
    /// </summary>
    public string SourceFilename { get; init; } = "";

    /// <summary>
    /// 函数列表
    /// </summary>
    public List<LlvmFunction> Functions { get; init; } = [];

    /// <summary>
    /// 全局变量声明
    /// </summary>
    public List<LlvmGlobal> Globals { get; init; } = [];

    /// <summary>
    /// 外部声明
    /// </summary>
    public List<LlvmDeclaration> Declarations { get; init; } = [];

    /// <summary>
    /// 通过 link 指令声明的外部库名称列表（传递给链接器 -l 参数）
    /// </summary>
    public List<string> LinkLibraries { get; init; } = [];

    /// <summary>
    /// 外部库搜索路径（传递给链接器 -L 参数）。
    /// </summary>
    public List<string> LinkLibraryPaths { get; init; } = [];

    /// <summary>
    /// 原生 C 源文件，随 LLVM 目标文件一起编译链接。
    /// </summary>
    public List<string> NativeSources { get; init; } = [];

    /// <summary>
    /// 原生 C 源文件的 include 搜索路径（传递给 clang -I 参数）。
    /// </summary>
    public List<string> NativeIncludePaths { get; init; } = [];

    /// <summary>
    /// 额外链接参数。
    /// </summary>
    public List<string> LinkerFlags { get; init; } = [];

    /// <summary>
    /// 模块中引用的具名结构体类型（从 TypeLowering 收集）。
    /// 这些类型定义会被无条件输出到 IR（即使没有 GEP 指令引用它们）。
    /// </summary>
    public List<LlvmStructType> NamedStructTypes { get; init; } = [];

    /// <summary>
    /// 属性 (属性组)
    /// </summary>
    public List<LlvmAttributeGroup> AttributeGroups { get; init; } = [];

    /// <summary>
    /// 命名全局值
    /// </summary>
    public Dictionary<string, LlvmGlobal> NamedGlobals { get; } = [];

    /// <summary>
    /// 获取或创建全局值
    /// </summary>
    public LlvmGlobal GetOrCreateGlobal(string name, LlvmType type)
    {
        if (NamedGlobals.TryGetValue(name, out var existing))
            return existing;

        var global = new LlvmGlobal { Name = name, Type = type };
        Globals.Add(global);
        NamedGlobals[name] = global;
        return global;
    }
}

/// <summary>
/// LLVM IR 函数
/// </summary>
public sealed class LlvmFunction
{
    /// <summary>
    /// 函数名称
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 返回类型
    /// </summary>
    public LlvmType ReturnType { get; init; } = LlvmVoidType.Instance;

    /// <summary>
    /// 参数列表
    /// </summary>
    public List<LlvmParameter> Parameters { get; init; } = [];

    /// <summary>
    /// 基本块列表
    /// </summary>
    public List<LlvmBasicBlock> BasicBlocks { get; init; } = [];

    /// <summary>
    /// 是否声明 (而非定义)
    /// </summary>
    public bool IsDeclaration { get; init; }

    /// <summary>
    /// 链接属性
    /// </summary>
    public LlvmLinkage Linkage { get; init; } = LlvmLinkage.External;

    /// <summary>
    /// 调用约定
    /// </summary>
    public string? CallingConvention { get; set; }

    /// <summary>
    /// 入口块
    /// </summary>
    public LlvmBasicBlock? EntryBlock => BasicBlocks.FirstOrDefault();

    /// <summary>
    /// 属性 ID
    /// </summary>
    public List<int> AttributeIds { get; init; } = [];
}

/// <summary>
/// LLVM IR 函数参数
/// </summary>
public sealed class LlvmParameter
{
    /// <summary>
    /// 参数类型
    /// </summary>
    public LlvmType Type { get; init; } = LlvmVoidType.Instance;

    /// <summary>
    /// 参数名称
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 参数属性
    /// </summary>
    public List<LlvmParameterAttribute> Attributes { get; init; } = [];
}

/// <summary>
/// LLVM IR 基本块
/// </summary>
public sealed class LlvmBasicBlock
{
    /// <summary>
    /// 块标签
    /// </summary>
    public string Label { get; init; } = "";

    /// <summary>
    /// 指令列表
    /// </summary>
    public List<LlvmInstruction> Instructions { get; init; } = [];

    /// <summary>
    /// 终止指令
    /// </summary>
    public LlvmTerminator? Terminator { get; set; }

    /// <summary>
    /// 添加指令
    /// </summary>
    public void AddInstruction(LlvmInstruction instruction)
    {
        Instructions.Add(instruction);
    }
}

/// <summary>
/// LLVM IR 全局变量
/// </summary>
public sealed class LlvmGlobal : LlvmValue
{
    /// <summary>
    /// 全局名称
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 初始值
    /// </summary>
    public LlvmValue? Initializer { get; init; }

    /// <summary>
    /// 链接类型
    /// </summary>
    public LlvmLinkage Linkage { get; init; } = LlvmLinkage.External;

    /// <summary>
    /// 是否常量
    /// </summary>
    public bool IsConstant { get; init; }

    /// <summary>
    /// 对齐
    /// </summary>
    public int Alignment { get; init; }

    public override string ToIrString() => $"@{Name}";
}

/// <summary>
/// LLVM IR 外部声明
/// </summary>
public sealed class LlvmDeclaration
{
    /// <summary>
    /// 声明名称
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 声明类型
    /// </summary>
    public LlvmType Type { get; init; } = LlvmVoidType.Instance;

    /// <summary>
    /// 声明来源
    /// </summary>
    public LlvmDeclarationOrigin Origin { get; init; } = LlvmDeclarationOrigin.Unspecified;
}

public enum LlvmDeclarationOrigin
{
    Unspecified,
    RuntimeIntrinsic,
    UnresolvedExternal,
    ExternalFfi,
    LlvmIntrinsic
}

/// <summary>
/// LLVM 链接类型
/// </summary>
public enum LlvmLinkage
{
    Private,
    Internal,
    External,
    AvailableExternally,
    LinkOnce,
    Weak,
    Common,
    Appending,
    ExternWeak,
    LinkOnceOdr,
    WeakOdr
}

/// <summary>
/// LLVM 参数属性
/// </summary>
public enum LlvmParameterAttribute
{
    ZeroExt,
    SignExt,
    InReg,
    ByVal,
    InAlloca,
    SRet,
    Align,
    NoAlias,
    NoCapture,
    NoFree,
    Nest,
    Returned,
    NonNull,
    Dereferenceable,
    DereferenceableOrNull,
    SwiftSelf,
    SwiftError,
    ImmArg,
    ReadOnly,
    WriteOnly
}

/// <summary>
/// LLVM 属性组
/// </summary>
public sealed class LlvmAttributeGroup
{
    public int Id { get; init; }
    public List<string> Attributes { get; init; } = [];
}
