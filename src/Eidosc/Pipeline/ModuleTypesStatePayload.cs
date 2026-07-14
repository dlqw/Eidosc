namespace Eidosc.Pipeline;

using System.Collections.Immutable;
using Eidosc.Ast.Declarations;
using Eidosc.Symbols;
using Eidosc.Types;
using Eidosc.Utils;

public sealed record ModuleTypesStatePayload(
    string SchemaVersion,
    string ModuleKey,
    ProjectModuleTypedSemanticNode TypedSemantic,
    IReadOnlyList<LiveStateSymbolIdentity> SymbolIdentities,
    TypesSymbolStatePayload SymbolState,
    int NextSymbolId,
    int NextTypeId,
    int NextEffectId,
    TypeEnvPayload TypeEnv,
    TypeSubstitutionPayload TypeSubstitution,
    FunctionTypeParametersPayload FunctionTypeParameters,
    ComptimeValuesPayload ComptimeValues,
    TypeConstraintsPayload Constraints,
    FunctionEffectSummariesPayload FunctionEffects,
    AstInferredTypeMapPayload AstInferredTypes,
    AstTypesStatePayload AstState,
    string PayloadHash)
{
    public const string CurrentSchemaVersion = "module-types-state-payload-v13";

    public static ModuleTypesStatePayload Create(
        string moduleKey,
        ProjectModuleTypedSemanticSnapshot typedSemanticSnapshot,
        IReadOnlyList<LiveStateSymbolIdentity> namerSymbolIdentities,
        SymbolTablePayload namerSymbolTable,
        ModuleDecl? ast,
        TypeInferer? typeInferer,
        EffectInferer? effectInferer,
        SymbolTable? symbolTable,
        IReadOnlyList<string>? sourcePaths = null) =>
        Create(
            moduleKey,
            typedSemanticSnapshot,
            namerSymbolIdentities,
            namerSymbolTable,
            ast,
            typeInferer,
            effectInferer,
            symbolTable,
            sourcePaths,
            allStableNodes: null);

    internal static ModuleTypesStatePayload Create(
        string moduleKey,
        ProjectModuleTypedSemanticSnapshot typedSemanticSnapshot,
        IReadOnlyList<LiveStateSymbolIdentity> namerSymbolIdentities,
        SymbolTablePayload namerSymbolTable,
        ModuleDecl? ast,
        TypeInferer? typeInferer,
        EffectInferer? effectInferer,
        SymbolTable? symbolTable,
        IReadOnlyList<string>? sourcePaths,
        IReadOnlyList<AstStableNodeEntry>? allStableNodes)
    {
        var typedSemantic = typedSemanticSnapshot.Nodes.FirstOrDefault(node =>
            string.Equals(node.ModuleKey, moduleKey, StringComparison.Ordinal));
        if (typedSemantic == null)
        {
            throw new ArgumentException($"Module '{moduleKey}' is missing from the typed semantic snapshot.", nameof(moduleKey));
        }

        var moduleIdentityKey = ModulePayloadSymbolSlicer.ResolveModuleIdentityKey(symbolTable, moduleKey);
        var astState = AstTypesStatePayload.Create(
            ast,
            moduleKey,
            moduleIdentityKey,
            sourcePaths,
            allStableNodes);
        var astInferredTypes = AstInferredTypeMapPayload.Create(
            ast,
            typeInferer,
            moduleKey,
            moduleIdentityKey,
            sourcePaths,
            allStableNodes);
        IReadOnlySet<int>? allowedSymbolIds = null;
        if (symbolTable != null)
        {
            var astNamerState = AstNamerStatePayload.Create(
                ast,
                moduleKey,
                moduleIdentityKey,
                sourcePaths,
                allStableNodes);
            allowedSymbolIds = ModulePayloadSymbolSlicer.CreateNamerSymbolClosure(
                symbolTable,
                namerSymbolIdentities,
                astNamerState,
                moduleIdentityKey,
                sourcePaths ?? []);
        }

        var slicedNamerSymbolTable = allowedSymbolIds == null
            ? namerSymbolTable
            : ModulePayloadSymbolSlicer.SliceSymbolTable(namerSymbolTable, allowedSymbolIds);
        var slicedSymbolIdentities = allowedSymbolIds == null
            ? namerSymbolIdentities
            : namerSymbolIdentities
                .Where(identity => allowedSymbolIds.Contains(identity.SymbolId))
                .OrderBy(static identity => identity.StableKey.ToString(), StringComparer.Ordinal)
                .ToArray();
        var payload = new ModuleTypesStatePayload(
            CurrentSchemaVersion,
            moduleKey,
            typedSemantic,
            slicedSymbolIdentities,
            TypesSymbolStatePayload.Create(symbolTable, slicedNamerSymbolTable, allowedSymbolIds),
            symbolTable?.NextSymbolIdValue ?? 0,
            symbolTable?.NextTypeIdValue ?? 0,
            symbolTable?.NextEffectIdValue ?? 0,
            TypeEnvPayload.Create(typeInferer, allowedSymbolIds),
            TypeSubstitutionPayload.Create(typeInferer?.Substitution),
            FunctionTypeParametersPayload.Create(typeInferer, allowedSymbolIds),
            ComptimeValuesPayload.Create(typeInferer, allowedSymbolIds),
            TypeConstraintsPayload.Create(
                typeInferer,
                sourcePaths,
                includeUnscoped: string.Equals(
                    moduleKey,
                    WellKnownStrings.SpecialNames.Main,
                    StringComparison.OrdinalIgnoreCase)),
            FunctionEffectSummariesPayload.Create(effectInferer, allowedSymbolIds),
            astInferredTypes,
            astState,
            "");

        return payload with { PayloadHash = ComputeHash(payload) };
    }

    public bool HasValidPayloadHash() =>
        !string.IsNullOrWhiteSpace(PayloadHash) &&
        string.Equals(PayloadHash, ComputeHash(this), StringComparison.Ordinal);

    private static string ComputeHash(ModuleTypesStatePayload payload) =>
        ModuleArtifactHash.ComputeJsonHash(payload with { PayloadHash = "" });
}

