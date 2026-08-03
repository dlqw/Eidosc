using Eidosc.CodeGen.Llvm;
using Xunit;

namespace Eidosc.Tests.Unit.Llvm;

public sealed class LlvmFunctionAttributeInferenceTests
{
    [Fact]
    public void Apply_InfersNounwindByCallGraphFixedPoint()
    {
        var leaf = Function("leaf");
        var runtimeWrapper = Function("runtime_wrapper", DirectCall("eidos_runtime_safe"));
        var recursiveLeft = Function("recursive_left", DirectCall("recursive_right"));
        var recursiveRight = Function("recursive_right", DirectCall("recursive_left"));
        var ffiBridge = Function("ffi_bridge", DirectCall("ffi_may_unwind"));
        var ffiCaller = Function("ffi_caller", DirectCall("ffi_bridge"));
        var indirectCaller = Function(
            "indirect_caller",
            new LlvmCall
            {
                Function = new LlvmLocal
                {
                    Name = "callback",
                    Type = LlvmPointerType.VoidPtr()
                },
                ReturnType = LlvmVoidType.Instance
            });
        var module = new LlvmModule
        {
            Functions =
            [
                leaf,
                runtimeWrapper,
                recursiveLeft,
                recursiveRight,
                ffiBridge,
                ffiCaller,
                indirectCaller
            ],
            Declarations =
            [
                Declaration("eidos_runtime_safe", LlvmDeclarationOrigin.RuntimeIntrinsic),
                Declaration("ffi_may_unwind", LlvmDeclarationOrigin.ExternalFfi)
            ],
            AttributeGroups =
            [
                new LlvmAttributeGroup
                {
                    Id = 4,
                    Attributes = ["alwaysinline"]
                }
            ]
        };

        LlvmFunctionAttributeInference.Apply(module);
        LlvmFunctionAttributeInference.Apply(module);

        var nounwindGroup = Assert.Single(
            module.AttributeGroups,
            static group => group.Attributes.SequenceEqual(["nounwind"]));
        Assert.Equal(5, nounwindGroup.Id);
        AssertNounwind(leaf, nounwindGroup.Id);
        AssertNounwind(runtimeWrapper, nounwindGroup.Id);
        AssertNounwind(recursiveLeft, nounwindGroup.Id);
        AssertNounwind(recursiveRight, nounwindGroup.Id);
        AssertNoNounwind(ffiBridge, nounwindGroup.Id);
        AssertNoNounwind(ffiCaller, nounwindGroup.Id);
        AssertNoNounwind(indirectCaller, nounwindGroup.Id);
    }

    [Fact]
    public void Apply_AnnotatesSyntheticScalarParametersButPreservesRuntimeWordBoundary()
    {
        var synthetic = Function("eidos_closure_invoke_1");
        synthetic.Parameters.Add(new LlvmParameter { Name = "count", Type = LlvmIntType.I64 });
        synthetic.Parameters.Add(new LlvmParameter { Name = "ratio", Type = LlvmFloatType.Double });
        synthetic.Parameters.Add(new LlvmParameter { Name = "payload", Type = LlvmPointerType.VoidPtr() });
        var runtimeWord = new LlvmFunction
        {
            Name = "runtime_word",
            ReturnType = LlvmIntType.I64,
            SuppressScalarNoundefParameters = true,
            Parameters = [new LlvmParameter { Name = "word", Type = LlvmIntType.I64 }],
            BasicBlocks =
            [
                new LlvmBasicBlock
                {
                    Label = "entry",
                    Terminator = new LlvmRet
                    {
                        Value = new LlvmConstant { Value = 0L, Type = LlvmIntType.I64 }
                    }
                }
            ]
        };
        var module = new LlvmModule { Functions = [synthetic, runtimeWord] };

        LlvmFunctionAttributeInference.Apply(module);

        Assert.Contains(LlvmParameterAttribute.Noundef, synthetic.Parameters[0].Attributes);
        Assert.Contains(LlvmParameterAttribute.Noundef, synthetic.Parameters[1].Attributes);
        Assert.DoesNotContain(LlvmParameterAttribute.Noundef, synthetic.Parameters[2].Attributes);
        Assert.DoesNotContain(LlvmParameterAttribute.Noundef, runtimeWord.Parameters[0].Attributes);
    }

