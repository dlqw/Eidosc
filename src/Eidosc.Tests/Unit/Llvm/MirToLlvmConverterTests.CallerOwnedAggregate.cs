using System.Text.RegularExpressions;
using Eidosc.CodeGen.Llvm;
using Eidosc.Mir;
using Eidosc.Symbols;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Llvm;

public partial class MirToLlvmConverterTests
{
    [Fact]
    public void Convert_CallerOwnedOutAbi_UsesCallerStorageAndUniqueFieldDropNames()
    {
        var recordType = new TypeId(9910);
        var stringType = new TypeId(BaseTypes.StringId);
        var intType = new TypeId(BaseTypes.IntId);
        var factoryIdentity = CallerOwnedIdentity("make_owned");
        var returned = new LocalId { Value = 1 };
        var factory = new MirFunc
        {
            Name = "make_owned__out",
            SourceName = "make_owned__out",
            FunctionId = factoryIdentity with { StableIdentityKey = "test:make_owned|caller-out" },
            ReturnType = recordType,
            EntryBlockId = new BlockId { Value = 1 },
            Locals = [new MirLocal { Id = returned, Name = "result", TypeId = recordType }],
            CallerOwnedAggregateAbi = new MirCallerOwnedAggregateAbi
            {
                OutReturnType = recordType,
                OutReturnLocals = new HashSet<LocalId> { returned }
            },
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
                            Target = CallerOwnedPlace(returned, recordType),
                            Function = new MirFunctionRef
                            {
                                Name = "Owned",
                                SymbolKind = SymbolKind.Constructor,
                                TypeId = recordType
                            },
                            Arguments =
                            [
                                new MirConstant
                                {
                                    TypeId = stringType,
                                    Value = new MirConstantValue.StringValue("payload")
                                }
                            ]
                        }
                    ],
                    Terminator = new MirReturn { Value = CallerOwnedPlace(returned, recordType) }
                }
            ]
        };
        var local = new LocalId { Value = 1 };
        var caller = new MirFunc
        {
            Name = "use_owned",
            SourceName = "use_owned",
            FunctionId = CallerOwnedIdentity("use_owned"),
            ReturnType = intType,
            EntryBlockId = new BlockId { Value = 1 },
            Locals = [new MirLocal { Id = local, Name = "owned", TypeId = recordType }],
            CallerOwnedAggregateAbi = new MirCallerOwnedAggregateAbi
            {
                LocalGroups =
                [
                    new MirCallerOwnedAggregateGroup
                    {
                        CanonicalLocal = local,
                        TypeId = recordType,
                        Locals = new HashSet<LocalId> { local }
                    }
                ]
            },
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
                            Target = CallerOwnedPlace(local, recordType),
                            Function = new MirFunctionRef
                            {
                                Name = factory.Name,
                                FunctionId = factory.FunctionId,
                                TypeId = recordType
                            }
                        },
                        new MirDrop { Value = CallerOwnedPlace(local, recordType) },
                        new MirCall
                        {
                            Target = CallerOwnedPlace(local, recordType),
                            Function = new MirFunctionRef
                            {
                                Name = factory.Name,
                                FunctionId = factory.FunctionId,
                                TypeId = recordType
                            }
                        },
                        new MirDrop { Value = CallerOwnedPlace(local, recordType) }
                    ],
                    Terminator = new MirReturn
                    {
                        Value = new MirConstant
                        {
                            TypeId = intType,
                            Value = new MirConstantValue.IntValue(0)
                        }
                    }
                }
            ]
        };
        var module = new MirModule
        {
            Name = "caller_owned",
            Functions = [factory, caller],
            ConstructorLayouts = new Dictionary<int, List<ConstructorTypeLayout>>
            {
                [recordType.Value] =
                [
                    new ConstructorTypeLayout
                    {
                        TypeName = "Owned",
                        ConstructorName = "Owned",
                        FieldTypeIds = [stringType]
                    }
                ]
            }
        };

        var ir = new LlvmEmitter().Emit(new MirToLlvmConverter().Convert(module));

        Assert.Matches(@"define private void @.*make_owned.*\(ptr %__aggregate_out\)", ir);
        Assert.Matches(@"%aggregate_l1_\d+ = alloca %struct\.eidos_Owned", ir);
        Assert.Equal(2, Regex.Matches(ir, @"call void @eidos_decref_shared\(ptr %aggregate_drop_field0_ptr_\d+_payload_val\)").Count);
        Assert.Equal(
            2,
            Regex.Matches(
                ir,
                @"call void @eidos_decref_shared\(ptr %aggregate_drop_field0_ptr_\d+_payload_val\)\r?\n\s+store ptr null, ptr %aggregate_drop_field0_ptr_\d+").Count);
        var assignedNames = Regex.Matches(ir, @"(?m)^\s*(%[A-Za-z0-9_]+)\s*=")
            .Select(match => match.Groups[1].Value)
            .ToArray();
        Assert.Equal(assignedNames.Length, assignedNames.Distinct(StringComparer.Ordinal).Count());
        var outDefinition = Regex.Match(ir, @"define private void @.*make_owned[\s\S]*?^}", RegexOptions.Multiline).Value;
        Assert.DoesNotContain("eidos_alloc", outDefinition, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_CallerOwnedNestedTupleDrop_RecursivelyClearsManagedSlots()
    {
        var recordType = new TypeId(9911);
        var tupleType = new TypeId(9912);
        var stringType = new TypeId(BaseTypes.StringId);
        var intType = new TypeId(BaseTypes.IntId);
        var factoryTuple = new LocalId { Value = 1 };
        var returned = new LocalId { Value = 2 };
        var factory = new MirFunc
        {
            Name = "make_nested_tuple__out",
            SourceName = "make_nested_tuple__out",
            FunctionId = CallerOwnedIdentity("make_nested_tuple") with
            {
                StableIdentityKey = "test:make_nested_tuple|caller-out"
            },
            ReturnType = recordType,
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal
                {
                    Id = factoryTuple,
                    Name = "payload",
                    TypeId = tupleType,
                    IsParameter = true
                },
                new MirLocal { Id = returned, Name = "result", TypeId = recordType }
            ],
            CallerOwnedAggregateAbi = new MirCallerOwnedAggregateAbi
            {
                OutReturnType = recordType,
                OutReturnLocals = new HashSet<LocalId> { returned }
            },
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
                            Target = CallerOwnedPlace(returned, recordType),
                            Function = new MirFunctionRef
                            {
                                Name = "NestedTuple",
                                SymbolKind = SymbolKind.Constructor,
                                TypeId = recordType
                            },
                            Arguments = [CallerOwnedPlace(factoryTuple, tupleType)]
                        }
                    ],
                    Terminator = new MirReturn { Value = CallerOwnedPlace(returned, recordType) }
                }
            ]
        };
        var callerTuple = new LocalId { Value = 1 };
        var local = new LocalId { Value = 2 };
        var caller = new MirFunc
        {
            Name = "use_nested_tuple",
            SourceName = "use_nested_tuple",
            FunctionId = CallerOwnedIdentity("use_nested_tuple"),
            ReturnType = intType,
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal
                {
                    Id = callerTuple,
                    Name = "payload",
                    TypeId = tupleType,
                    IsParameter = true
                },
                new MirLocal { Id = local, Name = "owned", TypeId = recordType }
            ],
            CallerOwnedAggregateAbi = new MirCallerOwnedAggregateAbi
            {
                LocalGroups =
                [
                    new MirCallerOwnedAggregateGroup
                    {
                        CanonicalLocal = local,
                        TypeId = recordType,
                        Locals = new HashSet<LocalId> { local }
                    }
                ]
            },
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
                            Target = CallerOwnedPlace(local, recordType),
                            Function = new MirFunctionRef
                            {
                                Name = factory.Name,
                                FunctionId = factory.FunctionId,
                                TypeId = recordType
                            },
                            Arguments = [CallerOwnedPlace(callerTuple, tupleType)]
                        },
                        new MirDrop { Value = CallerOwnedPlace(local, recordType) },
                        new MirDrop { Value = CallerOwnedPlace(local, recordType) }
                    ],
                    Terminator = new MirReturn
                    {
                        Value = new MirConstant
                        {
                            TypeId = intType,
                            Value = new MirConstantValue.IntValue(0)
                        }
                    }
                }
            ]
        };
        var module = new MirModule
        {
            Name = "caller_owned_nested_tuple",
            Functions = [factory, caller],
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [tupleType.Value] = new TypeDescriptor.Tuple([stringType, intType])
            },
            ConstructorLayouts = new Dictionary<int, List<ConstructorTypeLayout>>
            {
                [recordType.Value] =
                [
                    new ConstructorTypeLayout
                    {
                        TypeName = "NestedTuple",
                        ConstructorName = "NestedTuple",
                        FieldTypeIds = [tupleType]
                    }
                ]
            }
        };

        var ir = new LlvmEmitter().Emit(new MirToLlvmConverter().Convert(module));

        Assert.Equal(
            2,
            Regex.Matches(
                ir,
                @"call void @eidos_decref_shared\(ptr %aggregate_drop_field0_ptr_\d+_payload_field0_val\)").Count);
        Assert.Equal(
            2,
            Regex.Matches(
                ir,
                @"store ptr null, ptr %aggregate_drop_field0_ptr_\d+_payload_clear_field0_ptr_\d+").Count);
    }

    [Fact]
    public void Convert_CallerOwnedNestedArray_UsesConcreteInlineStorageAndManagedPolicy()
    {
        var recordType = new TypeId(9920);
        var arrayType = new TypeId(9921);
        var returned = new LocalId { Value = 1 };
        var array = new LocalId { Value = 2 };
        var storage = new MirCallerOwnedArrayStorage
        {
            Key = "test:nested|array:2",
            ArrayLocal = array,
            ArrayTypeId = arrayType,
            Capacity = 3,
            ElementSize = 8,
            StorageBytes = 88
        };
        var factory = new MirFunc
        {
            Name = "make_nested__out",
            FunctionId = CallerOwnedIdentity("make_nested") with
            {
                StableIdentityKey = "test:make_nested|caller-out"
            },
            ReturnType = recordType,
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = returned, Name = "result", TypeId = recordType },
                new MirLocal { Id = array, Name = "items", TypeId = arrayType }
            ],
            CallerOwnedAggregateAbi = new MirCallerOwnedAggregateAbi
            {
                OutReturnType = recordType,
                OutReturnLocals = new HashSet<LocalId> { returned },
                OutArrayStorages = [storage]
            },
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
                            Target = CallerOwnedPlace(array, arrayType),
                            Function = MirRuntimeFunctions.CreateFunctionRef(
                                WellKnownStrings.InternalNames.ArrayNew,
                                arrayType,
                                SourceSpan.Empty),
                            Arguments =
                            [
                                new MirConstant
                                {
                                    TypeId = new TypeId(BaseTypes.IntId),
                                    Value = new MirConstantValue.IntValue(3)
                                },
                                new MirConstant
                                {
                                    TypeId = new TypeId(BaseTypes.IntId),
                                    Value = new MirConstantValue.IntValue(8)
                                }
                            ]
                        },
                        new MirCall
                        {
                            Target = CallerOwnedPlace(returned, recordType),
                            Function = new MirFunctionRef
                            {
                                Name = "Nested",
                                SymbolKind = SymbolKind.Constructor,
                                TypeId = recordType
                            },
                            Arguments = [CallerOwnedPlace(array, arrayType)]
                        }
                    ],
                    Terminator = new MirReturn { Value = CallerOwnedPlace(returned, recordType) }
                }
            ]
        };
        var local = new LocalId { Value = 1 };
        var caller = new MirFunc
        {
            Name = "use_nested",
            FunctionId = CallerOwnedIdentity("use_nested"),
            ReturnType = new TypeId(BaseTypes.IntId),
            EntryBlockId = new BlockId { Value = 1 },
            Locals = [new MirLocal { Id = local, Name = "nested", TypeId = recordType }],
            CallerOwnedAggregateAbi = new MirCallerOwnedAggregateAbi
            {
                LocalGroups =
                [
                    new MirCallerOwnedAggregateGroup
                    {
                        CanonicalLocal = local,
                        TypeId = recordType,
                        Locals = new HashSet<LocalId> { local },
                        ArrayStorages = [storage]
                    }
                ]
            },
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
                            Target = CallerOwnedPlace(local, recordType),
                            Function = new MirFunctionRef
                            {
                                Name = factory.Name,
                                FunctionId = factory.FunctionId,
                                TypeId = recordType
                            }
                        },
                        new MirDrop { Value = CallerOwnedPlace(local, recordType) }
                    ],
                    Terminator = new MirReturn
                    {
                        Value = new MirConstant
                        {
                            TypeId = new TypeId(BaseTypes.IntId),
                            Value = new MirConstantValue.IntValue(0)
                        }
                    }
                }
            ]
        };
        var module = new MirModule
        {
            Functions = [factory, caller],
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [arrayType.Value] = new TypeDescriptor.TyCon(
                    TypeConstructorKey.FromTypeId(arrayType),
                    [new TypeId(BaseTypes.StringId)])
            },
            ConstructorLayouts = new Dictionary<int, List<ConstructorTypeLayout>>
            {
                [recordType.Value] =
                [
                    new ConstructorTypeLayout
                    {
                        TypeName = "Nested",
                        ConstructorName = "Nested",
                        FieldTypeIds = [arrayType]
                    }
                ]
            }
        };

        var ir = new LlvmEmitter().Emit(new MirToLlvmConverter().Convert(module));

        Assert.Matches(@"alloca \{%struct\.eidos_Nested, \[11 x i64\]\}", ir);
        Assert.Matches(@"define private void @.*make_nested.*\(ptr %__aggregate_out, ptr %__array_storage_0\)", ir);
        Assert.Matches(@"call ptr @eidos_array_new_in_storage\(ptr %__array_storage_0, i64 88, i64 3, i64 8, ptr @eidos_array_retain_elem__[0-9A-F]+, ptr @eidos_array_release_elem__[0-9A-F]+\)", ir);
        var outDefinition = Regex.Match(ir, @"define private void @.*make_nested[\s\S]*?^}", RegexOptions.Multiline).Value;
        Assert.DoesNotContain("eidos_array_new_with_policy", outDefinition, StringComparison.Ordinal);
    }

    private static MirPlace CallerOwnedPlace(LocalId local, TypeId typeId) => new()
    {
        Kind = PlaceKind.Local,
        Local = local,
        TypeId = typeId
    };

    private static FunctionId CallerOwnedIdentity(string name) => new()
    {
        StableIdentityKey = $"test:{name}",
        Name = name,
        QualifiedName = $"test.{name}"
    };
}
