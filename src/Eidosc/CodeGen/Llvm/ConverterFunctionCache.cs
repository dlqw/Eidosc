using Eidosc.Symbols;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Types;

namespace Eidosc.CodeGen.Llvm;

/// <summary>
/// Function resolution cache extracted from MirToLlvmConverter (D2/D3).
/// Owns 12 fields for function type/name lookup by symbol, function id,
/// source name, and signature — including ambiguity tracking.
/// </summary>
internal sealed class ConverterFunctionCache
{
    private readonly Dictionary<SymbolId, LlvmFunctionType> _functionTypeBySymbol = new();
    private readonly Dictionary<string, LlvmFunctionType> _functionTypeByFunctionId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LlvmFunctionType> _functionTypeByName = new(StringComparer.Ordinal);
    private readonly Dictionary<SymbolId, MirFunc> _mirFunctionBySymbol = [];
    private readonly Dictionary<string, MirFunc> _mirFunctionByName = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ambiguousMirFunctionNames = new(StringComparer.Ordinal);
    private readonly Dictionary<SymbolId, string> _functionLlvmNameBySymbol = new();
    private readonly Dictionary<string, string> _functionLlvmNameByFunctionId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _functionLlvmNameBySourceName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, LlvmFunctionType>> _functionTypeBySourceAndSignature = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _functionLlvmNameBySourceAndSignature = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ambiguousFunctionSourceNames = new(StringComparer.Ordinal);

    // ── Direct dictionary access (backward compat) ──

    internal Dictionary<SymbolId, LlvmFunctionType> FunctionTypeBySymbol => _functionTypeBySymbol;
    internal Dictionary<string, LlvmFunctionType> FunctionTypeByFunctionId => _functionTypeByFunctionId;
    internal Dictionary<string, LlvmFunctionType> FunctionTypeByName => _functionTypeByName;
    internal Dictionary<SymbolId, MirFunc> MirFunctionBySymbol => _mirFunctionBySymbol;
    internal Dictionary<string, MirFunc> MirFunctionByName => _mirFunctionByName;
    internal HashSet<string> AmbiguousMirFunctionNames => _ambiguousMirFunctionNames;
    internal Dictionary<SymbolId, string> FunctionLlvmNameBySymbol => _functionLlvmNameBySymbol;
    internal Dictionary<string, string> FunctionLlvmNameByFunctionId => _functionLlvmNameByFunctionId;
    internal Dictionary<string, string> FunctionLlvmNameBySourceName => _functionLlvmNameBySourceName;
    internal Dictionary<string, Dictionary<string, LlvmFunctionType>> FunctionTypeBySourceAndSignature => _functionTypeBySourceAndSignature;
    internal Dictionary<string, Dictionary<string, string>> FunctionLlvmNameBySourceAndSignature => _functionLlvmNameBySourceAndSignature;
    internal HashSet<string> AmbiguousFunctionSourceNames => _ambiguousFunctionSourceNames;

    // ── Lifecycle ──

    public void Clear()
    {
        _functionTypeBySymbol.Clear();
        _functionTypeByFunctionId.Clear();
        _functionTypeByName.Clear();
        _mirFunctionBySymbol.Clear();
        _mirFunctionByName.Clear();
        _ambiguousMirFunctionNames.Clear();
        _functionLlvmNameBySymbol.Clear();
        _functionLlvmNameByFunctionId.Clear();
        _functionLlvmNameBySourceName.Clear();
        _functionTypeBySourceAndSignature.Clear();
        _functionLlvmNameBySourceAndSignature.Clear();
        _ambiguousFunctionSourceNames.Clear();
    }
}
