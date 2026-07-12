using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

/// <summary>
/// Template lookup registry extracted from MirGenericSpecializer (D2/D3).
/// Owns 7 fields for template key resolution by symbol, unique name,
/// alternate name, and function identity — with ambiguity tracking.
/// </summary>
internal sealed class SpecializerTemplateRegistry
{
    private readonly Dictionary<string, MirGenericSpecializer.TemplateInfo> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<SymbolId, string> _keyBySymbol = [];
    private readonly Dictionary<string, string> _keyByUniqueName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _keyByAlternateName = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ambiguousAlternateNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _keyByFunctionIdentity = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ambiguousFunctionIdentityKeys = new(StringComparer.Ordinal);

    // ── Properties ──

    public int Count => _byKey.Count;

    // Direct dictionary access for backward compatibility with partials
    internal Dictionary<string, MirGenericSpecializer.TemplateInfo> ByKeyDict => _byKey;
    internal Dictionary<SymbolId, string> KeyBySymbolDict => _keyBySymbol;
    internal Dictionary<string, string> KeyByUniqueNameDict => _keyByUniqueName;
    internal Dictionary<string, string> KeyByAlternateNameDict => _keyByAlternateName;
    internal HashSet<string> AmbiguousAlternateNamesSet => _ambiguousAlternateNames;
    internal Dictionary<string, string> KeyByFunctionIdentityDict => _keyByFunctionIdentity;
    internal HashSet<string> AmbiguousFunctionIdentityKeysSet => _ambiguousFunctionIdentityKeys;

    // ── Registration ──

    public void Register(
        SymbolId symbolId,
        string uniqueName,
        string templateKey,
        MirGenericSpecializer.TemplateInfo info)
    {
        _byKey[templateKey] = info;
        _keyBySymbol[symbolId] = templateKey;
        _keyByUniqueName[uniqueName] = templateKey;
    }

    public void RegisterAlternateName(string alternateName, string templateKey)
    {
        if (_keyByAlternateName.TryGetValue(alternateName, out var existing) &&
            existing != templateKey)
        {
            _keyByAlternateName.Remove(alternateName);
            _ambiguousAlternateNames.Add(alternateName);
            return;
        }
        if (_ambiguousAlternateNames.Contains(alternateName))
            return;
        _keyByAlternateName[alternateName] = templateKey;
    }

    public void RegisterFunctionIdentity(string identityKey, string templateKey)
    {
        if (_ambiguousFunctionIdentityKeys.Contains(identityKey))
            return;

        if (_keyByFunctionIdentity.TryGetValue(identityKey, out var existing) &&
            existing != templateKey)
        {
            _keyByFunctionIdentity.Remove(identityKey);
            _ambiguousFunctionIdentityKeys.Add(identityKey);
            return;
        }
        _keyByFunctionIdentity[identityKey] = templateKey;
    }

    // ── Lookup ──

    public bool TryGetTemplate(string templateKey, out MirGenericSpecializer.TemplateInfo info)
        => _byKey.TryGetValue(templateKey, out info!);

    public bool TryGetKeyBySymbol(SymbolId symbolId, out string key)
        => _keyBySymbol.TryGetValue(symbolId, out key!);

    public bool TryGetKeyByUniqueName(string name, out string key)
        => _keyByUniqueName.TryGetValue(name, out key!);

    public bool TryGetKeyByAlternateName(string name, out string key)
        => _keyByAlternateName.TryGetValue(name, out key!);

    public bool TryGetKeyByFunctionIdentity(string identity, out string key)
        => _keyByFunctionIdentity.TryGetValue(identity, out key!);

    public bool IsAmbiguousAlternateName(string name)
        => _ambiguousAlternateNames.Contains(name);

    public bool IsAmbiguousFunctionIdentity(string identity)
        => _ambiguousFunctionIdentityKeys.Contains(identity);

    // ── Lifecycle ──

    public void Clear()
    {
        _byKey.Clear();
        _keyBySymbol.Clear();
        _keyByUniqueName.Clear();
        _keyByAlternateName.Clear();
        _ambiguousAlternateNames.Clear();
        _keyByFunctionIdentity.Clear();
        _ambiguousFunctionIdentityKeys.Clear();
    }

    public IEnumerable<KeyValuePair<string, MirGenericSpecializer.TemplateInfo>> EnumerateTemplates()
        => _byKey;
}
