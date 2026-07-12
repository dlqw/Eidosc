using Eidosc;
using Eidosc.CodeGen.Llvm;
using Eidosc.Mir;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Llvm;

public class TypeLoweringTests
{
    [Fact]
    public void Lower_BuiltinTypeIds_ReturnsExpectedLlvmTypes()
    {
        var lowering = new TypeLowering();

        Assert.IsType<LlvmIntType>(lowering.Lower(new TypeId(BaseTypes.IntId)));
        Assert.IsType<LlvmFloatType>(lowering.Lower(new TypeId(BaseTypes.FloatId)));
        Assert.IsType<LlvmIntType>(lowering.Lower(new TypeId(BaseTypes.BoolId)));
        Assert.IsType<LlvmPointerType>(lowering.Lower(new TypeId(BaseTypes.StringId)));
        Assert.IsType<LlvmIntType>(lowering.Lower(new TypeId(BaseTypes.CharId)));
        Assert.IsType<LlvmVoidType>(lowering.Lower(new TypeId(BaseTypes.UnitId)));
    }

    [Fact]
    public void Lower_UnknownTypeId_DefaultsToPointer()
    {
        var lowering = new TypeLowering();
        var lowered = lowering.Lower(new TypeId(5000));

        Assert.IsType<LlvmPointerType>(lowered);
    }

    [Fact]
    public void LowerRaw_UnresolvedTypeVariable_Throws()
    {
        var lowering = new TypeLowering();

        Assert.Throws<TypeLoweringException>(() => lowering.LowerRaw(new TyVar { Index = 1 }));
    }

    [Fact]
    public void LowerRaw_ResolvedTypeVariable_LowersToInstanceType()
    {
        var lowering = new TypeLowering();
        var typeVar = new TyVar
        {
            Index = 1,
            Instance = BaseTypes.Bool
        };

        var lowered = lowering.LowerRaw(typeVar);

        var intType = Assert.IsType<LlvmIntType>(lowered);
        Assert.Equal(1, intType.Bits);
    }

    [Fact]
    public void IsOpenDynamicType_DirectTypeVariable_ReturnsTrue()
    {
        var typeId = new TypeId(7001);
        var lowering = new TypeLowering();
        lowering.SetDynamicTypeKeys(new Dictionary<int, string>
        {
            [typeId.Value] = "TyVar_1"
        });

        Assert.True(lowering.IsOpenDynamicType(typeId));
    }

    [Fact]
    public void IsOpenDynamicType_ClosedBuiltin_ReturnsFalse()
    {
        var lowering = new TypeLowering();

        Assert.False(lowering.IsOpenDynamicType(new TypeId(BaseTypes.StringId)));
    }

    [Fact]
    public void Lower_DynamicCurriedFunctionType_FlattensIntoFullArityFunctionPointer()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var outerFunctionType = new TypeId(7001);
        var innerFunctionType = new TypeId(7002);
        var lowering = new TypeLowering();
        lowering.SetDynamicTypeKeys(new Dictionary<int, string>
        {
            [outerFunctionType.Value] = $"Fun(T{intType.Value})->T{innerFunctionType.Value}",
            [innerFunctionType.Value] = $"Fun(T{intType.Value})->T{intType.Value}"
        });

        var lowered = lowering.Lower(outerFunctionType);

