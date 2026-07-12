using Eidosc.Symbols;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Types;
using Eidosc.Diagnostic;
using Eidosc.Semantic;

namespace Eidosc.Types;

public sealed partial class TypeInferer
{
    private List<Kind> CreateExpectedKinds(
        IReadOnlyList<TypeParam> declaredTypeParams,
        IReadOnlyList<string> typeParamNames,
        KindInferer kindUnifier)
    {
        var expectedKinds = new List<Kind>(typeParamNames.Count);
        for (var i = 0; i < typeParamNames.Count; i++)
        {
            expectedKinds.Add(CreateDeclaredOrFreshTypeParamKind(
                declaredTypeParams,
                i,
                kindUnifier));
        }

        return expectedKinds;
    }

    private static Dictionary<string, Kind> CreateTypeParamKindBindings(
        IReadOnlyList<string> typeParamNames,
        IReadOnlyList<Kind> expectedKinds)
    {
        var kindByTypeParamName = new Dictionary<string, Kind>(typeParamNames.Count, StringComparer.Ordinal);
        var matchCount = Math.Min(typeParamNames.Count, expectedKinds.Count);
        for (var i = 0; i < matchCount; i++)
        {
            kindByTypeParamName[typeParamNames[i]] = expectedKinds[i];
        }

        return kindByTypeParamName;
    }

    private void FinalizeExpectedKindsInPlace(List<Kind> expectedKinds)
    {
        for (var i = 0; i < expectedKinds.Count; i++)
        {
            var resolvedKind = ResolveKind(expectedKinds[i]);
            if (resolvedKind is Kind.KVar unresolvedKindVar &&
                unresolvedKindVar.Instance == null)
            {
                resolvedKind = Kind.KStar.Instance;
            }

            expectedKinds[i] = resolvedKind;
        }
    }

    private void ApplyFuncDeclTypeParamKindInference(
        FuncDecl funcDecl,
        IReadOnlyDictionary<string, Kind> ownerKindByTypeParamName,
        KindInferer kindUnifier,
        string ownerKind,
        string ownerName)
    {
        ApplyFuncDefTypeParamKindInference(
            funcDecl.TypeParams, funcDecl.Signature,
            ownerKindByTypeParamName, kindUnifier, ownerKind, ownerName,
            funcDecl.TypeParams);
    }

    private void ApplyFuncDefTypeParamKindInference(
        FuncDef funcDecl,
        IReadOnlyDictionary<string, Kind> ownerKindByTypeParamName,
        KindInferer kindUnifier,
        string ownerKind,
        string ownerName)
    {
        ApplyFuncDefTypeParamKindInference(
            funcDecl.TypeParams, funcDecl.Signature,
            ownerKindByTypeParamName, kindUnifier, ownerKind, ownerName,
            funcDecl.TypeParams);
    }

    private void ApplyFuncDefTypeParamKindInference(
        List<Ast.Types.TypeParam> typeParams,
        List<Ast.Types.TypeNode> signature,
        IReadOnlyDictionary<string, Kind> ownerKindByTypeParamName,
        KindInferer kindUnifier,
        string ownerKind,
        string ownerName,
        List<Ast.Types.TypeParam> originalTypeParams)
    {
        var methodTypeParamNames = new List<string>(typeParams.Count);
        foreach (var typeParam in typeParams)
        {
            if (!string.IsNullOrWhiteSpace(typeParam.Name))
            {
                methodTypeParamNames.Add(typeParam.Name);
            }
        }

        var methodExpectedKinds = CreateExpectedKinds(
            typeParams,
            methodTypeParamNames,
            kindUnifier);
        var mergedKindByTypeParamName = new Dictionary<string, Kind>(ownerKindByTypeParamName, StringComparer.Ordinal);
        var matchCount = Math.Min(methodTypeParamNames.Count, methodExpectedKinds.Count);
        for (var i = 0; i < matchCount; i++)
        {
            mergedKindByTypeParamName[methodTypeParamNames[i]] = methodExpectedKinds[i];
        }

        ApplyTypeParamKindInference(
            signature,
            mergedKindByTypeParamName,
            kindUnifier,
            ownerKind,
            ownerName);

        FinalizeExpectedKindsInPlace(methodExpectedKinds);
        UpdateTypeParamSymbolsWithKinds(originalTypeParams, methodExpectedKinds);
    }

