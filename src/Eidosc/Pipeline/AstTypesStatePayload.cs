namespace Eidosc.Pipeline;

using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Symbols;
using Eidosc.Types;

public sealed record AstTypesStatePayload(
    string SchemaVersion,
    string ModuleKey,
    string ModuleIdentityKey,
    IReadOnlyList<string> SourcePaths,
    int AstNodeCount,
    string AstStructureHash,
    int UnsupportedStructuralRewrites,
    int UnresolvedNodeReferences,
    IReadOnlyList<AstTypesStateEntryPayload> Entries,
    string Hash)
{
    public const string CurrentSchemaVersion = "ast-types-state-payload-v6";

    public static AstTypesStatePayload Create(
        ModuleDecl? ast,
        string moduleKey = "",
        string moduleIdentityKey = "",
        IReadOnlyList<string>? sourcePaths = null) =>
        Create(ast, moduleKey, moduleIdentityKey, sourcePaths, allStableNodes: null);

    internal static AstTypesStatePayload Create(
        ModuleDecl? ast,
        string moduleKey,
        string moduleIdentityKey,
        IReadOnlyList<string>? sourcePaths,
        IReadOnlyList<AstStableNodeEntry>? allStableNodes)
    {
        if (ast == null)
        {
            return CreatePayload(
                moduleKey,
                moduleIdentityKey,
                sourcePaths,
                0,
                AstInferredTypeMapPayload.ComputeStructureHash([]),
                0,
                0,
                []);
        }

        allStableNodes ??= AstStableNodeTraversal.Enumerate(ast);
        var stableNodes = string.IsNullOrWhiteSpace(moduleKey)
            ? allStableNodes
            : AstStableNodeTraversal.EnumerateModule(allStableNodes, moduleKey, moduleIdentityKey, sourcePaths);
        var stableKeyByNode = new Dictionary<EidosAstNode, string>(ReferenceEqualityComparer.Instance);
        foreach (var stableNode in allStableNodes)
        {
            stableKeyByNode[stableNode.Node] = stableNode.StableIdentity.StableKey;
        }
        var entries = new List<AstTypesStateEntryPayload>();
        var unresolvedReferences = 0;
        foreach (var stableNode in stableNodes)
        {
            if (!AstTypesStateEntryPayload.TryCreate(
                    stableNode.Node,
                    stableNode.StableIdentity,
                    stableKeyByNode,
                    out var entry,
                    out var unresolvedReference))
            {
                unresolvedReferences += unresolvedReference ? 1 : 0;
                continue;
            }

            entries.Add(entry);
        }

        var unsupportedStructuralRewrites = stableNodes.Count(static entry =>
            entry.Node is ContextualRecordLiteralExpr { DesugaredCtor: not null } or
                RecordUpdateExpr { DesugaredCtor: not null } or
                RecordUpdateExpr { DesugaredMatch: not null });
        return CreatePayload(
            moduleKey,
            moduleIdentityKey,
            sourcePaths,
            stableNodes.Count,
            AstInferredTypeMapPayload.ComputeStructureHash(stableNodes),
            unsupportedStructuralRewrites,
            unresolvedReferences,
            entries);
    }

    public bool HasValidHash() =>
        SchemaVersion == CurrentSchemaVersion &&
        !string.IsNullOrWhiteSpace(Hash) &&
        string.Equals(Hash, ComputeHash(this), StringComparison.Ordinal);

    private static AstTypesStatePayload CreatePayload(
        string moduleKey,
        string moduleIdentityKey,
        IReadOnlyList<string>? sourcePaths,
        int astNodeCount,
        string astStructureHash,
        int unsupportedStructuralRewrites,
        int unresolvedNodeReferences,
        IReadOnlyList<AstTypesStateEntryPayload> entries)
    {
        var payload = new AstTypesStatePayload(
            CurrentSchemaVersion,
            moduleKey,
            moduleIdentityKey,
            sourcePaths?.ToArray() ?? [],
            astNodeCount,
            astStructureHash,
            unsupportedStructuralRewrites,
            unresolvedNodeReferences,
            entries,
            "");
        return payload with { Hash = ComputeHash(payload) };
    }

    private static string ComputeHash(AstTypesStatePayload payload) =>
        ModuleArtifactHash.ComputeJsonHash(payload with { Hash = "" });
}

