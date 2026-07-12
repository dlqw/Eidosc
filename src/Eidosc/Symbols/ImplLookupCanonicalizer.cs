using Type = Eidosc.Types.Type;
using Eidosc.Types;
using System.Collections.Immutable;

namespace Eidosc.Symbols;

/// <summary>
/// 统一 impl 查找所需的具体类型归一化逻辑。
/// </summary>
public static class ImplLookupCanonicalizer
{
    public static TypeId ResolveLookupTypeId(SymbolTable symbolTable, TyCon con)
    {
        ArgumentNullException.ThrowIfNull(symbolTable);
        ArgumentNullException.ThrowIfNull(con);

        if (con.Id.IsValid)
        {
            return con.Id;
        }

        if (con.Symbol.IsValid &&
            symbolTable.GetSymbol(con.Symbol) is Symbol symbol &&
            symbol.TypeId.IsValid)
        {
            return symbol.TypeId;
        }

        if (!string.IsNullOrWhiteSpace(con.Name))
        {
            if (TryResolveBuiltinTypeId(con.Name, out var builtinTypeId))
            {
                return builtinTypeId;
            }

            var lookup = symbolTable.LookupType(con.Name);
            if (lookup.HasValue &&
                symbolTable.GetSymbol(lookup.Value) is Symbol lookupSymbol &&
                lookupSymbol.TypeId.IsValid)
            {
                return lookupSymbol.TypeId;
            }
        }

        return TypeId.None;
    }

    public static string ResolveCanonicalImplementingType(
        SymbolTable symbolTable,
        Type type,
        Func<Type, Type>? apply = null)
    {
        ArgumentNullException.ThrowIfNull(symbolTable);
        ArgumentNullException.ThrowIfNull(type);

        var resolved = apply?.Invoke(type) ?? type;
        return resolved switch
        {
            TyCon con => ResolveCanonicalTyCon(symbolTable, con, apply),
            TyVar var => $"t{var.Index}",
            TyTuple tuple => $"({string.Join(",", tuple.Elements.Select(element => ResolveCanonicalImplementingType(symbolTable, element, apply)))})",
            TyFun fun => ResolveCanonicalFunctionType(symbolTable, fun, apply),
            TyRef reference => $"Ref[{ResolveCanonicalImplementingType(symbolTable, reference.Inner, apply)}]",
            TyMutRef mutReference => $"MRef[{ResolveCanonicalImplementingType(symbolTable, mutReference.Inner, apply)}]",
            EffectRow or EffectTag => throw new InvalidOperationException($"Unsupported impl lookup type: {resolved.GetType().Name}"),
            _ => throw new System.Diagnostics.UnreachableException()
        };
    }

    /// <summary>
    /// Builds the structured impl lookup key for a type after applying the supplied substitution.
    /// </summary>
    public static ImplTypeRefKey BuildTypeRefKey(
        SymbolTable symbolTable,
        Type type,
        Func<Type, Type>? apply = null)
    {
        ArgumentNullException.ThrowIfNull(symbolTable);
        ArgumentNullException.ThrowIfNull(type);

        var resolved = apply?.Invoke(type) ?? type;
        return resolved switch
        {
            TyCon con => BuildTyConRefKey(symbolTable, con, apply),
            TyVar variable => new ImplTypeRefKey(SymbolId.None, TypeId.None, $"var:{variable.Index}", []),
            TyTuple tuple => new ImplTypeRefKey(
                SymbolId.None,
                TypeId.None,
                "tuple",
                tuple.Elements.Select(element => BuildTypeRefKey(symbolTable, element, apply)).ToImmutableArray()),
            TyFun function => BuildFunctionRefKey(symbolTable, function, apply),
            TyRef reference => new ImplTypeRefKey(
                SymbolId.None,
                TypeId.None,
                "Ref",
                ImmutableArray.Create(BuildTypeRefKey(symbolTable, reference.Inner, apply))),
            TyMutRef reference => new ImplTypeRefKey(
                SymbolId.None,
                TypeId.None,
                "MRef",
                ImmutableArray.Create(BuildTypeRefKey(symbolTable, reference.Inner, apply))),
            EffectRow or EffectTag => throw new InvalidOperationException($"Unsupported impl lookup type: {resolved.GetType().Name}"),
            _ => throw new System.Diagnostics.UnreachableException()
        };
    }

    public static string ResolveBuiltinCanonicalTypeName(TypeId typeId)
    {
        return typeId.Value switch
        {
            BaseTypes.IntId => WellKnownStrings.BuiltinTypes.Int,
            BaseTypes.FloatId => WellKnownStrings.BuiltinTypes.Float,
            BaseTypes.BoolId => WellKnownStrings.BuiltinTypes.Bool,
            BaseTypes.StringId => WellKnownStrings.BuiltinTypes.String,
            BaseTypes.CharId => WellKnownStrings.BuiltinTypes.Char,
            BaseTypes.UnitId => WellKnownStrings.BuiltinTypes.Unit,
            BaseTypes.NeverId => WellKnownStrings.BuiltinTypes.Never,
            _ => string.Empty
        };
    }

    private static string ResolveCanonicalTyCon(
        SymbolTable symbolTable,
        TyCon con,
        Func<Type, Type>? apply)
    {
        var constructorName = ResolveCanonicalTypeName(symbolTable, con);
        if (con.Args.Count == 0)
        {
            return constructorName;
        }

        var args = con.Args
            .Select(arg => ResolveCanonicalImplementingType(symbolTable, arg, apply))
            .ToList();
        return $"{constructorName}[{string.Join(",", args)}]";
    }

