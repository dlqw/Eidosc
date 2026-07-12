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
    public void Run_GenericIdentityCall_SpecializesFunctionAndRewritesCall()
    {
        var genericSymbol = new SymbolId(1001);
        var intType = new TypeId(BaseTypes.IntId);
        var unitType = new TypeId(BaseTypes.UnitId);

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

        var callerArg = LocalPlace(1, intType);
        var callerResult = LocalPlace(2, intType);
        var caller = BuildFunction(
            returnType: unitType,
            locals:
            [
                new MirLocal { Id = callerArg.Local, Name = "arg", TypeId = intType, IsParameter = true },
                new MirLocal { Id = callerResult.Local, Name = "res", TypeId = intType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "id",
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
            symbolId: new SymbolId(1002));

        var module = new MirModule
        {
            Name = "generic_single",
            Functions = [genericId, caller]
        };

        var specialized = new MirGenericSpecializer().Run(module);

        Assert.DoesNotContain(specialized.Functions, function => function.SymbolId == genericSymbol);

        var instance = Assert.Single(
            specialized.Functions,
            function => function.Name.StartsWith("id__spec_", StringComparison.Ordinal));
        Assert.Equal(intType, instance.ReturnType);
        Assert.Equal(intType, Assert.Single(instance.Locals, local => local.IsParameter).TypeId);

        var loweredCaller = Assert.Single(specialized.Functions, function => function.Name == "caller");
        var rewrittenCall = Assert.Single(loweredCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(instance.SymbolId, rewrittenRef.SymbolId);
        Assert.Equal(instance.Name, rewrittenRef.Name);
        Assert.Equal(intType, rewrittenRef.TypeId);
        Assert.Equal(instance.SymbolId, instance.FunctionId.SymbolId);
        Assert.Equal(instance.FunctionId, rewrittenRef.FunctionId);
        Assert.Equal(
            """
            func <spec:id:1> symbol=<spec:id:1> fid=<spec:id:1>
            func caller symbol=sym:1002 fid=sym:1002
              call %2:T1 -> <spec:id:1> fid=<spec:id:1> args=[%1:T1]
            """.ReplaceLineEndings("\n"),
            BuildIdentityContract(specialized).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Run_SpecializedWrapper_RewritesReturnOnlyGenericCallInsideBody()
    {
        var unboxSymbol = new SymbolId(1003);
        var wrapperSymbol = new SymbolId(1004);
        var callerSymbol = new SymbolId(1005);
        var rawPtrType = new TypeId(BaseTypes.RawPtrId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var typeVariable = new TypeId(1006);
        var tupleType = new TypeId(1007);

        var unboxArg = LocalPlace(1, rawPtrType);
        var unbox = BuildFunction(
            returnType: typeVariable,
            locals:
            [
                new MirLocal { Id = unboxArg.Local, Name = "ptr", TypeId = rawPtrType, IsParameter = true }
            ],
            instructions: [],
            returnValue: unboxArg,
            name: "unbox",
            symbolId: unboxSymbol,
            genericParameterCount: 1,
            genericTypeParameterIds: [typeVariable]);

        var wrapperArg = LocalPlace(1, rawPtrType);
        var wrapperResult = LocalPlace(2, typeVariable);
        var wrapper = BuildFunction(
            returnType: typeVariable,
            locals:
            [
                new MirLocal { Id = wrapperArg.Local, Name = "ptr", TypeId = rawPtrType, IsParameter = true },
                new MirLocal { Id = wrapperResult.Local, Name = "value", TypeId = typeVariable }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = wrapperResult,
                    Function = new MirFunctionRef
                    {
                        Name = "unbox",
                        SymbolId = unboxSymbol,
                        TypeId = TypeId.None
                    },
                    Arguments = [wrapperArg]
                }
            ],
            returnValue: wrapperResult,
            name: "wrapper",
            symbolId: wrapperSymbol,
            genericParameterCount: 1,
            genericTypeParameterIds: [typeVariable]);

        var callerArg = LocalPlace(1, rawPtrType);
        var callerResult = LocalPlace(2, tupleType);
        var caller = BuildFunction(
            returnType: unitType,
            locals:
            [
                new MirLocal { Id = callerArg.Local, Name = "ptr", TypeId = rawPtrType, IsParameter = true },
                new MirLocal { Id = callerResult.Local, Name = "value", TypeId = tupleType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "wrapper",
                        SymbolId = wrapperSymbol,
                        TypeId = typeVariable,
                        TypeArgumentIds = [tupleType]
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
            symbolId: callerSymbol);

        var specialized = new MirGenericSpecializer().Run(new MirModule
        {
            Name = "return_only_nested_specialization",
            Functions = [unbox, wrapper, caller],
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [typeVariable.Value] = new TypeDescriptor.TypeVar(0),
                [tupleType.Value] = new TypeDescriptor.Tuple([new TypeId(BaseTypes.StringId), new TypeId(BaseTypes.IntId)])
            }
        });

        var specializedUnbox = Assert.Single(
            specialized.Functions,
            function => function.Name.StartsWith("unbox__spec_", StringComparison.Ordinal));
        Assert.Equal(tupleType, specializedUnbox.ReturnType);

        var specializedWrapper = Assert.Single(
            specialized.Functions,
            function => function.Name.StartsWith("wrapper__spec_", StringComparison.Ordinal));
        var innerCall = Assert.Single(specializedWrapper.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var innerRef = Assert.IsType<MirFunctionRef>(innerCall.Function);
        Assert.Equal(specializedUnbox.SymbolId, innerRef.SymbolId);
        Assert.Equal(specializedUnbox.Name, innerRef.Name);
        Assert.Equal(tupleType, innerRef.TypeId);
    }

    [Fact]
    public void Run_ReturnOnlyGenericCall_RewritesWhenTargetTypeFlowsBackFromLaterCallArgument()
    {
        var unboxSymbol = new SymbolId(1101);
        var someSymbol = new SymbolId(1102);
        var wrapperSymbol = new SymbolId(1103);
        var callerSymbol = new SymbolId(1104);
        var optionSymbol = new SymbolId(1200);
        var rawPtrType = new TypeId(BaseTypes.RawPtrId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var typeVariable = new TypeId(1201);
        var tupleType = new TypeId(1202);
        var optionOpenType = new TypeId(1203);
        var optionTupleType = new TypeId(1204);

        var unboxArg = LocalPlace(1, rawPtrType);
        var unbox = BuildFunction(
            returnType: typeVariable,
            locals:
            [
                new MirLocal { Id = unboxArg.Local, Name = "ptr", TypeId = rawPtrType, IsParameter = true }
            ],
            instructions: [],
            returnValue: unboxArg,
            name: "Std__FFI__unbox_value",
            symbolId: unboxSymbol,
            genericParameterCount: 1,
            genericTypeParameterIds: [typeVariable],
            functionId: BuildFunctionId(unboxSymbol, "Std__FFI__unbox_value", "Std::FFI::unbox_value"));

        var someArg = LocalPlace(1, typeVariable);
        var someResult = LocalPlace(2, optionOpenType);
        var some = BuildFunction(
            returnType: optionOpenType,
            locals:
            [
                new MirLocal { Id = someArg.Local, Name = "value", TypeId = typeVariable, IsParameter = true },
                new MirLocal { Id = someResult.Local, Name = "result", TypeId = optionOpenType }
            ],
            instructions: [],
            returnValue: someResult,
            name: "Some",
            symbolId: someSymbol,
            genericParameterCount: 1,
            genericTypeParameterIds: [typeVariable]);

        var wrapperArg = LocalPlace(1, rawPtrType);
        var unboxed = LocalPlace(2, TypeId.None);
        var copied = LocalPlace(3, TypeId.None);
        var wrapped = LocalPlace(4, optionOpenType);
        var wrapper = BuildFunction(
            returnType: optionOpenType,
            locals:
            [
                new MirLocal { Id = wrapperArg.Local, Name = "ptr", TypeId = rawPtrType, IsParameter = true },
                new MirLocal { Id = unboxed.Local, Name = "unboxed", TypeId = TypeId.None },
                new MirLocal { Id = copied.Local, Name = "copied", TypeId = TypeId.None },
                new MirLocal { Id = wrapped.Local, Name = "wrapped", TypeId = optionOpenType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = unboxed,
                    Function = new MirFunctionRef
                    {
                        Name = "Std__FFI__unbox_value",
                        SymbolId = unboxSymbol,
                        TypeId = TypeId.None
                    },
                    Arguments = [wrapperArg]
                },
                new MirCopy
                {
                    Target = copied,
                    Source = unboxed
                },
                new MirCall
                {
                    Target = wrapped,
                    Function = new MirFunctionRef
                    {
                        Name = "Some",
                        SymbolId = someSymbol,
                        TypeId = optionOpenType
                    },
                    Arguments = [copied]
                }
            ],
            returnValue: wrapped,
            name: "wrapper",
            symbolId: wrapperSymbol,
            genericParameterCount: 1,
            genericTypeParameterIds: [typeVariable]);

        var callerArg = LocalPlace(1, rawPtrType);
        var callerResult = LocalPlace(2, optionTupleType);
        var caller = BuildFunction(
            returnType: unitType,
            locals:
            [
                new MirLocal { Id = callerArg.Local, Name = "ptr", TypeId = rawPtrType, IsParameter = true },
                new MirLocal { Id = callerResult.Local, Name = "value", TypeId = optionTupleType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "wrapper",
                        SymbolId = wrapperSymbol,
                        TypeId = optionOpenType,
                        TypeArgumentIds = [tupleType]
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
            symbolId: callerSymbol);

        var specialized = new MirGenericSpecializer().Run(new MirModule
        {
            Name = "return_only_nested_specialization_from_later_argument",
            Functions = [unbox, some, wrapper, caller],
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [typeVariable.Value] = new TypeDescriptor.TypeVar(0),
                [tupleType.Value] = new TypeDescriptor.Tuple([new TypeId(BaseTypes.StringId), new TypeId(BaseTypes.IntId)]),
                [optionOpenType.Value] = new TypeDescriptor.TyCon(TypeConstructorKey.FromSymbol(optionSymbol), [typeVariable]),
                [optionTupleType.Value] = new TypeDescriptor.TyCon(TypeConstructorKey.FromSymbol(optionSymbol), [tupleType])
            }
        });

        var specializedUnbox = Assert.Single(
            specialized.Functions,
            function => function.Name.StartsWith("Std__FFI__unbox_value__spec_", StringComparison.Ordinal));
        Assert.Equal(tupleType, specializedUnbox.ReturnType);

        var specializedWrapper = Assert.Single(
            specialized.Functions,
            function => function.Name.StartsWith("wrapper__spec_", StringComparison.Ordinal));
        var innerCall = Assert.Single(
            specializedWrapper.BasicBlocks.Single().Instructions.OfType<MirCall>(),
            call => call.Function is MirFunctionRef functionRef &&
                    functionRef.Name.StartsWith("Std__FFI__unbox_value", StringComparison.Ordinal));
        var innerRef = Assert.IsType<MirFunctionRef>(innerCall.Function);
        Assert.Equal(specializedUnbox.SymbolId, innerRef.SymbolId);
        Assert.Equal(specializedUnbox.Name, innerRef.Name);
        Assert.Equal(tupleType, innerRef.TypeId);
    }

    [Fact]
    public void Run_ReturnOnlyGenericCall_RewritesWhenTargetTypeFlowsBackFromCallableArgument()
    {
        var unboxSymbol = new SymbolId(1121);
        var wrapperSymbol = new SymbolId(1122);
        var callerSymbol = new SymbolId(1123);
        var updateSymbol = new SymbolId(1124);
        var rawPtrType = new TypeId(BaseTypes.RawPtrId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var typeVariable = new TypeId(1125);
        var tupleType = new TypeId(1126);
        var openUpdateType = new TypeId(1127);
        var tupleUpdateType = new TypeId(1128);

        var unboxArg = LocalPlace(1, rawPtrType);
        var unbox = BuildFunction(
            returnType: typeVariable,
            locals:
            [
                new MirLocal { Id = unboxArg.Local, Name = "ptr", TypeId = rawPtrType, IsParameter = true }
            ],
            instructions: [],
            returnValue: unboxArg,
            name: "Std__FFI__unbox_value",
            symbolId: unboxSymbol,
            genericParameterCount: 1,
            genericTypeParameterIds: [typeVariable],
            functionId: BuildFunctionId(unboxSymbol, "Std__FFI__unbox_value", "Std::FFI::unbox_value"));

        var updateArg = LocalPlace(1, tupleType);
        var update = BuildFunction(
            returnType: tupleType,
            locals:
            [
                new MirLocal { Id = updateArg.Local, Name = "value", TypeId = tupleType, IsParameter = true }
            ],
            instructions: [],
            returnValue: updateArg,
            name: "update_tuple",
            symbolId: updateSymbol,
            functionId: BuildFunctionId(updateSymbol, "update_tuple"));

        var wrapperPtr = LocalPlace(1, rawPtrType);
        var wrapperUpdate = LocalPlace(2, openUpdateType);
        var unboxed = LocalPlace(3, TypeId.None);
        var copied = LocalPlace(4, TypeId.None);
        var updated = LocalPlace(5, typeVariable);
        var wrapper = BuildFunction(
            returnType: typeVariable,
            locals:
            [
                new MirLocal { Id = wrapperPtr.Local, Name = "ptr", TypeId = rawPtrType, IsParameter = true },
                new MirLocal { Id = wrapperUpdate.Local, Name = "update", TypeId = openUpdateType, IsParameter = true },
                new MirLocal { Id = unboxed.Local, Name = "unboxed", TypeId = TypeId.None },
                new MirLocal { Id = copied.Local, Name = "copied", TypeId = TypeId.None },
                new MirLocal { Id = updated.Local, Name = "updated", TypeId = typeVariable }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = unboxed,
                    Function = new MirFunctionRef
                    {
                        Name = "Std__FFI__unbox_value",
                        SymbolId = unboxSymbol,
                        TypeId = TypeId.None
                    },
                    Arguments = [wrapperPtr]
                },
                new MirCopy
                {
                    Target = copied,
                    Source = unboxed
                },
                new MirCall
                {
                    Target = updated,
                    Function = wrapperUpdate,
                    Arguments = [copied]
                }
            ],
            returnValue: updated,
            name: "wrapper",
            symbolId: wrapperSymbol,
            genericParameterCount: 1,
            genericTypeParameterIds: [typeVariable]);

        var callerPtr = LocalPlace(1, rawPtrType);
        var callerResult = LocalPlace(2, tupleType);
        var caller = BuildFunction(
            returnType: unitType,
            locals:
            [
                new MirLocal { Id = callerPtr.Local, Name = "ptr", TypeId = rawPtrType, IsParameter = true },
                new MirLocal { Id = callerResult.Local, Name = "value", TypeId = tupleType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "wrapper",
                        SymbolId = wrapperSymbol,
                        TypeId = typeVariable,
                        TypeArgumentIds = [tupleType]
                    },
                    Arguments =
                    [
                        callerPtr,
                        new MirFunctionRef
                        {
                            Name = "update_tuple",
                            SymbolId = updateSymbol,
                            TypeId = tupleUpdateType,
                            FunctionId = update.FunctionId
                        }
                    ]
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "caller",
            symbolId: callerSymbol);

        var specialized = new MirGenericSpecializer().Run(new MirModule
        {
            Name = "return_only_nested_specialization_from_callable_argument",
            Functions = [unbox, update, wrapper, caller],
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [typeVariable.Value] = new TypeDescriptor.TypeVar(0),
                [tupleType.Value] = new TypeDescriptor.Tuple([new TypeId(BaseTypes.StringId), new TypeId(BaseTypes.IntId)]),
                [openUpdateType.Value] = new TypeDescriptor.Function([typeVariable], typeVariable),
                [tupleUpdateType.Value] = new TypeDescriptor.Function([tupleType], tupleType)
            }
        });

        var specializedUnbox = Assert.Single(
            specialized.Functions,
            function => function.Name.StartsWith("Std__FFI__unbox_value__spec_", StringComparison.Ordinal));
        Assert.Equal(tupleType, specializedUnbox.ReturnType);

        var specializedWrapper = Assert.Single(
            specialized.Functions,
            function => function.Name.StartsWith("wrapper__spec_", StringComparison.Ordinal));
        var innerCall = Assert.Single(
            specializedWrapper.BasicBlocks.Single().Instructions.OfType<MirCall>(),
            call => call.Function is MirFunctionRef functionRef &&
                    functionRef.Name.StartsWith("Std__FFI__unbox_value", StringComparison.Ordinal));
        var innerRef = Assert.IsType<MirFunctionRef>(innerCall.Function);
        Assert.Equal(specializedUnbox.SymbolId, innerRef.SymbolId);
        Assert.Equal(specializedUnbox.Name, innerRef.Name);
        Assert.Equal(tupleType, innerRef.TypeId);
    }

    [Fact]
    public void Run_ReturnOnlyGenericCall_RewritesWhenExplicitTypeArgumentIsOuterOpenVariable()
    {
        var unboxSymbol = new SymbolId(1301);
        var wrapperSymbol = new SymbolId(1302);
        var callerSymbol = new SymbolId(1303);
        var optionSymbol = new SymbolId(1304);
        var rawPtrType = new TypeId(BaseTypes.RawPtrId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var unboxTypeVariable = new TypeId(1305);
        var wrapperTypeVariable = new TypeId(1306);
        var explicitTypeArgumentVariable = new TypeId(1307);
        var tupleType = new TypeId(1308);
        var optionOpenType = new TypeId(1309);
        var optionTupleType = new TypeId(1310);
        var unboxFunctionType = new TypeId(1311);

        var unboxArg = LocalPlace(1, rawPtrType);
        var unbox = BuildFunction(
            returnType: unboxTypeVariable,
            locals:
            [
                new MirLocal { Id = unboxArg.Local, Name = "ptr", TypeId = rawPtrType, IsParameter = true }
            ],
            instructions: [],
            returnValue: unboxArg,
            name: "Std__FFI__unbox_value",
            symbolId: unboxSymbol,
            genericParameterCount: 1,
            genericTypeParameterIds: [unboxTypeVariable],
            functionId: BuildFunctionId(unboxSymbol, "Std__FFI__unbox_value", "Std::FFI::unbox_value"));

        var someArg = LocalPlace(1, wrapperTypeVariable);
        var someResult = LocalPlace(2, optionOpenType);
        var some = BuildFunction(
            returnType: optionOpenType,
            locals:
            [
                new MirLocal { Id = someArg.Local, Name = "value", TypeId = wrapperTypeVariable, IsParameter = true },
                new MirLocal { Id = someResult.Local, Name = "result", TypeId = optionOpenType }
            ],
            instructions: [],
            returnValue: someResult,
            name: "Some",
            symbolId: new SymbolId(1310),
            genericParameterCount: 1,
            genericTypeParameterIds: [wrapperTypeVariable]);

        var wrapperArg = LocalPlace(1, rawPtrType);
        var unboxed = LocalPlace(2, wrapperTypeVariable);
        var copied = LocalPlace(3, wrapperTypeVariable);
        var wrapped = LocalPlace(4, optionOpenType);
        var wrapper = BuildFunction(
            returnType: optionOpenType,
            locals:
            [
                new MirLocal { Id = wrapperArg.Local, Name = "ptr", TypeId = rawPtrType, IsParameter = true },
                new MirLocal { Id = unboxed.Local, Name = "unboxed", TypeId = wrapperTypeVariable },
                new MirLocal { Id = copied.Local, Name = "copied", TypeId = wrapperTypeVariable },
                new MirLocal { Id = wrapped.Local, Name = "wrapped", TypeId = optionOpenType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = unboxed,
                    Function = new MirFunctionRef
                    {
                        Name = "Std__FFI__unbox_value",
                        SymbolId = unboxSymbol,
                        TypeId = unboxFunctionType,
                        SignatureTypeId = unboxFunctionType,
                        TypeArgumentIds = [explicitTypeArgumentVariable]
                    },
                    Arguments = [wrapperArg]
                },
                new MirMove
                {
                    Target = copied,
                    Source = unboxed
                },
                new MirCall
                {
                    Target = wrapped,
                    Function = new MirFunctionRef
                    {
                        Name = "Some",
                        SymbolId = some.SymbolId,
                        TypeId = optionOpenType
                    },
                    Arguments = [copied]
                }
            ],
            returnValue: wrapped,
            name: "wrapper",
            symbolId: wrapperSymbol,
            genericParameterCount: 1,
            genericTypeParameterIds: [wrapperTypeVariable]);

        var callerArg = LocalPlace(1, rawPtrType);
        var callerResult = LocalPlace(2, optionTupleType);
        var caller = BuildFunction(
            returnType: unitType,
            locals:
            [
                new MirLocal { Id = callerArg.Local, Name = "ptr", TypeId = rawPtrType, IsParameter = true },
                new MirLocal { Id = callerResult.Local, Name = "value", TypeId = optionTupleType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "wrapper",
                        SymbolId = wrapperSymbol,
                        TypeId = optionOpenType,
                        TypeArgumentIds = [tupleType]
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
            symbolId: callerSymbol);

        var specialized = new MirGenericSpecializer().Run(new MirModule
        {
            Name = "return_only_explicit_outer_open_type_argument",
            Functions = [unbox, some, wrapper, caller],
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [unboxTypeVariable.Value] = new TypeDescriptor.TypeVar(unboxTypeVariable.Value),
                [wrapperTypeVariable.Value] = new TypeDescriptor.TypeVar(wrapperTypeVariable.Value),
                [tupleType.Value] = new TypeDescriptor.Tuple([new TypeId(BaseTypes.StringId), new TypeId(BaseTypes.IntId)]),
                [optionOpenType.Value] = new TypeDescriptor.TyCon(TypeConstructorKey.FromSymbol(optionSymbol), [wrapperTypeVariable]),
                [optionTupleType.Value] = new TypeDescriptor.TyCon(TypeConstructorKey.FromSymbol(optionSymbol), [tupleType]),
                [unboxFunctionType.Value] = new TypeDescriptor.Function([rawPtrType], explicitTypeArgumentVariable)
            }
        });

        var specializedUnbox = Assert.Single(
            specialized.Functions,
            function => function.Name.StartsWith("Std__FFI__unbox_value__spec_", StringComparison.Ordinal));
        Assert.Equal(tupleType, specializedUnbox.ReturnType);

        var specializedWrapper = Assert.Single(
            specialized.Functions,
            function => function.Name.StartsWith("wrapper__spec_", StringComparison.Ordinal));
        var innerCall = Assert.Single(
            specializedWrapper.BasicBlocks.Single().Instructions.OfType<MirCall>(),
            call => call.Function is MirFunctionRef functionRef &&
                    functionRef.Name.StartsWith("Std__FFI__unbox_value", StringComparison.Ordinal));
        var innerRef = Assert.IsType<MirFunctionRef>(innerCall.Function);
        Assert.Equal(specializedUnbox.SymbolId, innerRef.SymbolId);
        Assert.Equal(specializedUnbox.Name, innerRef.Name);
        Assert.Equal(tupleType, innerRef.TypeId);
        Assert.DoesNotContain(innerRef.TypeArgumentIds, typeId => typeId == explicitTypeArgumentVariable);
    }

    [Fact]
    public void Run_FunctionRefWithTemplateSymbolAndDifferentName_DoesNotResolveByName()
    {
        var firstGenericSymbol = new SymbolId(1003);
        var secondGenericSymbol = new SymbolId(1004);
        var callerSymbol = new SymbolId(1005);
        var intType = new TypeId(BaseTypes.IntId);
        var unitType = new TypeId(BaseTypes.UnitId);

        var firstGeneric = BuildFunction(
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
            name: "first",
            symbolId: firstGenericSymbol);
        var secondGeneric = BuildFunction(
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
            name: "second",
            symbolId: secondGenericSymbol);

        var callerArg = LocalPlace(1, intType);
        var callerResult = LocalPlace(2, intType);
        var caller = BuildFunction(
            returnType: unitType,
            locals:
            [
                new MirLocal { Id = callerArg.Local, Name = "arg", TypeId = intType, IsParameter = true },
                new MirLocal { Id = callerResult.Local, Name = "res", TypeId = intType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "second",
                        SymbolId = firstGenericSymbol,
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
            name: "caller_identity_conflict",
            symbolId: callerSymbol);

        var module = new MirModule
        {
            Name = "generic_symbol_name_conflict",
            Functions = [firstGeneric, secondGeneric, caller]
        };

        var specialized = new MirGenericSpecializer().Run(module);

        Assert.DoesNotContain(
            specialized.Functions,
            function => function.Name.StartsWith("second__spec_", StringComparison.Ordinal));

        var loweredCaller = Assert.Single(specialized.Functions, function => function.SymbolId == callerSymbol);
        var call = Assert.Single(loweredCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var functionRef = Assert.IsType<MirFunctionRef>(call.Function);
        Assert.Equal(firstGenericSymbol, functionRef.SymbolId);
        Assert.Equal("second", functionRef.Name);
    }

    [Fact]
    public void Run_FunctionRefWithUnknownValidSymbol_DoesNotResolveTemplateByName()
    {
        var genericSymbol = new SymbolId(1006);
        var unknownSymbol = new SymbolId(1007);
        var callerSymbol = new SymbolId(1008);
        var intType = new TypeId(BaseTypes.IntId);
        var unitType = new TypeId(BaseTypes.UnitId);

        var generic = BuildFunction(
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

        var callerArg = LocalPlace(1, intType);
        var callerResult = LocalPlace(2, intType);
        var caller = BuildFunction(
            returnType: unitType,
            locals:
            [
                new MirLocal { Id = callerArg.Local, Name = "arg", TypeId = intType, IsParameter = true },
                new MirLocal { Id = callerResult.Local, Name = "res", TypeId = intType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "id",
                        SymbolId = unknownSymbol,
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
            name: "caller_unknown_symbol",
            symbolId: callerSymbol);

        var module = new MirModule
        {
            Name = "generic_unknown_symbol_name",
            Functions = [generic, caller]
        };

        var specialized = new MirGenericSpecializer().Run(module);

        Assert.DoesNotContain(
            specialized.Functions,
            function => function.Name.StartsWith("id__spec_", StringComparison.Ordinal));

        var loweredCaller = Assert.Single(specialized.Functions, function => function.SymbolId == callerSymbol);
        var call = Assert.Single(loweredCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var functionRef = Assert.IsType<MirFunctionRef>(call.Function);
        Assert.Equal(unknownSymbol, functionRef.SymbolId);
        Assert.Equal("id", functionRef.Name);
    }

    [Fact]
    public void Run_FunctionRefWithDifferentSymbol_ResolvesTemplateByStructuredFunctionIdentity()
    {
        var genericSymbol = new SymbolId(1009);
        var importedSymbol = new SymbolId(1010);
        var callerSymbol = new SymbolId(1011);
        var intType = new TypeId(BaseTypes.IntId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var qualifiedName = "Lib::id";

        var generic = BuildFunction(
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
            name: "Lib__id",
            symbolId: genericSymbol,
            functionId: BuildFunctionId(genericSymbol, "id", qualifiedName));

        var callerArg = LocalPlace(1, intType);
        var callerResult = LocalPlace(2, intType);
        var caller = BuildFunction(
            returnType: unitType,
            locals:
            [
                new MirLocal { Id = callerArg.Local, Name = "arg", TypeId = intType, IsParameter = true },
                new MirLocal { Id = callerResult.Local, Name = "res", TypeId = intType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "imported_id",
                        SymbolId = importedSymbol,
                        FunctionId = BuildFunctionId(importedSymbol, "id", qualifiedName),
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
            name: "caller_structured_identity",
            symbolId: callerSymbol);

        var module = new MirModule
        {
            Name = "generic_structured_identity",
            Functions = [generic, caller]
        };

        var specialized = new MirGenericSpecializer().Run(module);

        var instance = Assert.Single(
            specialized.Functions,
            function => function.Name.StartsWith("Lib__id__spec_", StringComparison.Ordinal));
        var loweredCaller = Assert.Single(specialized.Functions, function => function.SymbolId == callerSymbol);
        var call = Assert.Single(loweredCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var functionRef = Assert.IsType<MirFunctionRef>(call.Function);
        Assert.Equal(instance.SymbolId, functionRef.SymbolId);
        Assert.Equal(instance.FunctionId, functionRef.FunctionId);
    }

    [Fact]
    public void Run_FunctionRefWithKnownConcreteSymbol_DoesNotResolveTemplateByStaleFunctionIdentity()
    {
        var genericSymbol = new SymbolId(1012);
        var concreteSymbol = new SymbolId(1013);
        var callerSymbol = new SymbolId(1014);
        var intType = new TypeId(BaseTypes.IntId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var genericFunctionId = BuildFunctionId(genericSymbol, "id", "Lib::id");

        var generic = BuildFunction(
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
            name: "Lib__id",
            symbolId: genericSymbol,
            functionId: genericFunctionId);

        var concrete = BuildFunction(
            returnType: intType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "x",
                    TypeId = intType,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: LocalPlace(1, intType),
            name: "id_int",
            symbolId: concreteSymbol,
            functionId: genericFunctionId);

        var callerArg = LocalPlace(1, intType);
        var callerResult = LocalPlace(2, intType);
        var caller = BuildFunction(
            returnType: unitType,
            locals:
            [
                new MirLocal { Id = callerArg.Local, Name = "arg", TypeId = intType, IsParameter = true },
                new MirLocal { Id = callerResult.Local, Name = "res", TypeId = intType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "id",
                        SymbolId = concreteSymbol,
                        FunctionId = genericFunctionId,
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
            name: "caller_concrete_symbol_stale_function_id",
            symbolId: callerSymbol);

        var specialized = new MirGenericSpecializer().Run(new MirModule
        {
            Name = "generic_concrete_symbol_stale_function_identity",
            Functions = [generic, concrete, caller]
        });

        Assert.DoesNotContain(
            specialized.Functions,
            function => function.Name.StartsWith("Lib__id__spec_", StringComparison.Ordinal));

        var loweredCaller = Assert.Single(specialized.Functions, function => function.SymbolId == callerSymbol);
        var call = Assert.Single(loweredCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var functionRef = Assert.IsType<MirFunctionRef>(call.Function);
        Assert.Equal(concreteSymbol, functionRef.SymbolId);
        Assert.Equal(genericFunctionId, functionRef.FunctionId);
    }

    [Fact]
    public void Run_FunctionRefWithKnownConcreteSymbolAndEmptyName_DoesNotResolveTemplateByStaleFunctionIdentity()
    {
        var genericSymbol = new SymbolId(1015);
        var concreteSymbol = new SymbolId(1016);
        var callerSymbol = new SymbolId(1017);
        var intType = new TypeId(BaseTypes.IntId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var genericFunctionId = BuildFunctionId(genericSymbol, "id", "Lib::id");

        var generic = BuildFunction(
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
            name: "Lib__id",
            symbolId: genericSymbol,
            functionId: genericFunctionId);

        var concrete = BuildFunction(
            returnType: intType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "x",
                    TypeId = intType,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: LocalPlace(1, intType),
            name: string.Empty,
            symbolId: concreteSymbol,
            functionId: genericFunctionId);

        var callerArg = LocalPlace(1, intType);
        var callerResult = LocalPlace(2, intType);
        var caller = BuildFunction(
            returnType: unitType,
            locals:
            [
                new MirLocal { Id = callerArg.Local, Name = "arg", TypeId = intType, IsParameter = true },
                new MirLocal { Id = callerResult.Local, Name = "res", TypeId = intType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "id",
                        SymbolId = concreteSymbol,
                        FunctionId = genericFunctionId,
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
            name: "caller_concrete_symbol_empty_name_stale_function_id",
            symbolId: callerSymbol);

        var specialized = new MirGenericSpecializer().Run(new MirModule
        {
            Name = "generic_concrete_symbol_empty_name_stale_function_identity",
            Functions = [generic, concrete, caller]
        });

        Assert.DoesNotContain(
            specialized.Functions,
            function => function.Name.StartsWith("Lib__id__spec_", StringComparison.Ordinal));

        var loweredCaller = Assert.Single(specialized.Functions, function => function.SymbolId == callerSymbol);
        var call = Assert.Single(loweredCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var functionRef = Assert.IsType<MirFunctionRef>(call.Function);
        Assert.Equal(concreteSymbol, functionRef.SymbolId);
        Assert.Equal(genericFunctionId, functionRef.FunctionId);
    }

    [Fact]
    public void Run_GenericIdentityCalledWithIntAndBool_CreatesTwoInstances()
    {
        var genericSymbol = new SymbolId(1101);
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var unitType = new TypeId(BaseTypes.UnitId);

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

        var intArg = LocalPlace(1, intType);
        var boolArg = LocalPlace(2, boolType);
        var intResult = LocalPlace(3, intType);
        var boolResult = LocalPlace(4, boolType);
        var caller = BuildFunction(
            returnType: unitType,
            locals:
            [
                new MirLocal { Id = intArg.Local, Name = "a", TypeId = intType, IsParameter = true },
                new MirLocal { Id = boolArg.Local, Name = "b", TypeId = boolType, IsParameter = true },
                new MirLocal { Id = intResult.Local, Name = "ri", TypeId = intType },
                new MirLocal { Id = boolResult.Local, Name = "rb", TypeId = boolType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = intResult,
                    Function = new MirFunctionRef
                    {
                        Name = "id",
                        SymbolId = genericSymbol,
                        TypeId = TypeId.None
                    },
                    Arguments = [intArg]
                },
                new MirCall
                {
                    Target = boolResult,
                    Function = new MirFunctionRef
                    {
                        Name = "id",
                        SymbolId = genericSymbol,
                        TypeId = TypeId.None
                    },
                    Arguments = [boolArg]
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "caller",
            symbolId: new SymbolId(1102));

        var module = new MirModule
        {
            Name = "generic_multi",
            Functions = [genericId, caller]
        };

        var specialized = new MirGenericSpecializer().Run(module);
        var instances = specialized.Functions
            .Where(function => function.Name.StartsWith("id__spec_", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, instances.Count);
        Assert.Equal(2, instances.Select(function => function.SymbolId).Distinct().Count());
        Assert.Equal(
            new HashSet<TypeId> { intType, boolType },
            instances.Select(function => Assert.Single(function.Locals, local => local.IsParameter).TypeId).ToHashSet());
        Assert.DoesNotContain(specialized.Functions, function => function.SymbolId == genericSymbol);

        var rewrittenCalls = specialized.Functions
            .Single(function => function.Name == "caller")
            .BasicBlocks.Single()
            .Instructions.OfType<MirCall>()
            .ToList();
        Assert.Equal(2, rewrittenCalls.Count);
        Assert.Equal(
            2,
            rewrittenCalls
                .Select(call => Assert.IsType<MirFunctionRef>(call.Function).SymbolId)
                .Distinct()
                .Count());
    }

    private static MirFunc BuildFunction(
        TypeId returnType,
        List<MirLocal> locals,
        List<MirInstruction> instructions,
        MirOperand returnValue,
        string name,
        SymbolId symbolId,
        int genericParameterCount = 0,
        List<TypeId>? genericTypeParameterIds = null,
        TraitInvokeHelperKind traitInvokeHelper = TraitInvokeHelperKind.None,
        SymbolId traitInvokeHelperTraitId = default,
        FunctionId? functionId = null,
        string? sourceName = null)
    {
        var normalizedTraitInvokeHelperTraitId = traitInvokeHelper == TraitInvokeHelperKind.None
            ? SymbolId.None
            : traitInvokeHelperTraitId;

        return new MirFunc
        {
            Name = name,
            SourceName = sourceName ?? string.Empty,
            ReturnType = returnType,
            GenericParameterCount = genericParameterCount,
            GenericTypeParameterIds = genericTypeParameterIds ?? [],
            TraitInvokeHelper = traitInvokeHelper,
            TraitInvokeHelperTraitId = normalizedTraitInvokeHelperTraitId,
            EntryBlockId = new BlockId { Value = 1 },
            SymbolId = symbolId,
            FunctionId = functionId ?? BuildFunctionId(symbolId, name),
            Locals = locals,
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions = instructions,
                    Terminator = new MirReturn
                    {
                        Value = returnValue
                    }
                }
            ]
        };
    }

    private static FunctionId BuildFunctionId(SymbolId symbolId, string name)
    {
        return BuildFunctionId(symbolId, name, name);
    }

    private static FunctionId BuildFunctionId(SymbolId symbolId, string name, string qualifiedName)
    {
        return new FunctionId
        {
            SymbolId = symbolId,
            Name = name,
            QualifiedName = qualifiedName
        };
    }

    private static List<ImplSymbol> MirTraitImplsFrom(SymbolTable symbolTable)
    {
        return symbolTable.Symbols.Values
            .OfType<ImplSymbol>()
            .ToList();
    }

    private static List<MirTypeAliasInfo> MirTypeAliasesFrom(SymbolTable symbolTable)
    {
        return symbolTable.Symbols.Values
            .OfType<AdtSymbol>()
            .Where(static symbol => symbol.IsTypeAlias &&
                                    symbol.Id.IsValid &&
                                    symbol.TypeId.IsValid &&
                                    symbol.AliasTarget is { IsValid: true } &&
                                    !string.IsNullOrWhiteSpace(symbol.Name))
            .Select(static symbol => new MirTypeAliasInfo
            {
                AliasId = symbol.Id,
                Name = symbol.Name,
                TypeId = symbol.TypeId,
                AliasTarget = symbol.AliasTarget!.Value,
                TypeParameterIds = symbol.TypeParams.ToList()
            })
            .ToList();
    }

    private static MirPlace LocalPlace(int id, TypeId typeId)
    {
        return new MirPlace
        {
            Kind = PlaceKind.Local,
            Local = new LocalId { Value = id },
            TypeId = typeId
        };
    }
}