public sealed record AstTypesStateEntryPayload(
    AstInferredTypeStableKeyPayload StableIdentity,
    int? ResolvedSymbolId,
    bool? PathIsTypePath,
    bool? PathIsConstructorPath,
    bool? MethodResolvedAsStaticPath,
    int? SynthesizedUnitArgumentCount,
    bool? UsesFfiUnitArgumentElision,
    bool? ResolvedAsFieldAccess,
    int? FieldSymbolId,
    string? CStructGetterName,
    int? CStructGetterSymbolId,
    int? InfixFunctionSymbolId,
    string? LetQuestionBindingKind,
    int? SuccessConstructorSymbolId,
    int? FailureConstructorSymbolId,
    int? FailureBindingSymbolId,
    TypeShapePayload? SuccessPayloadType,
    TypeShapePayload? FailurePayloadType,
    TypeShapePayload? ShortCircuitReturnType,
    string? MethodAssociatedConstImplementationStableKey,
    string? AssociatedConstImplementationStableKey)
{
    public static bool TryCreate(
        EidosAstNode node,
        AstInferredTypeStableKeyPayload stableIdentity,
        IReadOnlyDictionary<EidosAstNode, string> stableKeyByNode,
        out AstTypesStateEntryPayload entry,
        out bool unresolvedReference)
    {
        unresolvedReference = false;
        string? implementationStableKey = null;
        string? methodAssociatedConstImplementationStableKey = null;
        if (node is MethodCallExpr
            {
                ResolvedStaticExpression: AssociatedConstExpr
                {
                    ImplementationValue: { } methodImplementation
                }
            } &&
            !stableKeyByNode.TryGetValue(methodImplementation, out methodAssociatedConstImplementationStableKey))
        {
            entry = null!;
            unresolvedReference = true;
            return false;
        }

        if (node is AssociatedConstExpr { ImplementationValue: { } implementation } &&
            !stableKeyByNode.TryGetValue(implementation, out implementationStableKey))
        {
            entry = null!;
            unresolvedReference = true;
            return false;
        }

        if (node is not (CallExpr or MethodCallExpr or InfixCallExpr or LetQuestionDecl or AssociatedConstExpr) &&
            node is not IdentifierExpr { SymbolId.IsValid: true } &&
            node is not PathExpr { SymbolId.IsValid: true })
        {
            entry = null!;
            return false;
        }

        entry = new AstTypesStateEntryPayload(
            stableIdentity,
            node is IdentifierExpr or PathExpr or MethodCallExpr ? node.SymbolId.Value : null,
            node is PathExpr typePath ? typePath.IsTypePath : null,
            node is PathExpr constructorPath ? constructorPath.IsConstructorPath : null,
            node is MethodCallExpr staticMethod ? staticMethod.ResolvedAsStaticPath : null,
            node switch
            {
                CallExpr call => call.SynthesizedUnitArgumentCount,
                MethodCallExpr method => method.SynthesizedUnitArgumentCount,
                _ => null
            },
            node switch
            {
                CallExpr call => call.UsesFfiUnitArgumentElision,
                MethodCallExpr method => method.UsesFfiUnitArgumentElision,
                _ => null
            },
            node is MethodCallExpr methodField ? methodField.ResolvedAsFieldAccess : null,
            node is MethodCallExpr methodFieldSymbol ? methodFieldSymbol.FieldSymbolId.Value : null,
            node is MethodCallExpr methodCStruct ? methodCStruct.CStructGetterName : null,
            node is MethodCallExpr methodCStructSymbol ? methodCStructSymbol.CStructGetterSymbolId.Value : null,
            node is InfixCallExpr infix ? infix.FunctionSymbolId.Value : null,
            node is LetQuestionDecl letQuestion ? letQuestion.BindingKind.ToString() : null,
            node is LetQuestionDecl letSuccess ? letSuccess.SuccessConstructorSymbolId.Value : null,
            node is LetQuestionDecl letFailure ? letFailure.FailureConstructorSymbolId.Value : null,
            node is LetQuestionDecl letBinding ? letBinding.FailureBindingSymbolId.Value : null,
            node is LetQuestionDecl { SuccessPayloadType: Eidosc.Types.Type successType }
                ? TypeShapePayload.Create(successType)
                : null,
            node is LetQuestionDecl { FailurePayloadType: Eidosc.Types.Type failureType }
                ? TypeShapePayload.Create(failureType)
                : null,
            node is LetQuestionDecl { ShortCircuitReturnType: Eidosc.Types.Type returnType }
                ? TypeShapePayload.Create(returnType)
                : null,
            methodAssociatedConstImplementationStableKey,
            implementationStableKey);
        return true;
    }
}

