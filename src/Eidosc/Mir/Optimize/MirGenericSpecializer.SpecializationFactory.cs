using Eidosc.Types;

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

        var specializedReturnType = substitutionService.SubstituteTypeId(signature.ReturnType, typeBindings, resolvingTypeIds);
        // A call-site borrow view (Ref/MutRef) must not become the value return
        // type of a specialization whose template declares a value return: the
        // view is an ABI artifact of an adjacent borrow use, not the function's
        // result type. Prefer the substituted template return type in that case.
        if (IsReferenceTypeDescriptorId(specializedReturnType) &&
            template.ReturnType.IsValid &&
            !IsReferenceTypeDescriptorId(template.ReturnType))
        {
            specializedReturnType = substitutionService.SubstituteTypeId(template.ReturnType, typeBindings, resolvingTypeIds);
        }

        return new MirFunc
        {
            Name = specializationName,
            SourceName = string.IsNullOrWhiteSpace(template.SourceName) ? template.Name : template.SourceName,
            Locals = specializedLocals,
            BasicBlocks = specializedBlocks,
            EntryBlockId = template.EntryBlockId,
            ReturnType = specializedReturnType,
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

    private bool IsReferenceTypeDescriptorId(TypeId typeId)
    {
        return typeId.IsValid &&
               TryGetTypeDescriptor(typeId, out var descriptor) &&
               descriptor is TypeDescriptor.Ref or TypeDescriptor.MutRef;
    }
}
