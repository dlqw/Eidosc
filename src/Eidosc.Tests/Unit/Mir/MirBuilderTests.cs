using Eidosc.Symbols;
using Eidosc;
using Eidosc.Hir;
using Eidosc.Mir;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public partial class MirBuilderTests
{
    [Fact]
    public void Build_FunctionBinOpBody_UsesParameterLocalWithoutCastFailure()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var paramSymbol = new SymbolId(100);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "main",
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "x",
                            SymbolId = paramSymbol,
                            TypeId = intType
                        }
                    ],
                    ReturnType = intType,
                    Body = new HirBinOp
                    {
                        Operator = Eidosc.Hir.BinaryOp.Add,
                        Left = new HirVar
                        {
                            Name = "x_shadow_name_ignored",
                            SymbolId = paramSymbol,
                            TypeId = intType
                        },
                        Right = new HirLiteral
                        {
                            LiteralKind = LiteralKind.Int,
                            Value = 1L,
                            TypeId = intType
                        },
                        TypeId = intType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);
        var func = Assert.Single(mirModule.Functions);
        var entry = Assert.Single(func.BasicBlocks, b => b.IsEntry);
        var copy = Assert.Single(entry.Instructions.OfType<MirCopy>());
        var binOp = Assert.Single(entry.Instructions.OfType<MirBinOp>());

        var paramLocal = Assert.Single(func.Locals, l => l.IsParameter && l.Name == "x");
        var leftPlace = Assert.IsType<MirPlace>(binOp.Left);

        Assert.Equal(paramLocal.Id, copy.Source.Local);
        Assert.Equal(copy.Target.Local, leftPlace.Local);
    }

    [Fact]
    public void Build_FunctionCall_UsesFunctionRefOperand()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var calleeSymbol = new SymbolId(200);
        var callerSymbol = new SymbolId(201);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "callee",
                    SymbolId = calleeSymbol,
                    ReturnType = intType,
                    Body = new HirLiteral
                    {
                        LiteralKind = LiteralKind.Int,
                        Value = 0L,
                        TypeId = intType
                    }
                },
                new HirFunc
                {
                    Name = "caller",
                    SymbolId = callerSymbol,
                    ReturnType = intType,
                    Body = new HirCall
                    {
                        Function = new HirVar
                        {
                            Name = "callee",
                            SymbolId = calleeSymbol,
                            TypeId = intType
                        },
                        TypeId = intType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);
        var callerFunc = Assert.Single(mirModule.Functions, f => f.Name == "caller");
        var entry = Assert.Single(callerFunc.BasicBlocks, b => b.IsEntry);
        var call = Assert.Single(entry.Instructions.OfType<MirCall>());
        var funcRef = Assert.IsType<MirFunctionRef>(call.Function);

        Assert.Equal(calleeSymbol, funcRef.SymbolId);
        Assert.Equal("callee", funcRef.Name);
    }

    [Fact]
    public void Build_FunctionCall_RecordsCallSiteFunctionSignature()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var calleeSymbol = new SymbolId(205);
        var callerSymbol = new SymbolId(206);
        var paramSymbol = new SymbolId(207);
        var calleeParamSymbol = new SymbolId(208);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "isPositive",
                    SymbolId = calleeSymbol,
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "value",
                            SymbolId = calleeParamSymbol,
                            TypeId = intType
                        }
                    ],
                    ReturnType = boolType,
                    Body = new HirLiteral
                    {
                        LiteralKind = LiteralKind.Bool,
                        Value = true,
                        TypeId = boolType
                    }
                },
                new HirFunc
                {
                    Name = "caller",
                    SymbolId = callerSymbol,
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "input",
                            SymbolId = paramSymbol,
                            TypeId = intType
                        }
                    ],
                    ReturnType = boolType,
                    Body = new HirCall
                    {
                        Function = new HirVar
                        {
                            Name = "isPositive",
                            SymbolId = calleeSymbol,
                            TypeId = boolType
                        },
                        Arguments =
                        [
                            new HirVar
                            {
                                Name = "input",
                                SymbolId = paramSymbol,
                                TypeId = intType
                            }
                        ],
                        TypeId = boolType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);
        var callerFunc = Assert.Single(mirModule.Functions, function => function.Name == "caller");
        var call = Assert.Single(callerFunc.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirCall>());
        var funcRef = Assert.IsType<MirFunctionRef>(call.Function);

        Assert.Equal(boolType, funcRef.TypeId);
        Assert.True(funcRef.SignatureTypeId.IsValid);
        var descriptor = Assert.IsType<TypeDescriptor.Function>(mirModule.TypeDescriptors[funcRef.SignatureTypeId.Value]);
        Assert.Equal(boolType, descriptor.ReturnType);
        Assert.Equal(intType, Assert.Single(descriptor.ParamTypes));
    }

    [Fact]
    public void Build_FunctionRefWithSymbol_DoesNotUseSameNameSignatureFallback()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var functionValueType = new TypeId(211);
        var sameNameFunctionSymbol = new SymbolId(212);
        var lambdaValueSymbol = new SymbolId(213);
        var callerSymbol = new SymbolId(214);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "shared",
                    SymbolId = sameNameFunctionSymbol,
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "flag",
                            SymbolId = new SymbolId(215),
                            TypeId = boolType
                        }
                    ],
                    ReturnType = boolType,
                    Body = new HirLiteral
                    {
                        LiteralKind = LiteralKind.Bool,
                        Value = true,
                        TypeId = boolType
                    }
                },
                new HirVal
                {
                    Name = "shared",
                    SymbolId = lambdaValueSymbol,
                    IsModuleLevel = true,
                    TypeId = functionValueType,
                    Pattern = new HirVarPattern
                    {
                        Name = "shared",
                        SymbolId = lambdaValueSymbol,
                        TypeId = functionValueType
                    },
                    Initializer = new HirLambda
                    {
                        SymbolId = lambdaValueSymbol,
                        TypeId = functionValueType,
                        ReturnType = intType,
                        Body = new HirLiteral
                        {
                            LiteralKind = LiteralKind.Int,
                            Value = 1L,
                            TypeId = intType
                        }
                    }
                },
                new HirFunc
                {
                    Name = "caller",
                    SymbolId = callerSymbol,
                    ReturnType = functionValueType,
                    Body = new HirVar
                    {
                        Name = "shared",
                        SymbolId = lambdaValueSymbol,
                        TypeId = functionValueType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);
        var callerFunc = Assert.Single(mirModule.Functions, function => function.SymbolId == callerSymbol);
        var returnTerminator = Assert.IsType<MirReturn>(callerFunc.BasicBlocks.Single(block => block.IsEntry).Terminator);
        var functionRef = Assert.IsType<MirFunctionRef>(returnTerminator.Value);

        Assert.Equal(lambdaValueSymbol, functionRef.SymbolId);
        Assert.Equal("shared", functionRef.Name);
        Assert.Equal(TypeId.None, functionRef.SignatureTypeId);
    }

    [Fact]
    public void Build_ModuleValueGetter_FunctionAndReferenceShareSyntheticIdentity()
    {
        var intType = new TypeId(BaseTypes.IntId);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirVal
                {
                    Name = "answer",
                    IsModuleLevel = true,
                    TypeId = intType,
                    Pattern = new HirVarPattern
                    {
                        Name = "answer",
                        TypeId = intType
                    },
                    Initializer = new HirLiteral
                    {
                        LiteralKind = LiteralKind.Int,
                        Value = 42L,
                        TypeId = intType
                    }
                },
                new HirFunc
                {
                    Name = "caller",
                    ReturnType = intType,
                    Body = new HirVar
                    {
                        Name = "answer",
                        TypeId = intType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);
        var getter = Assert.Single(mirModule.Functions, function => function.Name.StartsWith("__module_val__", StringComparison.Ordinal));
        var caller = Assert.Single(mirModule.Functions, function => function.Name == "caller");
        var call = Assert.Single(caller.BasicBlocks.Single(block => block.IsEntry).Instructions.OfType<MirCall>());
        var getterRef = Assert.IsType<MirFunctionRef>(call.Function);

        Assert.Equal(getter.Name, getterRef.Name);
        Assert.Equal(getter.FunctionId, getterRef.FunctionId);
        var getterStableKey = MirFunctionIdentity.GetStableKey(getter);
        Assert.Equal(getterStableKey, MirFunctionIdentity.GetStableKey(getterRef));
        Assert.False(getter.FunctionId.SymbolId.IsValid);
        Assert.Contains("synthetic:", getterStableKey, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_BuiltinIntrinsicFunctionRef_CarriesBuiltinIdentity()
    {
        var floatType = new TypeId(BaseTypes.FloatId);
        var symbolTable = new SymbolTable();
        var maybeMathSinSymbol = symbolTable.LookupValue("math_sin");
        Assert.True(maybeMathSinSymbol.HasValue);
        var mathSinSymbol = maybeMathSinSymbol.Value;

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "caller",
                    ReturnType = floatType,
                    Body = new HirCall
                    {
                        Function = new HirVar
                        {
                            Name = "math_sin",
                            SymbolId = mathSinSymbol,
                            TypeId = floatType
                        },
                        Arguments =
                        [
                            new HirLiteral
                            {
                                LiteralKind = LiteralKind.Float,
                                Value = 1.0,
                                TypeId = floatType
                            }
                        ],
                        TypeId = floatType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder(null, symbolTable: symbolTable).Build(module);
        var caller = Assert.Single(mirModule.Functions, function => function.Name == "caller");
        var call = Assert.Single(caller.BasicBlocks.Single(block => block.IsEntry).Instructions.OfType<MirCall>());
        var functionRef = Assert.IsType<MirFunctionRef>(call.Function);

        Assert.Equal(mathSinSymbol, functionRef.SymbolId);
        Assert.Equal("builtin", functionRef.FunctionId.Module);
        Assert.Equal("builtin:math_sin", functionRef.FunctionId.QualifiedName);
    }

    [Fact]
    public void Build_FunctionCall_PreservesExplicitTypeArguments()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var calleeSymbol = new SymbolId(209);
        var callerSymbol = new SymbolId(210);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "identity",
                    SymbolId = calleeSymbol,
                    ReturnType = intType,
                    Body = new HirLiteral
                    {
                        LiteralKind = LiteralKind.Int,
                        Value = 0L,
                        TypeId = intType
                    }
                },
                new HirFunc
                {
                    Name = "caller",
                    SymbolId = callerSymbol,
                    ReturnType = intType,
                    Body = new HirCall
                    {
                        Function = new HirVar
                        {
                            Name = "identity",
                            SymbolId = calleeSymbol,
                            TypeId = intType,
                            TypeArgumentIds = [intType]
                        },
                        TypeId = intType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);
        var callerFunc = Assert.Single(mirModule.Functions, function => function.Name == "caller");
        var call = Assert.Single(callerFunc.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirCall>());
        var funcRef = Assert.IsType<MirFunctionRef>(call.Function);

        Assert.Equal([intType], funcRef.TypeArgumentIds);
    }

    [Fact]
    public void Build_GenericCallSiteSignature_PublishesConcreteTypeDescriptor()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var genericType = new TypeId(2301);
        var genericSymbol = new SymbolId(2302);
        var genericParamSymbol = new SymbolId(2303);
        var callerSymbol = new SymbolId(2304);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "identity",
                    SymbolId = genericSymbol,
                    TypeParams =
                    [
                        new HirTypeParam
                        {
                            Name = "T",
                            SymbolId = new SymbolId(2305),
                            TypeId = genericType
                        }
                    ],
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "value",
                            SymbolId = genericParamSymbol,
                            TypeId = genericType
                        }
                    ],
                    ReturnType = genericType,
                    Body = new HirVar
                    {
                        Name = "value",
                        SymbolId = genericParamSymbol,
                        TypeId = genericType
                    }
                },
                new HirFunc
                {
                    Name = "caller",
                    SymbolId = callerSymbol,
                    ReturnType = intType,
                    Body = new HirCall
                    {
                        Function = new HirVar
                        {
                            Name = "identity",
                            SymbolId = genericSymbol,
                            TypeId = genericType,
                            TypeArgumentIds = [intType]
                        },
                        Arguments =
                        [
                            new HirLiteral
                            {
                                LiteralKind = LiteralKind.Int,
                                Value = 1L,
                                TypeId = intType
                            }
                        ],
                        TypeId = intType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);
        var callerFunc = Assert.Single(mirModule.Functions, function => function.Name == "caller");
        var call = Assert.Single(callerFunc.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirCall>());
        var funcRef = Assert.IsType<MirFunctionRef>(call.Function);

        Assert.True(funcRef.SignatureTypeId.IsValid);
        Assert.True(mirModule.TypeDescriptors.ContainsKey(funcRef.SignatureTypeId.Value));
        var descriptor = Assert.IsType<TypeDescriptor.Function>(mirModule.TypeDescriptors[funcRef.SignatureTypeId.Value]);
        Assert.Equal(intType, descriptor.ReturnType);
        Assert.Equal(intType, Assert.Single(descriptor.ParamTypes));
    }

    [Fact]
    public void Build_TypeAppliedFunctionValue_UsesConcreteFunctionTypeAsSignature()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var genericType = new TypeId(2401);
        var concreteSignatureType = new TypeId(2402);
        var genericSymbol = new SymbolId(2403);
        var genericParamSymbol = new SymbolId(2404);
        var callerSymbol = new SymbolId(2405);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "identity",
                    SymbolId = genericSymbol,
                    TypeParams =
                    [
                        new HirTypeParam
                        {
                            Name = "T",
                            SymbolId = new SymbolId(2406),
                            TypeId = genericType
                        }
                    ],
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "value",
                            SymbolId = genericParamSymbol,
                            TypeId = genericType
                        }
                    ],
                    ReturnType = genericType,
                    Body = new HirVar
                    {
                        Name = "value",
                        SymbolId = genericParamSymbol,
                        TypeId = genericType
                    }
                },
                new HirFunc
                {
                    Name = "caller",
                    SymbolId = callerSymbol,
                    ReturnType = concreteSignatureType,
                    Body = new HirVar
                    {
                        Name = "identity",
                        SymbolId = genericSymbol,
                        TypeId = concreteSignatureType,
                        TypeArgumentIds = [intType]
                    }
                }
            ]
        };

        var mirModule = new MirBuilder(
            null,
            typeDescriptors: new Dictionary<int, TypeDescriptor>
            {
                [concreteSignatureType.Value] = new TypeDescriptor.Function([intType], intType)
            }).Build(module);
        var callerFunc = Assert.Single(mirModule.Functions, function => function.Name == "caller");
        var returnTerminator = Assert.IsType<MirReturn>(callerFunc.BasicBlocks.Single(block => block.IsEntry).Terminator);
        var functionRef = Assert.IsType<MirFunctionRef>(returnTerminator.Value);

        Assert.Equal([intType], functionRef.TypeArgumentIds);
        Assert.Equal(concreteSignatureType, functionRef.SignatureTypeId);
        Assert.True(mirModule.TypeDescriptors.ContainsKey(concreteSignatureType.Value));
    }

    [Fact]
    public void Build_BooleanAndExpression_LowersWithShortCircuitControlFlow()
    {
        var boolType = new TypeId(BaseTypes.BoolId);
        var rhsSymbol = new SymbolId(210);
        var callerSymbol = new SymbolId(211);
        var flagSymbol = new SymbolId(212);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "rhs",
                    SymbolId = rhsSymbol,
                    ReturnType = boolType,
                    Body = new HirLiteral
                    {
                        LiteralKind = LiteralKind.Bool,
                        Value = true,
                        TypeId = boolType
                    }
                },
                new HirFunc
                {
                    Name = "caller",
                    SymbolId = callerSymbol,
                    ReturnType = boolType,
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "flag",
                            SymbolId = flagSymbol,
                            TypeId = boolType
                        }
                    ],
                    Body = new HirBinOp
                    {
                        Operator = Eidosc.Hir.BinaryOp.And,
                        Left = new HirVar
                        {
                            Name = "flag",
                            SymbolId = flagSymbol,
                            TypeId = boolType
                        },
                        Right = new HirCall
                        {
                            Function = new HirVar
                            {
                                Name = "rhs",
                                SymbolId = rhsSymbol,
                                TypeId = boolType
                            },
                            TypeId = boolType
                        },
                        TypeId = boolType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);
        var caller = Assert.Single(mirModule.Functions, function => function.Name == "caller");
        var entry = Assert.Single(caller.BasicBlocks, block => block.IsEntry);

        Assert.IsType<MirSwitch>(entry.Terminator);
        Assert.DoesNotContain(entry.Instructions, instruction => instruction is MirCall);
        Assert.Contains(
            caller.BasicBlocks,
            block => !block.IsEntry &&
                     block.Instructions.OfType<MirCall>().Any(call => call.Function is MirFunctionRef { Name: "rhs" }));
    }

    [Fact]
    public void Build_BooleanOrExpression_LowersWithShortCircuitControlFlow()
    {
        var boolType = new TypeId(BaseTypes.BoolId);
        var rhsSymbol = new SymbolId(220);
        var callerSymbol = new SymbolId(221);
        var flagSymbol = new SymbolId(222);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "rhs",
                    SymbolId = rhsSymbol,
                    ReturnType = boolType,
                    Body = new HirLiteral
                    {
                        LiteralKind = LiteralKind.Bool,
                        Value = false,
                        TypeId = boolType
                    }
                },
                new HirFunc
                {
                    Name = "caller",
                    SymbolId = callerSymbol,
                    ReturnType = boolType,
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "flag",
                            SymbolId = flagSymbol,
                            TypeId = boolType
                        }
                    ],
                    Body = new HirBinOp
                    {
                        Operator = Eidosc.Hir.BinaryOp.Or,
                        Left = new HirVar
                        {
                            Name = "flag",
                            SymbolId = flagSymbol,
                            TypeId = boolType
                        },
                        Right = new HirCall
                        {
                            Function = new HirVar
                            {
                                Name = "rhs",
                                SymbolId = rhsSymbol,
                                TypeId = boolType
                            },
                            TypeId = boolType
                        },
                        TypeId = boolType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);
        var caller = Assert.Single(mirModule.Functions, function => function.Name == "caller");
        var entry = Assert.Single(caller.BasicBlocks, block => block.IsEntry);

        Assert.IsType<MirSwitch>(entry.Terminator);
        Assert.DoesNotContain(entry.Instructions, instruction => instruction is MirCall);
        Assert.Contains(
            caller.BasicBlocks,
            block => !block.IsEntry &&
                     block.Instructions.OfType<MirCall>().Any(call => call.Function is MirFunctionRef { Name: "rhs" }));
    }

    [Fact]
    public void Build_UnaryDeref_EmitsLoadFromDerefPlace()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var paramSymbol = new SymbolId(202);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "deref_id",
                    ReturnType = intType,
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
                        Operator = Eidosc.Hir.UnaryOp.Deref,
                        Operand = new HirVar
                        {
                            Name = "x",
                            SymbolId = paramSymbol,
                            TypeId = intType
                        },
                        TypeId = intType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);
        var function = Assert.Single(mirModule.Functions, item => item.Name == "deref_id");
        var entry = Assert.Single(function.BasicBlocks, block => block.IsEntry);
        var load = Assert.Single(entry.Instructions.OfType<MirLoad>());
        var derefSource = Assert.IsType<MirPlace>(load.Source);

        Assert.Equal(PlaceKind.Deref, derefSource.Kind);
        var derefBase = Assert.IsType<MirPlace>(derefSource.Base);
        Assert.Equal(PlaceKind.Local, derefBase.Kind);

        var parameterLocal = Assert.Single(function.Locals, local => local.IsParameter && local.Name == "x");
        Assert.Equal(parameterLocal.Id, derefBase.Local);

        var terminator = Assert.IsType<MirReturn>(entry.Terminator);
        var returnValue = Assert.IsType<MirPlace>(terminator.Value);
        Assert.Equal(load.Target.Local, returnValue.Local);
    }

    [Fact]
    public void Build_DerefAssignment_EmitsStoreToDerefPlace()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var targetSymbol = new SymbolId(203);
        var valueSymbol = new SymbolId(204);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "replace",
                    ReturnType = intType,
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "target",
                            SymbolId = targetSymbol,
                            TypeId = intType
                        },
                        new HirParam
                        {
                            Name = "value",
                            SymbolId = valueSymbol,
                            TypeId = intType
                        }
                    ],
                    Body = new HirBlock
                    {
                        Statements =
                        [
                            new HirAssignStatement
                            {
                                Target = new HirUnaryOp
                                {
                                    Operator = Eidosc.Hir.UnaryOp.Deref,
                                    Operand = new HirVar
                                    {
                                        Name = "target",
                                        SymbolId = targetSymbol,
                                        TypeId = intType
                                    },
                                    TypeId = intType
                                },
                                Value = new HirVar
                                {
                                    Name = "value",
                                    SymbolId = valueSymbol,
                                    TypeId = intType
                                }
                            }
                        ],
                        Result = new HirUnaryOp
                        {
                            Operator = Eidosc.Hir.UnaryOp.Deref,
                            Operand = new HirVar
                            {
                                Name = "target",
                                SymbolId = targetSymbol,
                                TypeId = intType
                            },
                            TypeId = intType
                        },
                        TypeId = intType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);
        var function = Assert.Single(mirModule.Functions, item => item.Name == "replace");
        var entry = Assert.Single(function.BasicBlocks, block => block.IsEntry);
        var store = Assert.Single(entry.Instructions.OfType<MirStore>());

        Assert.Equal(PlaceKind.Deref, store.Target.Kind);
        var derefBase = Assert.IsType<MirPlace>(store.Target.Base);
        Assert.Equal(PlaceKind.Local, derefBase.Kind);
    }

    [Fact]
    public void Build_BuiltinCall_UsesFunctionRefOperandForReferencedExternalFunction()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var stringType = new TypeId(BaseTypes.StringId);
        var builtinSymbol = new SymbolId(9200);
        var sourceSymbol = new SymbolId(9201);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "caller",
                    ReturnType = intType,
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "src",
                            SymbolId = sourceSymbol,
                            TypeId = stringType
                        }
                    ],
                    Body = new HirCall
                    {
                        Function = new HirVar
                        {
                            Name = "string_length",
                            SymbolId = builtinSymbol,
                            TypeId = intType
                        },
                        Arguments =
                        [
                            new HirVar
                            {
                                Name = "src",
                                SymbolId = sourceSymbol,
                                TypeId = stringType
                            }
                        ],
                        TypeId = intType
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);
        var callerFunc = Assert.Single(mirModule.Functions, f => f.Name == "caller");
        var entry = Assert.Single(callerFunc.BasicBlocks, b => b.IsEntry);
        var call = Assert.Single(entry.Instructions.OfType<MirCall>());
        var funcRef = Assert.IsType<MirFunctionRef>(call.Function);

        Assert.Equal(builtinSymbol, funcRef.SymbolId);
        Assert.Equal("string_length", funcRef.Name);
        Assert.DoesNotContain(
            builder.Diagnostics,
            diagnostic => diagnostic.Code?.StartsWith("E5", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Build_BuiltinStringSliceCall_FirstArgumentIsCopiedNotMoved()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var stringType = new TypeId(BaseTypes.StringId);
        var builtinSymbol = new SymbolId(9202);
        var sourceSymbol = new SymbolId(9203);
        var startSymbol = new SymbolId(9204);
        var lenSymbol = new SymbolId(9205);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "caller",
                    ReturnType = stringType,
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "src",
                            SymbolId = sourceSymbol,
                            TypeId = stringType
                        },
                        new HirParam
                        {
                            Name = "start",
                            SymbolId = startSymbol,
                            TypeId = intType
                        },
                        new HirParam
                        {
                            Name = "len",
                            SymbolId = lenSymbol,
                            TypeId = intType
                        }
                    ],
                    Body = new HirCall
                    {
                        Function = new HirVar
                        {
                            Name = "string_slice",
                            SymbolId = builtinSymbol,
                            TypeId = stringType
                        },
                        Arguments =
                        [
                            new HirVar
                            {
                                Name = "src",
                                SymbolId = sourceSymbol,
                                TypeId = stringType
                            },
                            new HirVar
                            {
                                Name = "start",
                                SymbolId = startSymbol,
                                TypeId = intType
                            },
                            new HirVar
                            {
                                Name = "len",
                                SymbolId = lenSymbol,
                                TypeId = intType
                            }
                        ],
                        TypeId = stringType
                    }
                }
            ]
        };

        var effectMap = new ParameterEffectMap();
        effectMap.Add("string_slice", builtinSymbol.Value, [ParameterEffect.Read, ParameterEffect.Read, ParameterEffect.Read]);

        var builder = new MirBuilder(null, parameterEffects: effectMap);
        var mirModule = builder.Build(module);
        var callerFunc = Assert.Single(mirModule.Functions, function => function.Name == "caller");
        var entry = Assert.Single(callerFunc.BasicBlocks, block => block.IsEntry);
        var call = Assert.Single(entry.Instructions.OfType<MirCall>());
        var funcRef = Assert.IsType<MirFunctionRef>(call.Function);
        Assert.Equal("string_slice", funcRef.Name);
        Assert.Equal(3, call.Arguments.Count);

        var sourceLocal = Assert.Single(callerFunc.Locals, local => local.IsParameter && local.Name == "src").Id;
        var sourceCopies = entry.Instructions
            .OfType<MirCopy>()
            .Where(copy => copy.Source.Local.Equals(sourceLocal))
            .ToList();
        Assert.NotEmpty(sourceCopies);
        Assert.DoesNotContain(
            entry.Instructions.OfType<MirMove>(),
            move => move.Source.Local.Equals(sourceLocal));

        var firstArg = Assert.IsType<MirPlace>(call.Arguments[0]);
        Assert.Contains(sourceCopies, copy => copy.Target.Local.Equals(firstArg.Local));
    }

    [Fact]
    public void Build_BuiltinPrintStringCall_FirstArgumentIsCopiedNotMoved()
    {
        var unitType = new TypeId(BaseTypes.UnitId);
        var stringType = new TypeId(BaseTypes.StringId);
        var builtinSymbol = new SymbolId(9210);
        var sourceSymbol = new SymbolId(9211);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "caller",
                    ReturnType = unitType,
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "src",
                            SymbolId = sourceSymbol,
                            TypeId = stringType
                        }
                    ],
                    Body = new HirCall
                    {
                        Function = new HirVar
                        {
                            Name = "print_string",
                            SymbolId = builtinSymbol,
                            TypeId = unitType
                        },
                        Arguments =
                        [
                            new HirVar
                            {
                                Name = "src",
                                SymbolId = sourceSymbol,
                                TypeId = stringType
                            }
                        ],
                        TypeId = unitType
                    }
                }
            ]
        };

        var effectMap = new ParameterEffectMap();
        effectMap.Add("print_string", builtinSymbol.Value, [ParameterEffect.Read]);

        var builder = new MirBuilder(null, parameterEffects: effectMap);
        var mirModule = builder.Build(module);
        var callerFunc = Assert.Single(mirModule.Functions, function => function.Name == "caller");
        var entry = Assert.Single(callerFunc.BasicBlocks, block => block.IsEntry);
        var call = Assert.Single(entry.Instructions.OfType<MirCall>());
        var funcRef = Assert.IsType<MirFunctionRef>(call.Function);
        Assert.Equal("print_string", funcRef.Name);
        Assert.Single(call.Arguments);

        var sourceLocal = Assert.Single(callerFunc.Locals, local => local.IsParameter && local.Name == "src").Id;
        var sourceCopies = entry.Instructions
            .OfType<MirCopy>()
            .Where(copy => copy.Source.Local.Equals(sourceLocal))
            .ToList();
        Assert.NotEmpty(sourceCopies);
        Assert.DoesNotContain(
            entry.Instructions.OfType<MirMove>(),
            move => move.Source.Local.Equals(sourceLocal));

        var firstArg = Assert.IsType<MirPlace>(call.Arguments[0]);
        Assert.Contains(sourceCopies, copy => copy.Target.Local.Equals(firstArg.Local));
    }

}
