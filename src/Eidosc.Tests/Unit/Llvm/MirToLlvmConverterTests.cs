using Eidosc;
using Eidosc.Borrow;
using Eidosc.CodeGen.Llvm;
using Eidosc.Mir;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Llvm;

public partial class MirToLlvmConverterTests
{
    [Fact]
    public void ConvertFunction_CopyManagedValue_EmitsIncrefAndAliasesTarget()
    {
        var managedType = new TypeId(BaseTypes.StringId);
        var sourcePlace = LocalPlace(1, managedType);
        var targetPlace = LocalPlace(2, managedType);

        var func = BuildFunction(
            managedType,
            locals:
            [
                new MirLocal { Id = sourcePlace.Local, Name = "src", TypeId = managedType, IsParameter = true },
                new MirLocal { Id = targetPlace.Local, Name = "dst", TypeId = managedType }
            ],
            instructions:
            [
                new MirCopy
                {
                    Source = sourcePlace,
                    Target = targetPlace
                }
            ],
            returnValue: targetPlace);

        var llvmFunc = new MirToLlvmConverter().ConvertFunction(func);
        var entry = Assert.Single(llvmFunc.BasicBlocks);

        var increfCall = Assert.Single(entry.Instructions.OfType<LlvmCall>());
        var increfGlobal = Assert.IsType<LlvmGlobal>(increfCall.Function);
        Assert.Equal("eidos_incref_local", increfGlobal.Name);
        Assert.IsType<LlvmPointerType>(Assert.Single(increfCall.Arguments).Type);

        var ret = Assert.IsType<LlvmRet>(entry.Terminator);
        var retLocal = Assert.IsType<LlvmLocal>(ret.Value);
        Assert.Equal("src", retLocal.Name);
    }

