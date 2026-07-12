namespace Eidosc.Pipeline;

public static class SourcePathNormalizer
{
    public static string Normalize(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return "";
        }

        if (sourcePath.StartsWith('<') && sourcePath.EndsWith('>'))
        {
            return sourcePath;
        }

        try
        {
            return Path.GetFullPath(sourcePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace('\\', '/');
        }
        catch
        {
            return sourcePath.Replace('\\', '/');
        }
    }

    public static string NormalizeForCacheKey(string sourcePath) => Normalize(sourcePath);
}
