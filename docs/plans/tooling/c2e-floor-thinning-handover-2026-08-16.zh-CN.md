# C2E 地板压薄：交接文档（2026-08-16）

> 目标：把"raylib 经 C2E 翻译为 Eidos 库"从当前的混合形态推向高覆盖率翻译 + 最薄地板；
> 建立 C2E 对 OS 二进制边界（第一层）的自动识别机制；补齐 Eidos 语言侧缺口。
> 前置会话已完成结构体按值桥、全局桥、float ABI shim、extern 回退等，见
> `changelogs/0.9.0-alpha.2/2026-08-16-0.9.0-alpha.2-c2e-struct-bridge-and-raylib.md`。

> **进度（同日第二会话）**：§2 L1 识别机制与 §4.1 位运算符**已落地**（分支
> `feat/c2e-translator-extensions`，提交 8da5888/9543edb/61cbb0a + 位运算提交）。
> 本文档 §0/§3 数据已刷新为第二会话后基线；§2/§4.1 保留原文并在标题标注【已完成】。

> **进度（第三会话，2026-08-17，语言/编译器侧）**：`projects/ecc` 模板**首次完整编译通过**
>（201 个借用错误 → 0，两个 `EccMainTemplate_*` 门转绿），根因是三处所有权分析缺陷
>（提交 856ea26）：(1) 借用按活性终结——自递归尾调用转循环后守卫别名绕回边累积成
> MutateWhileBorrowed 误报（183 处）；(2) 内联器对 Ref/MutRef 参数改借用绑定
>（原物化为 move，8 处移动链误报）；(3) 可靠 Seq 判别器——Seq 索引投影按运行时语义
> 是 IncRef 独立副本，不再挂 owning-aggregate 借用别名（#84 的聚合投影严格性不变，
> deref 负例仍报错）。另确认 §4.2 语言侧**早已存在**（`Ffi.offset_bytes`/
> `load[T]`/`store[T]`/`ptr_add` intrinsic 全链路在库），缺的只是 C2E 侧数组下标接线。
> 已知失败剩 1 个：#85 并发压力 soak flake。
> **通用性收尾（同会话，提交 b9a6436）**：活性终结推广为语言级机制——三条管线的
> 优化器构建点共享 Ref/MutRef 解析器；经典 BorrowChecker 与 LoanConstraintVerifier
> 的**全部**冲突路径统一经 `BorrowLivenessGate` 剪枝（借用者在冲突指令自身被消费
> 视为存活）；Seq 判别器局部类型表缓存化并覆盖嵌套/解引用基。语义确定：
> "比较后重绑定"（`mut cond := x == "hello"; x := "world"`）合法（借用者已死），
> 相应负例夹具转为正例，字段/索引敏感锚点改为存活借用者形态仍断言 E1002。

## 0. 当前状态（新会话先读这段）

