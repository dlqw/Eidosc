namespace Eidosc.Pipeline;

/// <summary>
/// 编译阶段
/// </summary>
public enum CompilationPhase
{
    /// <summary>词法分析</summary>
    Lexer,
    /// <summary>语法分析</summary>
    Parser,
    /// <summary>名称解析</summary>
    Namer,
    /// <summary>类型推断</summary>
    Types,
    /// <summary>能力推断 (Effect Inference)</summary>
    Effects,
    /// <summary>借用检查</summary>
    Borrow,
    /// <summary>Send 检查 (multithreading: spawn 捕获类型安全性)</summary>
    Send,
    /// <summary>HIR 构建</summary>
    Hir,
    /// <summary>MIR 构建</summary>
    Mir,
    /// <summary>LLVM IR 生成</summary>
    Llvm
}

/// <summary>
/// 编译目标格式
/// </summary>
public enum CompilationTarget
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
    /// <summary>完整编译</summary>
    Cil
}

/// <summary>
/// 编译配置
/// </summary>
public sealed class CompilationOptions
{
    internal Types.BuildComptimeContext? BuildComptimeContext { get; set; }

    /// <summary>输入文件路径</summary>
    public string InputFile { get; set; } = "";

    /// <summary>源文件语法版本。</summary>
    public string LanguageVersion { get; set; } = ProjectSystem.EidosLanguageVersions.DefaultForExistingProjects;

    /// <summary>项目 target 指定的入口函数名。为空时沿用历史入口识别规则。</summary>
    public string? EntryFunctionName { get; set; }

    /// <summary>
    /// 允许 <see cref="InputFile"/> 作为逻辑入口文件路径使用，即使该文件不存在于磁盘。
    /// </summary>
    public bool AllowVirtualInputFile { get; set; }

    /// <summary>输出文件/目录路径</summary>
    public string? OutputPath { get; set; }

    /// <summary>编译目标</summary>
    public CompilationTarget Target { get; set; } = CompilationTarget.Cil;

    /// <summary>停止阶段</summary>
    public CompilationPhase? StopAtPhase { get; set; }

    /// <summary>调试输出目录</summary>
    public string? DebugOutputPath { get; set; }

    /// <summary>是否在写入调试产物前重建调试输出目录。</summary>
    public bool CleanDebugOutput { get; set; }

    /// <summary>调试级别</summary>
    public Debug.DebugLevel DebugLevel { get; set; } = Debug.DebugLevel.Normal;

    /// <summary>调试图产物格式。</summary>
    public Debug.DebugGraphFormat DebugGraphFormat { get; set; } = Debug.DebugGraphFormat.None;

    /// <summary>是否生成 CFG</summary>
    public bool EmitCfg { get; set; }

    /// <summary>是否启用 MIR 优化</summary>
    public bool EnableMirOptimizations { get; set; } = true;

    /// <summary>是否使用彩色输出</summary>
    public bool UseColors { get; set; } = true;

    /// <summary>是否输出 help 级调用风格建议。</summary>
    public bool EmitStyleSuggestions { get; set; }

    /// <summary>是否显示详细输出</summary>
    public bool Verbose { get; set; }

    /// <summary>LLVM 目标三元组 (例如: x86_64-pc-linux-gnu)</summary>
    public string? LlvmTargetTriple { get; set; }

    /// <summary>Capability-constrained Build host input/output fingerprint.</summary>
    public string? BuildHostFingerprint { get; set; }

    /// <summary>Canonical BuildGraph fingerprint.</summary>
    public string? BuildGraphFingerprint { get; set; }

    /// <summary>本地可执行文件链接模式。</summary>
    public CodeGen.NativeLinkMode NativeLinkMode { get; set; } = CodeGen.NativeLinkMode.NonPieExecutable;

    /// <summary>LLVM/native 后端优化级别，用于 codegen artifact identity。</summary>
    public int LlvmOptimizationLevel { get; set; } = 2;

    /// <summary>LLVM/native 后端是否启用 LTO，用于 codegen artifact identity。</summary>
    public bool LlvmEnableLto { get; set; }

