# Eidos 编译器建设计划：C 互操作收口与 C2E 铺路

- 日期：2026-08-15
- 状态：提案（待评审）
- 范围：Eidosc 编译器本体为主，含 Bindgen 消费侧切换；C2E（body translation）仅定义垂直切片起点
- 关联：`changelogs/0.9.0-alpha.2/2026-08-13-0.9.0-alpha.2-bindgen-clang-extract.md` 的 0.9.0 收口条款
  （"declaration migration, mixed compilation, body translation" 三件套）

## 1. 背景与定位

C→Eidos 工具链是 Eidos 0.9.0 的收口特性。其中声明迁移（BindGen，`pkg bind`）的 P0 已落地并通过
raylib 门禁；混合编译与函数体翻译（C2E）未开工。C2E 的每一个 C 结构都必须能在 Eidos 中表达、
并通过语义/MIR/LLVM 管线 lower，因此本计划把"编译器建设"收窄为**C 互操作面向的缺口收尾**，
每个里程碑以"某类 C 结构可直接表达/绑定"为验收门。这不是与 C2E 竞争的另一条线，而是同一条
主线的先后段。

计划制定时的一项重要事实修正：`E5337`（MIR/后端边界只支持 64 位标量）**已在 PR #65（dc307c4）
修复**——`MirValidator.IsBuiltinLoweringType` 白名单现已含全部窄标量（`src/Eidosc/Mir/MirValidator.cs:1244-1269`），
LLVM 类型映射（`src/Eidosc/CodeGen/Llvm/TypeLowering.cs:341-367`）与语义白名单
（`src/Eidosc/Semantic/FfiTypeValidator.cs:42-52`）均已就绪。bindgen changelog 第 8 行的
"MIR/backend only supports 64-bit scalars" 描述相对主干已过时。剩余工作是端到端验证、
少量边角补齐与 bindgen 侧摘除窄化 shim。

## 2. 现状基线（2026-08-15）

### 2.1 已具备

| 能力 | 位置/证据 |
| --- | --- |
| 窄标量 MIR/LLVM/语义白名单 | MirValidator.cs:1244-1269、TypeLowering.cs:341-367、FfiTypeValidator.cs:42-52 |
| UInt 全家桶（类型/后缀/算术/转换/trait） | PR #66-70；std.UInt、std.IntNarrow、std.FloatNarrow 转换 intrinsic |
| extern(c) 函数声明全链路 | DeclarationClauseBinder.cs:299-329 → FuncSymbol 元数据 → HirBuilder.cs:255,305 → MirFunc.cs:489 → CallLowering |
| 零捕获闭包 → Cfn（qsort 实战） | MirToLlvmConverter.FfiCalls.cs:94-117；集成测试 CfnCallback_QsortNativeSmoke |
| repr(c) struct（offset 事实 + RawPtr 访问器） | NameResolver.Declarations.cs:773-858、InteropIntrinsics.cs:539-607 |
| RawPtr 操作面（add/load_as/store_as/memcpy/box） | std/ffi.eidos；ConstructorCalls.cs:118-153、ValueBoxing.cs |
| 模块级 mut（字面量初始化） | PR #71-74；MirBuilder.ModuleVariables.cs、ModuleVariables.cs、LlvmCompiler 启动链 |
| 启动序列钩子 `eidos_module_init` | LlvmCompiler.cs:699-715（入口 shim 调用）、DestructorSynthesis.cs:489-556（生成） |
| BindGen clang 提取（含 union 布局事实、全局变量） | ClangHeaderParser.cs；CHeaderIr.cs:43-53 |
| `--lto` 链接 flag 与缓存键 | LlvmCompiler.cs:458-463、:1041-1047 |

### 2.2 已知过时/待清理