> **进度（2026-08-19 第四会话，门全线通过）**：raylib 四真实 TU 合并产物
> **654 函数，`--gate-phase Llvm` 全后端门 PASSED（0 错误）**——即 parse →
> namer → types → effects → borrow → MIR → LLVM 全链路通过（changelog
> `2026-08-19-...-c2e-context-coercions-and-int-as-ptr.md`）。修复分四轮：
> (1) 编译器侧 `LiteralExpr.ParseLiteral` 对 `l` 后缀落入 String 兜底（37 错
> 的根因）+ 进制分支 long 回退；(2) 语境强转全套：指针±int/指针差/指针序比较/
> NULL 跨语境（ParenExpr 解包）/`int_as_ptr` intrinsic（MAKEINTRESOURCE 形态）/
> Bool↔Int 数字化/复合赋值 Float 提升/Float→Int 截断/字符串下标基；(3) 结构体
> 按值参数跨 ABI：`_v`/`_sret` C 包装 shim 指针收参 + 调用点 calloc staging 槽
> 逐字段装载（嵌套记录经 `_addr` 递归）；(4) 门深入到 Effects/Borrow 相位暴露的
> 漏标：extern 调用方 need ffi 不能靠字典计数差（TryAdd 对重复 extern 不增长）、
> `(float)x` cast 未 tick、翻译记录 `@[derive(Copy)]`（C 值语义，否则按值传参即
> move，104 个 E1001 一行清除）、accessor 登记 NeedsAddress 合并而非覆写、驱动
> 合并对 accessor 块按 `@[extern]` 声明对去重（get/set/addr 同块不同 TU 子集）。
> 诚实跳过新增：变参内部调用（stbiw__outfile 的 fmt 驱动 va_list）、函数名值用
> （glad 回调）。对拍门新增 `C2E_PointerArithmeticAndCoercions_ParityWithClang`
> （12/12）。验证全套：snake anchor 837999808 复现、ecc 编译冒烟 exit 42、
> 全量回归 4318/4319（仅 #85 已知 flake）。
>
> **下一会话开工项**：门内 0 错 ≠ 能跑——距离"转译即最新版 Eidos 程序"还差：
> (a) 原生链接 + 运行（eidos.toml floorSymbols/原生 shim 已生成，需真实链
> raylib.dll 与 CRT 符号后跑冒烟）；(b) 翻译率本身（各 TU 仍有约 1/3 函数因
> 未支持构造跳过：goto/labels、函数指针值、static 局部、指派初始化——tier-2
> 清单）；(c) 变参与 va_list、位域、longjmp 等 floor 决策；(d) 编译器侧登记项：
> 诊断行号在 CRLF/混合换行文件上漂移（renderer 的行映射）、namer 大模块病态
> 性能（22k 行 ~6 分钟）、效果推断覆盖缺口（§4.4）。



- **可运行样板**：`projects/snake-gui-c2e`（游戏逻辑与 bindgen 版逐字节一致，无头校验和一致，
  GUI 已冒烟）。图形包 `projects/bindings/raylib-c2e`（`regen.sh` 一键重生成翻译层，
  自动把地板符号清单写进 `eidos.toml [ffi].floorSymbols`）。
- **驱动工具**：`tools/c2e`（dotnet；`--report` 出逐函数可行性矩阵 + 三级分类统计
  （translated / floor-extern / cross-TU），`--only` 选入口，`-I/-D/--isystem` 编译环境，
  `--floor-out` 落地板清单）。
- **翻译率现状**（真实 raylib 源，2026-08-18 第一档构造全量落地后）：
  rcore 821/1335（61%）、rshapes 112/162（69%）、rtext 604/766（79%）、
  rtextures 754/1238（61%）、raymath 168/201（84%，真实 skip 10→7；计数口径
  含 accessor 声明，值成员读取不再发射 accessor 后计数下降非回退）。
  地板分类：rcore 157/42，rshapes 11/8，rtext 75/28，rtextures 96/80，raymath 14/0。
- **git 状态**：第一会话的未提交改动已在分支 `feat/c2e-translator-extensions` 提交
  （翻译器扩展 + 交接文档 + L1 识别）；第二会话的位运算改动见该分支后续提交。
  工作区（repo 外）：`projects/bindings/raylib-c2e/`、`projects/snake-gui-c2e/`、
  `tools/c2e/`、`AGENTS.md` 注册行。**分支未推送**（push 需代理，见 §7）。
- **C2E 测试基线**：11/11（2026-08-18 新增第一档综合对拍门
  `C2E_TierOneConstructs_ParityWithClang`：switch/do-while/for 形态/break-continue/
  参数可变/字符字面量/窄化 cast/嵌套成员路径/cast 基/成员取址/条件位赋值/sret/2D 数组）。
  全量回归基线：4316/4317（唯一既知失败 #85 soak flake，基线提交复现验证过）。

## 1. 三层地板模型（本会话结论，指导后续取舍）

| 层 | 定义 | 处置 |
|---|---|---|
| L1 OS 二进制边界 | 系统 DLL 里的机器码入口（Win32/GL/libc），**没有 C 源码** | 永远 extern(c)；目标是最薄声明集（§2 自动识别） |
| L2 有源码的 C 胶水 | rcore 平台层、rlgl、rtext 等，被具体构造缺口挡住 | 逐项补翻译器能力（§3） |
| L3 不该翻的热路径 | rlgl 顶点缓冲等刻意裸内存代码 | 翻译零收益/负收益；显式标注留地板（§5） |

