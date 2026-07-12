using Eidosc.Symbols;
using Eidosc.Semantic;

namespace Eidosc.Types;

/// <summary>
/// 内置 Trait 定义和类型- Trait 映射
/// </summary>
public static class BuiltinTraits
{
    private static readonly Dictionary<string, int> _builtinTraitCodes = new(StringComparer.Ordinal)
    {
        [TraitNames.Eq] = 1,
        [TraitNames.Ord] = 2,
        [TraitNames.Num] = 3,
        [TraitNames.Show] = 4,
        [TraitNames.Clone] = 5,
    };

    /// <summary>
    /// 内置 Trait 名称常量
    /// </summary>
    public static class TraitNames
    {
        public const string Eq = "Eq";
        public const string Ord = "Ord";
        public const string Num = "Num";
        public const string Show = "Show";
        public const string Clone = "Clone";
        public const string Hash = "Hash";
        public const string Copy = "Copy";
    }

    public static class MethodNames
    {
        public const string Eq = "eq";
        public const string Show = "show";
    }

    /// <summary>
    /// 内置类型名称常量
    /// </summary>
    public static class TypeNames
    {
        public const string Int = WellKnownStrings.BuiltinTypes.Int;
        public const string Float = WellKnownStrings.BuiltinTypes.Float;
        public const string String = WellKnownStrings.BuiltinTypes.String;
        public const string Bool = WellKnownStrings.BuiltinTypes.Bool;
        public const string Char = WellKnownStrings.BuiltinTypes.Char;
        public const string Unit = WellKnownStrings.BuiltinTypes.UnitSyntax;
    }

    /// <summary>
    /// 类型到实现的 Trait 集合的映射
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> _typeTraits = new()
    {
        [TypeNames.Int] = [TraitNames.Eq, TraitNames.Ord, TraitNames.Num, TraitNames.Show, TraitNames.Clone, TraitNames.Hash, TraitNames.Copy],
        [TypeNames.Float] = [TraitNames.Eq, TraitNames.Ord, TraitNames.Num, TraitNames.Show, TraitNames.Clone, TraitNames.Copy],
        [TypeNames.String] = [TraitNames.Eq, TraitNames.Show, TraitNames.Clone, TraitNames.Hash],
        [TypeNames.Bool] = [TraitNames.Eq, TraitNames.Show, TraitNames.Clone, TraitNames.Hash, TraitNames.Copy],
        [TypeNames.Char] = [TraitNames.Eq, TraitNames.Ord, TraitNames.Show, TraitNames.Clone, TraitNames.Copy],
        [TypeNames.Unit] = [TraitNames.Eq, TraitNames.Show, TraitNames.Clone, TraitNames.Copy],
    };

    /// <summary>
    /// 检查类型是否实现了指定 Trait
    /// </summary>
    /// <param name="typeName">类型名称</param>
    /// <param name="traitName">Trait 名称</param>
    /// <returns>是否实现了该 Trait</returns>
    public static bool HasTrait(string typeName, string traitName)
    {
        return _typeTraits.TryGetValue(typeName, out var traits) && traits.Contains(traitName);
    }

    /// <summary>
    /// 检查类型是否实现了指定 Trait（通过 TyCon）
    /// </summary>
    public static bool HasTrait(TyCon type, string traitName)
    {
        return HasTrait(type.Name, traitName);
    }

    /// <summary>
    /// 获取类型实现的所有 Trait
    /// </summary>
    /// <param name="typeName">类型名称</param>
    /// <returns>Trait 名称集合</returns>
    public static IReadOnlySet<string> GetTraits(string typeName)
    {
        if (_typeTraits.TryGetValue(typeName, out var traits))
            return traits;
        return new HashSet<string>();
    }

    /// <summary>
    /// 检查是否为内置类型
    /// </summary>
    public static bool IsBuiltinType(string typeName)
    {
        return _typeTraits.ContainsKey(typeName);
    }

