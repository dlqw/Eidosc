using Eidosc.Borrow;
using Eidosc.Mir;

namespace Eidosc.Pipeline;

internal sealed class BorrowSnapshotFunctionIdentity
{
    private readonly IReadOnlyDictionary<string, string> _bodyHashByFunctionKey;
    private readonly IReadOnlyDictionary<string, string> _bodyHashBySymbol;
    private readonly IReadOnlyDictionary<string, string> _bodyHashByName;
    private readonly IReadOnlyDictionary<string, string> _functionKeyBySymbol;
    private readonly IReadOnlyDictionary<string, string> _functionKeyByName;

    private BorrowSnapshotFunctionIdentity(
        IReadOnlyDictionary<string, string> bodyHashByFunctionKey,
        IReadOnlyDictionary<string, string> bodyHashBySymbol,
        IReadOnlyDictionary<string, string> bodyHashByName,
        IReadOnlyDictionary<string, string> functionKeyBySymbol,
        IReadOnlyDictionary<string, string> functionKeyByName)
    {
        _bodyHashByFunctionKey = bodyHashByFunctionKey;
        _bodyHashBySymbol = bodyHashBySymbol;
        _bodyHashByName = bodyHashByName;
        _functionKeyBySymbol = functionKeyBySymbol;
        _functionKeyByName = functionKeyByName;
    }

    public static BorrowSnapshotFunctionIdentity Create(MirFunctionFingerprintSnapshot mirFingerprints)
    {
        return new BorrowSnapshotFunctionIdentity(
            BuildBodyHashByFullKey(mirFingerprints),
            BuildBodyHashByPrefix(mirFingerprints, "sym:"),
            BuildBodyHashByPrefix(mirFingerprints, "name:"),
            BuildFunctionKeyBySymbol(mirFingerprints),
            BuildFunctionKeyByName(mirFingerprints));
    }

    public string ResolveFunctionKey(BorrowCheckResult result, BorrowFunctionKey fallbackKey)
    {
        return ResolveFunctionKey(result.FunctionName, result.FunctionSymbolId, fallbackKey.StableText);
    }

    public string ResolveFunctionKey(MirFunc function)
    {
        var fallbackKey = MirFunctionIdentity.GetStableKey(function);
        if (_bodyHashByFunctionKey.ContainsKey(fallbackKey))
        {
            return fallbackKey;
        }

        return ResolveFunctionKey(function.Name, function.SymbolId, fallbackKey);
    }

    private string ResolveFunctionKey(string functionName, SymbolId functionSymbolId, string fallbackKey)
    {
        var symbolKey = ResolveSymbolKey(functionSymbolId);
        if (symbolKey != null &&
            _functionKeyBySymbol.TryGetValue(symbolKey, out var fullSymbolKey))
        {
            return fullSymbolKey;
        }

        var symbolStableKey = symbolKey == null ? null : $"sym:{symbolKey}";
        if (symbolStableKey != null && _bodyHashByFunctionKey.ContainsKey(symbolStableKey))
        {
            return symbolStableKey;
        }

        if (!string.IsNullOrWhiteSpace(functionName) &&
            _functionKeyByName.TryGetValue(functionName, out var fullNameKey))
        {
            return fullNameKey;
        }

        if (!string.IsNullOrWhiteSpace(functionName) &&
            _bodyHashByName.ContainsKey(functionName))
        {
            return $"name:{functionName}";
        }

        return fallbackKey;
    }

    public string? ResolveBodyHash(BorrowCheckResult result, string stableKey)
    {
        if (_bodyHashByFunctionKey.TryGetValue(stableKey, out var bodyHash))
        {
            return bodyHash;
        }

        if (!string.IsNullOrWhiteSpace(result.FunctionName) &&
            _functionKeyByName.TryGetValue(result.FunctionName, out var fullNameKey) &&
            _bodyHashByFunctionKey.TryGetValue(fullNameKey, out bodyHash))
        {
            return bodyHash;
        }

        if (!string.IsNullOrWhiteSpace(result.FunctionName) &&
            _bodyHashByName.TryGetValue(result.FunctionName, out bodyHash))
        {
            return bodyHash;
        }

        var symbolKey = ResolveSymbolKey(result.FunctionSymbolId);
        if (symbolKey != null &&
            _bodyHashBySymbol.TryGetValue(symbolKey, out bodyHash))
        {
            return bodyHash;
        }

        return null;
    }

