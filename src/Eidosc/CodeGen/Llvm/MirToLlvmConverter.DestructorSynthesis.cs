using Eidosc.Symbols;
using System.Security.Cryptography;
using System.Text;
using Eidosc.Borrow;
using Eidosc.Diagnostic;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Types;

namespace Eidosc.CodeGen.Llvm;

// ADT destructor synthesis, runtime type ID computation, FFI declarations
public sealed partial class MirToLlvmConverter
{


    private void ReportDuplicateGlobalDefinitions(LlvmModule module)
    {
        var definitionsByName = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var function in module.Functions)
        {
            if (string.IsNullOrWhiteSpace(function.Name))
            {
                continue;
            }

            AddDefinition(function.Name, $"function {function.Name}");
        }

        foreach (var global in module.Globals)
        {
            if (string.IsNullOrWhiteSpace(global.Name))
            {
                continue;
            }

            AddDefinition(global.Name, $"global {global.Name}");
        }

        foreach (var (name, definitions) in definitionsByName
                     .Where(entry => entry.Value.Count > 1)
                     .OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            Diagnostics.Add(
                Diagnostic.Diagnostic.Error(
                        DiagnosticMessages.LlvmDuplicateGlobalDefinition(name),
                        "E5308")
                    .WithNote(DiagnosticMessages.LlvmDuplicateGlobalDefinitionNote(string.Join(", ", definitions))));
        }