## 2. L1 识别机制设计【已完成，提交 61cbb0a】

**原理**：clang 对"系统头"与"项目头"本就有区分——`-I`（项目） vs `-isystem`（系统）。
系统头里的声明没有实现源码可翻，恰好就是二进制边界。libclang 提供
`clang_Location_isInSystemHeader(CXSourceLocation) -> unsigned` 直接读取该标记。

**实施现状**（全部落地，changelog `2026-08-16-...-c2e-l1-floor-identification.md`）：

1. `ClangNative` 增加导出与封装：
   - delegate `ClangLocationIsInSystemHeaderFn(ClangSourceLocation) -> int`；
   - `ClangApi` 属性 `LocationIsInSystemHeader`；
   - `CBodyTranslator.IsSystemDeclaration(ClangCursor)`：取 `GetCursorLocation(cursor)` 判定。
   （`ClangGetCursorLocationFn` 已存在，见 `ClangNative.cs` 既有导出。）
2. 驱动层（`tools/c2e`）新增 `--isystem <dir>` 参数，映射到 clang 的 `-isystem` 传参
   （`ClangSession.Parse` 的 `extraArgs` 已支持透传，只需在 `arguments` 里追加）。
   raylib 场景：`-I refs/raylib/src`（项目源，可翻）+ `--isystem` 指向 Windows SDK/UCRT
   （clang 默认已把 SDK 当系统头，多数情况无需显式传）。
3. 翻译器三级分类（`Translate` 收集函数/记录时一次性标注）：
   - `body 在非系统头/TU 内` → 候选翻译（现状）；
   - `无 body 且声明位置在系统头` → **L1 地板 extern**：进 `PendingExtern`，标注
     `Floor=true`，包生成时单独输出"地板符号清单"（extern 声明集 = 链接契约）；
   - `无 body、声明在项目头`（跨 TU）→ 同样 extern，但标记 `CrossTU=true`（供人工核对
     是否该把它所在 TU 也纳入翻译）。
4. 包清单落地：`bindings/*/eidos.toml` 的 `[ffi]` 增加可选 `floorSymbols = [...]`，
   由 regen 脚本从生成器的地板清单写入；构建期校验"翻译层引用的外部符号 ⊆ 声明的地板集"，
   防止隐性依赖扩散（链接器已兜底，此清单是文档化 + 审查面）。
5. **验证门**：`--report` 输出增加三列分类统计（translated / floor-extern / skipped-reason），
   作为每个库的"地板厚度"度量，CI 可追踪压薄进度。

## 3. L2 剩余 C2E 工程项（按实测阻塞量排序）

数据为四模块（rcore/rshapes/rtext/rtextures）全量 skip 聚合
（2026-08-17 数组下标/局部缓冲/sizeof/语句位自增已消化；changelog
`2026-08-17-...-c2e-array-subscript-and-typed-pointer-access.md`）：

