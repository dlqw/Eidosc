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

    private LlvmFunction GenerateRetainer(ConstructorTypeLayout layout, int typeId)
    {
        var sanitizedTypeName = NameMangler.SanitizeIdentifier(layout.TypeName);
        var sanitizedCtorName = NameMangler.SanitizeIdentifier(layout.ConstructorName);
        var constructorSymbol = $"{WellKnownStrings.Mangling.Prefix}{sanitizedTypeName}__{sanitizedCtorName}";
        var retainerName = $"eidos_retain_fields__{sanitizedTypeName}__{sanitizedCtorName}__{typeId:X8}";
        var retainer = new LlvmFunction
        {
            Name = retainerName,
            ReturnType = LlvmVoidType.Instance,
            Linkage = LlvmLinkage.Private
        };
        retainer.Parameters.Add(new LlvmParameter
        {
            Name = "ptr",
            Type = LlvmPointerType.VoidPtr()
        });

        var previousBlock = _currentBlock;
        var block = new LlvmBasicBlock { Label = WellKnownStrings.InternalNames.Entry };
        _currentBlock = block;
        _typeLowering.TryGetStructTypeByConstructorName(constructorSymbol, out var structType);
        var pointer = new LlvmLocal { Name = "%ptr", Type = LlvmPointerType.VoidPtr() };

        for (var index = 0; index < layout.FieldTypeIds.Count; index++)
        {
            var fieldTypeId = layout.FieldTypeIds[index];
            if (!PayloadContainsManagedRc(fieldTypeId))
            {
                continue;
            }

            var storageType = LowerStorageTypeIdOrReport(fieldTypeId, "record retainer field");
            LlvmGetElementPtr fieldPointer;
            if (structType != null)
            {
                fieldPointer = new LlvmGetElementPtr
                {
                    Pointer = pointer,
                    StructType = structType,
                    StructFieldIndex = ComputeStructFieldIndex(false, index),
                    ResultName = $"%field{index}_ptr"
                };
            }
            else
            {
                fieldPointer = new LlvmGetElementPtr
                {
                    Pointer = pointer,
                    ElementType = LlvmIntType.I8,
                    Index = new LlvmConstant { Value = (long)index * 8L, Type = LlvmIntType.I64 },
                    ResultName = $"%field{index}_ptr"
                };
            }

            block.Instructions.Add(fieldPointer);
            var load = new LlvmLoad
            {
                Pointer = new LlvmInstructionRef { Instruction = fieldPointer, Type = LlvmPointerType.VoidPtr() },
                LoadType = storageType,
                ResultName = $"%field{index}_value"
            };
            block.Instructions.Add(load);
            EmitRetainManagedPayloadValue(
                fieldTypeId,
                new LlvmInstructionRef { Instruction = load, Type = storageType },
                storageType);
        }

        block.Terminator = new LlvmRet();
        retainer.BasicBlocks.Add(block);
        _currentBlock = previousBlock;
        return retainer;
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
    /// 生成模块初始化函数：按拓扑序求值运行时初始化的模块变量并存储到全局，
    /// 随后注册所有析构器。
    /// </summary>
    /// <param name="typeOperations">析构器列表: (typeId, destructorName, retainerName)</param>
    /// <param name="runtimeInitVars">运行时初始化的模块变量: (全局, init 函数 LLVM 名)，已按依赖序排列</param>
    /// <returns>初始化函数</returns>
    public LlvmFunction GenerateModuleInit(
        List<(int typeId, string destructorName, string retainerName)> typeOperations,
        IReadOnlyList<(LlvmGlobal Global, string LlvmInitName)>? runtimeInitVars = null)
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

        // 模块变量运行时初始化：调用合成 init 函数并存储（依赖序已由 MIR 排定）。
        foreach (var (global, llvmInitName) in runtimeInitVars ?? [])
        {
            var initCall = new LlvmCall
            {
                Function = new LlvmGlobal
                {
                    Name = llvmInitName,
                    Type = new LlvmFunctionType
                    {
                        ReturnType = global.Type,
                        ParameterTypes = []
                    }
                },
                Arguments = [],
                ReturnType = global.Type,
                ResultName = _nameMangler.NewTempName("module_var_init_value")
            };
            entryBlock.Instructions.Add(initCall);
            entryBlock.Instructions.Add(new LlvmStore
            {
                Pointer = global,
                Value = new LlvmInstructionRef { Instruction = initCall, Type = global.Type }
            });
        }

        // 为每个析构器生成注册调用
        foreach (var (typeId, destructorName, retainerName) in typeOperations)
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
            if (!string.IsNullOrEmpty(retainerName))
            {
                entryBlock.Instructions.Add(new LlvmCall
                {
                    Function = new LlvmGlobal
                    {
                        Name = WellKnownStrings.Runtime.RegisterRetainer,
                        Type = new LlvmFunctionType
                        {
                            ReturnType = LlvmVoidType.Instance,
                            ParameterTypes = [LlvmIntType.I32, LlvmPointerType.VoidPtr()]
                        }
                    },
                    Arguments =
                    [
                        new LlvmConstant { Value = typeId, Type = LlvmIntType.I32 },
                        new LlvmGlobal { Name = retainerName, Type = LlvmPointerType.VoidPtr() }
                    ],
                    ReturnType = LlvmVoidType.Instance,
                    ResultName = ""
                });
            }
        }

        entryBlock.Terminator = new LlvmRet();
        initFunc.BasicBlocks.Add(entryBlock);

        return initFunc;
    }

    /// <summary>
    /// 生成 eidos_module_init：模块变量运行时初始化（若存在）+ ADT 析构器注册。
    /// 两者皆无时不生成（入口 shim 提供弱桩）。
    /// </summary>
    private void SynthesizeAdtDestructors(MirModule mirModule, LlvmModule llvmModule)
    {
        var runtimeInitVars = CollectRuntimeInitModuleVarEntries();
        var allocatedTypeIds = CollectAllocatedRuntimeTypeIds(llvmModule);
        if (allocatedTypeIds.Count == 0 && runtimeInitVars.Count == 0)
        {
            return;
        }

        var typeOperations = new List<(int typeId, string destructorName, string retainerName)>();
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
            var retainerFunc = GenerateRetainer(layout, typeId);
            llvmModule.Functions.Add(destructorFunc);
            llvmModule.Functions.Add(retainerFunc);
            typeOperations.Add((typeId, destructorFunc.Name, retainerFunc.Name));
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
            typeOperations.Add((boxRuntimeTypeId, destructorFunc.Name, string.Empty));
        }

        if (typeOperations.Count == 0 && runtimeInitVars.Count == 0)
        {
            return;
        }

        var moduleInit = GenerateModuleInit(typeOperations, runtimeInitVars);
        llvmModule.Functions.Add(moduleInit);
    }

    /// <summary>
    /// 汇总运行时初始化模块变量的 (全局, LLVM init 函数名)，按 MIR 排定的拓扑序。
    /// 未解析到 LLVM 名的 init 函数（合成失败/被裁剪）跳过，变量保持零初始化。
    /// </summary>
    private List<(LlvmGlobal Global, string LlvmInitName)> CollectRuntimeInitModuleVarEntries()
    {
        var result = new List<(LlvmGlobal Global, string LlvmInitName)>();
        foreach (var entry in _runtimeInitModuleVars.OrderBy(static entry => entry.Order))
        {
            if (entry.Global.Type is LlvmVoidType)
            {
                continue;
            }

            if (_moduleVarInitLlvmNames.TryGetValue(entry.MirInitName, out var llvmName))
            {
                result.Add((entry.Global, llvmName));
            }
        }

        return result;
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
            return AdtConstructorTypeId.Compute(
                constructorRef.FunctionId,
                constructorRef.SymbolId,
                constructorRef.Name);
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

        var completeFieldCount = layouts.Max(static candidate => candidate.FieldTypeIds.Count);
        var completeLayouts = layouts
            .Where(candidate => candidate.FieldTypeIds.Count == completeFieldCount)
            .ToArray();
        var selectedLayout = completeLayouts[0];
        var selectedMask = GetManagedFieldMask(selectedLayout);
        if (!selectedMask.Any(static isManaged => isManaged))
        {
            return false;
        }

        for (var index = 1; index < completeLayouts.Length; index++)
        {
            var candidateMask = GetManagedFieldMask(completeLayouts[index]);
            if (!HasSameManagedFieldShape(selectedMask, candidateMask))
            {
                return false;
            }
        }

        layout = selectedLayout;
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
