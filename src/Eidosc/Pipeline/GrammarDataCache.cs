using Eidosc.Parsing.Lexer;
using Eidosc.Utils;
using MemoryPack;

namespace Eidosc.Pipeline;

internal static class GrammarDataCache
{
    private static readonly object Gate = new();
    private static CacheData? CachedData;

    public static CacheData LoadOrBuild(string cachePath, string cacheVersion)
    {
        if (TryGet(cachePath, cacheVersion, out var grammarData, out var scannerData))
        {
            return new CacheData(grammarData, scannerData, cacheVersion);
        }

        (grammarData, scannerData) = LexerTableBuilder.Build();
        var cacheData = new CacheData(grammarData, scannerData, cacheVersion);
        Store(cacheData);
        return cacheData;
    }

    public static bool TryGet(string cachePath, string cacheVersion, out GrammarData grammarData, out ScannerData scannerData)
    {
        lock (Gate)
        {
            if (CachedData != null &&
                string.Equals(CachedData.CacheVersion, cacheVersion, StringComparison.Ordinal))
            {
                grammarData = CachedData.GrammarData;
                scannerData = CachedData.ScannerData;
                return true;
            }
        }

        if (File.Exists(cachePath))
        {
            try
            {
                var cacheData = MemoryPackSerializer.Deserialize<CacheData>(File.ReadAllBytes(cachePath));
                if (cacheData != null &&
                    string.Equals(cacheData.CacheVersion, cacheVersion, StringComparison.Ordinal))
                {
                    Store(cacheData);
                    grammarData = cacheData.GrammarData;
                    scannerData = cacheData.ScannerData;
                    return true;
                }
            }
            catch
            {
                // Cache corrupt, rebuild.
            }
        }

        grammarData = null!;
        scannerData = null!;
        return false;
    }

    public static void Store(CacheData cacheData)
    {
        lock (Gate)
        {
            CachedData = cacheData;
        }
    }
}
