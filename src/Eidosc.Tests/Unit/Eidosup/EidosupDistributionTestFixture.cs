using System.Security.Cryptography;
using System.Text;
using Eidosup.Distribution;
using Eidosup.Installation;
using Eidosup.Toolchains;

namespace Eidosc.Tests.Unit.Eidosup;

internal static class EidosupDistributionTestFixture
{
    public static LoadedToolchainDistribution Create(
        EidosReleaseInfo release,
        EidosReleaseAsset manifestAsset,
        PlatformContext platform,
        string bundleSha256,
        string executableContent = "binary")
    {
        var bundle = new ReleaseAssetLocator().ResolveEidoscBundleAsset(release, platform);
        var artifact = new ToolchainComponentArtifact(
            bundle.Name,
            bundle.Size ?? throw new InvalidOperationException("Test bundle size is required."),
            bundleSha256);
        var core = new ToolchainComponentDefinition(
            "eidosc-core",
            "eidosc-core",
            release.NormalizedVersion,
            Required: true,
            Target: null,
            Dependencies: [],
            Conflicts: [],
            artifact,
            [new ToolchainComponentFile(platform.ExecutableName, Encoding.UTF8.GetByteCount(executableContent), HashText(executableContent), Executable: true)]);
        var runtime = new ToolchainComponentDefinition(
            $"eidos-runtime@{platform.Rid}",
            "eidos-runtime",
            "0.1.0-alpha.1",
            Required: false,
            Target: platform.Rid,
            Dependencies: [core.Id],
            Conflicts: [],
            artifact,
            [new ToolchainComponentFile("runtime/runtime.h", 6, HashText("header"))]);
        var std = new ToolchainComponentDefinition(
            "eidos-std",
            "eidos-std",
            "0.1.0-alpha.1",
            Required: true,
            Target: null,
            Dependencies: [core.Id],
            Conflicts: [],
            artifact,
            [new ToolchainComponentFile("stdlib/Std/Core.eidos", 15, HashText("module Std.Core"))]);
        var docsIndex = $"{{\"schema\":1,\"eidoscVersion\":\"{release.NormalizedVersion}\",\"topics\":{{\"index\":\"index.md\"}}}}";
        var docs = new ToolchainComponentDefinition(
            "eidos-docs",
            "eidos-docs",
            release.NormalizedVersion,
            Required: false,
            Target: null,
            Dependencies: [core.Id],
            Conflicts: [],
            artifact,
            [
                new ToolchainComponentFile("docs/index.json", Encoding.UTF8.GetByteCount(docsIndex), HashText(docsIndex)),
                new ToolchainComponentFile("docs/index.md", 6, HashText("# docs"))
            ]);
        var bindgenName = platform.IsWindows ? "eidos-bindgen.exe" : "eidos-bindgen";
        var bindgen = new ToolchainComponentDefinition(
            "eidos-bindgen",
            "eidos-bindgen",
            "0.1.0-alpha.1",
            Required: false,
            Target: null,
            Dependencies: [core.Id],
            Conflicts: [],
            artifact,
            [new ToolchainComponentFile($"tools/eidos-bindgen/{bindgenName}", 7, HashText("bindgen"), Executable: true)]);
        var manifest = new ToolchainDistributionManifest(
            ToolchainDistributionManifest.CurrentSchema,
            $"eidosc-{release.NormalizedVersion}-{platform.Rid}",
            "preview",
            platform.Rid,
            new ToolchainProductIdentity(release.NormalizedVersion, new string('a', 40)),
            new ToolchainLanguageIdentity("0.6.0-alpha.1"),
            [
                new ToolchainProfileDefinition("minimal", [core.Id, std.Id]),
                new ToolchainProfileDefinition("default", [core.Id, std.Id, runtime.Id]),
                new ToolchainProfileDefinition("complete", [core.Id, std.Id, runtime.Id, docs.Id, bindgen.Id])
            ],
            [core, std, runtime, docs, bindgen],
            [new ToolchainTargetDefinition(
                platform.Rid,
                "test-triple",
                runtime.Id,
                ToolchainTargetSupport.Host,
                new ToolchainLinkerRequirement("clang", ExternalSdkRequired: false))],
            new ToolchainRequirementSet(new ToolchainLlvmRequirement(">=20.1.0 <22.0.0")),
            EidosupToolchainTestFixture.FixedTime);
        return new LoadedToolchainDistribution(
            manifest,
            manifestAsset,
            manifestAsset.Sha256 ?? new string('d', 64),
            ChecksumManifest.Parse($"{bundleSha256}  {bundle.Name}"),
            CacheHit: false,
            Resumed: false);
    }

    public static string HashText(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}

internal sealed class StubToolchainManifestLoader(
    Func<EidosReleaseInfo, EidosReleaseAsset, PlatformContext, LoadedToolchainDistribution> load)
    : IToolchainManifestLoader
{
    public Task<LoadedToolchainDistribution> LoadAsync(
        EidosReleaseInfo release,
        EidosReleaseAsset manifestAsset,
        EidosReleaseAsset checksumAsset,
        PlatformContext platform,
        ToolInstallLayout layout,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken) => Task.FromResult(load(release, manifestAsset, platform));

    public void Dispose()
    {
    }
}