- README/README.zh-CN 仍写 "0.6.0-alpha.1、组件独立版本化"，与 #50 后的统一版本线（0.9.0-alpha.1）不符。
- changelog 引用的 `docs/plans/tooling/eidos-clang-c-transpiler-plan-2026-08-13.zh-CN.md` 不在仓库中（本计划部分补位，M7 前应重建该文档）。
- 诊断码复用：字符串 `"E5302"` 同时用于模块变量初始化 fallback（ModuleVariables.cs:69-84）与函数签名角色诊断（Functions.cs:740-746），且与枚举旧名脱节。
- `src/Eidosc/Bindgen/BindingCShimGenerator.cs:79` 注释声称"窄类型无法从 Eidos 构造"，在 #67/#68/#70 后已不成立。

## 3. 里程碑总览

| 里程碑 | 主题 | 规模 | 前置 | 验收门 |
| --- | --- | --- | --- | --- |
| M0 | 在飞工作收口 + 流程清理 | S | — | 工作树干净、文档/诊断一致 |
| M1 | 窄标量 FFI 收口（编译器+bindgen 摘 shim） | M | M0 | extern(c) 窄参数/返回原生往返；raylib 门禁无窄化 shim |
| M2 | 模块级 mut 运行时初始化（E5301/E5302 放宽） | M | M0 | 非字面量初始化器 E2E 可用，初始化顺序确定 |
| M3 | extern(c) 全局变量（解 bindgen 全局 SKIP） | M/L | M2 | C 全局标量/指针在 Eidos 读写 E2E；bindgen 生成 extern 声明 |
| M4a | union 成员视图（shim 访问器 + 布局常量，零编译器改动） | S | — | union 经生成访问器可读写，raylib 门禁维持 |
| M4b | 语言级 repr(c) union | L | M4a + 设计决策 | （另行立项） |
| M4c | union→ADT 桥接（声明式标签关联 + decode/encode） | M | M4a | tagged-union 结构生成可模式匹配的 ADT |
| M5 | 捕获闭包 → Cfn（ctx-pointer 约定） | M | M0 | ctx 型回调 E2E native smoke |
| M6 | 混合编译接线（真跨语言 LTO） | S/M | M1 | --lto 产出真 LTO 对象并跨语言优化；关闭时行为不变 |
| M7 | C2E 垂直切片（body translation 起步） | L（切片 M） | M1-M6 | 小型真实 C 文件翻译产物过全管线且行为与 C 一致 |

并行长线（不阻塞主线，见 §12）：目标调试信息（DWARF）、交叉编译矩阵、lowering fallback 清理、
变参 FFI 策略。

排序依据：M1 是 C2E 最大单点依赖（C 窄整数无处不在）且已半就绪，最先做；M2/M3 对应 C 全局
状态；M4a 极便宜可随手插入；M5/M6 相对独立；M7 在前置齐备后开工。

## 4. M0 — 在飞工作收口与流程清理

当前分支 `fix/closed-case-ctor-expected-promotion` 有 8 文件修改 + 1 新增（+59/-549），内容为
三类相互独立的工作，与模块级 mut 无关：

1. **类型推断修复（分支主题）**：closed-case 构造器向 root 提升前增加期望类型 guard
   （`src/Eidosc/Types/TypeInferer.TypeConversionAndBasics.cs:923` 附近）。
2. **#74 后续缺陷修复**：fusion sink plan 选择改为按 block 分组取最小 start index
   （`SequencePipelineFusionPass.Discovery.cs:181-217`）；zip 聚合字段写入 MirMove→MirStore
   （`SequencePipelineFusionPass.DirectSink.cs:580-585`）；对应测试拆分为
   `SequencePipelineFusionPassTests.FlatMapAndZip.cs`。
3. **fixture 路径适配**：4 个测试文件的 tutorial fixture 路径改为子目录形式
   （依赖仓库外 fixture 根，本仓库内无法独立验证，需 `EIDOS_TUTORIAL_EXAMPLES_ROOT` 环境）。

动作项：

- [ ] 跑相关单测（fusion/类型推断两组）后按上述三类拆 2-3 个 commit 合入。
- [ ] 重建 `docs/plans/tooling/eidos-clang-c-transpiler-plan-2026-08-13.zh-CN.md` 或改写
      changelog 引用（M7 开工前必须完成；本计划 §11 可作为其 M7 段的种子）。
