namespace Eidosc.CodeGen.Llvm;

/// <summary>
/// LLVM IR 终止指令基类
/// </summary>
public abstract class LlvmTerminator
{
    /// <summary>
    /// 获取终止指令的 LLVM IR 字符串表示
    /// </summary>
    public abstract string ToIrString();

    public override string ToString() => ToIrString();
}

/// <summary>
/// 返回指令
/// </summary>
public sealed class LlvmRet : LlvmTerminator
{
    /// <summary>
    /// 返回值 (void 函数为 null)
    /// </summary>
    public LlvmValue? Value { get; init; }

    public override string ToIrString()
    {
        if (Value == null || Value.Type is LlvmVoidType)
            return "ret void";
        return $"ret {Value.Type.ToIrString()} {Value.ToIrString()}";
    }
}

/// <summary>
/// 无条件跳转指令
/// </summary>
public sealed class LlvmBr : LlvmTerminator
{
    /// <summary>
    /// 目标基本块
    /// </summary>
    public LlvmBasicBlock Target { get; init; } = new() { Label = WellKnownStrings.InternalNames.Entry };

    public override string ToIrString() => $"br label %{Target.Label}";
}

/// <summary>
/// 条件跳转指令
/// </summary>
public sealed class LlvmCondBr : LlvmTerminator
{
    /// <summary>
    /// 条件值 (i1)
    /// </summary>
    public LlvmValue Condition { get; init; } = LlvmConstant.Zero;

    /// <summary>
    /// 真分支目标
    /// </summary>
    public LlvmBasicBlock ThenBlock { get; init; } = new() { Label = WellKnownStrings.Keywords.Then };

    /// <summary>
    /// 假分支目标
    /// </summary>
    public LlvmBasicBlock ElseBlock { get; init; } = new() { Label = WellKnownStrings.Keywords.Else };

    public override string ToIrString() =>
        $"br i1 {Condition.ToIrString()}, label %{ThenBlock.Label}, label %{ElseBlock.Label}";
}

/// <summary>
/// Switch 指令
/// </summary>
public sealed class LlvmSwitch : LlvmTerminator
{
    /// <summary>
    /// 判断值
    /// </summary>
    public LlvmValue Value { get; init; } = LlvmConstant.Zero;

    /// <summary>
    /// 默认目标块
    /// </summary>
    public LlvmBasicBlock DefaultBlock { get; init; } = new() { Label = "default" };

    /// <summary>
    /// case 列表 (值 -> 块)
    /// </summary>
    public List<(LlvmConstant Value, LlvmBasicBlock Block)> Cases { get; init; } = [];

    public override string ToIrString()
    {
        var casesStr = string.Join(", ", Cases.Select(c => $"{c.Value.Type.ToIrString()} {c.Value.ToIrString()}, label %{c.Block.Label}"));
        return $"switch {Value.Type.ToIrString()} {Value.ToIrString()}, label %{DefaultBlock.Label} [{casesStr}]";
    }
}

/// <summary>
/// 不可达指令
/// </summary>
public sealed class LlvmUnreachable : LlvmTerminator
{
    public static readonly LlvmUnreachable Instance = new();

    public override string ToIrString() => "unreachable";
}

/// <summary>
/// 间接跳转指令
/// </summary>
public sealed class LlvmIndirectBr : LlvmTerminator
{
    /// <summary>
    /// 目标地址
    /// </summary>
    public LlvmValue Address { get; init; } = LlvmNullPointer.Instance;

    /// <summary>
    /// 可能的目标块列表
    /// </summary>
    public List<LlvmBasicBlock> PossibleTargets { get; init; } = [];

    public override string ToIrString()
    {
        var targets = string.Join(", ", PossibleTargets.Select(t => $"label %{t.Label}"));
        return $"indirectbr {Address.Type.ToIrString()} {Address.ToIrString()}, [{targets}]";
    }
}

/// <summary>
/// Invoke 指令 (异常处理)
/// </summary>
public sealed class LlvmInvoke : LlvmTerminator
{
    /// <summary>
    /// 调用的函数
    /// </summary>
    public LlvmValue Function { get; init; } = LlvmNullPointer.Instance;

    /// <summary>
    /// 参数列表
    /// </summary>
    public List<LlvmValue> Arguments { get; init; } = [];

    /// <summary>
    /// 返回类型
    /// </summary>
    public LlvmType ReturnType { get; init; } = LlvmVoidType.Instance;

    /// <summary>
    /// 正常返回目标
    /// </summary>
    public LlvmBasicBlock NormalBlock { get; init; } = new() { Label = "normal" };

    /// <summary>
    /// 异常返回目标
    /// </summary>
    public LlvmBasicBlock UnwindBlock { get; init; } = new() { Label = "unwind" };

    public override string ToIrString()
    {
        var args = string.Join(", ", Arguments.Select(a => $"{a.Type.ToIrString()} {a.ToIrString()}"));
        return $"invoke {ReturnType.ToIrString()} @{((LlvmGlobal)Function).Name}({args}) to label %{NormalBlock.Label} unwind label %{UnwindBlock.Label}";
    }
}

/// <summary>
/// Resume 指令 (协程恢复)
/// </summary>
public sealed class LlvmResume : LlvmTerminator
{
    /// <summary>
    /// 恢复值
    /// </summary>
    public LlvmValue Value { get; init; } = LlvmNullPointer.Instance;

    public override string ToIrString() =>
        $"resume {Value.Type.ToIrString()} {Value.ToIrString()}";
}