public sealed record FunctionEffectSummariesPayload(
    string SchemaVersion,
    IReadOnlyList<FunctionEffectSummaryBindingPayload> Bindings,
    string Hash)
{
    public const string CurrentSchemaVersion = "function-effect-summaries-payload-v1";

    public static FunctionEffectSummariesPayload Create(
        EffectInferer? effectInferer,
        IReadOnlySet<int>? allowedSymbolIds = null)
    {
        var bindings = effectInferer?.FunctionSummariesBySymbol
            .Where(binding => allowedSymbolIds == null || allowedSymbolIds.Contains(binding.Key.Value))
            .OrderBy(static binding => binding.Key.Value)
            .Select(static binding => new FunctionEffectSummaryBindingPayload(
                binding.Key.Value,
                TypeShapePayload.Create(binding.Value.DeclaredUpperBound),
                TypeShapePayload.Create(binding.Value.InferredEffects)))
            .ToArray() ?? [];
        var payload = new FunctionEffectSummariesPayload(CurrentSchemaVersion, bindings, "");
        return payload with { Hash = ModuleArtifactHash.ComputeJsonHash(payload with { Hash = "" }) };
    }

    public bool HasValidHash() =>
        SchemaVersion == CurrentSchemaVersion &&
        !string.IsNullOrWhiteSpace(Hash) &&
        string.Equals(Hash, ModuleArtifactHash.ComputeJsonHash(this with { Hash = "" }), StringComparison.Ordinal);

    internal bool TryRestore(
        LiveStateIdRemapper? remapper,
        out IReadOnlyDictionary<SymbolId, FunctionEffectSummary> summaries)
    {
        summaries = new Dictionary<SymbolId, FunctionEffectSummary>();
        if (!HasValidHash())
        {
            return false;
        }

        var restored = new Dictionary<SymbolId, FunctionEffectSummary>();
        foreach (var binding in Bindings)
        {
            var declaredRestored = remapper == null
                ? binding.DeclaredUpperBound.TryRestoreType(out var declared)
                : binding.DeclaredUpperBound.TryRestoreType(remapper, out declared);
            var inferredRestored = remapper == null
                ? binding.InferredEffects.TryRestoreType(out var inferred)
                : binding.InferredEffects.TryRestoreType(remapper, out inferred);
            if (!declaredRestored ||
                !inferredRestored ||
                declared is not EffectRow declaredRow ||
                inferred is not EffectRow inferredRow)
            {
                return false;
            }

            restored[new SymbolId(remapper?.RemapSymbol(binding.FunctionSymbolId) ?? binding.FunctionSymbolId)] =
                new FunctionEffectSummary(declaredRow, inferredRow);
        }

        summaries = restored;
        return true;
    }
}

public sealed record FunctionEffectSummaryBindingPayload(
    int FunctionSymbolId,
    TypeShapePayload DeclaredUpperBound,
    TypeShapePayload InferredEffects);

public sealed record TypeEnvPayload(
    string SchemaVersion,
    IReadOnlyList<TypeEnvBindingPayload> Bindings,
    string Hash)
{
    public const string CurrentSchemaVersion = "type-env-payload-v1";

    public static TypeEnvPayload Create(
        TypeInferer? typeInferer,
        IReadOnlySet<int>? allowedSymbolIds = null)
    {
        if (typeInferer == null)
        {
            var empty = new TypeEnvPayload(CurrentSchemaVersion, [], "");
            return empty with { Hash = ModuleArtifactHash.ComputeJsonHash(empty with { Hash = "" }) };
        }

        var payload = new TypeEnvPayload(
            CurrentSchemaVersion,
            typeInferer.TypeEnvBindings
                .Where(binding => allowedSymbolIds == null || allowedSymbolIds.Contains(binding.Symbol.Value))
                .OrderBy(static binding => binding.Symbol.Value)
                .Select(static binding => TypeEnvBindingPayload.Create(binding))
                .ToArray(),
            "");

        return payload with { Hash = ModuleArtifactHash.ComputeJsonHash(payload with { Hash = "" }) };
    }
}

public sealed record TypeEnvBindingPayload(
    int SymbolId,
    TypeSchemePayload Scheme)
{
    public static TypeEnvBindingPayload Create(TypeEnvBindingSnapshot binding) =>
        new(binding.Symbol.Value, TypeSchemePayload.Create(binding.Scheme));
}