    private void ApplyProofDeclTypeParamKindInference(
        ProofDecl proofDecl,
        IReadOnlyDictionary<string, Kind> ownerKindByTypeParamName,
        KindInferer kindUnifier,
        string ownerKind,
        string ownerName)
    {
        var proofTypeParamNames = new List<string>(proofDecl.TypeParams.Count);
        foreach (var typeParam in proofDecl.TypeParams)
        {
            if (!string.IsNullOrWhiteSpace(typeParam.Name))
            {
                proofTypeParamNames.Add(typeParam.Name);
            }
        }

        var proofExpectedKinds = CreateExpectedKinds(
            proofDecl.TypeParams,
            proofTypeParamNames,
            kindUnifier);
        var mergedKindByTypeParamName = new Dictionary<string, Kind>(ownerKindByTypeParamName, StringComparer.Ordinal);
        var matchCount = Math.Min(proofTypeParamNames.Count, proofExpectedKinds.Count);
        for (var i = 0; i < matchCount; i++)
        {
            mergedKindByTypeParamName[proofTypeParamNames[i]] = proofExpectedKinds[i];
        }

        ApplyTypeParamKindInference(
            proofDecl.Parameters
                .Select(parameter => parameter.TypeAnnotation)
                .OfType<TypeNode>(),
            mergedKindByTypeParamName,
            kindUnifier,
            ownerKind,
            ownerName);

        FinalizeExpectedKindsInPlace(proofExpectedKinds);
        UpdateTypeParamSymbolsWithKinds(proofDecl.TypeParams, proofExpectedKinds);
    }

    private List<Kind> InferAndFinalizeTypeParamKinds(
        IReadOnlyList<TypeParam> declaredTypeParams,
        IReadOnlyList<string> typeParamNames,
        IEnumerable<TypeNode> usageTypeNodes,
        string ownerKind,
        string ownerName)
    {
        var kindUnifier = new KindInferer(
            _symbolTable,
            typeConstructorKindsBySymbol: _typeConstructorKindsBySymbol);
        var expectedKinds = CreateExpectedKinds(declaredTypeParams, typeParamNames, kindUnifier);
        var kindByTypeParamName = CreateTypeParamKindBindings(typeParamNames, expectedKinds);

        ApplyTypeParamKindInference(
            usageTypeNodes,
            kindByTypeParamName,
            kindUnifier,
            ownerKind,
            ownerName);

        FinalizeExpectedKindsInPlace(expectedKinds);

        UpdateTypeParamSymbolsWithKinds(declaredTypeParams, expectedKinds);
        return expectedKinds;
    }

    private Kind CreateDeclaredOrFreshTypeParamKind(
        IReadOnlyList<TypeParam> declaredTypeParams,
        int index,
        KindInferer kindUnifier)
    {
        if (index >= declaredTypeParams.Count)
        {
            return kindUnifier.FreshKindVariable();
        }

        var typeParam = declaredTypeParams[index];
        if (typeParam.KindAnnotation == null)
        {
            return kindUnifier.FreshKindVariable();
        }

        var kindText = typeParam.GetKindText();
        if (!KindParser.TryParse(kindText, out var parsedKind, out var parseError))
        {
            AddError(
                typeParam.Span,
                parseError ?? DiagnosticMessages.UnsupportedKindAnnotation(kindText));
            return Kind.KStar.Instance;
        }

        return parsedKind;
    }

