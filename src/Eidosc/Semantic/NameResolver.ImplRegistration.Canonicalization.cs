using Eidosc.Symbols;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Types;
using Eidosc.Types;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private List<string> CanonicalizeImplTraitTypeArgs(ImplTraitReference traitRef)
    {
        if (traitRef.GenericArguments.Count > 0)
        {
            return traitRef.GenericArguments
                .Select((argument, parameterIndex) =>
                    CanonicalizeImplGenericArgument(
                        argument,
                        parameterIndex,
                        new Dictionary<string, string>(StringComparer.Ordinal)))
                .ToList();
        }

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
            typePath.GenericArguments.Count == 0 &&
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
        if (typePath.GenericArguments.Count == 0 && typePath.TypeArgs.Count == 0)
        {
            return name;
        }

        var arguments = typePath.GenericArguments.Count > 0
            ? typePath.GenericArguments
                .Select((argument, parameterIndex) =>
                    CanonicalizeImplGenericArgument(argument, parameterIndex, textBindings))
            : typePath.TypeArgs.Select(typeArg => CanonicalizeTypeNodeForImplHead(typeArg, textBindings));
        return $"{name}[{string.Join(",", arguments)}]";
    }

    private string CanonicalizeImplGenericArgument(
        GenericArgumentNode argument,
        int parameterIndex,
        IReadOnlyDictionary<string, string> textBindings)
    {
        return argument switch
        {
            TypeGenericArgumentNode typeArgument =>
                CanonicalizeTypeNodeForImplHead(typeArgument.Type, textBindings),
            UnresolvedGenericArgumentNode { TypeCandidate: { } typeCandidate } =>
                CanonicalizeTypeNodeForImplHead(typeCandidate, textBindings),
            ValueGenericArgumentNode valueArgument =>
                CanonicalizeImplValueArgument(valueArgument.Expression, parameterIndex),
            _ => "_"
        };
    }

    private string CanonicalizeImplValueArgument(Eidosc.Ast.EidosAstNode expression, int parameterIndex)
    {
        var key = BuildImplValueRefKey(expression, parameterIndex);
        if (key.ValueArgument is not { } valueArgument)
        {
            return "_";
        }

        if (!valueArgument.IsConcrete)
        {
            return string.IsNullOrWhiteSpace(valueArgument.DisplayText)
                ? valueArgument.VariableIdentity
                : valueArgument.DisplayText;
        }

        return expression switch
        {
            LiteralExpr literal when !string.IsNullOrWhiteSpace(literal.RawText) => literal.RawText,
            IdentifierExpr identifier => identifier.Name,
            PathExpr path => string.Join(WellKnownStrings.Separators.Path, path.Path),
            _ when !string.IsNullOrWhiteSpace(valueArgument.DisplayText) => valueArgument.DisplayText,
            _ => valueArgument.CanonicalPayload
        };
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
        var valueArgBindings = new Dictionary<string, Eidosc.Ast.EidosAstNode>(StringComparer.Ordinal);
        if (typePath.GenericArguments.Count > 0)
        {
            for (var parameterIndex = 0;
                 parameterIndex < adtDefinition.TypeParams.Count &&
                 parameterIndex < typePath.GenericArguments.Count;
                 parameterIndex++)
            {
                var parameter = adtDefinition.TypeParams[parameterIndex];
                if (string.IsNullOrWhiteSpace(parameter.Name))
                {
                    continue;
                }

                switch (parameter.ParameterKind, typePath.GenericArguments[parameterIndex])
                {
                    case (GenericParameterKind.Type, TypeGenericArgumentNode typeArgument):
                        typeArgBindings[parameter.Name] = typeArgument.Type;
                        break;
                    case (GenericParameterKind.Type, UnresolvedGenericArgumentNode { TypeCandidate: { } typeCandidate }):
                        typeArgBindings[parameter.Name] = typeCandidate;
                        break;
                    case (GenericParameterKind.Value, ValueGenericArgumentNode valueArgument):
                        valueArgBindings[parameter.Name] = valueArgument.Expression;
                        break;
                    case (GenericParameterKind.Value, UnresolvedGenericArgumentNode { ValueCandidate: { } valueCandidate }):
                        valueArgBindings[parameter.Name] = valueCandidate;
                        break;
                }
            }
        }
        else
        {
            var typeArgumentIndex = 0;
            foreach (var parameter in adtDefinition.TypeParams)
            {
                if (parameter.ParameterKind != GenericParameterKind.Type ||
                    string.IsNullOrWhiteSpace(parameter.Name) ||
                    typeArgumentIndex >= typePath.TypeArgs.Count)
                {
                    continue;
                }

                typeArgBindings[parameter.Name] = typePath.TypeArgs[typeArgumentIndex++];
            }
        }

        expanded = SubstituteTypeNode(adtDefinition.AliasTarget, typeArgBindings, valueArgBindings);
        return true;
    }

    private TypeNode SubstituteTypeNode(
        TypeNode node,
        IReadOnlyDictionary<string, TypeNode> typeBindings,
        IReadOnlyDictionary<string, Eidosc.Ast.EidosAstNode> valueBindings)
    {
        return node switch
        {
            TypePath typePath => SubstituteTypePath(typePath, typeBindings, valueBindings),
            TupleType tuple => SubstituteTupleType(tuple, typeBindings, valueBindings),
            ArrowType arrow => SubstituteArrowType(arrow, typeBindings, valueBindings),
            EffectfulType effectful => effectful,
            _ => node
        };
    }

    private TypeNode SubstituteTupleType(
        TupleType tuple,
        IReadOnlyDictionary<string, TypeNode> typeBindings,
        IReadOnlyDictionary<string, Eidosc.Ast.EidosAstNode> valueBindings)
    {
        var substituted = new TupleType();
        foreach (var element in tuple.Elements)
        {
            substituted.Elements.Add(SubstituteTypeNode(element, typeBindings, valueBindings));
        }

        return substituted;
    }

    private TypeNode SubstituteArrowType(
        ArrowType arrow,
        IReadOnlyDictionary<string, TypeNode> typeBindings,
        IReadOnlyDictionary<string, Eidosc.Ast.EidosAstNode> valueBindings)
    {
        var substituted = new ArrowType();
        substituted.SetParamType(SubstituteTypeNode(arrow.ParamType, typeBindings, valueBindings));
        substituted.SetReturnType(SubstituteTypeNode(arrow.ReturnType, typeBindings, valueBindings));
        return substituted;
    }

    private TypeNode SubstituteTypePath(
        TypePath typePath,
        IReadOnlyDictionary<string, TypeNode> typeBindings,
        IReadOnlyDictionary<string, Eidosc.Ast.EidosAstNode> valueBindings)
    {
        if (typePath.ModulePath.Count == 0 &&
            typePath.GenericArguments.Count == 0 &&
            typePath.TypeArgs.Count == 0 &&
            typeBindings.TryGetValue(typePath.TypeName, out var bound))
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

        if (typePath.GenericArguments.Count > 0)
        {
            substituted.SetGenericArguments(typePath.GenericArguments.Select(argument => argument switch
            {
                TypeGenericArgumentNode typeArgument => new TypeGenericArgumentNode
                {
                    Type = SubstituteTypeNode(typeArgument.Type, typeBindings, valueBindings),
                    Span = typeArgument.Span
                },
                ValueGenericArgumentNode valueArgument => new ValueGenericArgumentNode
                {
                    Expression = SubstituteValueGenericExpression(valueArgument.Expression, valueBindings),
                    Span = valueArgument.Span
                },
                EffectGenericArgumentNode effectArgument => new EffectGenericArgumentNode
                {
                    EffectRow = SubstituteTypeNode(effectArgument.EffectRow, typeBindings, valueBindings),
                    Span = effectArgument.Span
                },
                UnresolvedGenericArgumentNode unresolved => new UnresolvedGenericArgumentNode
                {
                    TypeCandidate = unresolved.TypeCandidate == null
                        ? null
                        : SubstituteTypeNode(unresolved.TypeCandidate, typeBindings, valueBindings),
                    ValueCandidate = unresolved.ValueCandidate == null
                        ? null
                        : SubstituteValueGenericExpression(unresolved.ValueCandidate, valueBindings),
                    Span = unresolved.Span
                },
                _ => argument
            }));
        }
        else
        {
            foreach (var typeArg in typePath.TypeArgs)
            {
                substituted.TypeArgs.Add(SubstituteTypeNode(typeArg, typeBindings, valueBindings));
            }
        }

        return substituted;
    }

    private static Eidosc.Ast.EidosAstNode SubstituteValueGenericExpression(
        Eidosc.Ast.EidosAstNode expression,
        IReadOnlyDictionary<string, Eidosc.Ast.EidosAstNode> valueBindings)
    {
        var name = expression switch
        {
            IdentifierExpr identifier => identifier.Name,
            PathExpr { ModulePath.Count: 0 } path => path.Name,
            _ => null
        };

        return name != null && valueBindings.TryGetValue(name, out var bound)
            ? bound
            : expression;
    }
}
