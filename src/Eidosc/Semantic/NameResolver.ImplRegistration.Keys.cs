using Eidosc.Symbols;
using System.Collections.Immutable;
using Eidosc.Ast.Types;
using Eidosc.Types;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private List<ImplTypeRefKey> BuildImplTraitTypeArgKeys(ImplTraitReference traitRef)
    {
        if (traitRef.TypeArgs.Count == 0)
        {
            return traitRef.TypeArgTexts
                .Select(ImplTypeRefKey.FromCanonicalText)
                .ToList();
        }

        return traitRef.TypeArgs
            .Select(BuildImplTypeRefKey)
            .ToList();
    }

    private List<ImplTypeRefKey> BuildCanonicalImplTraitTypeArgKeys(IReadOnlyList<string> canonicalTraitTypeArgs)
    {
        return canonicalTraitTypeArgs
            .Select(BuildCanonicalImplTypeRefKey)
            .ToList();
    }

    private ImplTypeRefKey BuildCanonicalImplTypeRefKey(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return ImplTypeRefKey.Empty;
        }

        var bracketIndex = trimmed.IndexOf('[');
        if (bracketIndex <= 0 || !trimmed.EndsWith("]", StringComparison.Ordinal))
        {
            return BuildCanonicalSimpleTypeRefKey(trimmed);
        }

        var head = trimmed[..bracketIndex];
        var payload = trimmed.Substring(bracketIndex + 1, trimmed.Length - bracketIndex - 2);
        var typeArguments = SplitTopLevelCommaSeparated(payload)
            .Select(BuildCanonicalImplTypeRefKey)
            .Where(static key => !key.IsEmpty)
            .ToImmutableArray();
        var headKey = BuildCanonicalSimpleTypeRefKey(head);
        return new ImplTypeRefKey(headKey.SymbolId, headKey.TypeId, headKey.Text, typeArguments);
    }

    private ImplTypeRefKey BuildCanonicalSimpleTypeRefKey(string name)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return ImplTypeRefKey.Empty;
        }

        var builtInTypeId = BaseTypes.GetBuiltInTypeId(trimmed);
        if (builtInTypeId.IsValid)
        {
            return new ImplTypeRefKey(SymbolId.None, builtInTypeId, trimmed, []);
        }

        if (_symbolTable.LookupType(trimmed) is { IsValid: true } symbolId &&
            _symbolTable.GetSymbol(symbolId) is { TypeId: { IsValid: true } typeId })
        {
            return new ImplTypeRefKey(symbolId, typeId, trimmed, []);
        }

        return ImplTypeRefKey.FromText(trimmed);
    }

    private ImplTypeRefKey BuildImplTypeRefKey(TypeNode node)
    {
        return node switch
        {
            TypePath typePath => BuildTypePathRefKey(typePath),
            TupleType tuple => new ImplTypeRefKey(
                SymbolId.None,
                TypeId.None,
                "tuple",
                tuple.Elements.Select(BuildImplTypeRefKey).ToImmutableArray()),
            ArrowType arrow => new ImplTypeRefKey(
                SymbolId.None,
                TypeId.None,
                "arrow",
                ImmutableArray.Create(
                    BuildImplTypeRefKey(arrow.ParamType),
                    BuildImplTypeRefKey(arrow.ReturnType))),
            _ => ImplTypeRefKey.FromCanonicalText(CanonicalizeTypeNodeForImplHead(node))
        };
    }

    private ImplTypeRefKey BuildTypePathRefKey(TypePath typePath)
    {
        var symbolId = ResolveTypePathSymbolIdForImplKey(typePath);
        var typeId = symbolId.IsValid && _symbolTable.GetSymbol(symbolId) is { TypeId.IsValid: true } symbol
            ? symbol.TypeId
            : TypeId.None;
        return new ImplTypeRefKey(
            symbolId,
            typeId,
            CanonicalizeTypePathForImplHead(typePath),
            typePath.TypeArgs.Select(BuildImplTypeRefKey).ToImmutableArray());
    }

    private SymbolId ResolveTypePathSymbolIdForImplKey(TypePath typePath)
    {
        if (typePath.SymbolId.IsValid)
        {
            return typePath.SymbolId;
        }

        if (string.IsNullOrWhiteSpace(typePath.TypeName))
        {
            return SymbolId.None;
        }

        if (typePath.ModulePath.Count == 0)
        {
            return _symbolTable.LookupType(typePath.TypeName) ?? SymbolId.None;
        }

        var parts = new List<string>(typePath.ModulePath) { typePath.TypeName };
        var result = ResolvePathWithImports(parts);
        return result.IsSuccess ? result.SymbolId : SymbolId.None;
    }
}
