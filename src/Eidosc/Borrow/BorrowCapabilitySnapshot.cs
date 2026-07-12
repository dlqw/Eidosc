using Eidosc.Symbols;
using System.Text;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Types;

namespace Eidosc.Borrow;

public enum BorrowCapabilityKind
{
    Read,
    Write,
    Move
}

/// <summary>
/// 借用阶段可消费的能力快照。
/// 默认兼容策略：未启用（IsEnforced=false）时全部放行。
/// </summary>
public sealed class BorrowCapabilitySnapshot
{
    private readonly HashSet<BorrowCapabilityKind> _globalCapabilities;
    private readonly Dictionary<LocalId, HashSet<BorrowCapabilityKind>> _localCapabilities;
    private readonly Dictionary<string, HashSet<BorrowCapabilityKind>> _targetCapabilities;
    private readonly Dictionary<string, HashSet<BorrowCapabilityKind>> _capabilityProviders;

    public bool IsEnforced { get; }

    public IReadOnlyCollection<BorrowCapabilityKind> GlobalCapabilities => _globalCapabilities;

    public IEnumerable<(LocalId Local, IReadOnlyList<BorrowCapabilityKind> Capabilities)> EnumerateLocalCapabilityGrants()
    {
        foreach (var entry in _localCapabilities.OrderBy(entry => entry.Key.Value))
        {
            yield return (
                entry.Key,
                [.. entry.Value.OrderBy(capability => capability.ToString(), StringComparer.Ordinal)]);
        }
    }

    public IEnumerable<(string TargetKey, IReadOnlyList<BorrowCapabilityKind> Capabilities)> EnumerateTargetCapabilityGrants()
    {
        foreach (var entry in _targetCapabilities.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            yield return (
                entry.Key,
                [.. entry.Value.OrderBy(capability => capability.ToString(), StringComparer.Ordinal)]);
        }
    }

    public IEnumerable<(string Provider, IReadOnlyList<BorrowCapabilityKind> Capabilities)> EnumerateCapabilityProviders()
    {
        foreach (var entry in _capabilityProviders.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            yield return (
                entry.Key,
                [.. entry.Value.OrderBy(capability => capability.ToString(), StringComparer.Ordinal)]);
        }
    }

    public BorrowCapabilitySnapshot(
        IEnumerable<BorrowCapabilityKind>? globalCapabilities = null,
        bool isEnforced = false,
        IReadOnlyDictionary<string, HashSet<BorrowCapabilityKind>>? capabilityProviders = null)
    {
        _globalCapabilities = globalCapabilities != null
            ? [.. globalCapabilities]
            : [];
        _localCapabilities = [];
        _targetCapabilities = new Dictionary<string, HashSet<BorrowCapabilityKind>>(StringComparer.Ordinal);
        _capabilityProviders = capabilityProviders != null
            ? capabilityProviders.ToDictionary(
                entry => entry.Key,
                entry => new HashSet<BorrowCapabilityKind>(entry.Value),
                StringComparer.Ordinal)
            : new Dictionary<string, HashSet<BorrowCapabilityKind>>(StringComparer.Ordinal);
        IsEnforced = isEnforced;
    }

    public static BorrowCapabilitySnapshot AllowAll() => new(isEnforced: false);

    public static BorrowCapabilitySnapshot Enforced(params BorrowCapabilityKind[] globalCapabilities)
        => new(globalCapabilities, isEnforced: true);

    public bool CanRead(LocalId localId) => HasCapability(localId, BorrowCapabilityKind.Read);

    public bool CanRead(BorrowTarget target) => HasCapability(target, BorrowCapabilityKind.Read);

    public bool CanWrite(BorrowTarget target) => HasCapability(target, BorrowCapabilityKind.Write);

    public bool CanMove(LocalId localId) => HasCapability(localId, BorrowCapabilityKind.Move);

    public bool CanMove(BorrowTarget target) => HasCapability(target, BorrowCapabilityKind.Move);

    /// <summary>
    /// 解释 Local 能力命中路径（local -> global，显式 local grant 会遮蔽 fallback）。
    /// </summary>
    public string ExplainCapabilityResolution(LocalId localId, BorrowCapabilityKind required)
    {
        var requiredText = required.ToString().ToLowerInvariant();
        if (!IsEnforced)
        {
            return $"compat-mode (required={requiredText}, source=allow-all)";
        }

        if (!localId.IsValid)
        {
            return $"local:%? (required={requiredText}, source=invalid-local)";
        }

        if (_localCapabilities.TryGetValue(localId, out var localCapabilities))
        {
            var localText = FormatCapabilities(localCapabilities);
            if (HasGrantedCapability(localCapabilities, required))
            {
                return $"local:%{localId.Value} (required={requiredText}, granted={localText}, source=local)";
            }

            return
                $"local:%{localId.Value} (required={requiredText}, granted={localText}, source=none, fallback=blocked-by-explicit-local)";
        }

        var globalText = FormatCapabilities(_globalCapabilities);
        if (HasGrantedCapability(_globalCapabilities, required))
        {
            return
                $"local:%{localId.Value}->global (required={requiredText}, granted={globalText}, source=global)";
        }

        return $"local:%{localId.Value}->global (required={requiredText}, granted={globalText}, source=none)";
    }