    private void ApplyTypeParamKindInference(
        IEnumerable<TypeNode> usageTypeNodes,
        IReadOnlyDictionary<string, Kind> kindByTypeParamName,
        KindInferer kindUnifier,
        string ownerKind,
        string ownerName)
    {
        foreach (var typeNode in usageTypeNodes)
        {
            try
            {
                var nodeKind = InferTypeNodeKindForAdtInference(typeNode, kindByTypeParamName, kindUnifier);
                kindUnifier.UnifyKinds(nodeKind, Kind.KStar.Instance);
            }
            catch (KindUnificationException ex)
            {
                AddError(typeNode.Span, DiagnosticMessages.KindMismatchInDefinition(ownerKind, ownerName, ex.Message));
            }
        }
    }

    private static IEnumerable<TypeNode> EnumerateAdtKindInferenceTypeNodes(AdtDef adt)
    {
        if (IsTypeAliasDefinition(adt) && adt.AliasTarget != null)
        {
            yield return adt.AliasTarget;
        }

        // Bare product types carry their fields on a synthesized constructor
        // (Constructor.NamedArgs), so iterating adt.Fields here would double-yield.
        if (adt.Constructors.Count == 0)
        {
            foreach (var field in adt.Fields)
            {
                if (field.Type != null)
                {
                    yield return field.Type;
                }
            }
        }

        foreach (var ctor in adt.Constructors)
        {
            foreach (var positionalArg in ctor.PositionalArgs)
            {
                yield return positionalArg;
            }

            foreach (var namedArg in ctor.NamedArgs)
            {
                if (namedArg.Type != null)
                {
                    yield return namedArg.Type;
                }
            }
        }
    }

    private static IEnumerable<TypeNode> EnumerateConstructorKindInferenceTypeNodes(Constructor ctor)
    {
        foreach (var positionalArg in ctor.PositionalArgs)
        {
            yield return positionalArg;
        }

        foreach (var namedArg in ctor.NamedArgs)
        {
            if (namedArg.Type != null)
            {
                yield return namedArg.Type;
            }
        }

        if (ctor.ReturnType != null)
        {
            yield return ctor.ReturnType;
        }
    }

    private static IEnumerable<TypeNode> EnumerateTraitKindInferenceTypeNodes(TraitDef trait)
    {
        foreach (var method in trait.Methods)
        {
            foreach (var signatureType in method.Signature)
            {
                yield return signatureType;
            }
        }

    }

    private Kind InferTypeNodeKindForAdtInference(
        TypeNode typeNode,
        IReadOnlyDictionary<string, Kind> kindByTypeParamName,
        KindInferer kindUnifier)
    {
        return typeNode switch
        {
            TypePath path => InferTypePathKindForAdtInference(path, kindByTypeParamName, kindUnifier),
            ArrowType arrow => InferArrowTypeKindForAdtInference(arrow, kindByTypeParamName, kindUnifier),
            EffectfulType effectful => InferEffectfulTypeKindForAdtInference(effectful, kindByTypeParamName, kindUnifier),
            TupleType tuple => InferTupleTypeKindForAdtInference(tuple, kindByTypeParamName, kindUnifier),
            _ => Kind.KStar.Instance
        };
    }

    private Kind InferTypePathKindForAdtInference(
        TypePath path,
        IReadOnlyDictionary<string, Kind> kindByTypeParamName,
        KindInferer kindUnifier)
    {
        var constructorKind = ResolveKind(GetTypePathConstructorKindForAdtInference(path, kindByTypeParamName));
        if (path.TypeArgs.Count == 0)
        {
            return constructorKind;
        }

        var currentKind = constructorKind;
        foreach (var typeArg in path.TypeArgs)
        {
            var argumentKind = InferTypeNodeKindForAdtInference(typeArg, kindByTypeParamName, kindUnifier);
            currentKind = ApplyConstructorKindInAdtInference(currentKind, argumentKind, kindUnifier);
        }

        return ResolveKind(currentKind);
    }