        void AddDefinition(string name, string description)
        {
            if (!definitionsByName.TryGetValue(name, out var definitions))
            {
                definitions = [];
                definitionsByName[name] = definitions;
            }

            definitions.Add(description);
        }
    }

    private void AddMainEntryWrapperIfNeeded(LlvmModule module, LlvmFunction? mainFunction)
    {
        if (mainFunction == null)
        {
            return;
        }

        var entryName = _nameMangler.MangleFunctionName("", WellKnownStrings.SpecialNames.Main);
        if (string.Equals(mainFunction.Name, entryName, StringComparison.Ordinal) ||
            module.Functions.Any(function => string.Equals(function.Name, entryName, StringComparison.Ordinal)))
        {
            return;
        }

        var wrapper = new LlvmFunction
        {
            Name = entryName,
            ReturnType = LlvmIntType.I64,
            Linkage = LlvmLinkage.External
        };
        wrapper.Parameters.Add(new LlvmParameter
        {
            Name = "argc",
            Type = LlvmIntType.I64
        });

        var entryBlock = new LlvmBasicBlock
        {
            Label = WellKnownStrings.InternalNames.Entry
        };
        var argc = new LlvmLocal
        {
            Name = "argc",
            Type = LlvmIntType.I64
        };
        var wrapperArguments = mainFunction.Parameters
            .Select(parameter => BuildMainWrapperArgument(parameter.Type, argc, entryBlock))
            .ToList();
        var mainFunctionType = new LlvmFunctionType
        {
            ReturnType = mainFunction.ReturnType,
            ParameterTypes = mainFunction.Parameters.Select(parameter => parameter.Type).ToList()
        };
        var call = new LlvmCall
        {
            Function = new LlvmGlobal
            {
                Name = mainFunction.Name,
                Type = mainFunctionType
            },
            Arguments = wrapperArguments,
            ReturnType = mainFunction.ReturnType,
            ResultName = mainFunction.ReturnType is LlvmVoidType ? null : "main_result"
        };
        entryBlock.Instructions.Add(call);
        entryBlock.Terminator = new LlvmRet
        {
            Value = BuildMainWrapperReturnValue(call, entryBlock)
        };
        wrapper.BasicBlocks.Add(entryBlock);
        module.Functions.Add(wrapper);
    }

    private LlvmValue BuildMainWrapperArgument(
        LlvmType parameterType,
        LlvmValue argc,
        LlvmBasicBlock entryBlock)
    {
        if (parameterType == LlvmIntType.I64)
        {
            return argc;
        }

        if (parameterType == LlvmIntType.I1)
        {
            return new LlvmConstant
            {
                Value = true,
                Type = LlvmIntType.I1
            };
        }

        if (parameterType is LlvmPointerType pointerType)
        {
            return new LlvmIntToPtr
            {
                Integer = argc,
                TargetType = pointerType,
                Type = pointerType
            };
        }

        if (parameterType is LlvmIntType)
        {
            var trunc = new LlvmTrunc
            {
                Value = argc,
                TargetType = parameterType,
                ResultName = _nameMangler.NewTempName("main_arg_trunc")
            };
            entryBlock.Instructions.Add(trunc);
            return new LlvmInstructionRef
            {
                Instruction = trunc,
                Type = parameterType
            };
        }

        return argc;
    }

    private LlvmValue BuildMainWrapperReturnValue(
        LlvmCall call,
        LlvmBasicBlock entryBlock)
    {
        if (call.ReturnType is LlvmVoidType)
        {
            return LlvmConstant.Zero;
        }

        var result = new LlvmInstructionRef
        {
            Instruction = call,
            Type = call.ReturnType
        };
        if (call.ReturnType == LlvmIntType.I64)
        {
            return result;
        }

        if (call.ReturnType == LlvmIntType.I1)
        {
            var zext = new LlvmZext
            {
                Value = result,
                TargetType = LlvmIntType.I64,
                ResultName = _nameMangler.NewTempName("main_ret_zext")
            };
            entryBlock.Instructions.Add(zext);
            return new LlvmInstructionRef
            {
                Instruction = zext,
                Type = LlvmIntType.I64
            };
        }

        if (call.ReturnType is LlvmPointerType)
        {
            return new LlvmPtrToInt
            {
                Pointer = result,
                TargetType = LlvmIntType.I64,
                Type = LlvmIntType.I64
            };
        }

        return result;
    }

    private void SynthesizeAdtConstructorStubs(LlvmModule module)
    {
        if (module.Declarations.Count == 0)
        {
            return;
        }

        var existingFunctions = new HashSet<string>(
            module.Functions.Select(function => function.Name),
            StringComparer.Ordinal);
        var synthesized = new List<string>();

        foreach (var declaration in module.Declarations)
        {
            if (!TryCreateAdtConstructorStub(declaration, existingFunctions, out var constructor))
            {
                continue;
            }

            module.Functions.Add(constructor);
            existingFunctions.Add(constructor.Name);
            synthesized.Add(constructor.Name);
        }

        if (synthesized.Count == 0)
        {
            return;
        }

        module.Declarations.RemoveAll(declaration => synthesized.Contains(declaration.Name, StringComparer.Ordinal));
    }

    private bool TryCreateAdtConstructorStub(
        LlvmDeclaration declaration,
        HashSet<string> existingFunctions,
        out LlvmFunction constructor)
    {
        constructor = null!;

        if (existingFunctions.Contains(declaration.Name))
        {
            return false;
        }

        if (!TypeSemantics.IsLikelyAdtConstructorByMangledName(declaration.Name))
        {
            return false;
        }

        if (declaration.Type is not LlvmFunctionType functionType)
        {
            return false;
        }

        if (functionType.ReturnType is not LlvmPointerType)
        {
            return false;
        }

        constructor = new LlvmFunction
        {
            Name = declaration.Name,
            ReturnType = LlvmPointerType.VoidPtr(),
            Linkage = LlvmLinkage.External
        };
        PopulateConstructorParameters(constructor, functionType);

        _typeLowering.TryGetStructTypeByConstructorName(declaration.Name, out var structType);

        var entryBlock = new LlvmBasicBlock
        {
            Label = WellKnownStrings.InternalNames.Entry
        };
        var ctorObjRef = EmitConstructorAllocation(entryBlock, declaration.Name, constructor.Parameters);
        EmitConstructorFieldStores(entryBlock, ctorObjRef, constructor.Parameters, structType);
        entryBlock.Terminator = new LlvmRet { Value = ctorObjRef };
        constructor.BasicBlocks.Add(entryBlock);
        return true;
    }

    private static void PopulateConstructorParameters(LlvmFunction constructor, LlvmFunctionType functionType)
    {
        for (var index = 0; index < functionType.ParameterTypes.Count; index++)
        {
            constructor.Parameters.Add(new LlvmParameter
            {
                Name = $"arg{index}",
                Type = NormalizeParameterType(functionType.ParameterTypes[index])
            });
        }
    }

    private static LlvmInstructionRef EmitConstructorAllocation(
        LlvmBasicBlock entryBlock,
        string declarationName,
        IReadOnlyList<LlvmParameter> parameters)
    {
        var payloadSize = Math.Max(
            8L,
            parameters.Sum(static parameter => AlignConstructorPayloadSize(GetLlvmStorageSize(parameter.Type))));
        var allocCall = new LlvmCall
        {
            Function = CreateRuntimeFunctionGlobalUncached(
                WellKnownStrings.Runtime.Alloc,
                LlvmPointerType.VoidPtr(),
                [LlvmIntType.I64, LlvmIntType.I32]),
            Arguments =
            [
                new LlvmConstant { Value = payloadSize, Type = LlvmIntType.I64 },
                new LlvmConstant { Value = AdtConstructorTypeId.ComputeFromSymbol(declarationName), Type = LlvmIntType.I32 }
            ],
            ReturnType = LlvmPointerType.VoidPtr(),
            ResultName = "ctor_obj"
        };
        entryBlock.Instructions.Add(allocCall);
        return new LlvmInstructionRef
        {
            Instruction = allocCall,
            Type = LlvmPointerType.VoidPtr()
        };
    }

    private static void EmitConstructorFieldStores(
        LlvmBasicBlock entryBlock,
        LlvmInstructionRef ctorObjRef,
        IReadOnlyList<LlvmParameter> parameters,
        LlvmStructType? structType)
    {
        for (var index = 0; index < parameters.Count; index++)
        {
            var parameter = parameters[index];
            var fieldIndex = structType != null ? ComputeStructFieldIndex(false, index) : index;

            LlvmGetElementPtr slotPtr;
            if (structType != null)
            {
                slotPtr = new LlvmGetElementPtr
                {
                    Pointer = ctorObjRef,
                    StructType = structType,
                    StructFieldIndex = fieldIndex,
                    ResultName = $"field{index}_ptr"
                };
            }
            else
            {
                slotPtr = new LlvmGetElementPtr
                {
                    Pointer = ctorObjRef,
                    ElementType = LlvmIntType.I8,
                    Index = new LlvmConstant
                    {
                        Value = (long)index * 8L,
                        Type = LlvmIntType.I64
                    },
                    ResultName = $"field{index}_ptr"
                };
            }

            entryBlock.Instructions.Add(slotPtr);
            entryBlock.Instructions.Add(new LlvmStore
            {
                Value = new LlvmLocal
                {
                    Name = parameter.Name,
                    Type = parameter.Type
                },
                Pointer = new LlvmInstructionRef
                {
                    Instruction = slotPtr,
                    Type = LlvmPointerType.VoidPtr()
                }
            });
        }
    }

    /// <summary>
    /// 为 ADT 构造器生成析构器函数。
    /// 仅对托管 RC 字段生成 eidos_decref 调用。
    /// </summary>
    public LlvmFunction GenerateDestructor(
        ConstructorTypeLayout layout,
        Func<TypeId, bool> isManagedRcType,
        int typeId)
    {
        var sanitizedTypeName = NameMangler.SanitizeIdentifier(layout.TypeName);
        var sanitizedCtorName = NameMangler.SanitizeIdentifier(layout.ConstructorName);
        var constructorSymbol = $"{WellKnownStrings.Mangling.Prefix}{sanitizedTypeName}__{sanitizedCtorName}";
        var destructorName = $"{WellKnownStrings.SpecialNames.DestructorPrefix}{sanitizedTypeName}__{sanitizedCtorName}__{typeId:X8}";

        var destructor = new LlvmFunction
        {
            Name = destructorName,
            ReturnType = LlvmVoidType.Instance,
            Linkage = LlvmLinkage.Private
        };

        destructor.Parameters.Add(new LlvmParameter
        {
            Name = "ptr",
            Type = LlvmPointerType.VoidPtr()
        });

        var entryBlock = new LlvmBasicBlock { Label = WellKnownStrings.InternalNames.Entry };

        // 查找结构体类型以使用正确的 GEP 模式
        _typeLowering.TryGetStructTypeByConstructorName(constructorSymbol, out var structType);

        var ptrRef = new LlvmLocal { Name = "%ptr", Type = LlvmPointerType.VoidPtr() };

        for (var i = 0; i < layout.FieldTypeIds.Count; i++)
        {
            if (!isManagedRcType(layout.FieldTypeIds[i]))
            {
                continue;
            }

            // 计算字段指针 — 与 EmitConstructorFieldStores 相同的 GEP 模式
            LlvmGetElementPtr slotPtr;
            if (structType != null)
            {
                var fieldIndex = ComputeStructFieldIndex(false, i);
                slotPtr = new LlvmGetElementPtr
                {
                    Pointer = ptrRef,
                    StructType = structType,
                    StructFieldIndex = fieldIndex,
                    ResultName = $"%field{i}_ptr"
                };
            }
            else
            {
                slotPtr = new LlvmGetElementPtr
                {
                    Pointer = ptrRef,
                    ElementType = LlvmIntType.I8,
                    Index = new LlvmConstant { Value = (long)i * 8L, Type = LlvmIntType.I64 },
                    ResultName = $"%field{i}_ptr"
                };
            }

            entryBlock.Instructions.Add(slotPtr);

            // 加载字段值（托管字段存储为指针）
            var fieldVal = new LlvmLoad
            {
                Pointer = new LlvmInstructionRef { Instruction = slotPtr, Type = LlvmPointerType.VoidPtr() },
                LoadType = LlvmPointerType.VoidPtr(),
                ResultName = $"%field{i}_val"
            };
            entryBlock.Instructions.Add(fieldVal);

            // 调用 eidos_decref
            entryBlock.Instructions.Add(new LlvmCall
            {
                Function = new LlvmGlobal
                {
                    Name = WellKnownStrings.Runtime.DecRef,
                    Type = new LlvmFunctionType
                    {
                        ReturnType = LlvmVoidType.Instance,
                        ParameterTypes = [LlvmPointerType.VoidPtr()]
                    }
                },
                Arguments = [new LlvmInstructionRef { Instruction = fieldVal, Type = LlvmPointerType.VoidPtr() }],
                ReturnType = LlvmVoidType.Instance,
                ResultName = ""
            });
        }

        entryBlock.Terminator = new LlvmRet();
        destructor.BasicBlocks.Add(entryBlock);
        return destructor;
    }

    private LlvmFunction GenerateValueBoxDestructor(TypeId payloadTypeId, int boxRuntimeTypeId)
    {
        var destructorName = $"{WellKnownStrings.SpecialNames.DestructorPrefix}value_box__{payloadTypeId.Value:X8}__{boxRuntimeTypeId:X8}";
        var destructor = new LlvmFunction
        {
            Name = destructorName,
            ReturnType = LlvmVoidType.Instance,
            Linkage = LlvmLinkage.Private
        };

        destructor.Parameters.Add(new LlvmParameter
        {
            Name = "ptr",
            Type = LlvmPointerType.VoidPtr()
        });

        var entryBlock = new LlvmBasicBlock { Label = WellKnownStrings.InternalNames.Entry };
        var ptrRef = new LlvmLocal { Name = "%ptr", Type = LlvmPointerType.VoidPtr() };
        var storageType = LowerStorageTypeIdOrReport(payloadTypeId, "value_box destructor payload");
        EmitReleaseManagedPayloadFromPointer(entryBlock, ptrRef, payloadTypeId, storageType, "payload");
        entryBlock.Terminator = new LlvmRet();
        destructor.BasicBlocks.Add(entryBlock);
        return destructor;
    }

    private void EmitReleaseManagedPayloadFromPointer(
        LlvmBasicBlock block,
        LlvmValue pointer,
        TypeId typeId,
        LlvmType storageType,
        string namePrefix)
    {
        if (!PayloadContainsManagedRc(typeId))
        {
            return;
        }

        if (IsManagedRcType(typeId) && storageType is LlvmPointerType)
        {
            var load = new LlvmLoad
            {
                Pointer = pointer,
                LoadType = LlvmPointerType.VoidPtr(),
                ResultName = $"%{namePrefix}_val"
            };
            block.Instructions.Add(load);
            block.Instructions.Add(new LlvmCall
            {
                Function = CreateRuntimeFunctionGlobal(
                    WellKnownStrings.Runtime.DecRefShared,
                    LlvmVoidType.Instance,
                    [LlvmPointerType.VoidPtr()]),
                Arguments = [new LlvmInstructionRef { Instruction = load, Type = LlvmPointerType.VoidPtr() }]
            });
            return;
        }

        if (storageType is not LlvmStructType structType ||
            !_typeLowering.TryGetTypeDescriptor(typeId, out var descriptor) ||
            descriptor is not TypeDescriptor.Tuple tuple)
        {
            return;
        }

        for (var index = 0; index < tuple.FieldTypes.Length && index < structType.Fields.Count; index++)
        {
            var fieldTypeId = tuple.FieldTypes[index];
            if (!PayloadContainsManagedRc(fieldTypeId))
            {
                continue;
            }

            var fieldPointer = new LlvmGetElementPtr
            {
                Pointer = pointer,
                StructType = structType,
                StructFieldIndex = index,
                ResultName = $"%{namePrefix}_field{index}_ptr"
            };
            block.Instructions.Add(fieldPointer);
            EmitReleaseManagedPayloadFromPointer(
                block,
                new LlvmInstructionRef { Instruction = fieldPointer, Type = LlvmPointerType.VoidPtr() },
                fieldTypeId,
                structType.Fields[index],
                $"{namePrefix}_field{index}");
        }
    }

    /// <summary>
    /// 生成模块初始化函数，注册所有析构器
    /// </summary>
    /// <param name="destructors">析构器列表: (typeId, destructorName)</param>
    /// <returns>初始化函数</returns>
    public LlvmFunction GenerateModuleInit(List<(int typeId, string destructorName)> destructors)
    {
        var initFunc = new LlvmFunction
        {
            Name = WellKnownStrings.Runtime.ModuleInit,
            ReturnType = LlvmVoidType.Instance,
            Linkage = LlvmLinkage.External
        };

        var entryBlock = new LlvmBasicBlock
        {
            Label = WellKnownStrings.InternalNames.Entry
        };

        // 为每个析构器生成注册调用
        foreach (var (typeId, destructorName) in destructors)
        {
            var registerCall = new LlvmCall
            {
                Function = new LlvmGlobal
                {
                    Name = WellKnownStrings.Runtime.RegisterDestructor,
                    Type = new LlvmFunctionType
                    {
                        ReturnType = LlvmVoidType.Instance,
                        ParameterTypes = [LlvmIntType.I32, LlvmPointerType.VoidPtr()]
                    }
                },
                Arguments =
                [
                    new LlvmConstant { Value = typeId, Type = LlvmIntType.I32 },
                    new LlvmGlobal { Name = destructorName, Type = LlvmPointerType.VoidPtr() }
                ],
                ReturnType = LlvmVoidType.Instance,
                ResultName = ""
            };
            entryBlock.Instructions.Add(registerCall);
        }

        entryBlock.Terminator = new LlvmRet();
        initFunc.BasicBlocks.Add(entryBlock);

        return initFunc;
    }

    /// <summary>
    /// 为所有 ADT 构造器生成析构器，并生成 eidos_module_init 注册函数。
    /// 仅在有构造器布局时生成，无 ADT 的程序不需要此函数（入口 shim 提供弱桩）。
    /// </summary>
    private void SynthesizeAdtDestructors(MirModule mirModule, LlvmModule llvmModule)
    {
        var allocatedTypeIds = CollectAllocatedRuntimeTypeIds(llvmModule);
        if (allocatedTypeIds.Count == 0)
        {
            return;
        }

        var destructorPairs = new List<(int typeId, string destructorName)>();
        var layoutsByRuntimeTypeId = new Dictionary<int, List<ConstructorTypeLayout>>();

        if (mirModule.ConstructorLayouts.Count > 0)
        {
            foreach (var (_, layouts) in mirModule.ConstructorLayouts)
            {
                foreach (var layout in layouts)
                {
                    var typeId = ComputeRuntimeConstructorTypeId(layout);
                    if (!allocatedTypeIds.Contains(typeId))
                    {
                        continue;
                    }

                    if (!layoutsByRuntimeTypeId.TryGetValue(typeId, out var sameRuntimeTypeLayouts))
                    {
                        sameRuntimeTypeLayouts = [];
                        layoutsByRuntimeTypeId[typeId] = sameRuntimeTypeLayouts;
                    }

                    sameRuntimeTypeLayouts.Add(layout);
                }
            }
        }

        foreach (var (typeId, layouts) in layoutsByRuntimeTypeId)
        {
            if (!TrySelectDestructorLayout(typeId, layouts, out var layout))
            {
                continue;
            }

            var destructorFunc = GenerateDestructor(layout, IsManagedRcType, typeId);
            llvmModule.Functions.Add(destructorFunc);
            destructorPairs.Add((typeId, destructorFunc.Name));
        }

        foreach (var (boxRuntimeTypeId, payloadTypeId) in _valueBoxPayloadTypeByRuntimeTypeId)
        {
            if (!allocatedTypeIds.Contains(boxRuntimeTypeId) ||
                !PayloadContainsManagedRc(payloadTypeId))
            {
                continue;
            }

            var destructorFunc = GenerateValueBoxDestructor(payloadTypeId, boxRuntimeTypeId);
            llvmModule.Functions.Add(destructorFunc);
            destructorPairs.Add((boxRuntimeTypeId, destructorFunc.Name));
        }

        if (destructorPairs.Count == 0)
        {
            return;
        }

        var moduleInit = GenerateModuleInit(destructorPairs);
        llvmModule.Functions.Add(moduleInit);
    }

    private static int ComputeRuntimeConstructorTypeId(ConstructorTypeLayout layout)
    {
        return layout.RuntimeTypeId != 0
            ? layout.RuntimeTypeId
            : AdtConstructorTypeId.Compute(layout.ConstructorName);
    }

    private int ComputeRuntimeConstructorTypeId(MirFunctionRef constructorRef)
    {
        if (_typeLowering.TryGetConstructorLayouts(constructorRef.TypeId, out var layouts))
        {
            var layout = layouts.Count == 1
                ? layouts[0]
                : layouts.FirstOrDefault(candidate =>
                    string.Equals(candidate.ConstructorName, constructorRef.Name, StringComparison.Ordinal) ||
                    constructorRef.Name.EndsWith(
                        $"{WellKnownStrings.Separators.Path}{candidate.ConstructorName}",
                        StringComparison.Ordinal) ||
                    constructorRef.Name.EndsWith($"__{candidate.ConstructorName}", StringComparison.Ordinal));
            if (layout is { RuntimeTypeId: not 0 })
            {
                return layout.RuntimeTypeId;
            }
        }

        if (!string.IsNullOrWhiteSpace(constructorRef.FunctionId.StableIdentityKey))
        {
            return AdtConstructorTypeId.Compute(constructorRef.FunctionId.StableIdentityKey);
        }

        if (_symbolTable?.GetSymbol(constructorRef.SymbolId) is CtorSymbol)
        {
            return ConstructorRuntimeTypeId.Compute(
                _symbolTable,
                constructorRef.SymbolId,
                constructorRef.Name);
        }

        return AdtConstructorTypeId.Compute(
            constructorRef.FunctionId,
            constructorRef.SymbolId,
            constructorRef.Name);
    }

    private static HashSet<int> CollectAllocatedRuntimeTypeIds(LlvmModule llvmModule)
    {
        var typeIds = new HashSet<int>();

        foreach (var function in llvmModule.Functions)
        {
            foreach (var block in function.BasicBlocks)
            {
                foreach (var instruction in block.Instructions)
                {
                    if (instruction is not LlvmCall
                        {
                            Function: LlvmGlobal { Name: WellKnownStrings.Runtime.Alloc },
                            Arguments: [_, LlvmConstant { Value: int typeId }]
                        })
                    {
                        continue;
                    }

                    typeIds.Add(typeId);
                }
            }
        }

        return typeIds;
    }

    private bool TrySelectDestructorLayout(
        int typeId,
        IReadOnlyList<ConstructorTypeLayout> layouts,
        out ConstructorTypeLayout layout)
    {
        layout = null!;
        if (layouts.Count == 0)
        {
            return false;
        }

        var selectedMask = GetManagedFieldMask(layouts[0]);
        if (!selectedMask.Any(static isManaged => isManaged))
        {
            return false;
        }

        for (var index = 1; index < layouts.Count; index++)
        {
            var candidateMask = GetManagedFieldMask(layouts[index]);
            if (!HasSameManagedFieldShape(selectedMask, candidateMask))
            {
                return false;
            }
        }

        layout = layouts[0];
        return true;
    }

    private bool[] GetManagedFieldMask(ConstructorTypeLayout layout)
    {
        var mask = new bool[layout.FieldTypeIds.Count];
        for (var index = 0; index < layout.FieldTypeIds.Count; index++)
        {
            mask[index] = IsManagedRcType(layout.FieldTypeIds[index]);
        }

        return mask;
    }

    private static bool HasSameManagedFieldShape(IReadOnlyList<bool> left, IReadOnlyList<bool> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }

        return true;
    }

    #region FFI 外部函数支持

    /// <summary>
    /// 注册 FFI 外部函数的符号名映射
    /// </summary>
    private void RegisterFfiFunction(MirFunc func)
    {
        var externalName = func.ExternalSymbolName ?? func.Name;
        if (!string.IsNullOrEmpty(func.Name) && !func.SymbolId.IsValid)
        {
            _ffiSymbolNameBySourceName[func.Name] = externalName;
        }
        if (func.SymbolId.IsValid)
        {
            _ffiSymbolNameBySymbolId[func.SymbolId] = externalName;
        }
    }

    /// <summary>
    /// 为 FFI 外部函数生成 LLVM declare 声明
    /// </summary>
    private void AddExternalFfiDeclaration(MirFunc func, LlvmModule module)
    {
        RegisterFfiFunction(func);

        var cSymbolName = func.ExternalSymbolName ?? func.Name;
        var functionType = _typeLowering.GetFunctionType(func);

        module.Declarations.Add(new LlvmDeclaration
        {
            Name = cSymbolName,
            Type = functionType,
            Origin = LlvmDeclarationOrigin.ExternalFfi
        });
    }

    /// <summary>
    /// 尝试获取 FFI 外部函数的 C 符号名
    /// </summary>
    private bool TryGetExternalFfiSymbolName(string sourceName, SymbolId symbolId, out string cSymbolName)
    {
        if (symbolId.IsValid && _ffiSymbolNameBySymbolId.TryGetValue(symbolId, out var bySymbol))
        {
            cSymbolName = bySymbol;
            return true;
        }

        if (symbolId.IsValid &&
            _symbolTable?.GetSymbol<FuncSymbol>(symbolId) is { IsExternal: true } externalSymbol)
        {
            cSymbolName = externalSymbol.ExternalSymbolName ?? externalSymbol.Name;
            _ffiSymbolNameBySymbolId[symbolId] = cSymbolName;
            if (!string.IsNullOrWhiteSpace(externalSymbol.Name))
            {
                _ffiSymbolNameBySourceName.TryAdd(externalSymbol.Name, cSymbolName);
            }

            return true;
        }

        if (!string.IsNullOrEmpty(sourceName) && _ffiSymbolNameBySourceName.TryGetValue(sourceName, out var byName))
        {
            cSymbolName = byName;
            return true;
        }

        if (!string.IsNullOrEmpty(sourceName) &&
            TryGetShortSourceFunctionName(sourceName, out var shortSourceName) &&
            _ffiSymbolNameBySourceName.TryGetValue(shortSourceName, out var byShortName))
        {
            cSymbolName = byShortName;
            return true;
        }

        cSymbolName = null!;
        return false;
    }

    #endregion
}
