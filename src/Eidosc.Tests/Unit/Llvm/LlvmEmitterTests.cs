using Eidosc.CodeGen.Llvm;
using Xunit;

namespace Eidosc.Tests.Unit.Llvm;

public sealed class LlvmEmitterTests
{
    [Fact]
    public void Emit_BinOpInstruction_DoesNotDuplicateAssignmentPrefix()
    {
        var module = new LlvmModule
        {
            Name = "Test"
        };

        var function = new LlvmFunction
        {
            Name = "foo",
            ReturnType = LlvmIntType.I64,
            Linkage = LlvmLinkage.External
        };

        function.Parameters.Add(new LlvmParameter
        {
            Name = "x",
            Type = LlvmIntType.I64
        });

        var block = new LlvmBasicBlock
        {
            Label = "bb1",
            Terminator = new LlvmRet
            {
                Value = new LlvmLocal
                {
                    Name = "tmp",
                    Type = LlvmIntType.I64
                }
            }
        };

        block.Instructions.Add(new LlvmBinOp
        {
            ResultName = "tmp",
            Op = "add",
            ResultType = LlvmIntType.I64,
            Left = new LlvmLocal
            {
                Name = "x",
                Type = LlvmIntType.I64
            },
            Right = new LlvmConstant
            {
                Value = 1L,
                Type = LlvmIntType.I64
            }
        });

        function.BasicBlocks.Add(block);
        module.Functions.Add(function);

        var ir = new LlvmEmitter().Emit(module);

        Assert.Contains("%tmp = add i64 %x, 1", ir);
        Assert.DoesNotContain("%%tmp", ir);
        Assert.DoesNotContain("= %tmp =", ir);
    }

    [Fact]
    public void Emit_VoidCallWithoutResult_DoesNotEmitAssignment()
    {
        var module = new LlvmModule
        {
            Name = "Test"
        };

        var function = new LlvmFunction
        {
            Name = "foo",
            ReturnType = LlvmVoidType.Instance,
            Linkage = LlvmLinkage.External
        };

        var block = new LlvmBasicBlock
        {
            Label = "bb1",
            Terminator = new LlvmRet()
        };

        block.Instructions.Add(new LlvmCall
        {
            Function = new LlvmGlobal
            {
                Name = "log",
                Type = new LlvmFunctionType
                {
                    ReturnType = LlvmVoidType.Instance,
                    ParameterTypes = [LlvmIntType.I64]
                }
            },
            Arguments =
            [
                new LlvmConstant
                {
                    Value = 1L,
                    Type = LlvmIntType.I64
                }
            ],
            ReturnType = LlvmVoidType.Instance,
            ResultName = "ignored"
        });

        function.BasicBlocks.Add(block);
        module.Functions.Add(function);

        var ir = new LlvmEmitter().Emit(module);

        Assert.Contains("call void @log(i64 1)", ir);
        Assert.DoesNotContain("ignored = call void", ir);
    }

    [Fact]
    public void Emit_TailCall_UsesLlvmTailCallKeyword()
    {
        var call = new LlvmCall
        {
            Function = new LlvmGlobal
            {
                Name = "callee",
                Type = new LlvmFunctionType
                {
                    ReturnType = LlvmIntType.I64,
                    ParameterTypes = [LlvmIntType.I64]
                }
            },
            Arguments =
            [
                new LlvmConstant
                {
                    Value = 42L,
                    Type = LlvmIntType.I64
                }
            ],
            ReturnType = LlvmIntType.I64,
            ResultName = "result",
            IsTailCall = true
        };

        Assert.Equal("%result = tail call i64 @callee(i64 42)", call.ToIrString());
    }

    [Fact]
    public void Emit_MustTailCall_UsesLlvmMustTailKeyword()
    {
        var call = new LlvmCall
        {
            Function = new LlvmGlobal
            {
                Name = "callee",
                Type = new LlvmFunctionType
                {
                    ReturnType = LlvmIntType.I64,
                    ParameterTypes = [LlvmIntType.I64]
                }
            },
            Arguments =
            [
                new LlvmConstant
                {
                    Value = 42L,
                    Type = LlvmIntType.I64
                }
            ],
            ReturnType = LlvmIntType.I64,
            ResultName = "result",
            TailCallKind = LlvmTailCallKind.MustTail
        };

        Assert.Equal("%result = musttail call i64 @callee(i64 42)", call.ToIrString());
    }