public sealed record FunctionTypeParametersPayload(
    string SchemaVersion,
    IReadOnlyList<FunctionTypeParameterBindingPayload> Bindings,
    string Hash)
{
    public const string CurrentSchemaVersion = "function-type-parameters-payload-v1";

    public static FunctionTypeParametersPayload Create(
        TypeInferer? typeInferer,
        IReadOnlySet<int>? allowedSymbolIds = null)
    {
        if (typeInferer == null)
        {
            var empty = new FunctionTypeParametersPayload(CurrentSchemaVersion, [], "");
            return empty with { Hash = ModuleArtifactHash.ComputeJsonHash(empty with { Hash = "" }) };
        }

        var payload = new FunctionTypeParametersPayload(
            CurrentSchemaVersion,
            typeInferer.FunctionTypeParametersBySymbol
                .Where(binding => allowedSymbolIds == null || allowedSymbolIds.Contains(binding.Key.Value))
                .OrderBy(static binding => binding.Key.Value)
                .Select(static binding => new FunctionTypeParameterBindingPayload(
                    binding.Key.Value,
                    binding.Value.Select(TypeShapePayload.Create).ToArray()))
                .ToArray(),
            "");

        return payload with { Hash = ModuleArtifactHash.ComputeJsonHash(payload with { Hash = "" }) };
    }

    public bool HasValidHash() =>
        !string.IsNullOrWhiteSpace(Hash) &&
        string.Equals(Hash, ModuleArtifactHash.ComputeJsonHash(this with { Hash = "" }), StringComparison.Ordinal);

    public bool TryRestoreFunctionTypeParameters(
        out IReadOnlyDictionary<SymbolId, IReadOnlyList<Eidosc.Types.Type>> typeParameters)
        => TryRestoreFunctionTypeParameters(remapper: null, out typeParameters);

    internal bool TryRestoreFunctionTypeParameters(
        LiveStateIdRemapper? remapper,
        out IReadOnlyDictionary<SymbolId, IReadOnlyList<Eidosc.Types.Type>> typeParameters)
    {
        typeParameters = new Dictionary<SymbolId, IReadOnlyList<Eidosc.Types.Type>>();
        if (SchemaVersion != CurrentSchemaVersion || !HasValidHash())
        {
            return false;
        }

        var restored = new Dictionary<SymbolId, IReadOnlyList<Eidosc.Types.Type>>();
        foreach (var binding in Bindings.OrderBy(static binding => binding.SymbolId))
        {
            var parameters = new List<Eidosc.Types.Type>(binding.TypeParameters.Count);
            foreach (var parameter in binding.TypeParameters)
            {
                var typeRestored = remapper == null
                    ? parameter.TryRestoreType(out var typeParameter)
                    : parameter.TryRestoreType(remapper, out typeParameter);
                if (!typeRestored)
                {
                    return false;
                }

                parameters.Add(typeParameter);
            }

            restored[new SymbolId(remapper?.RemapSymbol(binding.SymbolId) ?? binding.SymbolId)] = parameters;
        }

        typeParameters = restored;
        return true;
    }
}

public sealed record FunctionTypeParameterBindingPayload(
    int SymbolId,
    IReadOnlyList<TypeShapePayload> TypeParameters);

public sealed record ComptimeValuesPayload(
    string SchemaVersion,
    IReadOnlyList<ComptimeValueBindingPayload> Bindings,
    int UnsupportedValues,
    string Hash)
{
    public const string CurrentSchemaVersion = "comptime-values-payload-v4";

    public static ComptimeValuesPayload Create(
        TypeInferer? typeInferer,
        IReadOnlySet<int>? allowedSymbolIds = null)
    {
        if (typeInferer == null)
        {
            var empty = new ComptimeValuesPayload(CurrentSchemaVersion, [], 0, "");
            return empty with { Hash = ModuleArtifactHash.ComputeJsonHash(empty with { Hash = "" }) };
        }

        var bindings = new List<ComptimeValueBindingPayload>();
        var unsupported = 0;
        foreach (var binding in typeInferer.ComptimeValues
                     .Where(binding => allowedSymbolIds == null || allowedSymbolIds.Contains(binding.Key.Value))
                     .OrderBy(static binding => binding.Key.Value))
        {
            if (ComptimeValuePayload.TryCreate(binding.Value, out var value))
            {
                bindings.Add(new ComptimeValueBindingPayload(binding.Key.Value, value));
            }
            else
            {
                unsupported++;
            }
        }

        var payload = new ComptimeValuesPayload(CurrentSchemaVersion, bindings, unsupported, "");
        return payload with { Hash = ModuleArtifactHash.ComputeJsonHash(payload with { Hash = "" }) };
    }

    public bool HasValidHash() =>
        !string.IsNullOrWhiteSpace(Hash) &&
        string.Equals(Hash, ModuleArtifactHash.ComputeJsonHash(this with { Hash = "" }), StringComparison.Ordinal);

    internal bool TryRestoreComptimeValues(out IReadOnlyDictionary<SymbolId, ComptimeValue> values)
        => TryRestoreComptimeValues(remapper: null, out values);

    internal bool TryRestoreComptimeValues(
        LiveStateIdRemapper? remapper,
        out IReadOnlyDictionary<SymbolId, ComptimeValue> values)
    {
        values = new Dictionary<SymbolId, ComptimeValue>();
        if (SchemaVersion != CurrentSchemaVersion ||
            UnsupportedValues != 0 ||
            !HasValidHash())
        {
            return false;
        }

        var restored = new Dictionary<SymbolId, ComptimeValue>();
        foreach (var binding in Bindings.OrderBy(static binding => binding.SymbolId))
        {
            if (!binding.Value.TryRestoreValue(remapper, out var value))
            {
                return false;
            }

            restored[new SymbolId(remapper?.RemapSymbol(binding.SymbolId) ?? binding.SymbolId)] = value;
        }

        values = restored;
        return true;
    }
}

public sealed record ComptimeValueBindingPayload(
    int SymbolId,
    ComptimeValuePayload Value);

