using Eidosc.Mir;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public sealed class PayloadlessAdtRepresentationAnalysisTests
{
    [Fact]
    public void Analyze_PayloadBearingLocalInjectedFromExactCase_VetoesSourceCaseType()
    {
        var parentType = new TypeId(7200);
        var exactCaseType = new TypeId(7201);
        var payloadLocal = new LocalId { Value = 1 };
        var aliasLocal = new LocalId { Value = 2 };
        var injectedLocal = new LocalId { Value = 3 };
        var module = new MirModule
        {
            Name = "payload_case_injection",
            ConstructorLayouts =
            {
                [parentType.Value] =
                [
                    new ConstructorTypeLayout
                    {
                        TypeName = "Result",
                        ConstructorName = "Ok",
                        FieldTypeIds = [new TypeId(BaseTypes.IntId)]
                    }
                ],
                [exactCaseType.Value] =
                [
                    new ConstructorTypeLayout
                    {
                        TypeName = "Result.Ok",
                        ConstructorName = "Ok",
                        FieldTypeIds = []
                    }
                ]
            },
            Functions =
            [
                new MirFunc
                {
                    Name = "inject",
                    EntryBlockId = new BlockId { Value = 1 },
                    Locals =
                    [
                        new MirLocal { Id = payloadLocal, Name = "payload", TypeId = parentType },
                        new MirLocal { Id = aliasLocal, Name = "alias", TypeId = parentType },
                        new MirLocal { Id = injectedLocal, Name = "injected", TypeId = parentType }
                    ],
                    BasicBlocks =
                    [
                        new MirBasicBlock
                        {
                            Id = new BlockId { Value = 1 },
                            IsEntry = true,
                            Instructions =
                            [
                                new MirCall
                                {
                                    Target = LocalPlace(payloadLocal, parentType),
                                    Function = new MirFunctionRef
                                    {
                                        Name = "Ok",
                                        SymbolKind = Eidosc.Symbols.SymbolKind.Constructor,
                                        TypeId = parentType
                                    },
                                    Arguments =
                                    [
                                        new MirConstant
                                        {
                                            TypeId = new TypeId(BaseTypes.IntId),
                                            Value = new MirConstantValue.IntValue(1)
                                        }
                                    ]
                                },
                                new MirLoad
                                {
                                    Target = LocalPlace(aliasLocal, parentType),
                                    Source = LocalPlace(payloadLocal, parentType)
                                },
                                new MirCaseInject
                                {
                                    Target = LocalPlace(injectedLocal, parentType),
                                    Operand = LocalPlace(aliasLocal, parentType),
                                    SourceTypeId = exactCaseType,
                                    TargetTypeId = parentType
                                }
                            ],
                            Terminator = new MirReturn { Value = LocalPlace(injectedLocal, parentType) }
                        }
                    ]
                }
            ]
        };

        var scalarTypes = PayloadlessAdtRepresentationAnalysis.Analyze(module);

        Assert.DoesNotContain(exactCaseType.Value, scalarTypes);
    }

    [Fact]
    public void Analyze_NonScalarCallResultInjectedIntoAncestor_VetoesScalarTag()
    {
        var payloadCaseType = new TypeId(7101);
        var parentType = new TypeId(7102);
        var payload = new MirLocal
        {
            Id = new LocalId { Value = 1 },
            Name = "payload",
            TypeId = payloadCaseType
        };
        var parent = new MirLocal
        {
            Id = new LocalId { Value = 2 },
            Name = "parent",
            TypeId = parentType
        };
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirCall
                {
                    Target = LocalPlace(payload.Id, payloadCaseType),
                    Function = new MirFunctionRef { Name = "produce_payload" }
                },
                new MirCaseInject
                {
                    Target = LocalPlace(parent.Id, parentType),
                    Operand = LocalPlace(payload.Id, payloadCaseType),
                    SourceTypeId = payloadCaseType,
                    TargetTypeId = parentType
                }
            ],
            Terminator = new MirReturn { Value = LocalPlace(parent.Id, parentType) }
        };
        var module = new MirModule
        {
            Name = "non_scalar_injection",
            Functions =
            [
                new MirFunc
                {
                    Name = "main",
                    ReturnType = parentType,
                    Locals = [payload, parent],
                    BasicBlocks = [block]
                }
            ],
            ConstructorLayouts = new Dictionary<int, List<ConstructorTypeLayout>>
            {
                [payloadCaseType.Value] =
                [
                    new ConstructorTypeLayout
                    {
                        TypeName = "Option.Some",
                        ConstructorName = "Some",
                        FieldTypeIds = [new TypeId(BaseTypes.IntId)]
                    }
                ],
                [parentType.Value] =
                [
                    new ConstructorTypeLayout
                    {
                        TypeName = "Option",
                        ConstructorName = "Some",
                        FieldTypeIds = []
                    },
                    new ConstructorTypeLayout
                    {
                        TypeName = "Option",
                        ConstructorName = "None",
                        FieldTypeIds = []
                    }
                ]
            }
        };

        var scalarTypes = PayloadlessAdtRepresentationAnalysis.Analyze(module);

        Assert.DoesNotContain(parentType.Value, scalarTypes);
    }

    [Fact]
    public void Analyze_FieldfulSiblingSpecialization_VetoesFieldlessLayoutInSameFamily()
    {
        var fieldlessSpecialization = new TypeId(7201);
        var fieldfulSpecialization = new TypeId(7202);
        var family = new TypeConstructorKey(TypeConstructorKeyKind.Symbol, 42);
        var module = new MirModule
        {
            Name = "generic_family",
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [fieldlessSpecialization.Value] = new TypeDescriptor.TyCon(
                    family,
                    [new TypeId(BaseTypes.IntId)]),
                [fieldfulSpecialization.Value] = new TypeDescriptor.TyCon(
                    family,
                    [new TypeId(BaseTypes.StringId)])
            },
            ConstructorLayouts = new Dictionary<int, List<ConstructorTypeLayout>>
            {
                [fieldlessSpecialization.Value] =
                [
                    new ConstructorTypeLayout
                    {
                        TypeName = "Option_Int",
                        ConstructorName = "Some",
                        FieldTypeIds = []
                    },
                    new ConstructorTypeLayout
                    {
                        TypeName = "Option_Int",
                        ConstructorName = "None",
                        FieldTypeIds = []
                    }
                ],
                [fieldfulSpecialization.Value] =
                [
                    new ConstructorTypeLayout
                    {
                        TypeName = "Option_String",
                        ConstructorName = "Some",
                        FieldTypeIds = [new TypeId(BaseTypes.StringId)]
                    },
                    new ConstructorTypeLayout
                    {
                        TypeName = "Option_String",
                        ConstructorName = "None",
                        FieldTypeIds = []
                    }
                ]
            }
        };

        var scalarTypes = PayloadlessAdtRepresentationAnalysis.Analyze(module);

        Assert.DoesNotContain(fieldlessSpecialization.Value, scalarTypes);
    }

    [Fact]
    public void Analyze_ConstructorFieldBackedByOpaqueRuntimeValue_VetoesFieldLayoutFamily()
    {
        var expectedOpaqueType = new TypeId(7301);
        var siblingOpaqueType = new TypeId(7302);
        var runtimeSharedType = new TypeId(7303);
        var linkType = new TypeId(7304);
        var sharedFamily = new TypeConstructorKey(TypeConstructorKeyKind.Symbol, 953);
        var sharedLocal = new LocalId { Value = 1 };
        var linkLocal = new LocalId { Value = 2 };
        var module = new MirModule
        {
            Name = "opaque_runtime_constructor_field",
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [expectedOpaqueType.Value] = new TypeDescriptor.TyCon(
                    sharedFamily,
                    [new TypeId(BaseTypes.IntId)]),
                [siblingOpaqueType.Value] = new TypeDescriptor.TyCon(
                    sharedFamily,
                    [new TypeId(BaseTypes.StringId)]),
                [runtimeSharedType.Value] = new TypeDescriptor.Shared(new TypeId(BaseTypes.IntId))
            },
            ConstructorLayouts = new Dictionary<int, List<ConstructorTypeLayout>>
            {
                [expectedOpaqueType.Value] =
                [
                    new ConstructorTypeLayout
                    {
                        TypeName = "Shared_Int",
                        ConstructorName = "Shared",
                        FieldTypeIds = []
                    }
                ],
                [siblingOpaqueType.Value] =
                [
                    new ConstructorTypeLayout
                    {
                        TypeName = "Shared_String",
                        ConstructorName = "Shared",
                        FieldTypeIds = []
                    }
                ],
                [linkType.Value] =
                [
                    new ConstructorTypeLayout
                    {
                        TypeName = "Link_Int_Int",
                        ConstructorName = "Link",
                        FieldTypeIds = [expectedOpaqueType]
                    }
                ]
            },
            Functions =
            [
                new MirFunc
                {
                    Name = "wrap_shared",
                    ReturnType = linkType,
                    EntryBlockId = new BlockId { Value = 1 },
                    Locals =
                    [
                        new MirLocal
                        {
                            Id = sharedLocal,
                            Name = "shared",
                            TypeId = runtimeSharedType,
                            IsParameter = true
                        },
                        new MirLocal { Id = linkLocal, Name = "link", TypeId = linkType }
                    ],
                    BasicBlocks =
                    [
                        new MirBasicBlock
                        {
                            Id = new BlockId { Value = 1 },
                            IsEntry = true,
                            Instructions =
                            [
                                new MirCall
                                {
                                    Target = LocalPlace(linkLocal, linkType),
                                    Function = new MirFunctionRef
                                    {
                                        Name = "Link",
                                        SymbolKind = Eidosc.Symbols.SymbolKind.Constructor,
                                        TypeId = linkType
                                    },
                                    Arguments = [LocalPlace(sharedLocal, runtimeSharedType)]
                                }
                            ],
                            Terminator = new MirReturn { Value = LocalPlace(linkLocal, linkType) }
                        }
                    ]
                }
            ]
        };

        var scalarTypes = PayloadlessAdtRepresentationAnalysis.Analyze(module);

        Assert.DoesNotContain(expectedOpaqueType.Value, scalarTypes);
        Assert.DoesNotContain(siblingOpaqueType.Value, scalarTypes);
    }

    [Fact]
    public void Analyze_ConstructorFieldBackedByNonSharedValue_DoesNotVetoFieldLayoutFamily()
    {
        var expectedPayloadlessType = new TypeId(7401);
        var actualValueType = new TypeId(7402);
        var wrapperType = new TypeId(7403);
        var valueLocal = new LocalId { Value = 1 };
        var wrapperLocal = new LocalId { Value = 2 };
        var module = new MirModule
        {
            Name = "non_shared_constructor_field",
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [expectedPayloadlessType.Value] = new TypeDescriptor.TyCon(
                    new TypeConstructorKey(TypeConstructorKeyKind.Symbol, 954),
                    [new TypeId(BaseTypes.IntId)]),
                [actualValueType.Value] = new TypeDescriptor.Builtin(actualValueType.Value)
            },
            ConstructorLayouts = new Dictionary<int, List<ConstructorTypeLayout>>
            {
                [expectedPayloadlessType.Value] =
                [
                    new ConstructorTypeLayout
                    {
                        TypeName = "Marker_Int",
                        ConstructorName = "Marker",
                        FieldTypeIds = []
                    }
                ],
                [wrapperType.Value] =
                [
                    new ConstructorTypeLayout
                    {
                        TypeName = "Wrapper",
                        ConstructorName = "Wrapper",
                        FieldTypeIds = [expectedPayloadlessType]
                    }
                ]
            },
            Functions =
            [
                new MirFunc
                {
                    Name = "wrap_value",
                    ReturnType = wrapperType,
                    EntryBlockId = new BlockId { Value = 1 },
                    Locals =
                    [
                        new MirLocal
                        {
                            Id = valueLocal,
                            Name = "value",
                            TypeId = actualValueType,
                            IsParameter = true
                        },
                        new MirLocal { Id = wrapperLocal, Name = "wrapper", TypeId = wrapperType }
                    ],
                    BasicBlocks =
                    [
                        new MirBasicBlock
                        {
                            Id = new BlockId { Value = 1 },
                            IsEntry = true,
                            Instructions =
                            [
                                new MirCall
                                {
                                    Target = LocalPlace(wrapperLocal, wrapperType),
                                    Function = new MirFunctionRef
                                    {
                                        Name = "Wrapper",
                                        SymbolKind = Eidosc.Symbols.SymbolKind.Constructor,
                                        TypeId = wrapperType
                                    },
                                    Arguments = [LocalPlace(valueLocal, actualValueType)]
                                }
                            ],
                            Terminator = new MirReturn { Value = LocalPlace(wrapperLocal, wrapperType) }
                        }
                    ]
                }
            ]
        };

        var scalarTypes = PayloadlessAdtRepresentationAnalysis.Analyze(module);

        Assert.Contains(expectedPayloadlessType.Value, scalarTypes);
    }

    private static MirPlace LocalPlace(LocalId local, TypeId typeId) =>
        new()
        {
            Kind = PlaceKind.Local,
            Local = local,
            TypeId = typeId
        };
}
