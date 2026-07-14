<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/assets/eidos-lockup-dark.svg">
    <source media="(prefers-color-scheme: light)" srcset="docs/assets/eidos-lockup.svg">
    <img src="docs/assets/eidos-lockup.svg" width="330" alt="Eidos 猫头鹰图形与字标">
  </picture>
</p>

<p align="center">
  <strong>一门处于实验阶段、拥有函数式核心与类型化元编程能力的静态类型原生语言。</strong>
</p>

<p align="center">
  <a href="README.md">English</a> ·
  <a href="README.zh-CN.md">简体中文</a>
</p>

<p align="center">
  <a href="https://github.com/dlqw/Eidosc/actions/workflows/ci.yml"><img alt="持续集成" src="https://github.com/dlqw/Eidosc/actions/workflows/ci.yml/badge.svg?branch=main"></a>
  <img alt="状态：预发布" src="https://img.shields.io/badge/status-prerelease-c9654f">
  <a href="LICENSE"><img alt="许可证：MIT" src="https://img.shields.io/badge/license-MIT-7aa2f7"></a>
</p>

Eidos 将面向表达式的语言设计、基于 LLVM 的原生编译流程和一组协调一致的
第一方开发工具结合在一起。本仓库包含 Eidos 的参考编译器与命令行工具链
**Eidosc**，以及标准库、Eidosup 工具链管理器、C 绑定生成器和语言服务。

> [!IMPORTANT]
> Eidos 目前仍处于预发布阶段，语言与工具接口可能在预发布版本之间发生变化，
> 包括不兼容变化。当前语言基线为 **Eidos 0.6.0-alpha.1**；Eidos 语言、
> Eidosc、Std、Eidosup 与 Bindgen 采用相互独立的版本号。

## 为什么选择 Eidos？

- **富有表现力的函数式核心。** 代数数据类型、模式匹配、trait、高阶类型（HKT）
  和面向表达式的控制流被设计为彼此协作的一套语言能力。
- **类型化编译期编程。** 纯 CTFE、值级常量泛型、只读反射和用户自定义结构化
  derive 直接生成经过检查的声明，而不是拼接源代码字符串。
- **明确的能力边界。** 构建程序显式声明输入、工具、步骤和输出；普通编译期
  求值不会隐式获得宿主环境访问能力。
- **一体化工具链。** 编译器、项目模型、格式化器、文档生成器、诊断和语言服务
  共享一致的语言语义。
- **原生代码输出。** Eidosc 经由 HIR、MIR 和 LLVM 完成降级，并结合 Eidos
  运行时与 Clang/LLVM 生成原生程序。

## 先看一段 Eidos

Eidos 使用名称优先的声明形式，并以模式分支表达数据处理：

```eidos
Shape :: type
{
    Circle(Int),
    Rectangle(Int, Int)
}

area_hint :: Shape -> Int
{
    Circle(radius) => radius * radius,
    Rectangle(width, height) => width * height
}

main :: Unit -> Int
{
    _ => area_hint(Rectangle(6, 7))
}
```

可以从[简体中文教程][tutorial-zh]开始系统学习，也可以查阅
[English tutorial][tutorial-en]。

## 构建并体验 Eidosc

### 环境要求

- [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0)
- 用于原生代码生成和原生集成测试的 Clang/LLVM

### 从源码开始

```powershell
git clone https://github.com/dlqw/Eidosc.git
cd Eidosc
dotnet build Eidosc.sln --nologo

# 创建并运行一个最小项目。
dotnet run --project src/Eidosc.Cli -- new hello-eidos --name dev.eidos.hello
dotnet run --project src/Eidosc.Cli -- run hello-eidos
```

运行 `dotnet run --project src/Eidosc.Cli -- --help` 可以查看当前 CLI 的完整入口。
关于预发布分发、安装、发布通道、完整性验证和环境配置，请参阅
[Eidosup 引导文档](docs/eidosup/bootstrap.md)。

常用开发命令：

| 任务 | 命令 |
| --- | --- |
| 构建解决方案 | `dotnet build Eidosc.sln --nologo` |
| 运行测试套件 | `dotnet test src/Eidosc.Tests/Eidosc.Tests.csproj --nologo` |
| 分析文件或项目 | `dotnet run --project src/Eidosc.Cli -- analyze <path>` |
| 格式化文件 | `dotnet run --project src/Eidosc.Cli -- fmt <file>` |
| 启动语言服务器 | `dotnet run --project src/Eidosc.Cli -- lsp` |
| 查看编译器与 Std 信息 | `dotnet run --project src/Eidosc.Cli -- info --stdlib` |

