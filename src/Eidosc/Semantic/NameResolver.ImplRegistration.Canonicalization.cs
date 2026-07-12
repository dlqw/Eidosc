using Eidosc.Symbols;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Types;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private List<string> CanonicalizeImplTraitTypeArgs(ImplTraitReference traitRef)
    {
        if (traitRef.TypeArgs.Count == 0)
        {
            return traitRef.TypeArgTexts
                .Select(CanonicalizeTypeTextForImplHead)
                .ToList();
        }

        var canonical = new List<string>(traitRef.TypeArgs.Count);
        foreach (var typeArg in traitRef.TypeArgs)
        {
            canonical.Add(CanonicalizeTypeNodeForImplHead(typeArg));
        }

        return canonical;
    }

    private string CanonicalizeTypeTextForImplHead(string typeText)
    {
        var text = typeText.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (!TrySplitTraitReferenceText(text, out var pathText, out var typeArgText))
        {
            return RemoveInsignificantTypeWhitespace(text);
        }

        var path = ParsePathText(pathText);
        if (path.Count == 0)
        {
            return RemoveInsignificantTypeWhitespace(text);
        }

        var canonicalArgs = string.IsNullOrWhiteSpace(typeArgText)
            ? new List<string>()
            : SplitTopLevelCommaList(typeArgText).Select(CanonicalizeTypeTextForImplHead).ToList();

        if (TryGetTypeAliasDefinition(path, out var aliasDefinition) &&
            aliasDefinition.AliasTarget != null)
        {
            var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < aliasDefinition.TypeParams.Count && i < canonicalArgs.Count; i++)
            {
                var typeParamName = aliasDefinition.TypeParams[i].Name;
                if (!string.IsNullOrWhiteSpace(typeParamName))
                {
                    bindings[typeParamName] = canonicalArgs[i];
                }
            }

            return CanonicalizeTypeNodeForImplHead(aliasDefinition.AliasTarget, bindings);
        }

        var displayName = path.Count == 1
            ? path[0]
            : string.Join(WellKnownStrings.Separators.Path, path[..^1]) + WellKnownStrings.Separators.Path + path[^1];
        return canonicalArgs.Count == 0
            ? displayName
            : $"{displayName}[{string.Join(",", canonicalArgs)}]";
    }

    private string CanonicalizeTypeNodeForImplHead(TypeNode node)
    {
        return CanonicalizeTypeNodeForImplHead(node, new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private string CanonicalizeTypeNodeForImplHead(TypeNode node, IReadOnlyDictionary<string, string> textBindings)
    {
        return node switch
        {
            TypePath typePath => CanonicalizeTypePathForImplHead(typePath, textBindings),
            ArrowType arrow => $"{CanonicalizeTypeNodeForImplHead(arrow.ParamType, textBindings)}->{CanonicalizeTypeNodeForImplHead(arrow.ReturnType, textBindings)}",
            EffectfulType effectful => CanonicalizeEffectfulTypeForImplHead(effectful, textBindings),
            TupleType tuple => $"({string.Join(",", tuple.Elements.Select(element => CanonicalizeTypeNodeForImplHead(element, textBindings)))})",
            WildcardType => "_",
            _ => NormalizeTypeNode(node, selfType: null, traitTypeArgBindings: null)
        };
    }

    private string CanonicalizeEffectfulTypeForImplHead(EffectfulType effectful)
    {
        return CanonicalizeEffectfulTypeForImplHead(effectful, new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private string CanonicalizeEffectfulTypeForImplHead(
        EffectfulType effectful,
        IReadOnlyDictionary<string, string> textBindings)
    {
        var effectPaths = effectful.EnumerateEffectPaths()
            .Select(path => string.Join(WellKnownStrings.Separators.Path, path.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part.Trim())))
            .Where(path => path.Length > 0)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        var input = CanonicalizeTypeNodeForImplHead(effectful.InputType, textBindings);
        var output = effectful.OutputType == null ? WellKnownStrings.BuiltinTypes.Unit : CanonicalizeTypeNodeForImplHead(effectful.OutputType, textBindings);
        if (effectPaths.Count == 0)
        {
            return $"{input}->{output}";
        }

        return $"{input}->{{{string.Join(", ", effectPaths)}}}->{output}";
    }

    private string CanonicalizeTypePathForImplHead(TypePath typePath)
    {
        return CanonicalizeTypePathForImplHead(typePath, new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private string CanonicalizeTypePathForImplHead(TypePath typePath, IReadOnlyDictionary<string, string> textBindings)
    {
        if (typePath.ModulePath.Count == 0 &&
            typePath.TypeArgs.Count == 0 &&
            textBindings.TryGetValue(typePath.TypeName, out var boundText))
        {
            return boundText;
        }

        if (TryExpandAliasTypePath(typePath, out var expanded))
        {
            return CanonicalizeTypeNodeForImplHead(expanded, textBindings);
        }

        var name = typePath.ModulePath.Count > 0
            ? string.Join(WellKnownStrings.Separators.Path, typePath.ModulePath) + WellKnownStrings.Separators.Path + typePath.TypeName
            : typePath.TypeName;
        if (typePath.TypeArgs.Count == 0)
        {
            return name;
        }

        return $"{name}[{string.Join(",", typePath.TypeArgs.Select(typeArg => CanonicalizeTypeNodeForImplHead(typeArg, textBindings)))}]";
    }

    private bool TryGetTypeAliasDefinition(IReadOnlyList<string> path, out AdtDef aliasDefinition)
    {
        aliasDefinition = null!;
        if (path.Count == 0)
        {
            return false;
        }

        var symbolId = path.Count == 1
            ? _symbolTable.LookupType(path[0]) ?? SymbolId.None
            : ResolvePathWithImports(path).SymbolId;
        if (!symbolId.IsValid ||
            !_adtDefinitions.TryGetValue(symbolId, out var definition) ||
            !definition.IsTypeAlias)
        {
            return false;
        }

        aliasDefinition = definition;
        return true;
    }

    private bool TryExpandAliasTypePath(TypePath typePath, out TypeNode expanded)
    {
        expanded = typePath;

        var symbolId = typePath.SymbolId;
        if (!symbolId.IsValid && !string.IsNullOrWhiteSpace(typePath.TypeName))
        {
            symbolId = _symbolTable.LookupType(typePath.TypeName) ?? SymbolId.None;
        }

        if (!symbolId.IsValid ||
            !_adtDefinitions.TryGetValue(symbolId, out var adtDefinition) ||
            !adtDefinition.IsTypeAlias ||
            adtDefinition.AliasTarget == null)
        {
            return false;
        }

        var typeArgBindings = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        for (var i = 0; i < adtDefinition.TypeParams.Count && i < typePath.TypeArgs.Count; i++)
        {
            var typeParamName = adtDefinition.TypeParams[i].Name;
            if (!string.IsNullOrWhiteSpace(typeParamName))
            {
                typeArgBindings[typeParamName] = typePath.TypeArgs[i];
            }
        }

        expanded = SubstituteTypeNode(adtDefinition.AliasTarget, typeArgBindings);
        return true;
    }

    private TypeNode SubstituteTypeNode(TypeNode node, IReadOnlyDictionary<string, TypeNode> bindings)
    {
        return node switch
        {
            TypePath typePath => SubstituteTypePath(typePath, bindings),
            TupleType tuple => SubstituteTupleType(tuple, bindings),
            ArrowType arrow => SubstituteArrowType(arrow, bindings),
            EffectfulType effectful => effectful,
            _ => node
        };
    }

    private TypeNode SubstituteTupleType(TupleType tuple, IReadOnlyDictionary<string, TypeNode> bindings)
    {
        var substituted = new TupleType();
        foreach (var element in tuple.Elements)
        {
            substituted.Elements.Add(SubstituteTypeNode(element, bindings));
        }

        return substituted;
    }

    private TypeNode SubstituteArrowType(ArrowType arrow, IReadOnlyDictionary<string, TypeNode> bindings)
    {
        var substituted = new ArrowType();
        substituted.SetParamType(SubstituteTypeNode(arrow.ParamType, bindings));
        substituted.SetReturnType(SubstituteTypeNode(arrow.ReturnType, bindings));
        return substituted;
    }

    private TypeNode SubstituteTypePath(TypePath typePath, IReadOnlyDictionary<string, TypeNode> bindings)
    {
        if (typePath.ModulePath.Count == 0 &&
            typePath.TypeArgs.Count == 0 &&
            bindings.TryGetValue(typePath.TypeName, out var bound))
        {
            return bound;
        }

        var substituted = new TypePath
        {
            SymbolId = typePath.SymbolId
        };
        substituted.SetTypeName(typePath.TypeName);
        foreach (var modulePart in typePath.ModulePath)
        {
            substituted.ModulePath.Add(modulePart);
        }

        foreach (var typeArg in typePath.TypeArgs)
        {
            substituted.TypeArgs.Add(SubstituteTypeNode(typeArg, bindings));
        }

        return substituted;
    }
}
