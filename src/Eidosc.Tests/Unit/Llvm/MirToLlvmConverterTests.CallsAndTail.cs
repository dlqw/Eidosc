using Eidosc;
using Eidosc.CodeGen.Llvm;
using Eidosc.Mir;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Llvm;

public partial class MirToLlvmConverterTests
{
    [Fact]
    public void ConvertFunction_TailCallWithMatchingSignature_EmitsMustTail()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var argumentPlace = LocalPlace(1, intType);
        var resultPlace = LocalPlace(2, intType);
        var calleeSymbol = new SymbolId(510);

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
                    Arguments = [argumentPlace],
                    IsTailCall = true
                }
            ],
            returnValue: resultPlace);

        var llvmFunc = new MirToLlvmConverter().ConvertFunction(func);
        var entry = Assert.Single(llvmFunc.BasicBlocks);
        var call = Assert.Single(entry.Instructions.OfType<LlvmCall>());

        Assert.Equal(LlvmTailCallKind.MustTail, call.TailCallKind);
        Assert.Contains("musttail call i64 @eidos_callee", call.ToIrString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertFunction_TailCallWithMismatchedSignature_EmitsTailHint()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var firstArgument = LocalPlace(1, intType);
        var secondArgument = LocalPlace(2, intType);
        var resultPlace = LocalPlace(3, intType);
        var calleeSymbol = new SymbolId(511);

        var func = BuildFunction(
            intType,
            locals:
            [
                new MirLocal { Id = firstArgument.Local, Name = "x", TypeId = intType, IsParameter = true },
                new MirLocal { Id = secondArgument.Local, Name = "y", TypeId = intType, IsParameter = true },
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
                    Arguments = [firstArgument],
                    IsTailCall = true
                }
            ],
            returnValue: resultPlace);

        var llvmFunc = new MirToLlvmConverter().ConvertFunction(func);
        var entry = Assert.Single(llvmFunc.BasicBlocks);
        var call = Assert.Single(entry.Instructions.OfType<LlvmCall>());

        Assert.Equal(LlvmTailCallKind.Tail, call.TailCallKind);
        Assert.Contains("tail call i64 @eidos_callee", call.ToIrString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertFunction_TailCallWithReferenceLikeSignature_EmitsMustTailWhenSignatureMatches()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var argumentPlace = LocalPlace(1, stringType);
        var resultPlace = LocalPlace(2, stringType);
        var calleeSymbol = new SymbolId(512);

        var func = BuildFunction(
            stringType,
            locals:
            [
                new MirLocal { Id = argumentPlace.Local, Name = "x", TypeId = stringType, IsParameter = true },
                new MirLocal { Id = resultPlace.Local, Name = "ret", TypeId = stringType }
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
                        TypeId = stringType
                    },
                    Arguments = [argumentPlace],
                    IsTailCall = true
                }
            ],
            returnValue: resultPlace);

        var llvmFunc = new MirToLlvmConverter().ConvertFunction(func);
        var entry = Assert.Single(llvmFunc.BasicBlocks);
        var call = Assert.Single(entry.Instructions.OfType<LlvmCall>());

        Assert.Equal(LlvmTailCallKind.MustTail, call.TailCallKind);
        Assert.Contains("musttail call ptr @eidos_callee", call.ToIrString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_ModuleCallWithoutTarget_UsesCalleeSignatureReturnType()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var calleeSymbol = new SymbolId(700);

        var callee = BuildFunction(
            intType,
            locals: [],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(42)
            },
            name: "callee",
            symbolId: calleeSymbol);

        var caller = BuildFunction(
            unitType,
            locals: [],
            instructions:
            [
                new MirCall
                {
                    Function = new MirFunctionRef
                    {
                        Name = "callee",
                        SymbolId = calleeSymbol,
                        TypeId = TypeId.None
                    },
                    Arguments = []
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "caller",
            symbolId: new SymbolId(701));

        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "test",
            Functions = [callee, caller]
        });

        var llvmCaller = SingleFunctionBySourceName(llvmModule, "caller");
        var call = Assert.Single(llvmCaller.BasicBlocks.Single().Instructions.OfType<LlvmCall>());

        var returnType = Assert.IsType<LlvmIntType>(call.ReturnType);
        Assert.Equal(64, returnType.Bits);
        var calleeName = SingleFunctionNameBySourceName(llvmModule, "callee");
        Assert.Contains($"call i64 @{calleeName}", call.ToIrString());
    }

    [Fact]
    public void Convert_ModuleCallWithStringLiteral_UsesGlobalStringAndPointerCast()
    {
        var unitType = new TypeId(BaseTypes.UnitId);
        var stringType = new TypeId(BaseTypes.StringId);

        var caller = BuildFunction(
            unitType,
            locals: [],
            instructions:
            [
                new MirCall
                {
                    Function = new MirFunctionRef
                    {
                        Name = "print_string",
                        TypeId = TypeId.None
                    },
                    Arguments =
                    [
                        new MirConstant
                        {
                            TypeId = stringType,
                            Value = new MirConstantValue.StringValue("int")
                        }
                    ]
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "caller",
            symbolId: new SymbolId(801));

        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "test",
            Functions = [caller]
        });

        var stringGlobal = Assert.Single(llvmModule.Globals);
        Assert.Equal(LlvmLinkage.Private, stringGlobal.Linkage);
        Assert.True(stringGlobal.IsConstant);
        var arrayType = Assert.IsType<LlvmArrayType>(stringGlobal.Type);
        Assert.Equal(4, arrayType.Size);

        var initializer = Assert.IsType<LlvmByteArrayConstant>(stringGlobal.Initializer);
        Assert.Equal(new byte[] { (byte)'i', (byte)'n', (byte)'t', 0 }, initializer.Bytes);

        var llvmCaller = Assert.Single(llvmModule.Functions);
        var entry = Assert.Single(llvmCaller.BasicBlocks);
        var calls = entry.Instructions.OfType<LlvmCall>().ToList();
        Assert.True(calls.Count >= 2);

        var internCall = Assert.Single(calls, call => call.Function is LlvmGlobal { Name: "eidos_string_intern" });
        Assert.Equal(2, internCall.Arguments.Count);
        var internCstrArgRef = Assert.IsType<LlvmInstructionRef>(internCall.Arguments[0]);
        var cast = Assert.IsType<LlvmCast>(internCstrArgRef.Instruction);
        Assert.Equal("bitcast", cast.Op);
        var lengthArg = Assert.IsType<LlvmConstant>(internCall.Arguments[1]);
        Assert.Equal(3L, lengthArg.Value);

        var printCall = Assert.Single(calls, call => call.Function is LlvmGlobal { Name: "eidos_print_string" });
        var printArgRef = Assert.IsType<LlvmInstructionRef>(Assert.Single(printCall.Arguments));
        Assert.Same(internCall, printArgRef.Instruction);

        var ir = new LlvmEmitter().Emit(llvmModule);
        Assert.Contains("private constant [4 x i8] c\"int\\00\"", ir);
        Assert.Contains("call ptr @eidos_string_intern", ir);
        Assert.DoesNotContain("call ptr @eidos_string_from_cstr", ir);
        Assert.DoesNotContain("ptr \"int\"", ir);
    }

    [Fact]
    public void Convert_ModuleWithRepeatedStringLiteral_UsesOneGlobalAndInternCalls()
    {
        var unitType = new TypeId(BaseTypes.UnitId);
        var stringType = new TypeId(BaseTypes.StringId);

        MirFunc BuildPrinter(string name, int symbolId)
        {
            return BuildFunction(
                unitType,
                locals: [],
                instructions:
                [
                    new MirCall
                    {
                        Function = new MirFunctionRef
                        {
                            Name = "print_string",
                            TypeId = TypeId.None
                        },
                        Arguments =
                        [
                            new MirConstant
                            {
                                TypeId = stringType,
                                Value = new MirConstantValue.StringValue("repeat")
                            }
                        ]
                    }
                ],
                returnValue: new MirConstant
                {
                    TypeId = unitType,
                    Value = new MirConstantValue.UnitValue()
                },
                name: name,
                symbolId: new SymbolId(symbolId));
        }

        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "test",
            Functions =
            [
                BuildPrinter("first", 811),
                BuildPrinter("second", 812)
            ]
        });

        var stringGlobal = Assert.Single(llvmModule.Globals);
        var initializer = Assert.IsType<LlvmByteArrayConstant>(stringGlobal.Initializer);
        Assert.Equal(new byte[] { (byte)'r', (byte)'e', (byte)'p', (byte)'e', (byte)'a', (byte)'t', 0 }, initializer.Bytes);

        var internCalls = llvmModule.Functions
            .SelectMany(function => function.BasicBlocks)
            .SelectMany(block => block.Instructions)
            .OfType<LlvmCall>()
            .Where(call => call.Function is LlvmGlobal { Name: "eidos_string_intern" })
            .ToList();
        Assert.Equal(2, internCalls.Count);
        Assert.All(internCalls, call =>
        {
            var lengthArg = Assert.IsType<LlvmConstant>(call.Arguments[1]);
            Assert.Equal(6L, lengthArg.Value);
        });

        var ir = new LlvmEmitter().Emit(llvmModule);
        Assert.Contains("private constant [7 x i8] c\"repeat\\00\"", ir);
        Assert.Equal(2, CountOccurrences(ir, "call ptr @eidos_string_intern"));
        Assert.DoesNotContain("call ptr @eidos_string_from_cstr", ir);
    }

    [Fact]
    public void Convert_ModuleWithSameSourceNameDifferentSignatures_UsesDistinctInstanceNames()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var unitType = new TypeId(BaseTypes.UnitId);

        var intParam = LocalPlace(1, intType);
        var boolParam = LocalPlace(1, boolType);

        var intId = BuildFunction(
            intType,
            locals:
            [
                new MirLocal { Id = intParam.Local, Name = "x", TypeId = intType, IsParameter = true }
            ],
            instructions: [],
            returnValue: intParam,
            name: "id",
            symbolId: new SymbolId(2101));

        var boolId = BuildFunction(
            boolType,
            locals:
            [
                new MirLocal { Id = boolParam.Local, Name = "x", TypeId = boolType, IsParameter = true }
            ],
            instructions: [],
            returnValue: boolParam,
            name: "id",
            symbolId: new SymbolId(2102));

        var callerIntArg = LocalPlace(1, intType);
        var callerBoolArg = LocalPlace(2, boolType);
        var callerIntResult = LocalPlace(3, intType);
        var callerBoolResult = LocalPlace(4, boolType);

        var caller = BuildFunction(
            unitType,
            locals:
            [
                new MirLocal { Id = callerIntArg.Local, Name = "a", TypeId = intType, IsParameter = true },
                new MirLocal { Id = callerBoolArg.Local, Name = "b", TypeId = boolType, IsParameter = true },
                new MirLocal { Id = callerIntResult.Local, Name = "ri", TypeId = intType },
                new MirLocal { Id = callerBoolResult.Local, Name = "rb", TypeId = boolType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerIntResult,
                    Function = new MirFunctionRef
                    {
                        Name = "id",
                        SymbolId = intId.SymbolId,
                        TypeId = intType
                    },
                    Arguments = [callerIntArg]
                },
                new MirCall
                {
                    Target = callerBoolResult,
                    Function = new MirFunctionRef
                    {
                        Name = "id",
                        SymbolId = boolId.SymbolId,
                        TypeId = boolType
                    },
                    Arguments = [callerBoolArg]
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "caller",
            symbolId: new SymbolId(2103));

        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "same_name_instances",
            Functions = [intId, boolId, caller]
        });

        var idFunctions = llvmModule.Functions
            .Where(function => function.Name.StartsWith("eidos_id", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, idFunctions.Count);
        Assert.Equal(2, idFunctions.Select(function => function.Name).Distinct(StringComparer.Ordinal).Count());

        var llvmCaller = SingleFunctionBySourceName(llvmModule, "caller");
        var calls = llvmCaller.BasicBlocks.Single().Instructions.OfType<LlvmCall>().ToList();
        Assert.Equal(2, calls.Count);

        var callNames = calls
            .Select(call => Assert.IsType<LlvmGlobal>(call.Function).Name)
            .ToList();
        Assert.Equal(2, callNames.Distinct(StringComparer.Ordinal).Count());
        Assert.All(callNames, callName => Assert.Contains(callName, idFunctions.Select(function => function.Name)));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }
}
