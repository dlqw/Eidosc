using System.Reflection;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Hir;
using Eidosc.Mir;
using Eidosc.Symbols;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Pipeline;

public sealed record CompilationLiveStatePayload(
    string SchemaVersion,
    string InputHash,
    string FlagsHash,
    SymbolTablePayload SymbolTable,
    ModuleRegistryPayload ModuleRegistry,
    TypeSubstitutionPayload TypeSubstitution,
    AstInferredTypeMapPayload AstInferredTypes,
    MetaQueryStatePayload MetaQueries,
    HirGraphPayload HirGraph,
    ModuleHirStatePayload HirState,
    MirGraphPayload MirGraph,
    ModuleMirStatePayload MirState,
    LiveStateRemapPlan RemapPlan,
    string PayloadHash)
{
    public const string CurrentSchemaVersion = "compilation-live-state-payload-v9";

    public static CompilationLiveStatePayload Create(
        string sourceText,
        string flagsHash,
        SymbolTable? symbolTable,
        TypeInferer? typeInferer,
        ModuleDecl? ast,
        HirModule? hirModule,
        ParameterEffectMap? hirParameterEffects,
        IReadOnlySet<TypeId>? hirCopyLikeTypeIds,
        IReadOnlyDictionary<TypeId, string>? hirDynamicTypeKeys,
        IReadOnlyDictionary<int, TypeDescriptor>? hirTypeDescriptors,
        IReadOnlyDictionary<int, List<ConstructorTypeLayout>>? hirConstructorLayouts,
        MirModule? mirModule)
    {
        var payloadWithoutHash = new CompilationLiveStatePayload(
            CurrentSchemaVersion,
            ModuleArtifactHash.ComputeSourceHash(sourceText),
            flagsHash,
            SymbolTablePayload.Create(symbolTable),
            ModuleRegistryPayload.Create(symbolTable?.Modules),
            TypeSubstitutionPayload.Create(typeInferer?.Substitution),
            AstInferredTypeMapPayload.Create(ast, typeInferer),
            MetaQueryStatePayload.Create(symbolTable),
            HirGraphPayload.Create(hirModule),
            ModuleHirStatePayload.Create(
                hirModule,
                hirParameterEffects,
                hirCopyLikeTypeIds,
                hirDynamicTypeKeys,
                hirTypeDescriptors,
                hirConstructorLayouts),
            MirGraphPayload.Create(mirModule),
            ModuleMirStatePayload.Create(mirModule),
            LiveStateRemapPlan.Identity(symbolTable),
            "");

        return payloadWithoutHash with
        {
            PayloadHash = ComputeHash(payloadWithoutHash)
        };
    }

    public CompilationLiveStatePayloadValidationResult ValidateAgainst(CompilationLiveStatePayload current)
    {
        var failures = new List<string>();
        AddFailureIfDifferent(failures, nameof(SchemaVersion), SchemaVersion, current.SchemaVersion);
        AddFailureIfDifferent(failures, nameof(InputHash), InputHash, current.InputHash);
        AddFailureIfDifferent(failures, nameof(FlagsHash), FlagsHash, current.FlagsHash);
        AddFailureIfDifferent(failures, "symbolTable.hash", SymbolTable.Hash, current.SymbolTable.Hash);
        AddFailureIfDifferent(failures, "moduleRegistry.hash", ModuleRegistry.Hash, current.ModuleRegistry.Hash);
        AddFailureIfDifferent(failures, "typeSubstitution.hash", TypeSubstitution.Hash, current.TypeSubstitution.Hash);
        AddFailureIfDifferent(failures, "astInferredTypes.hash", AstInferredTypes.Hash, current.AstInferredTypes.Hash);
        AddFailureIfDifferent(failures, "metaQueries.hash", MetaQueries.Hash, current.MetaQueries.Hash);
        AddFailureIfDifferent(failures, "hirGraph.hash", HirGraph.Hash, current.HirGraph.Hash);
        AddFailureIfDifferent(failures, "hirState.hash", HirState.Hash, current.HirState.Hash);
        AddFailureIfDifferent(failures, "mirGraph.hash", MirGraph.Hash, current.MirGraph.Hash);
        AddFailureIfDifferent(failures, "mirState.hash", MirState.Hash, current.MirState.Hash);

        var restorable = failures.Count == 0 &&
                         RemapPlan.IsIdentity &&
                         current.RemapPlan.IsIdentity &&
                         SymbolTable.Symbols.Count == current.SymbolTable.Symbols.Count &&
                         TypeSubstitution.Bindings.Count == current.TypeSubstitution.Bindings.Count &&
                         TypeSubstitution.ValueBindings.Count == current.TypeSubstitution.ValueBindings.Count;

        return new CompilationLiveStatePayloadValidationResult(
            IsValid: failures.Count == 0,
            IsRestorable: restorable,
            RequiredRemap: restorable ? LiveStateRemapKind.Identity : LiveStateRemapKind.NotRestorable,
            Failures: failures);
    }

    private static void AddFailureIfDifferent(List<string> failures, string field, string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            failures.Add($"{field}: expected {expected}, actual {actual}");
        }
    }

    private static string ComputeHash(CompilationLiveStatePayload payload) =>
        ModuleArtifactHash.ComputeJsonHash(payload with { PayloadHash = "" });
}

public sealed record CompilationLiveStatePayloadValidationResult(
    bool IsValid,
    bool IsRestorable,
    LiveStateRemapKind RequiredRemap,
    IReadOnlyList<string> Failures);

public enum LiveStateRemapKind
{
    Identity,
    StableKey,
    NotRestorable
}

public sealed record LiveStateRemapPlan(
    string SchemaVersion,
    LiveStateRemapKind Kind,
    IReadOnlyList<LiveStateSymbolRemapEntry> Symbols,
    IReadOnlyList<LiveStateTypeRemapEntry> Types,
    IReadOnlyList<LiveStateEffectRemapEntry> Effects,
    string Hash)
{
    public const string CurrentSchemaVersion = "live-state-remap-plan-v1";

    public bool IsIdentity => Kind == LiveStateRemapKind.Identity;

    public bool IsStableKey => Kind == LiveStateRemapKind.StableKey;

    public static LiveStateRemapPlan Identity(SymbolTable? symbolTable)
    {
        var symbols = symbolTable?.Symbols.Keys
            .Where(static id => id.IsValid)
            .OrderBy(static id => id.Value)
            .Select(static id => new LiveStateSymbolRemapEntry(id.Value, id.Value))
            .ToArray() ?? [];

        var types = symbolTable?.Symbols.Values
            .Select(static symbol => symbol.TypeId)
            .Where(static id => id.IsValid)
            .Distinct()
            .OrderBy(static id => id.Value)
            .Select(static id => new LiveStateTypeRemapEntry(id.Value, id.Value))
            .ToArray() ?? [];

        var plan = new LiveStateRemapPlan(CurrentSchemaVersion, LiveStateRemapKind.Identity, symbols, types, [], "");
        return plan with { Hash = ModuleArtifactHash.ComputeJsonHash(plan with { Hash = "" }) };
    }

    public static LiveStateRemapPlan FromResolution(LiveStateRemapResolution resolution)
    {
        var plan = new LiveStateRemapPlan(
            CurrentSchemaVersion,
            resolution.Kind,
            resolution.Symbols
                .OrderBy(static entry => entry.From)
                .ThenBy(static entry => entry.To)
                .ToArray(),
            resolution.Types
                .OrderBy(static entry => entry.From)
                .ThenBy(static entry => entry.To)
                .ToArray(),
            [],
            "");
        return plan with { Hash = ModuleArtifactHash.ComputeJsonHash(plan with { Hash = "" }) };
    }
}

public sealed record LiveStateSymbolRemapEntry(int From, int To);

public sealed record LiveStateTypeRemapEntry(int From, int To);

public sealed record LiveStateEffectRemapEntry(int From, int To);

