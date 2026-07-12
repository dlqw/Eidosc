using System.Security.Cryptography;
using System.Text;

namespace Eidosc.Mir.Optimize;

public sealed partial class MirGenericSpecializer
{
    private string CreateSpecializationName(string templateName, string signatureKey)
    {
        var normalizedBase = string.IsNullOrWhiteSpace(templateName) ? "generic" : templateName;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(signatureKey));
        var hashFragment = System.Convert.ToHexString(hash.AsSpan(0, 6));
        var candidate = $"{normalizedBase}{WellKnownStrings.InternalNames.SpecializationMarker}{hashFragment}";
        while (_usedFunctionNames.Contains(candidate))
        {
            candidate = $"{normalizedBase}{WellKnownStrings.InternalNames.SpecializationMarker}{hashFragment}_{_nextSpecializationNameOrdinal++}";
        }

        _usedFunctionNames.Add(candidate);
        return candidate;
    }

    private bool TryResolveTemplateKey(MirFunctionRef functionRef, out string templateKey)
    {
        templateKey = string.Empty;

        var symbolHasTemplateKey = false;
        if (functionRef.SymbolId.IsValid &&
            _templateRegistry.KeyBySymbolDict.TryGetValue(functionRef.SymbolId, out var keyBySymbol))
        {
            if (FunctionRefNameMatchesTemplate(functionRef, keyBySymbol))
            {
                symbolHasTemplateKey = true;
                templateKey = keyBySymbol;
                return true;
            }

            return false;
        }

        // 如果 functionRef 有有效的 SymbolId 但不在 _templateRegistry.KeyBySymbolDict 中，
        // 已知非模板函数必须保持非模板身份。未知符号可能来自跨模块/
        // 预编译引用，只能按稳定 FunctionId 解析，不能按名字误配本模块模板。
        if (functionRef.SymbolId.IsValid && !symbolHasTemplateKey)
        {
            if (ResolvesToKnownNonTemplateMirFunction(functionRef.SymbolId))
            {
                return false;
            }

            return TryResolveTemplateKeyByFunctionIdentity(functionRef.FunctionId, out templateKey);
        }

        if (TryResolveTemplateKeyByFunctionIdentity(functionRef.FunctionId, out templateKey))
        {
            return true;
        }

        return TryResolveTemplateKeyByName(functionRef.Name, out templateKey);
    }

    private bool FunctionRefNameMatchesTemplate(MirFunctionRef functionRef, string templateKey)
    {
        if (string.IsNullOrWhiteSpace(functionRef.Name) ||
            !_templateRegistry.ByKeyDict.TryGetValue(templateKey, out var template))
        {
            return true;
        }

        return string.Equals(functionRef.Name, template.TemplateSource.Name, StringComparison.Ordinal) ||
               TemplateAlternateNameMatches(template.TemplateSource.Name, functionRef.Name);
    }

    private bool ResolvesToKnownNonTemplateMirFunction(SymbolId symbolId)
    {
        return symbolId.IsValid &&
               _functionBySymbol.TryGetValue(symbolId, out var function) &&
               !IsGenericTemplateCandidate(function);
    }

    private bool TryResolveTemplateKeyByFunctionIdentity(FunctionId? functionId, out string templateKey)
    {
        templateKey = string.Empty;
        if (TryGetTemplateFunctionIdentityKey(functionId, out var identityKey) &&
            _templateRegistry.KeyByFunctionIdentityDict.TryGetValue(identityKey, out var resolvedTemplateKey))
        {
            templateKey = resolvedTemplateKey;
            return true;
        }

        return TryGetTemplateFunctionIdentityFallbackKey(functionId, out var fallbackIdentityKey) &&
               _templateRegistry.KeyByFunctionIdentityDict.TryGetValue(fallbackIdentityKey, out resolvedTemplateKey) &&
               (templateKey = resolvedTemplateKey) != null;
    }

    private bool TryResolveTemplateKeyByName(string? functionName, out string templateKey)
    {
        templateKey = string.Empty;

        if (string.IsNullOrWhiteSpace(functionName))
        {
            return false;
        }

        if (_templateRegistry.KeyByUniqueNameDict.TryGetValue(functionName, out var keyByName))
        {
            templateKey = keyByName;
            return true;
        }

        if (_templateRegistry.KeyByAlternateNameDict.TryGetValue(functionName, out var keyByAlternateName))
        {
            templateKey = keyByAlternateName;
            return true;
        }

        return false;
    }

    private bool TryResolveTemplateKey(MirFunc function, out string templateKey)
    {
        templateKey = string.Empty;

        if (function.SymbolId.IsValid &&
            _templateRegistry.KeyBySymbolDict.TryGetValue(function.SymbolId, out var keyBySymbol))
        {
            templateKey = keyBySymbol;
            return true;
        }

        var functionName = function.Name;
        return !string.IsNullOrWhiteSpace(functionName) &&
               _templateRegistry.KeyByUniqueNameDict.TryGetValue(functionName, out var resolvedTemplateKey) &&
               (templateKey = resolvedTemplateKey) != null;
    }
}