public sealed record AstTypesStateRestoreResult(
    bool Applied,
    int RestoredNodes,
    IReadOnlyList<string> Failures)
{
    public static AstTypesStateRestoreResult Failed(params string[] failures) =>
        new(false, 0, failures);
}

public static class AstTypesStateRestorer
{
    internal static AstTypesStateRestoreResult Restore(
        ModuleDecl ast,
        AstTypesStatePayload payload,
        LiveStateIdRemapper? remapper,
        SymbolTable? symbolTable)
    {
        ArgumentNullException.ThrowIfNull(ast);
        ArgumentNullException.ThrowIfNull(payload);

        if (!payload.HasValidHash() ||
            payload.UnsupportedStructuralRewrites != 0 ||
            payload.UnresolvedNodeReferences != 0)
        {
            return AstTypesStateRestoreResult.Failed("Types AST state payload is not safely restorable");
        }

        var stableNodes = string.IsNullOrWhiteSpace(payload.ModuleKey)
            ? AstStableNodeTraversal.Enumerate(ast)
            : AstStableNodeTraversal.EnumerateModule(
                ast,
                payload.ModuleKey,
                payload.ModuleIdentityKey,
                payload.SourcePaths);
        if (stableNodes.Count != payload.AstNodeCount ||
            !string.Equals(
                AstInferredTypeMapPayload.ComputeStructureHash(stableNodes),
                payload.AstStructureHash,
                StringComparison.Ordinal))
        {
            return AstTypesStateRestoreResult.Failed("Types AST structure does not match payload");
        }

        var nodesByKey = stableNodes.ToDictionary(
            static entry => entry.StableIdentity.StableKey,
            static entry => entry.Node,
            StringComparer.Ordinal);
        var failures = new List<string>();
        var pending = new List<PendingAstTypesState>(payload.Entries.Count);
        foreach (var entry in payload.Entries)
        {
            if (!nodesByKey.TryGetValue(entry.StableIdentity.StableKey, out var node))
            {
                failures.Add($"missing Types AST state node: {entry.StableIdentity.StableKey}");
                continue;
            }

            if (!TryPrepare(entry, node, nodesByKey, remapper, symbolTable, out var state, out var failure))
            {
                failures.Add(failure);
                continue;
            }

            pending.Add(state);
        }

        if (failures.Count > 0)
        {
            return new AstTypesStateRestoreResult(false, 0, failures);
        }

        foreach (var state in pending)
        {
            Apply(state);
        }

        return new AstTypesStateRestoreResult(true, pending.Count, []);
    }