public sealed record SymbolTablePayload(
    string SchemaVersion,
    int NextSymbolId,
    int NextTypeId,
    int NextEffectId,
    IReadOnlyList<SymbolPayload> Symbols,
    IReadOnlyList<ScopePayload> Scopes,
    IReadOnlyDictionary<string, int> GlobalTypes,
    IReadOnlyDictionary<string, int> GlobalTraits,
    IReadOnlyDictionary<string, int> GlobalConstructors,
    IReadOnlyDictionary<string, int> GlobalAbilities,
    string Hash)
{
    public const string CurrentSchemaVersion = "symbol-table-payload-v6";

    public static SymbolTablePayload Create(SymbolTable? symbolTable)
    {
        if (symbolTable == null)
        {
            return Empty();
        }

        var scopeIndices = new Dictionary<Scope, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < symbolTable.ScopeStack.Count; i++)
        {
            scopeIndices[symbolTable.ScopeStack[i]] = i;
        }

        var payload = new SymbolTablePayload(
            CurrentSchemaVersion,
            symbolTable.NextSymbolIdValue,
            symbolTable.NextTypeIdValue,
            symbolTable.NextEffectIdValue,
            symbolTable.Symbols.Values
                .OrderBy(static symbol => symbol.Id.Value)
                .Select(SymbolPayload.Create)
                .ToArray(),
            symbolTable.ScopeStack
                .Select((scope, index) => ScopePayload.Create(scope, index, scopeIndices))
                .ToArray(),
            ToIntMap(symbolTable.GlobalTypes),
            ToIntMap(symbolTable.GlobalTraits),
            ToIntMap(symbolTable.GlobalConstructors),
            ToIntMap(symbolTable.GlobalAbilities),
            "");

        return payload with { Hash = ModuleArtifactHash.ComputeJsonHash(payload with { Hash = "" }) };
    }

    private static SymbolTablePayload Empty()
    {
        var payload = new SymbolTablePayload(CurrentSchemaVersion, 0, 0, 0, [], [], EmptyStringIntMap(), EmptyStringIntMap(), EmptyStringIntMap(), EmptyStringIntMap(), "");
        return payload with { Hash = ModuleArtifactHash.ComputeJsonHash(payload with { Hash = "" }) };
    }

    private static IReadOnlyDictionary<string, int> ToIntMap(IReadOnlyDictionary<string, SymbolId> map) =>
        map.OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .ToDictionary(static entry => entry.Key, static entry => entry.Value.Value, StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, int> EmptyStringIntMap() =>
        new Dictionary<string, int>(StringComparer.Ordinal);
}

public sealed record SymbolPayload(
    int Id,
    string Kind,
    string Name,
    SourceSpanPayload Span,
    bool IsTypeResolved,
    bool IsModuleLevel,
    bool IsPublic,
    int TypeId,
    IReadOnlyDictionary<string, string> Facts,
    GeneratedDeclarationOriginPayload? GeneratedOrigin = null)
{
    public static SymbolPayload Create(Symbol symbol) =>
        new(
            symbol.Id.Value,
            symbol.Kind.ToString(),
            symbol.Name,
            SourceSpanPayload.Create(symbol.Span),
            symbol.IsTypeResolved,
            symbol.IsModuleLevel,
            symbol.IsPublic,
            symbol.TypeId.Value,
            CreateFacts(symbol),
            symbol.GeneratedOrigin == null
                ? null
                : GeneratedDeclarationOriginPayload.Create(symbol.GeneratedOrigin));

    private static IReadOnlyDictionary<string, string> CreateFacts(Symbol symbol)
    {
        var facts = new SortedDictionary<string, string>(StringComparer.Ordinal);
        facts["definitionModule"] = symbol.DefinitionModuleId.Value.ToString();

        switch (symbol)
        {
            case FuncSymbol func:
                facts["typeParams"] = JoinSymbolIds(func.TypeParams);
                facts["parameters"] = JoinSymbolIds(func.Parameters);
                facts["paramTypes"] = JoinTypeIds(func.ParamTypes);
                facts["returnType"] = func.ReturnType.Value.ToString();
                facts["abilities"] = JoinEffectIds(func.Effects);
                facts["implicitAbilities"] = string.Join(",", func.ImplicitAbilities);
                facts["isComptime"] = func.IsComptime.ToString();
                facts["ownerTrait"] = (func.OwnerTrait?.Value ?? SymbolId.None.Value).ToString();
                facts["traitSelfPosition"] = func.TraitSelfPosition.ToString();
                facts["traitSelfParameterIndices"] = string.Join(",", func.TraitSelfParameterIndices);
                facts["traitSelfInResult"] = func.TraitSelfInResult.ToString();
                facts["traitMethodRole"] = func.TraitMethodRole.ToString();
                facts["hasBody"] = func.HasBody.ToString();
                facts["isDefaultImplementation"] = func.IsDefaultImplementation.ToString();
                facts["isTraitImplementation"] = func.IsTraitImplementation.ToString();
                facts["isExternal"] = func.IsExternal.ToString();
                facts["externalSymbolName"] = func.ExternalSymbolName ?? "";
                facts["externalLibrary"] = func.ExternalLibrary ?? "";
                facts["cStructFieldTypeId"] = func.CStructFieldTypeId.Value.ToString();
                facts["isCStructAccessor"] = func.IsCStructAccessor.ToString();
                facts["cStructFieldOffset"] = func.CStructFieldOffset.ToString();
                facts["isCStructGetter"] = func.IsCStructGetter.ToString();
                facts["intrinsicName"] = func.IntrinsicName ?? "";
                facts["builtinIntrinsicRole"] = func.BuiltinIntrinsicRole.ToString();
                break;
            case VarSymbol variable:
                facts["isMutable"] = variable.IsMutable.ToString();
                facts["isComptime"] = variable.IsComptime.ToString();
                facts["type"] = variable.Type.Value.ToString();
                facts["isParameter"] = variable.IsParameter.ToString();
                facts["isPatternBound"] = variable.IsPatternBound.ToString();
                facts["bindingMode"] = variable.BindingMode.ToString();
                facts["scheme"] = variable.Scheme?.ToString() ?? "";
                break;
            case AdtSymbol adt:
                facts["typeParams"] = JoinSymbolIds(adt.TypeParams);
                facts["constructors"] = JoinSymbolIds(adt.Constructors);
                facts["fields"] = JoinSymbolIds(adt.Fields);
                facts["directCases"] = JoinSymbolIds(adt.DirectCases);
                facts["parentAdt"] = adt.ParentAdt.Value.ToString();
                facts["caseConstructor"] = adt.CaseConstructor.Value.ToString();
                facts["canonicalParentSpecialization"] = adt.CanonicalParentSpecialization;
                facts["aliasTarget"] = (adt.AliasTarget?.Value ?? Eidosc.TypeId.None.Value).ToString();
                facts["isCStruct"] = adt.IsCStruct.ToString();
                facts["cStructLayout"] = adt.CStructLayoutInfo?.ToString() ?? "";
                break;
            case CtorSymbol ctor:
                facts["ownerAdt"] = ctor.OwnerAdt.Value.ToString();
                facts["typeParams"] = JoinSymbolIds(ctor.TypeParams);
                facts["positionalArgs"] = JoinTypeIds(ctor.PositionalArgs);
                facts["namedFields"] = JoinSymbolIds(ctor.NamedFields);
                facts["signatureText"] = ctor.SignatureText ?? "";
                break;
            case FieldSymbol field:
                facts["fieldType"] = field.FieldType.Value.ToString();
                facts["ownerType"] = field.OwnerType.Value.ToString();
                facts["index"] = field.Index.ToString();
                break;
            case TraitSymbol trait:
                facts["typeParams"] = JoinSymbolIds(trait.TypeParams);
                facts["methods"] = JoinSymbolIds(trait.Methods);
                facts["associatedTypes"] = JoinSymbolIds(trait.AssociatedTypes);
                facts["associatedConsts"] = JoinSymbolIds(trait.AssociatedConsts);
                facts["parentTraits"] = JoinSymbolIds(trait.ParentTraits);
                facts["selfPosition"] = trait.SelfPosition.ToString();
                break;
            case AssociatedTypeSymbol associatedType:
                facts["ownerTrait"] = associatedType.OwnerTrait.Value.ToString();
                facts["ownerImpl"] = associatedType.OwnerImpl.Value.ToString();
                facts["typeParams"] = JoinSymbolIds(associatedType.TypeParams);
                break;
            case AssociatedConstSymbol associatedConst:
                facts["ownerTrait"] = associatedConst.OwnerTrait.Value.ToString();
                facts["ownerImpl"] = associatedConst.OwnerImpl.Value.ToString();
                facts["valueType"] = associatedConst.ValueType.Value.ToString();
                break;
            case EffectSymbol:
                break;
            case TypeParamSymbol typeParam:
                facts["kindAnnotation"] = typeParam.KindAnnotation;
                facts["parameterKind"] = typeParam.ParameterKind.ToString();
                facts["isComptime"] = typeParam.IsComptime.ToString();
                facts["comptimeTypeAnnotation"] = typeParam.ComptimeTypeAnnotation ?? "";
                facts["traitConstraints"] = JoinSymbolIds(typeParam.TraitConstraints);
                break;
            case ImplSymbol impl:
                facts["trait"] = impl.Trait.Value.ToString();
                facts["implementingType"] = impl.ImplementingType.Value.ToString();
                facts["canonicalImplementingType"] = impl.CanonicalImplementingType;
                facts["implementingTypeDisplay"] = impl.ImplementingTypeDisplay;
                facts["implementingTypeKey"] = impl.ImplementingTypeKey.ToString() ?? "";
                facts["methods"] = JoinSymbolIds(impl.Methods);
                facts["associatedTypes"] = JoinSymbolIds(impl.AssociatedTypes);
                facts["associatedConsts"] = JoinSymbolIds(impl.AssociatedConsts);
                facts["traitTypeArgs"] = string.Join(",", impl.TraitTypeArgs);
                facts["traitTypeArgKeys"] = string.Join("|", impl.TraitTypeArgKeys.Select(static key => key.ToString()));
                facts["canonicalTraitTypeArgs"] = string.Join(",", impl.CanonicalTraitTypeArgs);
                facts["canonicalTraitTypeArgKeys"] = string.Join("|", impl.CanonicalTraitTypeArgKeys.Select(static key => key.ToString()));
                facts["traitMethodImplementations"] = string.Join(",", impl.TraitMethodImplementations
                    .OrderBy(static entry => entry.Key.Value)
                    .Select(static entry => $"{entry.Key.Value}->{entry.Value.Value}"));
                facts["typeArguments"] = string.Join(",", impl.TypeArguments
                    .OrderBy(static entry => entry.Key.Value)
                    .Select(static entry => $"{entry.Key.Value}->{entry.Value.Value}"));
                facts["isAutoDerived"] = impl.IsAutoDerived.ToString();
                break;
            case ModuleSymbol module:
                facts["packageAlias"] = module.PackageAlias ?? "";
                facts["packageInstanceKey"] = module.PackageInstanceKey ?? "";
                facts["identity"] = module.Identity.ToIdentityKey();
                facts["path"] = string.Join("/", module.Path);
                facts["members"] = JoinSymbolIds(module.Members);
                facts["exports"] = JoinModuleBindings(module.ExportedBindings);
                facts["usesExplicitExports"] = module.UsesExplicitExports.ToString();
                facts["imports"] = JoinSymbolIds(module.Imports);
                facts["parentModule"] = (module.ParentModule?.Value ?? SymbolId.None.Value).ToString();
                break;
        }

        return facts;
    }

    private static string JoinSymbolIds(IEnumerable<SymbolId> values) =>
        string.Join(",", values.Select(static id => id.Value));

    private static string JoinTypeIds(IEnumerable<TypeId> values) =>
        string.Join(",", values.Select(static id => id.Value));

    private static string JoinEffectIds(IEnumerable<EffectId> values) =>
        string.Join(",", values.Select(static id => id.Value));

    private static string JoinModuleBindings(IEnumerable<ModuleBindingEntry> bindings) =>
        string.Join(",", bindings
            .OrderBy(static binding => binding.Name, StringComparer.Ordinal)
            .ThenBy(static binding => binding.Kind)
            .ThenBy(static binding => binding.SymbolId.Value)
            .Select(static binding => $"{binding.Name}:{binding.Kind}:{binding.SymbolId.Value}"));
}