- [ ] README/README.zh-CN 版本表述更新（0.9.0-alpha.1、统一版本线）。
- [ ] 诊断码清理：`"E5302"` 三义拆分（模块变量初始化、函数签名角色各立新码或回收旧枚举名）。

明确不做：不在此里程碑引入任何新语言行为。

## 5. M1 — 窄标量 FFI 边界收口

**目标**：extern(c) 直接以 `Int32/Int16/Int8/UInt32/.../Float32/Float16` 作为参数与返回；
bindgen 停止为窄标量生成 C shim。链路（语义白名单 → MIR TypeId 原样保留 → LLVM 类型映射 →
参数 coerce）已通，本里程碑以验证为主、小补齐为辅。

动作项（按序）：

1. **E2E 集成测试（纯新增，先跑）**：仿 NativeMemoryBalance 的 native probe 模式，在
   `src/Eidosc.Tests/Integration/` 增加：`@[extern(c)] fn(Int32, Float32) -> Float32`、
   `(UInt32) -> UInt32`、`(Int8) -> Int8` 等用例；实参用 `IntNarrow.from_int32(...)`、`42u32`、
   `FloatNarrow.from_float32(...)` 构造；断言 IR `declare` 为 `i32/float` 且值原生往返正确。
   **预期不改任何编译器文件即通过**；若不通，失败点即真实缺口。
2. **窄有符号/窄浮点 trait 补齐**：`src/Eidosc/Types/BuiltinTraits.cs:56-68` 给
   Int64/32/16/8、Float64/32/16 加 `Num/Ord/Eq/Show/Clone/Copy`（对标 UInt 行），否则 extern
   返回的窄值不能直接比较/运算。
3. **coerce 符号性修复**：`src/Eidosc/CodeGen/Llvm/CallLowering.cs:1596-1610`
   `CoerceIntegerToWidth` 宽化固定 zext，应按源类型符号性选 `sext/zext`（正确原语
   `CoerceSignedToType` 已在 :1362-1398 存在）。
4. **（可选）字面量后缀**：`i8/i16/i32/f32/f16` 仿 #67 四处改动
   （NumberMatchRule.cs:218-249、LiteralExpr.cs:310-317、TypeInferer.Expressions.cs:342-352、
   TypeIdRegistry.cs:203-206）。若暂不做，std 转换函数已够 C2E 生成器使用。
5. **std 行为级测试**：IntNarrow/FloatNarrow/UInt 目前只有 intrinsic 注册测试
   （PrecompiledModuleRegistryTests.cs:104-142），补 codegen/运行时行为测试。
6. **bindgen 摘 shim**：
   - `src/Eidosc/Bindgen/BindingTypeMapper.cs:81-96`：`int/int32_t → Int32`、
     `unsigned int/uint32_t → UInt32`、`short/int16_t → Int16`、`char/int8_t/uint8_t → Int8/UInt8`、
     `float → Float32`（`long/size_t` 维持 Int64）；
   - `src/Eidosc/Bindgen/BindingCShimGenerator.cs:70-85`：`NeedsNarrowing` 对标量返回 false
     （保留 struct 拆分/静态槽 shim），更新 :79 过时注释；
   - 重跑 raylib 门禁，记录 shim 行数下降。

明确不做：C ABI 多寄存器 struct-by-value 分类（struct 按值继续走 void*/静态槽 shim）；
变参；`ptr_load_i32/i8` 固定 zext 语义（设计如此，如需变更另行评审）。

验收门：窄参数/返回 extern(c) 原生往返测试全绿；raylib 门禁中窄化 shim 为零；changelog 更新
（含对旧 §7.2 描述的勘误）。

## 6. M2 — 模块级 mut 运行时初始化（E5301/E5302 放宽）

**目标**：模块级 mut 初始化器不再限于"静态 int/float 标量"。采用**启动期初始化序列**而非
静态求值扩展——入口 shim 已调用 `eidos_module_init()`（LlvmCompiler.cs:706-713），扩展点现成；
运行时路径直接复用现有函数 lowering，无需复刻表达式语义。

