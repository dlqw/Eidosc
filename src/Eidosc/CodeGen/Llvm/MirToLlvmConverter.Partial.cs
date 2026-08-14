using Eidosc.Mir;

namespace Eidosc.CodeGen.Llvm;

public sealed partial class MirToLlvmConverter
{
    public LlvmModule ConvertSelectedFunctions(
        MirModule module,
        IReadOnlySet<string> functionKeys)
    {
        Diagnostics.Clear();
        ResetAndIndexModuleContext(module);

        using (MeasureConverterSubphase("register_function_types"))
        {
            foreach (var func in module.Functions)
            {
                RegisterFunctionType(func);
            }
        }
        var nounwindEligibleFunctions = AnalyzeMirNounwindEligibleFunctions(module);

        LlvmModule llvmModule;
        using (MeasureConverterSubphase("create_module"))
        {
            llvmModule = new LlvmModule
            {
                Name = module.Name,
                Functions = new List<LlvmFunction>(Math.Min(module.Functions.Count, functionKeys.Count)),
                LinkLibraries = module.LinkLibraries.ToList()
            };
        }

        _currentModule = llvmModule;
        if (module.Functions.Any(ShouldAlwaysInline))
        {
            llvmModule.AttributeGroups.Add(new LlvmAttributeGroup
            {
                Id = 0,
                Attributes = ["alwaysinline"]
            });
        }

        using (MeasureConverterSubphase("lower_module_variables"))
        {
            LowerModuleVariables(module, llvmModule);
        }

        using (MeasureConverterSubphase("collect_named_struct_types"))
        {
            CollectNamedStructTypes(llvmModule);
        }

        using (MeasureConverterSubphase("convert_selected_functions"))
        {
            foreach (var func in module.Functions)
            {
                if (!functionKeys.Contains(MirFunctionIdentity.GetStableKey(func)))
                {
                    continue;
                }

                if (func.IsExternal)
                {
                    AddExternalFfiDeclaration(func, llvmModule);
                    continue;
                }

                if (IsIntrinsicDeclaration(func) ||
                    (!func.IsRuntimeWordAbi && IsGenericSignature(func)))
                {
                    continue;
                }

                llvmModule.Functions.Add(ConvertFunctionCore(func));
            }
        }

        using (MeasureConverterSubphase("add_selected_helpers"))
        {
            if (_synthesizedClosureHelpers.Count > 0)
            {
                llvmModule.Functions.AddRange(_synthesizedClosureHelpers);
            }
        }

        _currentModule = null;

        using (MeasureConverterSubphase("add_declarations"))
        {
            AddRuntimeDeclarations(llvmModule);
            AddRecordedExternalDeclarations(llvmModule);
        }

        using (MeasureConverterSubphase("infer_function_attributes"))
        {
            LlvmFunctionAttributeInference.Apply(
                llvmModule,
                nounwindEligibleFunctions);
        }

        using (MeasureConverterSubphase("validate_output"))
        {
            ReportDuplicateGlobalDefinitions(llvmModule);
            ReportInvalidUnresolvedExternalDeclarations(llvmModule);
        }

        return llvmModule;
    }

    private void ResetAndIndexModuleContext(MirModule module)
    {
        using (MeasureConverterSubphase("reset_and_index_metadata"))
        {
            _typeLowering.SetDynamicTypeKeys(module.DynamicTypeKeys);
            _typeLowering.SetTypeDescriptors(module.TypeDescriptors);
            _typeLowering.SetConstructorLayouts(module.ConstructorLayouts);
            _typeLowering.SetScalarTagTypeIds(PayloadlessAdtRepresentationAnalysis.Analyze(module));
            _typeLowering.SetCopyLikeTypeIds(module.CopyLikeTypeIds);
            if (module.CStructAccessors.Count > 0)
            {
                _cstructAccessors = module.CStructAccessors;
            }

            _funcCache.Clear();
            _externalFunctionDeclarations.Clear();
            _specializationFailureByTemplateKey.Clear();
            IndexTypeConstructors(module.TypeConstructors);
            _ffiSymbolNameBySourceName.Clear();
            _ffiSymbolNameBySymbolId.Clear();
            _genericFunctionSymbols.Clear();
            _genericFunctionNames.Clear();
            _reportedGenericCallSites.Clear();
            _reportedUnresolvedTypeSites.Clear();
            _reportedUnresolvedFunctionSites.Clear();
            _valueBoxPayloadTypeByRuntimeTypeId.Clear();
            _runtimeFunctionGlobalCache.Clear();
            _arrayElementPolicies.Clear();
            _stringLiteralGlobals.Clear();
            _moduleVarGlobalsBySymbol.Clear();
            _moduleVarGlobalsByName.Clear();
            _stringLiteralCounter = 0;
            _closureThunkCounter = 0;
            _synthesizedClosureHelpers.Clear();
            _builtinShowBoolHelper = null;
            _erasedShowHelper = null;
            IndexSpecializationFailures(module.SpecializationFailures);
        }
    }
}
