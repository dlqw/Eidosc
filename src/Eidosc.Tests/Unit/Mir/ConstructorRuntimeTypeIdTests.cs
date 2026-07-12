using Eidosc.Mir;
using Eidosc.Symbols;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public sealed class ConstructorRuntimeTypeIdTests
{
    [Fact]
    public void Compute_ModuleAwareConstructor_IgnoresSessionSymbolIds()
    {
        var first = CreateModuleSymbols(1001, 1002, 1003, "Domain", "Result", "Ok");
        var second = CreateModuleSymbols(2001, 2002, 2003, "Domain", "Result", "Ok");

        var firstRuntimeTypeId = ConstructorRuntimeTypeId.Compute(first.SymbolTable, first.ConstructorId, "Ok");
        var secondRuntimeTypeId = ConstructorRuntimeTypeId.Compute(second.SymbolTable, second.ConstructorId, "Ok");

        Assert.Equal(firstRuntimeTypeId, secondRuntimeTypeId);
    }

    [Fact]
    public void Compute_ModuleAwareConstructor_DistinguishesOwnerAdtAndModule()
    {
        var baseline = CreateModuleSymbols(1001, 1002, 1003, "Domain", "Result", "Same");
        var differentAdt = CreateModuleSymbols(2001, 2002, 2003, "Domain", "Option", "Same");
        var differentModule = CreateModuleSymbols(3001, 3002, 3003, "Other", "Result", "Same");

        var baselineId = ConstructorRuntimeTypeId.Compute(baseline.SymbolTable, baseline.ConstructorId, "Same");
        var differentAdtId = ConstructorRuntimeTypeId.Compute(differentAdt.SymbolTable, differentAdt.ConstructorId, "Same");
        var differentModuleId = ConstructorRuntimeTypeId.Compute(differentModule.SymbolTable, differentModule.ConstructorId, "Same");

        Assert.NotEqual(baselineId, differentAdtId);
        Assert.NotEqual(baselineId, differentModuleId);
    }

    [Fact]
    public void Compute_WithoutModuleIdentity_IgnoresSessionSymbolId()
    {
        var first = ConstructorRuntimeTypeId.Compute(null, new SymbolId(101), "Ok");
        var second = ConstructorRuntimeTypeId.Compute(null, new SymbolId(202), "Ok");

        Assert.Equal(first, second);
    }

    private static (SymbolTable SymbolTable, SymbolId ConstructorId) CreateModuleSymbols(
        int moduleIdValue,
        int adtIdValue,
        int constructorIdValue,
        string moduleName,
        string adtName,
        string constructorName)
    {
        var symbolTable = new SymbolTable();
        var moduleId = new SymbolId(moduleIdValue);
        var adtId = new SymbolId(adtIdValue);
        var constructorId = new SymbolId(constructorIdValue);
        var module = new ModuleSymbol
        {
            Id = moduleId,
            Name = moduleName,
            Path = [moduleName],
            Members = [adtId]
        };

        symbolTable.RegisterSymbol(module);
        symbolTable.RegisterSymbol(new AdtSymbol
        {
            Id = adtId,
            Name = adtName,
            Constructors = [constructorId]
        });
        symbolTable.RegisterSymbol(new CtorSymbol
        {
            Id = constructorId,
            Name = constructorName,
            OwnerAdt = adtId
        });
        symbolTable.Modules.RegisterModule(module, moduleId);

        return (symbolTable, constructorId);
    }
}
