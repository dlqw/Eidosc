using Eidosc.CodeGen.Llvm;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Llvm;

public sealed class NameManglerTests
{
    [Fact]
    public void MangleFunctionName_DifferentTypeParams_ReturnsDifferentNames()
    {
        var mangler = new NameMangler();

        var intName = mangler.MangleFunctionName("", "map", [BaseTypes.Int]);
        var stringName = mangler.MangleFunctionName("", "map", [BaseTypes.String]);

        Assert.NotEqual(intName, stringName);
    }

    [Fact]
    public void MangleFunctionName_TypeAwareCache_DoesNotPolluteAcrossInstantiations()
    {
        var mangler = new NameMangler();

        var first = mangler.MangleFunctionName("", "fold", [BaseTypes.Int]);
        _ = mangler.MangleFunctionName("", "fold", [BaseTypes.String]);
        var second = mangler.MangleFunctionName("", "fold", [BaseTypes.Int]);

        Assert.Equal(first, second);
    }

    [Fact]
    public void MangleTypeName_DifferentTypeArgs_ReturnsDifferentNames()
    {
        var mangler = new NameMangler();

        var intList = mangler.MangleTypeName("", "Seq", [BaseTypes.Int]);
        var stringList = mangler.MangleTypeName("", "Seq", [BaseTypes.String]);

        Assert.NotEqual(intList, stringList);
    }

    [Fact]
    public void MangleFunctionName_DifferentValueGenericArguments_ReturnsDifferentNames()
    {
        var mangler = new NameMangler();
        var buffer4 = new TyCon
        {
            Name = "Buffer",
            Args = [BaseTypes.Int],
            ValueArgs = [new GenericValueArgument(0, "typed:496e74:int:4", "hash-4", "4", new TypeId(BaseTypes.IntId))]
        };
        var buffer5 = buffer4 with
        {
            ValueArgs = [new GenericValueArgument(0, "typed:496e74:int:5", "hash-5", "5", new TypeId(BaseTypes.IntId))]
        };

        var fourName = mangler.MangleFunctionName("", "read", [buffer4]);
        var fiveName = mangler.MangleFunctionName("", "read", [buffer5]);

        Assert.NotEqual(fourName, fiveName);
    }

    [Fact]
    public void MangleFunctionName_IsDeterministicAcrossInstances()
    {
        var first = new NameMangler().MangleFunctionName("Core", "map", [BaseTypes.Int]);
        var second = new NameMangler().MangleFunctionName("Core", "map", [BaseTypes.Int]);

        Assert.Equal(first, second);
    }

    [Fact]
    public void MangleFunctionName_PackageQualifiedModule_ReturnsReadablePackageAwareName()
    {
        var mangled = new NameMangler().MangleFunctionName("Std::Seq", "map", [BaseTypes.Int]);

        Assert.StartsWith("eidos_Std__Seq_map_", mangled, StringComparison.Ordinal);
        Assert.DoesNotContain("u003A", mangled, StringComparison.Ordinal);
    }

    [Fact]
    public void AllocateFunctionName_DifferentModuleIdentityKeys_ReturnsDifferentInstanceNames()
    {
        var allocator = new LlvmSymbolNameAllocator(new NameMangler());
        var allocated = new Dictionary<string, LlvmFunctionType>();
        var functionType = new LlvmFunctionType
        {
            ReturnType = LlvmIntType.I32,
            ParameterTypes = []
        };

        var first = allocator.AllocateFunctionName(
            CreateStructuredRequest("pkg:Lib|inst:a|module:Math", functionType),
            allocated);
        allocated[first] = functionType;

        var second = allocator.AllocateFunctionName(
            CreateStructuredRequest("pkg:Lib|inst:b|module:Math", functionType),
            allocated);

        Assert.NotEqual(first, second);
        Assert.StartsWith("eidos_Lib__Math_value_", first, StringComparison.Ordinal);
        Assert.StartsWith("eidos_Lib__Math_value_", second, StringComparison.Ordinal);
    }

    private static LlvmFunctionNameAllocationRequest CreateStructuredRequest(
        string moduleIdentityKey,
        LlvmFunctionType functionType)
    {
        return new LlvmFunctionNameAllocationRequest
        {
            ModuleName = "Lib::Math",
            SourceName = "value",
            SignatureKey = "i32()",
            FunctionIdKey = $"module-id:{moduleIdentityKey}::value",
            HasStructuredIdentity = true,
            FunctionType = functionType
        };
    }
}
