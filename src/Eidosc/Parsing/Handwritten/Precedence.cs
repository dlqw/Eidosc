namespace Eidosc.Parsing.Handwritten;

public enum Assoc { Left, Right, None }

public readonly record struct PrecEntry(int Level, Assoc Associativity);

public static class Precedence
{
    public const int Selection = 0;
    public const int Assign = 0;
    public const int Pipe = 1;
    public const int Coalesce = 2;
    public const int Or = 3;
    public const int And = 4;
    public const int BitOr = 5;
    public const int BitXor = 6;
    public const int BitAnd = 7;
    public const int Comparison = 8;
    public const int Shift = 9;
    public const int Cons = 10;
    public const int Additive = 11;
    public const int Multiplicative = 12;
    public const int UnaryPrefix = 13;
    public const int Arrow = 14;

    public static PrecEntry? TryGetBinary(string op) => op switch
    {
        "|>"  => new(Pipe, Assoc.Left),
        ">>=" => new(Pipe, Assoc.Left),
        "??"  => new(Coalesce, Assoc.Right),
        "||"  => new(Or, Assoc.Right),
        "&&"  => new(And, Assoc.Right),
        "|"   => new(BitOr, Assoc.Left),
        "^"   => new(BitXor, Assoc.Left),
        "&"   => new(BitAnd, Assoc.Left),
        "=="  => new(Comparison, Assoc.None),
        "!="  => new(Comparison, Assoc.None),
        "<"   => new(Comparison, Assoc.None),
        ">"   => new(Comparison, Assoc.None),
        "<="  => new(Comparison, Assoc.None),
        ">="  => new(Comparison, Assoc.None),
        ".."  => new(Comparison, Assoc.None),
        "<<"  => new(Shift, Assoc.Left),
        ">>"  => new(Shift, Assoc.Left),
        "+:"  => new(Cons, Assoc.Right),
        ":+"  => new(Cons, Assoc.Left),
        ">>>" => new(Cons, Assoc.Right),
        "<<<" => new(Cons, Assoc.Right),
        "+"   => new(Additive, Assoc.Left),
        "-"   => new(Additive, Assoc.Left),
        "++"  => new(Additive, Assoc.Left),
        "<>"  => new(Additive, Assoc.Left),
        "*"   => new(Multiplicative, Assoc.Left),
        "/"   => new(Multiplicative, Assoc.Left),
        "%"   => new(Multiplicative, Assoc.Left),
        "<$>" => new(Multiplicative, Assoc.Left),
        "<*>" => new(Multiplicative, Assoc.Left),
        _ => null
    };

    public static PrecEntry? TryGetPrefix(string op) => op switch
    {
        "-"    => new(UnaryPrefix, Assoc.Right),
        "!"    => new(UnaryPrefix, Assoc.Right),
        "*"    => new(UnaryPrefix, Assoc.Right),
        "ref"  => new(UnaryPrefix, Assoc.Right),
        "mref" => new(UnaryPrefix, Assoc.Right),
        _ => null
    };
}
