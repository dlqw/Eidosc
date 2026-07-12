using Eidosc.Symbols;
using Eidosc;
using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public sealed partial class MirGenericSpecializerTests
{
    [Fact]
    public void Run_GenericTemplate_UsesTypeDescriptorsWithoutDynamicKeys()
    {
        var genericSymbol = new SymbolId(9031);
        var intType = new TypeId(BaseTypes.IntId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var typeVariable = new TypeId(9032);
        var tupleOfVariable = new TypeId(9033);
        var tupleOfInt = new TypeId(9034);

        var genericBox = BuildFunction(
            returnType: tupleOfVariable,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "x",
                    TypeId = typeVariable,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "boxed",
                    TypeId = tupleOfVariable
                }
            ],
            instructions: [],
            returnValue: LocalPlace(2, tupleOfVariable),
            name: "box",
            symbolId: genericSymbol,
            genericParameterCount: 1);

        var callerArg = LocalPlace(1, intType);
        var callerResult = LocalPlace(2, tupleOfInt);
        var caller = BuildFunction(
            returnType: unitType,
            locals:
            [
                new MirLocal { Id = callerArg.Local, Name = "arg", TypeId = intType, IsParameter = true },
                new MirLocal { Id = callerResult.Local, Name = "res", TypeId = tupleOfInt }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "box",
                        SymbolId = genericSymbol,
                        TypeId = TypeId.None
                    },
                    Arguments = [callerArg]
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "caller",
            symbolId: new SymbolId(9035));

        var module = new MirModule
        {
            Name = "descriptor_only_specialize",
            Functions = [genericBox, caller],
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [typeVariable.Value] = new TypeDescriptor.TypeVar(0),
                [tupleOfVariable.Value] = new TypeDescriptor.Tuple([typeVariable]),
                [tupleOfInt.Value] = new TypeDescriptor.Tuple([intType])
            }
        };

        var specialized = new MirGenericSpecializer().Run(module);

        var instance = Assert.Single(
            specialized.Functions,
            function => function.Name.StartsWith("box__spec_", StringComparison.Ordinal));
        Assert.Equal(tupleOfInt, instance.ReturnType);
        Assert.Equal(intType, Assert.Single(instance.Locals, local => local.IsParameter).TypeId);
        Assert.Contains(
            specialized.TypeDescriptors,
            entry => entry.Value is TypeDescriptor.Tuple tuple &&
                     tuple.FieldTypes.SequenceEqual([intType]));

        var rewrittenCaller = Assert.Single(specialized.Functions, function => function.Name == "caller");
        var rewrittenCall = Assert.Single(
            rewrittenCaller.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.True(rewrittenRef.SignatureTypeId.IsValid);
        var functionDescriptor = Assert.IsType<TypeDescriptor.Function>(
            specialized.TypeDescriptors[rewrittenRef.SignatureTypeId.Value]);
        Assert.Equal(tupleOfInt, functionDescriptor.ReturnType);
        Assert.Equal(intType, Assert.Single(functionDescriptor.ParamTypes));
    }

    [Fact]
    public void Run_GenericTemplate_UsesMirGenericTypeParameterIdsWithoutSymbolTable()
    {
        var genericSymbol = new SymbolId(9061);
        var callerSymbol = new SymbolId(9062);
        var intType = new TypeId(BaseTypes.IntId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var typeVariable = new TypeId(9063);

        var identity = BuildFunction(
            returnType: typeVariable,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "value",
                    TypeId = typeVariable,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: LocalPlace(1, typeVariable),
            name: "identity_without_descriptor",
            symbolId: genericSymbol,
            genericParameterCount: 1,
            genericTypeParameterIds: [typeVariable]);

        var argument = LocalPlace(1, intType);
        var result = LocalPlace(2, intType);
        var caller = BuildFunction(
            returnType: unitType,
            locals:
            [
                new MirLocal { Id = argument.Local, Name = "argument", TypeId = intType, IsParameter = true },
                new MirLocal { Id = result.Local, Name = "result", TypeId = intType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = result,
                    Function = new MirFunctionRef
                    {
                        Name = "identity_without_descriptor",
                        SymbolId = genericSymbol,
                        TypeId = TypeId.None
                    },
                    Arguments = [argument]
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "caller_identity_without_descriptor",
            symbolId: callerSymbol);

        var specialized = new MirGenericSpecializer().Run(new MirModule
        {
            Name = "generic_type_parameter_ids_without_symbol_table",
            Functions = [identity, caller]
        });

        var instance = Assert.Single(
            specialized.Functions,
            function => function.Name.StartsWith("identity_without_descriptor__spec_", StringComparison.Ordinal));
        Assert.Equal(intType, instance.ReturnType);
        Assert.Equal(intType, Assert.Single(instance.Locals, local => local.IsParameter).TypeId);

        var rewrittenCaller = Assert.Single(specialized.Functions, function => function.SymbolId == callerSymbol);
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(instance.SymbolId, rewrittenRef.SymbolId);
    }

    [Fact]
    public void Run_NoGenericTemplates_PreservesTypeDescriptors()
    {
        var unitType = new TypeId(BaseTypes.UnitId);
        var tupleType = new TypeId(9021);
        var module = new MirModule
        {
            Name = "descriptor_preserve",
            Functions =
            [
                BuildFunction(
                    returnType: unitType,
                    locals: [],
                    instructions: [],
                    returnValue: new MirConstant
                    {
                        TypeId = unitType,
                        Value = new MirConstantValue.UnitValue()
                    },
                    name: "main",
                    symbolId: new SymbolId(9022))
            ],
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [tupleType.Value] = new TypeDescriptor.Tuple([unitType])
            }
        };

        var specialized = new MirGenericSpecializer().Run(module);

        var descriptor = Assert.IsType<TypeDescriptor.Tuple>(specialized.TypeDescriptors[tupleType.Value]);
        Assert.Equal(unitType, Assert.Single(descriptor.FieldTypes));
    }

    [Fact]
    public void Run_GenericTemplate_InfersOpenReturnFromMirSignatureWithoutSymbolTable()
    {
        var genericSymbol = new SymbolId(9036);
        var callerSymbol = new SymbolId(9037);
        var intType = new TypeId(BaseTypes.IntId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var typeVariable = new TypeId(9038);
        var signatureType = new TypeId(9039);

        var genericMake = BuildFunction(
            returnType: typeVariable,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "unit",
                    TypeId = unitType,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = typeVariable,
                Value = new MirConstantValue.UnitValue()
            },
            name: "make_from_signature",
            symbolId: genericSymbol,
            genericParameterCount: 1);

        var callerArg = LocalPlace(1, unitType);
        var caller = BuildFunction(
            returnType: unitType,
            locals:
            [
                new MirLocal { Id = callerArg.Local, Name = "unit", TypeId = unitType, IsParameter = true }
            ],
            instructions:
            [
                new MirCall
                {
                    Function = new MirFunctionRef
                    {
                        Name = "make_from_signature",
                        SymbolId = genericSymbol,
                        TypeId = TypeId.None,
                        SignatureTypeId = signatureType
                    },
                    Arguments = [callerArg]
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "caller_make_from_signature",
            symbolId: callerSymbol);

        var specialized = new MirGenericSpecializer().Run(new MirModule
        {
            Name = "signature_return_inference_without_symbol_table",
            Functions = [genericMake, caller],
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [typeVariable.Value] = new TypeDescriptor.TypeVar(0),
                [signatureType.Value] = new TypeDescriptor.Function([unitType], intType)
            }
        });

        var instance = Assert.Single(
            specialized.Functions,
            function => function.Name.StartsWith("make_from_signature__spec_", StringComparison.Ordinal));
        Assert.Equal(intType, instance.ReturnType);

        var rewrittenCaller = Assert.Single(specialized.Functions, function => function.SymbolId == callerSymbol);
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(instance.SymbolId, rewrittenRef.SymbolId);
    }

    [Fact]
    public void Run_GenericTemplate_UsesExplicitTypeArgumentsAsBindings()
    {
        var genericSymbol = new SymbolId(9041);
        var intType = new TypeId(BaseTypes.IntId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var typeVariable = new TypeId(9042);

        var genericMake = BuildFunction(
            returnType: typeVariable,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "unit",
                    TypeId = unitType,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "value",
                    TypeId = typeVariable
                }
            ],
            instructions: [],
            returnValue: LocalPlace(2, typeVariable),
            name: "make",
            symbolId: genericSymbol,
            genericParameterCount: 1);

        var callerArg = LocalPlace(1, unitType);
        var caller = BuildFunction(
            returnType: unitType,
            locals:
            [
                new MirLocal { Id = callerArg.Local, Name = "arg", TypeId = unitType, IsParameter = true }
            ],
            instructions:
            [
                new MirCall
                {
                    Function = new MirFunctionRef
                    {
                        Name = "make",
                        SymbolId = genericSymbol,
                        TypeId = typeVariable,
                        TypeArgumentIds = [intType]
                    },
                    Arguments = [callerArg]
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "caller",
            symbolId: new SymbolId(9043));

        var module = new MirModule
        {
            Name = "explicit_type_arg_specialize",
            Functions = [genericMake, caller],
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [typeVariable.Value] = new TypeDescriptor.TypeVar(0)
            }
        };

        var specialized = new MirGenericSpecializer().Run(module);

        var instance = Assert.Single(
            specialized.Functions,
            function => function.Name.StartsWith("make__spec_", StringComparison.Ordinal));
        Assert.Equal(intType, instance.ReturnType);
        Assert.Equal(unitType, Assert.Single(instance.Locals, local => local.IsParameter).TypeId);

        var rewrittenCaller = Assert.Single(specialized.Functions, function => function.Name == "caller");
        var rewrittenCall = Assert.Single(
            rewrittenCaller.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal([intType], rewrittenRef.TypeArgumentIds);
        Assert.Equal(instance.SymbolId, rewrittenRef.SymbolId);
    }

    [Fact]
    public void Run_GenericTemplate_BindsExplicitTypeArgumentsByDeclarationOrder()
    {
        var genericSymbol = new SymbolId(9061);
        var intType = new TypeId(BaseTypes.IntId);
        var stringType = new TypeId(BaseTypes.StringId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var typeA = new TypeId(9062);
        var typeB = new TypeId(9063);

        var genericChooseSecond = BuildFunction(
            returnType: typeA,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "first",
                    TypeId = typeB,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "second",
                    TypeId = typeA,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: LocalPlace(2, typeA),
            name: "choose_second",
            symbolId: genericSymbol,
            genericParameterCount: 2,
            genericTypeParameterIds: [typeA, typeB]);

        var firstArg = LocalPlace(1, stringType);
        var secondArg = LocalPlace(2, intType);
        var caller = BuildFunction(
            returnType: unitType,
            locals:
            [
                new MirLocal { Id = firstArg.Local, Name = "first_arg", TypeId = stringType, IsParameter = true },
                new MirLocal { Id = secondArg.Local, Name = "second_arg", TypeId = intType, IsParameter = true }
            ],
            instructions:
            [
                new MirCall
                {
                    Function = new MirFunctionRef
                    {
                        Name = "choose_second",
                        SymbolId = genericSymbol,
                        TypeId = typeA,
                        TypeArgumentIds = [intType, stringType]
                    },
                    Arguments = [firstArg, secondArg]
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "caller",
            symbolId: new SymbolId(9064));

        var module = new MirModule
        {
            Name = "explicit_type_arg_order",
            Functions = [genericChooseSecond, caller],
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [typeA.Value] = new TypeDescriptor.TypeVar(0),
                [typeB.Value] = new TypeDescriptor.TypeVar(1)
            }
        };

        var specialized = new MirGenericSpecializer().Run(module);

        var instance = Assert.Single(
            specialized.Functions,
            function => function.Name.StartsWith("choose_second__spec_", StringComparison.Ordinal));
        Assert.Equal(intType, instance.ReturnType);
        var parameterTypes = instance.Locals
            .Where(static local => local.IsParameter)
            .Select(static local => local.TypeId)
            .ToList();
        Assert.Equal([stringType, intType], parameterTypes);
    }

    [Fact]
    public void Run_SpecializedConstructorLayout_UsesDescriptorBackedGenericBase()
    {
        var genericSymbol = new SymbolId(9071);
        var intType = new TypeId(BaseTypes.IntId);
        var stringType = new TypeId(BaseTypes.StringId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var typeVariable = new TypeId(9072);
        var optionStringType = new TypeId(9073);
        var optionTemplateType = new TypeId(9074);
        var optionConstructorType = new TypeId(9076);
        var optionDescriptor = $"type:{optionConstructorType.Value}";

        var genericMakeOption = BuildFunction(
            returnType: optionTemplateType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "unit",
                    TypeId = unitType,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "value",
                    TypeId = optionTemplateType
                }
            ],
            instructions: [],
            returnValue: LocalPlace(2, optionTemplateType),
            name: "make_option",
            symbolId: genericSymbol,
            genericParameterCount: 1,
            genericTypeParameterIds: [typeVariable]);

        var callerArg = LocalPlace(1, unitType);
        var caller = BuildFunction(
            returnType: unitType,
            locals:
            [
                new MirLocal { Id = callerArg.Local, Name = "arg", TypeId = unitType, IsParameter = true }
            ],
            instructions:
            [
                new MirCall
                {
                    Function = new MirFunctionRef
                    {
                        Name = "make_option",
                        SymbolId = genericSymbol,
                        TypeId = optionTemplateType,
                        TypeArgumentIds = [intType]
                    },
                    Arguments = [callerArg]
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "caller",
            symbolId: new SymbolId(9075));

        var module = new MirModule
        {
            Name = "constructor_layout_descriptor_base",
            Functions = [genericMakeOption, caller],
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [optionStringType.Value] = new TypeDescriptor.TyCon(optionDescriptor, [stringType]),
                [typeVariable.Value] = new TypeDescriptor.TypeVar(0),
                [optionTemplateType.Value] = new TypeDescriptor.TyCon(optionDescriptor, [typeVariable])
            },
            ConstructorLayouts = new Dictionary<int, List<ConstructorTypeLayout>>
            {
                [optionTemplateType.Value] =
                [
                    new ConstructorTypeLayout
                    {
                        TypeName = "Option_T",
                        ConstructorName = "Some",
                        TagValue = 1,
                        FieldTypeIds = [typeVariable]
                    },
                    new ConstructorTypeLayout
                    {
                        TypeName = "Option_T",
                        ConstructorName = "None",
                        TagValue = 2,
                        FieldTypeIds = []
                    }
                ]
            }
        };

        var specialized = new MirGenericSpecializer().Run(module);

        var optionIntEntry = Assert.Single(
            specialized.TypeDescriptors,
            entry => entry.Value is TypeDescriptor.TyCon tyCon &&
                     tyCon.ConstructorDescriptor == optionDescriptor &&
                     tyCon.TypeArgs.SequenceEqual([intType]));
        Assert.True(
            specialized.ConstructorLayouts.TryGetValue(optionIntEntry.Key, out var optionIntLayouts),
            "Expected specialized Option[Int] constructor layouts.");

        var someLayout = Assert.Single(optionIntLayouts, layout => layout.ConstructorName == "Some");
        Assert.Equal([intType], someLayout.FieldTypeIds);
        Assert.DoesNotContain(typeVariable, someLayout.FieldTypeIds);
    }

    [Fact]
    public void Run_SpecializedConstructorLayout_BackfillsExistingConcreteDescriptorWithStructuredBase()
    {
        var symbolTable = new SymbolTable();
        var optionSymbolId = symbolTable.DeclareAdt("Option", SourceSpan.Empty);
        var optionTypeId = symbolTable.GetSymbol<AdtSymbol>(optionSymbolId)!.TypeId;
        var genericSymbol = new SymbolId(9091);
        var intType = new TypeId(BaseTypes.IntId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var typeVariable = new TypeId(9092);
        var optionTemplateType = new TypeId(9093);
        var optionIntType = new TypeId(9094);
        var templateConstructorDescriptor = $"sym:{optionSymbolId.Value}";
        var concreteConstructorDescriptor = $"type:{optionTypeId.Value}";

        var genericMakeOption = BuildFunction(
            returnType: optionTemplateType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "unit",
                    TypeId = unitType,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "value",
                    TypeId = optionTemplateType
                }
            ],
            instructions: [],
            returnValue: LocalPlace(2, optionTemplateType),
            name: "make_option_existing_descriptor",
            symbolId: genericSymbol,
            genericParameterCount: 1,
            genericTypeParameterIds: [typeVariable]);

        var callerArg = LocalPlace(1, unitType);
        var caller = BuildFunction(
            returnType: unitType,
            locals:
            [
                new MirLocal { Id = callerArg.Local, Name = "arg", TypeId = unitType, IsParameter = true }
            ],
            instructions:
            [
                new MirCall
                {
                    Function = new MirFunctionRef
                    {
                        Name = "make_option_existing_descriptor",
                        SymbolId = genericSymbol,
                        TypeId = optionTemplateType,
                        TypeArgumentIds = [intType]
                    },
                    Arguments = [callerArg]
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "caller_existing_descriptor",
            symbolId: new SymbolId(9095));

        var module = new MirModule
        {
            Name = "constructor_layout_existing_concrete_descriptor",
            Functions = [genericMakeOption, caller],
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [typeVariable.Value] = new TypeDescriptor.TypeVar(0),
                [optionTemplateType.Value] = new TypeDescriptor.TyCon(templateConstructorDescriptor, [typeVariable]),
                [optionIntType.Value] = new TypeDescriptor.TyCon(concreteConstructorDescriptor, [intType])
            },
            TypeConstructors =
            [
                new MirTypeConstructorInfo
                {
                    SymbolId = optionSymbolId,
                    Name = "Option",
                    TypeId = optionTypeId
                }
            ],
            ConstructorLayouts = new Dictionary<int, List<ConstructorTypeLayout>>
            {
                [optionTemplateType.Value] =
                [
                    new ConstructorTypeLayout
                    {
                        TypeName = "Option_T",
                        ConstructorName = "Some",
                        TagValue = 1,
                        FieldTypeIds = [typeVariable]
                    },
                    new ConstructorTypeLayout
                    {
                        TypeName = "Option_T",
                        ConstructorName = "None",
                        TagValue = 2,
                        FieldTypeIds = []
                    }
                ]
            }
        };

        var specialized = new MirGenericSpecializer(null, null, symbolTable).Run(module);

        Assert.True(
            specialized.ConstructorLayouts.TryGetValue(optionIntType.Value, out var optionIntLayouts),
            "Expected existing concrete Option[Int] descriptor to receive constructor layouts.");
        var someLayout = Assert.Single(optionIntLayouts, layout => layout.ConstructorName == "Some");
        Assert.Equal([intType], someLayout.FieldTypeIds);
    }

    [Fact]
    public void Run_SpecializedConstructorLayout_SubstitutesNestedAdtFieldDescriptors()
    {
        var genericSymbol = new SymbolId(9101);
        var intType = new TypeId(BaseTypes.IntId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var typeVariable = new TypeId(9102);
        var boxTemplateType = new TypeId(9103);
        var optionTemplateType = new TypeId(9104);
        var boxConstructorType = new TypeId(9106);
        var optionConstructorType = new TypeId(9107);
        var boxDescriptor = $"type:{boxConstructorType.Value}";
        var optionDescriptor = $"type:{optionConstructorType.Value}";

        var genericMakeBox = BuildFunction(
            returnType: boxTemplateType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "unit",
                    TypeId = unitType,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "value",
                    TypeId = boxTemplateType
                }
            ],
            instructions: [],
            returnValue: LocalPlace(2, boxTemplateType),
            name: "make_box",
            symbolId: genericSymbol,
            genericParameterCount: 1,
            genericTypeParameterIds: [typeVariable]);

        var callerArg = LocalPlace(1, unitType);
        var caller = BuildFunction(
            returnType: unitType,
            locals:
            [
                new MirLocal { Id = callerArg.Local, Name = "arg", TypeId = unitType, IsParameter = true }
            ],
            instructions:
            [
                new MirCall
                {
                    Function = new MirFunctionRef
                    {
                        Name = "make_box",
                        SymbolId = genericSymbol,
                        TypeId = boxTemplateType,
                        TypeArgumentIds = [intType]
                    },
                    Arguments = [callerArg]
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "caller_box",
            symbolId: new SymbolId(9105));

        var module = new MirModule
        {
            Name = "constructor_layout_nested_adt_fields",
            Functions = [genericMakeBox, caller],
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [typeVariable.Value] = new TypeDescriptor.TypeVar(0),
                [boxTemplateType.Value] = new TypeDescriptor.TyCon(boxDescriptor, [typeVariable]),
                [optionTemplateType.Value] = new TypeDescriptor.TyCon(optionDescriptor, [typeVariable])
            },
            ConstructorLayouts = new Dictionary<int, List<ConstructorTypeLayout>>
            {
                [boxTemplateType.Value] =
                [
                    new ConstructorTypeLayout
                    {
                        TypeName = "Box_T",
                        ConstructorName = "Box",
                        TagValue = 0,
                        FieldTypeIds = [optionTemplateType]
                    }
                ]
            }
        };

        var specialized = new MirGenericSpecializer().Run(module);

        var boxIntEntry = Assert.Single(
            specialized.TypeDescriptors,
            entry => entry.Value is TypeDescriptor.TyCon tyCon &&
                     tyCon.ConstructorDescriptor == boxDescriptor &&
                     tyCon.TypeArgs.SequenceEqual([intType]));
        var optionIntEntry = Assert.Single(
            specialized.TypeDescriptors,
            entry => entry.Value is TypeDescriptor.TyCon tyCon &&
                     tyCon.ConstructorDescriptor == optionDescriptor &&
                     tyCon.TypeArgs.SequenceEqual([intType]));
        var boxLayout = Assert.Single(specialized.ConstructorLayouts[boxIntEntry.Key]);
        Assert.Equal([new TypeId(optionIntEntry.Key)], boxLayout.FieldTypeIds);
        Assert.DoesNotContain(optionTemplateType, boxLayout.FieldTypeIds);
    }

    [Fact]
    public void Run_SpecializedConstructorLayout_ExpandsTypeAliasDescriptorToGenericBase()
    {
        var genericSymbol = new SymbolId(9201);
        var boxSymbolId = new SymbolId(9202);
        var aliasSymbolId = new SymbolId(9203);
        var aliasTypeParamId = new SymbolId(9204);
        var aliasTypeId = new TypeId(9205);
        var intType = new TypeId(BaseTypes.IntId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var typeVariable = new TypeId(aliasTypeParamId.Value);
        var boxTemplateType = new TypeId(9206);
        var aliasTemplateType = new TypeId(9207);
        var aliasTargetType = new TypeId(9208);
        var boxConstructorDescriptor = $"sym:{boxSymbolId.Value}";
        var aliasConstructorDescriptor = $"sym:{aliasSymbolId.Value}";

        var genericMakeAlias = BuildFunction(
            returnType: aliasTemplateType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "unit",
                    TypeId = unitType,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "value",
                    TypeId = aliasTemplateType
                }
            ],
            instructions: [],
            returnValue: LocalPlace(2, aliasTemplateType),
            name: "make_alias_box",
            symbolId: genericSymbol,
            genericParameterCount: 1,
            genericTypeParameterIds: [typeVariable]);

        var callerArg = LocalPlace(1, unitType);
        var caller = BuildFunction(
            returnType: unitType,
            locals:
            [
                new MirLocal { Id = callerArg.Local, Name = "arg", TypeId = unitType, IsParameter = true }
            ],
            instructions:
            [
                new MirCall
                {
                    Function = new MirFunctionRef
                    {
                        Name = "make_alias_box",
                        SymbolId = genericSymbol,
                        TypeId = aliasTemplateType,
                        TypeArgumentIds = [intType]
                    },
                    Arguments = [callerArg]
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "caller_alias_box",
            symbolId: new SymbolId(9209));

        var module = new MirModule
        {
            Name = "constructor_layout_type_alias_descriptor",
            Functions = [genericMakeAlias, caller],
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [typeVariable.Value] = new TypeDescriptor.TypeVar(aliasTypeParamId.Value),
                [boxTemplateType.Value] = new TypeDescriptor.TyCon(boxConstructorDescriptor, [typeVariable]),
                [aliasTemplateType.Value] = new TypeDescriptor.TyCon(aliasConstructorDescriptor, [typeVariable]),
                [aliasTargetType.Value] = new TypeDescriptor.TyCon(boxConstructorDescriptor, [typeVariable])
            },
            ConstructorLayouts = new Dictionary<int, List<ConstructorTypeLayout>>
            {
                [boxTemplateType.Value] =
                [
                    new ConstructorTypeLayout
                    {
                        TypeName = "Box_T",
                        ConstructorName = "Box",
                        TagValue = 0,
                        FieldTypeIds = [typeVariable]
                    }
                ]
            },
            TypeAliases =
            [
                new MirTypeAliasInfo
                {
                    AliasId = aliasSymbolId,
                    Name = "BoxAlias",
                    TypeId = aliasTypeId,
                    AliasTarget = aliasTargetType,
                    TypeParameterIds = [aliasTypeParamId]
                }
            ]
        };

        var specialized = new MirGenericSpecializer().Run(module);

        var aliasIntEntry = Assert.Single(
            specialized.TypeDescriptors,
            entry => entry.Value is TypeDescriptor.TyCon tyCon &&
                     tyCon.ConstructorDescriptor == aliasConstructorDescriptor &&
                     tyCon.TypeArgs.SequenceEqual([intType]));
        Assert.True(
            specialized.ConstructorLayouts.TryGetValue(aliasIntEntry.Key, out var aliasLayouts),
            "Expected alias concrete descriptor to reuse the target ADT constructor layout.");
        var aliasLayout = Assert.Single(aliasLayouts);
        Assert.Equal([intType], aliasLayout.FieldTypeIds);
    }

    [Fact]
    public void Run_SpecializedConstructorLayout_UsesMirTypeConstructorMetadataForGenericBaseWithoutSymbolTable()
    {
        var boxSymbolId = new SymbolId(9210);
        var boxTypeParamId = new SymbolId(9211);
        var boxTypeId = new TypeId(9212);
        var boxIntType = new TypeId(9213);
        var intType = new TypeId(BaseTypes.IntId);
        var boxConstructorDescriptor = $"sym:{boxSymbolId.Value}";

        var module = new MirModule
        {
            Name = "constructor_layout_type_constructor_metadata_without_symbol_table",
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [boxIntType.Value] = new TypeDescriptor.TyCon(boxConstructorDescriptor, [intType])
            },
            ConstructorLayouts = new Dictionary<int, List<ConstructorTypeLayout>>
            {
                [boxTypeId.Value] =
                [
                    new ConstructorTypeLayout
                    {
                        TypeName = "Box_T",
                        ConstructorName = "Box",
                        TagValue = 0,
                        FieldTypeIds = [new TypeId(boxTypeParamId.Value)]
                    }
                ]
            },
            TypeConstructors =
            [
                new MirTypeConstructorInfo
                {
                    SymbolId = boxSymbolId,
                    Name = "Box",
                    TypeId = boxTypeId,
                    TypeParameterIds = [boxTypeParamId]
                }
            ]
        };

        var specialized = new MirGenericSpecializer().Run(module);

        Assert.True(
            specialized.ConstructorLayouts.TryGetValue(boxIntType.Value, out var boxIntLayouts),
            "Expected concrete Box[Int] descriptor to reuse the generic Box constructor layout from MIR metadata.");
        var boxLayout = Assert.Single(boxIntLayouts);
        Assert.Equal([intType], boxLayout.FieldTypeIds);
    }

    [Fact]
    public void SpecializationFailure_ToDiagnostic_CarriesStructuredMetadata()
    {
        var failure = new SpecializationFailure(
            SpecializationFailureReason.UnresolvedTypes,
            "sym:9041",
            "make",
            SourceSpan.Empty,
            "1|2,3",
            "Return=1 Params=[2,3]",
            "make__spec_AABBCC");

        var diagnostic = failure.ToDiagnostic();

        Assert.Equal("E5310", diagnostic.Code);
        Assert.Equal("mir-specialization", diagnostic.Metadata["phase"]);
        Assert.Equal("unresolved-types", diagnostic.Metadata["reason"]);
        Assert.Equal("sym:9041", diagnostic.Metadata["templateKey"]);
        Assert.Equal("make", diagnostic.Metadata["templateName"]);
        Assert.Equal("1|2,3", diagnostic.Metadata["signatureKey"]);
        Assert.Equal("make__spec_AABBCC", diagnostic.Metadata["previewName"]);
        Assert.Contains(
            diagnostic.Notes,
            note => note.Contains("provide concrete type arguments or add annotations", StringComparison.Ordinal));
    }

    [Fact]
    public void SpecializationFailure_ToDiagnostic_UsesReasonSpecificSuggestion()
    {
        var cases = new (SpecializationFailureReason Reason, string ExpectedNoteFragment)[]
        {
            (SpecializationFailureReason.UnresolvedConstructorBinding, "higher-kinded constructor arguments"),
            (SpecializationFailureReason.TypeInferenceFailed, "explicit type argument count/order"),
            (SpecializationFailureReason.PartialBindingIncomplete, "partial application"),
            (SpecializationFailureReason.NoConcreteDispatchType, "trait Self metadata")
        };

        foreach (var (reason, expectedNoteFragment) in cases)
        {
            var failure = new SpecializationFailure(
                reason,
                "sym:9042",
                "make",
                SourceSpan.Empty,
                "1|2,3",
                "Return=1 Params=[2,3]",
                "make__spec_AABBCC");

            var diagnostic = failure.ToDiagnostic();

            Assert.Contains(
                diagnostic.Notes,
                note => note.Contains(expectedNoteFragment, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Run_OpenConstructorVariableWithExplicitTypeArgument_RecordsUnresolvedConstructorBinding()
    {
        var genericSymbol = new SymbolId(9081);
        var callerSymbol = new SymbolId(9082);
        var openConstructorType = new TypeId(9083);
        var typeVariable = new TypeId(9084);
        var intType = new TypeId(BaseTypes.IntId);
        var unitType = new TypeId(BaseTypes.UnitId);

        var make = BuildFunction(
            returnType: openConstructorType,
            locals: [],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = openConstructorType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "make",
            symbolId: genericSymbol,
            genericParameterCount: 1,
            genericTypeParameterIds: [typeVariable]);
        var caller = BuildFunction(
            returnType: unitType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "result",
                    TypeId = openConstructorType
                }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = LocalPlace(1, openConstructorType),
                    Function = new MirFunctionRef
                    {
                        Name = "make",
                        SymbolId = genericSymbol,
                        TypeId = openConstructorType,
                        TypeArgumentIds = [intType]
                    },
                    Arguments = []
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "caller_open_constructor_failure",
            symbolId: callerSymbol);
        var module = new MirModule
        {
            Name = "open_constructor_failure",
            Functions = [make, caller],
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [typeVariable.Value] = new TypeDescriptor.TypeVar(0),
                [openConstructorType.Value] = new TypeDescriptor.TyCon("var:0", [typeVariable])
            }
        };

        var specializer = new MirGenericSpecializer();
        var specialized = specializer.Run(module);

        var failure = Assert.Single(specialized.SpecializationFailures);
        Assert.Equal("unresolved-constructor-binding", failure.Reason);
        Assert.Contains(
            specializer.Diagnostics,
            diagnostic => diagnostic.Code == "E5310" &&
                          diagnostic.Metadata.TryGetValue("reason", out var reason) &&
                          reason == "unresolved-constructor-binding");
        Assert.DoesNotContain(
            specialized.Functions,
            function => function.Name.StartsWith("make__spec_", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_FullApplicationWithMismatchedExplicitTypeArguments_FallsBackToInference()
    {
        var genericSymbol = new SymbolId(9091);
        var callerSymbol = new SymbolId(9092);
        var typeA = new TypeId(9093);
        var typeB = new TypeId(9094);
        var intType = new TypeId(BaseTypes.IntId);
        var unitType = new TypeId(BaseTypes.UnitId);

        var make = BuildFunction(
            returnType: typeA,
            locals: [],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = typeA,
                Value = new MirConstantValue.UnitValue()
            },
            name: "make_pairish",
            symbolId: genericSymbol,
            genericParameterCount: 2,
            genericTypeParameterIds: [typeA, typeB]);
        var caller = BuildFunction(
            returnType: unitType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "result",
                    TypeId = intType
                }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = LocalPlace(1, intType),
                    Function = new MirFunctionRef
                    {
                        Name = "make_pairish",
                        SymbolId = genericSymbol,
                        TypeId = intType,
                        TypeArgumentIds = [intType]
                    },
                    Arguments = []
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "caller_type_inference_failure",
            symbolId: callerSymbol);
        var module = new MirModule
        {
            Name = "type_inference_failure",
            Functions = [make, caller],
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [typeA.Value] = new TypeDescriptor.TypeVar(0),
                [typeB.Value] = new TypeDescriptor.TypeVar(1)
            }
        };

        var specializer = new MirGenericSpecializer();
        var specialized = specializer.Run(module);

        var specialization = Assert.Single(
            specialized.Functions,
            function => function.Name.StartsWith("make_pairish__spec_", StringComparison.Ordinal));
        Assert.Equal(intType, specialization.ReturnType);
        Assert.Empty(specialized.SpecializationFailures);

        var specializedCaller = Assert.Single(
            specialized.Functions,
            function => string.Equals(function.Name, "caller_type_inference_failure", StringComparison.Ordinal));
        var rewrittenCall = Assert.IsType<MirCall>(Assert.Single(specializedCaller.BasicBlocks[0].Instructions));
        var rewrittenFunctionRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(specialization.SymbolId, rewrittenFunctionRef.SymbolId);
        Assert.StartsWith("make_pairish__spec_", rewrittenFunctionRef.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_PartialApplicationWithoutBindableTarget_RecordsPartialBindingIncomplete()
    {
        var genericSymbol = new SymbolId(9101);
        var callerSymbol = new SymbolId(9102);
        var typeVariable = new TypeId(9103);
        var intType = new TypeId(BaseTypes.IntId);
        var unitType = new TypeId(BaseTypes.UnitId);

        var first = BuildFunction(
            returnType: typeVariable,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "left",
                    TypeId = typeVariable,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "right",
                    TypeId = typeVariable,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: LocalPlace(1, typeVariable),
            name: "first_unbound_partial",
            symbolId: genericSymbol,
            genericParameterCount: 1,
            genericTypeParameterIds: [typeVariable]);
        var callerArg = LocalPlace(1, intType);
        var caller = BuildFunction(
            returnType: unitType,
            locals:
            [
                new MirLocal
                {
                    Id = callerArg.Local,
                    Name = "arg",
                    TypeId = intType,
                    IsParameter = true
                }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = null,
                    Function = new MirFunctionRef
                    {
                        Name = "first_unbound_partial",
                        SymbolId = genericSymbol,
                        TypeId = TypeId.None
                    },
                    Arguments = [callerArg]
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "caller_unbound_partial",
            symbolId: callerSymbol);
        var module = new MirModule
        {
            Name = "partial_binding_incomplete",
            Functions = [first, caller],
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [typeVariable.Value] = new TypeDescriptor.TypeVar(0)
            }
        };

        var specializer = new MirGenericSpecializer();
        var specialized = specializer.Run(module);

        var failure = Assert.Single(specialized.SpecializationFailures);
        Assert.Equal("partial-binding-incomplete", failure.Reason);
        Assert.Contains(
            specializer.Diagnostics,
            diagnostic => diagnostic.Code == "E5310" &&
                          diagnostic.Metadata.TryGetValue("reason", out var reason) &&
                          reason == "partial-binding-incomplete");
    }

    [Fact]
    public void Run_ExplicitTypeArgumentWithConstructorVariable_BindsConstructorAcrossReturnType()
    {
        var genericSymbol = new SymbolId(9111);
        var callerSymbol = new SymbolId(9112);
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var templateConstructorOfInt = new TypeId(9113);
        var templateConstructorOfBool = new TypeId(9114);
        var optionOfInt = new TypeId(9115);
        var optionOfBool = new TypeId(9116);
        var optionConstructorType = new TypeId(9117);

        var makeBool = BuildFunction(
            returnType: templateConstructorOfBool,
            locals: [],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = templateConstructorOfBool,
                Value = new MirConstantValue.UnitValue()
            },
            name: "make_bool",
            symbolId: genericSymbol,
            genericParameterCount: 1,
            genericTypeParameterIds: [templateConstructorOfInt]);
        var result = LocalPlace(1, TypeId.None);
        var caller = BuildFunction(
            returnType: unitType,
            locals:
            [
                new MirLocal
                {
                    Id = result.Local,
                    Name = "result",
                    TypeId = TypeId.None
                }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = result,
                    Function = new MirFunctionRef
                    {
                        Name = "make_bool",
                        SymbolId = genericSymbol,
                        TypeId = templateConstructorOfBool,
                        TypeArgumentIds = [optionOfInt]
                    },
                    Arguments = []
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "caller_explicit_constructor_arg",
            symbolId: callerSymbol);
        var module = new MirModule
        {
            Name = "explicit_constructor_type_argument",
            Functions = [makeBool, caller],
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [templateConstructorOfInt.Value] = new TypeDescriptor.TyCon("var:0", [intType]),
                [templateConstructorOfBool.Value] = new TypeDescriptor.TyCon("var:0", [boolType]),
                [optionOfInt.Value] = new TypeDescriptor.TyCon($"type:{optionConstructorType.Value}", [intType]),
                [optionOfBool.Value] = new TypeDescriptor.TyCon($"type:{optionConstructorType.Value}", [boolType])
            }
        };

        var specialized = new MirGenericSpecializer().Run(module);

        var specialization = Assert.Single(
            specialized.Functions,
            function => function.Name.StartsWith("make_bool__spec_", StringComparison.Ordinal));
        Assert.Equal(optionOfBool, specialization.ReturnType);
        Assert.Empty(specialized.SpecializationFailures);
    }

    [Fact]
    public void Run_RepeatedConstructorVariableWithConflictingConcreteConstructors_RejectsSpecialization()
    {
        var genericSymbol = new SymbolId(9121);
        var callerSymbol = new SymbolId(9122);
        var intType = new TypeId(BaseTypes.IntId);
        var stringType = new TypeId(BaseTypes.StringId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var templateConstructorOfInt = new TypeId(9123);
        var templateConstructorOfString = new TypeId(9124);
        var boxOfInt = new TypeId(9125);
        var optionOfString = new TypeId(9126);
        var boxConstructorType = new TypeId(9127);
        var optionConstructorType = new TypeId(9128);

        var genericMerge = BuildFunction(
            returnType: unitType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "left",
                    TypeId = templateConstructorOfInt,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "right",
                    TypeId = templateConstructorOfString,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "merge_constructor",
            symbolId: genericSymbol,
            genericParameterCount: 1);
        var caller = BuildFunction(
            returnType: unitType,
            locals:
            [
                new MirLocal { Id = new LocalId { Value = 1 }, Name = "box", TypeId = boxOfInt, IsParameter = true },
                new MirLocal { Id = new LocalId { Value = 2 }, Name = "option", TypeId = optionOfString, IsParameter = true }
            ],
            instructions:
            [
                new MirCall
                {
                    Function = new MirFunctionRef
                    {
                        Name = "merge_constructor",
                        SymbolId = genericSymbol,
                        TypeId = unitType
                    },
                    Arguments = [LocalPlace(1, boxOfInt), LocalPlace(2, optionOfString)]
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "caller_conflicting_constructor",
            symbolId: callerSymbol);
        var module = new MirModule
        {
            Name = "conflicting_constructor_variable",
            Functions = [genericMerge, caller],
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [templateConstructorOfInt.Value] = new TypeDescriptor.TyCon("var:0", [intType]),
                [templateConstructorOfString.Value] = new TypeDescriptor.TyCon("var:0", [stringType]),
                [boxOfInt.Value] = new TypeDescriptor.TyCon($"type:{boxConstructorType.Value}", [intType]),
                [optionOfString.Value] = new TypeDescriptor.TyCon($"type:{optionConstructorType.Value}", [stringType])
            }
        };

        var specialized = new MirGenericSpecializer().Run(module);

        Assert.DoesNotContain(
            specialized.Functions,
            function => function.Name.StartsWith("merge_constructor__spec_", StringComparison.Ordinal));
        Assert.Contains(
            specialized.SpecializationFailures,
            failure => failure.Reason == "type-inference-failed");
    }
}
