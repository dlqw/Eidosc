using Eidosc.Symbols;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

public sealed class OwnershipContractTests
{
    [Fact]
    public void Create_ClassifiesStructuredSignatureWithoutBodyFacts()
    {
        var valueType = new TypeId(9001);
        var sharedType = new TypeId(9002);
        var mutableType = new TypeId(9003);
        var genericType = new TypeId(9004);
        var descriptors = new Dictionary<int, TypeDescriptor>
        {
            [valueType.Value] = new TypeDescriptor.TyCon(TypeConstructorKey.FromTypeId(valueType), []),
            [sharedType.Value] = new TypeDescriptor.Ref(valueType),
            [mutableType.Value] = new TypeDescriptor.MutRef(valueType),
            [genericType.Value] = new TypeDescriptor.TypeVar(0)
        };

        var contract = OwnershipContract.Create(
            new SymbolId(41),
            "process",
            [
                ("value", valueType),
                ("shared", sharedType),
                ("mutable", mutableType),
                ("generic", genericType)
            ],
            sharedType,
            descriptors);

        Assert.Equal(OwnershipPassingKind.ByValue, contract.GetParameter(0).Projection.Kind);
        Assert.Equal(OwnershipPassingKind.SharedBorrow, contract.GetParameter(1).Projection.Kind);
        Assert.Equal(OwnershipPassingKind.MutableBorrow, contract.GetParameter(2).Projection.Kind);
        Assert.Equal(OwnershipPassingKind.ByValue, contract.GetParameter(3).Projection.Kind);
        Assert.True(contract.GetParameter(3).Projection.IsDeferred);
        Assert.Equal(OwnershipPassingKind.SharedBorrow, contract.Result.Projection.Kind);
    }

    [Fact]
    public void CanonicalIdentity_DependsOnTypedSignatureNotBodyNamesOrSymbolIdChurn()
    {
        var valueType = new TypeId(BaseTypes.IntId);
        var descriptors = new Dictionary<int, TypeDescriptor>
        {
            [valueType.Value] = new TypeDescriptor.Builtin(valueType.Value)
        };
        var firstSymbols = new SymbolTable();
        firstSymbols.InitializeGlobalScope();
        var firstCallable = firstSymbols.DeclareFunction("consume", SourceSpan.Empty);
        var secondSymbols = new SymbolTable();
        secondSymbols.InitializeGlobalScope();
        secondSymbols.DeclareAdt("SymbolIdChurn", SourceSpan.Empty);
        var secondCallable = secondSymbols.DeclareFunction("consume", SourceSpan.Empty);

        var first = OwnershipContract.Create(
            firstCallable,
            "consume",
            [("before", valueType)],
            valueType,
            descriptors,
            firstSymbols);
        var second = OwnershipContract.Create(
            secondCallable,
            "consume",
            [("after", valueType)],
            valueType,
            descriptors,
            secondSymbols);

        Assert.NotEqual(firstCallable, secondCallable);
        Assert.Equal(first.CanonicalIdentity, second.CanonicalIdentity);
        Assert.Contains(OwnershipContract.CurrentSchemaVersion, first.CanonicalIdentity, StringComparison.Ordinal);
        Assert.DoesNotContain("before", first.CanonicalIdentity, StringComparison.Ordinal);
        Assert.DoesNotContain("after", second.CanonicalIdentity, StringComparison.Ordinal);
    }
}