    /// <summary>
    /// 解释 BorrowTarget 能力命中路径（target -> local -> global，显式 target grant 会遮蔽 fallback）。
    /// </summary>
    public string ExplainCapabilityResolution(BorrowTarget target, BorrowCapabilityKind required)
    {
        var requiredText = required.ToString().ToLowerInvariant();
        if (!IsEnforced)
        {
            return $"compat-mode (required={requiredText}, source=allow-all)";
        }

        if (!target.IsValid)
        {
            return $"target:%?/root (required={requiredText}, source=invalid-target)";
        }

        var targetKey = target.StableKey;
        if (_targetCapabilities.TryGetValue(targetKey, out var targetCapabilities))
        {
            var targetText = FormatCapabilities(targetCapabilities);
            if (HasGrantedCapability(targetCapabilities, required))
            {
                return $"target:{targetKey} (required={requiredText}, granted={targetText}, source=target)";
            }

            return
                $"target:{targetKey} (required={requiredText}, granted={targetText}, source=none, fallback=blocked-by-explicit-target)";
        }

        var localResolution = ExplainCapabilityResolution(target.BaseLocal, required);
        return $"target:{targetKey}->{localResolution}";
    }

    public void GrantLocal(LocalId localId, params BorrowCapabilityKind[] capabilities)
    {
        if (!localId.IsValid || capabilities.Length == 0)
        {
            return;
        }

        var set = GetOrCreateLocalCapabilities(localId);
        foreach (var capability in capabilities)
        {
            set.Add(capability);
        }
    }

    public void GrantTarget(BorrowTarget target, params BorrowCapabilityKind[] capabilities)
    {
        if (!target.IsValid || capabilities.Length == 0)
        {
            return;
        }

        var key = target.StableKey;
        if (!_targetCapabilities.TryGetValue(key, out var set))
        {
            set = [];
            _targetCapabilities[key] = set;
        }

        foreach (var capability in capabilities)
        {
            set.Add(capability);
        }
    }

    public string ToDebugString()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"enforced: {IsEnforced}");
        builder.AppendLine($"global: {FormatCapabilities(_globalCapabilities)}");

        var localGrants = EnumerateLocalCapabilityGrants().ToList();
        if (localGrants.Count > 0)
        {
            builder.AppendLine("locals:");
            foreach (var (local, capabilities) in localGrants)
            {
                builder.AppendLine($"  %{local.Value}: {FormatCapabilities(capabilities)}");
            }
        }

        var targetGrants = EnumerateTargetCapabilityGrants().ToList();
        if (targetGrants.Count > 0)
        {
            builder.AppendLine("targets:");
            foreach (var (targetKey, capabilities) in targetGrants)
            {
                builder.AppendLine($"  {targetKey}: {FormatCapabilities(capabilities)}");
            }
        }

        var providers = EnumerateCapabilityProviders().ToList();
        if (providers.Count > 0)
        {
            builder.AppendLine("providers:");
            foreach (var (provider, capabilities) in providers)
            {
                builder.AppendLine($"  {provider}: {FormatCapabilities(capabilities)}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private bool HasCapability(LocalId localId, BorrowCapabilityKind required)
    {
        if (!IsEnforced || !localId.IsValid)
        {
            return true;
        }

        if (_localCapabilities.TryGetValue(localId, out var localCapabilities))
        {
            return HasGrantedCapability(localCapabilities, required);
        }

        return HasGrantedCapability(_globalCapabilities, required);
    }

    private bool HasCapability(BorrowTarget target, BorrowCapabilityKind required)
    {
        if (!IsEnforced || !target.IsValid)
        {
            return true;
        }

        if (_targetCapabilities.TryGetValue(target.StableKey, out var targetCapabilities))
        {
            return HasGrantedCapability(targetCapabilities, required);
        }

        return HasCapability(target.BaseLocal, required);
    }

    private HashSet<BorrowCapabilityKind> GetOrCreateLocalCapabilities(LocalId localId)
    {
        if (!_localCapabilities.TryGetValue(localId, out var set))
        {
            set = [];
            _localCapabilities[localId] = set;
        }

        return set;
    }

    private static bool HasGrantedCapability(IReadOnlySet<BorrowCapabilityKind> granted, BorrowCapabilityKind required)
    {
        if (granted.Contains(required))
        {
            return true;
        }

        // 写能力隐含读能力。
        return required == BorrowCapabilityKind.Read &&
               granted.Contains(BorrowCapabilityKind.Write);
    }

    private static bool TryResolveCapabilityTag(string tag, out BorrowCapabilityKind capability)
    {
        capability = BorrowCapabilityKind.Read;
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        switch (tag.Trim().ToLowerInvariant())
        {
            case "read":
                capability = BorrowCapabilityKind.Read;
                return true;
            case "write":
            case WellKnownStrings.Keywords.Mut:
            case "mutable":
                capability = BorrowCapabilityKind.Write;
                return true;
            case "move":
            case "consume":
            case "ownership":
                capability = BorrowCapabilityKind.Move;
                return true;
            default:
                return false;
        }
    }

    private static string FormatCapabilities(IEnumerable<BorrowCapabilityKind> capabilities)
    {
        var parts = capabilities
            .Select(capability => capability.ToString().ToLowerInvariant())
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        return parts.Count == 0 ? "(none)" : string.Join(", ", parts);
    }
}
