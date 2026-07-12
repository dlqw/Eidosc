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
    public void Run_NonZeroArgGenericPartialWithResolverCopyBoundIndexOnFieldPlace_RewritesToSpecializedDirectCall()
    {
        var genericSymbol = new SymbolId(1251);
        var customType = new TypeId(9013);
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

        var objLocal = LocalPlace(1, customType);
        var fieldPlace = new MirPlace
        {
            Kind = PlaceKind.Field,
            Base = objLocal,
            FieldName = "_items",
            TypeId = customType
        };
        var boundIndex = new MirPlace
        {
            Kind = PlaceKind.Index,
            Base = fieldPlace,
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
                new MirLocal { Id = objLocal.Local, Name = "obj", TypeId = customType, IsParameter = true },
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
            name: "caller_nonzero_partial_resolver_copy_index_field",
            symbolId: new SymbolId(1252));

        var module = new MirModule
        {
            Name = "generic_nonzero_partial_resolver_copy_index_field",
            Functions = [genericFirst, caller]
        };

        var specialized = new MirGenericSpecializer(typeId => typeId.Equals(customType)).Run(module);
        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_nonzero_partial_resolver_copy_index_field");
        var entryBlock = rewrittenCaller.BasicBlocks.Single();
        var rewrittenSecondCall = Assert.IsType<MirCall>(entryBlock.Instructions[0]);
        var rewrittenFunctionRef = Assert.IsType<MirFunctionRef>(rewrittenSecondCall.Function);
        Assert.NotEqual(genericSymbol, rewrittenFunctionRef.SymbolId);
        Assert.StartsWith("first__spec_", rewrittenFunctionRef.Name, StringComparison.Ordinal);
        Assert.Equal(customType, rewrittenFunctionRef.TypeId);

        var firstArgument = Assert.IsType<MirPlace>(rewrittenSecondCall.Arguments[0]);
        Assert.Equal(PlaceKind.Index, firstArgument.Kind);
        Assert.NotNull(firstArgument.Base);
        Assert.Equal(PlaceKind.Field, firstArgument.Base!.Kind);
    }

    [Fact]
    public void Run_NonZeroArgGenericPartialWithResolverRejectedBoundIndexOnFieldPlace_DoesNotRewriteToSpecializedDirectCall()
    {
        var genericSymbol = new SymbolId(1253);
        var customType = new TypeId(9014);
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

        var objLocal = LocalPlace(1, customType);
        var fieldPlace = new MirPlace
        {
            Kind = PlaceKind.Field,
            Base = objLocal,
            FieldName = "_items",
            TypeId = customType
        };
        var boundIndex = new MirPlace
        {
            Kind = PlaceKind.Index,
            Base = fieldPlace,
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
                new MirLocal { Id = objLocal.Local, Name = "obj", TypeId = customType, IsParameter = true },
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
            name: "caller_nonzero_partial_resolver_reject_index_field",
            symbolId: new SymbolId(1254));

        var module = new MirModule
        {
            Name = "generic_nonzero_partial_resolver_reject_index_field",
            Functions = [genericFirst, caller]
        };

        var specialized = new MirGenericSpecializer(_ => false).Run(module);
        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_nonzero_partial_resolver_reject_index_field");
        var entryBlock = rewrittenCaller.BasicBlocks.Single();
        var secondCall = Assert.IsType<MirCall>(entryBlock.Instructions[1]);
        Assert.IsType<MirPlace>(secondCall.Function);
    }

    [Fact]
    public void Run_NonZeroArgGenericPartialWithCopyBoundIndexPlace_RewritesToSpecializedDirectCall()
    {
        var genericSymbol = new SymbolId(1223);
        var intType = new TypeId(BaseTypes.IntId);
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

        var arrLocal = LocalPlace(1, intType);
        var boundIndex = new MirPlace
        {
            Kind = PlaceKind.Index,
            Base = arrLocal,
            Index = new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(0)
            },
            IndexAccessKind = MirIndexAccessKind.Aggregate,
            TypeId = intType
        };
        var partialSlot = LocalPlace(2, TypeId.None);
        var argB = LocalPlace(3, boolType);
        var resultSlot = LocalPlace(4, intType);
        var caller = BuildFunction(
            returnType: intType,
            locals:
            [
                new MirLocal { Id = arrLocal.Local, Name = "arr", TypeId = intType, IsParameter = true },
                new MirLocal { Id = partialSlot.Local, Name = "f", TypeId = TypeId.None },
                new MirLocal { Id = argB.Local, Name = "b", TypeId = boolType, IsParameter = true },
                new MirLocal { Id = resultSlot.Local, Name = "res", TypeId = intType }
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
            name: "caller_nonzero_partial_copy_index",
            symbolId: new SymbolId(1224));

        var module = new MirModule
        {
            Name = "generic_nonzero_partial_copy_index",
            Functions = [genericFirst, caller]
        };

        var specialized = new MirGenericSpecializer().Run(module);
        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_nonzero_partial_copy_index");
        var entryBlock = rewrittenCaller.BasicBlocks.Single();

        var rewrittenSecondCall = Assert.IsType<MirCall>(entryBlock.Instructions[0]);
        var rewrittenFunctionRef = Assert.IsType<MirFunctionRef>(rewrittenSecondCall.Function);
        Assert.NotEqual(genericSymbol, rewrittenFunctionRef.SymbolId);
        Assert.StartsWith("first__spec_", rewrittenFunctionRef.Name, StringComparison.Ordinal);
        Assert.Equal(intType, rewrittenFunctionRef.TypeId);

        var firstArgument = Assert.IsType<MirPlace>(rewrittenSecondCall.Arguments[0]);
        Assert.Equal(PlaceKind.Index, firstArgument.Kind);
        Assert.NotNull(firstArgument.Base);
        Assert.Equal(arrLocal.Local, firstArgument.Base!.Local);
    }

    [Fact]
    public void Run_NonZeroArgGenericPartialWithNonCopyBoundFieldPlace_DoesNotRewriteToSpecializedDirectCall()
    {
        var genericSymbol = new SymbolId(1225);
        var stringType = new TypeId(BaseTypes.StringId);
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

        var objLocal = LocalPlace(1, stringType);
        var boundField = new MirPlace
        {
            Kind = PlaceKind.Field,
            Base = objLocal,
            FieldName = "_name",
            TypeId = stringType
        };
        var partialSlot = LocalPlace(2, TypeId.None);
        var argB = LocalPlace(3, boolType);
        var resultSlot = LocalPlace(4, stringType);
        var caller = BuildFunction(
            returnType: stringType,
            locals:
            [
                new MirLocal { Id = objLocal.Local, Name = "obj", TypeId = stringType, IsParameter = true },
                new MirLocal { Id = partialSlot.Local, Name = "f", TypeId = TypeId.None },
                new MirLocal { Id = argB.Local, Name = "b", TypeId = boolType, IsParameter = true },
                new MirLocal { Id = resultSlot.Local, Name = "res", TypeId = stringType }
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
            name: "caller_nonzero_partial_noncopy_field",
            symbolId: new SymbolId(1226));

        var module = new MirModule
        {
            Name = "generic_nonzero_partial_noncopy_field",
            Functions = [genericFirst, caller]
        };

        var specialized = new MirGenericSpecializer().Run(module);
        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_nonzero_partial_noncopy_field");
        var entryBlock = rewrittenCaller.BasicBlocks.Single();

        var secondCall = Assert.IsType<MirCall>(entryBlock.Instructions[1]);
        Assert.IsType<MirPlace>(secondCall.Function);
    }
}