public sealed record ComptimeValuePayload(
    string Kind,
    string? ScalarKind = null,
    string? ScalarValue = null,
    string? SequenceKind = null,
    IReadOnlyList<ComptimeValuePayload>? Elements = null,
    int? ConstructorSymbolId = null,
    string? ConstructorName = null,
    IReadOnlyList<ComptimeNamedValuePayload>? NamedValues = null,
    TypeShapePayload? StaticType = null,
    MetaTypeRefPayload? MetaType = null,
    MetaDeclValuePayload? MetaDeclaration = null,
    string? MetaSchemaKind = null)
{
    public const string NullKind = "Null";
    public const string ScalarKindName = "Scalar";
    public const string SequenceKindName = "Sequence";
    public const string AdtKindName = "Adt";
    public const string MetaTypeKindName = "MetaType";
    public const string MetaDeclarationKindName = "MetaDeclaration";
    public const string MetaObjectKindName = "MetaObject";

    internal static bool TryCreate(ComptimeValue value, out ComptimeValuePayload payload)
    {
        switch (value)
        {
            case ComptimeUnitValue:
                payload = AttachStaticType(value, new ComptimeValuePayload(NullKind));
                return true;
            case ComptimeBoolValue scalar:
                payload = CreateScalar(scalar, nameof(Boolean), scalar.Value ? "true" : "false");
                return true;
            case ComptimeIntegerValue scalar:
                payload = CreateScalar(scalar, nameof(Int64), scalar.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return true;
            case ComptimeFloatValue scalar:
                payload = CreateScalar(scalar, nameof(Double), scalar.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                return true;
            case ComptimeStringValue scalar:
                payload = CreateScalar(scalar, nameof(String), scalar.Value);
                return true;
            case ComptimeCharValue scalar:
                payload = CreateScalar(scalar, nameof(Char), scalar.Value.ToString());
                return true;
            case ComptimeSequenceValue sequence:
                var elements = new List<ComptimeValuePayload>(sequence.Elements.Count);
                foreach (var element in sequence.Elements)
                {
                    if (!TryCreate(element, out var elementPayload))
                    {
                        payload = new ComptimeValuePayload("Unsupported");
                        return false;
                    }

                    elements.Add(elementPayload);
                }

                payload = AttachStaticType(sequence, new ComptimeValuePayload(
                    SequenceKindName,
                    SequenceKind: sequence.Kind.ToString(),
                    Elements: elements));
                return true;
            case ComptimeAdtValue adt:
                var positionalValues = new List<ComptimeValuePayload>(adt.PositionalValues.Count);
                foreach (var positionalValue in adt.PositionalValues)
                {
                    if (!TryCreate(positionalValue, out var positionalPayload))
                    {
                        payload = new ComptimeValuePayload("Unsupported");
                        return false;
                    }

                    positionalValues.Add(positionalPayload);
                }

                var namedValues = new List<ComptimeNamedValuePayload>(adt.NamedValues.Count);
                foreach (var namedValue in adt.NamedValues)
                {
                    if (!TryCreate(namedValue.Value, out var namedPayload))
                    {
                        payload = new ComptimeValuePayload("Unsupported");
                        return false;
                    }

                    namedValues.Add(new ComptimeNamedValuePayload(namedValue.Name, namedPayload));
                }

                payload = AttachStaticType(adt, new ComptimeValuePayload(
                    AdtKindName,
                    Elements: positionalValues,
                    ConstructorSymbolId: adt.ConstructorId.Value,
                    ConstructorName: adt.ConstructorName,
                    NamedValues: namedValues));
                return true;
            case ComptimeTypeValue typeValue:
                payload = AttachStaticType(typeValue, new ComptimeValuePayload(
                    MetaTypeKindName,
                    MetaType: MetaTypeRefPayload.Create(typeValue.TypeRef)));
                return true;
            case ComptimeDeclValue declaration:
                payload = AttachStaticType(declaration, new ComptimeValuePayload(
                    MetaDeclarationKindName,
                    MetaDeclaration: MetaDeclValuePayload.Create(declaration)));
                return true;
            case ComptimeMetaObjectValue metaObject:
                var properties = new List<ComptimeNamedValuePayload>(metaObject.Properties.Count);
                foreach (var property in metaObject.Properties)
                {
                    if (!TryCreate(property.Value, out var propertyPayload))
                    {
                        payload = new ComptimeValuePayload("Unsupported");
                        return false;
                    }

                    properties.Add(new ComptimeNamedValuePayload(property.Name, propertyPayload));
                }

                payload = AttachStaticType(metaObject, new ComptimeValuePayload(
                    MetaObjectKindName,
                    NamedValues: properties,
                    MetaSchemaKind: metaObject.SchemaKind));
                return true;
            default:
                payload = new ComptimeValuePayload("Unsupported");
                return false;
        }
    }

    internal bool TryRestoreValue(LiveStateIdRemapper? remapper, out ComptimeValue value)
    {
        if (!TryRestoreValueCore(remapper, out value))
        {
            return false;
        }

        if (StaticType == null)
        {
            return true;
        }

        var restoredType = remapper == null
            ? StaticType.TryRestoreType(out var staticType)
            : StaticType.TryRestoreType(remapper, out staticType);
        if (!restoredType)
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        value = value with { StaticType = staticType };
        return true;
    }

    private bool TryRestoreValueCore(LiveStateIdRemapper? remapper, out ComptimeValue value)
    {
        switch (Kind)
        {
            case NullKind:
                value = ComptimeUnitValue.Instance;
                return true;

            case ScalarKindName:
                return TryRestoreScalar(out value);

            case SequenceKindName:
                if (!Enum.TryParse<ComptimeSequenceKind>(SequenceKind, out var sequenceKind) ||
                    Elements == null)
                {
                    value = ComptimeUnitValue.Instance;
                    return false;
                }

                var elements = new ComptimeValue[Elements.Count];
                for (var i = 0; i < Elements.Count; i++)
                {
                    if (!Elements[i].TryRestoreValue(remapper, out elements[i]))
                    {
                        value = ComptimeUnitValue.Instance;
                        return false;
                    }
                }

                value = new ComptimeSequenceValue(sequenceKind, elements);
                return true;

            case AdtKindName:
                if (ConstructorSymbolId == null ||
                    string.IsNullOrWhiteSpace(ConstructorName) ||
                    Elements == null ||
                    NamedValues == null)
                {
                    value = ComptimeUnitValue.Instance;
                    return false;
                }

                var positionalValues = new ComptimeValue[Elements.Count];
                for (var i = 0; i < Elements.Count; i++)
                {
                    if (!Elements[i].TryRestoreValue(remapper, out positionalValues[i]))
                    {
                        value = ComptimeUnitValue.Instance;
                        return false;
                    }
                }

                var namedValues = new ComptimeNamedValue[NamedValues.Count];
                for (var i = 0; i < NamedValues.Count; i++)
                {
                    if (!NamedValues[i].Value.TryRestoreValue(remapper, out var namedValue))
                    {
                        value = ComptimeUnitValue.Instance;
                        return false;
                    }

                    namedValues[i] = new ComptimeNamedValue(NamedValues[i].Name, namedValue);
                }

                value = new ComptimeAdtValue(
                    new SymbolId(remapper?.RemapSymbol(ConstructorSymbolId.Value) ?? ConstructorSymbolId.Value),
                    ConstructorName,
                    positionalValues,
                    namedValues);
                return true;

            case MetaTypeKindName:
                if (MetaType == null || !MetaType.TryRestore(remapper, out var typeRef))
                {
                    value = ComptimeUnitValue.Instance;
                    return false;
                }

                value = new ComptimeTypeValue(typeRef);
                return true;

            case MetaDeclarationKindName:
                if (MetaDeclaration == null)
                {
                    value = ComptimeUnitValue.Instance;
                    return false;
                }

                value = MetaDeclaration.Restore(remapper);
                return true;

            case MetaObjectKindName:
                if (string.IsNullOrWhiteSpace(MetaSchemaKind) || NamedValues == null)
                {
                    value = ComptimeUnitValue.Instance;
                    return false;
                }

                var metaProperties = new ComptimeNamedValue[NamedValues.Count];
                for (var i = 0; i < NamedValues.Count; i++)
                {
                    if (!NamedValues[i].Value.TryRestoreValue(remapper, out var propertyValue))
                    {
                        value = ComptimeUnitValue.Instance;
                        return false;
                    }

                    metaProperties[i] = new ComptimeNamedValue(NamedValues[i].Name, propertyValue);
                }

                value = new ComptimeMetaObjectValue(MetaSchemaKind, metaProperties);
                return true;

            default:
                value = ComptimeUnitValue.Instance;
                return false;
        }
    }

    private static ComptimeValuePayload CreateScalar(
        ComptimeValue value,
        string scalarKind,
        string scalarValue) =>
        AttachStaticType(value, new ComptimeValuePayload(ScalarKindName, scalarKind, scalarValue));

    private static ComptimeValuePayload AttachStaticType(
        ComptimeValue value,
        ComptimeValuePayload payload) =>
        value.StaticType == null
            ? payload
            : payload with { StaticType = TypeShapePayload.Create(value.StaticType) };

    private bool TryRestoreScalar(out ComptimeValue value)
    {
        value = ComptimeUnitValue.Instance;
        if (ScalarKind == null ||
            ScalarValue == null)
        {
            return false;
        }

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        switch (ScalarKind)
        {
            case nameof(Boolean) when bool.TryParse(ScalarValue, out var scalar):
                value = new ComptimeBoolValue(scalar);
                return true;
            case nameof(Int64) when long.TryParse(ScalarValue, System.Globalization.NumberStyles.Integer, culture, out var scalar):
                value = new ComptimeIntegerValue(scalar);
                return true;
            case nameof(Double) when double.TryParse(ScalarValue, System.Globalization.NumberStyles.Float, culture, out var scalar):
                value = new ComptimeFloatValue(scalar);
                return true;
            case nameof(String):
                value = new ComptimeStringValue(ScalarValue);
                return true;
            case nameof(Char) when ScalarValue.Length == 1:
                value = new ComptimeCharValue(ScalarValue[0]);
                return true;
            default:
                return false;
        }
    }
}

public sealed record ComptimeNamedValuePayload(
    string Name,
    ComptimeValuePayload Value);

public sealed record MetaTypeRefPayload(
    string Kind,
    string Name,
    string StableIdentity,
    int SymbolId,
    int TypeId,
    IReadOnlyList<MetaTypeRefPayload> Arguments,
    IReadOnlyList<MetaGenericArgumentRefPayload>? GenericArguments = null)
{
    internal static MetaTypeRefPayload Create(MetaTypeRef type) => new(
        type.Kind,
        type.Name,
        type.StableIdentity,
        type.SymbolId.Value,
        type.TypeId.Value,
        type.Arguments.Select(Create).ToArray(),
        type.GenericArguments?.Select(MetaGenericArgumentRefPayload.Create).ToArray());

    internal bool TryRestore(LiveStateIdRemapper? remapper, out MetaTypeRef type)
    {
        var arguments = new MetaTypeRef[Arguments.Count];
        for (var i = 0; i < Arguments.Count; i++)
        {
            if (!Arguments[i].TryRestore(remapper, out arguments[i]))
            {
                type = null!;
                return false;
            }
        }

        MetaGenericArgumentRef[]? genericArguments = null;
        if (GenericArguments != null)
        {
            genericArguments = new MetaGenericArgumentRef[GenericArguments.Count];
            for (var i = 0; i < GenericArguments.Count; i++)
            {
                if (!GenericArguments[i].TryRestore(remapper, out genericArguments[i]))
                {
                    type = null!;
                    return false;
                }
            }
        }

        type = new MetaTypeRef(
            Kind,
            Name,
            StableIdentity,
            new SymbolId(remapper?.RemapSymbol(SymbolId) ?? SymbolId),
            new TypeId(remapper?.RemapType(TypeId) ?? TypeId),
            arguments,
            GenericArguments: genericArguments);
        return true;
    }
}

public sealed record MetaGenericArgumentRefPayload(
    string Domain,
    string Display,
    string StableIdentity,
    int SymbolId,
    MetaTypeRefPayload? Type)
{
    internal static MetaGenericArgumentRefPayload Create(MetaGenericArgumentRef argument) => new(
        argument.Domain,
        argument.Display,
        argument.StableIdentity,
        argument.SymbolId.Value,
        argument.Type == null ? null : MetaTypeRefPayload.Create(argument.Type));

    internal bool TryRestore(LiveStateIdRemapper? remapper, out MetaGenericArgumentRef argument)
    {
        MetaTypeRef? type = null;
        if (Type != null && !Type.TryRestore(remapper, out type))
        {
            argument = null!;
            return false;
        }

        argument = new MetaGenericArgumentRef(
            Domain,
            Display,
            StableIdentity,
            new SymbolId(remapper?.RemapSymbol(SymbolId) ?? SymbolId),
            type);
        return true;
    }
}

public sealed record MetaDeclValuePayload(
    int SymbolId,
    string StableIdentity,
    string Name,
    string DeclarationKind,
    SourceSpanPayload Span)
{
    internal static MetaDeclValuePayload Create(ComptimeDeclValue declaration) => new(
        declaration.SymbolId.Value,
        declaration.StableIdentity,
        declaration.Name,
        declaration.DeclarationKind,
        SourceSpanPayload.Create(declaration.Span));

    internal ComptimeDeclValue Restore(LiveStateIdRemapper? remapper) => new(
        new SymbolId(remapper?.RemapSymbol(SymbolId) ?? SymbolId),
        StableIdentity,
        Name,
        DeclarationKind,
        new SourceSpan(
            new SourceLocation(Span.Position, Span.Line, Span.Column, Span.FilePath),
            Span.Length));
}

public sealed record TypeConstraintsPayload(
    string SchemaVersion,
    IReadOnlyList<TypeConstraintPayload> Constraints,
    string Hash)
{
    public const string CurrentSchemaVersion = "type-constraints-payload-v1";

    public static TypeConstraintsPayload Create(
        TypeInferer? typeInferer,
        IReadOnlyList<string>? sourcePaths = null,
        bool includeUnscoped = false)
    {
        if (typeInferer == null)
        {
            var empty = new TypeConstraintsPayload(CurrentSchemaVersion, [], "");
            return empty with { Hash = ModuleArtifactHash.ComputeJsonHash(empty with { Hash = "" }) };
        }

        var normalizedSourcePaths = (sourcePaths ?? [])
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeSourcePath)
            .ToHashSet(SourcePathComparer);
        var payload = new TypeConstraintsPayload(
            CurrentSchemaVersion,
            typeInferer.ConstraintGenerator.Constraints.Constraints
                .Where(constraint =>
                    normalizedSourcePaths.Count == 0 ||
                    (!string.IsNullOrWhiteSpace(constraint.Span.FilePath) &&
                     normalizedSourcePaths.Contains(NormalizeSourcePath(constraint.Span.FilePath))) ||
                    (includeUnscoped && string.IsNullOrWhiteSpace(constraint.Span.FilePath)))
                .Select(TypeConstraintPayload.Create)
                .OrderBy(static constraint => constraint.Display, StringComparer.Ordinal)
                .ThenBy(static constraint => constraint.Kind, StringComparer.Ordinal)
                .ThenBy(static constraint => constraint.Span.FilePath, StringComparer.Ordinal)
                .ThenBy(static constraint => constraint.Span.Position)
                .ToArray(),
            "");

        return payload with { Hash = ModuleArtifactHash.ComputeJsonHash(payload with { Hash = "" }) };
    }

    private static string NormalizeSourcePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('<'))
        {
            return path.Replace('\\', '/');
        }

        try
        {
            return Path.GetFullPath(path).Replace('\\', '/');
        }
        catch
        {
            return path.Replace('\\', '/');
        }
    }

    private static StringComparer SourcePathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public bool HasValidHash() =>
        !string.IsNullOrWhiteSpace(Hash) &&
        string.Equals(Hash, ModuleArtifactHash.ComputeJsonHash(this with { Hash = "" }), StringComparison.Ordinal);

    public bool TryRestoreConstraints(out IReadOnlyList<TypeConstraint> constraints)
        => TryRestoreConstraints(remapper: null, out constraints);

    internal bool TryRestoreConstraints(
        LiveStateIdRemapper? remapper,
        out IReadOnlyList<TypeConstraint> constraints)
    {
        constraints = [];
        if (SchemaVersion != CurrentSchemaVersion ||
            !HasValidHash())
        {
            return false;
        }

        var restored = new List<TypeConstraint>(Constraints.Count);
        foreach (var constraintPayload in Constraints)
        {
            if (!constraintPayload.TryRestoreTypeConstraint(remapper, out var constraint))
            {
                return false;
            }

            restored.Add(constraint);
        }

        constraints = restored;
        return true;
    }
}

