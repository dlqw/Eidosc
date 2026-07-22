using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Symbols;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public sealed partial class MirGenericSpecializerTests
{
    [Fact]
    public void Run_SpecializedWrapperCallingSameNamedGeneric_RewritesToCalleeSpecialization()
    {
        var taskSpawnSymbol = new SymbolId(1301);
        var asyncSpawnSymbol = new SymbolId(1302);
        var callerSymbol = new SymbolId(1303);
        var intType = new TypeId(BaseTypes.IntId);
        var unitType = new TypeId(BaseTypes.UnitId);

        var taskArgument = LocalPlace(1, TypeId.None);
        var taskSpawn = BuildFunction(
            returnType: TypeId.None,
            locals:
            [
                new MirLocal
                {
                    Id = taskArgument.Local,
                    Name = "thunk",
                    TypeId = TypeId.None,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: taskArgument,
            name: "std__Task__spawn_raw",
            symbolId: taskSpawnSymbol,
            functionId: BuildFunctionId(taskSpawnSymbol, "spawn_raw", "Std.Task.spawn_raw"),
            sourceName: "spawn_raw");

        var asyncArgument = LocalPlace(1, TypeId.None);
        var asyncResult = LocalPlace(2, TypeId.None);
        var asyncSpawn = BuildFunction(
            returnType: TypeId.None,
            locals:
            [
                new MirLocal
                {
                    Id = asyncArgument.Local,
                    Name = "thunk",
                    TypeId = TypeId.None,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = asyncResult.Local,
                    Name = "task",
                    TypeId = TypeId.None
                }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = asyncResult,
                    Function = new MirFunctionRef
                    {
                        Name = "std__Task__spawn_raw",
                        SymbolId = taskSpawnSymbol,
                        FunctionId = BuildFunctionId(taskSpawnSymbol, "spawn_raw", "Std.Task.spawn_raw"),
                        TypeId = TypeId.None
                    },
                    Arguments = [asyncArgument]
                }
            ],
            returnValue: asyncResult,
            name: "std__Async__spawn_raw",
            symbolId: asyncSpawnSymbol,
            functionId: BuildFunctionId(asyncSpawnSymbol, "spawn_raw", "Std.Async.spawn_raw"),
            sourceName: "spawn_raw");

        var callerArgument = LocalPlace(1, intType);
        var callerResult = LocalPlace(2, intType);
        var caller = BuildFunction(
            returnType: unitType,
            locals:
            [
                new MirLocal
                {
                    Id = callerArgument.Local,
                    Name = "thunk",
                    TypeId = intType,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = callerResult.Local,
                    Name = "task",
                    TypeId = intType
                }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "std__Async__spawn_raw",
                        SymbolId = asyncSpawnSymbol,
                        FunctionId = BuildFunctionId(asyncSpawnSymbol, "spawn_raw", "Std.Async.spawn_raw"),
                        TypeId = TypeId.None
                    },
                    Arguments = [callerArgument]
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
            Name = "generic_same_named_wrapper",
            Functions = [taskSpawn, asyncSpawn, caller]
        });

        var taskSpecialization = Assert.Single(
            specialized.Functions,
            function => function.Name.StartsWith("std__Task__spawn_raw__spec_", StringComparison.Ordinal));
        var asyncSpecialization = Assert.Single(
            specialized.Functions,
            function => function.Name.StartsWith("std__Async__spawn_raw__spec_", StringComparison.Ordinal));
        var wrappedCall = Assert.Single(
            asyncSpecialization.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var wrappedFunction = Assert.IsType<MirFunctionRef>(wrappedCall.Function);

        Assert.NotEqual(asyncSpecialization.SymbolId, wrappedFunction.SymbolId);
        Assert.Equal(taskSpecialization.SymbolId, wrappedFunction.SymbolId);
        Assert.Equal(taskSpecialization.FunctionId, wrappedFunction.FunctionId);
    }
}
