namespace Eidosc.Pipeline;

using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Mir;
using Eidosc.Symbols;
using Eidosc.Types;

public sealed record ModuleTypesStateRestoreResult(
    bool Applied,
    int RestoredInferredTypes,
    int RestoredSymbolIds,
    int RestoredTypeEnvBindings,
    int RestoredSubstitutionBindings,
    int RestoredFunctionTypeParameterBindings,
    int RestoredComptimeValues,
    int RestoredConstraints,
    int MissingNodes,
    int StaleEntries,
    IReadOnlyList<string> Failures)
{
    public static ModuleTypesStateRestoreResult Failed(params string[] failures) =>
        new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, failures);
}

public static class ModuleTypesStateRestorer
{
    internal static TypesSymbolStateRestoreResult RestoreSymbolState(
        SymbolTable symbolTable,
        TypesSymbolStatePayload payload,
        LiveStateIdRemapper? remapper)
    {
        ArgumentNullException.ThrowIfNull(symbolTable);
        ArgumentNullException.ThrowIfNull(payload);

        if (!payload.HasValidHash())
        {
            return TypesSymbolStateRestoreResult.Failed("invalid Types symbol state payload");
        }

        var failures = new List<string>();
        var updates = new Dictionary<SymbolId, Symbol>();
        foreach (var entry in payload.Entries.OrderBy(static entry => entry.SymbolId))
        {
            var symbolId = new SymbolId(remapper?.RemapSymbol(entry.SymbolId) ?? entry.SymbolId);
            var current = symbolTable.GetSymbol(symbolId);
            if (current == null)
            {
                failures.Add($"missing current symbol for Types state: {entry.SymbolKind}:{entry.SymbolId}");
                continue;
            }

            if (!string.Equals(current.Kind.ToString(), entry.SymbolKind, StringComparison.Ordinal))
            {
                failures.Add($"Types symbol kind mismatch for {entry.SymbolId}: expected {entry.SymbolKind}, actual {current.Kind}");
                continue;
            }

            if (!TryCreateUpdatedSymbol(current, entry, remapper, out var updated, out var failure))
            {
                failures.Add(failure);
                continue;
            }

            if (!updates.TryAdd(symbolId, updated))
            {
                failures.Add($"duplicate mapped Types symbol state: {symbolId.Value}");
            }
        }

        if (failures.Count > 0)
        {
            return new TypesSymbolStateRestoreResult(false, 0, failures);
        }

        foreach (var update in updates.Values.OrderBy(static symbol => symbol.Id.Value))
        {
            symbolTable.UpdateSymbol(update);
        }

        return new TypesSymbolStateRestoreResult(true, updates.Count, []);
    }

    internal static ModuleTypesStateRestoreResult RestoreState(
        ModuleDecl ast,
        ModuleTypesStatePayload payload,
        out IReadOnlyDictionary<SymbolId, TypeScheme> typeEnv,
        out Substitution substitution,
        out IReadOnlyDictionary<SymbolId, IReadOnlyList<Eidosc.Types.Type>> functionTypeParameters,
        out IReadOnlyDictionary<SymbolId, ComptimeValue> comptimeValues,
        out IReadOnlyList<TypeConstraint> constraints) =>
        RestoreState(
            ast,
            payload,
            remapper: null,
            out typeEnv,
            out substitution,
            out functionTypeParameters,
            out comptimeValues,
            out constraints);

