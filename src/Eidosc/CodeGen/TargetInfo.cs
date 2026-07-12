using Eidosc.Diagnostic;

namespace Eidosc.CodeGen;

/// <summary>
/// 目标平台信息 - 用于交叉编译
/// </summary>
public sealed class TargetInfo
{
    /// <summary>
    /// 目标三元组 (例如: x86_64-pc-linux-gnu)
    /// </summary>
    public string Triple { get; init; } = "";

    /// <summary>
    /// CPU 架构名称 (例如: x86-64, arm64)
    /// </summary>
    public string Cpu { get; init; } = "";

    /// <summary>
    /// CPU 特性列表 (例如: +sse4.2,+avx)
    /// </summary>
    public string Features { get; init; } = "";

    /// <summary>
    /// 指针宽度 (32 或 64)
    /// </summary>
    public int PointerWidth { get; init; } = 64;

    /// <summary>
    /// 操作系统
    /// </summary>
    public TargetOs Os { get; init; }

    /// <summary>
    /// 架构
    /// </summary>
    public TargetArch Arch { get; init; }

    /// <summary>
    /// 数据布局 (LLVM 格式)
    /// </summary>
    public string DataLayout { get; init; } = "";

    /// <summary>
    /// 目标文件格式
    /// </summary>
    public TargetObjectFormat ObjectFormat { get; init; }

    /// <summary>
    /// 可执行文件扩展名
    /// </summary>
    public string ExecutableExtension => Os switch
    {
        TargetOs.Windows => ".exe",
        _ => ""
    };

    /// <summary>
    /// 目标文件扩展名
    /// </summary>
    public string ObjectExtension => Os switch
    {
        TargetOs.Windows => ".obj",
        _ => ".o"
    };

    /// <summary>
    /// 库文件扩展名
    /// </summary>
    public string LibraryExtension => Os switch
    {
        TargetOs.Windows => ".lib",
        TargetOs.MacOS => ".dylib",
        _ => ".so"
    };

    #region 预定义目标

    /// <summary>
    /// x86-64 Linux 目标
    /// </summary>
    public static TargetInfo X86_64Linux { get; } = new()
    {
        Triple = "x86_64-pc-linux-gnu",
        Cpu = "x86-64",
        Features = "+sse,+sse2,+sse3,+sse4.1,+sse4.2,+avx",
        PointerWidth = 64,
        Os = TargetOs.Linux,
        Arch = TargetArch.X86_64,
        DataLayout = "e-m:e-p270:32:32-p271:32:32-p272:64:64-i64:64-f80:128-n8:16:32:64-S128",
        ObjectFormat = TargetObjectFormat.Elf
    };

    /// <summary>
    /// x86-64 Windows 目标
    /// </summary>
    public static TargetInfo X86_64Windows { get; } = new()
    {
        Triple = "x86_64-pc-windows-msvc",
        Cpu = "x86-64",
        Features = "+sse,+sse2,+sse3,+sse4.1,+sse4.2,+avx",
        PointerWidth = 64,
        Os = TargetOs.Windows,
        Arch = TargetArch.X86_64,
        DataLayout = "e-m:w-p270:32:32-p271:32:32-p272:64:64-i64:64-f80:128-n8:16:32:64-S128",
        ObjectFormat = TargetObjectFormat.Coff
    };

    /// <summary>
    /// x86-64 macOS 目标
    /// </summary>
    public static TargetInfo X86_64MacOS { get; } = new()
    {
        Triple = "x86_64-apple-macosx10.15",
        Cpu = "x86-64",
        Features = "+sse,+sse2,+sse3,+sse4.1,+sse4.2,+avx",
        PointerWidth = 64,
        Os = TargetOs.MacOS,
        Arch = TargetArch.X86_64,
        DataLayout = "e-m:o-p270:32:32-p271:32:32-p272:64:64-i64:64-f80:128-n8:16:32:64-S128",
        ObjectFormat = TargetObjectFormat.Macho
    };

    /// <summary>
    /// ARM64 Linux 目标
    /// </summary>
    public static TargetInfo Arm64Linux { get; } = new()
    {
        Triple = "aarch64-unknown-linux-gnu",
        Cpu = "cortex-a72",
        Features = "+neon,+crc,+crypto",
        PointerWidth = 64,
        Os = TargetOs.Linux,
        Arch = TargetArch.Arm64,
        DataLayout = "e-m:e-p270:32:32-p271:32:32-p272:64:64-i8:8:32-i16:16:32-i64:64-i128:128-n32:64-S128-Fn32",
        ObjectFormat = TargetObjectFormat.Elf
    };

    /// <summary>
    /// ARM64 macOS (Apple Silicon) 目标
    /// </summary>
    public static TargetInfo Arm64MacOS { get; } = new()
    {
        Triple = "arm64-apple-macosx11",
        Cpu = "apple-m1",
        Features = "+neon,+crc,+crypto,+fp16",
        PointerWidth = 64,
        Os = TargetOs.MacOS,
        Arch = TargetArch.Arm64,
        DataLayout = "e-m:o-p270:32:32-p271:32:32-p272:64:64-i8:8:32-i16:16:32-i64:64-i128:128-n32:64-S128-Fn32",
        ObjectFormat = TargetObjectFormat.Macho
    };