    private static bool TryPrepare(
        AstTypesStateEntryPayload entry,
        EidosAstNode node,
        IReadOnlyDictionary<string, EidosAstNode> nodesByKey,
        LiveStateIdRemapper? remapper,
        SymbolTable? symbolTable,
        out PendingAstTypesState state,
        out string failure)
    {
        state = null!;
        failure = "";
        if (!TryMapOptionalSymbol(entry.ResolvedSymbolId, remapper, symbolTable, out var resolvedSymbolId) ||
            !TryMapOptionalSymbol(entry.FieldSymbolId, remapper, symbolTable, out var fieldSymbolId) ||
            !TryMapOptionalSymbol(entry.CStructGetterSymbolId, remapper, symbolTable, out var cStructGetterSymbolId) ||
            !TryMapOptionalSymbol(entry.InfixFunctionSymbolId, remapper, symbolTable, out var infixFunctionSymbolId) ||
            !TryMapOptionalSymbol(entry.SuccessConstructorSymbolId, remapper, symbolTable, out var successConstructorSymbolId) ||
            !TryMapOptionalSymbol(entry.FailureConstructorSymbolId, remapper, symbolTable, out var failureConstructorSymbolId) ||
            !TryMapOptionalSymbol(entry.FailureBindingSymbolId, remapper, symbolTable, out var failureBindingSymbolId) ||
            !TryRestoreOptionalType(entry.SuccessPayloadType, remapper, out var successPayloadType) ||
            !TryRestoreOptionalType(entry.FailurePayloadType, remapper, out var failurePayloadType) ||
            !TryRestoreOptionalType(entry.ShortCircuitReturnType, remapper, out var shortCircuitReturnType))
        {
            failure = $"failed to remap Types AST state node: {entry.StableIdentity.StableKey}";
            return false;
        }

        EidosAstNode? implementationValue = null;
        if (entry.AssociatedConstImplementationStableKey != null &&
            !nodesByKey.TryGetValue(entry.AssociatedConstImplementationStableKey, out implementationValue))
        {
            failure = $"missing associated const implementation node: {entry.AssociatedConstImplementationStableKey}";
            return false;
        }

        EidosAstNode? methodAssociatedConstImplementationValue = null;
        if (entry.MethodAssociatedConstImplementationStableKey != null)
        {
            if (node is not MethodCallExpr
                {
                    ResolvedStaticExpression: AssociatedConstExpr
                })
            {
                failure = $"missing resolved associated const projection: {entry.StableIdentity.StableKey}";
                return false;
            }

            if (!nodesByKey.TryGetValue(
                    entry.MethodAssociatedConstImplementationStableKey,
                    out methodAssociatedConstImplementationValue))
            {
                failure = $"missing method associated const implementation node: {entry.MethodAssociatedConstImplementationStableKey}";
                return false;
            }
        }

        LetQuestionBindingKind? bindingKind = null;
        if (entry.LetQuestionBindingKind != null)
        {
            if (!Enum.TryParse<LetQuestionBindingKind>(entry.LetQuestionBindingKind, out var parsedKind))
            {
                failure = $"invalid let-question binding kind: {entry.LetQuestionBindingKind}";
                return false;
            }

            bindingKind = parsedKind;
        }

        state = new PendingAstTypesState(
            node,
            resolvedSymbolId,
            entry.PathIsTypePath,
            entry.PathIsConstructorPath,
            entry.MethodResolvedAsStaticPath,
            entry.SynthesizedUnitArgumentCount,
            entry.UsesFfiUnitArgumentElision,
            entry.ResolvedAsFieldAccess,
            fieldSymbolId,
            entry.CStructGetterName,
            cStructGetterSymbolId,
            infixFunctionSymbolId,
            bindingKind,
            successConstructorSymbolId,
            failureConstructorSymbolId,
            failureBindingSymbolId,
            successPayloadType,
            failurePayloadType,
            shortCircuitReturnType,
            methodAssociatedConstImplementationValue,
            implementationValue);
        return true;
    }

    private static bool TryMapOptionalSymbol(
        int? value,
        LiveStateIdRemapper? remapper,
        SymbolTable? symbolTable,
        out SymbolId? mapped)
    {
        mapped = null;
        if (!value.HasValue)
        {
            return true;
        }

        var symbolId = new SymbolId(remapper?.RemapSymbol(value.Value) ?? value.Value);
        if (symbolId.IsValid && symbolTable != null && symbolTable.GetSymbol(symbolId) == null)
        {
            return false;
        }

        mapped = symbolId;
        return true;
    }

    private static bool TryRestoreOptionalType(
        TypeShapePayload? payload,
        LiveStateIdRemapper? remapper,
        out Eidosc.Types.Type? type)
    {
        type = null;
        if (payload == null)
        {
            return true;
        }

        return remapper == null
            ? payload.TryRestoreType(out type)
            : payload.TryRestoreType(remapper, out type);
    }

