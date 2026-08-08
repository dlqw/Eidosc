using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Symbols;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public sealed class TraversableConsumerSpecializationPassTests
{
    [Fact]
    public void Run_IdentityConsumerThroughEnvironmentAlias_ClonesAndElidesCallbackCall()
    {
        var valueType = new TypeId(7001);
        var callbackType = new TypeId(7002);
        var input = Local(1, valueType);
        var callback = Local(2, callbackType);
        var environment = Local(3, valueType);
        var environmentAlias = Local(4, valueType);
        var storedCallback = Local(5, callbackType);
        var loadedCallback = Local(6, callbackType);
        var callbackArgument = Local(7, valueType);
        var callbackResult = Local(8, valueType);
        var recursiveInput = Local(9, valueType);
        var recursiveCallback = Local(10, callbackType);
        var recursiveResult = Local(11, valueType);
        var calleeName = "__eidos_prelude_core__Seq__TraversableSeq__traverse__spec_TEST";
        var calleeRef = Function(calleeName);
        var callee = new MirFunc
        {
            Name = calleeName,
            FunctionId = calleeRef.FunctionId,
            ReturnType = valueType,
            Locals =
            [
                Decl(input, "input", isParameter: true),
                Decl(callback, "callback", isParameter: true),
                Decl(environment, "environment"),
                Decl(environmentAlias, "environment_alias"),
                Decl(storedCallback, "stored_callback"),
                Decl(loadedCallback, "loaded_callback"),
                Decl(callbackArgument, "callback_argument"),
                Decl(callbackResult, "callback_result"),
                Decl(recursiveInput, "recursive_input"),
                Decl(recursiveCallback, "recursive_callback"),
                Decl(recursiveResult, "recursive_result")
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirAlloc { Target = environment, TypeId = valueType },
                        new MirMove { Target = storedCallback, Source = callback },
                        new MirStore
                        {
                            Target = Index(environment, 1, callbackType),
                            Value = storedCallback
                        },
                        new MirLoad { Target = environmentAlias, Source = environment },
                        new MirLoad
                        {
                            Target = loadedCallback,
                            Source = Index(environmentAlias, 1, callbackType),
                            MovesOutOfSource = true
                        },
                        new MirCall
                        {
                            Target = callbackResult,
                            Function = loadedCallback,
                            Arguments = [callbackArgument]
                        },
                        new MirMove { Target = recursiveCallback, Source = loadedCallback },
                        new MirCall
                        {
                            Target = recursiveResult,
                            Function = calleeRef,
                            Arguments = [recursiveInput, recursiveCallback]
                        }
                    ],
                    Terminator = new MirReturn { Value = recursiveResult }
                }
            ]
        };

        var sequenceInput = Local(20, valueType);
        var sequenceResult = Local(21, valueType);
        var identityRef = Function("__eidos_prelude_core__Traversable__identity_applicative__spec_TEST");
        var consumer = new MirFunc
        {
            Name = "__eidos_prelude_core__Traversable__sequence__spec_TEST",
            ReturnType = valueType,
            Locals = [Decl(sequenceInput, "input", isParameter: true), Decl(sequenceResult, "result")],
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
                            Target = sequenceResult,
                            Function = calleeRef,
                            Arguments = [sequenceInput, identityRef]
                        }
                    ],
                    Terminator = new MirReturn { Value = sequenceResult }
                }
            ]
        };
        var module = new MirModule { Name = "traversable", Functions = [consumer, callee] };
        var pass = new TraversableConsumerSpecializationPass();

        var result = pass.Run(module);

        Assert.NotSame(module, result);
        var clone = Assert.Single(result.Functions, function => function.Name.EndsWith("__consumer_identity", StringComparison.Ordinal));
        Assert.Contains(
            clone.BasicBlocks.SelectMany(static block => block.Instructions),
            instruction => instruction is MirMove move &&
                           move.Target.Local == callbackResult.Local &&
                           move.Source.Local == callbackArgument.Local);
        Assert.DoesNotContain(
            clone.BasicBlocks.SelectMany(static block => block.Instructions).OfType<MirCall>(),
            static call => call.Function is MirPlace { Kind: PlaceKind.Local });
        Assert.Contains(
            clone.BasicBlocks.SelectMany(static block => block.Instructions).OfType<MirCall>(),
            call => call.Function is MirFunctionRef functionRef &&
                    string.Equals(functionRef.Name, clone.Name, StringComparison.Ordinal));

        var consumerCall = Assert.Single(consumer.BasicBlocks[0].Instructions.OfType<MirCall>());
        var specializedRef = Assert.IsType<MirFunctionRef>(consumerCall.Function);
        Assert.Equal(clone.Name, specializedRef.Name);

        var metrics = pass.GetMetricsSnapshot();
        Assert.Equal(1, metrics["traversable.identity_clones_created"]);
        Assert.Equal(1, metrics["traversable.callback_calls_elided"]);
        Assert.Equal(0, metrics["traversable.fallback.unknown_callback"]);
    }

    [Fact]
    public void Run_EnvironmentSnapshotBeforeCallbackStore_FallsBack()
    {
        var valueType = new TypeId(7051);
        var callbackType = new TypeId(7052);
        var input = Local(1, valueType);
        var callback = Local(2, callbackType);
        var environment = Local(3, valueType);
        var environmentSnapshot = Local(4, valueType);
        var storedCallback = Local(5, callbackType);
        var loadedCallback = Local(6, callbackType);
        var argument = Local(7, valueType);
        var callbackResult = Local(8, valueType);
        var calleeName = "__eidos_prelude_core__Seq__TraversableSeq__traverse__spec_ORDER";
        var calleeRef = Function(calleeName);
        var callee = new MirFunc
        {
            Name = calleeName,
            FunctionId = calleeRef.FunctionId,
            ReturnType = valueType,
            Locals =
            [
                Decl(input, "input", isParameter: true),
                Decl(callback, "callback", isParameter: true),
                Decl(environment, "environment"),
                Decl(environmentSnapshot, "environment_snapshot"),
                Decl(storedCallback, "stored_callback"),
                Decl(loadedCallback, "loaded_callback"),
                Decl(argument, "argument"),
                Decl(callbackResult, "result")
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirAlloc { Target = environment, TypeId = valueType },
                        new MirLoad { Target = environmentSnapshot, Source = environment },
                        new MirMove { Target = storedCallback, Source = callback },
                        new MirStore
                        {
                            Target = Index(environment, 1, callbackType),
                            Value = storedCallback
                        },
                        new MirLoad
                        {
                            Target = loadedCallback,
                            Source = Index(environmentSnapshot, 1, callbackType),
                            MovesOutOfSource = true
                        },
                        new MirCall
                        {
                            Target = callbackResult,
                            Function = loadedCallback,
                            Arguments = [argument]
                        }
                    ],
                    Terminator = new MirReturn { Value = callbackResult }
                }
            ]
        };

        var consumerInput = Local(20, valueType);
        var consumerResult = Local(21, valueType);
        var consumer = new MirFunc
        {
            Name = "__eidos_prelude_core__Traversable__sequence__spec_ORDER",
            ReturnType = valueType,
            Locals =
            [
                Decl(consumerInput, "input", isParameter: true),
                Decl(consumerResult, "result")
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
                            Target = consumerResult,
                            Function = calleeRef,
                            Arguments =
                            [
                                consumerInput,
                                Function("__eidos_prelude_core__Traversable__identity_applicative__spec_ORDER")
                            ]
                        }
                    ],
                    Terminator = new MirReturn { Value = consumerResult }
                }
            ]
        };
        var module = new MirModule { Name = "ordering", Functions = [consumer, callee] };
        var pass = new TraversableConsumerSpecializationPass();

        var result = pass.Run(module);

        Assert.Same(module, result);
        Assert.DoesNotContain(result.Functions, static function => function.Name.Contains("__consumer_identity", StringComparison.Ordinal));
        var preservedCall = Assert.Single(consumer.BasicBlocks[0].Instructions.OfType<MirCall>());
        Assert.Equal(calleeName, Assert.IsType<MirFunctionRef>(preservedCall.Function).Name);
        var metrics = pass.GetMetricsSnapshot();
        Assert.Equal(1, metrics["traversable.fallback.unknown_callback"]);
        Assert.Equal(0, metrics["traversable.callback_calls_elided"]);
    }

    [Fact]
    public void Run_EscapedConsumerCallback_PreservesOriginalCall()
    {
        var valueType = new TypeId(7101);
        var callbackType = new TypeId(7102);
        var calleeRef = Function("__eidos_prelude_core__Seq__TraversableSeq__traverse__spec_FALLBACK");
        var callee = new MirFunc
        {
            Name = calleeRef.Name,
            FunctionId = calleeRef.FunctionId,
            ReturnType = valueType,
            Locals =
            [
                Decl(Local(1, valueType), "input", isParameter: true),
                Decl(Local(2, callbackType), "callback", isParameter: true)
            ]
        };
        var input = Local(10, valueType);
        var escapedCallback = Local(11, callbackType);
        var resultPlace = Local(12, valueType);
        var call = new MirCall
        {
            Target = resultPlace,
            Function = calleeRef,
            Arguments = [input, escapedCallback]
        };
        var consumer = new MirFunc
        {
            Name = "__eidos_prelude_core__Traversable__sequence__spec_FALLBACK",
            ReturnType = valueType,
            Locals =
            [
                Decl(input, "input", isParameter: true),
                Decl(escapedCallback, "callback", isParameter: true),
                Decl(resultPlace, "result")
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions = [call],
                    Terminator = new MirReturn { Value = resultPlace }
                }
            ]
        };
        var module = new MirModule { Name = "fallback", Functions = [consumer, callee] };
        var pass = new TraversableConsumerSpecializationPass();

        var result = pass.Run(module);

        Assert.Same(module, result);
        Assert.Same(call, consumer.BasicBlocks[0].Instructions[0]);
        Assert.DoesNotContain(result.Functions, static function => function.Name.Contains("__consumer_identity", StringComparison.Ordinal));
        var metrics = pass.GetMetricsSnapshot();
        Assert.Equal(1, metrics["traversable.fallback.escaped_callback"]);
        Assert.Equal(0, metrics["traversable.identity_clones_created"]);
    }

    private static MirFunctionRef Function(string name) => new()
    {
        Name = name,
        SymbolKind = SymbolKind.Function,
        FunctionId = new FunctionId
        {
            Name = name,
            QualifiedName = $"test:{name}",
            Module = "test",
            Kind = SymbolKind.Function
        }
    };

    private static MirPlace Local(int id, TypeId type) => new()
    {
        Kind = PlaceKind.Local,
        Local = new LocalId { Value = id },
        TypeId = type
    };

    private static MirPlace Index(MirPlace root, long index, TypeId type) => new()
    {
        Kind = PlaceKind.Index,
        Base = root,
        Index = new MirConstant
        {
            Value = new MirConstantValue.IntValue(index),
            TypeId = new TypeId(BaseTypes.IntId)
        },
        TypeId = type
    };

    private static MirLocal Decl(MirPlace place, string name, bool isParameter = false) => new()
    {
        Id = place.Local,
        Name = name,
        TypeId = place.TypeId,
        IsParameter = isParameter
    };
}
