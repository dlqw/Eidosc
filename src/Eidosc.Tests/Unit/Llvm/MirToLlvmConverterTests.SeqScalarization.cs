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
    public void Convert_PromotedArrayLength_ReadsLiveLocalPointerWithoutRuntimeCall()
    {
        var recordType = new TypeId(9911);
        var arrayType = new TypeId(9912);
        var intType = new TypeId(BaseTypes.IntId);
        var stringType = new TypeId(BaseTypes.StringId);
        var returned = new LocalId { Value = 1 };
        var array = new LocalId { Value = 2 };
        var length = new LocalId { Value = 3 };
        var factoryIdentity = CallerOwnedIdentity("make_owned");
        var factory = new MirFunc
        {
            Name = "make_owned__out",
            SourceName = "make_owned__out",
            FunctionId = factoryIdentity with { StableIdentityKey = "test:make_owned|caller-out" },
            ReturnType = recordType,
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = returned, Name = "result", TypeId = recordType },
                new MirLocal { Id = array, Name = "items", TypeId = arrayType },
                new MirLocal { Id = length, Name = "len", TypeId = intType }
            ],
            CallerOwnedAggregateAbi = new MirCallerOwnedAggregateAbi
            {
                OutReturnType = recordType,
                OutReturnLocals = new HashSet<LocalId> { returned },
                OutArrayStorages =
                [
                    new MirCallerOwnedArrayStorage
                    {
                        Key = "test:make_owned|array:2",
                        ArrayLocal = array,
                        ArrayTypeId = arrayType,
                        Capacity = 3,
                        ElementSize = 8,
                        StorageBytes = 88,
                        PromoteInline = true
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
                            Target = CallerOwnedPlace(array, arrayType),
                            Function = MirRuntimeFunctions.CreateFunctionRef(
                                WellKnownStrings.InternalNames.ArrayNew,
                                arrayType,
                                default),
                            Arguments =
                            [
                                new MirConstant
                                {
                                    TypeId = intType,
                                    Value = new MirConstantValue.IntValue(3)
                                },
                                new MirConstant
                                {
                                    TypeId = intType,
                                    Value = new MirConstantValue.IntValue(8)
                                }
                            ]
                        },
                        new MirCall
                        {
                            Target = CallerOwnedPlace(returned, recordType),
                            Function = new MirFunctionRef
                            {
                                Name = "Owned",
                                SymbolKind = SymbolKind.Constructor,
                                TypeId = recordType
                            },
                            Arguments = [CallerOwnedPlace(array, arrayType)]
                        },
                        new MirCall
                        {
                            Target = CallerOwnedPlace(length, intType),
                            Function = MirRuntimeFunctions.CreateFunctionRef(
                                WellKnownStrings.InternalNames.ArrayLength,
                                intType,
                                default),
                            Arguments = [CallerOwnedPlace(array, arrayType)]
                        }
                    ],
                    Terminator = new MirReturn { Value = CallerOwnedPlace(returned, recordType) }
                }
            ]
        };
        var module = new MirModule
        {
            Name = "seq_scalarization_local",
            Functions = [factory],
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [arrayType.Value] = new TypeDescriptor.TyCon(
                    TypeConstructorKey.FromTypeId(arrayType),
                    [stringType])
            },
            ConstructorLayouts = new Dictionary<int, List<ConstructorTypeLayout>>
            {
                [recordType.Value] =
                [
                    new ConstructorTypeLayout
                    {
                        TypeName = "Owned",
                        ConstructorName = "Owned",
                        FieldTypeIds = [arrayType]
                    }
                ]
            }
        };

        var ir = new LlvmEmitter().Emit(new MirToLlvmConverter().Convert(module));
        var definition = Regex.Match(ir, @"define private void @.*make_owned[\s\S]*?^}", RegexOptions.Multiline).Value;

        Assert.Matches(@"%promoted_length_\d+ = load i64, ptr %promoted_length_ptr_\d+", definition);
        Assert.Matches(@"%promoted_array_null_\d+ = icmp eq ptr %[A-Za-z0-9_]+_\d+, null", definition);
        Assert.Matches(@"%promoted_length_guarded_\d+ = select i1 %promoted_array_null_\d+, i64 0, i64 %promoted_length_\d+", definition);
        Assert.DoesNotContain("call i64 @eidos_array_length", definition, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_LocalSequenceStorage_UsesEntryAllocaAndLivePointerAfterGrowth()
    {
        var arrayType = new TypeId(9915);
        var intType = new TypeId(BaseTypes.IntId);
        var array = new LocalId { Value = 1 };
        var length = new LocalId { Value = 2 };
        var function = new MirFunc
        {
            Name = "local_sequence_storage",
            SourceName = "local_sequence_storage",
            FunctionId = CallerOwnedIdentity("local_sequence_storage"),
            ReturnType = intType,
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = array, Name = "items", TypeId = arrayType },
                new MirLocal { Id = length, Name = "len", TypeId = intType }
            ],
            CallerOwnedAggregateAbi = new MirCallerOwnedAggregateAbi
            {
                LocalArrayStorages =
                [
                    new MirCallerOwnedArrayStorage
                    {
                        Key = "test:local_sequence_storage|local-array:1",
                        ArrayLocal = array,
                        ArrayTypeId = arrayType,
                        Capacity = 2,
                        ElementSize = 8,
                        StorageBytes = 80,
                        PromoteInline = true
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
                        ArrayCall(WellKnownStrings.InternalNames.ArrayNew, array, arrayType, Constant(2), Constant(8)),
                        ArrayCall(
                            WellKnownStrings.InternalNames.ArrayPush,
                            array,
                            arrayType,
                            CallerOwnedPlace(array, arrayType),
                            Constant(1),
                            Constant(8)),
                        ArrayCall(
                            WellKnownStrings.InternalNames.ArrayPush,
                            array,
                            arrayType,
                            CallerOwnedPlace(array, arrayType),
                            Constant(2),
                            Constant(8)),
                        ArrayCall(
                            WellKnownStrings.InternalNames.ArrayPush,
                            array,
                            arrayType,
                            CallerOwnedPlace(array, arrayType),
                            Constant(3),
                            Constant(8)),
                        ArrayCall(
                            WellKnownStrings.InternalNames.ArrayLength,
                            length,
                            intType,
                            CallerOwnedPlace(array, arrayType))
                    ],
                    Terminator = new MirReturn { Value = CallerOwnedPlace(length, intType) }
                }
            ]
        };
        var module = new MirModule
        {
            Name = "local_sequence_storage",
            Functions = [function],
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [arrayType.Value] = new TypeDescriptor.TyCon(
                    TypeConstructorKey.FromTypeId(arrayType),
                    [intType])
            }
        };

        var ir = new LlvmEmitter().Emit(new MirToLlvmConverter().Convert(module));
        var definition = Regex.Match(
            ir,
            @"define .* @.*local_sequence_storage[\s\S]*?^}",
            RegexOptions.Multiline).Value;

        Assert.Matches(@"%sequence_l1_storage_\d+ = alloca \[80 x i8\], align 8", definition);
        Assert.Matches(
            @"call ptr @eidos_array_new_in_storage\(ptr %sequence_l1_storage_\d+, i64 80, i64 2, i64 8, ptr null, ptr null\)",
            definition);
        Assert.Equal(3, Regex.Matches(definition, @"call ptr @eidos_array_push").Count);
        Assert.Matches(@"%promoted_length_\d+ = load i64, ptr %promoted_length_ptr_\d+", definition);
        Assert.DoesNotContain("call i64 @eidos_array_length", definition, StringComparison.Ordinal);

        static MirCall ArrayCall(
            string name,
            LocalId target,
            TypeId targetType,
            params MirOperand[] arguments) => new()
        {
            Target = CallerOwnedPlace(target, targetType),
            Function = MirRuntimeFunctions.CreateFunctionRef(name, targetType, default),
            Arguments = arguments.ToList()
        };

        static MirConstant Constant(long value) => new()
        {
            TypeId = new TypeId(BaseTypes.IntId),
            Value = new MirConstantValue.IntValue(value)
        };
    }

    [Fact]
    public void Convert_PromotedArrayLength_FieldProjection_ReadsLiveAggregateFieldPointer()
    {
        var recordType = new TypeId(9913);
        var arrayType = new TypeId(9914);
        var intType = new TypeId(BaseTypes.IntId);
        var stringType = new TypeId(BaseTypes.StringId);
        var returned = new LocalId { Value = 1 };
        var array = new LocalId { Value = 2 };
        var factoryIdentity = CallerOwnedIdentity("make_owned");
        var storage = new MirCallerOwnedArrayStorage
        {
            Key = "test:make_owned|array:2",
            ArrayLocal = array,
            ArrayTypeId = arrayType,
            Capacity = 3,
            ElementSize = 8,
            StorageBytes = 88,
            PromoteInline = true
        };
        var factory = new MirFunc
        {
            Name = "make_owned__out",
            SourceName = "make_owned__out",
            FunctionId = factoryIdentity with { StableIdentityKey = "test:make_owned|caller-out" },
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
                            Target = CallerOwnedPlace(returned, recordType),
                            Function = new MirFunctionRef
                            {
                                Name = "Owned",
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

        var groupLocal = new LocalId { Value = 1 };
        var length = new LocalId { Value = 2 };
        var caller = new MirFunc
        {
            Name = "use_owned",
            SourceName = "use_owned",
            FunctionId = CallerOwnedIdentity("use_owned"),
            ReturnType = intType,
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = groupLocal, Name = "owned", TypeId = recordType },
                new MirLocal { Id = length, Name = "len", TypeId = intType }
            ],
            CallerOwnedAggregateAbi = new MirCallerOwnedAggregateAbi
            {
                LocalGroups =
                [
                    new MirCallerOwnedAggregateGroup
                    {
                        CanonicalLocal = groupLocal,
                        TypeId = recordType,
                        Locals = new HashSet<LocalId> { groupLocal },
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
                            Target = CallerOwnedPlace(groupLocal, recordType),
                            Function = new MirFunctionRef
                            {
                                Name = factory.Name,
                                FunctionId = factory.FunctionId,
                                TypeId = recordType
                            }
                        },
                        new MirCall
                        {
                            Target = CallerOwnedPlace(length, intType),
                            Function = MirRuntimeFunctions.CreateFunctionRef(
                                WellKnownStrings.InternalNames.ArrayLength,
                                intType,
                                default),
                            Arguments =
                            [
                                new MirPlace
                                {
                                    Kind = PlaceKind.Field,
                                    FieldName = "_0",
                                    Base = new MirPlace
                                    {
                                        Kind = PlaceKind.Local,
                                        Local = groupLocal,
                                        TypeId = recordType
                                    },
                                    TypeId = arrayType
                                }
                            ]
                        }
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
            Name = "seq_scalarization_field",
            Functions = [factory, caller],
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [arrayType.Value] = new TypeDescriptor.TyCon(
                    TypeConstructorKey.FromTypeId(arrayType),
                    [stringType])
            },
            ConstructorLayouts = new Dictionary<int, List<ConstructorTypeLayout>>
            {
                [recordType.Value] =
                [
                    new ConstructorTypeLayout
                    {
                        TypeName = "Owned",
                        ConstructorName = "Owned",
                        FieldTypeIds = [arrayType]
                    }
                ]
            }
        };

        var ir = new LlvmEmitter().Emit(new MirToLlvmConverter().Convert(module));
        var definition = Regex.Match(ir, @"define (?:private|external) i64 @.*use_owned[\s\S]*?^}", RegexOptions.Multiline).Value;

        Assert.Matches(@"%promoted_array_ptr_\d+ = load ptr, ptr %field_\d+", definition);
        Assert.Matches(@"%promoted_array_null_\d+ = icmp eq ptr %promoted_array_ptr_\d+, null", definition);
        Assert.Matches(@"%promoted_length_\d+ = load i64, ptr %promoted_length_ptr_\d+", definition);
        Assert.DoesNotContain("call i64 @eidos_array_length", definition, StringComparison.Ordinal);
    }
}
