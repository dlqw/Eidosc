using Eidosc.Symbols;
using Eidosc;
using Eidosc.CodeGen.Llvm;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Llvm;

public partial class MirToLlvmConverterTests
{
    [Fact]
    public void Convert_ModuleDirectCallWithFunctionPointerArgument_BitcastsToExpectedFunctionType()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var concreteFunctionTypeId = new TypeId(9020);
        var erasedTypeVarId = new TypeId(9021);
        var erasedFunctionTypeId = new TypeId(9022);
        var calleeSymbol = new SymbolId(3020);

        var acceptsPredicate = BuildFunction(
            unitType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "pred",
                    TypeId = concreteFunctionTypeId,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "accepts_predicate",
            symbolId: calleeSymbol);

        var predicatePlace = LocalPlace(1, concreteFunctionTypeId);
        var caller = BuildFunction(
            unitType,
            locals:
            [
                new MirLocal { Id = predicatePlace.Local, Name = "pred", TypeId = concreteFunctionTypeId, IsParameter = true }
            ],
            instructions:
            [
                new MirCall
                {
                    Function = new MirFunctionRef
                    {
                        Name = "accepts_predicate",
                        SymbolId = calleeSymbol,
                        TypeId = unitType
                    },
                    Arguments = [predicatePlace]
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "caller_direct_function_arg",
            symbolId: new SymbolId(3021));

        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "direct_function_arg",
            DynamicTypeKeys = new Dictionary<int, string>
            {
                [erasedTypeVarId.Value] = "TyVar_9021",
                [concreteFunctionTypeId.Value] = $"Fun({intType})-" + $">{boolType}",
                [erasedFunctionTypeId.Value] = $"Fun({erasedTypeVarId})-" + $">{boolType}"
            },
            Functions = [acceptsPredicate, caller]
        });