public sealed record GeneratedDeclarationOriginPayload(
    string StableIdentity,
    string GenerationSlotIdentity,
    string GeneratorIdentity,
    string TargetIdentity,
    int GeneratorSymbolId,
    int TargetSymbolId,
    int ClauseOccurrenceIndex,
    string ClauseOccurrenceIdentity,
    int ClauseArgumentSubIndex,
    int ExpansionOutputIndex,
    string CanonicalArgumentsHash,
    int MetaSchemaVersion,
    SourceSpanPayload ClauseSpan,
    string VirtualDocumentPath)
{
    public static GeneratedDeclarationOriginPayload Create(GeneratedDeclarationOrigin origin) => new(
        origin.StableIdentity,
        origin.GenerationSlotIdentity,
        origin.GeneratorIdentity,
        origin.TargetIdentity,
        origin.GeneratorSymbolId.Value,
        origin.TargetSymbolId.Value,
        origin.ClauseOccurrenceIndex,
        origin.ClauseOccurrenceIdentity,
        origin.ClauseArgumentSubIndex,
        origin.ExpansionOutputIndex,
        origin.CanonicalArgumentsHash,
        origin.MetaSchemaVersion,
        SourceSpanPayload.Create(origin.ClauseSpan),
        origin.VirtualDocumentPath);

    public GeneratedDeclarationOrigin Restore() => new()
    {
        StableIdentity = StableIdentity,
        GenerationSlotIdentity = GenerationSlotIdentity,
        GeneratorIdentity = GeneratorIdentity,
        TargetIdentity = TargetIdentity,
        GeneratorSymbolId = new SymbolId(GeneratorSymbolId),
        TargetSymbolId = new SymbolId(TargetSymbolId),
        ClauseOccurrenceIndex = ClauseOccurrenceIndex,
        ClauseOccurrenceIdentity = ClauseOccurrenceIdentity,
        ClauseArgumentSubIndex = ClauseArgumentSubIndex,
        ExpansionOutputIndex = ExpansionOutputIndex,
        CanonicalArgumentsHash = CanonicalArgumentsHash,
        MetaSchemaVersion = MetaSchemaVersion,
        ClauseSpan = ClauseSpan.ToSourceSpan(),
        VirtualDocumentPath = VirtualDocumentPath
    };
}

public sealed record ScopePayload(
    int Index,
    int ParentIndex,
    int Depth,
    string Kind,
    IReadOnlyDictionary<string, int> Bindings,
    IReadOnlyDictionary<string, IReadOnlyList<int>> FunctionOverloads,
    IReadOnlyDictionary<string, int> Types,
    IReadOnlyDictionary<string, int> Traits,
    IReadOnlyDictionary<string, int> Effects,
    IReadOnlyDictionary<string, int> Constructors)
{
    public static ScopePayload Create(Scope scope, int index, IReadOnlyDictionary<Scope, int> scopeIndices)
    {
        var parentIndex = scope.Parent != null && scopeIndices.TryGetValue(scope.Parent, out var resolvedParentIndex)
            ? resolvedParentIndex
            : -1;
        return new ScopePayload(
            index,
            parentIndex,
            scope.Depth,
            scope.Kind.ToString(),
            ToIntMap(scope.GetLocalBindings()),
            scope.GetLocalFunctionOverloads()
                .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                .ToDictionary(
                    static entry => entry.Key,
                    static entry => (IReadOnlyList<int>)entry.Value.Select(static id => id.Value).ToArray(),
                    StringComparer.Ordinal),
            ToIntMap(scope.GetLocalTypes()),
            ToIntMap(scope.GetLocalTraits()),
            ToIntMap(scope.GetLocalAbilities()),
            ToIntMap(scope.GetLocalConstructors()));
    }

    private static IReadOnlyDictionary<string, int> ToIntMap(IReadOnlyDictionary<string, SymbolId> map) =>
        map.OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .ToDictionary(static entry => entry.Key, static entry => entry.Value.Value, StringComparer.Ordinal);
}