    [Fact]
    public void FunctionFragments_PreserveCallingConventionParameterAndAllFunctionAttributes()
    {
        var function = new LlvmFunction
        {
            Name = "helper",
            ReturnType = LlvmIntType.I64,
            Linkage = LlvmLinkage.External,
            CallingConvention = "fastcc",
            AttributeIds = [0, 1],
            Parameters =
            [
                new LlvmParameter
                {
                    Name = "value",
                    Type = LlvmIntType.I64,
                    Attributes = [LlvmParameterAttribute.Noundef]
                }
            ],
            BasicBlocks =
            [
                new LlvmBasicBlock
                {
                    Label = "entry",
                    Terminator = new LlvmRet
                    {
                        Value = new LlvmLocal { Name = "value", Type = LlvmIntType.I64 }
                    }
                }
            ]
        };
        var module = new LlvmModule
        {
            Name = "attributes",
            Functions = [function],
            AttributeGroups =
            [
                new LlvmAttributeGroup { Id = 0, Attributes = ["alwaysinline"] },
                new LlvmAttributeGroup { Id = 1, Attributes = ["nounwind"] }
            ]
        };

        var emitted = new LlvmEmitter().Emit(module);
        var fragment = LlvmFunctionFingerprintBuilder.BuildFragment(function);

        const string expectedDefinition =
            "define external fastcc i64 @helper(i64 noundef %value) #0 #1";
        Assert.Contains(expectedDefinition, emitted, StringComparison.Ordinal);
        Assert.Contains(expectedDefinition, fragment.IrFragment, StringComparison.Ordinal);
        Assert.Equal("declare fastcc i64 @helper(i64 noundef) #0 #1", fragment.DeclarationIr);
    }

    [Fact]
    public void FunctionFragments_DeclarationPreservesCallingConventionParameterAndAllFunctionAttributes()
    {
        var declaration = new LlvmFunction
        {
            Name = "helper",
            ReturnType = LlvmIntType.I64,
            CallingConvention = "fastcc",
            AttributeIds = [0, 1],
            Parameters =
            [
                new LlvmParameter
                {
                    Name = "value",
                    Type = LlvmIntType.I64,
                    Attributes = [LlvmParameterAttribute.Noundef]
                }
            ]
        };

        var fragment = LlvmFunctionFingerprintBuilder.BuildFragment(declaration);

        Assert.Equal(
            "declare fastcc i64 @helper(i64 noundef %value) #0 #1" + Environment.NewLine,
            fragment.IrFragment);
    }

    private static LlvmFunction Function(string name, params LlvmInstruction[] instructions) => new()
    {
        Name = name,
        ReturnType = LlvmVoidType.Instance,
        BasicBlocks =
        [
            new LlvmBasicBlock
            {
                Label = "entry",
                Instructions = [.. instructions],
                Terminator = new LlvmRet()
            }
        ]
    };

    private static LlvmCall DirectCall(string name) => new()
    {
        Function = new LlvmGlobal
        {
            Name = name,
            Type = new LlvmFunctionType { ReturnType = LlvmVoidType.Instance }
        },
        ReturnType = LlvmVoidType.Instance
    };

    private static LlvmDeclaration Declaration(string name, LlvmDeclarationOrigin origin) => new()
    {
        Name = name,
        Origin = origin,
        Type = new LlvmFunctionType { ReturnType = LlvmVoidType.Instance }
    };

    private static void AssertNounwind(LlvmFunction function, int nounwindAttributeId)
    {
        Assert.Equal(1, function.AttributeIds.Count(id => id == nounwindAttributeId));
    }

    private static void AssertNoNounwind(LlvmFunction function, int nounwindAttributeId)
    {
        Assert.DoesNotContain(nounwindAttributeId, function.AttributeIds);
    }
}
