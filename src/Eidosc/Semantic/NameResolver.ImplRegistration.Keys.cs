using Eidosc.Symbols;
using System.Collections.Immutable;
using Eidosc.Ast;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Types;
using Eidosc.Types;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private List<ImplTypeRefKey> BuildImplTraitTypeArgKeys(SymbolId traitId, ImplTraitReference traitRef)
    {
        if (traitRef.GenericArguments.Count > 0)
        {
            return traitRef.GenericArguments
                .Select((argument, parameterIndex) =>
                    BuildImplTraitGenericArgumentKey(traitId, argument, parameterIndex))
                .ToList();
        }

        if (traitRef.TypeArgs.Count > 0)
        {
            return traitRef.TypeArgs
                .Select((argument, parameterIndex) =>
                    GetImplTraitGenericParameterKind(traitId, parameterIndex) == GenericParameterKind.Value &&
                    argument is TypePath valueCandidate
                        ? BuildImplValueRefKey(ConvertTypePathToValueExpression(valueCandidate), parameterIndex)
                        : BuildImplTypeRefKey(argument))
                .ToList();
        }

        return traitRef.TypeArgTexts
            .Select((argumentText, parameterIndex) =>
                GetImplTraitGenericParameterKind(traitId, parameterIndex) == GenericParameterKind.Value
                    ? BuildImplValueRefKey(ConvertTextToImplValueExpression(argumentText), parameterIndex)
                    : ImplTypeRefKey.FromCanonicalText(argumentText))
            .ToList();
    }

    private ImplTypeRefKey BuildImplTraitGenericArgumentKey(
        SymbolId traitId,
        GenericArgumentNode argument,
        int parameterIndex)
    {
        if (GetImplTraitGenericParameterKind(traitId, parameterIndex) != GenericParameterKind.Value)
        {
            return BuildImplGenericArgumentKey(argument, parameterIndex);
        }

        var expression = argument switch
        {
            ValueGenericArgumentNode valueArgument => valueArgument.Expression,
            UnresolvedGenericArgumentNode { ValueCandidate: { } valueCandidate } => valueCandidate,
            TypeGenericArgumentNode { Type: TypePath typeCandidate } =>
                ConvertTypePathToValueExpression(typeCandidate),
            UnresolvedGenericArgumentNode { TypeCandidate: TypePath typeCandidate } =>
                ConvertTypePathToValueExpression(typeCandidate),
            _ => null
        };
        return expression == null
            ? ImplTypeRefKey.Empty
            : BuildImplValueRefKey(expression, parameterIndex);
    }

    private GenericParameterKind GetImplTraitGenericParameterKind(SymbolId traitId, int parameterIndex)
    {
        if (_traitDefinitions.TryGetValue(traitId, out var traitDefinition) &&
            parameterIndex < traitDefinition.TypeParams.Count)
        {
            return traitDefinition.TypeParams[parameterIndex].ParameterKind;
        }

        var parameterIds = _symbolTable.GetSymbol<TraitSymbol>(traitId)?.TypeParams;
        return parameterIds != null &&
               parameterIndex < parameterIds.Count &&
               _symbolTable.GetSymbol<TypeParamSymbol>(parameterIds[parameterIndex]) is { } parameter
            ? parameter.ParameterKind
            : GenericParameterKind.Type;
    }

    private static EidosAstNode ConvertTextToImplValueExpression(string text)
    {
        var candidate = new TypePath();
        candidate.SetTypeName(text.Trim());
        return ConvertTypePathToValueExpression(candidate);
    }

    private List<ImplTypeRefKey> BuildCanonicalImplTraitTypeArgKeys(
        IReadOnlyList<string> canonicalTraitTypeArgs,
        IReadOnlyList<ImplTypeRefKey>? structuredTraitTypeArgKeys = null)
    {
        var result = new List<ImplTypeRefKey>(canonicalTraitTypeArgs.Count);
        for (var index = 0; index < canonicalTraitTypeArgs.Count; index++)
        {
            if (structuredTraitTypeArgKeys != null &&
                index < structuredTraitTypeArgKeys.Count &&
                structuredTraitTypeArgKeys[index].ValueArgument != null)
            {
                result.Add(structuredTraitTypeArgKeys[index]);
                continue;
            }

            result.Add(BuildCanonicalImplTypeRefKey(canonicalTraitTypeArgs[index]));
        }

        return result;
    }

    private ImplTypeRefKey BuildImplGenericArgumentKey(GenericArgumentNode argument, int parameterIndex)
    {
        return argument switch
        {
            TypeGenericArgumentNode typeArgument => BuildImplTypeRefKey(typeArgument.Type),
            UnresolvedGenericArgumentNode { TypeCandidate: { } typeCandidate } => BuildImplTypeRefKey(typeCandidate),
            ValueGenericArgumentNode valueArgument => BuildImplValueRefKey(valueArgument.Expression, parameterIndex),
            EffectGenericArgumentNode effectArgument => BuildImplTypeRefKey(effectArgument.EffectRow),
            _ => ImplTypeRefKey.Empty
        };
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
            BuildImplGenericArgumentKeys(typePath).ToImmutableArray());
    }

    private List<ImplTypeRefKey> BuildImplGenericArgumentKeys(TypePath typePath)
    {
        if (typePath.GenericArguments.Count == 0)
        {
            return typePath.TypeArgs.Select(BuildImplTypeRefKey).ToList();
        }

        var keys = new List<ImplTypeRefKey>(typePath.GenericArguments.Count);
        for (var parameterIndex = 0; parameterIndex < typePath.GenericArguments.Count; parameterIndex++)
        {
            keys.Add(BuildImplGenericArgumentKey(typePath.GenericArguments[parameterIndex], parameterIndex));
        }

        return keys;
    }

    private ImplTypeRefKey BuildImplValueRefKey(EidosAstNode expression, int parameterIndex)
    {
        var symbolId = expression switch
        {
            IdentifierExpr identifier => identifier.SymbolId,
            PathExpr path => path.SymbolId,
            _ => SymbolId.None
        };
        if (symbolId.IsValid &&
            _symbolTable.GetSymbol<TypeParamSymbol>(symbolId) is
            {
                ParameterKind: GenericParameterKind.Value
            } parameter)
        {
            return ImplTypeRefKey.FromValueArgument(new ImplValueRefKey(
                parameterIndex,
                "",
                parameter.TypeId,
                $"symbol:{symbolId.Value}",
                parameter.Name));
        }

        if (ComptimeEvaluator.TryEvaluate(
                expression,
                new Dictionary<SymbolId, ComptimeValue>(),
                out var value,
                out _))
        {
            return ImplTypeRefKey.FromValueArgument(new ImplValueRefKey(
                parameterIndex,
                ImplValueRefKey.NormalizeCanonicalPayload(value.CanonicalText),
                ResolveImplValueTypeId(expression, value),
                DisplayText: FormatImplComptimeValue(value)));
        }

        return ImplTypeRefKey.FromValueArgument(new ImplValueRefKey(
            parameterIndex,
            "",
            TypeId.None,
            $"expression:{expression.Span.Position}:{expression.Span.Length}",
            expression.GetType().Name));
    }

    private static TypeId ResolveImplValueTypeId(EidosAstNode expression, ComptimeValue value)
    {
        if (expression.InferredType is Eidosc.Types.Type { Id.IsValid: true } inferredType)
        {
            return inferredType.Id;
        }

        return value switch
        {
            ComptimeIntegerValue => new TypeId(BaseTypes.IntId),
            ComptimeFloatValue => new TypeId(BaseTypes.FloatId),
            ComptimeBoolValue => new TypeId(BaseTypes.BoolId),
            ComptimeStringValue => new TypeId(BaseTypes.StringId),
            ComptimeCharValue => new TypeId(BaseTypes.CharId),
            ComptimeUnitValue => new TypeId(BaseTypes.UnitId),
            _ => TypeId.None
        };
    }

    private static string FormatImplComptimeValue(ComptimeValue value) => value switch
    {
        ComptimeUnitValue => "()",
        ComptimeBoolValue scalar => scalar.Value ? "true" : "false",
        ComptimeIntegerValue scalar => scalar.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ComptimeFloatValue scalar => scalar.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        ComptimeCharValue scalar => $"'{scalar.Value}'",
        ComptimeStringValue scalar => $"\"{scalar.Value}\"",
        _ => value.CanonicalText
    };

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
