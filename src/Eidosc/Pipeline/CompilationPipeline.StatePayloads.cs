namespace Eidosc.Pipeline;

public sealed partial class CompilationPipeline
{
    private bool ShouldCreateModuleStatePayloads() =>
        _options.EnableIncrementalCompilation ||
        _options.PreviousModuleNamerStatePayloads != null ||
        _options.PreviousModuleTypesStatePayloads != null ||
        _options.PreviousModuleHirStatePayloads != null ||
        _options.PreviousModuleMirStatePayloads != null ||
        _options.ModuleNamerStatePayloadLoader != null ||
        _options.ModuleTypesStatePayloadLoader != null ||
        _options.ModuleHirStatePayloadLoader != null ||
        _options.ModuleMirStatePayloadLoader != null;

    private IReadOnlyList<ModuleNamerStatePayload>? CreateModuleNamerStatePayloads()
    {
        if (_symbolTable == null ||
            _moduleMemberIndexSnapshot == null ||
            _moduleGraphSnapshot == null)
        {
            return null;
        }

        var allStableNodes = _ast == null ? null : AstStableNodeTraversal.Enumerate(_ast);
        var allSymbolIdentities = LiveStateStableIdentityBuilder.BuildSymbolIdentities(
            _symbolTable,
            _moduleGraphSnapshot);
        var fullSymbolTablePayload = SymbolTablePayload.Create(_symbolTable);
        var fullModuleRegistryPayload = ModuleRegistryPayload.Create(_symbolTable.Modules);
        return _moduleGraphSnapshot.Nodes
            .OrderBy(static node => node.ModuleKey, StringComparer.Ordinal)
            .Select(node => ModuleNamerStatePayload.Create(
                node.ModuleKey,
                _symbolTable,
                _moduleMemberIndexSnapshot,
                _moduleGraphSnapshot,
                _ast,
                allStableNodes,
                allSymbolIdentities,
                fullSymbolTablePayload,
                fullModuleRegistryPayload))
            .ToArray();
    }

    private IReadOnlyList<ModuleTypesStatePayload>? CreateModuleTypesStatePayloads()
    {
        if (_moduleTypedSemanticSnapshot == null ||
            _typesEntrySymbolIdentities == null ||
            _typesEntrySymbolTable == null)
        {
            return null;
        }

        var allStableNodes = _ast == null ? null : AstStableNodeTraversal.Enumerate(_ast);
        return _moduleTypedSemanticSnapshot.Nodes
            .OrderBy(static node => node.ModuleKey, StringComparer.Ordinal)
            .Select(node => ModuleTypesStatePayload.Create(
                node.ModuleKey,
                _moduleTypedSemanticSnapshot,
                _typesEntrySymbolIdentities,
                _typesEntrySymbolTable,
                _ast,
                _typeInferer,
                _abilityInferer,
                _symbolTable,
                _moduleGraphSnapshot?.Nodes.FirstOrDefault(graphNode =>
                    string.Equals(graphNode.ModuleKey, node.ModuleKey, StringComparison.Ordinal))?.SourcePaths,
                allStableNodes))
            .ToArray();
    }

    private void CaptureTypesEntrySymbolState()
    {
        _typesEntrySymbolIdentities = LiveStateStableIdentityBuilder.BuildSymbolIdentities(
            _symbolTable!,
            _moduleGraphSnapshot);
        _typesEntrySymbolTable = SymbolTablePayload.Create(_symbolTable);
    }

    private IReadOnlyList<ModuleHirStateArtifactPayload>? CreateModuleHirStatePayloads()
    {
        if (_moduleTypedSemanticSnapshot == null ||
            _hirModule == null)
        {
            return null;
        }

        return _moduleTypedSemanticSnapshot.Nodes
            .OrderBy(static node => node.ModuleKey, StringComparer.Ordinal)
            .Select(node => ModuleHirStateArtifactPayload.Create(
                node.ModuleKey,
                _moduleTypedSemanticSnapshot,
                _hirModule,
                _hirParameterEffects,
                _hirCopyLikeTypeIds,
                _hirDynamicTypeKeys,
                _hirTypeDescriptors,
                _hirConstructorLayouts))
            .ToArray();
    }

    private IReadOnlyList<ModuleMirStateArtifactPayload>? CreateModuleMirStatePayloads()
    {
        if (_moduleTypedSemanticSnapshot == null ||
            _mirModule == null)
        {
            return null;
        }

        return _moduleTypedSemanticSnapshot.Nodes
            .OrderBy(static node => node.ModuleKey, StringComparer.Ordinal)
            .Select(node => ModuleMirStateArtifactPayload.Create(
                node.ModuleKey,
                _moduleTypedSemanticSnapshot,
                _mirModule))
            .ToArray();
    }
}
