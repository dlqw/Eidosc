using Eidosc.Symbols;
using Eidosc;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

public class CopyTypeSemanticsTests
{
    [Fact]
    public void IsIntrinsicCopyType_BuiltinTypesExceptString_ReturnsExpectedResult()
    {
        Assert.True(CopyTypeSemantics.IsIntrinsicCopyType(new TypeId(BaseTypes.IntId)));
        Assert.True(CopyTypeSemantics.IsIntrinsicCopyType(new TypeId(BaseTypes.FloatId)));
        Assert.True(CopyTypeSemantics.IsIntrinsicCopyType(new TypeId(BaseTypes.BoolId)));
        Assert.True(CopyTypeSemantics.IsIntrinsicCopyType(new TypeId(BaseTypes.CharId)));
        Assert.True(CopyTypeSemantics.IsIntrinsicCopyType(new TypeId(BaseTypes.UnitId)));
        Assert.False(CopyTypeSemantics.IsIntrinsicCopyType(new TypeId(BaseTypes.StringId)));
    }

    [Fact]
    public void IsCopyType_NonIntrinsicWithoutResolver_ReturnsFalse()
    {
        var userType = new TypeId(9001);

        Assert.False(CopyTypeSemantics.IsCopyType(userType));
    }

    [Fact]
    public void IsCopyType_NonIntrinsicWithResolver_UsesResolverResult()
    {
        var userType = new TypeId(9002);

        Assert.True(CopyTypeSemantics.IsCopyType(userType, _ => true));
        Assert.False(CopyTypeSemantics.IsCopyType(userType, _ => false));
    }

    [Fact]
    public void IsCopyType_Intrinsic_DoesNotInvokeResolver()
    {
        var resolverInvoked = false;
        var intType = new TypeId(BaseTypes.IntId);

        var result = CopyTypeSemantics.IsCopyType(
            intType,
            _ =>
            {
                resolverInvoked = true;
                return false;
            });

        Assert.True(result);
        Assert.False(resolverInvoked);
    }

    [Fact]
    public void IsCopyType_InvalidType_ReturnsFalseWithoutResolverInvocation()
    {
        var resolverInvoked = false;

        var result = CopyTypeSemantics.IsCopyType(
            TypeId.None,
            _ =>
            {
                resolverInvoked = true;
                return true;
            });

        Assert.False(result);
        Assert.False(resolverInvoked);
    }

    [Fact]
    public void IsCopyType_FunctionDescriptorWithoutDynamicKey_ReturnsTrue()
    {
        var functionType = new TypeId(9101);
        var descriptors = new Dictionary<int, TypeDescriptor>
        {
            [functionType.Value] = new TypeDescriptor.Function(
                [new TypeId(BaseTypes.IntId)],
                new TypeId(BaseTypes.BoolId))
        };

        var result = CopyTypeSemantics.IsCopyType(functionType, null, descriptors);

        Assert.True(result);
    }

    [Fact]
    public void IsTypeVariable_DescriptorWithoutDynamicKey_ReturnsTrue()
    {
        var typeVariable = new TypeId(9102);
        var descriptors = new Dictionary<int, TypeDescriptor>
        {
            [typeVariable.Value] = new TypeDescriptor.TypeVar(0)
        };

        Assert.True(CopyTypeSemantics.IsTypeVariable(typeVariable, descriptors));
    }

    [Fact]
    public void CreateSymbolTableCopyResolver_CopyTraitImpl_ReturnsTrueForImplementedType()
    {
        var symbolTable = new SymbolTable();
        var adtId = symbolTable.DeclareAdt("Boxed", SourceSpan.Empty);
        var boxedType = symbolTable.GetSymbol<AdtSymbol>(adtId)!.TypeId;
        var copyTrait = symbolTable.DeclareTrait("Copy", SourceSpan.Empty);
        symbolTable.DeclareImpl(copyTrait, boxedType, SourceSpan.Empty);

        var resolver = CopyTypeSemantics.CreateSymbolTableCopyResolver(symbolTable);

        Assert.True(resolver(boxedType));
        Assert.False(resolver(new TypeId(99001)));
    }

    [Fact]
    public void CreateSymbolTableCopyResolver_SpecializedCopyImpl_UsesStructuredImplementingKey()
    {
        var symbolTable = new SymbolTable();
        var boxId = symbolTable.DeclareAdt("Box", SourceSpan.Empty);
        var boxType = symbolTable.GetSymbol<AdtSymbol>(boxId)!.TypeId;
        var copyTrait = symbolTable.DeclareTrait("Copy", SourceSpan.Empty);
        var intKey = new ImplTypeRefKey(SymbolId.None, new TypeId(BaseTypes.IntId), "Int", []);
        var boxIntKey = new ImplTypeRefKey(boxId, boxType, "Box", [intKey]);
        var boxIntType = new TypeId(9103);
        var boxStringType = new TypeId(9104);
        var descriptors = new Dictionary<int, TypeDescriptor>
        {
            [boxIntType.Value] = new TypeDescriptor.TyCon($"sym:{boxId.Value}", [new TypeId(BaseTypes.IntId)]),
            [boxStringType.Value] = new TypeDescriptor.TyCon($"sym:{boxId.Value}", [new TypeId(BaseTypes.StringId)])
        };
        symbolTable.DeclareImpl(
            copyTrait,
            boxType,
            SourceSpan.Empty,
            implementingTypeDisplay: "Box[Int]",
            canonicalImplementingType: "Box[Int]",
            implementingTypeKey: boxIntKey);

        var resolver = CopyTypeSemantics.CreateSymbolTableCopyResolver(symbolTable, descriptors);

        Assert.True(resolver(boxIntType));
        Assert.False(resolver(boxStringType));
        Assert.False(resolver(boxType));
    }

    [Fact]
    public void CreateSymbolTableCopyResolver_CloneTraitFallback_ReturnsTrueWhenCopyTraitAbsent()
    {
        var symbolTable = new SymbolTable();
        var adtId = symbolTable.DeclareAdt("Packet", SourceSpan.Empty);
        var packetType = symbolTable.GetSymbol<AdtSymbol>(adtId)!.TypeId;
        var cloneTrait = symbolTable.DeclareTrait(BuiltinTraits.TraitNames.Clone, SourceSpan.Empty);
        symbolTable.DeclareImpl(cloneTrait, packetType, SourceSpan.Empty);

        var resolver = CopyTypeSemantics.CreateSymbolTableCopyResolver(symbolTable);

        Assert.True(resolver(packetType));
    }

    [Fact]
    public void CreateSymbolTableCopyResolver_NoCopyLikeTrait_ReturnsFalse()
    {
        var symbolTable = new SymbolTable();
        var adtId = symbolTable.DeclareAdt("Blob", SourceSpan.Empty);
        var blobType = symbolTable.GetSymbol<AdtSymbol>(adtId)!.TypeId;

        var resolver = CopyTypeSemantics.CreateSymbolTableCopyResolver(symbolTable);

        Assert.False(resolver(blobType));
    }
}
