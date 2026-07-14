using Eidosc.Symbols;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Types;
using Eidosc.Diagnostic;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private static Dictionary<string, string>? BuildTraitTypeArgBindings(
        TraitDef traitDefinition,
        IReadOnlyList<string> traitTypeArgs,
        out string reason)
    {
        reason = string.Empty;
        if (traitDefinition.TypeParams.Count == 0)
        {
            if (traitTypeArgs.Count == 0)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            reason = DiagnosticMessages.TraitDoesNotAcceptTypeArgumentsInImpl(traitDefinition.Name);
            return null;
        }

        if (traitTypeArgs.Count == 0)
        {
            reason = DiagnosticMessages.TraitExpectsTypeArgumentsInImpl(
                traitDefinition.Name,
                traitDefinition.TypeParams.Count,
                traitTypeArgs.Count);
            return null;
        }

        if (traitTypeArgs.Count != traitDefinition.TypeParams.Count)
        {
            reason = DiagnosticMessages.TraitExpectsTypeArgumentsInImpl(
                traitDefinition.Name,
                traitDefinition.TypeParams.Count,
                traitTypeArgs.Count);
            return null;
        }

        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < traitDefinition.TypeParams.Count; i++)
        {
            var paramName = traitDefinition.TypeParams[i].Name;
            if (string.IsNullOrWhiteSpace(paramName))
            {
                continue;
            }

            var argText = RemoveInsignificantTypeWhitespace(traitTypeArgs[i]);
            bindings[paramName] = argText;
        }

        return bindings;
    }

    private bool IsTraitDefinedInCurrentModule(SymbolId traitId)
    {
        return _traitOwnerModules.TryGetValue(traitId, out var ownerModule) && ownerModule.Equals(_currentModule);
    }

    private static bool AreSignaturesEquivalent(
        FuncDef expected,
        FuncDef actual,
        TypePath implementingTypePath,
        IReadOnlyDictionary<string, string>? traitTypeArgBindings)
    {
        var expectedText = NormalizeSignature(expected, implementingTypePath, traitTypeArgBindings);
        var actualText = NormalizeSignature(actual, selfType: null, traitTypeArgBindings: null);
        return string.Equals(expectedText, actualText, StringComparison.Ordinal);
    }

    private static string NormalizeSignature(
        FuncDecl declaration,
        TypePath? selfType,
        IReadOnlyDictionary<string, string>? traitTypeArgBindings) =>
        NormalizeSignature(
            declaration.Signature,
            declaration.RequiredAbilities,
            declaration.TypeParams,
            selfType,
            traitTypeArgBindings);

    private static string NormalizeSignature(
        FuncDef definition,
        TypePath? selfType,
        IReadOnlyDictionary<string, string>? traitTypeArgBindings) =>
        NormalizeSignature(
            definition.Signature,
            definition.RequiredAbilities,
            definition.TypeParams,
            selfType,
            traitTypeArgBindings);

    private static string NormalizeSignature(
        IReadOnlyList<TypeNode> signature,
        IReadOnlyList<EffectRequirementNode> requiredAbilities,
        IReadOnlyList<TypeParam> typeParams,
        TypePath? selfType,
        IReadOnlyDictionary<string, string>? traitTypeArgBindings)
    {
        if (signature.Count == 0)
        {
            return "<empty>";
        }

        var signatureBindings = BuildSignatureBindings(typeParams, traitTypeArgBindings);
        var typeText = string.Join(" | ", signature.Select(node => NormalizeFunctionSignatureTypeNode(node, selfType, signatureBindings)));
        var abilityText = NormalizeEffectRequirements(requiredAbilities, selfType, signatureBindings);
        return abilityText.Length == 0
            ? typeText
            : $"{typeText} need {abilityText}";
    }

    private static IReadOnlyDictionary<string, string>? BuildSignatureBindings(
        IReadOnlyList<TypeParam> typeParams,
        IReadOnlyDictionary<string, string>? traitTypeArgBindings)
    {
        Dictionary<string, string>? bindings = traitTypeArgBindings == null
            ? null
            : new Dictionary<string, string>(traitTypeArgBindings, StringComparer.Ordinal);
        var effectIndex = 0;
        foreach (var typeParam in typeParams)
        {
            if (!typeParam.IsEffectSet || string.IsNullOrWhiteSpace(typeParam.Name))
            {
                continue;
            }

            bindings ??= new Dictionary<string, string>(StringComparer.Ordinal);
            bindings[typeParam.Name] = $"$effect{effectIndex++}";
        }

        return bindings;
    }

    private static string NormalizeEffectRequirements(
        IReadOnlyList<EffectRequirementNode> requiredAbilities,
        TypePath? selfType,
        IReadOnlyDictionary<string, string>? traitTypeArgBindings)
    {
        if (requiredAbilities.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            ", ",
            requiredAbilities
                .Select(requirement => NormalizeEffectRequirement(requirement, selfType, traitTypeArgBindings))
                .Where(requirement => requirement.Length > 0)
                .OrderBy(requirement => requirement, StringComparer.Ordinal));
    }

    private static string NormalizeEffectRequirement(
        EffectRequirementNode requirement,
        TypePath? selfType,
        IReadOnlyDictionary<string, string>? traitTypeArgBindings)
    {
        var path = requirement.Path
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part.Trim())
            .ToList();
        if (path.Count == 0)
        {
            return string.Empty;
        }

        if (path.Count == 1)
        {
            var name = path[0];
            if (selfType != null &&
                string.Equals(name, WellKnownStrings.Keywords.Self, StringComparison.Ordinal))
            {
                return NormalizeTypePath(selfType, selfType: null, traitTypeArgBindings: null);
            }

            if (traitTypeArgBindings != null &&
                traitTypeArgBindings.TryGetValue(name, out var traitArg))
            {
                return traitArg;
            }
        }

        return string.Join(WellKnownStrings.Separators.Path, path);
    }

    private static string NormalizeTypeNode(
        TypeNode node,
        TypePath? selfType,
        IReadOnlyDictionary<string, string>? traitTypeArgBindings)
    {
        return node switch
        {
            TypePath typePath => NormalizeTypePath(typePath, selfType, traitTypeArgBindings),
            ArrowType arrow => NormalizeArrowType(arrow, selfType, traitTypeArgBindings),
            EffectfulType effectful => NormalizeEffectfulType(effectful, selfType, traitTypeArgBindings),
            TupleType tuple => $"({string.Join(",", tuple.Elements.Select(element => NormalizeTypeNode(element, selfType, traitTypeArgBindings)))})",
            WildcardType => "_",
            _ => node.GetType().Name
        };
    }

    private static string NormalizeFunctionSignatureTypeNode(
        TypeNode node,
        TypePath? selfType,
        IReadOnlyDictionary<string, string>? traitTypeArgBindings)
    {
        if (node is not ArrowType arrow)
        {
            return NormalizeTypeNode(node, selfType, traitTypeArgBindings);
        }

        return $"{NormalizeTypeNode(arrow.ParamType, selfType, traitTypeArgBindings)}->{NormalizeFunctionSignatureTypeNode(arrow.ReturnType, selfType, traitTypeArgBindings)}";
    }

    private static string NormalizeArrowType(
        ArrowType arrow,
        TypePath? selfType,
        IReadOnlyDictionary<string, string>? traitTypeArgBindings)
    {
        var normalized = $"{NormalizeTypeNode(arrow.ParamType, selfType, traitTypeArgBindings)}->{NormalizeTypeNode(arrow.ReturnType, selfType, traitTypeArgBindings)}";
        var effects = NormalizeEffectRequirements(arrow.RequiredEffects, selfType, traitTypeArgBindings);
        return effects.Length == 0 ? normalized : $"{normalized} need {effects}";
    }

    private static string NormalizeEffectfulType(
        EffectfulType effectful,
        TypePath? selfType,
        IReadOnlyDictionary<string, string>? traitTypeArgBindings)
    {
        var effectPaths = effectful.EnumerateEffectPaths()
            .Select(path => path
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(part => part.Trim())
                .ToList())
            .Where(path => path.Count > 0)
            .Select(path => NormalizeEffectPath(path, selfType, traitTypeArgBindings))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        var input = NormalizeTypeNode(effectful.InputType, selfType, traitTypeArgBindings);
        if (effectPaths.Count == 0)
        {
            var pureOutput = effectful.OutputType == null
                ? WellKnownStrings.BuiltinTypes.Unit
                : NormalizeTypeNode(effectful.OutputType, selfType, traitTypeArgBindings);
            return $"{input}->{pureOutput}";
        }

        var effectSetText = "{" + string.Join(", ", effectPaths) + "}";
        return effectful.OutputType == null
            ? $"{input}->{effectSetText}"
            : $"{input}->{effectSetText}->{NormalizeTypeNode(effectful.OutputType, selfType, traitTypeArgBindings)}";
    }

    private static string NormalizeEffectPath(
        IReadOnlyList<string> path,
        TypePath? selfType,
        IReadOnlyDictionary<string, string>? traitTypeArgBindings)
    {
        if (path.Count == 1)
        {
            var name = path[0];
            if (selfType != null && string.Equals(name, WellKnownStrings.Keywords.Self, StringComparison.Ordinal))
            {
                return NormalizeTypePath(selfType, selfType: null, traitTypeArgBindings: null);
            }

            if (traitTypeArgBindings != null && traitTypeArgBindings.TryGetValue(name, out var binding))
            {
                return binding;
            }
        }

        return string.Join(WellKnownStrings.Separators.Path, path);
    }

    private static string NormalizeTypePath(
        TypePath typePath,
        TypePath? selfType,
        IReadOnlyDictionary<string, string>? traitTypeArgBindings)
    {
        if (selfType != null &&
            typePath.ModulePath.Count == 0 &&
            string.Equals(typePath.TypeName, WellKnownStrings.Keywords.Self, StringComparison.Ordinal))
        {
            return NormalizeTypePath(selfType, selfType: null, traitTypeArgBindings: null);
        }

        if (typePath.ModulePath.Count == 0 &&
            traitTypeArgBindings != null &&
            traitTypeArgBindings.TryGetValue(typePath.TypeName, out var traitArg))
        {
            if (typePath.GenericArguments.Count == 0 && typePath.TypeArgs.Count == 0)
            {
                return traitArg;
            }

            var appliedArgs = string.Join(",", NormalizeGenericArguments(
                typePath,
                selfType,
                traitTypeArgBindings));
            if (TrySplitNormalizedTypeApplication(traitArg, out var baseName, out var existingArgs))
            {
                var mergedArgs = string.IsNullOrWhiteSpace(existingArgs)
                    ? appliedArgs
                    : $"{existingArgs},{appliedArgs}";
                return $"{baseName}[{mergedArgs}]";
            }

            return $"{traitArg}[{appliedArgs}]";
        }

        if (typePath.SymbolId.IsValid)
        {
            var symbolName = typePath.TypeName;
            if (typePath.GenericArguments.Count == 0 && typePath.TypeArgs.Count == 0)
            {
                return symbolName;
            }

            return $"{symbolName}[{string.Join(",", NormalizeGenericArguments(typePath, selfType, traitTypeArgBindings))}]";
        }

        var name = typePath.ModulePath.Count > 0
            ? string.Join(WellKnownStrings.Separators.Path, typePath.ModulePath) + WellKnownStrings.Separators.Path + typePath.TypeName
            : typePath.TypeName;

        if (typePath.GenericArguments.Count == 0 && typePath.TypeArgs.Count == 0)
        {
            return name;
        }

        return $"{name}[{string.Join(",", NormalizeGenericArguments(typePath, selfType, traitTypeArgBindings))}]";
    }

    private static IEnumerable<string> NormalizeGenericArguments(
        TypePath typePath,
        TypePath? selfType,
        IReadOnlyDictionary<string, string>? traitTypeArgBindings)
    {
        if (typePath.GenericArguments.Count == 0)
        {
            return typePath.TypeArgs.Select(argument =>
                NormalizeTypeNode(argument, selfType, traitTypeArgBindings));
        }

        return typePath.GenericArguments.Select(argument => argument switch
        {
            TypeGenericArgumentNode typeArgument =>
                NormalizeTypeNode(typeArgument.Type, selfType, traitTypeArgBindings),
            UnresolvedGenericArgumentNode { TypeCandidate: { } typeCandidate } =>
                NormalizeTypeNode(typeCandidate, selfType, traitTypeArgBindings),
            ValueGenericArgumentNode valueArgument =>
                NormalizeValueGenericArgument(valueArgument.Expression, traitTypeArgBindings),
            _ => "_"
        });
    }

    private static string NormalizeValueGenericArgument(
        Eidosc.Ast.EidosAstNode expression,
        IReadOnlyDictionary<string, string>? traitTypeArgBindings)
    {
        return expression switch
        {
            IdentifierExpr identifier when traitTypeArgBindings != null &&
                                           traitTypeArgBindings.TryGetValue(identifier.Name, out var identifierBinding) =>
                identifierBinding,
            PathExpr { ModulePath.Count: 0 } path when traitTypeArgBindings != null &&
                                                        traitTypeArgBindings.TryGetValue(path.Name, out var pathBinding) =>
                pathBinding,
            LiteralExpr literal when !string.IsNullOrWhiteSpace(literal.RawText) => literal.RawText,
            IdentifierExpr identifier => identifier.Name,
            PathExpr path => string.Join(WellKnownStrings.Separators.Path, path.Path),
            _ => expression.GetType().Name
        };
    }

    private static bool TrySplitNormalizedTypeApplication(
        string normalized,
        out string baseName,
        out string existingArgs)
    {
        baseName = normalized;
        existingArgs = string.Empty;

        if (string.IsNullOrWhiteSpace(normalized) || normalized[^1] != ']')
        {
            return false;
        }

        var depth = 0;
        for (var i = normalized.Length - 1; i >= 0; i--)
        {
            switch (normalized[i])
            {
                case ']':
                    depth++;
                    break;

                case '[':
                    depth--;
                    if (depth == 0)
                    {
                        baseName = normalized[..i];
                        existingArgs = normalized[(i + 1)..^1];
                        return !string.IsNullOrWhiteSpace(baseName);
                    }
                    break;
            }
        }

        return false;
    }

    private static string RemoveInsignificantTypeWhitespace(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (!char.IsWhiteSpace(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }
}