    private static Kind ApplyConstructorKindInAdtInference(
        Kind constructorKind,
        Kind argumentKind,
        KindInferer kindUnifier)
    {
        var normalizedConstructorKind = ResolveKind(constructorKind);
        switch (normalizedConstructorKind)
        {
            case Kind.KVar kindVar when kindVar.Instance == null:
            {
                var resultKind = kindUnifier.FreshKindVariable();
                kindUnifier.UnifyKinds(kindVar, new Kind.KArrow(argumentKind, resultKind));
                return resultKind;
            }
            case Kind.KArrow arrow:
                kindUnifier.UnifyKinds(arrow.Param, argumentKind);
                return arrow.Result;
            default:
                throw new KindUnificationException(
                    DiagnosticMessages.KindCannotBeAppliedToAdditionalTypeArguments(
                        KindParser.ToKindText(normalizedConstructorKind)));
        }
    }

    private Kind GetTypePathConstructorKindForAdtInference(
        TypePath path,
        IReadOnlyDictionary<string, Kind> kindByTypeParamName)
    {
        if (TryGetBuiltinTypeConstructorKind(path.TypeName, out var builtinConstructorKind))
        {
            return builtinConstructorKind;
        }

        if (path.ModulePath.Count == 0 &&
            kindByTypeParamName.TryGetValue(path.TypeName, out var typeParamKind))
        {
            return typeParamKind;
        }

        if (path.SymbolId.IsValid)
        {
            return GetTypeConstructorKind(path.SymbolId);
        }

        if (IsBuiltinTypeName(path.TypeName))
        {
            return Kind.KStar.Instance;
        }

        return path.TypeArgs.Count > 0
            ? Kind.BuildArrowKind(path.TypeArgs.Count)
            : Kind.KStar.Instance;
    }

    private Kind InferArrowTypeKindForAdtInference(
        ArrowType arrow,
        IReadOnlyDictionary<string, Kind> kindByTypeParamName,
        KindInferer kindUnifier)
    {
        var paramKind = InferTypeNodeKindForAdtInference(arrow.ParamType, kindByTypeParamName, kindUnifier);
        kindUnifier.UnifyKinds(paramKind, Kind.KStar.Instance);
        var returnKind = InferTypeNodeKindForAdtInference(arrow.ReturnType, kindByTypeParamName, kindUnifier);
        kindUnifier.UnifyKinds(returnKind, Kind.KStar.Instance);
        return Kind.KStar.Instance;
    }

    private Kind InferEffectfulTypeKindForAdtInference(
        EffectfulType effectful,
        IReadOnlyDictionary<string, Kind> kindByTypeParamName,
        KindInferer kindUnifier)
    {
        var inputKind = InferTypeNodeKindForAdtInference(effectful.InputType, kindByTypeParamName, kindUnifier);
        kindUnifier.UnifyKinds(inputKind, Kind.KStar.Instance);

        if (effectful.OutputType != null)
        {
            var outputKind = InferTypeNodeKindForAdtInference(effectful.OutputType, kindByTypeParamName, kindUnifier);
            kindUnifier.UnifyKinds(outputKind, Kind.KStar.Instance);
        }

        return Kind.KStar.Instance;
    }

    private Kind InferTupleTypeKindForAdtInference(
        TupleType tuple,
        IReadOnlyDictionary<string, Kind> kindByTypeParamName,
        KindInferer kindUnifier)
    {
        foreach (var element in tuple.Elements)
        {
            var elementKind = InferTypeNodeKindForAdtInference(element, kindByTypeParamName, kindUnifier);
            kindUnifier.UnifyKinds(elementKind, Kind.KStar.Instance);
        }

        return Kind.KStar.Instance;
    }

    private static bool IsBuiltinTypeName(string typeName)
    {
        return typeName is WellKnownStrings.BuiltinTypes.Int or WellKnownStrings.BuiltinTypes.Float or WellKnownStrings.BuiltinTypes.Bool or WellKnownStrings.BuiltinTypes.String or WellKnownStrings.BuiltinTypes.Char or WellKnownStrings.BuiltinTypes.Unit or WellKnownStrings.BuiltinTypes.Never or "()" or WellKnownStrings.BuiltinTypes.Ref or WellKnownStrings.BuiltinTypes.MRef or WellKnownStrings.BuiltinTypes.MutRef or WellKnownStrings.BuiltinTypes.Shared;
    }