        // Function-typed values are represented as opaque closure pointers (ptr),
        // not typed function pointers, since the invoke function lives inside
        // the closure object.
        var pointerType = Assert.IsType<LlvmPointerType>(lowered);
        Assert.Null(pointerType.ElementType);
    }

    [Fact]
    public void TryGetStructType_MultiCtorADT_ReturnsStructWithTagField()
    {
        var adtTypeId = new TypeId(8001);
        var intTypeId = new TypeId(BaseTypes.IntId);
        var lowering = new TypeLowering();
        lowering.SetConstructorLayouts(new Dictionary<int, List<ConstructorTypeLayout>>
        {
            [adtTypeId.Value] =
            [
                new ConstructorTypeLayout
                {
                    TypeName = "Option_Int",
                    ConstructorName = "None",
                    TagValue = 1,
                    FieldTypeIds = []
                },
                new ConstructorTypeLayout
                {
                    TypeName = "Option_Int",
                    ConstructorName = "Some",
                    TagValue = 2,
                    FieldTypeIds = [intTypeId]
                }
            ]
        });

        var found = lowering.TryGetStructType(adtTypeId, out var structType);
        Assert.True(found);
        Assert.NotNull(structType);
        Assert.Equal("eidos_Option_Int", structType.Name);
        Assert.False(structType.IsLiteral);
        // Tag is in EidosHeader, not payload — payload only has max 1 field (i64)
        var fieldType = Assert.IsType<LlvmIntType>(Assert.Single(structType.Fields));
        Assert.Equal(64, fieldType.Bits);
    }

    [Fact]
    public void TryGetStructType_SingleCtorADT_ReturnsStructWithoutTagField()
    {
        var adtTypeId = new TypeId(8002);
        var intTypeId = new TypeId(BaseTypes.IntId);
        var lowering = new TypeLowering();
        lowering.SetConstructorLayouts(new Dictionary<int, List<ConstructorTypeLayout>>
        {
            [adtTypeId.Value] =
            [
                new ConstructorTypeLayout
                {
                    TypeName = "Pair_Int",
                    ConstructorName = "MkPair",
                    TagValue = 0,
                    FieldTypeIds = [intTypeId, intTypeId]
                }
            ]
        });

        var found = lowering.TryGetStructType(adtTypeId, out var structType);
        Assert.True(found);
        Assert.NotNull(structType);
        Assert.Equal("eidos_Pair_Int", structType.Name);
        // Single-ctor: no tag, just 2 fields
        Assert.Equal(2, structType.Fields.Count);
    }

    [Fact]
    public void TryGetStructType_SingleCtorADT_UsesPrecisePayloadFieldTypes()
    {
        var adtTypeId = new TypeId(8005);
        var lowering = new TypeLowering();
        lowering.SetConstructorLayouts(new Dictionary<int, List<ConstructorTypeLayout>>
        {
            [adtTypeId.Value] =
            [
                new ConstructorTypeLayout
                {
                    TypeName = "Mixed",
                    ConstructorName = "Mixed",
                    TagValue = 0,
                    FieldTypeIds = [new TypeId(BaseTypes.FloatId), new TypeId(BaseTypes.BoolId)]
                }
            ]
        });

        var found = lowering.TryGetStructType(adtTypeId, out var structType);

        Assert.True(found);
        Assert.NotNull(structType);
        Assert.Same(LlvmFloatType.Double, structType.Fields[0]);
        var boolType = Assert.IsType<LlvmIntType>(structType.Fields[1]);
        Assert.Equal(1, boolType.Bits);
    }

    [Fact]
    public void Lower_ADTTypeId_StillReturnsPointerNotStruct()
    {
        var adtTypeId = new TypeId(8003);
        var lowering = new TypeLowering();
        lowering.SetConstructorLayouts(new Dictionary<int, List<ConstructorTypeLayout>>
        {
            [adtTypeId.Value] =
            [
                new ConstructorTypeLayout
                {
                    TypeName = "Unit",
                    ConstructorName = "MkUnit",
                    TagValue = 0,
                    FieldTypeIds = []
                }
            ]
        });

        // Lower() should still return ptr (ADT values are heap-allocated RC objects)
        var lowered = lowering.Lower(adtTypeId);
        Assert.IsType<LlvmPointerType>(lowered);
    }

    [Fact]
    public void TryGetConstructorLayouts_MultiCtor_ReturnsTrue()
    {
        var adtTypeId = new TypeId(8004);
        var layouts = new List<ConstructorTypeLayout>
        {
            new() { TypeName = "Bool", ConstructorName = "False", TagValue = 0, FieldTypeIds = [] },
            new() { TypeName = "Bool", ConstructorName = "True", TagValue = 1, FieldTypeIds = [] }
        };
        var lowering = new TypeLowering();
        lowering.SetConstructorLayouts(new Dictionary<int, List<ConstructorTypeLayout>>
        {
            [adtTypeId.Value] = layouts
        });

        Assert.True(lowering.TryGetConstructorLayouts(adtTypeId, out var result));
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void TryGetTyConTypeArguments_UsesTypeDescriptorBeforeStringFallback()
    {
        var optionType = new TypeId(8100);
        var intType = new TypeId(BaseTypes.IntId);
        var stringType = new TypeId(BaseTypes.StringId);
        var lowering = new TypeLowering();
        lowering.SetTypeDescriptors(new Dictionary<int, TypeDescriptor>
        {
            [optionType.Value] = new TypeDescriptor.TyCon("sym:1", [intType])
        });
        lowering.SetDynamicTypeKeys(new Dictionary<int, string>
        {
            [optionType.Value] = $"TyCon(sym:2;T{stringType.Value})"
        });

        Assert.True(lowering.TryGetTyConTypeArguments(optionType, out var constructorDescriptor, out var typeArguments));
        Assert.Equal("sym:1", constructorDescriptor);
        Assert.Equal(intType, Assert.Single(typeArguments));
    }

    [Fact]
    public void TryGetFunctionSignature_UsesFlattenedTypeDescriptors()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var stringType = new TypeId(BaseTypes.StringId);
        var outerFunctionType = new TypeId(8200);
        var innerFunctionType = new TypeId(8201);
        var lowering = new TypeLowering();
        lowering.SetTypeDescriptors(new Dictionary<int, TypeDescriptor>
        {
            [outerFunctionType.Value] = new TypeDescriptor.Function([intType], innerFunctionType),
            [innerFunctionType.Value] = new TypeDescriptor.Function([stringType], boolType)
        });

        Assert.True(lowering.TryGetFunctionSignature(outerFunctionType, out var parameterTypes, out var resultType));
        Assert.Equal([intType, stringType], parameterTypes);
        Assert.Equal(boolType, resultType);
    }

    [Fact]
    public void TryGetFunctionSignature_UsesTypeDescriptorBeforeStringFallback()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var stringType = new TypeId(BaseTypes.StringId);
        var functionType = new TypeId(8300);
        var lowering = new TypeLowering();
        lowering.SetTypeDescriptors(new Dictionary<int, TypeDescriptor>
        {
            [functionType.Value] = new TypeDescriptor.Function([intType], boolType)
        });
        lowering.SetDynamicTypeKeys(new Dictionary<int, string>
        {
            [functionType.Value] = $"Fun(T{stringType.Value})->T{intType.Value}"
        });

        Assert.True(lowering.TryGetFunctionSignature(functionType, out var parameterTypes, out var resultType));
        Assert.Equal([intType], parameterTypes);
        Assert.Equal(boolType, resultType);
    }

    [Fact]
    public void HasKnownLoweringMetadata_UnknownValidTypeId_ReturnsFalse()
    {
        var lowering = new TypeLowering();

        Assert.False(lowering.HasKnownLoweringMetadata(new TypeId(8400)));
    }

    [Fact]
    public void HasKnownLoweringMetadata_TypeDescriptorTypeId_ReturnsTrue()
    {
        var tupleType = new TypeId(8401);
        var lowering = new TypeLowering();
        lowering.SetTypeDescriptors(new Dictionary<int, TypeDescriptor>
        {
            [tupleType.Value] = new TypeDescriptor.Tuple([new TypeId(BaseTypes.IntId)])
        });

        Assert.True(lowering.HasKnownLoweringMetadata(tupleType));
    }

    [Fact]
    public void Lower_TupleTypeDescriptor_LowersStructWithoutStringFallback()
    {
        var tupleType = new TypeId(8402);
        var lowering = new TypeLowering();
        lowering.SetTypeDescriptors(new Dictionary<int, TypeDescriptor>
        {
            [tupleType.Value] = new TypeDescriptor.Tuple(
            [
                new TypeId(BaseTypes.IntId),
                new TypeId(BaseTypes.BoolId)
            ])
        });

        var lowered = lowering.Lower(tupleType);

        var structType = Assert.IsType<LlvmStructType>(lowered);
        Assert.IsType<LlvmIntType>(structType.Fields[0]);
        var boolType = Assert.IsType<LlvmIntType>(structType.Fields[1]);
        Assert.Equal(1, boolType.Bits);
    }
}
