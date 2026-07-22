using Eidosc.CodeGen.Llvm;
using Eidosc.Mir;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    public void ConstGenericAdtSpecializations_EmitDistinctLayoutsAndLlvmTypes()
    {
        const string source = """
Buffer[comptime N: Int, comptime T: Type] :: type
{
    Buffer:: type(T)
}

make4 :: Int -> Buffer[4, Int]
{
    value => Buffer(value)
}

make5 :: Int -> Buffer[5, Int]
{
    value => Buffer(value)
}
""";

        var result = RunSourceAtLlvm(source, "llvm_const_generic_adt_layouts.eidos");

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        var module = Assert.IsType<MirModule>(result.MirModule);
        var layouts = module.ConstructorLayouts
            .SelectMany(entry => entry.Value.Select(layout => new
            {
                Layout = layout,
                Descriptor = module.TypeDescriptors.GetValueOrDefault(entry.Key) as TypeDescriptor.TyCon
            }))
            .Where(static entry =>
                entry.Layout.TypeName.StartsWith("Buffer_", StringComparison.Ordinal) &&
                entry.Descriptor is { ValueArgs.Length: > 0 })
            .ToList();
        var buffer4 = Assert.Single(
            layouts,
            static entry => entry.Descriptor!.ValueArgs.Any(static argument => argument.DisplayText == "4"));
        var buffer5 = Assert.Single(
            layouts,
            static entry => entry.Descriptor!.ValueArgs.Any(static argument => argument.DisplayText == "5"));

        Assert.NotEqual(buffer4.Layout.TypeName, buffer5.Layout.TypeName);
        var llvmIr = Assert.IsType<string>(result.LlvmIrText);
        Assert.Contains(NameMangler.SanitizeIdentifier(buffer4.Layout.TypeName), llvmIr, StringComparison.Ordinal);
        Assert.Contains(NameMangler.SanitizeIdentifier(buffer5.Layout.TypeName), llvmIr, StringComparison.Ordinal);
    }
}