    private static ImplTypeRefKey BuildTyConRefKey(
        SymbolTable symbolTable,
        TyCon con,
        Func<Type, Type>? apply)
    {
        var symbolId = con.Symbol;
        if (!symbolId.IsValid &&
            !con.ConstructorVarIndex.HasValue &&
            !string.IsNullOrWhiteSpace(con.Name))
        {
            symbolId = symbolTable.LookupType(con.Name) ?? SymbolId.None;
        }

        var typeId = ResolveLookupTypeId(symbolTable, con);
        var text = con.ConstructorVarIndex.HasValue
            ? $"var:{con.ConstructorVarIndex.Value}"
            : ResolveCanonicalTypeName(symbolTable, con);

        return new ImplTypeRefKey(
            symbolId,
            typeId,
            text,
            con.Args.Select(arg => BuildTypeRefKey(symbolTable, arg, apply)).ToImmutableArray());
    }

    private static ImplTypeRefKey BuildFunctionRefKey(
        SymbolTable symbolTable,
        TyFun function,
        Func<Type, Type>? apply)
    {
        var parameterKey = function.Params.Count switch
        {
            0 => new ImplTypeRefKey(
                SymbolId.None,
                new TypeId(BaseTypes.UnitId),
                WellKnownStrings.BuiltinTypes.Unit,
                []),
            1 => BuildTypeRefKey(symbolTable, function.Params[0], apply),
            _ => new ImplTypeRefKey(
                SymbolId.None,
                TypeId.None,
                "tuple",
                function.Params.Select(parameter => BuildTypeRefKey(symbolTable, parameter, apply)).ToImmutableArray())
        };

        return new ImplTypeRefKey(
            SymbolId.None,
            TypeId.None,
            "arrow",
            ImmutableArray.Create(
                parameterKey,
                BuildTypeRefKey(symbolTable, function.Result, apply)));
    }

    private static string ResolveCanonicalFunctionType(
        SymbolTable symbolTable,
        TyFun fun,
        Func<Type, Type>? apply)
    {
        var parameters = fun.Params
            .Select(param => ResolveCanonicalImplementingType(symbolTable, param, apply))
            .ToList();
        var input = parameters.Count switch
        {
            0 => WellKnownStrings.BuiltinTypes.Unit,
            1 => parameters[0],
            _ => $"({string.Join(",", parameters)})"
        };
        var result = ResolveCanonicalImplementingType(symbolTable, fun.Result, apply);
        return $"{input}->{result}";
    }

    private static string ResolveCanonicalTypeName(SymbolTable symbolTable, TyCon con)
    {
        if (con.Id.IsValid)
        {
            var builtin = ResolveBuiltinCanonicalTypeName(con.Id);
            if (!string.IsNullOrWhiteSpace(builtin))
            {
                return builtin;
            }
        }

        if (con.Symbol.IsValid &&
            symbolTable.GetSymbol(con.Symbol) is Symbol symbol)
        {
            if (symbol.TypeId.IsValid)
            {
                var builtin = ResolveBuiltinCanonicalTypeName(symbol.TypeId);
                if (!string.IsNullOrWhiteSpace(builtin))
                {
                    return builtin;
                }
            }

            if (!string.IsNullOrWhiteSpace(symbol.Name))
            {
                return symbol.Name;
            }
        }

        if (TryResolveBuiltinCanonicalName(con.Name, out var builtinName))
        {
            return builtinName;
        }

        return string.IsNullOrWhiteSpace(con.Name)
            ? "<type>"
            : con.Name;
    }

    private static bool TryResolveBuiltinCanonicalName(string? name, out string builtinName)
    {
        builtinName = string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        builtinName = name.Trim() switch
        {
            WellKnownStrings.BuiltinTypes.Int => WellKnownStrings.BuiltinTypes.Int,
            WellKnownStrings.BuiltinTypes.Float => WellKnownStrings.BuiltinTypes.Float,
            WellKnownStrings.BuiltinTypes.Bool => WellKnownStrings.BuiltinTypes.Bool,
            WellKnownStrings.BuiltinTypes.String => WellKnownStrings.BuiltinTypes.String,
            WellKnownStrings.BuiltinTypes.Char => WellKnownStrings.BuiltinTypes.Char,
            WellKnownStrings.BuiltinTypes.Unit or "()" => WellKnownStrings.BuiltinTypes.Unit,
            WellKnownStrings.BuiltinTypes.Never => WellKnownStrings.BuiltinTypes.Never,
            _ => string.Empty
        };

        return builtinName.Length > 0;
    }

    private static bool TryResolveBuiltinTypeId(string? name, out TypeId typeId)
    {
        typeId = TypeId.None;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        typeId = name.Trim() switch
        {
            WellKnownStrings.BuiltinTypes.Int => new TypeId(BaseTypes.IntId),
            WellKnownStrings.BuiltinTypes.Float => new TypeId(BaseTypes.FloatId),
            WellKnownStrings.BuiltinTypes.Bool => new TypeId(BaseTypes.BoolId),
            WellKnownStrings.BuiltinTypes.String => new TypeId(BaseTypes.StringId),
            WellKnownStrings.BuiltinTypes.Char => new TypeId(BaseTypes.CharId),
            WellKnownStrings.BuiltinTypes.Unit or "()" => new TypeId(BaseTypes.UnitId),
            WellKnownStrings.BuiltinTypes.Never => new TypeId(BaseTypes.NeverId),
            _ => TypeId.None
        };

        return typeId.IsValid;
    }

}