现状两道闸：E5301（MIR 层，初始化器必须是 `HirLiteral`/`Neg(HirLiteral)`，
`src/Eidosc/Mir/MirBuilder.ModuleVariables.cs:116-186`）；E5302（LLVM 层，静态标量
`src/Eidosc/CodeGen/Llvm/MirToLlvmConverter.ModuleVariables.cs:50-84`）。

动作项：

1. **MIR**：`MirModuleVar` 增加 `RuntimeInitializer`（或保留 HIR 节点引用）；放宽 E5301——
   字面量走现有常量路径，非字面量合成 per-module 初始化函数 `__eidos_module_init_<module>`
   （复用 `ConvertLambdaToFunction` 基建；`MirBuilder.ModuleValues.cs:144-183` 的 getter 合成
   是现成模板）；模块变量间引用顺序复用 `DetectModuleValueCycles`（:220-285）做拓扑排序。
2. **LLVM**：运行时初始化的变量以 `zeroinitializer` 定义全局，在
   `GenerateModuleInit`（DestructorSynthesis.cs:489-556）中追加对合成 init 函数的调用或直接
   发射 store 序列；**模块变量 linkage 改为 Internal**（当前默认 External，有符号污染风险，
   LlvmModule.cs:212）。
3. **多翻译单元聚合**：`eidos_module_init` 是单一 External 符号，模块变量初始化必须与 ADT
   析构注册合并在同一 converter 单元生成（或 per-module init + 主单元聚合），避免重复定义。
4. **诊断**：E5302 仅在"既不能静态 lower、又不能走运行时路径"时报告。
5. **测试**：函数调用/聚合/字符串初始化器 E2E；初始化顺序确定性（拓扑）；现有标量路径回归；
   补 E5302 fallback 测试（当前为零覆盖）。

明确不做：模块变量持有托管值的退出期析构（后续项）；跨模块初始化顺序承诺。

## 7. M3 — extern(c) 全局变量

**目标**：解除 bindgen 的 C 全局 SKIP（`RawBindingGenerator.cs:144-146`，注释已过时），让
Eidos 直接读写 C 全局。clang 提取侧数据已具备（`CBindingGlobal`，CHeaderIr.cs:53）。

改动面（六层，均仿函数 extern 的既有路径）：

1. **语义**：`VarSymbol` 增加 `IsExternal/ExternalSymbolName/ExternalLibrary`
   （仿 `NameResolver.Declarations.cs:293-303` 的 FuncSymbol 路径）；
   `DeclarationClauseBinder`（:299-329）允许变量声明上的 extern(c) 子句（仅 ABI 'c'、
   要求 `need ffi`）。
2. **语法**：`ParseTopLevelMutBinding`（DeclParser.cs:222-251）支持**无初始化器**形式；
   extern 变量禁止携带初始化器。
3. **跨模块写路径（关键坑）**：函数体内 `name := expr` 仅当 name 在
   `ParserContext._moduleLevelMutableBindings`（ParserContext.cs:308-317）注册表内才解析为
   Assignment；**导入的模块变量不在注册表中，写路径会静默退化为局部绑定**。短期：import
   解析时回填注册表；长期（推荐）：把 assignment 消歧挪到语义层按符号可变性判定。
4. **MIR**：`MirModuleVar` 加 `IsExternal/ExternalName`。
5. **LLVM**：`LlvmGlobal` 支持 declaration-only 形态；`LlvmEmitter.EmitGlobal`
   （LlvmEmitter.cs:151-163）输出 `@name = external global T`；直接用 C 符号名（不 mangle）。
   declaration-only 是必需而非可选——否则 extern 声明被当作零初始化定义会触发 E5302 且
   多目标文件重复定义。
6. **bindgen**：全局 SKIP 换成 extern 声明（标量/指针直出；聚合与字符串先用 shim
   getter/setter 函数对兜底，`BindingCShimGenerator` 基建现成）。

验收门：C 侧 `int`/指针全局在 Eidos 读写 E2E（native smoke）；bindgen 对标量全局生成 extern
声明并通过语义管线。

## 8. M4 — union 表示