    [Fact]
    public void ConvertFunction_NullReturnInStructFunction_ReportsDiagnosticAndEmitsUnreachable()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var tupleType = new TypeId(9901);
        var func = new MirFunc
        {
            Name = "default_tuple",
            ReturnType = tupleType,
            EntryBlockId = new BlockId { Value = 1 },
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Terminator = new MirReturn()
                }
            ]
        };

        var converter = new MirToLlvmConverter();
        var llvmModule = converter.Convert(new MirModule
        {
            Name = "default_return",
            DynamicTypeKeys = new Dictionary<int, string>
            {
                [tupleType.Value] = $"Tuple(T{intType.Value},T{boolType.Value})"
            },
            Functions = [func]
        });

        var llvmFunc = Assert.Single(llvmModule.Functions);
        Assert.IsType<LlvmUnreachable>(Assert.Single(llvmFunc.BasicBlocks).Terminator);
        Assert.Contains(converter.Diagnostics, diagnostic => diagnostic.Code == "E5203");
    }

    [Fact]
    public void ConvertSelectedFunctions_UsesModuleContextAndMatchesFullFunctionFragment()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var selected = BuildFunction(
            stringType,
            locals: [],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = stringType,
                Value = new MirConstantValue.StringValue("hello")
            },
            name: "selected");
        var unused = BuildFunction(
            new TypeId(BaseTypes.IntId),
            locals: [],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = new TypeId(BaseTypes.IntId),
                Value = new MirConstantValue.IntValue(1)
            },
            name: "unused");
        var module = new MirModule
        {
            Name = "selected_module",
            Functions = [selected, unused]
        };

        var full = new MirToLlvmConverter().Convert(module);
        var partial = new MirToLlvmConverter().ConvertSelectedFunctions(
            module,
            new HashSet<string>(StringComparer.Ordinal) { "name:selected" });
        var fullSelected = LlvmFunctionFingerprintBuilder.BuildFragment(
            SingleFunctionBySourceName(full, "selected"));
        var partialSelected = LlvmFunctionFingerprintBuilder.BuildFragment(
            SingleFunctionBySourceName(partial, "selected"));

        Assert.Equal(fullSelected.IrFragment, partialSelected.IrFragment);
        Assert.DoesNotContain(partial.Functions, function => function.Name.Contains("unused", StringComparison.Ordinal));
        Assert.NotEmpty(partial.Globals);
    }

    [Fact]
    public void ConvertFunction_DefaultIntReturnInStructFunction_ReportsDiagnosticAndEmitsUnreachable()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var tupleType = new TypeId(9902);
        var func = new MirFunc
        {
            Name = "default_tuple",
            ReturnType = tupleType,
            EntryBlockId = new BlockId { Value = 1 },
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Terminator = new MirReturn
                    {
                        Value = new MirConstant
                        {
                            TypeId = intType,
                            Value = new MirConstantValue.IntValue(0)
                        }
                    }
                }
            ]
        };

        var converter = new MirToLlvmConverter();
        var llvmModule = converter.Convert(new MirModule
        {
            Name = "default_return",
            DynamicTypeKeys = new Dictionary<int, string>
            {
                [tupleType.Value] = $"Tuple(T{intType.Value},T{boolType.Value})"
            },
            Functions = [func]
        });

        var llvmFunc = Assert.Single(llvmModule.Functions);
        Assert.IsType<LlvmUnreachable>(Assert.Single(llvmFunc.BasicBlocks).Terminator);
        Assert.Contains(converter.Diagnostics, diagnostic => diagnostic.Code == "E5204");
    }

    [Fact]
    public void ConvertFunction_UnitReturnInPointerFunction_ReportsDiagnosticAndEmitsUnreachable()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var func = new MirFunc
        {
            Name = "default_ptr",
            ReturnType = stringType,
            EntryBlockId = new BlockId { Value = 1 },
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Terminator = new MirReturn
                    {
                        Value = new MirConstant
                        {
                            TypeId = new TypeId(BaseTypes.UnitId),
                            Value = new MirConstantValue.UnitValue()
                        }
                    }
                }
            ]
        };

        var converter = new MirToLlvmConverter();
        var llvmFunc = converter.ConvertFunction(func);
        Assert.IsType<LlvmUnreachable>(Assert.Single(llvmFunc.BasicBlocks).Terminator);
        Assert.Contains(converter.Diagnostics, diagnostic => diagnostic.Code == "E5204");
    }

    [Fact]
    public void ConvertFunction_MissingTerminator_ReportsDiagnosticAndEmitsUnreachable()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var func = new MirFunc
        {
            Name = "missing_terminator",
            ReturnType = intType,
            EntryBlockId = new BlockId { Value = 1 },
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true
                }
            ]
        };

        var converter = new MirToLlvmConverter();
        var llvmFunc = converter.ConvertFunction(func);

        Assert.IsType<LlvmUnreachable>(Assert.Single(llvmFunc.BasicBlocks).Terminator);
        Assert.Contains(converter.Diagnostics, diagnostic => diagnostic.Code == "E5202");
    }

    [Fact]
    public void ConvertFunction_IntComparisonWithUntypedConstant_DoesNotEmitPtrToIntCast()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var input = LocalPlace(1, intType);
        var result = LocalPlace(2, boolType);

        var func = BuildFunction(
            boolType,
            locals:
            [
                new MirLocal { Id = input.Local, Name = "x", TypeId = intType, IsParameter = true },
                new MirLocal { Id = result.Local, Name = "flag", TypeId = boolType }
            ],
            instructions:
            [
                new MirBinOp
                {
                    Target = result,
                    Operator = BinaryOp.Eq,
                    Left = input,
                    Right = new MirConstant
                    {
                        TypeId = TypeId.None,
                        Value = new MirConstantValue.IntValue(10)
                    }
                }
            ],
            returnValue: result);

        var llvmFunc = new MirToLlvmConverter().ConvertFunction(func);
        var entry = Assert.Single(llvmFunc.BasicBlocks);

        Assert.DoesNotContain(entry.Instructions.OfType<LlvmCast>(), cast => cast.Op == "ptrtoint");

        var icmp = Assert.Single(entry.Instructions.OfType<LlvmIcmp>());
        var right = Assert.IsType<LlvmConstant>(icmp.Right);
        var rightType = Assert.IsType<LlvmIntType>(right.Type);
        Assert.Equal(64, rightType.Bits);
        Assert.Equal(10L, Convert.ToInt64(right.Value));
    }

    [Fact]
    public void ResolveFunctionInstanceName_DoesNotUseSubstringSourceNameFallback()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var callee = BuildFunction(
            intType,
            locals:
            [
                new MirLocal { Id = new LocalId { Value = 1 }, Name = "x", TypeId = intType, IsParameter = true }
            ],
            instructions: [],
            returnValue: new MirPlace
            {
                Kind = PlaceKind.Local,
                Local = new LocalId { Value = 1 },
                TypeId = intType
            },
            name: "map",
            symbolId: new SymbolId(9910));

        var converter = new MirToLlvmConverter();
        _ = converter.Convert(new MirModule
        {
            Name = "source_name_resolution",
            Functions = [callee]
        });

        var preferredType = new LlvmFunctionType
        {
            ReturnType = LlvmIntType.I64,
            ParameterTypes = [LlvmIntType.I64]
        };

        Assert.False(InvokePrivateFunctionNameResolver(
            converter,
            "TryResolveFunctionInstanceNameBySignature",
            "ap",
            preferredType,
            out _));
        Assert.False(InvokePrivateFunctionNameResolver(
            converter,
            "TryResolveFunctionInstanceNameByRegisteredType",
            "ap",
            preferredType,
            out _));
        Assert.False(InvokePrivateFunctionNameResolver(
            converter,
            "TryResolveFunctionInstanceNameByLlvmName",
            "idos_map",
            preferredType,
            out _));
    }

    [Fact]
    public void ConvertFunction_BinOpUnsupportedTargetOperand_ReportsDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var left = LocalPlace(1, intType);
        var right = LocalPlace(2, intType);
        var invalidTarget = new MirConstant
        {
            Span = new SourceSpan(new SourceLocation(12, 1, 4), 1),
            TypeId = intType,
            Value = new MirConstantValue.IntValue(0)
        };

        var func = BuildFunction(
            intType,
            locals:
            [
                new MirLocal { Id = left.Local, Name = "left", TypeId = intType, IsParameter = true },
                new MirLocal { Id = right.Local, Name = "right", TypeId = intType, IsParameter = true }
            ],
            instructions:
            [
                new MirBinOp
                {
                    Target = invalidTarget,
                    Operator = BinaryOp.Add,
                    Left = left,
                    Right = right
                }
            ],
            returnValue: left,
            name: "unsupported_target_operand");

        var converter = new MirToLlvmConverter();
        _ = converter.ConvertFunction(func);

        Assert.Contains(
            converter.Diagnostics,
            diagnostic => diagnostic.Code == "E5306" &&
                          diagnostic.Message.Contains("Unsupported MIR target operand", StringComparison.Ordinal) &&
                          diagnostic.Notes.Contains("expected MirPlace or MirTemp target operand before LLVM lowering"));
    }

    [Fact]
    public void ConvertFunction_UnsupportedPlaceKind_ReportsDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var source = new MirPlace
        {
            Kind = (PlaceKind)999,
            Span = new SourceSpan(new SourceLocation(20, 2, 8), 1),
            TypeId = intType
        };
        var target = LocalPlace(1, intType);

        var func = BuildFunction(
            intType,
            locals:
            [
                new MirLocal { Id = target.Local, Name = "target", TypeId = intType }
            ],
            instructions:
            [
                new MirCopy
                {
                    Source = source,
                    Target = target
                }
            ],
            returnValue: target,
            name: "unsupported_place_kind");

        var converter = new MirToLlvmConverter();
        _ = converter.ConvertFunction(func);

        Assert.Contains(
            converter.Diagnostics,
            diagnostic => diagnostic.Code == "E5307" &&
                          diagnostic.Message.Contains("Unsupported MIR place kind", StringComparison.Ordinal) &&
                          diagnostic.Notes.Contains("expected Local, Deref, Field, or Index place before LLVM lowering"));
    }

    [Fact]
    public void ConvertFunction_MoveValue_InvalidatesSourceAlias()
    {
        var affineType = new TypeId(101);
        var sourcePlace = LocalPlace(1, affineType);
        var targetPlace = LocalPlace(2, affineType);

        var func = BuildFunction(
            affineType,
            locals:
            [
                new MirLocal { Id = sourcePlace.Local, Name = "src", TypeId = affineType, IsParameter = true },
                new MirLocal { Id = targetPlace.Local, Name = "dst", TypeId = affineType }
            ],
            instructions:
            [
                new MirMove
                {
                    Source = sourcePlace,
                    Target = targetPlace
                }
            ],
            returnValue: sourcePlace);

        var llvmFunc = new MirToLlvmConverter().ConvertFunction(func);
        var entry = Assert.Single(llvmFunc.BasicBlocks);
        Assert.Empty(entry.Instructions.OfType<LlvmCall>());

        // After move, the source local retains its original SSA name — no alias
        // seed is emitted (emitting one in a branch arm would cause dominance
        // violations across merge points).
        var ret = Assert.IsType<LlvmRet>(entry.Terminator);
        var retLocal = Assert.IsType<LlvmLocal>(ret.Value);
        Assert.Equal("src", retLocal.Name);
    }

    [Fact]
    public void ConvertFunction_DropManagedAlias_EmitsDecrefAndClearsLocalAlias()
    {
        var managedType = new TypeId(120);
        var sourcePlace = LocalPlace(1, managedType);
        var targetPlace = LocalPlace(2, managedType);

        var func = BuildFunction(
            managedType,
            locals:
            [
                new MirLocal { Id = sourcePlace.Local, Name = "src", TypeId = managedType, IsParameter = true },
                new MirLocal { Id = targetPlace.Local, Name = "dst", TypeId = managedType }
            ],
            instructions:
            [
                new MirMove
                {
                    Source = sourcePlace,
                    Target = targetPlace
                },
                new MirDrop
                {
                    Value = targetPlace
                }
            ],
            returnValue: targetPlace);

        var llvmFunc = new MirToLlvmConverter().ConvertFunction(func);
        var entry = Assert.Single(llvmFunc.BasicBlocks);

        var decrefCall = Assert.Single(entry.Instructions.OfType<LlvmCall>());
        var decrefGlobal = Assert.IsType<LlvmGlobal>(decrefCall.Function);
        Assert.Equal("eidos_decref_local", decrefGlobal.Name);

        // After move+drop, the local retains its original SSA name — no alias
        // seed is emitted (emitting one in a branch arm would cause dominance
        // violations across merge points).  The target inherits the source's
        // name "src" via AssignPlaceFromValue, which persists after the no-op
        // invalidation.
        var ret = Assert.IsType<LlvmRet>(entry.Terminator);
        var retLocal = Assert.IsType<LlvmLocal>(ret.Value);
        Assert.Equal("src", retLocal.Name);
    }

    [Fact]
    public void ConvertFunction_DropInsertionOutput_EmitsNativeDecrefSmoke()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var stringType = new TypeId(BaseTypes.StringId);
        var textPlace = LocalPlace(1, stringType);
        var func = BuildFunction(
            intType,
            locals:
            [
                new MirLocal { Id = textPlace.Local, Name = "text", TypeId = stringType, IsParameter = true }
            ],
            instructions:
            [
                new MirCall
                {
                    Function = new MirFunctionRef
                    {
                        Name = "observe",
                        TypeId = intType
                    },
                    Arguments = [textPlace]
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(0)
            });
        var optimizedModule = new DropInsertionPass().Run(new MirModule
        {
            Name = "drop_smoke",
            Functions = [func]
        });
        var optimizedFunc = Assert.Single(optimizedModule.Functions);

        var llvmFunc = new MirToLlvmConverter().ConvertFunction(optimizedFunc);
        var calls = Assert.Single(llvmFunc.BasicBlocks).Instructions.OfType<LlvmCall>().ToList();

        Assert.Contains(
            calls,
            call => call.Function is LlvmGlobal { Name: "eidos_decref_local" });
    }

    [Fact]
    public void ConvertFunction_CallWithTarget_UsesTargetTypeAsReturnType()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var argumentPlace = LocalPlace(1, intType);
        var resultPlace = LocalPlace(2, intType);
        var calleeSymbol = new SymbolId(500);

        var func = BuildFunction(
            intType,
            locals:
            [
                new MirLocal { Id = argumentPlace.Local, Name = "x", TypeId = intType, IsParameter = true },
                new MirLocal { Id = resultPlace.Local, Name = "ret", TypeId = intType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = resultPlace,
                    Function = new MirFunctionRef
                    {
                        SymbolId = calleeSymbol,
                        Name = "callee",
                        TypeId = intType
                    },
                    Arguments = [argumentPlace]
                }
            ],
            returnValue: resultPlace);

        var llvmFunc = new MirToLlvmConverter().ConvertFunction(func);
        var entry = Assert.Single(llvmFunc.BasicBlocks);
        var call = Assert.Single(entry.Instructions.OfType<LlvmCall>());

        var returnType = Assert.IsType<LlvmIntType>(call.ReturnType);
        Assert.Equal(64, returnType.Bits);
        Assert.Contains("call i64 @eidos_callee", call.ToIrString());
    }

    [Fact]
    public void Convert_ModuleCallStringEquals_UsesRuntimeDeclarationWithBoolReturn()
    {
        var boolType = new TypeId(BaseTypes.BoolId);
        var stringType = new TypeId(BaseTypes.StringId);
        var leftPlace = LocalPlace(1, stringType);
        var rightPlace = LocalPlace(2, stringType);
        var resultPlace = LocalPlace(3, boolType);

        var caller = BuildFunction(
            boolType,
            locals:
            [
                new MirLocal { Id = leftPlace.Local, Name = "a", TypeId = stringType, IsParameter = true },
                new MirLocal { Id = rightPlace.Local, Name = "b", TypeId = stringType, IsParameter = true },
                new MirLocal { Id = resultPlace.Local, Name = "eq", TypeId = boolType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = resultPlace,
                    Function = new MirFunctionRef
                    {
                        Name = "string_equals",
                        SymbolId = SymbolId.None,
                        TypeId = boolType
                    },
                    Arguments = [leftPlace, rightPlace]
                }
            ],
            returnValue: resultPlace,
            name: "string_equals_runtime",
            symbolId: new SymbolId(1901));

        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "test_string_equals_runtime",
            Functions = [caller]
        });

        var llvmCaller = SingleFunctionBySourceName(llvmModule, "string_equals_runtime");
        var call = Assert.Single(llvmCaller.BasicBlocks.Single().Instructions.OfType<LlvmCall>());
        Assert.Contains("@eidos_string_equals", call.ToIrString(), StringComparison.Ordinal);

        var returnType = Assert.IsType<LlvmIntType>(call.ReturnType);
        Assert.Equal(1, returnType.Bits);

        var declaration = Assert.Single(llvmModule.Declarations, item => item.Name == "eidos_string_equals");
        var declarationType = Assert.IsType<LlvmFunctionType>(declaration.Type);
        var declarationReturnType = Assert.IsType<LlvmIntType>(declarationType.ReturnType);
        Assert.Equal(1, declarationReturnType.Bits);
        Assert.Equal(2, declarationType.ParameterTypes.Count);
        Assert.All(declarationType.ParameterTypes, parameter => Assert.IsType<LlvmPointerType>(parameter));
    }

    [Fact]
    public void ConvertFunction_UnknownMirInstruction_ReportsDiagnostic()
    {
        var unitType = new TypeId(BaseTypes.UnitId);
        var span = new SourceSpan(new SourceLocation(3, 1, 2), 4);

        var func = BuildFunction(
            unitType,
            locals: [],
            instructions:
            [
                new UnknownMirInstruction
                {
                    Span = span
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            });

        var converter = new MirToLlvmConverter();
        _ = converter.ConvertFunction(func);

        Assert.Contains(
            converter.Diagnostics,
            diagnostic => diagnostic.Code == "E5200" &&
                          diagnostic.Message.Contains("UnknownMirInstruction"));
    }

    [Fact]
    public void ConvertFunction_UnknownMirTerminator_ReportsDiagnosticAndUsesUnreachable()
    {
        var unitType = new TypeId(BaseTypes.UnitId);
        var span = new SourceSpan(new SourceLocation(8, 2, 1), 3);

        var func = new MirFunc
        {
            Name = "test_unknown_term",
            ReturnType = unitType,
            EntryBlockId = new BlockId { Value = 1 },
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Terminator = new UnknownMirTerminator
                    {
                        Span = span
                    }
                }
            ]
        };

        var converter = new MirToLlvmConverter();
        var llvmFunc = converter.ConvertFunction(func);

        Assert.Contains(
            converter.Diagnostics,
            diagnostic => diagnostic.Code == "E5201" &&
                          diagnostic.Message.Contains("UnknownMirTerminator"));
        Assert.IsType<LlvmUnreachable>(Assert.Single(llvmFunc.BasicBlocks).Terminator);
    }

    [Fact]
    public void ConvertFunction_UnknownValidTypeId_ReportsOpaquePointerFallbackDiagnostic()
    {
        var unknownType = new TypeId(990_990);
        var sourcePlace = LocalPlace(1, unknownType);
        var targetPlace = LocalPlace(2, unknownType);
        var func = BuildFunction(
            unknownType,
            locals:
            [
                new MirLocal { Id = sourcePlace.Local, Name = "src", TypeId = unknownType, IsParameter = true },
                new MirLocal { Id = targetPlace.Local, Name = "dst", TypeId = unknownType }
            ],
            instructions:
            [
                new MirCopy
                {
                    Source = sourcePlace,
                    Target = targetPlace
                }
            ],
            returnValue: targetPlace,
            name: "unknown_type_fallback");

        var converter = new MirToLlvmConverter();
        _ = converter.ConvertFunction(func);

        Assert.Contains(
            converter.Diagnostics,
            diagnostic => diagnostic.Code == "E5304" &&
                          diagnostic.Message.Contains("opaque pointer", StringComparison.Ordinal) &&
                          diagnostic.Notes.Contains("context: materialize local"));
    }

    [Fact]
    public void ConvertFunction_UnknownLoadResultType_ReportsOpaquePointerFallbackDiagnostic()
    {
        var unitType = new TypeId(BaseTypes.UnitId);
        var intType = new TypeId(BaseTypes.IntId);
        var unknownType = new TypeId(990_991);
        var targetPlace = LocalPlace(1, unknownType);
        var func = BuildFunction(
            unitType,
            locals:
            [
                new MirLocal { Id = targetPlace.Local, Name = "dst", TypeId = unknownType }
            ],
            instructions:
            [
                new MirLoad
                {
                    Target = targetPlace,
                    Source = new MirConstant
                    {
                        TypeId = intType,
                        Value = new MirConstantValue.IntValue(0)
                    }
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "unknown_load_result_type");

        var converter = new MirToLlvmConverter();
        _ = converter.ConvertFunction(func);

        Assert.Contains(
            converter.Diagnostics,
            diagnostic => diagnostic.Code == "E5304" &&
                          diagnostic.Message.Contains("opaque pointer", StringComparison.Ordinal) &&
                          diagnostic.Notes.Contains("context: load result"));
    }

    [Fact]
    public void ConvertFunction_UnknownTempOperandType_ReportsOpaquePointerFallbackDiagnostic()
    {
        var unitType = new TypeId(BaseTypes.UnitId);
        var unknownType = new TypeId(990_992);
        var targetPlace = LocalPlace(1, unitType);
        var func = BuildFunction(
            unitType,
            locals:
            [
                new MirLocal { Id = targetPlace.Local, Name = "dst", TypeId = unitType }
            ],
            instructions:
            [
                new MirStore
                {
                    Target = targetPlace,
                    Value = new MirTemp
                    {
                        Id = new TempId { Value = 7 },
                        TypeId = unknownType
                    }
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "unknown_temp_operand_type");

        var converter = new MirToLlvmConverter();
        _ = converter.ConvertFunction(func);

        Assert.Contains(
            converter.Diagnostics,
            diagnostic => diagnostic.Code == "E5304" &&
                          diagnostic.Message.Contains("opaque pointer", StringComparison.Ordinal) &&
                          diagnostic.Notes.Contains("context: temp operand"));
    }

    [Fact]
    public void ConvertFunction_CStructAccessorMissingTarget_ReportsDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var rawPtrType = new TypeId(BaseTypes.RawPtrId);
        var basePtr = LocalPlace(1, rawPtrType);
        var span = new SourceSpan(new SourceLocation(21, 2, 8), 5);
        var func = BuildFunction(
            intType,
            locals:
            [
                new MirLocal { Id = basePtr.Local, Name = "base", TypeId = rawPtrType, IsParameter = true }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = null,
                    Function = new MirFunctionRef
                    {
                        Name = "read_field",
                        TypeId = intType,
                        Span = span
                    },
                    Arguments =
                    [
                        basePtr
                    ],
                    Span = span
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(0)
            },
            name: "cstruct_missing_target");

        var converter = new MirToLlvmConverter();
        converter.SetCStructAccessors(new Dictionary<string, CStructAccessorInfo>
        {
            ["read_field"] = new()
            {
                FieldOffset = 0,
                FieldTypeId = BaseTypes.IntId,
                IsGetter = true
            }
        });

        _ = converter.ConvertFunction(func);

        Assert.Contains(
            converter.Diagnostics,
            diagnostic => diagnostic.Code == "E5306" &&
                          diagnostic.Message.Contains("Missing MIR target place", StringComparison.Ordinal) &&
                          diagnostic.Notes.Contains("expected MirPlace target operand before LLVM lowering"));
    }

    private static MirFunc BuildFunction(
        TypeId returnType,
        List<MirLocal> locals,
        List<MirInstruction> instructions,
        MirOperand returnValue,
        string name = "test",
        SymbolId symbolId = default,
        FunctionId? functionId = null)
    {
        var effectiveSymbolId = symbolId.Value == 0 && functionId == null ? SymbolId.None : symbolId;
        return new MirFunc
        {
            Name = name,
            ReturnType = returnType,
            EntryBlockId = new BlockId { Value = 1 },
            SymbolId = effectiveSymbolId,
            FunctionId = functionId ?? (effectiveSymbolId.IsValid
                ? new FunctionId
                {
                    SymbolId = effectiveSymbolId,
                    Name = name,
                    QualifiedName = name
                }
                : new FunctionId()),
            Locals = locals,
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions = instructions,
                    Terminator = new MirReturn
                    {
                        Value = returnValue
                    }
                }
            ]
        };
    }

    private static MirPlace LocalPlace(int id, TypeId typeId)
    {
        return new MirPlace
        {
            Kind = PlaceKind.Local,
            Local = new LocalId { Value = id },
            TypeId = typeId
        };
    }

    private static LlvmFunction SingleFunctionBySourceName(LlvmModule module, string sourceName)
    {
        var baseName = new NameMangler().MangleFunctionName("", sourceName);
        return Assert.Single(
            module.Functions,
            function => string.Equals(function.Name, baseName, StringComparison.Ordinal) ||
                        function.Name.StartsWith($"{baseName}_i", StringComparison.Ordinal));
    }

    private static string SingleFunctionNameBySourceName(LlvmModule module, string sourceName)
    {
        return SingleFunctionBySourceName(module, sourceName).Name;
    }

    private static bool InvokePrivateFunctionNameResolver(
        MirToLlvmConverter converter,
        string methodName,
        string sourceName,
        LlvmFunctionType preferredType,
        out string llvmName)
    {
        var method = typeof(MirToLlvmConverter).GetMethod(
            methodName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        object?[] arguments = [sourceName, preferredType, string.Empty];
        var resolved = Assert.IsType<bool>(method.Invoke(converter, arguments));
        llvmName = Assert.IsType<string>(arguments[2]);
        return resolved;
    }

    private sealed record UnknownMirInstruction : MirInstruction;

    private sealed record UnknownMirTerminator : MirTerminator;
}
