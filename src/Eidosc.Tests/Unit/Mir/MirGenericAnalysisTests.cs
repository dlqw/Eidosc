using Eidosc.Mir;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public sealed class MirGenericAnalysisTests
{
    [Fact]
    public void ContainsOpenTypeVariable_UsesTypeDescriptorsWithoutDynamicKeys()
    {
        var typeVariable = new TypeId(9001);
        var tupleType = new TypeId(9002);
        var descriptors = new Dictionary<int, TypeDescriptor>
        {
            [typeVariable.Value] = new TypeDescriptor.TypeVar(0),
            [tupleType.Value] = new TypeDescriptor.Tuple([typeVariable])
        };

        var containsOpenType = MirGenericAnalysis.ContainsOpenTypeVariable(
            tupleType,
            descriptors,
            new Dictionary<int, string>());

        Assert.True(containsOpenType);
    }

    [Fact]
    public void ContainsOpenConstructorVariable_DetectsTyConConstructorVariable()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var openConstructor = new TypeId(9003);
        var descriptors = new Dictionary<int, TypeDescriptor>
        {
            [openConstructor.Value] = new TypeDescriptor.TyCon("var:0", [intType])
        };

        var containsOpenConstructor = MirGenericAnalysis.ContainsOpenConstructorVariable(
            openConstructor,
            descriptors,
            new Dictionary<int, string>());

        Assert.True(containsOpenConstructor);
    }

    [Fact]
    public void IsGenericSignature_HonorsRequestedLocalScope()
    {
        var typeVariable = new TypeId(9011);
        var descriptors = new Dictionary<int, TypeDescriptor>
        {
            [typeVariable.Value] = new TypeDescriptor.TypeVar(0)
        };
        var function = new MirFunc
        {
            Name = "local_open",
            ReturnType = new TypeId(BaseTypes.UnitId),
            Locals =
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "tmp",
                    TypeId = typeVariable,
                    IsParameter = false
                }
            ]
        };

        Assert.False(function.IsGenericSignature(
            descriptors,
            new Dictionary<int, string>(),
            MirGenericLocalScope.ParametersOnly));
        Assert.True(function.IsGenericSignature(
            descriptors,
            new Dictionary<int, string>(),
            MirGenericLocalScope.AllLocals));
    }
}
