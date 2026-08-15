using Eidosc.Hir;
using Eidosc.Mir;
using Eidosc.Pipeline;
using Eidosc.Symbols;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public partial class MirBuilderTests
{
    [Fact]
    public void Build_ModuleLevelMutableVar_RegistersModuleVarAndLowersLoadStore()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var counterSymbol = new SymbolId(3100);
        var incSymbol = new SymbolId(3101);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirVarDecl
                {
                    Name = "counter",
                    SymbolId = counterSymbol,
                    IsModuleLevel = true,
                    TypeId = intType,
                    Pattern = new HirVarPattern
                    {
                        Name = "counter",
                        SymbolId = counterSymbol,
                        TypeId = intType,
                        IsMutableBinding = true
                    },
                    Initializer = new HirLiteral
                    {
                        LiteralKind = LiteralKind.Int,
                        Value = 0L,
                        TypeId = intType
                    }
                },
                new HirFunc
                {
                    Name = "inc",
                    SymbolId = incSymbol,
                    ReturnType = intType,
                    Body = new HirBlock
                    {
                        Statements =
                        [
                            new HirAssignStatement
                            {
                                Target = new HirVar
                                {
                                    Name = "counter",
                                    SymbolId = counterSymbol,
                                    TypeId = intType
                                },
                                Value = new HirBinOp
                                {
                                    Operator = Eidosc.Hir.BinaryOp.Add,
                                    Left = new HirVar
                                    {
                                        Name = "counter",
                                        SymbolId = counterSymbol,
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
                        ],
                        Result = new HirVar
                        {
                            Name = "counter",
                            SymbolId = counterSymbol,
                            TypeId = intType
                        }
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);

        Assert.Empty(builder.Diagnostics);
        var moduleVar = Assert.Single(mirModule.ModuleVars);
        Assert.Equal("counter", moduleVar.Name);
        Assert.Equal(counterSymbol, moduleVar.SymbolId);
        Assert.Equal(intType, moduleVar.TypeId);
        Assert.True(moduleVar.IsMutable);
        var initializer = Assert.IsType<MirConstant>(moduleVar.Initializer);
        Assert.Equal(0L, Assert.IsType<MirConstantValue.IntValue>(initializer.Value).Value);

        var func = Assert.Single(mirModule.Functions, function => function.Name == "inc");
        var instructions = func.BasicBlocks.SelectMany(static block => block.Instructions).ToList();

        var moduleVarLoads = instructions.OfType<MirLoad>().Where(instruction =>
            instruction.Source is MirPlace { Kind: PlaceKind.ModuleVar } loadPlace &&
            loadPlace.ModuleVarName == "counter").ToList();
        var load = Assert.Single(moduleVarLoads);
        Assert.Equal(counterSymbol, Assert.IsType<MirPlace>(load.Source).ModuleVarSymbol);

        var moduleVarStores = instructions.OfType<MirStore>().Where(instruction =>
            instruction.Target is MirPlace { Kind: PlaceKind.ModuleVar } storePlace &&
            storePlace.ModuleVarName == "counter").ToList();
        var store = Assert.Single(moduleVarStores);
        Assert.Equal(counterSymbol, store.Target.ModuleVarSymbol);

        Assert.True(new MirValidator().Validate(mirModule));
    }

    [Fact]
    public void Build_ModuleLevelMutableVar_NonConstantInitializer_RegistersRuntimeInitFunction()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var counterSymbol = new SymbolId(3110);
        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirVarDecl
                {
                    Name = "counter",
                    SymbolId = counterSymbol,
                    IsModuleLevel = true,
                    TypeId = intType,
                    Pattern = new HirVarPattern
                    {
                        Name = "counter",
                        SymbolId = counterSymbol,
                        TypeId = intType,
                        IsMutableBinding = true
                    },
                    Initializer = new HirCall
                    {
                        Function = new HirVar
                        {
                            Name = "make_counter",
                            SymbolId = new SymbolId(3111),
                            TypeId = intType
                        },
                        TypeId = intType
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);

        // 调用初始化器不再报 E5312，而是登记运行时初始化函数。
        Assert.DoesNotContain(builder.Diagnostics, diagnostic => diagnostic.Code == "E5312");
        var moduleVar = Assert.Single(mirModule.ModuleVars);
        Assert.StartsWith("__module_var_init_", moduleVar.RuntimeInitializerName, StringComparison.Ordinal);
        Assert.Equal(1, moduleVar.RuntimeInitOrder);
        Assert.IsType<MirPoison>(moduleVar.Initializer);
        Assert.Contains(
            mirModule.Functions,
            function => function.Name == moduleVar.RuntimeInitializerName && function.ReturnType == intType);
    }

    [Fact]
    public void Build_ModuleLevelMutableVars_CyclicRuntimeInitializers_ReportCycleAndSkipInit()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var aSymbol = new SymbolId(3121);
        var bSymbol = new SymbolId(3122);
        HirVarDecl VarDecl(string name, SymbolId symbol, SymbolId referenced) => new()
        {
            Name = name,
            SymbolId = symbol,
            IsModuleLevel = true,
            TypeId = intType,
            Pattern = new HirVarPattern
            {
                Name = name,
                SymbolId = symbol,
                TypeId = intType,
                IsMutableBinding = true
            },
            Initializer = new HirBinOp
            {
                Operator = Eidosc.Hir.BinaryOp.Add,
                Left = new HirVar { Name = "other", SymbolId = referenced, TypeId = intType },
                Right = new HirLiteral { LiteralKind = LiteralKind.Int, Value = 1L, TypeId = intType },
                TypeId = intType
            }
        };

        var module = new HirModule
        {
            Name = "Main",
            Declarations = [VarDecl("a", aSymbol, bSymbol), VarDecl("b", bSymbol, aSymbol)]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);

        Assert.Contains(builder.Diagnostics, diagnostic => diagnostic.Code == "E5300");
        Assert.All(mirModule.ModuleVars, moduleVar => Assert.Null(moduleVar.RuntimeInitializerName));
    }

    [Fact]
    public void ModuleMirStatePayload_RoundTripsModuleVars()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var counterSymbol = new SymbolId(3120);
        var module = new MirModule
        {
            Name = "Main",
            ModuleVars =
            [
                new MirModuleVar
                {
                    Name = "counter",
                    SymbolId = counterSymbol,
                    TypeId = intType,
                    IsMutable = true,
                    Initializer = new MirConstant
                    {
                        Value = new MirConstantValue.IntValue(7L),
                        TypeId = intType
                    }
                }
            ]
        };

        var payload = ModuleMirStatePayload.Create(module);
        Assert.True(payload.IsRestorable, string.Join(Environment.NewLine, payload.UnsupportedNodeKinds));
        Assert.True(payload.TryRestore(out var restored));

        var restoredVar = Assert.Single(restored.ModuleVars);
        Assert.Equal("counter", restoredVar.Name);
        Assert.Equal(counterSymbol, restoredVar.SymbolId);
        Assert.Equal(intType, restoredVar.TypeId);
        var restoredInitializer = Assert.IsType<MirConstant>(restoredVar.Initializer);
        Assert.Equal(7L, Assert.IsType<MirConstantValue.IntValue>(restoredInitializer.Value).Value);
    }

    [Fact]
    public void ModuleMirStatePayload_RoundTripsRuntimeInitModuleVar()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var counterSymbol = new SymbolId(3125);
        var module = new MirModule
        {
            Name = "Main",
            ModuleVars =
            [
                new MirModuleVar
                {
                    Name = "counter",
                    SymbolId = counterSymbol,
                    TypeId = intType,
                    IsMutable = true,
                    Initializer = new MirPoison { TypeId = intType, Reason = "runtime" },
                    RuntimeInitializerName = "__module_var_init_counter",
                    RuntimeInitOrder = 3
                }
            ]
        };

        var payload = ModuleMirStatePayload.Create(module);
        Assert.True(payload.TryRestore(out var restored));

        var restoredVar = Assert.Single(restored.ModuleVars);
        Assert.Equal("__module_var_init_counter", restoredVar.RuntimeInitializerName);
        Assert.Equal(3, restoredVar.RuntimeInitOrder);
    }
}
