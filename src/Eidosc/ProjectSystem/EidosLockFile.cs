using Eidosc.Pipeline;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eidosc.ProjectSystem;

public sealed record EidosLockFile
{
    public int Version { get; init; } = 1;
    public Dictionary<string, LockedPackage> Packages { get; init; } = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static EidosLockFile Load(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException(PipelineMessages.LockFileNotFound(filePath));

        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<EidosLockFile>(json, JsonOptions)
            ?? throw new InvalidOperationException(PipelineMessages.FailedToDeserializeLockFile);
    }

    public static bool TryLoad(string filePath, out EidosLockFile? lockFile)
    {
        try
        {
            lockFile = Load(filePath);
            return true;
        }
        catch
        {
            lockFile = null;
            return false;
        }
    }

    public void Save(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(filePath, json);
    }

    public bool Validate(string projectDir)
    {
        foreach (var (name, pkg) in Packages)
        {
            if (pkg.Source == "path" && pkg.Path != null)
            {
                var resolvedPath = Path.GetFullPath(Path.Combine(projectDir, pkg.Path));
                if (!Directory.Exists(resolvedPath)) return false;

                var expected = ContentHash.ComputeForDirectory(resolvedPath);
                if (pkg.ContentHash != null && pkg.ContentHash != expected) return false;
            }
        }
        return true;
    }
}

public sealed record LockedPackage
{
    public string Source { get; init; } = "";
    public string? Path { get; init; }
    public string? Git { get; init; }
    public string? RegistryName { get; init; }
    public string? RegistryIndex { get; init; }
    public string? Commit { get; init; }
    public string? Tag { get; init; }
    public string? Branch { get; init; }
    public string? Version { get; init; }
    public string? ContentHash { get; init; }
}