    private static bool TryGetBuiltinTypeConstructorKind(string typeName, out Kind kind)
    {
        switch (typeName)
        {
            case WellKnownStrings.BuiltinTypes.Ref:
            case WellKnownStrings.BuiltinTypes.MRef:
            case WellKnownStrings.BuiltinTypes.MutRef:
            case WellKnownStrings.BuiltinTypes.Shared:
                kind = Kind.BuildArrowKind(1);
                return true;
            default:
                kind = Kind.KStar.Instance;
                return false;
        }
    }

    private static bool IsTypeAliasDefinition(AdtDef adt)
    {
        return adt.IsTypeAlias &&
               adt.AliasTarget != null &&
               adt.Constructors.Count == 0 &&
               adt.Fields.Count == 0;
    }

    private static List<string> GetAdtTypeParamNames(AdtDef adt)
    {
        var names = new List<string>(adt.TypeParams.Count);
        for (var i = 0; i < adt.TypeParams.Count; i++)
        {
            var name = adt.TypeParams[i].Name;
            names.Add(string.IsNullOrWhiteSpace(name) ? $"__T{i}" : name);
        }

        return names;
    }

    private static List<string> GetConstructorTypeParamNames(Constructor ctor)
    {
        var names = new List<string>(ctor.TypeParams.Count);
        for (var i = 0; i < ctor.TypeParams.Count; i++)
        {
            var name = ctor.TypeParams[i].Name;
            names.Add(string.IsNullOrWhiteSpace(name) ? $"__C{i}" : name);
        }

        return names;
    }

    private void RegisterConstructorTypeParamKinds(
        AdtDef adt,
        Constructor ctor,
        IReadOnlyList<string> adtTypeParamNames,
        IReadOnlyList<string> ctorTypeParamNames)
    {
        if (!ctor.SymbolId.IsValid || ctorTypeParamNames.Count == 0)
        {
            return;
        }

        var kindUnifier = new KindInferer(
            _symbolTable,
            typeConstructorKindsBySymbol: _typeConstructorKindsBySymbol);
        var expectedKinds = CreateExpectedKinds(ctor.TypeParams, ctorTypeParamNames, kindUnifier);
        var kindByTypeParamName = CreateTypeParamKindMapForOwner(adt.SymbolId, adtTypeParamNames);
        var ctorKinds = CreateTypeParamKindBindings(ctorTypeParamNames, expectedKinds);
        foreach (var (name, kind) in ctorKinds)
        {
            kindByTypeParamName[name] = kind;
        }

        ApplyTypeParamKindInference(
            EnumerateConstructorKindInferenceTypeNodes(ctor),
            kindByTypeParamName,
            kindUnifier,
            ownerKind: "constructor",
            ownerName: ctor.Name);

        FinalizeExpectedKindsInPlace(expectedKinds);
        UpdateTypeParamSymbolsWithKinds(ctor.TypeParams, expectedKinds);

        _typeParamKindBindingsBySymbol[ctor.SymbolId] = new TypeParamKindBinding(
            ctor.SymbolId,
            [.. ctorTypeParamNames],
            expectedKinds);
    }

    private static List<string> GetTraitTypeParamNames(TraitDef trait)
    {
        var names = new List<string>(trait.TypeParams.Count);
        for (var i = 0; i < trait.TypeParams.Count; i++)
        {
            var name = trait.TypeParams[i].Name;
            names.Add(string.IsNullOrWhiteSpace(name) ? $"__T{i}" : name);
        }

        return names;
    }