    /// <summary>是否启用详细性能分析（子阶段计时/内存/GC）。</summary>
    public bool EnableDetailedProfiling { get; set; }

    /// <summary>是否记录确定性的编译期调用、反射查询和缓存 trace。</summary>
    public bool TraceComptime { get; set; }

    /// <summary>每次顶层编译期求值允许消耗的最大 fuel。</summary>
    public long ComptimeFuelBudget { get; set; } = Types.ComptimeResourceBudget.DefaultFuel;

    /// <summary>每次顶层编译期求值允许保留的最大 canonical value 字节数。</summary>
    public long ComptimeAllocatedValueBytesBudget { get; set; } = Types.ComptimeResourceBudget.DefaultAllocatedBytes;

    /// <summary>每次顶层编译期求值允许发出的最大诊断数量。</summary>
    public int ComptimeDiagnosticBudget { get; set; } = Types.ComptimeResourceBudget.DefaultDiagnosticCount;

    /// <summary>
    /// 允许在 MIR 指纹完全不变时，用 previous LLVM envelope/fragments 直接恢复 LLVM IR 文本。
    /// 当前仅适用于 LlvmIr 输出；Native object-groups 使用 <see cref="AllowNativeObjectGroupRestore"/>。
    /// </summary>
    public bool AllowLlvmIrTextRestore { get; set; }

    /// <summary>
    /// 允许 Native object-groups 在 MIR 指纹完全不变时，直接复用 previous LLVM envelope/fragments/plan。
    /// </summary>
    public bool AllowNativeObjectGroupRestore { get; set; }

    /// <summary>
    /// Allows exact-input same-process restoration of live compiler state for Namer/Types/HIR/MIR.
    /// This does not enable disk artifact live-state restore.
    /// </summary>
    public bool EnableLiveStateCache { get; set; }

    /// <summary>将所有 warning 视为 error。</summary>
    public bool TreatWarningsAsErrors { get; set; }