        var llvmCaller = SingleFunctionBySourceName(llvmModule, "caller_direct_function_arg");
        var entry = Assert.Single(llvmCaller.BasicBlocks);
        var call = Assert.Single(entry.Instructions.OfType<LlvmCall>());
        // Function-typed arguments are passed as opaque ptr (closure objects),
        // not typed function pointers.
        var acceptsPredicateName = SingleFunctionNameBySourceName(llvmModule, "accepts_predicate");
        Assert.Contains($"call void @{acceptsPredicateName}(ptr", call.ToIrString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_ModuleWithNonZeroArgPartialThenIndirectApply_DoesNotReportE5301()
    {
        var genericSymbol = new SymbolId(2213);
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var unitType = new TypeId(BaseTypes.UnitId);

        var first = BuildFunction(
            TypeId.None,
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

        var partialFn = LocalPlace(1, TypeId.None);
        var argA = LocalPlace(2, intType);
        var argB = LocalPlace(3, boolType);
        var result = LocalPlace(4, intType);

        var caller = BuildFunction(
            unitType,
            locals:
            [
                new MirLocal { Id = partialFn.Local, Name = "f", TypeId = TypeId.None },
                new MirLocal { Id = argA.Local, Name = "a", TypeId = intType, IsParameter = true },
                new MirLocal { Id = argB.Local, Name = "b", TypeId = boolType, IsParameter = true },
                new MirLocal { Id = result.Local, Name = "r", TypeId = intType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = partialFn,
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
                    Target = result,
                    Function = partialFn,
                    Arguments = [argB]
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "caller_nonzero_partial",
            symbolId: new SymbolId(2214));

        var converter = new MirToLlvmConverter();
        _ = converter.Convert(new MirModule
        {
            Name = "nonzero_partial_indirect",
            Functions = [first, caller]
        });

        Assert.DoesNotContain(converter.Diagnostics, diagnostic => diagnostic.Code == "E5301");
    }

    [Fact]
    public void Convert_DirectFunctionRefWithFunctionId_ResolvesBeforeAmbiguousSourceName()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var firstSymbol = new SymbolId(3030);
        var secondSymbol = new SymbolId(3031);
        var firstFunctionId = new FunctionId
        {
            SymbolId = firstSymbol,
            Name = "dup",
            QualifiedName = "A::dup"
        };

        var first = BuildFunction(
            intType,
            locals: [],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(1)
            },
            name: "dup",
            symbolId: firstSymbol,
            functionId: firstFunctionId);

        var second = BuildFunction(
            intType,
            locals: [],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(2)
            },
            name: "dup",
            symbolId: secondSymbol,
            functionId: new FunctionId
            {
                SymbolId = secondSymbol,
                Name = "dup",
                QualifiedName = "B::dup"
            });

        var result = LocalPlace(1, intType);
        var caller = BuildFunction(
            unitType,
            locals:
            [
                new MirLocal { Id = result.Local, Name = "result", TypeId = intType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = result,
                    Function = new MirFunctionRef
                    {
                        Name = "dup",
                        SymbolId = SymbolId.None,
                        FunctionId = firstFunctionId,
                        TypeId = intType
                    },
                    Arguments = []
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "caller_function_id_ref",
            symbolId: new SymbolId(3032));

        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "function_id_resolution",
            Functions = [first, second, caller]
        });

        var callerLlvm = SingleFunctionBySourceName(llvmModule, "caller_function_id_ref");
        var call = Assert.Single(callerLlvm.BasicBlocks.Single().Instructions.OfType<LlvmCall>());

        var firstName = llvmModule.Functions[0].Name;
        Assert.Contains($"call i64 @{firstName}(", call.ToIrString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_SameSourceNameAndSignatureWithDifferentFunctionId_UsesDistinctLlvmNames()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var first = BuildFunction(
            intType,
            locals: [],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(1)
            },
            name: "dup",
            symbolId: SymbolId.None,
            functionId: new FunctionId
            {
                Module = "A",
                Name = "dup",
                QualifiedName = "A::dup"
            });
        var second = BuildFunction(
            intType,
            locals: [],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(2)
            },
            name: "dup",
            symbolId: SymbolId.None,
            functionId: new FunctionId
            {
                Module = "B",
                Name = "dup",
                QualifiedName = "B::dup"
            });

        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "same_source_name_different_function_id",
            Functions = [first, second]
        });

        var dupNames = llvmModule.Functions
            .Select(function => function.Name)
            .ToList();

        Assert.Equal(2, dupNames.Count);
        Assert.Equal(2, dupNames.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Convert_DuplicateFunctionIdDefinitions_ReportsDuplicateLlvmGlobalDefinition()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var sharedFunctionId = new FunctionId
        {
            Module = "Synthetic",
            Name = "dup",
            QualifiedName = "synthetic:test:1:lambda:dup"
        };
        var first = BuildFunction(
            intType,
            locals: [],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(1)
            },
            name: "dup_first",
            symbolId: SymbolId.None,
            functionId: sharedFunctionId);
        var second = BuildFunction(
            intType,
            locals: [],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(2)
            },
            name: "dup_second",
            symbolId: SymbolId.None,
            functionId: sharedFunctionId);

        var converter = new MirToLlvmConverter();
        _ = converter.Convert(new MirModule
        {
            Name = "duplicate_function_id_definitions",
            Functions = [first, second]
        });

        Assert.Contains(
            converter.Diagnostics,
            diagnostic => diagnostic.Code == "E5308" &&
                          diagnostic.Message.Contains("LLVM module contains multiple global definitions", StringComparison.Ordinal));
    }

    [Fact]
    public void Convert_DirectFunctionRefWithoutIdentity_DoesNotResolveBySourceName()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var callee = BuildFunction(
            intType,
            locals: [],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(7)
            },
            name: "name_only_target",
            symbolId: new SymbolId(3040));

        var result = LocalPlace(1, intType);
        var caller = BuildFunction(
            unitType,
            locals:
            [
                new MirLocal { Id = result.Local, Name = "result", TypeId = intType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = result,
                    Function = new MirFunctionRef
                    {
                        Name = "name_only_target",
                        SymbolId = SymbolId.None,
                        FunctionId = new FunctionId(),
                        TypeId = intType
                    },
                    Arguments = []
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "caller_name_only_ref",
            symbolId: new SymbolId(3041));

        var converter = new MirToLlvmConverter();
        var llvmModule = converter.Convert(new MirModule
        {
            Name = "name_only_resolution",
            Functions = [callee, caller]
        });

        var callerLlvm = SingleFunctionBySourceName(llvmModule, "caller_name_only_ref");
        var call = Assert.Single(callerLlvm.BasicBlocks.Single().Instructions.OfType<LlvmCall>());

        Assert.Contains("__unresolved_ref__name_only_target", call.ToIrString(), StringComparison.Ordinal);
        Assert.Contains(converter.Diagnostics, diagnostic => diagnostic.Code == "E5304");
    }
}
