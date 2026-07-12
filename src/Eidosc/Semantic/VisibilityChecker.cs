using Eidosc.Symbols;

namespace Eidosc.Semantic;

/// <summary>
/// 可见性检查器
/// </summary>
public sealed class VisibilityChecker
{
    private readonly SymbolTable _symbolTable;
    private readonly ModuleRegistry _moduleRegistry;

    public VisibilityChecker(SymbolTable symbolTable)
    {
        _symbolTable = symbolTable;
        _moduleRegistry = symbolTable.Modules;
    }

    /// <summary>
    /// 检查符号是否对当前模块可见
    /// </summary>
    public bool IsVisible(SymbolId symbolId, SymbolId currentModule)
    {
        var symbol = _symbolTable.GetSymbol(symbolId);
        if (symbol == null) return false;

        // 公开符号始终可见
        if (symbol.IsPublic)
            return true;

        // 检查是否在同一模块
        return IsInSameModule(symbol, currentModule);
    }

    /// <summary>
    /// 检查符号是否在同一模块
    /// </summary>
    private bool IsInSameModule(Symbol symbol, SymbolId currentModule)
    {
        // 获取符号所属模块
        var symbolModule = GetOwnerModule(symbol);
        if (symbolModule == null)
            return false;

        return symbolModule.Value == currentModule;
    }

    /// <summary>
    /// 获取符号所属的模块
    /// </summary>
    private SymbolId? GetOwnerModule(Symbol symbol)
    {
        // 如果符号本身就是模块级别的，检查它是否是模块
        if (symbol is ModuleSymbol moduleSymbol)
        {
            // 查找模块 ID
            return _moduleRegistry.LookupModuleByPath(moduleSymbol.PackageAlias, moduleSymbol.Path);
        }

        // 遍历所有模块，查找包含该符号的模块
        foreach (var (path, moduleId) in _moduleRegistry.ModulePaths)
        {
            var mod = _moduleRegistry.GetModule(moduleId);
            if (mod != null && mod.Members.Contains(symbol.Id))
            {
                return moduleId;
            }
        }

        return null;
    }
}