    [Fact]
    public void Emit_FunctionDeclaration_UsesDeclareReturnAndParameterSignature()
    {
        var module = new LlvmModule
        {
            Name = "Test"
        };
        module.Declarations.Add(new LlvmDeclaration
        {
            Name = "eidos_alloc",
            Type = new LlvmFunctionType
            {
                ReturnType = LlvmPointerType.VoidPtr(),
                ParameterTypes = [LlvmIntType.I64, LlvmIntType.I32]
            }
        });

        var ir = new LlvmEmitter().Emit(module);

        Assert.Contains("declare ptr @eidos_alloc(i64, i32)", ir);
        Assert.DoesNotContain("declare ptr (i64, i32) @eidos_alloc", ir);
    }

    [Fact]
    public void Emit_StructGEP_EmitsTypeDefinitionAndFieldGEP()
    {
        var structType = new LlvmStructType
        {
            Name = "eidos_Option_Int",
            IsLiteral = false,
            Fields = [LlvmIntType.I64, LlvmIntType.I64] // tag + value
        };

        var module = new LlvmModule
        {
            Name = "Test"
        };

        var function = new LlvmFunction
        {
            Name = "get_field",
            ReturnType = LlvmIntType.I64,
            Linkage = LlvmLinkage.External,
            Parameters =
            [
                new LlvmParameter { Name = "obj", Type = LlvmPointerType.VoidPtr() }
            ]
        };

        var block = new LlvmBasicBlock
        {
            Label = "entry",
            Terminator = new LlvmRet
            {
                Value = new LlvmLocal { Name = "val", Type = LlvmIntType.I64 }
            }
        };

        // Struct field GEP: get field at index 1 (value field, after tag at index 0)
        block.Instructions.Add(new LlvmGetElementPtr
        {
            Pointer = new LlvmLocal { Name = "obj", Type = LlvmPointerType.VoidPtr() },
            StructType = structType,
            StructFieldIndex = 1,
            ResultName = "field_ptr"
        });

        block.Instructions.Add(new LlvmLoad
        {
            Pointer = new LlvmLocal { Name = "field_ptr", Type = LlvmPointerType.VoidPtr() },
            LoadType = LlvmIntType.I64,
            ResultName = "val"
        });

        function.BasicBlocks.Add(block);
        module.Functions.Add(function);

        var ir = new LlvmEmitter().Emit(module);

        // Should contain the type definition
        Assert.Contains("%struct.eidos_Option_Int = type { i64, i64 }", ir);
        // Should contain struct-typed GEP
        Assert.Contains("getelementptr %struct.eidos_Option_Int, ptr %obj, i32 0, i32 1", ir);
    }

    [Fact]
    public void Emit_ByteOffsetGEP_DoesNotEmitTypeDefinition()
    {
        var module = new LlvmModule
        {
            Name = "Test"
        };

        var function = new LlvmFunction
        {
            Name = "get_byte_offset",
            ReturnType = LlvmIntType.I64,
            Linkage = LlvmLinkage.External,
            Parameters =
            [
                new LlvmParameter { Name = "obj", Type = LlvmPointerType.VoidPtr() }
            ]
        };

        var block = new LlvmBasicBlock
        {
            Label = "entry",
            Terminator = new LlvmRet
            {
                Value = new LlvmLocal { Name = "val", Type = LlvmIntType.I64 }
            }
        };

        // Byte-offset GEP (fallback path)
        block.Instructions.Add(new LlvmGetElementPtr
        {
            Pointer = new LlvmLocal { Name = "obj", Type = LlvmPointerType.VoidPtr() },
            ElementType = LlvmIntType.I8,
            Index = new LlvmConstant { Value = 8L, Type = LlvmIntType.I64 },
            ResultName = "field_ptr"
        });

        block.Instructions.Add(new LlvmLoad
        {
            Pointer = new LlvmLocal { Name = "field_ptr", Type = LlvmPointerType.VoidPtr() },
            LoadType = LlvmIntType.I64,
            ResultName = "val"
        });

        function.BasicBlocks.Add(block);
        module.Functions.Add(function);

        var ir = new LlvmEmitter().Emit(module);

        // Should NOT contain any struct type definitions
        Assert.DoesNotContain("%struct.", ir);
        // Should contain byte-offset GEP
        Assert.Contains("getelementptr i8, ptr %obj, i64 8", ir);
    }
}