## 工具链组成

| 组件 | 用途 |
| --- | --- |
| **Eidosc** | 编译器前端、类型系统、HIR/MIR 流水线、借用分析、LLVM 后端、格式化器、文档生成器和语言服务 |
| **Eidosc CLI** | `eidosc` 命令行界面、项目与包工作流、IDE 快照和 LSP 服务器 |
| **Eidos Std** | 随 Eidos 工具链分发并独立版本化的标准库 |
| **Eidosup** | 管理经过验证的不可变 Eidos 工具链、组件、profile 和 target 的安装器与管理器 |
| **Eidosc.Bindgen** | 从 C 头文件生成 Eidos 包绑定 |

## 仓库结构

| 路径 | 内容 |
| --- | --- |
| [`src/Eidosc`](src/Eidosc) | 编译器、项目系统、标准库源码、运行时源码与原生代码生成 |
| [`src/Eidosc.Cli`](src/Eidosc.Cli) | CLI、项目命令、REPL/TUI、IDE 服务与 LSP 服务器 |
| [`src/Eidosup`](src/Eidosup) | 工具链安装器与管理器 |
| [`src/Eidosc.Bindgen`](src/Eidosc.Bindgen) | C 绑定生成器 |
| [`src/Eidosc.Tests`](src/Eidosc.Tests) | 编译器、CLI、运行时和工具测试 |
| [`src/Eidosc.Benchmarks`](src/Eidosc.Benchmarks) | 可重复运行的编译器与语言服务基准测试 |
| [`docs`](docs) | 编译器架构和版本匹配的 Eidosup 文档 |
| [`eng`](eng) | 各组件的权威版本属性 |
| [`scripts`](scripts) | 验证、发布和性能自动化脚本 |
| [`changelogs`](changelogs) | 按版本组织的发布说明与开发中 changelog fragment |

## 学习与探索

| 资源 | 简体中文 | English |
| --- | --- | --- |
| 语言教程 | [阅读教程][tutorial-zh] | [Read the tutorial][tutorial-en] |
| 语法参考 | [BNF][grammar-zh] | [BNF][grammar-en] |
| 编译器架构 | — | [Overview](docs/architecture/compiler-overview.md) |
| Eidosup | — | [Bootstrap](docs/eidosup/bootstrap.md) · [Toolchain management](docs/eidosup/toolchain-management.md) · [Components, profiles, and targets](docs/eidosup/components-profiles-targets.md) |
| 编辑器支持 | [Visual Studio Code](https://github.com/dlqw/vscode-eidosc) | [Visual Studio Code](https://github.com/dlqw/vscode-eidosc) |
| 发布说明 | [Changelogs](changelogs) | [Changelogs](changelogs) |

## 参与贡献

欢迎为 Eidos 的语言、编译器、工具、测试和文档贡献改进。请先阅读
[CONTRIBUTING.md](CONTRIBUTING.md)，了解分支、代码风格、测试与拉取请求流程。

一次典型的贡献包括：

1. 从最新 `main` 创建范围明确的短生命周期分支；
2. 为可观察行为新增或更新测试；
3. 运行与变更相关的本地验证命令；
4. 创建面向 `main` 的拉取请求，说明动机、行为和已完成的验证。

可以通过 [GitHub Issues](https://github.com/dlqw/Eidosc/issues) 提交可复现的缺陷
或功能提案。疑似安全漏洞请按照 [SECURITY.md](SECURITY.md) 中的私密流程报告，
不要创建公开 issue。

## 许可证

Eidosc 基于 [MIT License](LICENSE) 开源。

Copyright © 2026 rdququ.

[tutorial-en]: https://github.com/dlqw/eidos-tutorial/blob/main/README.en.md
[tutorial-zh]: https://github.com/dlqw/eidos-tutorial/blob/main/README.zh-CN.md
[grammar-en]: https://github.com/dlqw/eidos-tutorial/blob/main/BNF.en.md
[grammar-zh]: https://github.com/dlqw/eidos-tutorial/blob/main/BNF.zh-CN.md