    internal static ModuleTypesStateRestoreResult RestoreState(
        ModuleDecl ast,
        ModuleTypesStatePayload payload,
        LiveStateIdRemapper? remapper,
        out IReadOnlyDictionary<SymbolId, TypeScheme> typeEnv,
        out Substitution substitution,
        out IReadOnlyDictionary<SymbolId, IReadOnlyList<Eidosc.Types.Type>> functionTypeParameters,
        out IReadOnlyDictionary<SymbolId, ComptimeValue> comptimeValues,
        out IReadOnlyList<TypeConstraint> constraints)
    {
        typeEnv = new Dictionary<SymbolId, TypeScheme>();
        substitution = new Substitution();
        functionTypeParameters = new Dictionary<SymbolId, IReadOnlyList<Eidosc.Types.Type>>();
        comptimeValues = new Dictionary<SymbolId, ComptimeValue>();
        constraints = [];

        if (!TryRestoreTypeEnv(payload, remapper, out typeEnv))
        {
            return ModuleTypesStateRestoreResult.Failed("failed to restore TypeEnv payload");
        }

        if (!payload.TypeSubstitution.TryRestoreSubstitution(remapper, out substitution))
        {
            return ModuleTypesStateRestoreResult.Failed("failed to restore type substitution payload");
        }

        if (!payload.FunctionTypeParameters.TryRestoreFunctionTypeParameters(remapper, out functionTypeParameters))
        {
            return ModuleTypesStateRestoreResult.Failed("failed to restore function type parameters payload");
        }

        if (!payload.ComptimeValues.TryRestoreComptimeValues(remapper, out comptimeValues))
        {
            return ModuleTypesStateRestoreResult.Failed("failed to restore comptime values payload");
        }

        if (!payload.Constraints.TryRestoreConstraints(remapper, out constraints))
        {
            return ModuleTypesStateRestoreResult.Failed("failed to restore type constraints payload");
        }

        var inferred = ApplyInferredTypes(ast, payload, remapper);
        return inferred with
        {
            Applied = inferred.Applied,
            RestoredTypeEnvBindings = typeEnv.Count,
            RestoredSubstitutionBindings = substitution.Count,
            RestoredFunctionTypeParameterBindings = functionTypeParameters.Count,
            RestoredComptimeValues = comptimeValues.Count,
            RestoredConstraints = constraints.Count
        };
    }

    public static ModuleTypesStateRestoreResult ApplyInferredTypes(
        ModuleDecl ast,
        ModuleTypesStatePayload payload) =>
        ApplyInferredTypes(ast, payload, remapper: null);