### 8.0 设计结论：C union 与 Eidos ADT 不存在表示层映射

C union 是**无标签**重叠存储（成员全部 offset 0，size = max，跨成员读 = C11 允许的
type punning）；Eidos ADT 是**带判别式**的和类型（tag + 最大 payload）。两者布局与
读取语义都不同，任何"union ↦ ADT"的直译都不健全：读方向无从得知匹配哪个构造器，
往返也不等价。因此分三层，各自回答不同的问题：

| 层 | 回答的问题 | 机制 | 语义地位 |
| --- | --- | --- | --- |
| M4a 成员视图 | 如何在 Eidos 里读写 union 内存 | shim 访问器 + size/align 常量 | 无类型存储 + 类型化成员访问（不安全面） |
| M4b repr(c) union | union 值是否按值过 FFI | 重叠布局的语言级存储类型 | 不安全的存储类型，**不是**和类型 |
| M4c ADT 桥接 | 何时可以当和类型用 | 声明式标签关联 → 生成 decode/encode | 只在 C 侧自己维护标签（enum+union 惯用法）时健全 |

C2E 翻译 union 用法时的精确规则：**同成员写后读** = 变体访问（构造器/模式匹配，健全）；
**成员写** = 变体切换（写后其它成员未指定——C 标准语义——故"丢掉"旧变体是忠实的）；
**跨成员读** = type punning，回落 M4a 原始视图，不套 ADT。

### M4a：成员视图（bindgen 侧，零编译器改动）

union 的 size/align/成员 offset/成员类型在 clang 提取侧已全部具备
（`ClangHeaderParser.ExtractUnion` + `ExtractFields` 带 per-field offset，
CHeaderIr.cs:43-47）。std.Ffi 的指针操作全部是 `compiler(internal)`，生成代码不可
调用，因此成员访问器经自动 C shim 实现：

- Eidos 侧：`@[extern(c)]` 访问器声明——`{u}_{m}_get :: RawPtr -> T`、
  `{u}_{m}_set :: RawPtr -> T -> Unit`（标量/指针成员直连，聚合成员返回成员地址
  RawPtr），外加 `{u}_size/{u}_align :: Int` 常量。
- shim 侧：成员 get/set C 函数（union 定义可见，shim 已 include 头文件）。
- 含 union 字段的 struct 按值参数、union 按值参数维持 SKIP（两个问题分离）。

### M4b：语言级 repr(c) union（另行立项，含设计决策）

重叠字段语法、未初始化读取限制、与封闭 case 类型的关系需要语言设计决策；实现面
集中在 `CollectCStructDef` + `CStructLayoutComputer`（重叠 offset）。只在出现
"union 按值过边界"的真实需求时立项。

### M4c：ADT 桥接（bindgen 生成层，声明式标签关联）

针对 C 的 tagged-union 惯用法（`struct { enum Kind kind; union Payload payload; }`），
bindgen.toml 声明标签关联后生成：

```eidos
-- enum + union 对 ↦ 单个和类型
EventValue :: type { Click(Int32), Move(Float32) }
event_value_decode :: RawPtr -> EventValue need ffi   -- 读 tag 分支 + 取成员
event_value_encode :: EventValue -> RawPtr -> Unit need ffi  -- 写 tag + 成员
```

- 标签来源必须**声明式**给出（`[[unions]]` 表：struct/tagField/payloadField/tagEnum +
  每个 variant 的 tag→member 映射），不从使用模式猜测。
- decode/encode 基于宿主 struct 的 @cstruct 点访问（tag 字段）+ M4a 成员访问器
  （payload 字段经 shim 取 `&p->payload`）。
- 变体成员限定为标量/指针映射（聚合负载要求指针成员）。
- 收益：SDL/X11/libuv 类事件结构自然成为可模式匹配的 Eidos ADT；C2E 的
  switch-on-tag 惯用法直接译为模式匹配。

## 9. M5 — 捕获闭包 → Cfn（ctx-pointer 约定）——已落地

