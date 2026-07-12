using Eidosc.Symbols;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Ast.Types;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public sealed class TypeIdRegistryTests
{
    [Fact]
    public void GetOrCreateDynamicTypeId_InternsStructurallyEquivalentDescriptors()
    {
        var registry = new TypeIdRegistry(new SymbolTable(), null);
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);

        var first = registry.GetOrCreateDynamicTypeId(new TypeDescriptor.Tuple([intType, boolType]));
        var second = registry.GetOrCreateDynamicTypeId(new TypeDescriptor.Tuple([intType, boolType]));

        Assert.Equal(first, second);
        Assert.Single(registry.TypeDescriptors);
    }

    [Fact]
    public void GetOrCreateDynamicTypeId_AllocatesAfterSymbolBackedTypeIds()
    {
        var symbolTable = new SymbolTable();
        var lastTypeParameter = SymbolId.None;
        for (var i = 0; i < 1100; i++)
        {
            lastTypeParameter = symbolTable.DeclareTypeParameter($"T{i}", default);
        }

        var registry = new TypeIdRegistry(symbolTable, null);

        var tupleType = registry.GetOrCreateDynamicTypeId(
            new TypeDescriptor.Tuple([new TypeId(BaseTypes.IntId), new TypeId(BaseTypes.BoolId)]));

        Assert.True(tupleType.Value > lastTypeParameter.Value);
        Assert.DoesNotContain(lastTypeParameter.Value, registry.TypeDescriptors.Keys);
        Assert.Contains(tupleType.Value, registry.TypeDescriptors.Keys);
    }

    [Fact]
    public void GetTypeTypeId_AppliesTypeInfererSubstitution()
    {
        var symbolTable = new SymbolTable();
        var typeInferer = new TypeInferer(symbolTable);
        var registry = new TypeIdRegistry(symbolTable, typeInferer);
        var typeVariable = typeInferer.Substitution.FreshTypeVariable();
        typeInferer.Substitution.Unify(
            typeVariable,
            new TyCon
            {
                Name = "Int",
                Id = new TypeId(BaseTypes.IntId)
            });

        var typeId = registry.GetTypeTypeId(typeVariable);

        Assert.Equal(new TypeId(BaseTypes.IntId), typeId);
    }

    [Fact]
    public void GetTypeTypeId_BuiltinTyConFastPath_DoesNotPublishNominalDescriptor()
    {
        var registry = new TypeIdRegistry(new SymbolTable(), null);
        var intType = new TyCon
        {
            Name = "Int",
            Id = new TypeId(BaseTypes.IntId)
        };

        var typeId = registry.GetTypeTypeId(intType);

        Assert.Equal(new TypeId(BaseTypes.IntId), typeId);
        Assert.DoesNotContain(BaseTypes.IntId, registry.TypeDescriptors.Keys);
    }

    [Fact]
    public void GetTypeId_TypeParameterPath_ReturnsSymbolBackedLayoutPlaceholder()
    {
        var symbolTable = new SymbolTable();
        var registry = new TypeIdRegistry(symbolTable, null);
        var typeParamId = symbolTable.DeclareTypeParameter("T", default);
        var typePath = new TypePath
        {
            SymbolId = typeParamId
        };
        typePath.SetTypeName("T");

        var typeId = registry.GetTypeId(typePath);

        Assert.Equal(new TypeId(typeParamId.Value), typeId);
    }

    [Fact]
    public void GetTypeTypeId_NonGenericAdt_PublishesSymbolBackedTyConDescriptor()
    {
        var symbolTable = new SymbolTable();
        var registry = new TypeIdRegistry(symbolTable, null);
        var adtId = symbolTable.DeclareAdt("TaskGroup", default);
        var adt = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(adtId));
        var taskGroupType = new TyCon
        {
            Name = "TaskGroup",
            Symbol = adtId,
            Id = adt.TypeId
        };

        var typeId = registry.GetTypeTypeId(taskGroupType);

        Assert.Equal(adt.TypeId, typeId);
        var descriptor = Assert.IsType<TypeDescriptor.TyCon>(registry.TypeDescriptors[typeId.Value]);
        Assert.Equal($"sym:{adtId.Value}", descriptor.ConstructorDescriptor);
        Assert.Empty(descriptor.TypeArgs);
    }
}