public sealed record TypeSchemePayload(
    IReadOnlyList<int> ForAll,
    TypeShapePayload Type,
    IReadOnlyList<TypeConstraintPayload> Constraints,
    string Display,
    string Hash)
{
    public static TypeSchemePayload Create(TypeScheme scheme)
    {
        var payload = new TypeSchemePayload(
            scheme.ForAll.Order().ToArray(),
            TypeShapePayload.Create(scheme.Type),
            scheme.Constraints.Select(TypeConstraintPayload.Create).ToArray(),
            scheme.ToString(),
            "");

        return payload with { Hash = ModuleArtifactHash.ComputeJsonHash(payload with { Hash = "" }) };
    }

    public bool HasValidHash() =>
        !string.IsNullOrWhiteSpace(Hash) &&
        string.Equals(Hash, ModuleArtifactHash.ComputeJsonHash(this with { Hash = "" }), StringComparison.Ordinal);

    public bool TryRestoreTypeScheme(out TypeScheme scheme)
        => TryRestoreTypeScheme(remapper: null, out scheme);

    internal bool TryRestoreTypeScheme(
        LiveStateIdRemapper? remapper,
        out TypeScheme scheme)
    {
        scheme = null!;
        if (!HasValidHash())
        {
            return false;
        }

        var restoredType = remapper == null
            ? Type.TryRestoreType(out var type)
            : Type.TryRestoreType(remapper, out type);
        if (!restoredType)
        {
            return false;
        }

        var constraints = new List<TypeConstraint>(Constraints.Count);
        foreach (var constraintPayload in Constraints)
        {
            if (!constraintPayload.TryRestoreTypeConstraint(remapper, out var constraint))
            {
                return false;
            }

            constraints.Add(constraint);
        }

        scheme = new TypeScheme
        {
            ForAll = ForAll
                .Select(value => remapper?.RemapTypeVariable(value) ?? value)
                .ToHashSet(),
            Type = type,
            Constraints = constraints
        };
        return true;
    }
}