public sealed record ModuleRegistryPayload(
    string SchemaVersion,
    IReadOnlyDictionary<string, int> RootModules,
    IReadOnlyDictionary<string, int> ModulePaths,
    IReadOnlyDictionary<string, int> ModuleIdentityKeys,
    IReadOnlyDictionary<string, IReadOnlyList<int>> ModuleCandidatesByPath,
    IReadOnlyDictionary<int, IReadOnlyList<int>> MemberOwnerModules,
    IReadOnlyList<ModuleRegistryModulePayload> Modules,
    string Hash)
{
    public const string CurrentSchemaVersion = "module-registry-payload-v1";

    public static ModuleRegistryPayload Create(ModuleRegistry? registry)
    {
        if (registry == null)
        {
            var empty = new ModuleRegistryPayload(CurrentSchemaVersion, EmptyStringIntMap(), EmptyStringIntMap(), EmptyStringIntMap(), EmptyStringIntListMap(), EmptyIntIntListMap(), [], "");
            return empty with { Hash = ModuleArtifactHash.ComputeJsonHash(empty with { Hash = "" }) };
        }

        var payload = new ModuleRegistryPayload(
            CurrentSchemaVersion,
            ToIntMap(registry.RootModules),
            ToIntMap(registry.ModulePaths),
            ToIntMap(registry.ModuleIdentityKeys),
            ToIntListMap(registry.ModuleCandidatesByPath),
            registry.MemberOwnerModules
                .OrderBy(static entry => entry.Key.Value)
                .ToDictionary(
                    static entry => entry.Key.Value,
                    static entry => (IReadOnlyList<int>)entry.Value.Select(static id => id.Value).Order().ToArray()),
            registry.Modules
                .OrderBy(static entry => entry.Key.Value)
                .Select(static entry => ModuleRegistryModulePayload.Create(entry.Key, entry.Value))
                .ToArray(),
            "");

        return payload with { Hash = ModuleArtifactHash.ComputeJsonHash(payload with { Hash = "" }) };
    }

    private static IReadOnlyDictionary<string, int> ToIntMap(IReadOnlyDictionary<string, SymbolId> map) =>
        map.OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .ToDictionary(static entry => entry.Key, static entry => entry.Value.Value, StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, IReadOnlyList<int>> ToIntListMap(IReadOnlyDictionary<string, IReadOnlyList<SymbolId>> map) =>
        map.OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .ToDictionary(
                static entry => entry.Key,
                static entry => (IReadOnlyList<int>)entry.Value.Select(static id => id.Value).Order().ToArray(),
                StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, int> EmptyStringIntMap() =>
        new Dictionary<string, int>(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, IReadOnlyList<int>> EmptyStringIntListMap() =>
        new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);

    private static IReadOnlyDictionary<int, IReadOnlyList<int>> EmptyIntIntListMap() =>
        new Dictionary<int, IReadOnlyList<int>>();
}

public sealed record ModuleRegistryModulePayload(
    int Id,
    string DisplayKey,
    string IdentityKey,
    string? PackageAlias,
    string? PackageInstanceKey,
    IReadOnlyList<string> Path,
    IReadOnlyList<int> Members,
    IReadOnlyList<ModuleBindingPayload> Exports,
    IReadOnlyList<int> Imports,
    int ParentModule,
    bool UsesExplicitExports)
{
    public static ModuleRegistryModulePayload Create(SymbolId id, ModuleSymbol module) =>
        new(
            id.Value,
            module.Identity.ToDisplayKey(),
            module.Identity.ToIdentityKey(),
            module.PackageAlias,
            module.PackageInstanceKey,
            module.Path,
            module.Members.Select(static value => value.Value).ToArray(),
            module.ExportedBindings.Select(ModuleBindingPayload.Create).ToArray(),
            module.Imports.Select(static value => value.Value).ToArray(),
            module.ParentModule?.Value ?? SymbolId.None.Value,
            module.UsesExplicitExports);
}

public sealed record ModuleBindingPayload(string Name, int SymbolId, string Kind)
{
    public static ModuleBindingPayload Create(ModuleBindingEntry binding) =>
        new(binding.Name, binding.SymbolId.Value, binding.Kind.ToString());
}

public sealed record TypeSubstitutionPayload(
    string SchemaVersion,
    int NextFreshVarIndex,
    IReadOnlyList<TypeSubstitutionBindingPayload> Bindings,
    int NextFreshValueVarIndex,
    IReadOnlyList<ValueSubstitutionBindingPayload> ValueBindings,
    string Hash)
{
    public const string CurrentSchemaVersion = "type-substitution-payload-v3";

    public static TypeSubstitutionPayload Create(Substitution? substitution)
    {
        if (substitution == null)
        {
            var empty = new TypeSubstitutionPayload(CurrentSchemaVersion, 0, [], 0, [], "");
            return empty with { Hash = ModuleArtifactHash.ComputeJsonHash(empty with { Hash = "" }) };
        }

        var payload = new TypeSubstitutionPayload(
            CurrentSchemaVersion,
            substitution.NextFreshVarIndex,
            substitution.GetBindingsSnapshot()
                .OrderBy(static binding => binding.TypeVarIndex)
                .Select(static binding => TypeSubstitutionBindingPayload.Create(binding))
                .ToArray(),
            substitution.NextFreshValueVarIndex,
            substitution.GetValueBindingsSnapshot()
                .OrderBy(static binding => binding.ValueVarIndex)
                .Select(static binding => ValueSubstitutionBindingPayload.Create(binding))
                .ToArray(),
            "");

        return payload with { Hash = ModuleArtifactHash.ComputeJsonHash(payload with { Hash = "" }) };
    }

    public bool HasValidHash() =>
        !string.IsNullOrWhiteSpace(Hash) &&
        string.Equals(Hash, ModuleArtifactHash.ComputeJsonHash(this with { Hash = "" }), StringComparison.Ordinal);

    public bool TryRestoreSubstitution(out Substitution substitution)
        => TryRestoreSubstitution(remapper: null, out substitution);

    internal bool TryRestoreSubstitution(
        LiveStateIdRemapper? remapper,
        out Substitution substitution)
    {
        substitution = new Substitution();
        if (!HasValidHash() ||
            !string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return false;
        }

        var bindings = new List<SubstitutionBinding>(Bindings.Count);
        foreach (var binding in Bindings.OrderBy(static binding => binding.TypeVarIndex))
        {
            if (!binding.TryRestoreBinding(remapper, out var restored))
            {
                return false;
            }

            bindings.Add(restored);
        }

        var valueBindings = new List<ValueSubstitutionBinding>(ValueBindings.Count);
        foreach (var binding in ValueBindings.OrderBy(static binding => binding.ValueVarIndex))
        {
            if (!binding.TryRestoreBinding(remapper, out var restored))
            {
                return false;
            }

            valueBindings.Add(restored);
        }

        substitution.RestoreFromSnapshot(
            bindings,
            remapper?.RemapNextTypeVariable(NextFreshVarIndex) ?? NextFreshVarIndex,
            valueBindings,
            remapper?.RemapNextValueVariable(NextFreshValueVarIndex) ?? NextFreshValueVarIndex);
        return true;
    }
}

public sealed record ValueSubstitutionBindingPayload(
    int ValueVarIndex,
    GenericValueArgumentPayload RawValue,
    GenericValueArgumentPayload ResolvedValue)
{
    public static ValueSubstitutionBindingPayload Create(ValueSubstitutionBinding binding) =>
        new(
            binding.ValueVarIndex,
            GenericValueArgumentPayload.Create(binding.RawValue),
            GenericValueArgumentPayload.Create(binding.ResolvedValue));

    internal bool TryRestoreBinding(
        LiveStateIdRemapper? remapper,
        out ValueSubstitutionBinding binding)
    {
        var raw = RawValue.Restore(remapper);
        var resolved = ResolvedValue.Restore(remapper);
        binding = new ValueSubstitutionBinding(
            remapper?.RemapValueVariable(ValueVarIndex) ?? ValueVarIndex,
            raw,
            resolved);
        return true;
    }
}

public sealed record TypeSubstitutionBindingPayload(
    int TypeVarIndex,
    string Raw,
    string RawHash,
    TypeShapePayload RawShape,
    string Resolved,
    string ResolvedHash,
    TypeShapePayload ResolvedShape,
    string Chain,
    string Status)
{
    public static TypeSubstitutionBindingPayload Create(SubstitutionBinding binding)
    {
        var raw = binding.RawType.ToString();
        var resolved = binding.ResolvedType.ToString();
        return new TypeSubstitutionBindingPayload(
            binding.TypeVarIndex,
            raw,
            ModuleArtifactHash.ComputeTextHash(raw),
            TypeShapePayload.Create(binding.RawType),
            resolved,
            ModuleArtifactHash.ComputeTextHash(resolved),
            TypeShapePayload.Create(binding.ResolvedType),
            binding.Chain,
            string.Equals(raw, resolved, StringComparison.Ordinal) ? "stable" : "rewritten");
    }

    public bool TryRestoreBinding(out SubstitutionBinding binding)
        => TryRestoreBinding(remapper: null, out binding);

    internal bool TryRestoreBinding(
        LiveStateIdRemapper? remapper,
        out SubstitutionBinding binding)
    {
        binding = null!;
        if (!RawShape.TryRestoreType(out var originalRaw) ||
            !ResolvedShape.TryRestoreType(out var originalResolved) ||
            !string.Equals(RawHash, ModuleArtifactHash.ComputeTextHash(originalRaw.ToString()), StringComparison.Ordinal) ||
            !string.Equals(ResolvedHash, ModuleArtifactHash.ComputeTextHash(originalResolved.ToString()), StringComparison.Ordinal))
        {
            return false;
        }

        Eidosc.Types.Type raw;
        Eidosc.Types.Type resolved;
        var rawRestored = true;
        var resolvedRestored = true;
        if (remapper == null)
        {
            raw = originalRaw;
            resolved = originalResolved;
        }
        else
        {
            rawRestored = RawShape.TryRestoreType(remapper, out raw);
            resolvedRestored = ResolvedShape.TryRestoreType(remapper, out resolved);
        }
        if (!rawRestored ||
            !resolvedRestored)
        {
            return false;
        }

        binding = new SubstitutionBinding
        {
            TypeVarIndex = remapper?.RemapTypeVariable(TypeVarIndex) ?? TypeVarIndex,
            RawType = raw,
            ResolvedType = resolved,
            Chain = Chain
        };
        return true;
    }
}

public sealed record GenericValueArgumentPayload(
    int ParameterIndex,
    string CanonicalText,
    string CanonicalHash,
    string DisplayText,
    int TypeId,
    int ReferencedParameterIndex,
    int ValueVariableIndex)
{
    public static GenericValueArgumentPayload Create(GenericValueArgument argument) =>
        new(
            argument.ParameterIndex,
            argument.CanonicalText,
            argument.CanonicalHash,
            argument.DisplayText,
            argument.TypeId.Value,
            argument.ReferencedParameterIndex,
            argument.ValueVariableIndex);

    public GenericValueArgumentPayload RemapIds(LiveStateIdRemapper remapper) =>
        this with
        {
            TypeId = remapper.RemapType(TypeId),
            ValueVariableIndex = remapper.RemapValueVariable(ValueVariableIndex)
        };

    public GenericValueArgument Restore(LiveStateIdRemapper? remapper) =>
        new(
            ParameterIndex,
            CanonicalText,
            CanonicalHash,
            DisplayText,
            new TypeId(remapper?.RemapType(TypeId) ?? TypeId),
            ReferencedParameterIndex,
            remapper?.RemapValueVariable(ValueVariableIndex) ?? ValueVariableIndex);
}

public sealed record GenericEffectArgumentPayload(int ParameterIndex, TypeShapePayload Argument)
{
    public GenericEffectArgumentPayload RemapIds(LiveStateIdRemapper remapper) =>
        this with { Argument = Argument.RemapIds(remapper) };
}

public sealed record TypeShapePayload(
    string Kind,
    int TypeId,
    string Display,
    string CanonicalKey,
    string Hash,
    int? TypeVarIndex = null,
    bool? IsErrorRecovery = null,
    bool? IsRigidExistential = null,
    int? SymbolId = null,
    string? Name = null,
    int? ConstructorVarIndex = null,
    IReadOnlyList<TypeShapePayload>? Arguments = null,
    IReadOnlyList<GenericValueArgumentPayload>? ValueArguments = null,
    IReadOnlyList<GenericEffectArgumentPayload>? EffectArguments = null,
    IReadOnlyList<TypeShapePayload>? Parameters = null,
    TypeShapePayload? Result = null,
    TypeShapePayload? Inner = null,
    TypeShapePayload? Witness = null,
    TypeShapePayload? Effects = null,
    IReadOnlyList<TypeShapePayload>? Elements = null,
    IReadOnlyList<TypeShapePayload>? EffectMembers = null,
    IReadOnlyList<int>? EffectVariableIds = null,
    TypeShapePayload? EffectVariableInstance = null,
    TypeShapePayload? Effect = null,
    TypeShapePayload? Payload = null,
    TypeShapePayload? ResumeArg = null)
{
    public static TypeShapePayload Create(Eidosc.Types.Type type)
    {
        var visited = new HashSet<Eidosc.Types.Type>(ReferenceEqualityComparer.Instance);
        var payload = CreateCore(type, visited);
        return payload with { Hash = ModuleArtifactHash.ComputeJsonHash(payload with { Hash = "" }) };
    }

    private static TypeShapePayload Create(Eidosc.Types.Type type, HashSet<Eidosc.Types.Type> visited)
    {
        var payload = CreateCore(type, visited);
        return payload with { Hash = ModuleArtifactHash.ComputeJsonHash(payload with { Hash = "" }) };
    }

    public bool TryRestoreType(out Eidosc.Types.Type type)
    {
        type = null!;
        if (!HasValidHash())
        {
            return false;
        }

        return TryRestoreTypeCore(this, out type);
    }

    internal bool TryRestoreType(
        LiveStateIdRemapper remapper,
        out Eidosc.Types.Type type)
    {
        type = null!;
        if (!HasValidHash())
        {
            return false;
        }

        var remapped = RemapIds(remapper);
        return remapped.HasValidHash() && TryRestoreTypeCore(remapped, out type);
    }

    internal TypeShapePayload RemapIds(LiveStateIdRemapper remapper)
    {
        ArgumentNullException.ThrowIfNull(remapper);
        var payload = this with
        {
            TypeId = remapper.RemapType(TypeId),
            TypeVarIndex = TypeVarIndex.HasValue ? remapper.RemapTypeVariable(TypeVarIndex.Value) : null,
            SymbolId = SymbolId.HasValue ? remapper.RemapSymbol(SymbolId.Value) : null,
            ConstructorVarIndex = ConstructorVarIndex.HasValue
                ? remapper.RemapTypeVariable(ConstructorVarIndex.Value)
                : null,
            Arguments = RemapList(Arguments, remapper),
            ValueArguments = ValueArguments?
                .Select(value => value.RemapIds(remapper))
                .ToArray(),
            EffectArguments = EffectArguments?
                .Select(value => value.RemapIds(remapper))
                .ToArray(),
            Parameters = RemapList(Parameters, remapper),
            Result = Result?.RemapIds(remapper),
            Inner = Inner?.RemapIds(remapper),
            Witness = Witness?.RemapIds(remapper),
            Effects = Effects?.RemapIds(remapper),
            Elements = RemapList(Elements, remapper),
            EffectMembers = RemapList(EffectMembers, remapper),
            EffectVariableIds = EffectVariableIds?
                .Select(remapper.RemapTypeVariable)
                .ToArray(),
            EffectVariableInstance = EffectVariableInstance?.RemapIds(remapper),
            Effect = Effect?.RemapIds(remapper),
            Payload = Payload?.RemapIds(remapper),
            ResumeArg = ResumeArg?.RemapIds(remapper),
            Hash = ""
        };
        return payload with { Hash = ModuleArtifactHash.ComputeJsonHash(payload with { Hash = "" }) };
    }

    private static IReadOnlyList<TypeShapePayload>? RemapList(
        IReadOnlyList<TypeShapePayload>? values,
        LiveStateIdRemapper remapper) =>
        values?.Select(value => value.RemapIds(remapper)).ToArray();

    public bool HasValidHash() =>
        !string.IsNullOrWhiteSpace(Hash) &&
        string.Equals(Hash, ModuleArtifactHash.ComputeJsonHash(this with { Hash = "" }), StringComparison.Ordinal);

    // CreateCore walks the type graph depth-first. Only mutable-link types can
    // form cycles or arbitrarily deep chains during stdlib type inference:
    //   - TyVar.Instance (the unification union-find link, which can point at a
    //     type that mentions the same TyVar again),
    //   - RequestType, whose ability result/payload may close back over the request.
    // Plain structural types (TyCon, TyTuple, TyFun, TyRef, ...) are value-shaped
    // records whose children may freely share leaf types such as a single `Int`
    // TyCon instance, e.g. inside `(Int, Int)`. Tracking those by reference identity
    // would mistake legitimate shared leaves for cycles and emit identity-only
    // markers that round-trip back as error-recovery `<type>` nodes. We therefore
    // guard only cycle-capable types and let structural types re-serialize fully.
    private static TypeShapePayload CreateCore(Eidosc.Types.Type type, HashSet<Eidosc.Types.Type> visited)
    {
        if (IsCycleSource(type) && !visited.Add(type))
        {
            return new TypeShapePayload(
                type.GetType().Name,
                type.Id.Value,
                type.ToString(),
                TypeCanonicalKeyBuilder.Build(type, ResolveTyConTypeId),
                "");
        }

        var display = type.ToString();
        var canonicalKey = TypeCanonicalKeyBuilder.Build(type, ResolveTyConTypeId);
        return type switch
        {
            TyVar variable => new TypeShapePayload(
                nameof(TyVar),
                type.Id.Value,
                display,
                canonicalKey,
                "",
                TypeVarIndex: variable.Index,
                IsErrorRecovery: variable.IsErrorRecovery,
                IsRigidExistential: variable.IsRigidExistential,
                EffectVariableInstance: variable.Instance == null ? null : Create(variable.Instance, visited)),

            TyCon constructor => new TypeShapePayload(
                nameof(TyCon),
                type.Id.Value,
                display,
                canonicalKey,
                "",
                SymbolId: constructor.Symbol.Value,
                Name: constructor.Name,
                ConstructorVarIndex: constructor.ConstructorVarIndex,
                Arguments: constructor.Args.Select(argument => Create(argument, visited)).ToArray(),
                ValueArguments: constructor.ValueArgs.Select(GenericValueArgumentPayload.Create).ToArray(),
                EffectArguments: constructor.EffectArgs
                    .Select(argument => new GenericEffectArgumentPayload(
                        argument.ParameterIndex,
                        Create(argument.Argument, visited)))
                    .ToArray()),

            TyReflProof proof => new TypeShapePayload(
                nameof(TyReflProof),
                type.Id.Value,
                display,
                canonicalKey,
                "",
                Witness: proof.WitnessType == null ? null : Create(proof.WitnessType, visited)),

            TyFun function => new TypeShapePayload(
                nameof(TyFun),
                type.Id.Value,
                display,
                canonicalKey,
                "",
                Parameters: function.Params.Select(parameter => Create(parameter, visited)).ToArray(),
                Result: Create(function.Result, visited),
                Effects: Create(function.Effects, visited)),

            TyTuple tuple => new TypeShapePayload(
                nameof(TyTuple),
                type.Id.Value,
                display,
                canonicalKey,
                "",
                Elements: tuple.Elements.Select(element => Create(element, visited)).ToArray()),

            TyRef reference => new TypeShapePayload(
                nameof(TyRef),
                type.Id.Value,
                display,
                canonicalKey,
                "",
                Inner: Create(reference.Inner, visited)),

            TyMutRef reference => new TypeShapePayload(
                nameof(TyMutRef),
                type.Id.Value,
                display,
                canonicalKey,
                "",
                Inner: Create(reference.Inner, visited)),

            TyShared shared => new TypeShapePayload(
                nameof(TyShared),
                type.Id.Value,
                display,
                canonicalKey,
                "",
                Inner: Create(shared.Inner, visited)),

            EffectRow abilitySet => new TypeShapePayload(
                nameof(EffectRow),
                type.Id.Value,
                display,
                canonicalKey,
                "",
                EffectMembers: abilitySet.Effects
                    .OrderBy(static ability => ability.Name, StringComparer.Ordinal)
                    .ThenBy(static ability => ability.Symbol.Value)
                    .Select(ability => Create(ability, visited))
                    .ToArray(),
                EffectVariableIds: abilitySet.Variables
                    .OrderBy(static variable => variable.Id)
                    .Select(static variable => variable.Id)
                    .ToArray()),

            EffectTag ability => new TypeShapePayload(
                nameof(EffectTag),
                type.Id.Value,
                display,
                canonicalKey,
                "",
                SymbolId: ability.Symbol.Value,
                Name: ability.Name,
                Arguments: ability.TypeArgs.Select(argument => Create(argument, visited)).ToArray()),

            RequestType request => new TypeShapePayload(
                nameof(RequestType),
                type.Id.Value,
                display,
                canonicalKey,
                "",
                Effect: Create(request.Effect, visited),
                Result: Create(request.Result, visited),
                Payload: request.Payload == null ? null : Create(request.Payload, visited),
                ResumeArg: request.ResumeArg == null ? null : Create(request.ResumeArg, visited)),

            _ => new TypeShapePayload(
                type.GetType().Name,
                type.Id.Value,
                display,
                canonicalKey,
                "")
        };
    }

    private static TypeId ResolveTyConTypeId(TyCon constructor)
    {
        if (constructor.Id.IsValid)
        {
            return constructor.Id;
        }

        return constructor.Symbol.IsValid
            ? new TypeId(constructor.Symbol.Value)
            : BaseTypes.GetBuiltInTypeId(constructor.Name);
    }

    // Types whose mutable links can close back over themselves and therefore need
    // cycle protection during serialization. A TyVar only needs protection when it
    // carries a non-null Instance (the unification union-find link, which can point
    // at a type that mentions the same TyVar again); a plain type-parameter TyVar is
    // safe to re-serialize, e.g. both ends of `'t -> 't` must round-trip with their
    // TypeVarIndex. Structural records are intentionally excluded so that shared
    // leaves re-serialize correctly (see CreateCore).
    private static bool IsCycleSource(Eidosc.Types.Type type) =>
        type is RequestType ||
        (type is TyVar variable && variable.Instance != null);

    private static bool TryRestoreTypeCore(TypeShapePayload payload, out Eidosc.Types.Type type)
    {
        type = null!;
        switch (payload.Kind)
        {
            case nameof(TyVar):
                if (payload.TypeVarIndex is not { } variableIndex)
                {
                    return false;
                }

                var variable = new TyVar
                {
                    Id = new TypeId(payload.TypeId),
                    Index = variableIndex,
                    IsErrorRecovery = payload.IsErrorRecovery == true,
                    IsRigidExistential = payload.IsRigidExistential == true
                };
                if (payload.EffectVariableInstance != null)
                {
                    if (!payload.EffectVariableInstance.TryRestoreType(out var instance))
                    {
                        return false;
                    }

                    variable.Instance = instance;
                }

                type = variable;
                return true;

            case nameof(TyCon):
                if (!TryRestoreTypeList(payload.Arguments, out var arguments))
                {
                    return false;
                }

                var effectArguments = new List<GenericEffectArgument>();
                foreach (var effectArgument in payload.EffectArguments ?? [])
                {
                    if (!effectArgument.Argument.TryRestoreType(out var restoredArgument))
                    {
                        return false;
                    }

                    effectArguments.Add(new GenericEffectArgument(
                        effectArgument.ParameterIndex,
                        restoredArgument));
                }

                type = new TyCon
                {
                    Id = new TypeId(payload.TypeId),
                    Symbol = new global::Eidosc.SymbolId(payload.SymbolId ?? global::Eidosc.SymbolId.None.Value),
                    Name = payload.Name ?? "",
                    ConstructorVarIndex = payload.ConstructorVarIndex,
                    Args = arguments,
                    ValueArgs = (payload.ValueArguments ?? [])
                        .Select(static value => value.Restore(remapper: null))
                        .ToList(),
                    EffectArgs = effectArguments
                };
                return true;

            case nameof(TyReflProof):
                Eidosc.Types.Type? witness = null;
                if (payload.Witness != null &&
                    !payload.Witness.TryRestoreType(out witness))
                {
                    return false;
                }

                type = new TyReflProof
                {
                    Id = new TypeId(payload.TypeId),
                    WitnessType = payload.Witness == null ? null : witness
                };
                return true;

            case nameof(TyFun):
                if (!TryRestoreTypeList(payload.Parameters, out var parameters) ||
                    payload.Result == null ||
                    payload.Effects == null ||
                    !payload.Result.TryRestoreType(out var result) ||
                    !payload.Effects.TryRestoreType(out var restoredAbilities) ||
                    restoredAbilities is not EffectRow abilitySet)
                {
                    return false;
                }

                type = new TyFun
                {
                    Id = new TypeId(payload.TypeId),
                    Params = parameters,
                    Result = result,
                    Effects = abilitySet
                };
                return true;

            case nameof(TyTuple):
                if (!TryRestoreTypeList(payload.Elements, out var elements))
                {
                    return false;
                }

                type = new TyTuple
                {
                    Id = new TypeId(payload.TypeId),
                    Elements = elements
                };
                return true;

            case nameof(TyRef):
                if (payload.Inner == null ||
                    !payload.Inner.TryRestoreType(out var refInner))
                {
                    return false;
                }

                type = new TyRef
                {
                    Id = new TypeId(payload.TypeId),
                    Inner = refInner
                };
                return true;

            case nameof(TyMutRef):
                if (payload.Inner == null ||
                    !payload.Inner.TryRestoreType(out var mutRefInner))
                {
                    return false;
                }

                type = new TyMutRef
                {
                    Id = new TypeId(payload.TypeId),
                    Inner = mutRefInner
                };
                return true;

            case nameof(TyShared):
                if (payload.Inner == null ||
                    !payload.Inner.TryRestoreType(out var sharedInner))
                {
                    return false;
                }

                type = new TyShared
                {
                    Id = new TypeId(payload.TypeId),
                    Inner = sharedInner
                };
                return true;

            case nameof(EffectRow):
                if (!TryRestoreEffectMembers(payload.EffectMembers, out var abilities))
                {
                    return false;
                }

                var effectVariables = payload.EffectVariableIds?
                    .Select(static id => new EffectVariable { Id = id })
                    .ToArray() ?? [];

                type = new EffectRow(abilities, effectVariables)
                {
                    Id = new TypeId(payload.TypeId)
                };
                return true;

            case nameof(EffectTag):
                if (!TryRestoreTypeList(payload.Arguments, out var typeArguments))
                {
                    return false;
                }

                type = new EffectTag(
                    new global::Eidosc.SymbolId(payload.SymbolId ?? global::Eidosc.SymbolId.None.Value),
                    payload.Name ?? "",
                    typeArguments)
                {
                    Id = new TypeId(payload.TypeId)
                };
                return true;

            case nameof(RequestType):
                if (payload.Effect == null ||
                    payload.Result == null ||
                    !payload.Effect.TryRestoreType(out var requestEffect) ||
                    !payload.Result.TryRestoreType(out var requestResult))
                {
                    return false;
                }

                Eidosc.Types.Type? requestPayload = null;
                if (payload.Payload != null &&
                    !payload.Payload.TryRestoreType(out requestPayload))
                {
                    return false;
                }

                Eidosc.Types.Type? requestResumeArg = null;
                if (payload.ResumeArg != null &&
                    !payload.ResumeArg.TryRestoreType(out requestResumeArg))
                {
                    return false;
                }

                type = new RequestType
                {
                    Id = new TypeId(payload.TypeId),
                    Effect = requestEffect,
                    Result = requestResult,
                    Payload = requestPayload,
                    ResumeArg = requestResumeArg
                };
                return true;

            default:
                return false;
        }
    }

    private static bool TryRestoreTypeList(
        IReadOnlyList<TypeShapePayload>? payloads,
        out List<Eidosc.Types.Type> types)
    {
        types = [];
        if (payloads == null)
        {
            return true;
        }

        foreach (var payload in payloads)
        {
            if (!payload.TryRestoreType(out var type))
            {
                return false;
            }

            types.Add(type);
        }

        return true;
    }

    private static bool TryRestoreEffectMembers(
        IReadOnlyList<TypeShapePayload>? payloads,
        out HashSet<EffectTag> abilities)
    {
        abilities = [];
        if (payloads == null)
        {
            return true;
        }

        foreach (var payload in payloads)
        {
            if (!payload.TryRestoreType(out var type) ||
                type is not EffectTag ability)
            {
                return false;
            }

            abilities.Add(ability);
        }

        return true;
    }
}

public sealed record AstInferredTypeMapPayload(
    string SchemaVersion,
    string ModuleKey,
    string ModuleIdentityKey,
    IReadOnlyList<string> SourcePaths,
    int AstNodeCount,
    string AstStructureHash,
    IReadOnlyList<AstInferredTypeEntryPayload> Entries,
    string Hash)
{
    public const string CurrentSchemaVersion = "ast-inferred-type-map-payload-v7";

    public static AstInferredTypeMapPayload Create(
        ModuleDecl? ast,
        TypeInferer? typeInferer,
        string moduleKey = "",
        string moduleIdentityKey = "",
        IReadOnlyList<string>? sourcePaths = null) =>
        Create(ast, typeInferer, moduleKey, moduleIdentityKey, sourcePaths, allStableNodes: null);

    internal static AstInferredTypeMapPayload Create(
        ModuleDecl? ast,
        TypeInferer? typeInferer,
        string moduleKey,
        string moduleIdentityKey,
        IReadOnlyList<string>? sourcePaths,
        IReadOnlyList<AstStableNodeEntry>? allStableNodes)
    {
        if (ast == null)
        {
            var empty = new AstInferredTypeMapPayload(
                CurrentSchemaVersion,
                moduleKey,
                moduleIdentityKey,
                sourcePaths?.ToArray() ?? [],
                0,
                ComputeStructureHash([]),
                [],
                "");
            return empty with { Hash = ModuleArtifactHash.ComputeJsonHash(empty with { Hash = "" }) };
        }

        allStableNodes ??= AstStableNodeTraversal.Enumerate(ast);
        var stableNodes = string.IsNullOrWhiteSpace(moduleKey)
            ? allStableNodes
            : AstStableNodeTraversal.EnumerateModule(allStableNodes, moduleKey, moduleIdentityKey, sourcePaths);
        var entries = new List<AstInferredTypeEntryPayload>();
        foreach (var stableNode in stableNodes)
        {
            var node = stableNode.Node;
            if (node.InferredType is not Eidosc.Types.Type type)
            {
                continue;
            }

            var raw = type.ToString();
            var resolvedType = typeInferer?.Substitution.Apply(type) ?? type;
            var resolved = resolvedType.ToString();
            var rawEffects = node.InferredEffects;
            var resolvedEffects = rawEffects == null
                ? null
                : (EffectRow)(typeInferer?.Substitution.Apply(rawEffects) ?? rawEffects);
            var stableKey = stableNode.StableIdentity;
            var key = stableKey.LegacyOrdinalKey(stableNode.Ordinal, node.SymbolId.Value);
            entries.Add(new AstInferredTypeEntryPayload(
                stableNode.Ordinal,
                key,
                stableKey.StableKey,
                stableKey,
                node.GetType().Name,
                SourceSpanPayload.Create(node.Span),
                node.SymbolId.Value,
                raw,
                ModuleArtifactHash.ComputeTextHash(raw),
                TypeShapePayload.Create(type),
                resolved,
                ModuleArtifactHash.ComputeTextHash(resolved),
                TypeShapePayload.Create(resolvedType),
                rawEffects == null ? null : TypeShapePayload.Create(rawEffects),
                resolvedEffects == null ? null : TypeShapePayload.Create(resolvedEffects),
                stableKey.Details));
        }

        var payload = new AstInferredTypeMapPayload(
            CurrentSchemaVersion,
            moduleKey,
            moduleIdentityKey,
            sourcePaths?.ToArray() ?? [],
            stableNodes.Count,
            ComputeStructureHash(stableNodes),
            entries,
            "");
        return payload with { Hash = ModuleArtifactHash.ComputeJsonHash(payload with { Hash = "" }) };
    }

    public bool HasValidHash() =>
        SchemaVersion == CurrentSchemaVersion &&
        !string.IsNullOrWhiteSpace(Hash) &&
        string.Equals(
            Hash,
            ModuleArtifactHash.ComputeJsonHash(this with { Hash = "" }),
            StringComparison.Ordinal);

    internal static string ComputeStructureHash(IReadOnlyList<AstStableNodeEntry> nodes) =>
        ModuleArtifactHash.ComputeJsonHash(nodes.Select(static entry => entry.StableIdentity.StableKey).ToArray());
}

public sealed record AstInferredTypeEntryPayload(
    int Ordinal,
    string Key,
    string StableKey,
    AstInferredTypeStableKeyPayload StableIdentity,
    string NodeKind,
    SourceSpanPayload Span,
    int SymbolId,
    string RawType,
    string RawTypeHash,
    TypeShapePayload RawTypeShape,
    string ResolvedType,
    string ResolvedTypeHash,
    TypeShapePayload ResolvedTypeShape,
    TypeShapePayload? RawEffectsShape,
    TypeShapePayload? ResolvedEffectsShape,
    string Details);

public sealed record AstInferredTypeStableKeyPayload(
    string SchemaVersion,
    string ModuleKey,
    string ModuleIdentityKey,
    string NodeKind,
    SourceSpanPayload Span,
    IReadOnlyList<int> SiblingPath,
    string Details,
    string StableKey)
{
    public const string CurrentSchemaVersion = "ast-inferred-type-stable-key-v3";

    public static AstInferredTypeStableKeyPayload Create(
        string moduleKey,
        string moduleIdentityKey,
        EidosAstNode node,
        string details,
        IReadOnlyList<int> siblingPath)
    {
        var payload = new AstInferredTypeStableKeyPayload(
            CurrentSchemaVersion,
            moduleKey,
            moduleIdentityKey,
            node.GetType().Name,
            SourceSpanPayload.Create(node.Span),
            siblingPath.ToArray(),
            details,
            "");
        return payload with { StableKey = ComputeStableKey(payload) };
    }

    public string LegacyOrdinalKey(int ordinal, int symbolId) =>
        $"{ordinal}:{NodeKind}:{Span.FilePath ?? ""}:{Span.Position}:{Span.Length}:{symbolId}";

    private static string ComputeStableKey(AstInferredTypeStableKeyPayload payload)
    {
        var keyInput = new
        {
            payload.SchemaVersion,
            payload.ModuleKey,
            payload.ModuleIdentityKey,
            payload.NodeKind,
            Span = new
            {
                payload.Span.FilePath,
                payload.Span.Position,
                payload.Span.Length
            },
            payload.SiblingPath,
            payload.Details
        };
        return ModuleArtifactHash.ComputeJsonHash(keyInput);
    }
}

public sealed record HirGraphPayload(
    string SchemaVersion,
    HirModulePayload? Module,
    HirGraphCounts Counts,
    IReadOnlyList<HirGraphNodePayload> Nodes,
    IReadOnlyList<HirGraphEdgePayload> Edges,
    string Hash)
{
    public const string CurrentSchemaVersion = "hir-graph-payload-v1";

    public static HirGraphPayload Create(HirModule? module)
    {
        if (module == null)
        {
            var empty = new HirGraphPayload(CurrentSchemaVersion, null, new HirGraphCounts(0, 0, 0, 0, 0), [], [], "");
            return empty with { Hash = ModuleArtifactHash.ComputeJsonHash(empty with { Hash = "" }) };
        }

        var builder = new HirGraphPayloadBuilder();
        builder.Visit(module, parentId: null, role: "root", index: 0);
        var nodes = builder.Nodes
            .OrderBy(static node => node.Id)
            .ToArray();
        var edges = builder.Edges
            .OrderBy(static edge => edge.From)
            .ThenBy(static edge => edge.Role, StringComparer.Ordinal)
            .ThenBy(static edge => edge.Index)
            .ThenBy(static edge => edge.To)
            .ToArray();
        var counts = new HirGraphCounts(
            module.Declarations.Count,
            module.Declarations.OfType<HirFunc>().Count(),
            module.Declarations.OfType<HirVal>().Count() + module.Declarations.OfType<HirVarDecl>().Count(),
            module.Declarations.Count(static declaration => declaration.Kind == HirKind.Type),
            nodes.Length);
        var payload = new HirGraphPayload(
            CurrentSchemaVersion,
            HirModulePayload.Create(module),
            counts,
            nodes,
            edges,
            "");

        return payload with { Hash = ModuleArtifactHash.ComputeJsonHash(payload with { Hash = "" }) };
    }
}

public sealed record HirModulePayload(
    string Name,
    string? PackageAlias,
    string? PackageInstanceKey,
    IReadOnlyList<string> Path,
    IReadOnlyList<int> Exports,
    IReadOnlyList<string> LinkLibraries)
{
    public static HirModulePayload Create(HirModule module) =>
        new(
            module.Name,
            module.PackageAlias,
            module.PackageInstanceKey,
            module.Path,
            module.Exports.Select(static id => id.Value).ToArray(),
            module.LinkLibraries.Order(StringComparer.Ordinal).ToArray());
}

public sealed record HirGraphCounts(
    int DeclarationCount,
    int FunctionCount,
    int ValueCount,
    int TypeDeclarationCount,
    int NodeCount);

public sealed record HirGraphNodePayload(
    int Id,
    string Kind,
    string ConcreteKind,
    string Name,
    int SymbolId,
    int TypeId,
    string OrdinalPath,
    SourceSpanPayload Span);

public sealed record HirGraphEdgePayload(int From, string Role, int To, int Index);

public sealed record MirGraphPayload(
    string SchemaVersion,
    MirModulePayload? Module,
    IReadOnlyList<MirGraphFunctionPayload> Functions,
    string Hash)
{
    public const string CurrentSchemaVersion = "mir-graph-payload-v1";

    public static MirGraphPayload Create(MirModule? module)
    {
        if (module == null)
        {
            var empty = new MirGraphPayload(CurrentSchemaVersion, null, [], "");
            return empty with { Hash = ModuleArtifactHash.ComputeJsonHash(empty with { Hash = "" }) };
        }

        var fingerprintSnapshot = MirFunctionFingerprintSnapshot.FromModule(module);
        var fingerprints = fingerprintSnapshot.Functions
            .GroupBy(static fingerprint => fingerprint.FunctionKey, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => new Queue<MirFunctionFingerprint>(group),
                StringComparer.Ordinal);
        var functions = module.Functions
            .Select(static (function, ordinal) => new { Function = function, Ordinal = ordinal })
            .OrderBy(static item => MirFunctionIdentity.GetStableKey(item.Function), StringComparer.Ordinal)
            .ThenBy(static item => item.Ordinal)
            .Select(item => MirGraphFunctionPayload.Create(item.Function, fingerprints))
            .ToArray();

        var payload = new MirGraphPayload(
            CurrentSchemaVersion,
            MirModulePayload.Create(module, fingerprintSnapshot.ModuleFingerprint),
            functions,
            "");

        return payload with { Hash = ModuleArtifactHash.ComputeJsonHash(payload with { Hash = "" }) };
    }
}

public sealed record MirModulePayload(
    string Name,
    string? PackageAlias,
    string? PackageInstanceKey,
    IReadOnlyList<string> Path,
    int FunctionCount,
    int BlockCount,
    int InstructionCount,
    int LocalCount,
    int TypeDescriptorCount,
    int TraitImplCount,
    int TraitInfoCount,
    int TypeAliasCount,
    int TypeConstructorCount,
    int SpecializationFailureCount,
    string ModuleFingerprint,
    string StructuralFingerprint)
{
    public static MirModulePayload Create(MirModule module, string moduleFingerprint) =>
        new(
            module.Name,
            module.PackageAlias,
            module.PackageInstanceKey,
            module.Path,
            module.Functions.Count,
            module.Functions.Sum(static function => function.BasicBlocks.Count),
            module.Functions.Sum(static function => function.BasicBlocks.Sum(static block => block.Instructions.Count)),
            module.Functions.Sum(static function => function.Locals.Count),
            module.TypeDescriptors.Count,
            module.TraitImpls.Count,
            module.TraitInfos.Count,
            module.TypeAliases.Count,
            module.TypeConstructors.Count,
            module.SpecializationFailures.Count,
            moduleFingerprint,
            CompilationPipeline.CreateMirModuleFingerprint(module));
}

public sealed record MirGraphFunctionPayload(
    string FunctionKey,
    string Name,
    string SourceName,
    int SymbolId,
    int ReturnTypeId,
    int EntryBlockId,
    int GenericParameterCount,
    bool IsExternal,
    bool IsEntry,
    string BodyHash,
    int BasicBlockCount,
    int InstructionCount,
    int LocalCount,
    int ParameterCount,
    IReadOnlyList<MirGraphBlockPayload> Blocks)
{
    public static MirGraphFunctionPayload Create(
        MirFunc function,
        IReadOnlyDictionary<string, Queue<MirFunctionFingerprint>> fingerprints)
    {
        var functionKey = MirFunctionIdentity.GetStableKey(function);
        var fingerprint = fingerprints[functionKey].Dequeue();
        var cfg = new ControlFlowGraph(function);
        return new MirGraphFunctionPayload(
            functionKey,
            function.Name,
            function.SourceName,
            function.SymbolId.Value,
            function.ReturnType.Value,
            function.EntryBlockId.Value,
            function.GenericParameterCount,
            function.IsExternal,
            function.IsEntry,
            fingerprint.BodyHash,
            fingerprint.BasicBlockCount,
            fingerprint.InstructionCount,
            fingerprint.LocalCount,
            fingerprint.ParameterCount,
            function.BasicBlocks
                .OrderBy(static block => block.Id.Value)
                .Select(block => MirGraphBlockPayload.Create(block, cfg))
                .ToArray());
    }
}

public sealed record MirGraphBlockPayload(
    int Id,
    bool IsEntry,
    int InstructionCount,
    string TerminatorKind,
    IReadOnlyList<int> Successors,
    IReadOnlyList<int> Predecessors,
    IReadOnlyList<MirGraphInstructionPayload> Instructions)
{
    public static MirGraphBlockPayload Create(MirBasicBlock block, ControlFlowGraph cfg) =>
        new(
            block.Id.Value,
            block.IsEntry,
            block.Instructions.Count,
            block.Terminator?.GetType().Name ?? "<none>",
            cfg.GetSuccessors(block.Id).Select(static id => id.Value).Order().ToArray(),
            cfg.GetPredecessors(block.Id).Select(static id => id.Value).Order().ToArray(),
            block.Instructions
                .Select(static (instruction, index) => new MirGraphInstructionPayload(index, instruction.GetType().Name))
                .ToArray());
}

public sealed record MirGraphInstructionPayload(int Index, string Kind);

public sealed record SourceSpanPayload(
    string? FilePath,
    int Position,
    int Length,
    int Line,
    int Column,
    int EndPosition)
{
    public static SourceSpanPayload Create(SourceSpan span) =>
        new(
            span.FilePath,
            span.Position,
            span.Length,
            span.Location.Line,
            span.Location.Column,
            span.EndPosition);
}

internal sealed class HirGraphPayloadBuilder
{
    private readonly Dictionary<HirNode, int> _nodeIds = new(ReferenceEqualityComparer.Instance);
    private readonly List<int> _ordinalPath = [];
    private int _nextNodeId;

    public List<HirGraphNodePayload> Nodes { get; } = [];

    public List<HirGraphEdgePayload> Edges { get; } = [];

    public int Visit(HirNode node, int? parentId, string role, int index)
    {
        if (!_nodeIds.TryGetValue(node, out var id))
        {
            id = _nextNodeId++;
            _nodeIds[node] = id;
            Nodes.Add(new HirGraphNodePayload(
                id,
                node.Kind.ToString(),
                node.GetType().Name,
                node is HirDecl declaration ? declaration.Name : "",
                node.SymbolId.Value,
                node.TypeId.Value,
                string.Join(".", _ordinalPath),
                SourceSpanPayload.Create(node.Span)));
        }

        if (parentId.HasValue)
        {
            Edges.Add(new HirGraphEdgePayload(parentId.Value, role, id, index));
        }

        foreach (var child in EnumerateChildren(node))
        {
            _ordinalPath.Add(child.Index);
            Visit(child.Node, id, child.Role, child.Index);
            _ordinalPath.RemoveAt(_ordinalPath.Count - 1);
        }

        return id;
    }

    private static IEnumerable<(string Role, int Index, HirNode Node)> EnumerateChildren(HirNode node)
    {
        foreach (var property in node.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .OrderBy(static property => property.Name, StringComparer.Ordinal))
        {
            if (property.GetIndexParameters().Length > 0 ||
                property.Name is nameof(HirNode.Span) or nameof(HirNode.Kind) or nameof(HirNode.TypeId) or nameof(HirNode.SymbolId))
            {
                continue;
            }

            var value = property.GetValue(node);
            switch (value)
            {
                case HirNode child:
                    yield return (property.Name, 0, child);
                    break;
                case HirStatement statement:
                {
                    var statementIndex = 0;
                    foreach (var statementChild in EnumerateStatementChildren(statement))
                    {
                        yield return ($"{property.Name}.{statement.GetType().Name}", statementIndex++, statementChild);
                    }
                    break;
                }
                case System.Collections.IEnumerable sequence when value is not string:
                {
                    var index = 0;
                    foreach (var item in sequence)
                    {
                        switch (item)
                        {
                            case HirNode sequenceChild:
                                yield return (property.Name, index++, sequenceChild);
                                break;
                            case HirStatement statement:
                                foreach (var statementChild in EnumerateStatementChildren(statement))
                                {
                                    yield return ($"{property.Name}.{statement.GetType().Name}", index++, statementChild);
                                }
                                break;
                        }
                    }

                    break;
                }
            }
        }
    }

    private static IEnumerable<HirNode> EnumerateStatementChildren(HirStatement statement)
    {
        foreach (var property in statement.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .OrderBy(static property => property.Name, StringComparer.Ordinal))
        {
            if (property.GetIndexParameters().Length > 0 ||
                property.Name == nameof(HirStatement.Span))
            {
                continue;
            }

            var value = property.GetValue(statement);
            switch (value)
            {
                case HirNode child:
                    yield return child;
                    break;
                case System.Collections.IEnumerable sequence when value is not string:
                    foreach (var item in sequence.OfType<HirNode>())
                    {
                        yield return item;
                    }
                    break;
            }
        }
    }
}