    /// <summary>
    /// 默认目标（自动检测当前平台）
    /// </summary>
    public static TargetInfo Default
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return Environment.Is64BitOperatingSystem
                    ? X86_64Windows
                    : throw new NotSupportedException(DiagnosticMessages.ThirtyTwoBitWindowsUnsupported);
            }
            else if (OperatingSystem.IsMacOS())
            {
                return Environment.Is64BitOperatingSystem
                    ? (System.Runtime.Intrinsics.Arm.ArmBase.IsSupported ? Arm64MacOS : X86_64MacOS)
                    : X86_64MacOS;
            }
            else if (OperatingSystem.IsLinux())
            {
                return Environment.Is64BitOperatingSystem
                    ? (System.Runtime.Intrinsics.Arm.ArmBase.IsSupported ? Arm64Linux : X86_64Linux)
                    : X86_64Linux;
            }

            // 默认返回 Linux x86-64
            return X86_64Linux;
        }
    }

    /// <summary>
    /// 创建当前目标平台使用 native CPU 的变体（-mcpu=native）。
    /// 通过 clang -mcpu=native 让 LLVM 自动检测当前 CPU 特性。
    /// </summary>
    public TargetInfo WithNativeCpu()
    {
        return new TargetInfo
        {
            Triple = Triple,
            Cpu = "native",
            Features = "",
            PointerWidth = PointerWidth,
            Os = Os,
            Arch = Arch,
            DataLayout = DataLayout,
            ObjectFormat = ObjectFormat
        };
    }

    /// <summary>
    /// 从字符串解析目标
    /// </summary>
    public static TargetInfo Parse(string targetString)
    {
        if (TryParse(targetString, out var targetInfo))
        {
            return targetInfo;
        }

        throw new ArgumentException(DiagnosticMessages.UnknownTarget(
            targetString,
            string.Join(", ", GetSupportedTargetStrings())));
    }

    /// <summary>
    /// 尝试从字符串解析目标
    /// </summary>
    public static bool TryParse(string? targetString, out TargetInfo targetInfo)
    {
        targetInfo = null!;

        if (string.IsNullOrWhiteSpace(targetString))
        {
            return false;
        }

        var normalized = targetString.Trim();
        if (TryParseAlias(normalized, out targetInfo))
        {
            return true;
        }

        foreach (var knownTarget in EnumerateKnownTargets())
        {
            if (string.Equals(knownTarget.Triple, normalized, StringComparison.OrdinalIgnoreCase))
            {
                targetInfo = knownTarget;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 返回支持的目标字符串（包含别名和完整 triple）
    /// </summary>
    public static IReadOnlyList<string> GetSupportedTargetStrings()
    {
        return
        [
            "x86_64-linux",
            "x64-linux",
            "linux-x64",
            "x86_64-pc-linux-gnu",
            "x86_64-windows",
            "x64-windows",
            "windows-x64",
            "x86_64-pc-windows-msvc",
            "x86_64-macos",
            "x64-macos",
            "macos-x64",
            "x86_64-apple-macosx10.15",
            "arm64-linux",
            "aarch64-linux",
            "aarch64-unknown-linux-gnu",
            "arm64-macos",
            "aarch64-macos",
            "arm64-apple-macosx11"
        ];
    }

    private static IEnumerable<TargetInfo> EnumerateKnownTargets()
    {
        yield return X86_64Linux;
        yield return X86_64Windows;
        yield return X86_64MacOS;
        yield return Arm64Linux;
        yield return Arm64MacOS;
    }

    private static bool TryParseAlias(string normalizedTarget, out TargetInfo targetInfo)
    {
        targetInfo = normalizedTarget.ToLowerInvariant() switch
        {
            "x86_64-linux" or "x64-linux" or "linux-x64" => X86_64Linux,
            "x86_64-windows" or "x64-windows" or "windows-x64" => X86_64Windows,
            "x86_64-macos" or "x64-macos" or "macos-x64" => X86_64MacOS,
            "arm64-linux" or "aarch64-linux" => Arm64Linux,
            "arm64-macos" or "aarch64-macos" => Arm64MacOS,
            _ => null!
        };

        return targetInfo != null;
    }

    #endregion
}

/// <summary>
/// 目标操作系统
/// </summary>
public enum TargetOs
{
    Linux,
    Windows,
    MacOS,
    FreeBSD,
    Unknown
}

/// <summary>
/// 目标架构
/// </summary>
public enum TargetArch
{
    X86,
    X86_64,
    Arm,
    Arm64,
    RiscV32,
    RiscV64,
    Wasm32,
    Unknown
}

/// <summary>
/// 目标文件格式
/// </summary>
public enum TargetObjectFormat
{
    Elf,    // Linux, FreeBSD
    Coff,   // Windows
    Macho,  // macOS, iOS
    Wasm    // WebAssembly
}
