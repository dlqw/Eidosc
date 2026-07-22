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

    [Fact]
    public void GetTypeTypeId_ValueGenericArgumentsParticipateInDescriptorIdentity()
    {
        var symbolTable = new SymbolTable();
        var registry = new TypeIdRegistry(symbolTable, null);
        var vectorId = symbolTable.DeclareAdt("Vector", default);
        var intType = new TyCon { Name = "Int", Id = new TypeId(BaseTypes.IntId) };

        var vector4 = new TyCon
        {
            Name = "Vector",
            Symbol = vectorId,
            Args = [intType],
            ValueArgs = [new GenericValueArgument(0, "typed:496e74:int:4", "hash-4", "4", new TypeId(BaseTypes.IntId))]
        };
        var vector5 = vector4 with
        {
            ValueArgs = [new GenericValueArgument(0, "typed:496e74:int:5", "hash-5", "5", new TypeId(BaseTypes.IntId))]
        };

        var vector4TypeId = registry.GetTypeTypeId(vector4);
        var repeatedVector4TypeId = registry.GetTypeTypeId(vector4);
        var vector5TypeId = registry.GetTypeTypeId(vector5);

        Assert.Equal(vector4TypeId, repeatedVector4TypeId);
        Assert.NotEqual(vector4TypeId, vector5TypeId);
        var descriptor = Assert.IsType<TypeDescriptor.TyCon>(registry.TypeDescriptors[vector4TypeId.Value]);
        var valueArgument = Assert.Single(descriptor.ValueArgs);
        Assert.Equal("hash-4", valueArgument.CanonicalHash);
        Assert.Equal(0, valueArgument.ParameterIndex);
    }

    [Fact]
    public void GetTypeTypeId_EffectGenericArgumentsParticipateInDescriptorIdentity()
    {
        var symbolTable = new SymbolTable();
        var registry = new TypeIdRegistry(symbolTable, null);
        var envelopeId = symbolTable.DeclareAdt("Envelope", default);
        var ioId = symbolTable.DeclareEffect("io", default);
        var allocId = symbolTable.DeclareEffect("Alloc", default);
        var io = Assert.IsType<EffectSymbol>(symbolTable.GetSymbol(ioId));
        var alloc = Assert.IsType<EffectSymbol>(symbolTable.GetSymbol(allocId));
        var envelopeIo = new TyCon
        {
            Name = "Envelope",
            Symbol = envelopeId,
            EffectArgs =
            [
                new GenericEffectArgument(
                    0,
                    new TyCon { Name = "io", Symbol = ioId, Id = io.TypeId })
            ]
        };
        var envelopeAlloc = envelopeIo with
        {
            EffectArgs =
            [
                new GenericEffectArgument(
                    0,
                    new TyCon { Name = "Alloc", Symbol = allocId, Id = alloc.TypeId })
            ]
        };

        var ioTypeId = registry.GetTypeTypeId(envelopeIo);
        var repeatedIoTypeId = registry.GetTypeTypeId(envelopeIo);
        var allocTypeId = registry.GetTypeTypeId(envelopeAlloc);

        Assert.Equal(ioTypeId, repeatedIoTypeId);
        Assert.NotEqual(ioTypeId, allocTypeId);
        var descriptor = Assert.IsType<TypeDescriptor.TyCon>(registry.TypeDescriptors[ioTypeId.Value]);
        var effectArgument = Assert.Single(descriptor.EffectArgs);
        Assert.Equal(0, effectArgument.ParameterIndex);
        Assert.Equal(io.TypeId, effectArgument.TypeId);
    }

    [Fact]
    public void GetTypeTypeId_DifferentValueSpecializations_PublishDistinctConstructorLayouts()
    {
        var symbolTable = new SymbolTable();
        var registry = new TypeIdRegistry(symbolTable, null);
        var bufferId = symbolTable.DeclareAdt("Buffer", default);
        var constructorId = symbolTable.DeclareConstructor("Buffer", default, bufferId);
        symbolTable.AddConstructorToAdt(bufferId, constructorId);
        var intType = new TyCon { Name = "Int", Id = new TypeId(BaseTypes.IntId) };
        var buffer4 = new TyCon
        {
            Name = "Buffer",
            Symbol = bufferId,
            Args = [intType],
            ValueArgs = [new GenericValueArgument(0, "typed:496e74:int:4", "hash-4", "4", new TypeId(BaseTypes.IntId))]
        };
        var buffer5 = buffer4 with
        {
            ValueArgs = [new GenericValueArgument(0, "typed:496e74:int:5", "hash-5", "5", new TypeId(BaseTypes.IntId))]
        };

        var buffer4TypeId = registry.GetTypeTypeId(buffer4);
        var buffer5TypeId = registry.GetTypeTypeId(buffer5);

        var buffer4Layout = Assert.Single(registry.ConstructorLayouts[buffer4TypeId.Value]);
        var buffer5Layout = Assert.Single(registry.ConstructorLayouts[buffer5TypeId.Value]);
        Assert.NotEqual(buffer4Layout.TypeName, buffer5Layout.TypeName);
        Assert.Contains("vhash-4", buffer4Layout.TypeName, StringComparison.Ordinal);
        Assert.Contains("vhash-5", buffer5Layout.TypeName, StringComparison.Ordinal);
    }

    [Fact]
    public void GetTypeTypeId_FreshValueVariablesDoNotShareDescriptorIdentity()
    {
        var symbolTable = new SymbolTable();
        var registry = new TypeIdRegistry(symbolTable, null);
        var bufferId = symbolTable.DeclareAdt("Buffer", default);
        var openArgument = new GenericValueArgument(
            0,
            "value-parameter:0:4e",
            "parameter-n",
            "N",
            new TypeId(BaseTypes.IntId),
            ReferencedParameterIndex: 0,
            ValueVariableIndex: 1);
        var first = new TyCon
        {
            Name = "Buffer",
            Symbol = bufferId,
            Args = [BaseTypes.Int],
            ValueArgs = [openArgument]
        };
        var second = first with
        {
            ValueArgs = [openArgument with { ValueVariableIndex = 2 }]
        };

        var firstTypeId = registry.GetTypeTypeId(first);
        var secondTypeId = registry.GetTypeTypeId(second);

        Assert.NotEqual(firstTypeId, secondTypeId);
        var firstDescriptor = Assert.IsType<TypeDescriptor.TyCon>(registry.TypeDescriptors[firstTypeId.Value]);
        Assert.Equal(1, Assert.Single(firstDescriptor.ValueArgs).ValueVariableIndex);
    }
}
