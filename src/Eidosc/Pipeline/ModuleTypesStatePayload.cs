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
    public const string CurrentSchemaVersion = "module-types-state-payload-v11";

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
    public const string CurrentSchemaVersion = "comptime-values-payload-v1";

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
            if (ComptimeValuePayload.TryCreate(binding.Value.Value, out var value))
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
            if (!binding.Value.TryRestoreValue(out var value))
            {
                return false;
            }

            restored[new SymbolId(remapper?.RemapSymbol(binding.SymbolId) ?? binding.SymbolId)] = new ComptimeValue(value);
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
    IReadOnlyList<ComptimeValuePayload>? Elements = null)
{
    public const string NullKind = "Null";
    public const string ScalarKindName = "Scalar";
    public const string SequenceKindName = "Sequence";

    public static bool TryCreate(object? value, out ComptimeValuePayload payload)
    {
        switch (value)
        {
            case null:
                payload = new ComptimeValuePayload(NullKind);
                return true;
            case bool scalar:
                payload = CreateScalar(nameof(Boolean), scalar ? "true" : "false");
                return true;
            case byte scalar:
                payload = CreateScalar(nameof(Byte), scalar.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return true;
            case short scalar:
                payload = CreateScalar(nameof(Int16), scalar.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return true;
            case int scalar:
                payload = CreateScalar(nameof(Int32), scalar.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return true;
            case long scalar:
                payload = CreateScalar(nameof(Int64), scalar.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return true;
            case float scalar:
                payload = CreateScalar(nameof(Single), scalar.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                return true;
            case double scalar:
                payload = CreateScalar(nameof(Double), scalar.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                return true;
            case string scalar:
                payload = CreateScalar(nameof(String), scalar);
                return true;
            case char scalar:
                payload = CreateScalar(nameof(Char), scalar.ToString());
                return true;
            case ComptimeSequence sequence:
                var elements = new List<ComptimeValuePayload>(sequence.Elements.Length);
                foreach (var element in sequence.Elements)
                {
                    if (!TryCreate(element, out var elementPayload))
                    {
                        payload = new ComptimeValuePayload("Unsupported");
                        return false;
                    }

                    elements.Add(elementPayload);
                }

                payload = new ComptimeValuePayload(
                    SequenceKindName,
                    SequenceKind: sequence.Kind.ToString(),
                    Elements: elements);
                return true;
            default:
                payload = new ComptimeValuePayload("Unsupported");
                return false;
        }
    }

    internal bool TryRestoreValue(out object? value)
    {
        switch (Kind)
        {
            case NullKind:
                value = null;
                return true;

            case ScalarKindName:
                return TryRestoreScalar(out value);

            case SequenceKindName:
                if (!Enum.TryParse<ComptimeSequenceKind>(SequenceKind, out var sequenceKind) ||
                    Elements == null)
                {
                    value = null;
                    return false;
                }

                var elements = new object?[Elements.Count];
                for (var i = 0; i < Elements.Count; i++)
                {
                    if (!Elements[i].TryRestoreValue(out elements[i]))
                    {
                        value = null;
                        return false;
                    }
                }

                value = new ComptimeSequence(sequenceKind, elements);
                return true;

            default:
                value = null;
                return false;
        }
    }

    private static ComptimeValuePayload CreateScalar(string scalarKind, string scalarValue) =>
        new(ScalarKindName, scalarKind, scalarValue);

    private bool TryRestoreScalar(out object? value)
    {
        value = null;
        if (ScalarKind == null ||
            ScalarValue == null)
        {
            return false;
        }

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        switch (ScalarKind)
        {
            case nameof(Boolean) when bool.TryParse(ScalarValue, out var scalar):
                value = scalar;
                return true;
            case nameof(Byte) when byte.TryParse(ScalarValue, System.Globalization.NumberStyles.Integer, culture, out var scalar):
                value = scalar;
                return true;
            case nameof(Int16) when short.TryParse(ScalarValue, System.Globalization.NumberStyles.Integer, culture, out var scalar):
                value = scalar;
                return true;
            case nameof(Int32) when int.TryParse(ScalarValue, System.Globalization.NumberStyles.Integer, culture, out var scalar):
                value = scalar;
                return true;
            case nameof(Int64) when long.TryParse(ScalarValue, System.Globalization.NumberStyles.Integer, culture, out var scalar):
                value = scalar;
                return true;
            case nameof(Single) when float.TryParse(ScalarValue, System.Globalization.NumberStyles.Float, culture, out var scalar):
                value = scalar;
                return true;
            case nameof(Double) when double.TryParse(ScalarValue, System.Globalization.NumberStyles.Float, culture, out var scalar):
                value = scalar;
                return true;
            case nameof(String):
                value = ScalarValue;
                return true;
            case nameof(Char) when ScalarValue.Length == 1:
                value = ScalarValue[0];
                return true;
            default:
                return false;
        }
    }
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
    IReadOnlyList<ImplTypeRefKeyPayload> TypeArguments)
{
    public static ImplTypeRefKeyPayload Create(ImplTypeRefKey key) =>
        new(
            key.SymbolId.Value,
            key.TypeId.Value,
            key.Text,
            key.TypeArguments.IsDefaultOrEmpty
                ? []
                : key.TypeArguments.Select(Create).ToArray());

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
            arguments.ToImmutableArray());
        return true;
    }
}