**状态**：核心机制已实现并验证（2026-08-15）。`std.Ffi` 新增
`cfn_ctx_from`/`cfn_ctx_data` intrinsic（0..6 参重载）：闭包 invoke thunk 的 ABI
`(closure_ptr, args...)` 与 C 侧 `callback(void* ctx, args...)` 同构，
`Ffi.cfn_ctx_from(closure)` 取 invoke_fn（闭包 offset 8）作回调指针，
`Ffi.cfn_ctx_data(closure)` 取闭包对象指针作 ctx；Cfn 结果类型前置 ctx 槽位
（`Cfn[RawPtr, A..., R]`）。零捕获函数传入 `cfn_ctx_from` 报 E3053 引导包 lambda；
`cfn_from` 语义不变，其 E3053 文案已指向 `cfn_ctx_from`，报错后的死代码已移除。
闭包生命周期由调用方在 C 侧注册期间保证。

验证：IR 断言（GEP 8 + invoke_fn 加载 + 双 ptr 传参）+ 原生冒烟（C visitor
`int(*)(void*, int)` 驱动 Eidos 捕获闭包，捕获值参与每次回调，exit 123）。

**遗留后续**：bindgen 识别 `(ctx, callback)` 参数对生成惯用 wrapper（人体工学层，
机制已就绪）。

明确不做（记录为显式弃置）：libffi 式无 ctx trampoline（需闭包指针烧录/trampoline 页的
运行时新设施，成本高一个量级；真实 C 库中 ctx-pointer 约定覆盖面足够）。

## 10. M6 — 混合编译接线（真跨语言 LTO）——已落地

**状态**：已实现并验证（2026-08-15）。`--lto` 时 Eidos IR 编译路由到
`clang -x ir -c -flto`（bitcode 对象）而非 `llc -filetype=obj`（本机对象）；
`GetDefaultClangObjectCompileFlags` 在 LTO 下追加 `-flto`，统一覆盖 native FFI 源、
入口 shim、runtime 编译，并自动进入对象缓存键与后端配置哈希。链接维持 `-flto`
（Windows 强制 lld）。验证：Eidos→C extern 调用在 LTO 开/关下原生行为一致（exit 42）。

**遗留后续**：跨语言内联的强断言（符号消失/性能对比）与 `-flto=thin` 评估；
Linux/macOS 的 lld 可用性矩阵。

## 11. M7 — C2E 垂直切片（body translation 起步）——已落地（切片范围）

**状态**（2026-08-15）：`CBodyTranslator` 实现切片全集——标量算术/比较、局部变量
声明与赋值、if/else、while/for（loop+break 去糖）、return、整型/浮点字面量
（clang_Cursor_Evaluate）、同文件函数调用；不支持构造跳过并记录原因。对拍门通过：
同一 C 源经 clang 编译与翻译后编译，运行退出码一致。C 定宽溢出不建模（统一 Int）。
**后续矩阵**：指针（RawPtr）、union（M4 桥接）、extern 互操作、switch/goto、变参。


**目标**：在 M1-M6 前置齐备后，验证"C 函数体 → Eidos 函数体"翻译管线的端到端可行性。
前端底座现成：`ClangSession` 解析已保留函数体（`ClangHeaderParser.cs:34`
`skipFunctionBodies: false`），缺的只是语句/表达式游标消费层。

切片范围（刻意收窄）：

- 输入：单个小而真实的 .c 文件（不含宏黑魔法）。
- 可翻译构造：标量/指针局部变量；算术/逻辑/比较运算；`if/while/for/do-while/return/break/continue`；
  对已绑定 extern 与其他已翻译函数的调用；RawPtr 指针操作（deref/算术经 std.ffi）。
- 产出：Eidos 源文件（走正常语义管线编译，不旁路）。

关键开放问题（切片必须回答）：

1. **C 定宽整数语义**：Eidos 整数算术在 LLVM 层统一宽化到 i64（`ConvertBinOp`，
   MirToLlvmConverter.cs:1768-1782），加减乘模运算下 `回绕后截断` 与 C 定宽语义等价，但
   **除法/移位/比较在越界值上不等价**。切片内可先用"显式截断 pattern"生成；若确认长期
   需要，立项"窄整数算术 intrinsic"作为后续编译器工作项。
