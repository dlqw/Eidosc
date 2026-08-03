namespace Eidosc.Mir.Optimize;

internal static class MirFunctionTransform
{
    public static MirFunc CloneWithBody(
        MirFunc source,
        List<MirLocal> locals,
        List<MirBasicBlock> basicBlocks,
        BlockId? entryBlockId = null)
    {
        var clone = new MirFunc
        {
            Name = source.Name,
            SourceName = source.SourceName,
            Locals = locals,
            BasicBlocks = basicBlocks,
            EntryBlockId = entryBlockId ?? source.EntryBlockId,
            ReturnType = source.ReturnType,
            GenericParameterCount = source.GenericParameterCount,
            GenericParameters = source.GenericParameters.ToList(),
            GenericTypeParameterIds = source.GenericTypeParameterIds.ToList(),
            IsRuntimeWordAbi = source.IsRuntimeWordAbi,
            IsEntry = source.IsEntry,
            Span = source.Span,
            SymbolId = source.SymbolId,
            FunctionId = source.FunctionId,
            TraitInvokeHelper = source.TraitInvokeHelper,
            TraitInvokeHelperTraitId = source.TraitInvokeHelperTraitId,
            IsExternal = source.IsExternal,
            ExternalSymbolName = source.ExternalSymbolName,
            ExternalLibrary = source.ExternalLibrary,
            IntrinsicName = source.IntrinsicName,
            BuiltinIntrinsicRole = source.BuiltinIntrinsicRole,
            CallerOwnedAggregateAbi = source.CallerOwnedAggregateAbi
        };
        clone.OwnershipContract = source.OwnershipContract;
        return clone;
    }
}