public sealed record TypeConstraintPayload(
    string Kind,
    SourceSpanPayload Span,
    TypeShapePayload? Type = null,
    int? TraitSymbolId = null,
    string? TraitName = null,
    IReadOnlyList<TypeShapePayload>? TraitArguments = null,
    IReadOnlyList<ImplTypeRefKeyPayload>? TraitArgumentKeys = null,
    TypeShapePayload? Left = null,
    TypeShapePayload? Right = null,
    string? ExpectedKind = null,
    string? Display = null)
{
    public static TypeConstraintPayload Create(TypeConstraint constraint) =>
        constraint switch
        {
            TraitConstraint trait => new TypeConstraintPayload(
                nameof(TraitConstraint),
                SourceSpanPayload.Create(trait.Span),
                Type: TypeShapePayload.Create(trait.Type),
                TraitSymbolId: trait.Trait.Value,
                TraitName: trait.TraitName,
                TraitArguments: trait.TraitArgs.Select(TypeShapePayload.Create).ToArray(),
                TraitArgumentKeys: trait.TraitArgKeys.Select(ImplTypeRefKeyPayload.Create).ToArray(),
                Display: trait.ToString()),

            EqualityConstraint equality => new TypeConstraintPayload(
                nameof(EqualityConstraint),
                SourceSpanPayload.Create(equality.Span),
                Left: TypeShapePayload.Create(equality.Left),
                Right: TypeShapePayload.Create(equality.Right),
                Display: equality.ToString()),

            KindConstraint kind => new TypeConstraintPayload(
                nameof(KindConstraint),
                SourceSpanPayload.Create(kind.Span),
                Type: TypeShapePayload.Create(kind.Type),
                ExpectedKind: kind.ExpectedKind,
                Display: kind.ToString()),

            _ => new TypeConstraintPayload(
                constraint.GetType().Name,
                SourceSpanPayload.Create(constraint.Span),
                Display: constraint.ToString())
        };

    public bool TryRestoreTypeConstraint(out TypeConstraint constraint)
        => TryRestoreTypeConstraint(remapper: null, out constraint);

    internal bool TryRestoreTypeConstraint(
        LiveStateIdRemapper? remapper,
        out TypeConstraint constraint)
    {
        constraint = null!;
        switch (Kind)
        {
            case nameof(TraitConstraint):
                if (Type == null)
                {
                    return false;
                }

                var restoredTraitType = remapper == null
                    ? Type.TryRestoreType(out var traitType)
                    : Type.TryRestoreType(remapper, out traitType);
                if (!restoredTraitType)
                {
                    return false;
                }

                if (!TryRestoreTypeShapeList(TraitArguments, remapper, out var traitArguments))
                {
                    return false;
                }

                if (!TryRestoreImplTypeRefKeys(TraitArgumentKeys, remapper, out var traitArgumentKeys))
                {
                    return false;
                }

                constraint = new TraitConstraint
                {
                    Span = CreateSourceSpan(Span),
                    Type = traitType,
                    Trait = new SymbolId(remapper?.RemapSymbol(TraitSymbolId ?? SymbolId.None.Value) ??
                                         TraitSymbolId ??
                                         SymbolId.None.Value),
                    TraitName = TraitName ?? "",
                    TraitArgs = traitArguments,
                    TraitArgKeys = traitArgumentKeys
                };
                return true;

            case nameof(EqualityConstraint):
                if (Left == null ||
                    Right == null ||
                    !(remapper == null ? Left.TryRestoreType(out var left) : Left.TryRestoreType(remapper, out left)) ||
                    !(remapper == null ? Right.TryRestoreType(out var right) : Right.TryRestoreType(remapper, out right)))
                {
                    return false;
                }

                constraint = new EqualityConstraint
                {
                    Span = CreateSourceSpan(Span),
                    Left = left,
                    Right = right
                };
                return true;

            case nameof(KindConstraint):
                if (Type == null)
                {
                    return false;
                }

                var restoredKindType = remapper == null
                    ? Type.TryRestoreType(out var kindType)
                    : Type.TryRestoreType(remapper, out kindType);
                if (!restoredKindType)
                {
                    return false;
                }

                constraint = new KindConstraint
                {
                    Span = CreateSourceSpan(Span),
                    Type = kindType,
                    ExpectedKind = ExpectedKind ?? ""
                };
                return true;

            default:
                return false;
        }
    }

    private static bool TryRestoreTypeShapeList(
        IReadOnlyList<TypeShapePayload>? payloads,
        LiveStateIdRemapper? remapper,
        out List<Eidosc.Types.Type> types)
    {
        types = [];
        if (payloads == null)
        {
            return true;
        }

        foreach (var payload in payloads)
        {
            var restored = remapper == null
                ? payload.TryRestoreType(out var type)
                : payload.TryRestoreType(remapper, out type);
            if (!restored)
            {
                return false;
            }

            types.Add(type);
        }

        return true;
    }

    private static bool TryRestoreImplTypeRefKeys(
        IReadOnlyList<ImplTypeRefKeyPayload>? payloads,
        LiveStateIdRemapper? remapper,
        out List<ImplTypeRefKey> keys)
    {
        keys = [];
        if (payloads == null)
        {
            return true;
        }

        foreach (var payload in payloads)
        {
            if (!payload.TryRestore(remapper, out var key))
            {
                return false;
            }

            keys.Add(key);
        }

        return true;
    }

    private static SourceSpan CreateSourceSpan(SourceSpanPayload payload)
    {
        var filePath = payload.FilePath ?? "";
        return new SourceSpan(
            new SourceLocation(payload.Position, payload.Line, payload.Column, filePath),
            payload.Length);
    }
}

