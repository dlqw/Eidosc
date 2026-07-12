using Eidosc.Symbols;
using Eidosc.Semantic;

namespace Eidosc.Types;

/// <summary>
/// 统一类型构造器 kind 解析：
/// - 优先使用推断阶段提供的覆盖 kind（例如未标注参数推断结果）
/// - 其次使用符号表中的类型参数 kind 注解
/// - 最后退化到基于参数个数的 * 链
/// </summary>
public sealed class TypeConstructorKindResolver
{
    private readonly SymbolTable _symbolTable;
    private readonly IReadOnlyDictionary<SymbolId, Kind>? _kindOverridesBySymbol;

    public TypeConstructorKindResolver(
        SymbolTable symbolTable,
        IReadOnlyDictionary<SymbolId, Kind>? kindOverridesBySymbol = null)
    {
        _symbolTable = symbolTable;
        _kindOverridesBySymbol = kindOverridesBySymbol;
    }

    public Kind GetConstructorKind(SymbolId symbolId)
    {
        if (!symbolId.IsValid)
        {
            return Kind.KStar.Instance;
        }

        if (_kindOverridesBySymbol != null &&
            _kindOverridesBySymbol.TryGetValue(symbolId, out var overrideKind))
        {
            return NormalizeKind(overrideKind);
        }

        var kindFromSymbols = TryGetKindFromTypeParamSymbols(symbolId);
        if (kindFromSymbols != null)
        {
            return kindFromSymbols;
        }

        return Kind.BuildArrowKind(GetFallbackArity(symbolId));
    }

    public int GetExpectedParamCount(SymbolId symbolId)
    {
        var constructorKind = GetConstructorKind(symbolId);
        return KindParser.GetTopLevelArity(constructorKind);
    }

    public static Kind BuildConstructorKind(IReadOnlyList<Kind> parameterKinds)
    {
        Kind result = Kind.KStar.Instance;
        for (var i = parameterKinds.Count - 1; i >= 0; i--)
        {
            result = new Kind.KArrow(NormalizeKind(parameterKinds[i]), result);
        }

        return result;
    }

    private Kind? TryGetKindFromTypeParamSymbols(SymbolId symbolId)
    {
        return _symbolTable.GetSymbol(symbolId) switch
        {
            AdtSymbol adt when adt.TypeParams.Count > 0
                => BuildConstructorKindFromTypeParamSymbols(adt.TypeParams),
            TraitSymbol trait when trait.TypeParams.Count > 0
                => BuildConstructorKindFromTypeParamSymbols(trait.TypeParams),
            _ => null
        };
    }

    private Kind BuildConstructorKindFromTypeParamSymbols(IReadOnlyList<SymbolId> typeParamSymbols)
    {
        var parameterKinds = new List<Kind>(typeParamSymbols.Count);
        foreach (var symbolId in typeParamSymbols)
        {
            parameterKinds.Add(GetTypeParamKind(symbolId));
        }

        return BuildConstructorKind(parameterKinds);
    }

    private Kind GetTypeParamKind(SymbolId typeParamSymbolId)
    {
        if (!typeParamSymbolId.IsValid)
        {
            return Kind.KStar.Instance;
        }

        if (_symbolTable.GetSymbol(typeParamSymbolId) is not TypeParamSymbol typeParam)
        {
            return Kind.KStar.Instance;
        }

        var kindText = string.IsNullOrWhiteSpace(typeParam.KindAnnotation)
            ? "kind1"
            : typeParam.KindAnnotation;

        return KindParser.TryParse(kindText, out var parsedKind, out _)
            ? parsedKind
            : Kind.KStar.Instance;
    }

    private int GetFallbackArity(SymbolId symbolId)
    {
        return _symbolTable.GetSymbol(symbolId) switch
        {
            AdtSymbol adt => adt.TypeParams.Count,
            EffectSymbol => 0,
            TraitSymbol trait => trait.TypeParams.Count,
            _ => 0
        };
    }

    private static Kind NormalizeKind(Kind kind)
    {
        while (kind is Kind.KVar { Instance: not null } kindVar)
        {
            kind = kindVar.Instance;
        }

        return kind;
    }
}