| # | 阻塞 | 量 | 实施要点 |
|---|---|---|---|
| 1 | ~~数组下标 `a[i]`（指针算术）~~ | 已完成 | 元素地址 = `Ffi.offset_bytes(base)(i * sizeof(T))` + 按宽度选择的 load/store 变体（i64/i32/i16/i8/f32/ptr）；读写/复合赋值/自增/`&a[i]`/嵌套下标/记录元素成员访问全链路 |
| 2 | ~~局部 `T a[N]` 不可映射~~ | 已完成 | RawPtr 堆缓冲：无初始化器 `Ffi.malloc(N*S)`、带初始化列表 `Ffi.calloc` + 逐元素 store；另修复既有 `*p` 解引用的宽度错误（`Ffi.load[Int]` 是 i64，C int 走 `load_i32`） |
| 3 | ~~字符串字面量~~ | 已完成 | 见 2026-08-16 changelog（`Ffi.to_c_string` 边界转换） |
| 4 | 一元取址 `&x`（含 `&global`） | 47 | `&record局部` 需 §4.3 语言侧取址；`&global`（模块 mut 绑定）可先行：模块 mut 的地址经 accessor/全局桥获取 |
| 5 | 非记录指针基的成员访问（`p->f` 基类型解析失败） | 99 | 多为 void*/强制转换后的指针；部分随 §4.2 指针算术与 CastExpr 解包消化 |
| 6 | ~~语句位 `++`/`--`（含 `a[i]++` 后缀）~~ | 已完成 | token 扫描不取位置；值位（返回旧值）仍不支持 |
| 7 | 语句 kind 208 / for 形态 / 表达式 136/110 | 78+75+53 | kind 208 疑为 CompoundStmt 变体，需逐个确认；for 非常规形态（多初始化/空段）；136/110 待归类 |
| 8 | 嵌套成员赋值 `a.b.c = v` | 34 | 发射 `a := a.{b: a.b.{c: v}}`（需验证 Eidos 嵌套 record update 语法；先查 `TypeInferencePipelineTests` 与 snake-gui 是否已有用例） |
| 9 | ~~switch（kind 206）~~ | 已完成 | 包装循环 + 段级入口（进入后标签只执行该段起）+ 匹配闩锁；break 终止段复位闩锁；条件 break 退出包装；体内 continue 映射为 break |
| 10 | ~~extern 结构体按值返回（sret shim）~~ | 已完成 | C 侧静态槽 + 指针返回，Eidos 侧块表达式内经 accessor 重组记录；参数位按值仍挡住（调用点记录→blob 桥缺失）。`DrawRectangleLines` 的前置 `rlGetMatrixTransform` 可评估接入 |
| 11 | va_list（`TraceLog`） | 少量 | **策略性跳过**：翻译器对 va_list 原型已跳过；不建议支持，日志类函数留地板 |

## 4. L3 Eidos 语言/编译器项

### 4.1 位运算符【已完成，方案 A 全链路落地】

- **现状**：`& | ^ << >>` 已进语言（Int-only，`Int -> Int -> Int`，优先级同 C；
  `>>` 按符号性 ashr/lshr）。词法/AST/解析/类型/HIR/MIR/LLVM/解释器/comptime 全链路。
- **语法冲突处理**：`|` 与列表推导 `[e | quals]`、决策表键 `| k1 | k2` 冲突，
  经 `ParseExpr` 的 `stopAtBitBar` 模式解决（二元脊柱遇 `|` 停止，括号内复位）；
  这两个位置顶层位或需加括号。
- **C2E**：C `& | ^ << >>` 透传，`~x` 去糖 `(x ^ -1)`；对拍门
  `C2E_BitwiseOperators_ParityWithClang`（8/8）。
- changelog：`2026-08-16-0.9.0-alpha.2-bitwise-operators.md`。
- 后续（低优先）：Bool 位运算不做；unsigned 语义已由 #69 的 isUnsigned 路径覆盖右移。

### 4.2 Ffi 指针算术【已完成，2026-08-17，含宽度类型化访存】

- `Ffi.offset_bytes` 既有导出即指针算术入口；本轮新增按宽度命名的类型化访存：
  `load_i32/store_i32`、`load_i16/store_i16`、`load_i8/store_i8`（新 intrinsic
  `ptr_load/store_i16`）、`load_f32/store_f32`（新 intrinsic，fpext/fptrunc）。
  动机：`Ffi.load[Int]/store[Int]` 是 i64 存取，C int/short/char/float 元素必须用
  对应宽度变体，否则相邻元素被跨写/读到合并值（既有 `*p` 解引用的宽度缺陷一并修复）。
- `sizeof(T)` 经 clang 常量求值在翻译期折叠为字面量；元素大小同样取自
  `clang_Type_getSizeOf`（新增导出 `clang_getArrayElementType` 支撑局部数组映射）。
- 翻译器侧需注意：生成函数签名按"体内是否触及 Ffi/extern/accessor"发射 `need ffi`
  并沿内部调用图传播（效果推断不覆盖循环体/嵌套操作数位）。

### 4.3 局部取址（`&local`）

- 现状：`&` 一元被跳过。rcore 里主要用于把局部缓冲传给 Win32 API（`&msg`、`&rect`）。
- 最小方案：只支持 `&record局部` → 语言侧需要"局部可获取稳定地址"的原语
  （`Ffi.local_ref(x)`?）——**需要语言设计讨论**（与借用系统交互），交接时标记为
  设计项而非直接实现项；rcore 平台层多数场景可用"记录指针参数改传引用"绕开。

