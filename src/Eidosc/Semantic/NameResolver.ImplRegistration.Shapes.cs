using Eidosc.Symbols;
using Eidosc.Ast.Types;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private ImplHeadShape BuildImplHeadShape(
        SymbolId traitId,
        IReadOnlyList<TypeNode> traitTypeArgs,
        TypePath? implementingTypePath,
        IReadOnlyList<string>? canonicalTraitTypeArgs = null,
        string? canonicalImplementingType = null)
    {
        var traitArgs = traitTypeArgs.Count > 0
            ? traitTypeArgs.Select(BuildImplTypeShapeNode).ToList()
            : BuildShapeNodesFromCanonicalTraitTypeArgs(canonicalTraitTypeArgs);
        var implementingType = implementingTypePath != null
            ? BuildImplTypeShapeNode(implementingTypePath)
            : ParseCanonicalShapeOrFallback(canonicalImplementingType);
        return new ImplHeadShape(traitId, traitArgs, implementingType);
    }

    private ImplHeadShape BuildCanonicalImplHeadShape(
        SymbolId traitId,
        IReadOnlyList<TypeNode> traitTypeArgs,
        TypePath? implementingTypePath,
        IReadOnlyList<string>? canonicalTraitTypeArgs = null,
        IReadOnlyList<ImplTypeRefKey>? canonicalTraitTypeArgKeys = null,
        string? canonicalImplementingType = null)
    {
        var traitArgs = traitTypeArgs.Count > 0
            ? traitTypeArgs.Select(typeArg => BuildCanonicalImplTypeShapeNode(typeArg, [])).ToList()
            : BuildShapeNodesFromCanonicalTraitTypeArgKeysOrText(canonicalTraitTypeArgKeys, canonicalTraitTypeArgs);
        var implementingType = implementingTypePath != null
            ? BuildCanonicalImplTypeShapeNode(implementingTypePath, [])
            : ParseCanonicalShapeOrFallback(canonicalImplementingType);
        return new ImplHeadShape(traitId, traitArgs, implementingType);
    }

    private ImplHeadShape BuildImplHeadShape(ImplSymbol impl)
    {
        var traitArgs = BuildImplTraitArgShapes(impl);
        var implementingType = impl.ImplementingTypeShape ??
                               (!impl.ImplementingTypeKey.IsEmpty
                                   ? BuildImplTypeShapeNode(impl.ImplementingTypeKey)
                                   : BuildOpaqueImplementingTypeShape(impl));
        return new ImplHeadShape(impl.Trait, traitArgs, implementingType);
    }

    private ImplTypeShapeNode BuildOpaqueImplementingTypeShape(ImplSymbol impl)
    {
        if (!impl.ImplementingType.IsValid)
        {
            throw new InvalidOperationException(
                $"Impl '{impl.Name}' has no structured implementing type shape or key.");
        }

        return new ImplConstructorShapeNode($"type:{impl.ImplementingType.Value}", [])
        {
            TypeId = impl.ImplementingType
        };
    }

    private List<ImplTypeShapeNode> BuildImplTraitArgShapes(ImplSymbol impl)
    {
        if (impl.TraitTypeArgShapes.Count > 0)
        {
            return impl.TraitTypeArgShapes;
        }

        var traitTypeArgKeys = impl.GetMatchingTraitTypeArgKeys();
        if (traitTypeArgKeys.Count > 0)
        {
            return traitTypeArgKeys.Select(BuildImplTypeShapeNode).ToList();
        }

        return BuildShapeNodesFromCanonicalTraitTypeArgs(
            impl.CanonicalTraitTypeArgs.Count > 0 ? impl.CanonicalTraitTypeArgs : impl.TraitTypeArgs);
    }

    private ImplTypeShapeNode BuildImplTypeShapeNode(TypeNode node)
    {
        return node switch
        {
            TypePath typePath => BuildImplTypePathShape(typePath),
            TupleType tuple => new ImplTupleShapeNode(tuple.Elements.Select(BuildImplTypeShapeNode).ToList()),
            ArrowType arrow => new ImplArrowShapeNode(
                BuildImplTypeShapeNode(arrow.ParamType),
                BuildImplTypeShapeNode(arrow.ReturnType)),
            EffectfulType effectful => new ImplEffectfulShapeNode(
                BuildImplTypeShapeNode(effectful.InputType),
                effectful.EnumerateEffectPaths()
                    .Select(path => string.Join(WellKnownStrings.Separators.Path, path))
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToList(),
                effectful.OutputType == null ? null : BuildImplTypeShapeNode(effectful.OutputType)),
            WildcardType => ImplWildcardShapeNode.Instance,
            _ => new ImplConstructorShapeNode(node.GetType().Name, [])
        };
    }

    private ImplTypeShapeNode BuildCanonicalImplTypeShapeNode(
        TypeNode node,
        HashSet<SymbolId> expandingAliases)
    {
        return node switch
        {
            TypePath typePath => BuildCanonicalImplTypePathShape(typePath, expandingAliases),
            TupleType tuple => new ImplTupleShapeNode(
                tuple.Elements
                    .Select(element => BuildCanonicalImplTypeShapeNode(element, expandingAliases))
                    .ToList()),
            ArrowType arrow => new ImplArrowShapeNode(
                BuildCanonicalImplTypeShapeNode(arrow.ParamType, expandingAliases),
                BuildCanonicalImplTypeShapeNode(arrow.ReturnType, expandingAliases)),
            EffectfulType effectful => new ImplEffectfulShapeNode(
                BuildCanonicalImplTypeShapeNode(effectful.InputType, expandingAliases),
                effectful.EnumerateEffectPaths()
                    .Select(path => string.Join(WellKnownStrings.Separators.Path, path))
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToList(),
                effectful.OutputType == null
                    ? null
                    : BuildCanonicalImplTypeShapeNode(effectful.OutputType, expandingAliases)),
            WildcardType => ImplWildcardShapeNode.Instance,
            _ => BuildImplTypeShapeNode(node)
        };
    }

    private ImplTypeShapeNode BuildCanonicalImplTypePathShape(
        TypePath typePath,
        HashSet<SymbolId> expandingAliases)
    {
        var symbolId = ResolveTypePathSymbolIdForImplKey(typePath);
        if (symbolId.IsValid && expandingAliases.Add(symbolId))
        {
            if (!TryExpandAliasTypePath(typePath, out var expanded))
            {
                expandingAliases.Remove(symbolId);
                return BuildImplTypePathShape(typePath);
            }

            var shape = BuildCanonicalImplTypeShapeNode(expanded, expandingAliases);
            expandingAliases.Remove(symbolId);
            return shape;
        }

        return BuildImplTypePathShape(typePath);
    }

    private ImplTypeShapeNode BuildImplTypeShapeNode(ImplTypeRefKey key)
    {
        return ImplTypeShapeFactory.BuildFromKey(
            key,
            symbolId => _symbolTable.GetSymbol(symbolId) is TypeParamSymbol typeParam
                ? typeParam.Name
                : null);
    }

    private ImplTypeShapeNode BuildImplTypePathShape(TypePath typePath)
    {
        var symbolId = ResolveTypePathSymbolIdForImplKey(typePath);
        var symbol = symbolId.IsValid ? _symbolTable.GetSymbol(symbolId) : null;

        if (typePath.TypeArgs.Count == 0 &&
            symbol is TypeParamSymbol typeParam)
        {
            return new ImplVariableShapeNode(typeParam.Name);
        }

        if (typePath.ModulePath.Count == 0 &&
            typePath.TypeArgs.Count == 0 &&
            !string.IsNullOrWhiteSpace(typePath.TypeName) &&
            ImplTypeShapeFactory.IsVariableLikeName(typePath.TypeName))
        {
            return new ImplVariableShapeNode(typePath.TypeName);
        }

        var name = typePath.ModulePath.Count > 0
            ? string.Join(WellKnownStrings.Separators.Path, typePath.ModulePath) + WellKnownStrings.Separators.Path + typePath.TypeName
            : typePath.TypeName;
        return new ImplConstructorShapeNode(name, typePath.TypeArgs.Select(BuildImplTypeShapeNode).ToList())
        {
            SymbolId = symbolId,
            TypeId = symbol?.TypeId ?? TypeId.None
        };
    }

    private static List<ImplTypeShapeNode> BuildShapeNodesFromCanonicalTraitTypeArgs(IReadOnlyList<string>? canonicalTraitTypeArgs)
    {
        if (canonicalTraitTypeArgs == null || canonicalTraitTypeArgs.Count == 0)
        {
            return [];
        }

        return canonicalTraitTypeArgs.Select(ParseCanonicalShapeOrFallback).ToList();
    }

    private List<ImplTypeShapeNode> BuildShapeNodesFromCanonicalTraitTypeArgKeysOrText(
        IReadOnlyList<ImplTypeRefKey>? canonicalTraitTypeArgKeys,
        IReadOnlyList<string>? canonicalTraitTypeArgs)
    {
        if (canonicalTraitTypeArgKeys is { Count: > 0 } &&
            canonicalTraitTypeArgKeys.Any(static key => !key.IsEmpty))
        {
            return canonicalTraitTypeArgKeys
                .Where(static key => !key.IsEmpty)
                .Select(BuildImplTypeShapeNode)
                .ToList();
        }

        return BuildShapeNodesFromCanonicalTraitTypeArgs(canonicalTraitTypeArgs);
    }

    private static ImplTypeShapeNode ParseCanonicalShapeOrFallback(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            string.Equals(trimmed, "_", StringComparison.Ordinal))
        {
            return ImplWildcardShapeNode.Instance;
        }

        var bracketIndex = trimmed.IndexOf('[');
        if (bracketIndex <= 0 || !trimmed.EndsWith("]", StringComparison.Ordinal))
        {
            return ImplTypeShapeFactory.IsVariableLikeName(trimmed)
                ? new ImplVariableShapeNode(trimmed)
                : new ImplConstructorShapeNode(trimmed, []);
        }

        var name = trimmed[..bracketIndex];
        var payload = trimmed.Substring(bracketIndex + 1, trimmed.Length - bracketIndex - 2);
        var parts = SplitTopLevelCommaSeparated(payload);
        return new ImplConstructorShapeNode(name, parts.Select(ParseCanonicalShapeOrFallback).ToList());
    }

    private static List<string> SplitTopLevelCommaSeparated(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '[':
                case '(':
                case '{':
                    depth++;
                    break;
                case ']':
                case ')':
                case '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    var part = text[start..i].Trim();
                    if (part.Length > 0)
                    {
                        parts.Add(part);
                    }

                    start = i + 1;
                    break;
            }
        }

        var tail = text[start..].Trim();
        if (tail.Length > 0)
        {
            parts.Add(tail);
        }

        return parts;
    }
}
