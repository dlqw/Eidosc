namespace Eidosc.Ast;

public enum BinaryOp
{
    // 优先级 0 - 管道 / bind
    Pipe,        // |>
    Bind,        // >>=
    Coalesce,    // ??

    // 优先级 3 - 乘除模 / functor+applicative
    Multiply,    // *
    Divide,      // /
    Modulo,      // %
    Fmap,        // <$>
    Ap,          // <*>

    // 优先级 4 - 加减/拼接 / semigroup append
    Add,         // +
    Subtract,    // -
    Concat,      // ++
    Append,      // <>

    // 优先级 5 - Cons / compose
    Prepend,         // +:  (Scala-style list prepend, right-assoc)
    AppendLast,      // :+  (Scala-style list append, left-assoc)
    ComposeRight,    // >>>
    ComposeLeft,     // <<<

    // 优先级 6 - 比较
    Less,        // <
    Greater,     // >
    LessEqual,   // <=
    GreaterEqual,// >=
    Equal,       // ==
    NotEqual,    // !=

    // 优先级 7 - 逻辑与
    And,         // &&

    // 优先级 8 - 逻辑或
    Or,          // ||
}

public enum UnaryOp
{
    Negate,  // - (负号)
    Not,     // ! (逻辑非)
    Deref,   // * (解引用)
    AddressOf, // & (取地址，过渡语法)
    Ref,     // ref (共享借用)
    MRef     // mref (可写借用)
}

public static class OperatorHelper
{
    public static string ToSymbol(this BinaryOp op) => op switch
    {
        BinaryOp.Pipe => WellKnownStrings.Operators.PipeForward,
        BinaryOp.Bind => WellKnownStrings.Operators.Bind,
        BinaryOp.Coalesce => WellKnownStrings.Operators.Coalesce,
        BinaryOp.Multiply => WellKnownStrings.Operators.Multiply,
        BinaryOp.Divide => WellKnownStrings.Operators.Divide,
        BinaryOp.Modulo => WellKnownStrings.Operators.Modulo,
        BinaryOp.Fmap => WellKnownStrings.Operators.Fmap,
        BinaryOp.Ap => WellKnownStrings.Operators.Ap,
        BinaryOp.Add => WellKnownStrings.Operators.Add,
        BinaryOp.Subtract => WellKnownStrings.Operators.Subtract,
        BinaryOp.Concat => WellKnownStrings.Operators.Concat,
        BinaryOp.Append => WellKnownStrings.Operators.Append,
        BinaryOp.Prepend => WellKnownStrings.Operators.Prepend,
        BinaryOp.AppendLast => WellKnownStrings.Operators.AppendLast,
        BinaryOp.ComposeRight => WellKnownStrings.Operators.ComposeRight,
        BinaryOp.ComposeLeft => WellKnownStrings.Operators.ComposeLeft,
        BinaryOp.Less => WellKnownStrings.Operators.Less,
        BinaryOp.Greater => WellKnownStrings.Operators.Greater,
        BinaryOp.LessEqual => WellKnownStrings.Operators.LessEqual,
        BinaryOp.GreaterEqual => WellKnownStrings.Operators.GreaterEqual,
        BinaryOp.Equal => WellKnownStrings.Operators.Equal,
        BinaryOp.NotEqual => WellKnownStrings.Operators.NotEqual,
        BinaryOp.And => WellKnownStrings.Operators.And,
        BinaryOp.Or => WellKnownStrings.Operators.Or,
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, null)
    };

    public static string ToSymbol(this UnaryOp op) => op switch
    {
        UnaryOp.Negate => WellKnownStrings.Operators.Subtract,
        UnaryOp.Not => WellKnownStrings.Operators.Not,
        UnaryOp.Deref => WellKnownStrings.Operators.Multiply,
        UnaryOp.AddressOf => WellKnownStrings.Operators.AddressOf,
        UnaryOp.Ref => WellKnownStrings.Operators.Ref,
        UnaryOp.MRef => WellKnownStrings.Operators.MRef,
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, null)
    };
}