    /// <summary>
    /// 检查是否为内置类型
    /// </summary>
    public static bool IsBuiltinType(TyCon type)
    {
        return IsBuiltinType(type.Name);
    }

    public static bool IsBuiltinTraitName(string name)
    {
        return _builtinTraitCodes.ContainsKey(name);
    }

    public static bool TryGetBuiltinTraitCode(string name, out int code)
    {
        return _builtinTraitCodes.TryGetValue(name, out code);
    }

    public static SymbolId GetBuiltinTraitSymbolId(string name)
    {
        return TryGetBuiltinTraitCode(name, out var code)
            ? new SymbolId(-code)
            : SymbolId.None;
    }

    /// <summary>
    /// 在符号表中注册内置 Trait
    /// </summary>
    public static void RegisterBuiltinTraits(SymbolTable symbolTable)
    {
        // 注册 Eq Trait
        RegisterTrait(symbolTable, TraitNames.Eq);

        // 注册 Ord Trait
        RegisterTrait(symbolTable, TraitNames.Ord);

        // 注册 Num Trait
        RegisterTrait(symbolTable, TraitNames.Num);

        // 注册 Show Trait
        RegisterTrait(symbolTable, TraitNames.Show);

        // 注册 Clone Trait
        RegisterTrait(symbolTable, TraitNames.Clone);

        // 注册 Copy Trait
        RegisterTrait(symbolTable, TraitNames.Copy);

        // 注册 Hash Trait
        RegisterTrait(symbolTable, TraitNames.Hash);

        // 注册内置 supertrait 关系
        RegisterBuiltinSupertrait(symbolTable, TraitNames.Ord, TraitNames.Eq);
    }

    /// <summary>
    /// Registers a parent trait relationship between two builtin traits.
    /// For example, Ord: Eq means Ord extends Eq.
    /// </summary>
    private static void RegisterBuiltinSupertrait(SymbolTable symbolTable, string childTraitName, string parentTraitName)
    {
        var childId = symbolTable.LookupType(childTraitName);
        var parentId = symbolTable.LookupType(parentTraitName);
        if (childId is { } cId && cId.IsValid &&
            parentId is { } pId && pId.IsValid &&
            symbolTable.GetSymbol(cId) is TraitSymbol childSymbol &&
            symbolTable.GetSymbol(pId) is TraitSymbol &&
            !childSymbol.ParentTraits.Contains(pId))
        {
            var parentTraits = new List<SymbolId>(childSymbol.ParentTraits) { pId };
            symbolTable.UpdateSymbol(childSymbol with { ParentTraits = parentTraits });
        }
    }

    private static void RegisterTrait(SymbolTable symbolTable, string traitName)
    {
        // 检查是否已存在
        var existing = symbolTable.LookupType(traitName);
        if (existing == null || !existing.Value.IsValid)
        {
            var span = new Utils.SourceSpan(new Utils.SourceLocation(0, 1, 1), 0);
            symbolTable.DeclareTrait(traitName, span);
        }
    }

    /// <summary>
    /// 获取运算符对应的 Trait 名称
    /// </summary>
    public static string? GetOperatorTrait(string operatorSymbol)
    {
        return operatorSymbol switch
        {
            // 算术运算符 -> Num
            WellKnownStrings.Operators.Add or WellKnownStrings.Operators.Subtract or WellKnownStrings.Operators.Multiply or WellKnownStrings.Operators.Divide or WellKnownStrings.Operators.Modulo => TraitNames.Num,

            // 相等比较 -> Eq
            WellKnownStrings.Operators.Equal or WellKnownStrings.Operators.NotEqual => TraitNames.Eq,

            // 有序比较 -> Ord
            WellKnownStrings.Operators.Less or WellKnownStrings.Operators.Greater or WellKnownStrings.Operators.LessEqual or WellKnownStrings.Operators.GreaterEqual => TraitNames.Ord,

            _ => null
        };
    }
}
