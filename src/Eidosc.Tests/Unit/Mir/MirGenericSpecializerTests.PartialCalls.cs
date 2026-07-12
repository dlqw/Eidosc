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
    public void Run_ZeroArgGenericPartialThenIndirectCall_LowersPartialToAssignAndRewritesCall()
    {
        var genericSymbol = new SymbolId(1211);
        var intType = new TypeId(BaseTypes.IntId);

        var genericId = BuildFunction(
            returnType: TypeId.None,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "x",
                    TypeId = TypeId.None,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: LocalPlace(1, TypeId.None),
            name: "id",
            symbolId: genericSymbol);

        var partialSlot = LocalPlace(1, TypeId.None);
        var argSlot = LocalPlace(2, intType);
        var resultSlot = LocalPlace(3, intType);
        var caller = BuildFunction(
            returnType: intType,
            locals:
            [
                new MirLocal { Id = partialSlot.Local, Name = "f", TypeId = TypeId.None },
                new MirLocal { Id = argSlot.Local, Name = "arg", TypeId = intType, IsParameter = true },
                new MirLocal { Id = resultSlot.Local, Name = "res", TypeId = intType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = partialSlot,
                    Function = new MirFunctionRef
                    {
                        Name = "id",
                        SymbolId = genericSymbol,
                        TypeId = TypeId.None
                    },
                    Arguments = []
                },
                new MirCall
                {
                    Target = resultSlot,
                    Function = partialSlot,
                    Arguments = [argSlot]
                }
            ],
            returnValue: resultSlot,
            name: "caller_zero_arg_partial",
            symbolId: new SymbolId(1212));

        var module = new MirModule
        {
            Name = "generic_zero_arg_partial_indirect",
            Functions = [genericId, caller]
        };

        var specialized = new MirGenericSpecializer().Run(module);
        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_zero_arg_partial");
        var entryBlock = rewrittenCaller.BasicBlocks.Single();

        var rewrittenCall = Assert.IsType<MirCall>(entryBlock.Instructions[0]);
        var rewrittenFunctionRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.NotEqual(genericSymbol, rewrittenFunctionRef.SymbolId);
        Assert.StartsWith("id__spec_", rewrittenFunctionRef.Name, StringComparison.Ordinal);
        Assert.Equal(intType, rewrittenFunctionRef.TypeId);
    }

    [Fact]
    public void Run_NonZeroArgGenericPartialThenIndirectCall_RewritesToSpecializedDirectCall()
    {
        var genericSymbol = new SymbolId(1213);
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

        var partialSlot = LocalPlace(1, TypeId.None);
        var argB = LocalPlace(2, boolType);
        var resultSlot = LocalPlace(3, intType);
        var boundConstant = new MirConstant
        {
            TypeId = intType,
            Value = new MirConstantValue.IntValue(7)
        };
        var caller = BuildFunction(
            returnType: intType,
            locals:
            [
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
                    Arguments = [boundConstant]
                },
                new MirCall
                {
                    Target = resultSlot,
                    Function = partialSlot,
                    Arguments = [argB]
                }
            ],
            returnValue: resultSlot,
            name: "caller_nonzero_partial",
            symbolId: new SymbolId(1214));

        var module = new MirModule
        {
            Name = "generic_nonzero_partial_indirect",
            Functions = [genericFirst, caller]
        };

        var specialized = new MirGenericSpecializer().Run(module);
        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_nonzero_partial");
        var entryBlock = rewrittenCaller.BasicBlocks.Single();

        var rewrittenSecondCall = Assert.IsType<MirCall>(entryBlock.Instructions[0]);
        var rewrittenFunctionRef = Assert.IsType<MirFunctionRef>(rewrittenSecondCall.Function);
        Assert.NotEqual(genericSymbol, rewrittenFunctionRef.SymbolId);
        Assert.StartsWith("first__spec_", rewrittenFunctionRef.Name, StringComparison.Ordinal);
        Assert.Equal(intType, rewrittenFunctionRef.TypeId);
        Assert.Equal(2, rewrittenSecondCall.Arguments.Count);

        var firstArgument = Assert.IsType<MirConstant>(rewrittenSecondCall.Arguments[0]);
        var firstValue = Assert.IsType<MirConstantValue.IntValue>(firstArgument.Value);
        Assert.Equal(7, firstValue.Value);

        var secondArgument = Assert.IsType<MirPlace>(rewrittenSecondCall.Arguments[1]);
        Assert.Equal(argB.Local, secondArgument.Local);
        Assert.Equal(
            """
            func <spec:first:1> symbol=<spec:first:1> fid=<spec:first:1>
            func caller_nonzero_partial symbol=sym:1214 fid=sym:1214
              call %3:T1 -> <spec:first:1> fid=<spec:first:1> args=[7:T1, %2:T3]
            """.ReplaceLineEndings("\n"),
            BuildIdentityContract(specialized).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Run_GenericPartialCallThroughPrecompiledModuleAliasName_RewritesToSpecializedDirectCall()
    {
        var genericSymbol = new SymbolId(12135);
        var listType = new TypeId(9101);
        var predicateType = new TypeId(9102);
        var intType = new TypeId(BaseTypes.IntId);

        var genericCount = BuildFunction(
            returnType: intType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "xs",
                    TypeId = TypeId.None,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "pred",
                    TypeId = TypeId.None,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(0)
            },
            name: "Std__Seq__count",
            symbolId: genericSymbol);

        var rawListArg = LocalPlace(1, listType);
        var movedListArg = LocalPlace(2, listType);
        var partialSlot = LocalPlace(3, TypeId.None);
        var resultSlot = LocalPlace(4, intType);
        var caller = BuildFunction(
            returnType: intType,
            locals:
            [
                new MirLocal { Id = rawListArg.Local, Name = "xs", TypeId = listType, IsParameter = true },
                new MirLocal { Id = movedListArg.Local, Name = "xs_moved", TypeId = listType },
                new MirLocal { Id = partialSlot.Local, Name = "f", TypeId = TypeId.None },
                new MirLocal { Id = resultSlot.Local, Name = "res", TypeId = intType }
            ],
            instructions:
            [
                new MirMove
                {
                    Target = movedListArg,
                    Source = rawListArg
                },
                new MirCall
                {
                    Target = partialSlot,
                    Function = new MirFunctionRef
                    {
                        Name = "Seq::count",
                        SymbolId = SymbolId.None,
                        TypeId = TypeId.None
                    },
                    Arguments = [movedListArg]
                },
                new MirCall
                {
                    Target = resultSlot,
                    Function = partialSlot,
                    Arguments =
                    [
                        new MirFunctionRef
                        {
                            Name = "is_small",
                            SymbolId = SymbolId.None,
                            TypeId = predicateType
                        }
                    ]
                }
            ],
            returnValue: resultSlot,
            name: "caller_precompiled_alias_partial",
            symbolId: new SymbolId(12136));

        var module = new MirModule
        {
            Name = "generic_precompiled_alias_partial",
            Functions = [genericCount, caller]
        };

        var specialized = new MirGenericSpecializer().Run(module);
        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_precompiled_alias_partial");
        var entryBlock = rewrittenCaller.BasicBlocks.Single();

        var leadingMove = Assert.IsType<MirMove>(entryBlock.Instructions[0]);
        Assert.Equal(rawListArg.Local, leadingMove.Source.Local);
        Assert.Equal(movedListArg.Local, leadingMove.Target.Local);

        var rewrittenSecondCall = Assert.IsType<MirCall>(entryBlock.Instructions[1]);
        var rewrittenFunctionRef = Assert.IsType<MirFunctionRef>(rewrittenSecondCall.Function);
        Assert.NotEqual(genericSymbol, rewrittenFunctionRef.SymbolId);
        Assert.StartsWith("Std__Seq__count__spec_", rewrittenFunctionRef.Name, StringComparison.Ordinal);
        Assert.Equal(intType, rewrittenFunctionRef.TypeId);
        Assert.Equal(2, rewrittenSecondCall.Arguments.Count);

        var rewrittenFirstArgument = Assert.IsType<MirPlace>(rewrittenSecondCall.Arguments[0]);
        Assert.Equal(movedListArg.Local, rewrittenFirstArgument.Local);

        var rewrittenSecondArgument = Assert.IsType<MirFunctionRef>(rewrittenSecondCall.Arguments[1]);
        Assert.Equal("is_small", rewrittenSecondArgument.Name);
        Assert.Equal(predicateType, rewrittenSecondArgument.TypeId);
    }

    [Fact]
    public void Run_NonZeroArgGenericPartialWithCopyBoundLocal_RewritesToSpecializedDirectCall()
    {
        var genericSymbol = new SymbolId(1215);
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

        var argA = LocalPlace(1, intType);
        var partialSlot = LocalPlace(2, TypeId.None);
        var argB = LocalPlace(3, boolType);
        var resultSlot = LocalPlace(4, intType);
        var caller = BuildFunction(
            returnType: intType,
            locals:
            [
                new MirLocal { Id = argA.Local, Name = "a", TypeId = intType, IsParameter = true },
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
            name: "caller_nonzero_partial_copy_local",
            symbolId: new SymbolId(1216));

        var module = new MirModule
        {
            Name = "generic_nonzero_partial_copy_local",
            Functions = [genericFirst, caller]
        };

        var specialized = new MirGenericSpecializer().Run(module);
        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_nonzero_partial_copy_local");
        var entryBlock = rewrittenCaller.BasicBlocks.Single();

        var rewrittenSecondCall = Assert.IsType<MirCall>(entryBlock.Instructions[0]);
        var rewrittenFunctionRef = Assert.IsType<MirFunctionRef>(rewrittenSecondCall.Function);
        Assert.NotEqual(genericSymbol, rewrittenFunctionRef.SymbolId);
        Assert.StartsWith("first__spec_", rewrittenFunctionRef.Name, StringComparison.Ordinal);
        Assert.Equal(intType, rewrittenFunctionRef.TypeId);
        Assert.Equal(2, rewrittenSecondCall.Arguments.Count);

        var firstArgument = Assert.IsType<MirPlace>(rewrittenSecondCall.Arguments[0]);
        Assert.Equal(argA.Local, firstArgument.Local);
        var secondArgument = Assert.IsType<MirPlace>(rewrittenSecondCall.Arguments[1]);
        Assert.Equal(argB.Local, secondArgument.Local);
    }

    [Fact]
    public void Run_NonZeroArgGenericPartialWithCopyBoundTemp_RewritesToSpecializedDirectCall()
    {
        var genericSymbol = new SymbolId(1217);
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

        var boundTemp = new MirTemp
        {
            Id = new TempId { Value = 101 },
            TypeId = intType
        };
        var partialSlot = LocalPlace(1, TypeId.None);
        var argB = LocalPlace(2, boolType);
        var resultSlot = LocalPlace(3, intType);
        var caller = BuildFunction(
            returnType: intType,
            locals:
            [
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
                    Arguments = [boundTemp]
                },
                new MirCall
                {
                    Target = resultSlot,
                    Function = partialSlot,
                    Arguments = [argB]
                }
            ],
            returnValue: resultSlot,
            name: "caller_nonzero_partial_copy_temp",
            symbolId: new SymbolId(1218));

        var module = new MirModule
        {
            Name = "generic_nonzero_partial_copy_temp",
            Functions = [genericFirst, caller]
        };

        var specialized = new MirGenericSpecializer().Run(module);
        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_nonzero_partial_copy_temp");
        var entryBlock = rewrittenCaller.BasicBlocks.Single();

        var rewrittenSecondCall = Assert.IsType<MirCall>(entryBlock.Instructions[0]);
        var rewrittenFunctionRef = Assert.IsType<MirFunctionRef>(rewrittenSecondCall.Function);
        Assert.NotEqual(genericSymbol, rewrittenFunctionRef.SymbolId);
        Assert.StartsWith("first__spec_", rewrittenFunctionRef.Name, StringComparison.Ordinal);
        Assert.Equal(intType, rewrittenFunctionRef.TypeId);
        Assert.Equal(2, rewrittenSecondCall.Arguments.Count);

        var firstArgument = Assert.IsType<MirTemp>(rewrittenSecondCall.Arguments[0]);
        Assert.Equal(boundTemp.Id, firstArgument.Id);
        var secondArgument = Assert.IsType<MirPlace>(rewrittenSecondCall.Arguments[1]);
        Assert.Equal(argB.Local, secondArgument.Local);
    }

    [Fact]
    public void Run_NonZeroArgGenericPartialWithCopyBoundFieldPlace_RewritesToSpecializedDirectCall()
    {
        var genericSymbol = new SymbolId(1219);
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

        var tupleLocal = LocalPlace(1, intType);
        var boundField = new MirPlace
        {
            Kind = PlaceKind.Field,
            Base = tupleLocal,
            FieldName = "_0",
            TypeId = intType
        };
        var partialSlot = LocalPlace(2, TypeId.None);
        var argB = LocalPlace(3, boolType);
        var resultSlot = LocalPlace(4, intType);
        var caller = BuildFunction(
            returnType: intType,
            locals:
            [
                new MirLocal { Id = tupleLocal.Local, Name = "pair", TypeId = intType, IsParameter = true },
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
            name: "caller_nonzero_partial_copy_field",
            symbolId: new SymbolId(1220));

        var module = new MirModule
        {
            Name = "generic_nonzero_partial_copy_field",
            Functions = [genericFirst, caller]
        };

        var specialized = new MirGenericSpecializer().Run(module);
        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_nonzero_partial_copy_field");
        var entryBlock = rewrittenCaller.BasicBlocks.Single();

        var rewrittenSecondCall = Assert.IsType<MirCall>(entryBlock.Instructions[0]);
        var rewrittenFunctionRef = Assert.IsType<MirFunctionRef>(rewrittenSecondCall.Function);
        Assert.NotEqual(genericSymbol, rewrittenFunctionRef.SymbolId);
        Assert.StartsWith("first__spec_", rewrittenFunctionRef.Name, StringComparison.Ordinal);
        Assert.Equal(intType, rewrittenFunctionRef.TypeId);
        Assert.Equal(2, rewrittenSecondCall.Arguments.Count);

        var firstArgument = Assert.IsType<MirPlace>(rewrittenSecondCall.Arguments[0]);
        Assert.Equal(PlaceKind.Field, firstArgument.Kind);
        Assert.Equal("_0", firstArgument.FieldName);
        Assert.NotNull(firstArgument.Base);
        Assert.Equal(tupleLocal.Local, firstArgument.Base!.Local);

        var secondArgument = Assert.IsType<MirPlace>(rewrittenSecondCall.Arguments[1]);
        Assert.Equal(argB.Local, secondArgument.Local);
    }

    [Fact]
    public void Run_NonZeroArgGenericPartialWithNonCopyBoundLocal_DoesNotRewriteToSpecializedDirectCall()
    {
        var genericSymbol = new SymbolId(1221);
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

        var argA = LocalPlace(1, stringType);
        var partialSlot = LocalPlace(2, TypeId.None);
        var argB = LocalPlace(3, boolType);
        var resultSlot = LocalPlace(4, stringType);
        var caller = BuildFunction(
            returnType: stringType,
            locals:
            [
                new MirLocal { Id = argA.Local, Name = "a", TypeId = stringType, IsParameter = true },
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
            name: "caller_nonzero_partial_noncopy_local",
            symbolId: new SymbolId(1222));

        var module = new MirModule
        {
            Name = "generic_nonzero_partial_noncopy_local",
            Functions = [genericFirst, caller]
        };

        var specialized = new MirGenericSpecializer().Run(module);
        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_nonzero_partial_noncopy_local");
        var entryBlock = rewrittenCaller.BasicBlocks.Single();

        var secondCall = Assert.IsType<MirCall>(entryBlock.Instructions[1]);
        Assert.IsType<MirPlace>(secondCall.Function);
    }
}
