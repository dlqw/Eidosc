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
    public const int Comparison = 5;
    public const int Cons = 6;
    public const int Additive = 7;
    public const int Multiplicative = 8;
    public const int UnaryPrefix = 9;
    public const int Arrow = 10;

    public static PrecEntry? TryGetBinary(string op) => op switch
    {
        "|>"  => new(Pipe, Assoc.Left),
        ">>=" => new(Pipe, Assoc.Left),
        "??"  => new(Coalesce, Assoc.Right),
        "||"  => new(Or, Assoc.Right),
        "&&"  => new(And, Assoc.Right),
        "=="  => new(Comparison, Assoc.None),
        "!="  => new(Comparison, Assoc.None),
        "<"   => new(Comparison, Assoc.None),
        ">"   => new(Comparison, Assoc.None),
        "<="  => new(Comparison, Assoc.None),
        ">="  => new(Comparison, Assoc.None),
        ".."  => new(Comparison, Assoc.None),
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
        "&"    => new(UnaryPrefix, Assoc.Right),
        "ref"  => new(UnaryPrefix, Assoc.Right),
        "mref" => new(UnaryPrefix, Assoc.Right),
        _ => null
    };
}
