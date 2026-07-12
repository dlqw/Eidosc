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
    public void Run_NonZeroArgGenericPartialWithResolverCopyBoundLocal_RewritesToSpecializedDirectCall()
    {
        var genericSymbol = new SymbolId(1227);
        var customType = new TypeId(9001);
        var boolType = new TypeId(BaseTypes.BoolId);

        var genericFirst = BuildFunction(
            returnType: TypeId.None,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "a",
                    TypeId = TypeId.None,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "b",
                    TypeId = TypeId.None,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: LocalPlace(1, TypeId.None),
            name: "first",
            symbolId: genericSymbol);

        var argA = LocalPlace(1, customType);
        var partialSlot = LocalPlace(2, TypeId.None);
        var argB = LocalPlace(3, boolType);
        var resultSlot = LocalPlace(4, customType);
        var caller = BuildFunction(
            returnType: customType,
            locals:
            [
                new MirLocal { Id = argA.Local, Name = "a", TypeId = customType, IsParameter = true },
                new MirLocal { Id = partialSlot.Local, Name = "f", TypeId = TypeId.None },
                new MirLocal { Id = argB.Local, Name = "b", TypeId = boolType, IsParameter = true },
                new MirLocal { Id = resultSlot.Local, Name = "res", TypeId = customType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = partialSlot,
                    Function = new MirFunctionRef
                    {
                        Name = "first",
                        SymbolId = genericSymbol,
                        TypeId = TypeId.None
                    },
                    Arguments = [argA]
                },
                new MirCall
                {
                    Target = resultSlot,
                    Function = partialSlot,
                    Arguments = [argB]
                }
            ],
            returnValue: resultSlot,
            name: "caller_nonzero_partial_resolver_copy_local",
            symbolId: new SymbolId(1228));

        var module = new MirModule
        {
            Name = "generic_nonzero_partial_resolver_copy_local",
            Functions = [genericFirst, caller]
        };

        var resolverInvoked = false;
        var specialized = new MirGenericSpecializer(
            typeId =>
            {
                if (typeId.Equals(customType))
                {
                    resolverInvoked = true;
                }

                return typeId.Equals(customType);
            }).Run(module);
        Assert.True(resolverInvoked);

        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_nonzero_partial_resolver_copy_local");
        var entryBlock = rewrittenCaller.BasicBlocks.Single();
        var rewrittenSecondCall = Assert.IsType<MirCall>(entryBlock.Instructions[0]);
        var rewrittenFunctionRef = Assert.IsType<MirFunctionRef>(rewrittenSecondCall.Function);
        Assert.NotEqual(genericSymbol, rewrittenFunctionRef.SymbolId);
        Assert.StartsWith("first__spec_", rewrittenFunctionRef.Name, StringComparison.Ordinal);
        Assert.Equal(customType, rewrittenFunctionRef.TypeId);
    }

    [Fact]
    public void Run_NonZeroArgGenericPartialWithResolverRejectedBoundLocal_DoesNotRewriteToSpecializedDirectCall()
    {
        var genericSymbol = new SymbolId(1229);
        var customType = new TypeId(9002);
        var boolType = new TypeId(BaseTypes.BoolId);

        var genericFirst = BuildFunction(
            returnType: TypeId.None,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "a",
                    TypeId = TypeId.None,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "b",
                    TypeId = TypeId.None,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: LocalPlace(1, TypeId.None),
            name: "first",
            symbolId: genericSymbol);

        var argA = LocalPlace(1, customType);
        var partialSlot = LocalPlace(2, TypeId.None);
        var argB = LocalPlace(3, boolType);
        var resultSlot = LocalPlace(4, customType);
        var caller = BuildFunction(
            returnType: customType,
            locals:
            [
                new MirLocal { Id = argA.Local, Name = "a", TypeId = customType, IsParameter = true },
                new MirLocal { Id = partialSlot.Local, Name = "f", TypeId = TypeId.None },
                new MirLocal { Id = argB.Local, Name = "b", TypeId = boolType, IsParameter = true },
                new MirLocal { Id = resultSlot.Local, Name = "res", TypeId = customType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = partialSlot,
                    Function = new MirFunctionRef
                    {
                        Name = "first",
                        SymbolId = genericSymbol,
                        TypeId = TypeId.None
                    },
                    Arguments = [argA]
                },
                new MirCall
                {
                    Target = resultSlot,
                    Function = partialSlot,
                    Arguments = [argB]
                }
            ],
            returnValue: resultSlot,
            name: "caller_nonzero_partial_resolver_reject_local",
            symbolId: new SymbolId(1230));

        var module = new MirModule
        {
            Name = "generic_nonzero_partial_resolver_reject_local",
            Functions = [genericFirst, caller]
        };

        var resolverInvoked = false;
        var specialized = new MirGenericSpecializer(
            typeId =>
            {
                if (typeId.Equals(customType))
                {
                    resolverInvoked = true;
                }

                return false;
            }).Run(module);
        Assert.True(resolverInvoked);

        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_nonzero_partial_resolver_reject_local");
        var entryBlock = rewrittenCaller.BasicBlocks.Single();
        var secondCall = Assert.IsType<MirCall>(entryBlock.Instructions[1]);
        Assert.IsType<MirPlace>(secondCall.Function);
    }

    [Fact]
    public void Run_NonZeroArgGenericPartialWithResolverCopyBoundDerefPlace_RewritesToSpecializedDirectCall()
    {
        var genericSymbol = new SymbolId(1231);
        var customType = new TypeId(9003);
        var boolType = new TypeId(BaseTypes.BoolId);

        var genericFirst = BuildFunction(
            returnType: TypeId.None,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "a",
                    TypeId = TypeId.None,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "b",
                    TypeId = TypeId.None,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: LocalPlace(1, TypeId.None),
            name: "first",
            symbolId: genericSymbol);

        var ptrLocal = LocalPlace(1, customType);
        var boundDeref = new MirPlace
        {
            Kind = PlaceKind.Deref,
            Base = ptrLocal,
            TypeId = customType
        };
        var partialSlot = LocalPlace(2, TypeId.None);
        var argB = LocalPlace(3, boolType);
        var resultSlot = LocalPlace(4, customType);
        var caller = BuildFunction(
            returnType: customType,
            locals:
            [
                new MirLocal { Id = ptrLocal.Local, Name = "ptr", TypeId = customType, IsParameter = true },
                new MirLocal { Id = partialSlot.Local, Name = "f", TypeId = TypeId.None },
                new MirLocal { Id = argB.Local, Name = "b", TypeId = boolType, IsParameter = true },
                new MirLocal { Id = resultSlot.Local, Name = "res", TypeId = customType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = partialSlot,
                    Function = new MirFunctionRef
                    {
                        Name = "first",
                        SymbolId = genericSymbol,
                        TypeId = TypeId.None
                    },
                    Arguments = [boundDeref]
                },
                new MirCall
                {
                    Target = resultSlot,
                    Function = partialSlot,
                    Arguments = [argB]
                }
            ],
            returnValue: resultSlot,
            name: "caller_nonzero_partial_resolver_copy_deref",
            symbolId: new SymbolId(1232));

        var module = new MirModule
        {
            Name = "generic_nonzero_partial_resolver_copy_deref",
            Functions = [genericFirst, caller]
        };

        var specialized = new MirGenericSpecializer(typeId => typeId.Equals(customType)).Run(module);
        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_nonzero_partial_resolver_copy_deref");
        var entryBlock = rewrittenCaller.BasicBlocks.Single();

        var rewrittenSecondCall = Assert.IsType<MirCall>(entryBlock.Instructions[0]);
        var rewrittenFunctionRef = Assert.IsType<MirFunctionRef>(rewrittenSecondCall.Function);
        Assert.NotEqual(genericSymbol, rewrittenFunctionRef.SymbolId);
        Assert.StartsWith("first__spec_", rewrittenFunctionRef.Name, StringComparison.Ordinal);
        Assert.Equal(customType, rewrittenFunctionRef.TypeId);

        var firstArgument = Assert.IsType<MirPlace>(rewrittenSecondCall.Arguments[0]);
        Assert.Equal(PlaceKind.Deref, firstArgument.Kind);
    }

    [Fact]
    public void Run_NonZeroArgGenericPartialWithResolverRejectedBoundDerefPlace_DoesNotRewriteToSpecializedDirectCall()
    {
        var genericSymbol = new SymbolId(1233);
        var customType = new TypeId(9004);
        var boolType = new TypeId(BaseTypes.BoolId);

        var genericFirst = BuildFunction(
            returnType: TypeId.None,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "a",
                    TypeId = TypeId.None,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "b",
                    TypeId = TypeId.None,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: LocalPlace(1, TypeId.None),
            name: "first",
            symbolId: genericSymbol);

        var ptrLocal = LocalPlace(1, customType);
        var boundDeref = new MirPlace
        {
            Kind = PlaceKind.Deref,
            Base = ptrLocal,
            TypeId = customType
        };
        var partialSlot = LocalPlace(2, TypeId.None);
        var argB = LocalPlace(3, boolType);
        var resultSlot = LocalPlace(4, customType);
        var caller = BuildFunction(
            returnType: customType,
            locals:
            [
                new MirLocal { Id = ptrLocal.Local, Name = "ptr", TypeId = customType, IsParameter = true },
                new MirLocal { Id = partialSlot.Local, Name = "f", TypeId = TypeId.None },
                new MirLocal { Id = argB.Local, Name = "b", TypeId = boolType, IsParameter = true },
                new MirLocal { Id = resultSlot.Local, Name = "res", TypeId = customType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = partialSlot,
                    Function = new MirFunctionRef
                    {
                        Name = "first",
                        SymbolId = genericSymbol,
                        TypeId = TypeId.None
                    },
                    Arguments = [boundDeref]
                },
                new MirCall
                {
                    Target = resultSlot,
                    Function = partialSlot,
                    Arguments = [argB]
                }
            ],
            returnValue: resultSlot,
            name: "caller_nonzero_partial_resolver_reject_deref",
            symbolId: new SymbolId(1234));

        var module = new MirModule
        {
            Name = "generic_nonzero_partial_resolver_reject_deref",
            Functions = [genericFirst, caller]
        };

        var specialized = new MirGenericSpecializer(_ => false).Run(module);
        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_nonzero_partial_resolver_reject_deref");
        var entryBlock = rewrittenCaller.BasicBlocks.Single();
        var secondCall = Assert.IsType<MirCall>(entryBlock.Instructions[1]);
        Assert.IsType<MirPlace>(secondCall.Function);
    }

    [Fact]
    public void Run_NonZeroArgGenericPartialWithResolverCopyBoundIndexOnDerefPlace_RewritesToSpecializedDirectCall()
    {
        var genericSymbol = new SymbolId(1235);
        var customType = new TypeId(9005);
        var boolType = new TypeId(BaseTypes.BoolId);
        var intType = new TypeId(BaseTypes.IntId);

        var genericFirst = BuildFunction(
            returnType: TypeId.None,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "a",
                    TypeId = TypeId.None,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "b",
                    TypeId = TypeId.None,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: LocalPlace(1, TypeId.None),
            name: "first",
            symbolId: genericSymbol);

        var ptrLocal = LocalPlace(1, customType);
        var derefPlace = new MirPlace
        {
            Kind = PlaceKind.Deref,
            Base = ptrLocal,
            TypeId = customType
        };
        var boundIndex = new MirPlace
        {
            Kind = PlaceKind.Index,
            Base = derefPlace,
            Index = new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(0)
            },
            IndexAccessKind = MirIndexAccessKind.Aggregate,
            TypeId = customType
        };
        var partialSlot = LocalPlace(2, TypeId.None);
        var argB = LocalPlace(3, boolType);
        var resultSlot = LocalPlace(4, customType);
        var caller = BuildFunction(
            returnType: customType,
            locals:
            [
                new MirLocal { Id = ptrLocal.Local, Name = "ptr", TypeId = customType, IsParameter = true },
                new MirLocal { Id = partialSlot.Local, Name = "f", TypeId = TypeId.None },
                new MirLocal { Id = argB.Local, Name = "b", TypeId = boolType, IsParameter = true },
                new MirLocal { Id = resultSlot.Local, Name = "res", TypeId = customType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = partialSlot,
                    Function = new MirFunctionRef
                    {
                        Name = "first",
                        SymbolId = genericSymbol,
                        TypeId = TypeId.None
                    },
                    Arguments = [boundIndex]
                },
                new MirCall
                {
                    Target = resultSlot,
                    Function = partialSlot,
                    Arguments = [argB]
                }
            ],
            returnValue: resultSlot,
            name: "caller_nonzero_partial_resolver_copy_index_deref",
            symbolId: new SymbolId(1236));

        var module = new MirModule
        {
            Name = "generic_nonzero_partial_resolver_copy_index_deref",
            Functions = [genericFirst, caller]
        };

        var specialized = new MirGenericSpecializer(typeId => typeId.Equals(customType)).Run(module);
        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_nonzero_partial_resolver_copy_index_deref");
        var entryBlock = rewrittenCaller.BasicBlocks.Single();

        var rewrittenSecondCall = Assert.IsType<MirCall>(entryBlock.Instructions[0]);
        var rewrittenFunctionRef = Assert.IsType<MirFunctionRef>(rewrittenSecondCall.Function);
        Assert.NotEqual(genericSymbol, rewrittenFunctionRef.SymbolId);
        Assert.StartsWith("first__spec_", rewrittenFunctionRef.Name, StringComparison.Ordinal);
        Assert.Equal(customType, rewrittenFunctionRef.TypeId);

        var firstArgument = Assert.IsType<MirPlace>(rewrittenSecondCall.Arguments[0]);
        Assert.Equal(PlaceKind.Index, firstArgument.Kind);
        Assert.NotNull(firstArgument.Base);
        Assert.Equal(PlaceKind.Deref, firstArgument.Base!.Kind);
    }

    [Fact]
    public void Run_NonZeroArgGenericPartialWithResolverRejectedBoundIndexOnDerefPlace_DoesNotRewriteToSpecializedDirectCall()
    {
        var genericSymbol = new SymbolId(1237);
        var customType = new TypeId(9006);
        var boolType = new TypeId(BaseTypes.BoolId);
        var intType = new TypeId(BaseTypes.IntId);

        var genericFirst = BuildFunction(
            returnType: TypeId.None,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "a",
                    TypeId = TypeId.None,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "b",
                    TypeId = TypeId.None,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: LocalPlace(1, TypeId.None),
            name: "first",
            symbolId: genericSymbol);

        var ptrLocal = LocalPlace(1, customType);
        var derefPlace = new MirPlace
        {
            Kind = PlaceKind.Deref,
            Base = ptrLocal,
            TypeId = customType
        };
        var boundIndex = new MirPlace
        {
            Kind = PlaceKind.Index,
            Base = derefPlace,
            Index = new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(0)
            },
            IndexAccessKind = MirIndexAccessKind.Aggregate,
            TypeId = customType
        };
        var partialSlot = LocalPlace(2, TypeId.None);
        var argB = LocalPlace(3, boolType);
        var resultSlot = LocalPlace(4, customType);
        var caller = BuildFunction(
            returnType: customType,
            locals:
            [
                new MirLocal { Id = ptrLocal.Local, Name = "ptr", TypeId = customType, IsParameter = true },
                new MirLocal { Id = partialSlot.Local, Name = "f", TypeId = TypeId.None },
                new MirLocal { Id = argB.Local, Name = "b", TypeId = boolType, IsParameter = true },
                new MirLocal { Id = resultSlot.Local, Name = "res", TypeId = customType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = partialSlot,
                    Function = new MirFunctionRef
                    {
                        Name = "first",
                        SymbolId = genericSymbol,
                        TypeId = TypeId.None
                    },
                    Arguments = [boundIndex]
                },
                new MirCall
                {
                    Target = resultSlot,
                    Function = partialSlot,
                    Arguments = [argB]
                }
            ],
            returnValue: resultSlot,
            name: "caller_nonzero_partial_resolver_reject_index_deref",
            symbolId: new SymbolId(1238));

        var module = new MirModule
        {
            Name = "generic_nonzero_partial_resolver_reject_index_deref",
            Functions = [genericFirst, caller]
        };

        var specialized = new MirGenericSpecializer(_ => false).Run(module);
        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_nonzero_partial_resolver_reject_index_deref");
        var entryBlock = rewrittenCaller.BasicBlocks.Single();
        var secondCall = Assert.IsType<MirCall>(entryBlock.Instructions[1]);
        Assert.IsType<MirPlace>(secondCall.Function);
    }

    [Fact]
    public void Run_NonZeroArgGenericPartialWithResolverCopyBoundFieldOnIndexPlace_RewritesToSpecializedDirectCall()
    {
        var genericSymbol = new SymbolId(1239);
        var customType = new TypeId(9007);
        var boolType = new TypeId(BaseTypes.BoolId);
        var intType = new TypeId(BaseTypes.IntId);

        var genericFirst = BuildFunction(
            returnType: TypeId.None,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "a",
                    TypeId = TypeId.None,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "b",
                    TypeId = TypeId.None,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: LocalPlace(1, TypeId.None),
            name: "first",
            symbolId: genericSymbol);

        var arrLocal = LocalPlace(1, customType);
        var indexedPlace = new MirPlace
        {
            Kind = PlaceKind.Index,
            Base = arrLocal,
            Index = new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(0)
            },
            IndexAccessKind = MirIndexAccessKind.Aggregate,
            TypeId = customType
        };
        var boundField = new MirPlace
        {
            Kind = PlaceKind.Field,
            Base = indexedPlace,
            FieldName = "_0",
            TypeId = customType
        };
        var partialSlot = LocalPlace(2, TypeId.None);
        var argB = LocalPlace(3, boolType);
        var resultSlot = LocalPlace(4, customType);
        var caller = BuildFunction(
            returnType: customType,
            locals:
            [
                new MirLocal { Id = arrLocal.Local, Name = "arr", TypeId = customType, IsParameter = true },
                new MirLocal { Id = partialSlot.Local, Name = "f", TypeId = TypeId.None },
                new MirLocal { Id = argB.Local, Name = "b", TypeId = boolType, IsParameter = true },
                new MirLocal { Id = resultSlot.Local, Name = "res", TypeId = customType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = partialSlot,
                    Function = new MirFunctionRef
                    {
                        Name = "first",
                        SymbolId = genericSymbol,
                        TypeId = TypeId.None
                    },
                    Arguments = [boundField]
                },
                new MirCall
                {
                    Target = resultSlot,
                    Function = partialSlot,
                    Arguments = [argB]
                }
            ],
            returnValue: resultSlot,
            name: "caller_nonzero_partial_resolver_copy_field_index",
            symbolId: new SymbolId(1240));

        var module = new MirModule
        {
            Name = "generic_nonzero_partial_resolver_copy_field_index",
            Functions = [genericFirst, caller]
        };

        var specialized = new MirGenericSpecializer(typeId => typeId.Equals(customType)).Run(module);
        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_nonzero_partial_resolver_copy_field_index");
        var entryBlock = rewrittenCaller.BasicBlocks.Single();
        var rewrittenSecondCall = Assert.IsType<MirCall>(entryBlock.Instructions[0]);
        var rewrittenFunctionRef = Assert.IsType<MirFunctionRef>(rewrittenSecondCall.Function);
        Assert.NotEqual(genericSymbol, rewrittenFunctionRef.SymbolId);
        Assert.StartsWith("first__spec_", rewrittenFunctionRef.Name, StringComparison.Ordinal);
        Assert.Equal(customType, rewrittenFunctionRef.TypeId);

        var firstArgument = Assert.IsType<MirPlace>(rewrittenSecondCall.Arguments[0]);
        Assert.Equal(PlaceKind.Field, firstArgument.Kind);
        Assert.NotNull(firstArgument.Base);
        Assert.Equal(PlaceKind.Index, firstArgument.Base!.Kind);
    }

    [Fact]
    public void Run_NonZeroArgGenericPartialWithResolverRejectedBoundFieldOnIndexPlace_DoesNotRewriteToSpecializedDirectCall()
    {
        var genericSymbol = new SymbolId(1241);
        var customType = new TypeId(9008);
        var boolType = new TypeId(BaseTypes.BoolId);
        var intType = new TypeId(BaseTypes.IntId);

        var genericFirst = BuildFunction(
            returnType: TypeId.None,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "a",
                    TypeId = TypeId.None,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "b",
                    TypeId = TypeId.None,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: LocalPlace(1, TypeId.None),
            name: "first",
            symbolId: genericSymbol);

        var arrLocal = LocalPlace(1, customType);
        var indexedPlace = new MirPlace
        {
            Kind = PlaceKind.Index,
            Base = arrLocal,
            Index = new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(0)
            },
            IndexAccessKind = MirIndexAccessKind.Aggregate,
            TypeId = customType
        };
        var boundField = new MirPlace
        {
            Kind = PlaceKind.Field,
            Base = indexedPlace,
            FieldName = "_0",
            TypeId = customType
        };
        var partialSlot = LocalPlace(2, TypeId.None);
        var argB = LocalPlace(3, boolType);
        var resultSlot = LocalPlace(4, customType);
        var caller = BuildFunction(
            returnType: customType,
            locals:
            [
                new MirLocal { Id = arrLocal.Local, Name = "arr", TypeId = customType, IsParameter = true },
                new MirLocal { Id = partialSlot.Local, Name = "f", TypeId = TypeId.None },
                new MirLocal { Id = argB.Local, Name = "b", TypeId = boolType, IsParameter = true },
                new MirLocal { Id = resultSlot.Local, Name = "res", TypeId = customType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = partialSlot,
                    Function = new MirFunctionRef
                    {
                        Name = "first",
                        SymbolId = genericSymbol,
                        TypeId = TypeId.None
                    },
                    Arguments = [boundField]
                },
                new MirCall
                {
                    Target = resultSlot,
                    Function = partialSlot,
                    Arguments = [argB]
                }
            ],
            returnValue: resultSlot,
            name: "caller_nonzero_partial_resolver_reject_field_index",
            symbolId: new SymbolId(1242));

        var module = new MirModule
        {
            Name = "generic_nonzero_partial_resolver_reject_field_index",
            Functions = [genericFirst, caller]
        };

        var specialized = new MirGenericSpecializer(_ => false).Run(module);
        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_nonzero_partial_resolver_reject_field_index");
        var entryBlock = rewrittenCaller.BasicBlocks.Single();
        var secondCall = Assert.IsType<MirCall>(entryBlock.Instructions[1]);
        Assert.IsType<MirPlace>(secondCall.Function);
    }

    [Fact]
    public void Run_NonZeroArgGenericPartialWithResolverCopyBoundDynamicIndexPlace_RewritesToSpecializedDirectCallAfterCapture()
    {
        var genericSymbol = new SymbolId(1243);
        var customType = new TypeId(9009);
        var boolType = new TypeId(BaseTypes.BoolId);
        var intType = new TypeId(BaseTypes.IntId);

        var genericFirst = BuildFunction(
            returnType: TypeId.None,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "a",
                    TypeId = TypeId.None,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "b",
                    TypeId = TypeId.None,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: LocalPlace(1, TypeId.None),
            name: "first",
            symbolId: genericSymbol);

        var arrLocal = LocalPlace(1, customType);
        var idxLocal = LocalPlace(2, intType);
        var boundIndex = new MirPlace
        {
            Kind = PlaceKind.Index,
            Base = arrLocal,
            Index = idxLocal,
            IndexAccessKind = MirIndexAccessKind.Aggregate,
            TypeId = customType
        };
        var partialSlot = LocalPlace(3, TypeId.None);
        var argB = LocalPlace(4, boolType);
        var resultSlot = LocalPlace(5, customType);
        var caller = BuildFunction(
            returnType: customType,
            locals:
            [
                new MirLocal { Id = arrLocal.Local, Name = "arr", TypeId = customType, IsParameter = true },
                new MirLocal { Id = idxLocal.Local, Name = "idx", TypeId = intType, IsParameter = true },
                new MirLocal { Id = partialSlot.Local, Name = "f", TypeId = TypeId.None },
                new MirLocal { Id = argB.Local, Name = "b", TypeId = boolType, IsParameter = true },
                new MirLocal { Id = resultSlot.Local, Name = "res", TypeId = customType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = partialSlot,
                    Function = new MirFunctionRef
                    {
                        Name = "first",
                        SymbolId = genericSymbol,
                        TypeId = TypeId.None
                    },
                    Arguments = [boundIndex]
                },
                new MirCall
                {
                    Target = resultSlot,
                    Function = partialSlot,
                    Arguments = [argB]
                }
            ],
            returnValue: resultSlot,
            name: "caller_nonzero_partial_resolver_copy_dynamic_index",
            symbolId: new SymbolId(1244));

        var module = new MirModule
        {
            Name = "generic_nonzero_partial_resolver_copy_dynamic_index",
            Functions = [genericFirst, caller]
        };

        var specialized = new MirGenericSpecializer(typeId => typeId.Equals(customType)).Run(module);
        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_nonzero_partial_resolver_copy_dynamic_index");
        var entryBlock = rewrittenCaller.BasicBlocks.Single();
        Assert.IsType<MirLoad>(entryBlock.Instructions[0]);
        var secondCall = Assert.IsType<MirCall>(entryBlock.Instructions[1]);
        Assert.IsType<MirFunctionRef>(secondCall.Function);
        Assert.Equal(2, secondCall.Arguments.Count);
        Assert.IsType<MirPlace>(secondCall.Arguments[0]);
        Assert.IsType<MirPlace>(secondCall.Arguments[1]);
    }

    [Fact]
    public void Run_NonZeroArgGenericPartialWithResolverRejectedBoundDynamicIndexPlace_DoesNotRewriteToSpecializedDirectCall()
    {
        var genericSymbol = new SymbolId(1245);
        var customType = new TypeId(9010);
        var boolType = new TypeId(BaseTypes.BoolId);
        var intType = new TypeId(BaseTypes.IntId);

        var genericFirst = BuildFunction(
            returnType: TypeId.None,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "a",
                    TypeId = TypeId.None,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "b",
                    TypeId = TypeId.None,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: LocalPlace(1, TypeId.None),
            name: "first",
            symbolId: genericSymbol);

        var arrLocal = LocalPlace(1, customType);
        var idxLocal = LocalPlace(2, intType);
        var boundIndex = new MirPlace
        {
            Kind = PlaceKind.Index,
            Base = arrLocal,
            Index = idxLocal,
            IndexAccessKind = MirIndexAccessKind.Aggregate,
            TypeId = customType
        };
        var partialSlot = LocalPlace(3, TypeId.None);
        var argB = LocalPlace(4, boolType);
        var resultSlot = LocalPlace(5, customType);
        var caller = BuildFunction(
            returnType: customType,
            locals:
            [
                new MirLocal { Id = arrLocal.Local, Name = "arr", TypeId = customType, IsParameter = true },
                new MirLocal { Id = idxLocal.Local, Name = "idx", TypeId = intType, IsParameter = true },
                new MirLocal { Id = partialSlot.Local, Name = "f", TypeId = TypeId.None },
                new MirLocal { Id = argB.Local, Name = "b", TypeId = boolType, IsParameter = true },
                new MirLocal { Id = resultSlot.Local, Name = "res", TypeId = customType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = partialSlot,
                    Function = new MirFunctionRef
                    {
                        Name = "first",
                        SymbolId = genericSymbol,
                        TypeId = TypeId.None
                    },
                    Arguments = [boundIndex]
                },
                new MirCall
                {
                    Target = resultSlot,
                    Function = partialSlot,
                    Arguments = [argB]
                }
            ],
            returnValue: resultSlot,
            name: "caller_nonzero_partial_resolver_reject_dynamic_index",
            symbolId: new SymbolId(1246));

        var module = new MirModule
        {
            Name = "generic_nonzero_partial_resolver_reject_dynamic_index",
            Functions = [genericFirst, caller]
        };

        var specialized = new MirGenericSpecializer(_ => false).Run(module);
        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_nonzero_partial_resolver_reject_dynamic_index");
        var entryBlock = rewrittenCaller.BasicBlocks.Single();
        var secondCall = Assert.IsType<MirCall>(entryBlock.Instructions[1]);
        Assert.IsType<MirPlace>(secondCall.Function);
    }

    [Fact]
    public void Run_NonZeroArgGenericPartialWithResolverCopyBoundFieldOnDynamicIndexPlace_RewritesToSpecializedDirectCallAfterCapture()
    {
        var genericSymbol = new SymbolId(1247);
        var customType = new TypeId(9011);
        var boolType = new TypeId(BaseTypes.BoolId);
        var intType = new TypeId(BaseTypes.IntId);

        var genericFirst = BuildFunction(
            returnType: TypeId.None,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "a",
                    TypeId = TypeId.None,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "b",
                    TypeId = TypeId.None,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: LocalPlace(1, TypeId.None),
            name: "first",
            symbolId: genericSymbol);

        var arrLocal = LocalPlace(1, customType);
        var idxLocal = LocalPlace(2, intType);
        var dynamicIndex = new MirPlace
        {
            Kind = PlaceKind.Index,
            Base = arrLocal,
            Index = idxLocal,
            IndexAccessKind = MirIndexAccessKind.Aggregate,
            TypeId = customType
        };
        var boundField = new MirPlace
        {
            Kind = PlaceKind.Field,
            Base = dynamicIndex,
            FieldName = "_0",
            TypeId = customType
        };
        var partialSlot = LocalPlace(3, TypeId.None);
        var argB = LocalPlace(4, boolType);
        var resultSlot = LocalPlace(5, customType);
        var caller = BuildFunction(
            returnType: customType,
            locals:
            [
                new MirLocal { Id = arrLocal.Local, Name = "arr", TypeId = customType, IsParameter = true },
                new MirLocal { Id = idxLocal.Local, Name = "idx", TypeId = intType, IsParameter = true },
                new MirLocal { Id = partialSlot.Local, Name = "f", TypeId = TypeId.None },
                new MirLocal { Id = argB.Local, Name = "b", TypeId = boolType, IsParameter = true },
                new MirLocal { Id = resultSlot.Local, Name = "res", TypeId = customType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = partialSlot,
                    Function = new MirFunctionRef
                    {
                        Name = "first",
                        SymbolId = genericSymbol,
                        TypeId = TypeId.None
                    },
                    Arguments = [boundField]
                },
                new MirCall
                {
                    Target = resultSlot,
                    Function = partialSlot,
                    Arguments = [argB]
                }
            ],
            returnValue: resultSlot,
            name: "caller_nonzero_partial_resolver_copy_field_dynamic_index",
            symbolId: new SymbolId(1248));

        var module = new MirModule
        {
            Name = "generic_nonzero_partial_resolver_copy_field_dynamic_index",
            Functions = [genericFirst, caller]
        };

        var specialized = new MirGenericSpecializer(typeId => typeId.Equals(customType)).Run(module);
        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_nonzero_partial_resolver_copy_field_dynamic_index");
        var entryBlock = rewrittenCaller.BasicBlocks.Single();
        Assert.IsType<MirLoad>(entryBlock.Instructions[0]);
        var secondCall = Assert.IsType<MirCall>(entryBlock.Instructions[1]);
        Assert.IsType<MirFunctionRef>(secondCall.Function);
        Assert.Equal(2, secondCall.Arguments.Count);
        Assert.IsType<MirPlace>(secondCall.Arguments[0]);
        Assert.IsType<MirPlace>(secondCall.Arguments[1]);
    }

    [Fact]
    public void Run_NonZeroArgGenericPartialWithResolverRejectedBoundFieldOnDynamicIndexPlace_DoesNotRewriteToSpecializedDirectCall()
    {
        var genericSymbol = new SymbolId(1249);
        var customType = new TypeId(9012);
        var boolType = new TypeId(BaseTypes.BoolId);
        var intType = new TypeId(BaseTypes.IntId);

        var genericFirst = BuildFunction(
            returnType: TypeId.None,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "a",
                    TypeId = TypeId.None,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "b",
                    TypeId = TypeId.None,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: LocalPlace(1, TypeId.None),
            name: "first",
            symbolId: genericSymbol);

        var arrLocal = LocalPlace(1, customType);
        var idxLocal = LocalPlace(2, intType);
        var dynamicIndex = new MirPlace
        {
            Kind = PlaceKind.Index,
            Base = arrLocal,
            Index = idxLocal,
            IndexAccessKind = MirIndexAccessKind.Aggregate,
            TypeId = customType
        };
        var boundField = new MirPlace
        {
            Kind = PlaceKind.Field,
            Base = dynamicIndex,
            FieldName = "_0",
            TypeId = customType
        };
        var partialSlot = LocalPlace(3, TypeId.None);
        var argB = LocalPlace(4, boolType);
        var resultSlot = LocalPlace(5, customType);
        var caller = BuildFunction(
            returnType: customType,
            locals:
            [
                new MirLocal { Id = arrLocal.Local, Name = "arr", TypeId = customType, IsParameter = true },
                new MirLocal { Id = idxLocal.Local, Name = "idx", TypeId = intType, IsParameter = true },
                new MirLocal { Id = partialSlot.Local, Name = "f", TypeId = TypeId.None },
                new MirLocal { Id = argB.Local, Name = "b", TypeId = boolType, IsParameter = true },
                new MirLocal { Id = resultSlot.Local, Name = "res", TypeId = customType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = partialSlot,
                    Function = new MirFunctionRef
                    {
                        Name = "first",
                        SymbolId = genericSymbol,
                        TypeId = TypeId.None
                    },
                    Arguments = [boundField]
                },
                new MirCall
                {
                    Target = resultSlot,
                    Function = partialSlot,
                    Arguments = [argB]
                }
            ],
            returnValue: resultSlot,
            name: "caller_nonzero_partial_resolver_reject_field_dynamic_index",
            symbolId: new SymbolId(1250));

        var module = new MirModule
        {
            Name = "generic_nonzero_partial_resolver_reject_field_dynamic_index",
            Functions = [genericFirst, caller]
        };

        var specialized = new MirGenericSpecializer(_ => false).Run(module);
        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_nonzero_partial_resolver_reject_field_dynamic_index");
        var entryBlock = rewrittenCaller.BasicBlocks.Single();
        var secondCall = Assert.IsType<MirCall>(entryBlock.Instructions[1]);
        Assert.IsType<MirPlace>(secondCall.Function);
    }

}
