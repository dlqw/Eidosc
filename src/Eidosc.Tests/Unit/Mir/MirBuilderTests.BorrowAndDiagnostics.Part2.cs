using System;
using System.Linq;
using Eidosc;
using Eidosc.Hir;
using Eidosc.Mir;
using Eidosc.Pipeline;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public partial class MirBuilderTests
{
    [Fact]
    public void Build_MutableHirParameter_CreatesMutableMirParameterLocal()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var paramSymbol = new SymbolId(203);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "id",
                    ReturnType = intType,
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "x",
                            SymbolId = paramSymbol,
                            TypeId = intType,
                            IsMutable = true
                        }
                    ],
                    Body = new HirVar
                    {
                        Name = "x",
                        SymbolId = paramSymbol,
                        TypeId = intType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);
        var function = Assert.Single(mirModule.Functions, item => item.Name == "id");
        var parameterLocal = Assert.Single(function.Locals, local => local.IsParameter && local.Name == "x");

        Assert.True(parameterLocal.IsMutable);
    }

    [Fact]
    public void Build_MutableHirValPattern_CreatesMutableMirLocal()
    {
        const string source = """
main :: Unit -> Int
{
    _ => {
        mut x := 1;
        x := 2;
        x
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "mir_mutable_val_pattern.eidos",
            StopAtPhase = CompilationPhase.Mir,
            UseColors = false
        }).Run();

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        var function = Assert.Single(mirModule.Functions, item => item.Name == "main");
        var local = Assert.Single(function.Locals, item => item.Name == "x");

        Assert.True(local.IsMutable);
    }

    [Fact]
    public void Build_UnaryRef_ReturnsOperandPlaceWithoutMirUnaryInstruction()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var refType = new TypeId(9001);
        var paramSymbol = new SymbolId(204);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "ref_id",
                    ReturnType = refType,
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "x",
                            SymbolId = paramSymbol,
                            TypeId = intType
                        }
                    ],
                    Body = new HirUnaryOp
                    {
                        Operator = Eidosc.Hir.UnaryOp.Ref,
                        Operand = new HirVar
                        {
                            Name = "x",
                            SymbolId = paramSymbol,
                            TypeId = intType
                        },
                        TypeId = refType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);
        var function = Assert.Single(mirModule.Functions, item => item.Name == "ref_id");
        var entry = Assert.Single(function.BasicBlocks, block => block.IsEntry);

        Assert.Empty(entry.Instructions.OfType<MirUnaryOp>());

        var terminator = Assert.IsType<MirReturn>(entry.Terminator);
        var returnValue = Assert.IsType<MirPlace>(terminator.Value);
        var parameterLocal = Assert.Single(function.Locals, local => local.IsParameter && local.Name == "x");
        Assert.Equal(parameterLocal.Id, returnValue.Local);
        Assert.Equal(refType, returnValue.TypeId);
    }

    [Fact]
    public void Build_UnaryMRef_ReturnsOperandPlaceWithoutMirUnaryInstruction()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var mrefType = new TypeId(9002);
        var paramSymbol = new SymbolId(205);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "mref_id",
                    ReturnType = mrefType,
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "x",
                            SymbolId = paramSymbol,
                            TypeId = intType
                        }
                    ],
                    Body = new HirUnaryOp
                    {
                        Operator = Eidosc.Hir.UnaryOp.MRef,
                        Operand = new HirVar
                        {
                            Name = "x",
                            SymbolId = paramSymbol,
                            TypeId = intType
                        },
                        TypeId = mrefType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);
        var function = Assert.Single(mirModule.Functions, item => item.Name == "mref_id");
        var entry = Assert.Single(function.BasicBlocks, block => block.IsEntry);

        Assert.Empty(entry.Instructions.OfType<MirUnaryOp>());

        var terminator = Assert.IsType<MirReturn>(entry.Terminator);
        var returnValue = Assert.IsType<MirPlace>(terminator.Value);
        var parameterLocal = Assert.Single(function.Locals, local => local.IsParameter && local.Name == "x");
        Assert.Equal(parameterLocal.Id, returnValue.Local);
        Assert.Equal(mrefType, returnValue.TypeId);
    }

    [Fact]
    public void Build_UnaryRef_OnFieldProjection_ReturnsFieldPlaceWithoutLoad()
    {
        const string source = """
Box :: type {
    value:: Int, tag:: Int

}

borrow_value :: Ref[Box] -> Ref[Int]
{
    box => ref box.value
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "mir_unary_ref_field_place.eidos",
            StopAtPhase = CompilationPhase.Mir,
                UseColors = false
        }).Run();

        Assert.True(result.Success);
        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        var function = Assert.Single(mirModule.Functions, item => item.Name == "borrow_value");
        var entry = Assert.Single(function.BasicBlocks, block => block.IsEntry);
        var terminator = Assert.IsType<MirReturn>(entry.Terminator);
        var returnValue = Assert.IsType<MirPlace>(terminator.Value);
        var fieldBase = Assert.IsType<MirPlace>(returnValue.Base);
        var derefBase = Assert.IsType<MirPlace>(fieldBase.Base);

        Assert.Empty(entry.Instructions.OfType<MirLoad>());
        Assert.Equal(PlaceKind.Field, returnValue.Kind);
        Assert.False(string.IsNullOrWhiteSpace(returnValue.FieldName));
        Assert.Equal(PlaceKind.Deref, fieldBase.Kind);
        Assert.Equal(PlaceKind.Local, derefBase.Kind);
    }

    [Fact]
    public void Build_UnaryMRef_OnIndexProjection_ReturnsIndexedPlaceWithoutLoad()
    {
        const string source = """
borrow_first :: MRef[Seq[Int]] -> MRef[Int]
{
    xs => mref xs[0]
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "mir_unary_mref_index_place.eidos",
            StopAtPhase = CompilationPhase.Mir,
                UseColors = false
        }).Run();

        Assert.True(result.Success);
        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        var function = Assert.Single(mirModule.Functions, item => item.Name == "borrow_first");
        var entry = Assert.Single(function.BasicBlocks, block => block.IsEntry);
        var terminator = Assert.IsType<MirReturn>(entry.Terminator);
        var returnValue = Assert.IsType<MirPlace>(terminator.Value);
        var derefBase = Assert.IsType<MirPlace>(returnValue.Base);
        var localBase = Assert.IsType<MirPlace>(derefBase.Base);

        Assert.Empty(entry.Instructions.OfType<MirLoad>());
        Assert.Equal(PlaceKind.Index, returnValue.Kind);
        Assert.Equal(PlaceKind.Deref, derefBase.Kind);
        Assert.Equal(PlaceKind.Local, localBase.Kind);
    }

    [Fact]
    public void Build_IndexAccess_OnRefList_InsertsDerefBaseBeforeIndexedLoad()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var listType = new TypeId(9620);
        var refListType = new TypeId(9621);
        var listSymbol = new SymbolId(9622);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "readFirst",
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "xs",
                            SymbolId = listSymbol,
                            TypeId = refListType
                        }
                    ],
                    ReturnType = intType,
                    Body = new HirIndexAccess
                    {
                        Target = new HirVar
                        {
                            Name = "xs",
                            SymbolId = listSymbol,
                            TypeId = refListType
                        },
                        Index = new HirLiteral
                        {
                            LiteralKind = LiteralKind.Int,
                            Value = 0L,
                            TypeId = intType
                        },
                        TargetKind = HirIndexAccessKind.RuntimeArray,
                        TypeId = intType
                    }
                }
            ]
        };

        var dynamicTypeKeys = new Dictionary<TypeId, string>
        {
            [refListType] = $"Ref({listType.Value})"
        };

        var builder = new MirBuilder(null, null, dynamicTypeKeys);
        var mirModule = builder.Build(module);
        var func = Assert.Single(mirModule.Functions);
        var entry = Assert.Single(func.BasicBlocks, block => block.IsEntry);
        var load = Assert.Single(entry.Instructions.OfType<MirLoad>());
        var source = Assert.IsType<MirPlace>(load.Source);
        var derefBase = Assert.IsType<MirPlace>(source.Base);
        var parameterBase = Assert.IsType<MirPlace>(derefBase.Base);

        Assert.Empty(builder.Diagnostics);
        Assert.Equal(PlaceKind.Index, source.Kind);
        Assert.Equal(MirIndexAccessKind.RuntimeArray, source.IndexAccessKind);
        Assert.Equal(PlaceKind.Deref, derefBase.Kind);
        Assert.Equal(listType, derefBase.TypeId);
        Assert.Equal(PlaceKind.Local, parameterBase.Kind);
    }

    [Fact]
    public void Build_CallAcceptingRef_WithMRefArgument_PassesBorrowPlaceWithoutExtraLoad()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var readFunctionType = new TypeId(9630);
        var refIntType = new TypeId(9631);
        var mrefIntType = new TypeId(9632);
        var readSymbol = new SymbolId(9633);
        var readParam = new SymbolId(9634);
        var useParam = new SymbolId(9635);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "read",
                    SymbolId = readSymbol,
                    TypeId = readFunctionType,
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "r",
                            SymbolId = readParam,
                            TypeId = refIntType
                        }
                    ],
                    ReturnType = intType,
                    Body = new HirUnaryOp
                    {
                        Operator = Eidosc.Hir.UnaryOp.Deref,
                        Operand = new HirVar
                        {
                            Name = "r",
                            SymbolId = readParam,
                            TypeId = refIntType
                        },
                        TypeId = intType
                    }
                },
                new HirFunc
                {
                    Name = "use",
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "x",
                            SymbolId = useParam,
                            TypeId = intType
                        }
                    ],
                    ReturnType = intType,
                    Body = new HirCall
                    {
                        Function = new HirVar
                        {
                            Name = "read",
                            SymbolId = readSymbol,
                            TypeId = readFunctionType
                        },
                        Arguments =
                        [
                            new HirUnaryOp
                            {
                                Operator = Eidosc.Hir.UnaryOp.MRef,
                                Operand = new HirVar
                                {
                                    Name = "x",
                                    SymbolId = useParam,
                                    TypeId = intType
                                },
                                TypeId = mrefIntType
                            }
                        ],
                        TypeId = intType
                    }
                }
            ]
        };

        var dynamicTypeKeys = new Dictionary<TypeId, string>
        {
            [refIntType] = $"Ref({intType.Value})",
            [mrefIntType] = $"MRef({intType.Value})"
        };

        var mirModule = new MirBuilder(null, null, dynamicTypeKeys).Build(module);
        var func = Assert.Single(mirModule.Functions, item => item.Name == "use");
        var call = Assert.Single(func.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirCall>());
        var argument = Assert.Single(call.Arguments);
        var argumentPlace = Assert.IsType<MirPlace>(argument);

        Assert.Equal(PlaceKind.Local, argumentPlace.Kind);
    }

    [Fact]
    public void Build_CallAcceptingRef_WithFieldProjectedRefArgument_PassesProjectedPlaceWithoutLoad()
    {
        const string source = """
ReaderBox[T] :: type {
    reader:: Ref[T], tag:: Int
}

read :: Ref[Int] -> Int
{
    r => r
}

use :: Ref[ReaderBox[Int]] -> Int
{
    box => box.reader.read
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "mir_field_projected_ref_argument.eidos",
            StopAtPhase = CompilationPhase.Mir,
                UseColors = false
        }).Run();

        Assert.True(result.Success);
        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        var use = Assert.Single(mirModule.Functions, function => function.Name == "use");
        var entry = Assert.Single(use.BasicBlocks, block => block.IsEntry);
        var call = Assert.Single(entry.Instructions.OfType<MirCall>());
        var argument = Assert.IsType<MirPlace>(Assert.Single(call.Arguments));
        var derefBase = Assert.IsType<MirPlace>(argument.Base);
        var localBase = Assert.IsType<MirPlace>(derefBase.Base);

        Assert.DoesNotContain(
            entry.Instructions.OfType<MirLoad>(),
            item => item.Source is MirPlace { Kind: PlaceKind.Field });
        Assert.Equal(PlaceKind.Field, argument.Kind);
        Assert.False(string.IsNullOrWhiteSpace(argument.FieldName));
        Assert.Equal(PlaceKind.Deref, derefBase.Kind);
        Assert.Equal(PlaceKind.Local, localBase.Kind);
    }

    [Fact]
    public void Build_CallAcceptingRef_WithFieldProjectedMRefArgument_PassesProjectedPlaceWithoutLoad()
    {
        const string source = """
WriterBox[T] :: type {
    writer:: MRef[T], tag:: Int
}

read :: Ref[Int] -> Int
{
    r => r
}

use :: Ref[WriterBox[Int]] -> Int
{
    box => box.writer.read
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "mir_field_projected_mref_argument.eidos",
            StopAtPhase = CompilationPhase.Mir,
                UseColors = false
        }).Run();

        Assert.True(result.Success);
        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        var use = Assert.Single(mirModule.Functions, function => function.Name == "use");
        var entry = Assert.Single(use.BasicBlocks, block => block.IsEntry);
        var call = Assert.Single(entry.Instructions.OfType<MirCall>());
        var argument = Assert.IsType<MirPlace>(Assert.Single(call.Arguments));
        var derefBase = Assert.IsType<MirPlace>(argument.Base);
        var localBase = Assert.IsType<MirPlace>(derefBase.Base);

        Assert.DoesNotContain(
            entry.Instructions.OfType<MirLoad>(),
            item => item.Source is MirPlace { Kind: PlaceKind.Field });
        Assert.Equal(PlaceKind.Field, argument.Kind);
        Assert.False(string.IsNullOrWhiteSpace(argument.FieldName));
        Assert.Equal(PlaceKind.Deref, derefBase.Kind);
        Assert.Equal(PlaceKind.Local, localBase.Kind);
    }

    [Fact]
    public void Build_CallAcceptingRef_WithIndexProjectedRefArgument_PassesProjectedPlaceWithoutLoad()
    {
        const string source = """
read :: Ref[Int] -> Int
{
    r => r
}

use :: Ref[Seq[Ref[Int]]] -> Int
{
    xs => xs[0].read
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "mir_index_projected_ref_argument.eidos",
            StopAtPhase = CompilationPhase.Mir,
                UseColors = false
        }).Run();

        Assert.True(result.Success);
        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        var use = Assert.Single(mirModule.Functions, function => function.Name == "use");
        var entry = Assert.Single(use.BasicBlocks, block => block.IsEntry);
        var call = Assert.Single(entry.Instructions.OfType<MirCall>());
        var argument = Assert.IsType<MirPlace>(Assert.Single(call.Arguments));
        var derefBase = Assert.IsType<MirPlace>(argument.Base);
        var localBase = Assert.IsType<MirPlace>(derefBase.Base);

        Assert.DoesNotContain(
            entry.Instructions.OfType<MirLoad>(),
            item => item.Source is MirPlace { Kind: PlaceKind.Index });
        Assert.Equal(PlaceKind.Index, argument.Kind);
        Assert.Equal(MirIndexAccessKind.RuntimeArray, argument.IndexAccessKind);
        Assert.Equal(PlaceKind.Deref, derefBase.Kind);
        Assert.Equal(PlaceKind.Local, localBase.Kind);
    }

    [Fact]
    public void Build_FieldRead_OnRefRecord_InsertsDerefBaseBeforeFieldLoad()
    {
        const string source = """
Range :: type {
    start:: Int, end:: Int
}

read :: Ref[Range] -> Int
{
    r => r.start
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "mir_ref_record_field_autoderef.eidos",
            StopAtPhase = CompilationPhase.Mir,
                UseColors = false
        }).Run();

        Assert.True(result.Success);
        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        var read = Assert.Single(mirModule.Functions, function => function.Name == "read");
        var fieldLoad = Assert.Single(
            read.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirLoad>(),
            load => load.Source is MirPlace { Kind: PlaceKind.Field, FieldName: "_0" });
        var fieldSource = Assert.IsType<MirPlace>(fieldLoad.Source);
        var derefBase = Assert.IsType<MirPlace>(fieldSource.Base);

        Assert.Equal(PlaceKind.Deref, derefBase.Kind);
    }
}