### 4.4 效果推断覆盖缺口（编译器侧已知项，2026-08-17 登记）

- 效果推断只覆盖尾表达式/直接语句位的调用；循环体、中缀操作数、嵌套块内的调用
  不参与推断，但授权检查仍会看到它们并报 E3003（callee 为泛型应用时显示
  `<unknown-callee>`，是 `ResolveCalleeDisplayName` 不认 GenericApply 的展示缺陷）。
- C2E 侧已用"按函数发射 need ffi + 调用图传播"绕开（changelog 2026-08-17 数组
  下标条目）；若后续手写 Eidos 大量遇到同类 E3003，应修推断管线而非继续手工声明。

### 4.5 其他

- C 函数指针回调（WndProc）：Eidos 已有 M4 ctx 回调先例（`cfn_ctx_from/data`，
  见 `LlvmPipelineIntegrationTests.CfnCtxCallback.cs`）。C2E 生成 extern 回调注册
  属后期项（只有平台层需要，L3 策略下可能永远不做）。
- C bool extern ABI：当前用 int64 地板 shim 规避；若后续想直连，需 extern(c) 对
  i8/i1 返回的支持（低优先）。

## 5. L3"不该翻"显式化

- `bindings/*/eidos.toml` 增加注释性 `floorKeep = ["rlgl", "rcore_desktop_win32"]` 段
  （或独立 `floor.toml`）：regen 据此对命中函数**不尝试翻译**、直接 extern，
  `--report` 单列"kept-on-floor"。防止未来翻译率数字误导（这些函数翻得动也不翻）。
- 判据（写进文档供评审）：函数体内数组写密度高 + 每帧执行 + 数据最终喂给 extern
  上传（`glBufferData` 类）→ 留地板。

## 6. 验证门（每项落地后必须跑）

1. `dotnet test Eidosc/src/Eidosc.Tests --filter "FullyQualifiedName~C2E_"`（当前 9/9）；
   新能力各配一个 clang 对拍门（模板：`C2E_StructValueBridge_ParityWithClang`）。
2. `tools/c2e --report` 四模块翻译率不得回退（基线见 §0）。
3. `projects/bindings/raylib-c2e/regen.sh` + `eidosc build projects/snake-gui-c2e` +
   `./build/main.exe --bench-steps 2000000` 必须 == **837999808**（逻辑层不变的锚点）。
4. GUI 冒烟：`timeout 6 ./build/main.exe`（exit 124 = 存活到超时）。
5. 全量回归一次（第三会话后基线：仅 1 个既知失败 = #85 并发压力 soak flake；
   #83 的 EccMainTemplate 两个测试已随借用修复转绿）。
6. `projects/ecc`：干净构建 + `./build/main.exe` 退出码 0（模板迁移完成门）。

## 7. 新会话启动清单

```bash
cd /d/Project/eidos_workspace
# 1) 分支状态：feat/c2e-translator-extensions（未推送；push 前设代理，见下）
git -C Eidosc log --oneline -5 && git -C Eidosc status --short
# 2) 构建驱动与 CLI
dotnet build tools/c2e && dotnet build Eidosc/src/Eidosc.Cli
# 3) 复现基线
tools/c2e/bin/Debug/net10.0/C2eDriver.exe tmp/c2e_raylib/probe_rcore.c \
  -I refs/raylib/src -I tmp/c2e_raylib -D "RLAPI=" --report | head -2
# 4) 按优先级开工：§4.2 Ffi 指针算术（解锁 §3-#1/#2，量最大）→ §3-#3 字符串字面量
#    → §3-#6 表达式位 ++/-- → §3-#10 sret shim；§4.3 取址是设计讨论项
```

环境备忘：gh/push 需 `export HTTPS_PROXY=http://127.0.0.1:7890 HTTP_PROXY=http://127.0.0.1:7890`；
CI 只跑 stable 子集，Network/fixture 类仅本地；`dotnet run` 偶发残留 Eidosc.Cli 进程锁 DLL，
`taskkill //F //PID <pid>` 后重试。
