using Eidosc.Pipeline;
using System.Security.Cryptography;
using System.Text;

namespace Eidosc.ProjectSystem;

public static class ContentHash
{
    public static string ComputeForDirectory(string directory, string[]? sourceRoots = null)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(PipelineMessages.DirectoryNotFound(directory));

        var entries = new List<(string RelativePath, string Hash)>();
        var roots = sourceRoots ?? ["."];

        foreach (var root in roots)
        {
            var fullPath = Path.GetFullPath(Path.Combine(directory, root));
            if (!Directory.Exists(fullPath)) continue;

            foreach (var file in Directory.EnumerateFiles(fullPath, "*.eidos", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(directory, file).Replace('\\', '/');
                var content = File.ReadAllText(file);
                var hash = ComputeHash(content);
                entries.Add((relativePath, hash));
            }
        }

        var manifestPath = Path.Combine(directory, EidosProjectConfigurationLoader.DefaultFileName);
        if (File.Exists(manifestPath))
        {
            var hash = ComputeHash(File.ReadAllText(manifestPath));
            entries.Add((EidosProjectConfigurationLoader.DefaultFileName, hash));
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));

        var sb = new StringBuilder();
        foreach (var (path, hash) in entries)
            sb.AppendLine($"{path}:{hash}");

        return ComputeHash(sb.ToString());
    }

    public static string ComputeHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
