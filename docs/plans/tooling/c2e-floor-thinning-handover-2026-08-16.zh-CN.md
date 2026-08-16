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

## 0. 当前状态（新会话先读这段）

- **可运行样板**：`projects/snake-gui-c2e`（游戏逻辑与 bindgen 版逐字节一致，无头校验和一致，
  GUI 已冒烟）。图形包 `projects/bindings/raylib-c2e`（`regen.sh` 一键重生成翻译层，
  自动把地板符号清单写进 `eidos.toml [ffi].floorSymbols`）。
- **驱动工具**：`tools/c2e`（dotnet；`--report` 出逐函数可行性矩阵 + 三级分类统计
  （translated / floor-extern / cross-TU），`--only` 选入口，`-I/-D/--isystem` 编译环境，
  `--floor-out` 落地板清单）。
- **翻译率现状**（真实 raylib 源，`PLATFORM_DESKTOP_WIN32 + GRAPHICS_API_OPENGL_33`，
  2026-08-16 第二会话后）：rcore 661/1326、rshapes 62/127、rtext 323/628、
  rtextures 489/1156；raymath 162/174。地板分类：rcore 148 floor / 36 cross-TU，
  rshapes 9/7，rtext 66/39，rtextures 75/30。
- **git 状态**：第一会话的未提交改动已在分支 `feat/c2e-translator-extensions` 提交
  （翻译器扩展 + 交接文档 + L1 识别）；第二会话的位运算改动见该分支后续提交。
  工作区（repo 外）：`projects/bindings/raylib-c2e/`、`projects/snake-gui-c2e/`、
  `tools/c2e/`、`AGENTS.md` 注册行。**分支未推送**（push 需代理，见 §7）。
- **C2E 测试基线**：8/8（新增分类与位运算对拍门）。全量回归基线：4308/4311，
  3 个失败应核对为既知项（#83 模板迁移 / #85 flake）。

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
（2026-08-16 第二会话后，位运算与一元 `~` 已消化；另有 452 处
"call to untranslated function" 为下游传播，上游落地自动消解，不计入下表）：

| # | 阻塞 | 量 | 实施要点 |
|---|---|---|---|
| 1 | 数组下标 `a[i]`（指针算术） | 94+ | 见 §4.2（依赖语言侧 Ffi 指针算术）；翻译层：元素地址 = `Ffi.pointer_add(base, i * sizeof(T))` + `load/store[T]`；C 数组形参（`T a[]`/`T* a`）在 MapType 已落 RawPtr，缺的是下标表达式 |
| 2 | 局部/参数不可映射类型（多为数组与数组指针） | 125+118 | 大头是 `T a[N]` 局部与 `T*` 指向数组的形参；依赖 §4.2（数组缓冲 = RawPtr + 尺寸） |
| 3 | 字符串字面量 | 101 | 映射为 Eidos `String`；在 extern `RawPtr` 实参位自动 `Ffi.to_c_string(...)`（复用 `TranslateCallArgument` 的参数映射）；同文件内传给已翻函数的场景保持 String |
| 4 | 一元取址 `&x`（含 `&global`） | 47 | `&record局部` 需 §4.3 语言侧取址；`&global`（模块 mut 绑定）可先行：模块 mut 的地址经 accessor/全局桥获取 |
| 5 | 非记录指针基的成员访问（`p->f` 基类型解析失败） | 99 | 多为 void*/强制转换后的指针；部分随 §4.2 指针算术与 CastExpr 解包消化 |
| 6 | 表达式位 `++`/`--`（`x++` 作值） | ~98 | 值位自增去糖：`let old := x; x := x + 1; old`；语句位已支持 |
| 7 | 语句 kind 208 / for 形态 / 表达式 136/110 | 78+75+53 | kind 208 疑为 CompoundStmt 变体，需逐个确认；for 非常规形态（多初始化/空段）；136/110 待归类 |
| 8 | 嵌套成员赋值 `a.b.c = v` | 34 | 发射 `a := a.{b: a.b.{c: v}}`（需验证 Eidos 嵌套 record update 语法；先查 `TypeInferencePipelineTests` 与 snake-gui 是否已有用例） |
| 9 | switch（kind 206） | 25 | 去糖为 `decide`/if-else 链：`case A: ... break;` → 分支；注意 fallthrough 与 `default`；无 break 的 fallthrough 用嵌套块拼接。键位映射表是主要用户 |
| 10 | extern 结构体按值 ABI（`rlGetMatrixTransform` 等） | 39 | 生成 sret shim：C 侧 `void __c2e_sret_F(S* out){ *out = F(); }`，Eidos 侧 extern `Unit -> RawPtr` + 生成的字段读取拼装记录（复用 accessor 机制）。落地后 `DrawRectangleLines` 可真翻译，撤掉 drawing.eidos 的四矩形兜底 |
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

### 4.2 Ffi 指针算术（解锁数组下标与 C 缓冲遍历）

- `std.Ffi` 增加：`pointer_add :: RawPtr -> Int -> RawPtr need ffi`（+ `pointer_byte_add`），
  runtime `eidos_memory.c` 实现为 `(char*)p + n`；可选 `load_at[T]`/`store_at[T]`（带偏移）。
- 安全策略：与 `load/store` 一致的 `need ffi` 能力门槛即可（翻译产物本就 `need ffi`）。
- C2E 侧据此实现 §3-#1；同时 `sizeof(T)` 需在翻译期确定（clang `TypeGetSizeOf` 已在
  `ClangApi` 里，`CursorGetType` + `GetTypeSizeOf` 可得元素大小，发射为常量）。

### 4.3 局部取址（`&local`）

- 现状：`&` 一元被跳过。rcore 里主要用于把局部缓冲传给 Win32 API（`&msg`、`&rect`）。
- 最小方案：只支持 `&record局部` → 语言侧需要"局部可获取稳定地址"的原语
  （`Ffi.local_ref(x)`?）——**需要语言设计讨论**（与借用系统交互），交接时标记为
  设计项而非直接实现项；rcore 平台层多数场景可用"记录指针参数改传引用"绕开。

### 4.4 其他

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