    private static string? ResolveSymbolKey(SymbolId symbolId) =>
        symbolId.IsValid
            ? symbolId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;

    private static Dictionary<string, string> BuildFunctionKeyBySymbol(MirFunctionFingerprintSnapshot mirFingerprints)
    {
        return mirFingerprints.Functions
            .Where(static fingerprint => fingerprint.FunctionKey.StartsWith("sym:", StringComparison.Ordinal))
            .Select(static fingerprint => new
            {
                SymbolKey = ExtractSymbolKey(fingerprint.FunctionKey),
                fingerprint.FunctionKey
            })
            .Where(static item => !string.IsNullOrWhiteSpace(item.SymbolKey))
            .GroupBy(static item => item.SymbolKey!, StringComparer.Ordinal)
            .Where(static group => group.Count() == 1)
            .ToDictionary(
                static group => group.Key,
                static group => group.First().FunctionKey,
                StringComparer.Ordinal);
    }

    private static Dictionary<string, string> BuildFunctionKeyByName(MirFunctionFingerprintSnapshot mirFingerprints)
    {
        return mirFingerprints.Functions
            .Select(static fingerprint => new
            {
                FunctionName = ExtractFunctionName(fingerprint.FunctionKey),
                fingerprint.FunctionKey
            })
            .Where(static item => !string.IsNullOrWhiteSpace(item.FunctionName))
            .GroupBy(static item => item.FunctionName!, StringComparer.Ordinal)
            .Where(static group => group.Count() == 1)
            .ToDictionary(
                static group => group.Key,
                static group => group.First().FunctionKey,
                StringComparer.Ordinal);
    }

    private static Dictionary<string, string> BuildBodyHashByFullKey(MirFunctionFingerprintSnapshot mirFingerprints)
    {
        return mirFingerprints.Functions
            .GroupBy(static fingerprint => fingerprint.FunctionKey, StringComparer.Ordinal)
            .Where(static group => group.Count() == 1)
            .ToDictionary(static group => group.Key, static group => group.First().BodyHash, StringComparer.Ordinal);
    }

    private static Dictionary<string, string> BuildBodyHashByPrefix(
        MirFunctionFingerprintSnapshot mirFingerprints,
        string prefix)
    {
        return mirFingerprints.Functions
            .Where(fingerprint => fingerprint.FunctionKey.StartsWith(prefix, StringComparison.Ordinal))
            .GroupBy(fingerprint => ExtractPrefixedIdentity(fingerprint.FunctionKey, prefix), StringComparer.Ordinal)
            .Where(static group => group.Count() == 1)
            .ToDictionary(static group => group.Key, static group => group.First().BodyHash, StringComparer.Ordinal);
    }

    private static string ExtractPrefixedIdentity(string functionKey, string prefix)
    {
        var identity = functionKey[prefix.Length..];
        return string.Equals(prefix, "sym:", StringComparison.Ordinal)
            ? ExtractSymbolKey(functionKey) ?? identity
            : identity;
    }

    private static string? ExtractSymbolKey(string functionKey)
    {
        if (!functionKey.StartsWith("sym:", StringComparison.Ordinal))
        {
            return null;
        }

        var identity = functionKey["sym:".Length..];
        var separatorIndex = identity.LastIndexOf("::", StringComparison.Ordinal);
        return separatorIndex >= 0
            ? identity[(separatorIndex + 2)..]
            : identity;
    }

    private static string? ExtractFunctionName(string functionKey)
    {
        if (functionKey.StartsWith("name:", StringComparison.Ordinal))
        {
            return functionKey["name:".Length..];
        }

        if (!functionKey.StartsWith("stable:", StringComparison.Ordinal))
        {
            return null;
        }

        var fields = functionKey["stable:".Length..].Split('\0');
        return fields.Length >= 3 ? fields[2] : null;
    }

}
