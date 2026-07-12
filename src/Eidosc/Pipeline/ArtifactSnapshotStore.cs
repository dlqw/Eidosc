using System.Text.Json;

namespace Eidosc.Pipeline;

public static class ArtifactSnapshotStore
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static ModuleArtifactManifest StoreJson<T>(
        ModuleArtifactCache cache,
        ModuleArtifactKey key,
        string kind,
        T snapshot)
    {
        return cache.StoreArtifact(
            key,
            kind,
            ".json",
            JsonSerializer.Serialize(snapshot, WriteOptions));
    }

    public static bool TryLoadJson<T>(
        ModuleArtifactCache cache,
        ModuleArtifactKey key,
        string kind,
        out T? snapshot)
    {
        snapshot = default;
        try
        {
            if (!cache.TryReadArtifactText(key, kind, out var json))
            {
                return false;
            }

            snapshot = JsonSerializer.Deserialize<T>(json, ReadOptions);
            return snapshot != null;
        }
        catch
        {
            snapshot = default;
            return false;
        }
    }

    public static bool TryLoadJson<T>(
        ModuleArtifactCache cache,
        ModuleArtifactKey key,
        string kind,
        Func<T, bool> validate,
        out T? snapshot)
    {
        if (!TryLoadJson(cache, key, kind, out snapshot) || snapshot == null)
        {
            return false;
        }

        if (validate(snapshot))
        {
            return true;
        }

        snapshot = default;
        return false;
    }
}
