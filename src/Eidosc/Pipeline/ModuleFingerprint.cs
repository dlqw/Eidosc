using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Eidosc.Pipeline;

/// <summary>
/// 模块指纹：基于源文件内容的 SHA-256 哈希 + 依赖列表
/// </summary>
public sealed record ModuleFingerprint
{
    public string ContentHash { get; init; } = "";
    public List<string> Dependencies { get; init; } = [];
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    public static ModuleFingerprint Compute(string sourceText, List<string>? dependencies = null)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(sourceText));
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        return new ModuleFingerprint
        {
            ContentHash = hash,
            Dependencies = dependencies ?? [],
            Timestamp = DateTime.UtcNow
        };
    }

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    });

    public static ModuleFingerprint? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ModuleFingerprint>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
        catch
        {
            return null;
        }
    }

    public bool MatchesSource(string sourceText)
    {
        var current = Compute(sourceText);
        return string.Equals(ContentHash, current.ContentHash, StringComparison.OrdinalIgnoreCase);
    }
}
