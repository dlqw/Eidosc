# Eidos clang C 转译工具链计划（重建版）

- 原始日期：2026-08-13
- 重建日期：2026-08-15
- 状态：P0 已完成；P1/P2 未开工
- 说明：本文件是对原始计划的**重建**。原始计划撰写于 2026-08-13 但从未提交，
  `changelogs/0.9.0-alpha.2/2026-08-13-0.9.0-alpha.2-bindgen-clang-extract.md` 引用了本
  路径与其中 §7.2。重建依据：该 changelog、P0 落地代码与测试、以及 2026-08-15 的代码库
  现状核查。执行层面的里程碑拆解见
  `docs/plans/compiler/eidos-compiler-c-interop-roadmap-2026-08-15.zh-CN.md`（M0-M7）。

## 1. 定位

C→Eidos 工具链是 Eidos 0.9.0 的收口特性：在完整工具链（声明迁移、混合编译、函数体翻译）
落地之前，0.9.0 stable 不发布。三件套共享同一 libclang 前端：

1. **声明迁移（BindGen，`eidosc pkg bind`）**：从 C 头文件生成 Eidos 绑定包，函数体留在
   C 侧编译链接。→ P0，已落地。
2. **混合编译**：C 对象与 Eidos 编译产物在同一链接单元内做跨语言 LTO 优化。→ P1，未开工。
3. **函数体翻译（body translation，C2E）**：把 C 函数体翻译为 Eidos 函数体，产出不再依赖
   C object 的原生 Eidos 库。→ P2，未开工。

## 2. P0 — 声明迁移（已完成，2026-08-13）

按里程碑记录（代号 M1-M5，均属 P0）：

| 里程碑 | 内容 | 状态 |
| --- | --- | --- |
| M1 | 进程内 libclang P/Invoke 层（`Eidosc.Bindgen.Clang`：ClangNative/ClangSession），平台感知库发现与编译/链接用 clang 对齐 | 完成 |
| M2 | 全量声明提取器 `ClangHeaderParser` → `CHeaderIr`（include 展开、布局事实、union、typedef 链、枚举值、宏常量、函数指针 arity、变参/inline 标志、全局变量） | 完成 |
| M3 | `pkg bind` 接线 `parseMode = "clang"`（`clangDefines`/`clangArgs`/`[options]`），正则 `SimpleCHeaderParser` 保留为 `simple` 回退 | 完成 |
| M4 | 自动 C shim 扩展：struct 按值参数拆分（void*）、按值返回静态槽、窄标量窄化 | 完成 |
| M5 | 多文件项目 include 收编细化 | 部分完成（仅收编主头文件自身顶层声明，见 `ClangHeaderParser.cs` 基名匹配） |

raylib 门禁：真实 `raylib.h`、clang 模式，生成 599 绑定、6 个合法 SKIP、2400 行自动 shim
取代手写 `eidos_raylib.c`，生成模块通过完整语义管线。

## 3. P1 — 混合编译

目标：`--lto` 从链接 flag 变为真实跨语言优化。现状差距与接线面见路线图 M6
（集中在 `src/Eidosc/CodeGen/LlvmCompiler.cs`：Eidos IR 产 LTO 对象、native/runtime/shim
编译加 `-flto`、lld 链接）。

## 4. P2 — 函数体翻译（C2E）

前端底座已预留：TU 解析保留函数体（`ClangHeaderParser` 以 `skipFunctionBodies: false`
解析），缺语句/表达式游标消费层。垂直切片范围、C 定宽整数语义开放问题与对拍门见路线图
M7。

## 5. 里程碑外的语言面依赖

body translation 的可翻译面受 Eidos 语言/编译器能力约束，缺口清单与收口计划全部收敛到
路线图 M1-M5：

- 窄标量 FFI（原 §7.2 主体）：~~MIR/后端边界只支持 64 位标量~~ 已于 PR #65（dc307c4）
  修复，白名单现含全部窄标量；剩余为 trait 补齐、coerce 符号性与 bindgen 摘 shim（M1）。
- 模块级可变全局：语法/MIR/LLVM 三阶段已落地（PR #71-74）；运行时初始化扩展（M2）与
  extern(c) 全局变量声明（M3）待做。
- union：clang 布局事实已提取；指针式表示（M4a）与语言级 repr(c) union（M4b，需设计
  决策）待做。
- 捕获闭包 → Cfn：ctx-pointer 约定路径（M5）。

## 6. 历史记录

- 2026-08-13：原始计划撰写（未提交）；P0 落地（changelog
  `2026-08-13-0.9.0-alpha.2-bindgen-clang-extract.md`）。
- 2026-08-15：本重建版提交；编译器侧执行计划另立
  `docs/plans/compiler/eidos-compiler-c-interop-roadmap-2026-08-15.zh-CN.md`。