    /// <summary>将指定 warning code 视为 error。</summary>
    public HashSet<string> WarningCodesAsErrors { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 模块导入搜索根目录。调用方可直接显式提供；若为空，则工作区解析会先尝试最近祖先
    /// eidos.toml 的 sourceRoots / importRoots，再回退到默认的 workspace 导入根推断规则。
    /// </summary>
    public string[] ImportSearchRoots { get; set; } = [];

    /// <summary>
    /// package 别名到该 package 可导入源码根的映射。用于解析 package-qualified import，
    /// 例如 import crypto::hash/sha256。
    /// </summary>
    public Dictionary<string, string[]> PackageImportRoots { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 是否启用增量编译（模块级指纹缓存 + 依赖图失效传播）
    /// </summary>
    public bool EnableIncrementalCompilation { get; set; }

    /// <summary>
    /// 编译任务的最大并行度。小于等于 0 时使用逻辑处理器数量。
    /// </summary>
    public int MaxDegreeOfParallelism { get; set; }

    /// <summary>
    /// 增量编译缓存目录。为空时使用系统临时目录。
    /// </summary>
    public string? CacheDirectory { get; set; }

    /// <summary>
    /// 上一次构建保存的模块语义签名快照，用于生成模块失效计划。
    /// </summary>
    public ProjectModuleSemanticSignatureSnapshot? PreviousModuleSemanticSignatureSnapshot { get; set; }

    /// <summary>
    /// 上一次构建保存的类型化模块语义签名快照，用于生成 ABI/trait/layout 级失效计划。
    /// </summary>
    public ProjectModuleTypedSemanticSnapshot? PreviousModuleTypedSemanticSnapshot { get; set; }

    /// <summary>
    /// 上一次构建保存的模块成员索引快照，用于观察 Namer/member index 跨构建变化。
    /// </summary>
    public ProjectModuleMemberIndexSnapshot? PreviousModuleMemberIndexSnapshot { get; set; }

    /// <summary>
    /// 上一次构建保存的 per-module Namer live-state payload，用于 Namer 阶段入口恢复。
    /// </summary>
    public IReadOnlyList<ModuleNamerStatePayload>? PreviousModuleNamerStatePayloads { get; set; }

    /// <summary>
    /// 上一次构建保存的 per-module Types live-state payload，用于后续 Types 阶段入口恢复。
    /// </summary>
    public IReadOnlyList<ModuleTypesStatePayload>? PreviousModuleTypesStatePayloads { get; set; }

    /// <summary>
    /// 上一次构建保存的 per-module HIR live-state payload，用于后续 HIR 阶段入口恢复。
    /// </summary>
    public IReadOnlyList<ModuleHirStateArtifactPayload>? PreviousModuleHirStatePayloads { get; set; }

    /// <summary>
    /// 上一次构建保存的 per-module MIR live-state payload，用于后续 MIR 阶段入口恢复。
    /// </summary>
    public IReadOnlyList<ModuleMirStateArtifactPayload>? PreviousModuleMirStatePayloads { get; set; }

    /// <summary>
    /// 上一次构建保存的模块组合依赖签名快照，用于 gate module artifact restore。
    /// </summary>
    public ProjectModuleDependencySignatureSnapshot? PreviousModuleDependencySignatureSnapshot { get; set; }

    /// <summary>
    /// 上一次构建保存的 impl overlap/coherence registration query 快照。
    /// </summary>
    public Semantic.ImplOverlapCheckSnapshot? PreviousImplOverlapCheckSnapshot { get; set; }

    /// <summary>
    /// 上一次构建保存的 MIR 函数指纹快照，用于跨构建统计函数级变化。
    /// </summary>
    public Mir.MirFunctionFingerprintSnapshot? PreviousMirFunctionFingerprintSnapshot { get; set; }

    /// <summary>
    /// 上一次构建保存的 LLVM 函数指纹快照，用于跨构建统计函数级变化。
    /// </summary>
    public CodeGen.Llvm.LlvmFunctionFingerprintSnapshot? PreviousLlvmFunctionFingerprintSnapshot { get; set; }

    /// <summary>
    /// 上一次构建保存的 LLVM 函数 IR fragment 快照，用于统计可复用 fragment 边界。
    /// </summary>
    public CodeGen.Llvm.LlvmFunctionFragmentSnapshot? PreviousLlvmFunctionFragmentSnapshot { get; set; }

    /// <summary>
    /// 上一次构建保存的 LLVM module envelope，用于 LlvmIr 文本恢复。
    /// </summary>
    public CodeGen.Llvm.LlvmModuleEnvelopeSnapshot? PreviousLlvmModuleEnvelopeSnapshot { get; set; }

    /// <summary>
    /// 上一次构建保存的 LLVM codegen unit plan，用于 Native object-groups restore。
    /// </summary>
    public CodeGen.Llvm.LlvmCodegenUnitPlanSnapshot? PreviousLlvmCodegenUnitPlanSnapshot { get; set; }

    /// <summary>
    /// 上一次构建保存的 type-directed callable resolution 快照。仅用于按 canonical 名称回映射当前候选。
    /// </summary>
    public Types.TypeDirectedCallableResolutionSnapshot? PreviousTypeDirectedCallableResolutionSnapshot { get; set; }

    /// <summary>
    /// 上一次构建保存的 associated type projection 快照。仅用于稳定 projection query 的跨构建观测和缓存预热。
    /// </summary>
    public Types.AssociatedTypeProjectionSnapshot? PreviousAssociatedTypeProjectionSnapshot { get; set; }

    /// <summary>
    /// 上一次构建保存的 associated const projection 快照。仅用于稳定 projection query 的跨构建观测和缓存预热。
    /// </summary>
    public Types.AssociatedConstProjectionSnapshot? PreviousAssociatedConstProjectionSnapshot { get; set; }

    /// <summary>
    /// 上一次构建保存的 Send 函数级分析快照。仅在当前 MIR 函数 body hash 与 Send 依赖 hash 均匹配时恢复。
    /// </summary>
    public SendAnalysisSnapshot? PreviousSendAnalysisSnapshot { get; set; }

    /// <summary>
    /// 上一次构建保存的 Borrow 诊断快照。仅用于 Borrow stop target 的保守恢复。
    /// </summary>
    public BorrowDiagnosticSnapshot? PreviousBorrowDiagnosticSnapshot { get; set; }

    /// <summary>
    /// 上一次构建保存的 Borrow/Perceus/Reuse/StackPromotion codegen hints 快照。
    /// </summary>
    public BorrowCodegenHintsSnapshot? PreviousBorrowCodegenHintsSnapshot { get; set; }

    /// <summary>
    /// 上一次构建保存的 ground trait check 快照。仅用于无类型变量的稳定 trait 查询。
    /// </summary>
    public Types.TraitCheckSnapshot? PreviousTraitCheckSnapshot { get; set; }

    /// <summary>
    /// 查询模块级 artifact 是否已经存在。当前只用于 detailed profile/readiness 观测，
    /// 不启用实际编译跳过。
    /// </summary>
    public Func<string, string, string, string, bool>? ModuleArtifactAvailability { get; set; }

    /// <summary>
    /// Loads a persisted per-module semantic artifact node. Used only after readiness checks
    /// have selected a restore candidate.
    /// </summary>
    public Func<string, string, string, string, ProjectModuleSemanticSignatureNode?>? ModuleSemanticArtifactLoader { get; set; }

    /// <summary>
    /// Loads a persisted per-module Namer live-state payload.
    /// </summary>
    public Func<string, string, string, string, ModuleNamerStatePayload?>? ModuleNamerStatePayloadLoader { get; set; }

    /// <summary>
    /// Loads a persisted per-module Types live-state payload.
    /// </summary>
    public Func<string, string, string, string, ModuleTypesStatePayload?>? ModuleTypesStatePayloadLoader { get; set; }

    /// <summary>
    /// Loads a persisted per-module HIR live-state payload.
    /// </summary>
    public Func<string, string, string, string, ModuleHirStateArtifactPayload?>? ModuleHirStatePayloadLoader { get; set; }

    /// <summary>
    /// Loads a persisted per-module MIR live-state payload.
    /// </summary>
    public Func<string, string, string, string, ModuleMirStateArtifactPayload?>? ModuleMirStatePayloadLoader { get; set; }

    /// <summary>
    /// Loads a persisted per-module typed semantic artifact node.
    /// </summary>
    public Func<string, string, string, string, ProjectModuleTypedSemanticNode?>? ModuleTypedSemanticArtifactLoader { get; set; }

    /// <summary>
    /// Loads a persisted per-module MIR artifact node.
    /// </summary>
    public Func<string, string, string, string, ProjectModuleMirArtifactNode?>? ModuleMirArtifactLoader { get; set; }

    /// <summary>
    /// eidos.toml 中声明的 FFI 链接库名称。
    /// 编译期与源码级 link 声明合并。
    /// </summary>
    public string[] ConfigFfiLibraries { get; set; } = [];

    /// <summary>
    /// eidos.toml 中声明的 FFI 库搜索路径。
    /// </summary>
    public string[] ConfigFfiLibraryPaths { get; set; } = [];

    /// <summary>
    /// eidos.toml 中声明的 FFI C 头文件搜索路径。
    /// </summary>
    public string[] ConfigFfiIncludePaths { get; set; } = [];

    /// <summary>
    /// eidos.toml 中声明的、需要随模块一起编译链接的原生 C 源文件。
    /// </summary>
    public string[] ConfigFfiNativeSources { get; set; } = [];

    /// <summary>
    /// eidos.toml 中声明的额外链接参数。
    /// </summary>
    public string[] ConfigFfiLinkerFlags { get; set; } = [];

    /// <summary>
    /// 是否禁用自动导入 Std::Prelude。为 false 时，编译器会为每个非标准库模块
    /// 自动注入 <c>import Std::Prelude</c>，使核心类型和函数无需显式导入即可使用。
    /// </summary>
    public bool NoImplicitPrelude { get; set; }

}
