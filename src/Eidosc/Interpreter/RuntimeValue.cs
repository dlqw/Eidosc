namespace Eidosc.Interpreter;

/// <summary>
/// 运行时值基类
/// </summary>
public abstract record RuntimeValue
{
    public virtual string Display() => ToString() ?? "";

    public T AssertType<T>() where T : RuntimeValue
    {
        if (this is T typed) return typed;
        throw new InterpreterException(InterpreterMessages.ExpectedRuntimeValueType(typeof(T).Name, GetType().Name));
    }
}

public sealed record IntValue(long Value) : RuntimeValue
{
    public override string Display() => Value.ToString();
}

public sealed record FloatValue(double Value) : RuntimeValue
{
    public override string Display() => Value.ToString("G");
}

public sealed record StringValue(string Value) : RuntimeValue
{
    public override string Display() => $"\"{Value}\"";
}

public sealed record CharValue(char Value) : RuntimeValue
{
    public override string Display() => $"'{Value}'";
}

public sealed record BoolValue(bool Value) : RuntimeValue
{
    public override string Display() => Value ? "true" : "false";
}

public sealed record UnitValue : RuntimeValue
{
    public static readonly UnitValue Instance = new();
    public override string Display() => "()";
}

public sealed record TupleValue(List<RuntimeValue> Elements) : RuntimeValue
{
    public override string Display() => $"({string.Join(", ", Elements.Select(e => e.Display()))})";
}

public sealed record ListValue(List<RuntimeValue> Elements) : RuntimeValue
{
    public override string Display() => $"[{string.Join(", ", Elements.Select(e => e.Display()))}]";
}

public sealed record CtorValue(string Name, List<RuntimeValue> Fields) : RuntimeValue
{
    public override string Display()
    {
        if (Fields.Count == 0) return Name;
        return $"{Name}({string.Join(", ", Fields.Select(f => f.Display()))})";
    }
}

public sealed record FuncValue(
    List<string> Parameters,
    Eidosc.Hir.HirNode Body,
    InterpreterEnvironment Closure) : RuntimeValue
{
    public override string Display() => InterpreterMessages.RuntimeFunctionDisplay(string.Join(", ", Parameters));
}

public sealed record BuiltinFuncValue(
    string Name,
    Func<RuntimeValue[], RuntimeValue> Impl) : RuntimeValue
{
    public override string Display() => InterpreterMessages.RuntimeBuiltinFunctionDisplay(Name);
}

public sealed class InterpreterException : System.Exception
{
    public InterpreterException(string message) : base(message) { }
}

public sealed class BreakException : System.Exception
{
    public RuntimeValue Value { get; } = UnitValue.Instance;
    public BreakException() { }
    public BreakException(RuntimeValue value) { Value = value; }
}
public sealed class ContinueException : System.Exception { }
public sealed class ReturnException : System.Exception
{
    public RuntimeValue Value { get; }
    public ReturnException(RuntimeValue value) { Value = value; }
}
