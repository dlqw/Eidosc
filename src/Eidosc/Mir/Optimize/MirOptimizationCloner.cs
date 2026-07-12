using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

internal static class MirOptimizationCloner
{
    public static MirModule WithFunctions(MirModule module, List<MirFunc> functions) => new()
    {
        Name = module.Name,
        PackageAlias = module.PackageAlias,
        PackageInstanceKey = module.PackageInstanceKey,
        Path = module.Path.ToList(),
        Functions = functions,
        DynamicTypeKeys = new Dictionary<int, string>(module.DynamicTypeKeys),
        TypeDescriptors = new Dictionary<int, TypeDescriptor>(module.TypeDescriptors),
        LinkLibraries = module.LinkLibraries.ToList(),
        CStructAccessors = new Dictionary<string, CStructAccessorInfo>(module.CStructAccessors),
        ConstructorLayouts = module.ConstructorLayouts.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToList()),
        TraitImpls = module.TraitImpls.ToList(),
        TraitInfos = module.TraitInfos.ToList(),
        TypeAliases = module.TypeAliases.ToList(),
        TypeConstructors = module.TypeConstructors.ToList(),
        SpecializationFailures = module.SpecializationFailures.ToList(),
        Span = module.Span
    };

    public static MirFunc WithBlocks(MirFunc function, List<MirBasicBlock> blocks) => new()
    {
        Name = function.Name,
        SourceName = function.SourceName,
        Locals = function.Locals,
        BasicBlocks = blocks,
        EntryBlockId = function.EntryBlockId,
        ReturnType = function.ReturnType,
        GenericParameterCount = function.GenericParameterCount,
        GenericTypeParameterIds = function.GenericTypeParameterIds.ToList(),
        Span = function.Span,
        SymbolId = function.SymbolId,
        FunctionId = function.FunctionId,
        TraitInvokeHelper = function.TraitInvokeHelper,
        TraitInvokeHelperTraitId = function.TraitInvokeHelperTraitId,
        IsRuntimeWordAbi = function.IsRuntimeWordAbi,
        IsEntry = function.IsEntry,
        IsExternal = function.IsExternal,
        ExternalSymbolName = function.ExternalSymbolName,
        ExternalLibrary = function.ExternalLibrary,
        IntrinsicName = function.IntrinsicName,
        BuiltinIntrinsicRole = function.BuiltinIntrinsicRole
    };

    public static MirBasicBlock WithInstructions(
        MirBasicBlock block,
        List<MirInstruction> instructions) => new()
    {
        Id = block.Id,
        Instructions = instructions,
        Terminator = block.Terminator,
        Span = block.Span,
        IsEntry = block.IsEntry
    };
}