    internal static ModuleTypesStateRestoreResult ApplyInferredTypes(
        ModuleDecl ast,
        ModuleTypesStatePayload payload,
        LiveStateIdRemapper? remapper)
    {
        if (!payload.HasValidPayloadHash())
        {
            return ModuleTypesStateRestoreResult.Failed("invalid module Types payload hash");
        }

        if (payload.AstInferredTypes.SchemaVersion != AstInferredTypeMapPayload.CurrentSchemaVersion)
        {
            return ModuleTypesStateRestoreResult.Failed("unsupported AST inferred type map schema");
        }

        var failures = new List<string>();
        if (!payload.AstInferredTypes.HasValidHash())
        {
            return ModuleTypesStateRestoreResult.Failed("invalid AST inferred type map hash");
        }

        var stableNodes = string.IsNullOrWhiteSpace(payload.AstInferredTypes.ModuleKey)
            ? AstStableNodeTraversal.Enumerate(ast)
            : AstStableNodeTraversal.EnumerateModule(
                ast,
                payload.AstInferredTypes.ModuleKey,
                payload.AstInferredTypes.ModuleIdentityKey,
                payload.AstInferredTypes.SourcePaths);
        var currentStructureHash = AstInferredTypeMapPayload.ComputeStructureHash(stableNodes);
        if (stableNodes.Count != payload.AstInferredTypes.AstNodeCount ||
            !string.Equals(currentStructureHash, payload.AstInferredTypes.AstStructureHash, StringComparison.Ordinal))
        {
            return new ModuleTypesStateRestoreResult(
                false, 0, 0, 0, 0, 0, 0, 0,
                MissingNodes: Math.Abs(stableNodes.Count - payload.AstInferredTypes.AstNodeCount),
                StaleEntries: 0,
                Failures: ["AST structure does not match inferred type payload"]);
        }

        var duplicateKeys = stableNodes
            .GroupBy(static entry => entry.StableIdentity.StableKey, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToArray();
        if (duplicateKeys.Length > 0)
        {
            return new ModuleTypesStateRestoreResult(
                false, 0, 0, 0, 0, 0, 0, 0, 0, duplicateKeys.Length,
                duplicateKeys.Select(static key => $"duplicate AST inferred stable key: {key}").ToArray());
        }

        var nodesByStableKey = stableNodes.ToDictionary(
            static entry => entry.StableIdentity.StableKey,
            static entry => entry.Node,
            StringComparer.Ordinal);
        var pending = new List<PendingInferredTypeState>(payload.AstInferredTypes.Entries.Count);
        foreach (var entry in payload.AstInferredTypes.Entries
                     .OrderBy(static entry => entry.Ordinal))
        {
            if (!nodesByStableKey.TryGetValue(entry.StableKey, out var node))
            {
                failures.Add($"missing AST inferred type node: {entry.StableKey}");
                continue;
            }

            if (!string.Equals(entry.NodeKind, node.GetType().Name, StringComparison.Ordinal))
            {
                failures.Add($"AST inferred type node kind mismatch: {entry.StableKey}");
                continue;
            }

            var restoredType = remapper == null
                ? entry.ResolvedTypeShape.TryRestoreType(out var type)
                : entry.ResolvedTypeShape.TryRestoreType(remapper, out type);
            if (!restoredType)
            {
                failures.Add($"stale AST inferred type entry: {entry.StableKey}");
                continue;
            }

            EffectRow? inferredEffects = null;
            if ((entry.RawEffectsShape == null) != (entry.ResolvedEffectsShape == null))
            {
                failures.Add($"incomplete AST inferred effects entry: {entry.StableKey}");
                continue;
            }

            if (entry.ResolvedEffectsShape != null)
            {
                var restoredEffects = remapper == null
                    ? entry.ResolvedEffectsShape.TryRestoreType(out var effects)
                    : entry.ResolvedEffectsShape.TryRestoreType(remapper, out effects);
                if (!restoredEffects || effects is not EffectRow effectRow)
                {
                    failures.Add($"stale AST inferred effects entry: {entry.StableKey}");
                    continue;
                }

                inferredEffects = effectRow;
            }

            SymbolId? symbolId = null;
            if (entry.SymbolId != SymbolId.None.Value)
            {
                symbolId = new SymbolId(remapper?.RemapSymbol(entry.SymbolId) ?? entry.SymbolId);
            }

            pending.Add(new PendingInferredTypeState(node, type, inferredEffects, symbolId));
        }

        if (failures.Count > 0)
        {
            return new ModuleTypesStateRestoreResult(
                false, 0, 0, 0, 0, 0, 0, 0,
                failures.Count(static failure => failure.StartsWith("missing ", StringComparison.Ordinal)),
                failures.Count(static failure => !failure.StartsWith("missing ", StringComparison.Ordinal)),
                failures);
        }

        var astStateRestore = AstTypesStateRestorer.Restore(
            ast,
            payload.AstState,
            remapper,
            symbolTable: null);
        if (!astStateRestore.Applied)
        {
            return new ModuleTypesStateRestoreResult(
                false, 0, 0, 0, 0, 0, 0, 0, 0, astStateRestore.Failures.Count,
                astStateRestore.Failures);
        }

        foreach (var state in pending)
        {
            state.Node.InferredType = state.InferredType;
            state.Node.InferredEffects = state.InferredEffects;
            if (state.SymbolId.HasValue)
            {
                state.Node.SymbolId = state.SymbolId.Value;
            }
        }

        return new ModuleTypesStateRestoreResult(
            Applied: true,
            RestoredInferredTypes: pending.Count,
            RestoredSymbolIds: pending.Count(static state => state.SymbolId.HasValue),
            RestoredTypeEnvBindings: 0,
            RestoredSubstitutionBindings: 0,
            RestoredFunctionTypeParameterBindings: 0,
            RestoredComptimeValues: 0,
            RestoredConstraints: 0,
            MissingNodes: 0,
            StaleEntries: 0,
            Failures: []);
    }

    public static bool TryRestoreTypeEnv(
        ModuleTypesStatePayload payload,
        out IReadOnlyDictionary<SymbolId, TypeScheme> schemes) =>
        TryRestoreTypeEnv(payload, remapper: null, out schemes);

    internal static bool TryRestoreTypeEnv(
        ModuleTypesStatePayload payload,
        LiveStateIdRemapper? remapper,
        out IReadOnlyDictionary<SymbolId, TypeScheme> schemes)
    {
        schemes = new Dictionary<SymbolId, TypeScheme>();
        if (!payload.HasValidPayloadHash() ||
            payload.TypeEnv.SchemaVersion != TypeEnvPayload.CurrentSchemaVersion)
        {
            return false;
        }

        var restored = new Dictionary<SymbolId, TypeScheme>();
        foreach (var binding in payload.TypeEnv.Bindings.OrderBy(static binding => binding.SymbolId))
        {
            if (!binding.Scheme.TryRestoreTypeScheme(remapper, out var scheme))
            {
                return false;
            }

            restored[new SymbolId(remapper?.RemapSymbol(binding.SymbolId) ?? binding.SymbolId)] = scheme;
        }

        schemes = restored;
        return true;
    }

    private static bool TryCreateUpdatedSymbol(
        Symbol current,
        TypesSymbolStateEntryPayload entry,
        LiveStateIdRemapper? remapper,
        out Symbol updated,
        out string failure)
    {
        var typeId = RemapTypeId(entry.TypeId, remapper);
        switch (current)
        {
            case FuncSymbol function:
                updated = function with
                {
                    IsTypeResolved = entry.IsTypeResolved,
                    TypeId = typeId,
                    ParamTypes = entry.FunctionParameterTypeIds
                        .Select(value => RemapTypeId(value, remapper))
                        .ToList(),
                    ReturnType = RemapTypeId(entry.FunctionReturnTypeId, remapper),
                    CStructFieldTypeId = RemapTypeId(entry.FunctionCStructFieldTypeId, remapper)
                };
                failure = "";
                return true;

            case VarSymbol variable:
                TypeScheme? scheme = null;
                if (entry.VariableScheme != null &&
                    !entry.VariableScheme.TryRestoreTypeScheme(remapper, out scheme))
                {
                    updated = current;
                    failure = $"failed to restore variable type scheme for symbol {entry.SymbolId}";
                    return false;
                }

                updated = variable with
                {
                    IsTypeResolved = entry.IsTypeResolved,
                    TypeId = typeId,
                    Type = RemapTypeId(entry.VariableTypeId, remapper),
                    Scheme = scheme
                };
                failure = "";
                return true;

            case CtorSymbol constructor:
                updated = constructor with
                {
                    IsTypeResolved = entry.IsTypeResolved,
                    TypeId = typeId,
                    PositionalArgs = entry.ConstructorPositionalArgumentTypeIds
                        .Select(value => RemapTypeId(value, remapper))
                        .ToList(),
                    SignatureText = entry.ConstructorSignatureText
                };
                failure = "";
                return true;

            case FieldSymbol field:
                updated = field with
                {
                    IsTypeResolved = entry.IsTypeResolved,
                    TypeId = typeId,
                    FieldType = RemapTypeId(entry.FieldTypeId, remapper)
                };
                failure = "";
                return true;

            case TypeParamSymbol typeParameter:
                updated = typeParameter with
                {
                    IsTypeResolved = entry.IsTypeResolved,
                    TypeId = typeId,
                    KindAnnotation = entry.TypeParameterKindAnnotation ?? typeParameter.KindAnnotation
                };
                failure = "";
                return true;

            default:
                updated = current with
                {
                    IsTypeResolved = entry.IsTypeResolved,
                    TypeId = typeId
                };
                failure = "";
                return true;
        }
    }

    private static TypeId RemapTypeId(int value, LiveStateIdRemapper? remapper) =>
        new(remapper?.RemapType(value) ?? value);

    private sealed record PendingInferredTypeState(
        EidosAstNode Node,
        Eidosc.Types.Type InferredType,
        EffectRow? InferredEffects,
        SymbolId? SymbolId);

}

public sealed record TypesSymbolStateRestoreResult(
    bool Applied,
    int RestoredSymbols,
    IReadOnlyList<string> Failures)
{
    public static TypesSymbolStateRestoreResult Failed(params string[] failures) =>
        new(false, 0, failures);
}