    private static void Apply(PendingAstTypesState state)
    {
        if (state.ResolvedSymbolId.HasValue)
        {
            state.Node.SymbolId = state.ResolvedSymbolId.Value;
        }

        if (state.Node is PathExpr path)
        {
            path.SetIsTypePath(state.PathIsTypePath == true);
            path.SetIsConstructorPath(state.PathIsConstructorPath == true);
        }

        if (state.Node is CallExpr call)
        {
            ApplyEmptyCallState(call, state.SynthesizedUnitArgumentCount, state.UsesFfiUnitArgumentElision);
        }
        else if (state.Node is MethodCallExpr method)
        {
            ApplyEmptyCallState(method, state.SynthesizedUnitArgumentCount, state.UsesFfiUnitArgumentElision);
            if (state.MethodResolvedAsStaticPath == true)
            {
                method.MarkResolvedAsStaticPath();
            }

            if (state.ResolvedAsFieldAccess == true && state.FieldSymbolId.HasValue)
            {
                method.MarkResolvedAsFieldAccess(state.FieldSymbolId.Value);
            }

            if (state.CStructGetterName != null && state.CStructGetterSymbolId.HasValue)
            {
                method.MarkResolvedAsCStructAccess(state.CStructGetterName, state.CStructGetterSymbolId.Value);
            }

            if (state.MethodAssociatedConstImplementationValue != null &&
                method.ResolvedStaticExpression is AssociatedConstExpr associatedConst)
            {
                associatedConst.SetImplementationValue(state.MethodAssociatedConstImplementationValue);
            }
        }
        else if (state.Node is InfixCallExpr infix && state.InfixFunctionSymbolId.HasValue)
        {
            infix.FunctionSymbolId = state.InfixFunctionSymbolId.Value;
        }
        else if (state.Node is LetQuestionDecl letQuestion &&
                 state.LetQuestionBindingKind.HasValue &&
                 state.SuccessConstructorSymbolId.HasValue &&
                 state.FailureConstructorSymbolId.HasValue)
        {
            if (state.FailureBindingSymbolId.HasValue)
            {
                letQuestion.SetFailureBindingSymbol(state.FailureBindingSymbolId.Value);
            }

            letQuestion.SetDesugaring(
                state.LetQuestionBindingKind.Value,
                state.SuccessConstructorSymbolId.Value,
                state.FailureConstructorSymbolId.Value,
                state.SuccessPayloadType,
                state.FailurePayloadType,
                state.ShortCircuitReturnType);
        }
        else if (state.Node is AssociatedConstExpr associatedConst)
        {
            associatedConst.SetImplementationValue(state.AssociatedConstImplementationValue);
        }
    }

    private static void ApplyEmptyCallState(CallExpr call, int? synthesizedCount, bool? ffiElision)
    {
        if (synthesizedCount > 0)
        {
            call.MarkSyntheticUnitArguments(synthesizedCount.Value);
        }
        else if (ffiElision == true)
        {
            call.MarkFfiUnitArgumentElision();
        }
        else
        {
            call.ClearEmptyCallResolution();
        }
    }

    private static void ApplyEmptyCallState(MethodCallExpr method, int? synthesizedCount, bool? ffiElision)
    {
        if (synthesizedCount > 0)
        {
            method.MarkSyntheticUnitArguments(synthesizedCount.Value);
        }
        else if (ffiElision == true)
        {
            method.MarkFfiUnitArgumentElision();
        }
        else
        {
            method.ClearEmptyCallResolution();
        }
    }

    private sealed record PendingAstTypesState(
        EidosAstNode Node,
        SymbolId? ResolvedSymbolId,
        bool? PathIsTypePath,
        bool? PathIsConstructorPath,
        bool? MethodResolvedAsStaticPath,
        int? SynthesizedUnitArgumentCount,
        bool? UsesFfiUnitArgumentElision,
        bool? ResolvedAsFieldAccess,
        SymbolId? FieldSymbolId,
        string? CStructGetterName,
        SymbolId? CStructGetterSymbolId,
        SymbolId? InfixFunctionSymbolId,
        LetQuestionBindingKind? LetQuestionBindingKind,
        SymbolId? SuccessConstructorSymbolId,
        SymbolId? FailureConstructorSymbolId,
        SymbolId? FailureBindingSymbolId,
        Eidosc.Types.Type? SuccessPayloadType,
        Eidosc.Types.Type? FailurePayloadType,
        Eidosc.Types.Type? ShortCircuitReturnType,
        EidosAstNode? MethodAssociatedConstImplementationValue,
        EidosAstNode? AssociatedConstImplementationValue);
}
