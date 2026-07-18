namespace Eidosc.Semantic;

internal enum GeneratedDeclarationIdentityRegistration
{
    Added,
    Unchanged,
    Conflict
}

internal sealed record GeneratedDeclarationIdentityCandidate(string Identity, string PayloadHash);

internal sealed record PreparedGeneratedDeclarationIdentity(
    string Identity,
    string PayloadHash,
    GeneratedDeclarationIdentityRegistration Registration);

internal sealed class GeneratedDeclarationIdentityRegistry
{
    private readonly Dictionary<string, string> _payloadHashes = new(StringComparer.Ordinal);

    public void Clear() => _payloadHashes.Clear();

    public bool TryPrepareBatch(
        IReadOnlyList<GeneratedDeclarationIdentityCandidate> candidates,
        out IReadOnlyList<PreparedGeneratedDeclarationIdentity> prepared,
        out string conflictIdentity)
    {
        var observed = new Dictionary<string, string>(_payloadHashes, StringComparer.Ordinal);
        var result = new List<PreparedGeneratedDeclarationIdentity>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (!observed.TryGetValue(candidate.Identity, out var payloadHash))
            {
                observed[candidate.Identity] = candidate.PayloadHash;
                result.Add(new PreparedGeneratedDeclarationIdentity(
                    candidate.Identity,
                    candidate.PayloadHash,
                    GeneratedDeclarationIdentityRegistration.Added));
                continue;
            }

            if (!string.Equals(payloadHash, candidate.PayloadHash, StringComparison.Ordinal))
            {
                prepared = [];
                conflictIdentity = candidate.Identity;
                return false;
            }

            result.Add(new PreparedGeneratedDeclarationIdentity(
                candidate.Identity,
                candidate.PayloadHash,
                GeneratedDeclarationIdentityRegistration.Unchanged));
        }

        prepared = result;
        conflictIdentity = string.Empty;
        return true;
    }

    public void CommitBatch(IReadOnlyList<PreparedGeneratedDeclarationIdentity> prepared)
    {
        foreach (var entry in prepared)
        {
            if (entry.Registration == GeneratedDeclarationIdentityRegistration.Added)
            {
                _payloadHashes.Add(entry.Identity, entry.PayloadHash);
            }
        }
    }

    public GeneratedDeclarationIdentityRegistration Register(string identity, string payloadHash)
    {
        if (_payloadHashes.TryAdd(identity, payloadHash))
        {
            return GeneratedDeclarationIdentityRegistration.Added;
        }

        return string.Equals(_payloadHashes[identity], payloadHash, StringComparison.Ordinal)
            ? GeneratedDeclarationIdentityRegistration.Unchanged
            : GeneratedDeclarationIdentityRegistration.Conflict;
    }
}
