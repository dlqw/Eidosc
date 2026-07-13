using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Pipeline;

public sealed class ModuleArtifactCacheTests
{
    [Fact]
    public void ArtifactKey_ChangesWhenDependencySignatureChanges()
    {
        var first = CreateKey(dependencySignatureHash: "a");
        var second = CreateKey(dependencySignatureHash: "b");

        Assert.NotEqual(first.StableHash(), second.StableHash());
    }

    [Fact]
    public void ComputeTextHash_IsStableForSmallAndLargeInputs()
    {
        Assert.Equal(
            ModuleArtifactHash.ComputeTextHash("small"),
            ModuleArtifactHash.ComputeTextHash("small"));

        var large = string.Concat(Enumerable.Repeat("large-hash-input-", 512));
        Assert.Equal(
            ModuleArtifactHash.ComputeTextHash(large),
            ModuleArtifactHash.ComputeTextHash(large));
        Assert.NotEqual(
            ModuleArtifactHash.ComputeTextHash(large),
            ModuleArtifactHash.ComputeTextHash($"{large}!"));
    }

    [Fact]
    public void StoreArtifact_CanReloadManifest()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_artifact_cache_{Guid.NewGuid():N}");
        try
        {
            var key = CreateKey();
            var cache = new ModuleArtifactCache(tempDir);

            var manifest = cache.StoreArtifact(key, "signature", ".json", """{"exports":[]}""");

            Assert.True(File.Exists(manifest.PayloadPath));
            var reloaded = new ModuleArtifactCache(tempDir);
            Assert.True(reloaded.TryGetArtifact(key, "signature", out var loaded));
            Assert.NotNull(loaded);
            Assert.True(File.Exists(loaded.PayloadPath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void TryReadArtifactJson_LoadsStoredPayload()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_artifact_cache_json_{Guid.NewGuid():N}");
        try
        {
            var key = CreateKey();
            var cache = new ModuleArtifactCache(tempDir);
            cache.StoreArtifact(key, "signature", ".json", """{"name":"Main","count":2}""");

            var reloaded = new ModuleArtifactCache(tempDir);

            Assert.True(reloaded.TryReadArtifactJson<TestArtifactPayload>(key, "signature", out var payload));
            Assert.NotNull(payload);
            Assert.Equal("Main", payload!.Name);
            Assert.Equal(2, payload.Count);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void StoreArtifactFile_CopiesBinaryPayloadAndReloadsManifest()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_artifact_cache_file_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var sourcePayload = Path.Combine(tempDir, "payload.bin");
            File.WriteAllBytes(sourcePayload, [0, 1, 2, 255]);
            var key = CreateKey(dependencySignatureHash: "binary");
            var cache = new ModuleArtifactCache(Path.Combine(tempDir, "cache"));

            var manifest = cache.StoreArtifactFile(key, "native-executable", ".exe", sourcePayload);

            Assert.True(File.Exists(manifest.PayloadPath));
            Assert.Equal([0, 1, 2, 255], File.ReadAllBytes(manifest.PayloadPath));
            var reloaded = new ModuleArtifactCache(Path.Combine(tempDir, "cache"));
            Assert.True(reloaded.TryGetArtifact(key, "native-executable", out var loaded));
            Assert.NotNull(loaded);
            Assert.Equal([0, 1, 2, 255], File.ReadAllBytes(loaded.PayloadPath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StoreAndReadArtifacts_AllowsConcurrentAccess()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_artifact_cache_concurrent_{Guid.NewGuid():N}");
        try
        {
            var cache = new ModuleArtifactCache(tempDir);
            var tasks = Enumerable.Range(0, 32)
                .Select(index => Task.Run(() =>
                {
                    var key = CreateKey(dependencySignatureHash: $"deps-{index}");
                    cache.StoreArtifactJson(
                        key,
                        "signature",
                        new TestArtifactPayload($"Module{index}", index));

                    Assert.True(cache.TryReadArtifactJson<TestArtifactPayload>(
                        key,
                        "signature",
                        out var payload));
                    Assert.NotNull(payload);
                    Assert.Equal($"Module{index}", payload!.Name);
                    Assert.Equal(index, payload.Count);
                }))
                .ToArray();

            await Task.WhenAll(tasks);
            var reloaded = new ModuleArtifactCache(tempDir);
            for (var index = 0; index < tasks.Length; index++)
            {
                var key = CreateKey(dependencySignatureHash: $"deps-{index}");
                Assert.True(reloaded.TryReadArtifactJson<TestArtifactPayload>(
                    key,
                    "signature",
                    out var payload));
                Assert.NotNull(payload);
                Assert.Equal(index, payload!.Count);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void TryReadArtifactText_RejectsSameLengthPayloadCorruption()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_artifact_cache_corrupt_{Guid.NewGuid():N}");
        try
        {
            var key = CreateKey(dependencySignatureHash: "corrupt");
            var cache = new ModuleArtifactCache(tempDir);
            var manifest = cache.StoreArtifact(key, "signature", ".json", "abc");
            File.WriteAllText(manifest.PayloadPath, "xyz");

            Assert.False(cache.IsArtifactUpToDate(key, "signature"));
            Assert.False(cache.TryReadArtifactText(key, "signature", out _));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void GetStatusAndClear_ReportAllManagedFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_artifact_cache_status_{Guid.NewGuid():N}");
        try
        {
            var cache = new ModuleArtifactCache(tempDir);
            cache.Update("Main.eidos", "Main :: module {}");
            cache.StoreArtifact(CreateKey(dependencySignatureHash: "status"), "signature", ".json", "{}");

            var status = cache.GetStatus();
            Assert.Equal(3, status.TotalFiles);
            Assert.Equal(1, status.ArtifactManifests);
            Assert.Equal(1, status.PayloadFiles);
            Assert.Equal(1, status.FingerprintEntries);
            Assert.Equal(0, status.OrphanPayloadFiles);
            Assert.True(status.TotalBytes > 0);

            var cleared = cache.Clear();
            Assert.Equal(status.TotalFiles, cleared.DeletedFiles);
            Assert.Equal(status.TotalBytes, cleared.DeletedBytes);
            Assert.Equal(0, cache.GetStatus().TotalBytes);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Prune_CleansGarbageWhenCacheIsBelowLimit()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_artifact_cache_gc_{Guid.NewGuid():N}");
        try
        {
            var key = CreateKey(dependencySignatureHash: "gc");
            var cache = new ModuleArtifactCache(tempDir);
            cache.StoreArtifact(key, "signature", ".json", "{}");
            File.WriteAllText(Path.Combine(tempDir, "orphan.bin"), "orphan");
            File.WriteAllText(Path.Combine(tempDir, "write.tmp-crash"), "temporary");

            var result = cache.Prune(long.MaxValue);

            Assert.Equal(2, result.DeletedFiles);
            Assert.True(cache.IsArtifactUpToDate(key, "signature"));
            var status = cache.GetStatus();
            Assert.Equal(0, status.OrphanPayloadFiles);
            Assert.DoesNotContain(
                Directory.EnumerateFiles(tempDir),
                static path => Path.GetFileName(path).Contains(".tmp-", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Prune_ZeroLimitRemovesArtifactsAndFingerprintEntries()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_artifact_cache_prune_{Guid.NewGuid():N}");
        try
        {
            var key = CreateKey(dependencySignatureHash: "prune");
            var cache = new ModuleArtifactCache(tempDir);
            cache.Update("Main.eidos", "Main :: module {}");
            cache.StoreArtifact(key, "signature", ".json", "{}");

            var result = cache.Prune(0);

            Assert.True(result.DeletedFiles >= 3);
            Assert.Equal(0, result.BytesAfter);
            Assert.Equal(0, cache.GetStatus().TotalBytes);
            Assert.False(cache.TryGetFingerprint("Main.eidos", out _));
            Assert.False(cache.TryGetArtifact(key, "signature", out _));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StoreArtifacts_WithIndependentCacheInstances_UsesProcessLock()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_artifact_cache_process_lock_{Guid.NewGuid():N}");
        try
        {
            var tasks = Enumerable.Range(0, 16)
                .Select(index => Task.Run(() =>
                {
                    var cache = new ModuleArtifactCache(tempDir);
                    cache.StoreArtifactJson(
                        CreateKey(dependencySignatureHash: $"process-{index}"),
                        "signature",
                        new TestArtifactPayload($"Module{index}", index));
                }))
                .ToArray();

            await Task.WhenAll(tasks);

            var reloaded = new ModuleArtifactCache(tempDir);
            for (var index = 0; index < tasks.Length; index++)
            {
                Assert.True(reloaded.TryReadArtifactJson<TestArtifactPayload>(
                    CreateKey(dependencySignatureHash: $"process-{index}"),
                    "signature",
                    out var payload));
                Assert.Equal(index, payload!.Count);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static ModuleArtifactKey CreateKey(string dependencySignatureHash = "deps") =>
        new()
        {
            ModuleKey = "App/Main",
            SourceHash = ModuleArtifactHash.ComputeSourceHash("main :: Unit { _ => () }"),
            LanguageVersion = "0.5.0-alpha.1",
            DependencySignatureHash = dependencySignatureHash,
            TargetTriple = "x86_64-pc-windows-msvc",
            FlagsHash = ModuleArtifactHash.ComputeFlagsHash(["mir-opt=true"])
        };

    private sealed record TestArtifactPayload(string Name, int Count);
}
