using Eidosc.CodeGen.Llvm;
using Eidosc.Mir;
using Eidosc.Symbols;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Llvm;

public partial class MirToLlvmConverterTests
{
    [Fact]
    public void ConvertSelectedFunctions_CallerOfUnselectedNounwindCalleeMatchesFullFragment()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var calleeSymbol = new SymbolId(201);
        var calleeFunctionId = new FunctionId
        {
            SymbolId = calleeSymbol,
            Name = "callee",
            QualifiedName = "callee"
        };
        var callee = BuildFunction(
            intType,
            locals: [],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(1)
            },
            name: "callee",
            symbolId: calleeSymbol,
            functionId: calleeFunctionId);
        var result = LocalPlace(1, intType);
        var caller = BuildFunction(
            intType,
            locals:
            [
                new MirLocal
                {
                    Id = result.Local,
                    Name = "result",
                    TypeId = intType
                }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = result,
                    Function = new MirFunctionRef
                    {
                        Name = callee.Name,
                        SymbolId = callee.SymbolId,
                        FunctionId = callee.FunctionId,
                        TypeId = intType
                    }
                }
            ],
            returnValue: result,
            name: "caller");
        var module = new MirModule
        {
            Name = "selected_call_graph",
            Functions = [callee, caller]
        };

        var full = new MirToLlvmConverter().Convert(module);
        var partial = new MirToLlvmConverter().ConvertSelectedFunctions(
            module,
            new HashSet<string>(StringComparer.Ordinal) { "name:caller" });
        var fullCaller = LlvmFunctionFingerprintBuilder.BuildFragment(
            SingleFunctionBySourceName(full, "caller"));
        var partialCaller = LlvmFunctionFingerprintBuilder.BuildFragment(
            SingleFunctionBySourceName(partial, "caller"));

        Assert.Equal(fullCaller.IrFragment, partialCaller.IrFragment);
        Assert.Equal(fullCaller.DeclarationIr, partialCaller.DeclarationIr);
    }

    [Fact]
    public void ConvertSelectedFunctions_ReservesAlwaysInlineGroupIdsFromFullModule()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var variantId = new FunctionId
        {
            Name = "always_inline",
            QualifiedName = "always_inline",
            StableIdentityKey = "test:always_inline|unique:0"
        };
        var alwaysInline = BuildFunction(
            intType,
            locals: [],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(1)
            },
            name: "always_inline",
            functionId: variantId);
        var ordinary = BuildFunction(
            intType,
            locals: [],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(2)
            },
            name: "ordinary");
        var module = new MirModule
        {
            Name = "selected_attribute_groups",
            Functions = [alwaysInline, ordinary]
        };

        var full = new MirToLlvmConverter().Convert(module);
        var partial = new MirToLlvmConverter().ConvertSelectedFunctions(
            module,
            new HashSet<string>(StringComparer.Ordinal) { "name:ordinary" });
        var fullOrdinary = LlvmFunctionFingerprintBuilder.BuildFragment(
            SingleFunctionBySourceName(full, "ordinary"));
        var partialOrdinary = LlvmFunctionFingerprintBuilder.BuildFragment(
            SingleFunctionBySourceName(partial, "ordinary"));

        Assert.Equal(fullOrdinary.IrFragment, partialOrdinary.IrFragment);
        Assert.Equal(fullOrdinary.DeclarationIr, partialOrdinary.DeclarationIr);
        Assert.Contains(partial.AttributeGroups, static group =>
            group.Id == 0 && group.Attributes.SequenceEqual(["alwaysinline"]));
    }
}
