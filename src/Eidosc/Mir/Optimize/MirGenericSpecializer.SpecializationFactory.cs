namespace Eidosc.Mir.Optimize;

public sealed partial class MirGenericSpecializer
{
    private MirFunc CreateSpecializedFunction(
        MirFunc template,
        SpecializationSignature signature,
        SpecializationBindings typeBindings)
    {
        var specializationName = CreateSpecializationName(template.Name, signature.ToKeyString());
        var specializationSymbol = new SymbolId(_nextSyntheticSymbolId++);
        var specializationFunctionId = template.IntrinsicName != null
            ? MirBuiltinFunctions.CreateIntrinsicFunctionId(specializationSymbol, template.IntrinsicName)
            : new FunctionId
            {
                SymbolId = specializationSymbol,
                Kind = template.FunctionId.Kind,
                Module = template.FunctionId.Module,
                ModuleIdentityKey = template.FunctionId.ModuleIdentityKey,
                StableIdentityKey = string.IsNullOrWhiteSpace(template.FunctionId.StableIdentityKey)
                    ? ""
                    : $"{template.FunctionId.StableIdentityKey}\0specialization\0{signature.ToKeyString()}",
                Name = specializationName,
                QualifiedName = string.IsNullOrWhiteSpace(template.FunctionId.Module)
                    ? specializationName
                    : $"{template.FunctionId.Module}{WellKnownStrings.Separators.Path}{specializationName}"
        };
        var substitutionService = CreateSpecializationTypeSubstitutionService();
        var resolvingTypeIds = new HashSet<int>();

        var parameterTypes = signature.ParameterTypes;
        var parameterIndex = 0;
        var specializedLocals = new List<MirLocal>(template.Locals.Count);
        foreach (var local in template.Locals)
        {
            var localType = substitutionService.SubstituteTypeId(local.TypeId, typeBindings, resolvingTypeIds);
            if (local.IsParameter && parameterIndex < parameterTypes.Count)
            {
                localType = parameterTypes[parameterIndex++];
            }

            specializedLocals.Add(new MirLocal
            {
                Id = local.Id,
                Name = local.Name,
                TypeId = localType,
                IsMutable = local.IsMutable,
                IsParameter = local.IsParameter,
                BindingMode = local.BindingMode,
                Span = local.Span
            });
        }

        var specializedBlocks = CloneBlocksWithTypeSubstitution(
            template.BasicBlocks,
            typeBindings,
            substitutionService,
            resolvingTypeIds);
        RewriteConstGenericValues(specializedBlocks, signature.GenericValueArguments);

        return new MirFunc
        {
            Name = specializationName,
            SourceName = string.IsNullOrWhiteSpace(template.SourceName) ? template.Name : template.SourceName,
            Locals = specializedLocals,
            BasicBlocks = specializedBlocks,
            EntryBlockId = template.EntryBlockId,
            ReturnType = substitutionService.SubstituteTypeId(signature.ReturnType, typeBindings, resolvingTypeIds),
            GenericParameterCount = 0,
            GenericParameters = [],
            GenericTypeParameterIds = [],
            IsRuntimeWordAbi = template.IsRuntimeWordAbi,
            IsExternal = template.IsExternal,
            ExternalSymbolName = template.ExternalSymbolName,
            ExternalLibrary = template.ExternalLibrary,
            Span = template.Span,
            SymbolId = specializationSymbol,
            FunctionId = specializationFunctionId,
            TraitInvokeHelper = template.TraitInvokeHelper,
            TraitInvokeHelperTraitId = template.TraitInvokeHelperTraitId,
            IntrinsicName = template.IntrinsicName,
            BuiltinIntrinsicRole = template.BuiltinIntrinsicRole
        };
    }
}