2. **goto/switch 跳转、联合体局部变量、字符串字面量生命周期**：切片外，登记为后续矩阵。
3. **行为对拍门**：翻译产物与 clang 编译的同一 C 文件在同一输入下输出一致（native 对拍
   harness 仿 NativeMemoryBalance 模式）。

前置条件：M0 中重建的 transpiler 计划文档必须先行（本节可作其种子）；raylib 级全量矩阵
（struct 按值、函数指针、宏、变参）不在本切片，另立计划。

## 12. 并行长线（不阻塞主线，按需插入）

| 项 | 现状 | 触发时机 |
| --- | --- | --- |
| 目标调试信息（DWARF/DIBuilder） | CodeGen 零覆盖（无任何 DIBuilder 引用） | 语言可用性诉求；与 C2E 无依赖 |
| 交叉编译矩阵 | TargetInfo.cs:86/:189 有限 triple | Eidosup target 分发需求 |
| LLVM lowering fallback 清理 | ReportUnsupportedInstruction 等防御点（MirToLlvmConverter.cs:914 起若干） | 随各里程碑顺手收缩 |
| 变参 FFI | SKIP（要求定长 wrapper） | 出现真实变参 API 绑定需求时设计 std.Varargs |
| 模块变量持有托管值的退出析构 | store 记 escape，不跟踪 | M2 之后的所有权扩展 |

## 13. 依赖与排序

```
M0 ─┬─> M1 ──┐
    ├─> M2 ─> M3 ──┤
    ├─> M5 ────────┤
    └─> M6 ────────┴─> M7（垂直切片）
M4a：任意时点插入（零编译器改动）
M4b：待语言设计决策，独立立项
```

M1 先行（半就绪、单点依赖最大）；M2→M3 串行（共享模块变量机制）；M5/M6 可与 M2/M3 并行
（不同子系统，无文件冲突面）；M7 需 M1-M6 全部或其子集验收后开工。

## 14. 风险与开放决策

| 风险/决策 | 说明 | 缓解 |
| --- | --- | --- |
| M1 集成测试可能暴露隐藏断链 | "链路已通"基于代码审查推断，未经 E2E 证实 | 第一步即写测试，失败点即工作项；UInt 全链是现成参照 |
| `eidos_module_init` 多 TU 重复定义 | 单一 External 符号 | 单 converter 单元聚合或 per-module init 聚合（M2.3） |
| 跨模块 assignment 消歧 | parser 注册表方案脆弱 | 推荐语义层符号判定；注册表仅作过渡（M3.3） |
| E5302/E5301 放宽后初始化顺序 | 模块变量互引 | 复用 DetectModuleValueCycles 拓扑；跨模块顺序不做承诺 |
| ctx-pointer 约定覆盖面 | 无 ctx 的纯回调 C API 仍不可绑 | 显式弃置 libffi 路线并记录；shim 兜底 |
| LTO 各对象 flag 一致性 | 不一致的 -O/target 使 LTO 失效或误优化 | M6.2 统一 flag 矩阵；缓存键验证 |
| C 定宽算术语义（M7） | 宽化算术与 C 语义在 div/shift/比较上不等价 | 切片用显式截断 pattern；必要时立项窄算术 intrinsic |
| fixture 根在仓库外 | `EIDOS_TUTORIAL_EXAMPLES_ROOT`/`EIDOS_TEST_PROJECT_ROOT` 依赖环境 | CI 已有；本地验证注意环境变量 |

## 15. 验证策略

- 每里程碑：单元测试 + LLVM 集成测试 + native smoke 三层；bindgen 相关里程碑追加 raylib
  门禁（真实 raylib.h，clang 模式）。
- 行为对拍：M7 引入 C/Eidos 对拍 harness；M6 用符号消失/性能断言跨语言内联。
- 测试分层沿用现有 runsettings（fast/full/native/network）；新增 native 用例归 native 层。
- 每里程碑一个 changelog fragment，含验收证据（绑定数、shim 行数、IR 断言等可量化指标）。