    private void UpdateTypeParamSymbolsWithKinds(
        IReadOnlyList<TypeParam> typeParams,
        IReadOnlyList<Kind> kindsByIndex)
    {
        var matchCount = Math.Min(typeParams.Count, kindsByIndex.Count);
        for (var i = 0; i < matchCount; i++)
        {
            var typeParam = typeParams[i];
            if (!typeParam.SymbolId.IsValid ||
                _symbolTable.GetSymbol(typeParam.SymbolId) is not TypeParamSymbol typeParamSymbol)
            {
                continue;
            }

            var resolvedKind = ResolveKind(kindsByIndex[i]);
            if (resolvedKind is Kind.KVar unresolved && unresolved.Instance == null)
            {
                resolvedKind = Kind.KStar.Instance;
            }

            _symbolTable.UpdateSymbol(typeParamSymbol with
            {
                KindAnnotation = KindParser.ToKindText(resolvedKind)
            });
        }
    }

    private void RegisterAdtTypeParamConstraints(AdtDef adt, IReadOnlyList<string> typeParamNames)
    {
        var requirementsByIndex = new List<List<AdtTypeParamTraitRequirement>>(typeParamNames.Count);
        for (var i = 0; i < typeParamNames.Count; i++)
        {
            var requirements = new List<AdtTypeParamTraitRequirement>();
            if (i < adt.TypeParams.Count)
            {
                var typeParam = adt.TypeParams[i];
                foreach (var traitRef in typeParam.TraitConstraints)
                {
                    if (string.IsNullOrWhiteSpace(traitRef.TraitName))
                    {
                        continue;
                    }

                    requirements.Add(new AdtTypeParamTraitRequirement(
                        ResolveTraitConstraintSymbolId(traitRef),
                        traitRef.TraitName,
                        traitRef.TypeArgs.ToList()));
                }
            }

            requirementsByIndex.Add(requirements);
        }

        _adtTypeParamConstraintBindings[adt.SymbolId] = new AdtTypeParamConstraintBinding(
            adt.SymbolId,
            typeParamNames.ToList(),
            requirementsByIndex);
    }

    private void RegisterConstructorTypeParamConstraints(Constructor ctor, IReadOnlyList<string> typeParamNames)
    {
        if (!ctor.SymbolId.IsValid || typeParamNames.Count == 0)
        {
            return;
        }

        _ctorTypeParamConstraintBindings[ctor.SymbolId] = new TypeParamConstraintBinding(
            ctor.SymbolId,
            typeParamNames.ToList(),
            CollectTypeParamTraitRequirements(ctor.TypeParams, typeParamNames));
    }

    private List<List<AdtTypeParamTraitRequirement>> CollectTypeParamTraitRequirements(
        IReadOnlyList<TypeParam> typeParams,
        IReadOnlyList<string> typeParamNames)
    {
        var requirementsByIndex = new List<List<AdtTypeParamTraitRequirement>>(typeParamNames.Count);
        for (var i = 0; i < typeParamNames.Count; i++)
        {
            var requirements = new List<AdtTypeParamTraitRequirement>();
            if (i < typeParams.Count)
            {
                var typeParam = typeParams[i];
                foreach (var traitRef in typeParam.TraitConstraints)
                {
                    if (string.IsNullOrWhiteSpace(traitRef.TraitName))
                    {
                        continue;
                    }

                    requirements.Add(new AdtTypeParamTraitRequirement(
                        ResolveTraitConstraintSymbolId(traitRef),
                        traitRef.TraitName,
                        traitRef.TypeArgs.ToList()));
                }
            }

            requirementsByIndex.Add(requirements);
        }

        return requirementsByIndex;
    }

    private SymbolId ResolveTraitConstraintSymbolId(TraitRef traitRef)
    {
        if (traitRef.SymbolId.IsValid)
        {
            return traitRef.SymbolId;
        }

        if (!string.IsNullOrWhiteSpace(traitRef.TraitName))
        {
            var lookup = _symbolTable.LookupType(traitRef.TraitName);
            if (lookup.HasValue && lookup.Value.IsValid)
            {
                return lookup.Value;
            }

            var builtinTraitId = BuiltinTraits.GetBuiltinTraitSymbolId(traitRef.TraitName);
            if (builtinTraitId.IsValid)
            {
                return builtinTraitId;
            }
        }

        return SymbolId.None;
    }
}