public sealed record ImplTypeRefKeyPayload(
    int SymbolId,
    int TypeId,
    string Text,
    IReadOnlyList<ImplTypeRefKeyPayload> TypeArguments,
    ImplValueRefKeyPayload? ValueArgument = null)
{
    public static ImplTypeRefKeyPayload Create(ImplTypeRefKey key) =>
        new(
            key.SymbolId.Value,
            key.TypeId.Value,
            key.Text,
            key.TypeArguments.IsDefaultOrEmpty
                ? []
                : key.TypeArguments.Select(Create).ToArray(),
            key.ValueArgument is { } valueArgument
                ? ImplValueRefKeyPayload.Create(valueArgument)
                : null);

    public bool TryRestore(out ImplTypeRefKey key)
        => TryRestore(remapper: null, out key);

    internal bool TryRestore(LiveStateIdRemapper? remapper, out ImplTypeRefKey key)
    {
        var arguments = new List<ImplTypeRefKey>(TypeArguments.Count);
        foreach (var argument in TypeArguments)
        {
            if (!argument.TryRestore(remapper, out var restored))
            {
                key = ImplTypeRefKey.Empty;
                return false;
            }

            arguments.Add(restored);
        }

        key = new ImplTypeRefKey(
            new SymbolId(remapper?.RemapSymbol(SymbolId) ?? SymbolId),
            new TypeId(remapper?.RemapType(TypeId) ?? TypeId),
            Text,
            arguments.ToImmutableArray(),
            ValueArgument?.Restore(remapper));
        return true;
    }
}

public sealed record ImplValueRefKeyPayload(
    int ParameterIndex,
    string CanonicalPayload,
    int TypeId,
    string VariableIdentity,
    string DisplayText)
{
    public static ImplValueRefKeyPayload Create(ImplValueRefKey value) =>
        new(
            value.ParameterIndex,
            value.CanonicalPayload,
            value.TypeId.Value,
            value.VariableIdentity,
            value.DisplayText);

    internal ImplValueRefKey Restore(LiveStateIdRemapper? remapper)
    {
        var variableIdentity = VariableIdentity;
        if (remapper != null &&
            variableIdentity.StartsWith("var:", StringComparison.Ordinal) &&
            int.TryParse(variableIdentity["var:".Length..], out var valueVariableIndex))
        {
            variableIdentity = $"var:{remapper.RemapValueVariable(valueVariableIndex)}";
        }

        return new ImplValueRefKey(
            ParameterIndex,
            CanonicalPayload,
            new TypeId(remapper?.RemapType(TypeId) ?? TypeId),
            variableIdentity,
            DisplayText);
    }
}
