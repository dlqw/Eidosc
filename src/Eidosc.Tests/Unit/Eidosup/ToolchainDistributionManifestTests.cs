using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using Eidosup.Diagnostics;
using Eidosup.Distribution;
using Eidosup.Installation;
using Eidosup.Toolchains;

namespace Eidosc.Tests.Unit.Eidosup;

public sealed class ToolchainDistributionManifestTests
{
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Validate_AcceptsComponentizedAggregateBundle()
    {
        var manifest = CreateManifest();

        manifest.Validate(manifest.Eidosc.Version, manifest.Host);

        Assert.Equal("eidosc-core", manifest.GetProfile(ToolchainProfile.Minimal).Components[0]);
        Assert.Equal(manifest.Host, manifest.GetTarget(manifest.Host).Name);
    }

    [Fact]
    public async Task ReadAsync_RejectsUnknownFieldsAndSchema()
    {
        using var temporary = new TemporaryDirectory();
        var manifest = CreateManifest();
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        var unknownPath = Path.Combine(temporary.Path, "unknown.json");
        await File.WriteAllTextAsync(unknownPath, json.Replace("{\"schema\":", "{\"unknown\":true,\"schema\":", StringComparison.Ordinal));

        var unknown = await Assert.ThrowsAsync<EidosupException>(() =>
            ToolchainDistributionManifest.ReadAsync(unknownPath, manifest.Eidosc.Version, manifest.Host, CancellationToken.None));

        Assert.Equal(EidosupErrorCode.InvalidReleaseMetadata, unknown.Code);

        var schemaPath = Path.Combine(temporary.Path, "schema.json");
        await File.WriteAllTextAsync(schemaPath, JsonSerializer.Serialize(manifest with { Schema = 2 }, JsonOptions));
        await Assert.ThrowsAsync<EidosupException>(() =>
            ToolchainDistributionManifest.ReadAsync(schemaPath, manifest.Eidosc.Version, manifest.Host, CancellationToken.None));
    }

    [Theory]
    [InlineData("../eidosc")]
    [InlineData("/absolute/eidosc")]
    [InlineData("runtime//runtime.h")]
    public void Validate_RejectsInvalidOwnedPath(string path)
    {
        var manifest = CreateManifest();
        var core = manifest.Components[0];
        var invalid = ReplaceComponent(manifest, core with
        {
            Files = [core.Files[0] with { Path = path }]
        });

        Assert.Throws<EidosupException>(() => invalid.Validate(invalid.Eidosc.Version, invalid.Host));
    }

    [Fact]
    public void Validate_RejectsDuplicateOwnershipAndInconsistentArtifactBinding()
    {
        var manifest = CreateManifest();
        var std = manifest.GetComponent("eidos-std");
        var duplicate = ReplaceComponent(manifest, std with
        {
            Files = [std.Files[0] with { Path = manifest.GetComponent("eidosc-core").Files[0].Path }]
        });
        Assert.Throws<EidosupException>(() => duplicate.Validate(duplicate.Eidosc.Version, duplicate.Host));

        var inconsistent = ReplaceComponent(manifest, std with
        {
            Artifact = std.Artifact with { Size = std.Artifact.Size + 1 }
        });
        Assert.Throws<EidosupException>(() => inconsistent.Validate(inconsistent.Eidosc.Version, inconsistent.Host));
    }

    [Fact]
    public void Validate_RejectsDependencyCycleAndNonMonotonicProfiles()
    {
        var manifest = CreateManifest();
        var core = manifest.GetComponent("eidosc-core");
        var cycle = ReplaceComponent(manifest, core with { Dependencies = ["eidos-std"] });
        Assert.Throws<EidosupException>(() => cycle.Validate(cycle.Eidosc.Version, cycle.Host));

        var invalidProfiles = manifest with
        {
            Profiles =
            [
                new ToolchainProfileDefinition("minimal", ["eidosc-core", "eidos-std", "eidos-docs"]),
                new ToolchainProfileDefinition("default", ["eidosc-core", "eidos-std", $"eidos-runtime@{manifest.Host}"]),
                manifest.GetProfile(ToolchainProfile.Complete)
            ]
        };
        Assert.Throws<EidosupException>(() => invalidProfiles.Validate(invalidProfiles.Eidosc.Version, invalidProfiles.Host));
    }

    [Fact]
    public void Validate_RejectsMissingOrMisclassifiedHostTargetAndProfileRuntime()
    {
        var manifest = CreateManifest();
        var hostTarget = manifest.GetTarget(manifest.Host);
        var misclassified = manifest with
        {
            Targets = [hostTarget with { Support = ToolchainTargetSupport.CrossCompile }]
        };
        Assert.Throws<EidosupException>(() =>
            misclassified.Validate(misclassified.Eidosc.Version, misclassified.Host));

        var missingHost = manifest with { Targets = [] };
        Assert.Throws<EidosupException>(() =>
            missingHost.Validate(missingHost.Eidosc.Version, missingHost.Host));

        var missingDefaultRuntime = manifest with
        {
            Profiles = manifest.Profiles.Select(profile =>
                string.Equals(profile.Name, "default", StringComparison.Ordinal)
                    ? profile with { Components = manifest.GetProfile(ToolchainProfile.Minimal).Components }
                    : profile).ToArray()
        };
        Assert.Throws<EidosupException>(() =>
            missingDefaultRuntime.Validate(missingDefaultRuntime.Eidosc.Version, missingDefaultRuntime.Host));
    }

    [Fact]
    public void Solver_ResolvesProfilesDependenciesConflictsAndTargets()
    {
        var manifest = CreateManifest();
        var minimal = ToolchainComponentSolver.ResolveInitial(
            manifest,
            new ToolchainInstallSelection(ToolchainProfile.Minimal, ["eidos-docs"], [manifest.Host]));

        Assert.Equal(
            ["eidosc-core", "eidos-std", $"eidos-runtime@{manifest.Host}", "eidos-docs"],
            minimal.ComponentIds);
        Assert.Single(minimal.Targets);

        var conflictingManifest = ReplaceComponent(
            manifest,
            manifest.GetComponent("eidos-bindgen") with { Conflicts = ["eidos-docs"] });
        Assert.Throws<EidosupException>(() => ToolchainComponentSolver.ResolveInitial(
            conflictingManifest,
            new ToolchainInstallSelection(ToolchainProfile.Complete, [], [])));
    }

    [Fact]
    public void Solver_AddsAndRemovesTargetsWithoutAllowingRequiredOrDependentRemoval()
    {
        var manifest = CreateManifest();
        var minimal = ToolchainComponentSolver.ResolveInitial(
            manifest,
            new ToolchainInstallSelection(ToolchainProfile.Minimal, [], []));
        var added = ToolchainComponentSolver.Add(
            manifest,
            ToolchainProfile.Minimal,
            minimal.ComponentIds,
            minimal.ExplicitComponents,
            minimal.ExplicitTargets,
            [],
            [manifest.Host]);

        Assert.Contains($"eidos-runtime@{manifest.Host}", added.ComponentIds);
        Assert.Single(added.Targets);

        var removed = ToolchainComponentSolver.Remove(
            manifest,
            ToolchainProfile.Minimal,
            added.ComponentIds,
            added.ExplicitComponents,
            added.ExplicitTargets,
            [],
            [manifest.Host]);
        Assert.DoesNotContain($"eidos-runtime@{manifest.Host}", removed.ComponentIds);

        Assert.Throws<EidosupException>(() => ToolchainComponentSolver.Remove(
            manifest,
            ToolchainProfile.Minimal,
            added.ComponentIds,
            added.ExplicitComponents,
            added.ExplicitTargets,
            ["eidosc-core"],
            []));

        var complete = ToolchainComponentSolver.ResolveInitial(
            manifest,
            new ToolchainInstallSelection(ToolchainProfile.Complete, [], []));
        Assert.Throws<EidosupException>(() => ToolchainComponentSolver.Remove(
            manifest,
            ToolchainProfile.Complete,
            complete.ComponentIds,
            complete.ExplicitComponents,
            complete.ExplicitTargets,
            ["eidosc-core"],
            []));
    }

    [Fact]
    public async Task Loader_BindsSignedManifestChecksumsAndComponentArtifacts()
    {
        using var fixture = await LoaderFixture.CreateAsync();
        using var loader = new ToolchainManifestLoader();

        var loaded = await loader.LoadAsync(
            fixture.Release,
            fixture.ManifestAsset,
            fixture.ChecksumAsset,
            PlatformContext.Detect(),
            fixture.Layout,
            progress: null,
            CancellationToken.None);

        Assert.Equal(fixture.ManifestSha256, loaded.ManifestSha256);
        Assert.Equal(fixture.BundleSha256, loaded.Manifest.Components[0].Artifact.Sha256);
    }

    [Fact]
    public async Task Loader_RejectsSignedDigestChecksumAndContentTampering()
    {
        using var signedDigest = await LoaderFixture.CreateAsync(manifestSignedDigest: new string('f', 64));
        using var loader = new ToolchainManifestLoader();
        await Assert.ThrowsAsync<EidosupException>(() => loader.LoadAsync(
            signedDigest.Release,
            signedDigest.ManifestAsset,
            signedDigest.ChecksumAsset,
            PlatformContext.Detect(),
            signedDigest.Layout,
            progress: null,
            CancellationToken.None));

        using var checksum = await LoaderFixture.CreateAsync(bundleChecksum: new string('e', 64));
        await Assert.ThrowsAsync<EidosupException>(() => loader.LoadAsync(
            checksum.Release,
            checksum.ManifestAsset,
            checksum.ChecksumAsset,
            PlatformContext.Detect(),
            checksum.Layout,
            progress: null,
            CancellationToken.None));

        using var content = await LoaderFixture.CreateAsync();
        await File.AppendAllTextAsync(content.ManifestPath, " ");
        await Assert.ThrowsAsync<EidosupException>(() => loader.LoadAsync(
            content.Release,
            content.ManifestAsset,
            content.ChecksumAsset,
            PlatformContext.Detect(),
            content.Layout,
            progress: null,
            CancellationToken.None));
    }

    private static ToolchainDistributionManifest CreateManifest()
    {
        var host = PlatformContext.Detect().Rid;
        const string version = "0.4.0-alpha.3";
        var asset = new ToolchainComponentArtifact($"eidosc-v{version}-{host}.zip", 4096, Digest);
        var components = new ToolchainComponentDefinition[]
        {
            Component("eidosc-core", "eidosc-core", version, required: true, null, [], asset, PlatformContext.Detect().ExecutableName, executable: true),
            Component("eidos-std", "eidos-std", "0.1.0-alpha.1", required: true, null, ["eidosc-core"], asset, "stdlib/Std/Core.eidos"),
            Component($"eidos-runtime@{host}", "eidos-runtime", "0.1.0-alpha.1", required: false, host, ["eidosc-core"], asset, "runtime/runtime.h"),
            Component("eidos-docs", "eidos-docs", version, required: false, null, ["eidosc-core"], asset, "docs/index.html"),
            Component("eidos-bindgen", "eidos-bindgen", "0.1.0-alpha.1", required: false, null, ["eidosc-core"], asset, "tools/eidos-bindgen/eidos-bindgen")
        };
        return new ToolchainDistributionManifest(
            ToolchainDistributionManifest.CurrentSchema,
            $"eidosc-{version}-{host}",
            "preview",
            host,
            new ToolchainProductIdentity(version, new string('a', 40)),
            new ToolchainLanguageIdentity("0.6.0-alpha.1"),
            [
                new ToolchainProfileDefinition("minimal", ["eidosc-core", "eidos-std"]),
                new ToolchainProfileDefinition("default", ["eidosc-core", "eidos-std", $"eidos-runtime@{host}"]),
                new ToolchainProfileDefinition("complete", ["eidosc-core", "eidos-std", $"eidos-runtime@{host}", "eidos-docs", "eidos-bindgen"])
            ],
            components,
            [
                new ToolchainTargetDefinition(
                    host,
                    "x86_64-unknown-test",
                    $"eidos-runtime@{host}",
                    ToolchainTargetSupport.Host,
                    new ToolchainLinkerRequirement("clang", ExternalSdkRequired: false))
            ],
            new ToolchainRequirementSet(new ToolchainLlvmRequirement(">=20.1.0 <22.0.0")),
            EidosupToolchainTestFixture.FixedTime);
    }

    private static ToolchainComponentDefinition Component(
        string id,
        string name,
        string version,
        bool required,
        string? target,
        IReadOnlyList<string> dependencies,
        ToolchainComponentArtifact artifact,
        string file,
        bool executable = false) => new(
        id,
        name,
        version,
        required,
        target,
        dependencies,
        [],
        artifact,
        [new ToolchainComponentFile(file, 1, Digest, executable)]);

    private static ToolchainDistributionManifest ReplaceComponent(
        ToolchainDistributionManifest manifest,
        ToolchainComponentDefinition replacement) => manifest with
    {
        Components = manifest.Components.Select(component =>
            string.Equals(component.Id, replacement.Id, StringComparison.Ordinal) ? replacement : component).ToArray()
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"eidosup-toolchain-manifest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class LoaderFixture : IDisposable
    {
        private LoaderFixture(
            TemporaryDirectory temporary,
            ToolInstallLayout layout,
            EidosReleaseInfo release,
            EidosReleaseAsset manifestAsset,
            EidosReleaseAsset checksumAsset,
            string manifestPath,
            string manifestSha256,
            string bundleSha256)
        {
            Temporary = temporary;
            Layout = layout;
            Release = release;
            ManifestAsset = manifestAsset;
            ChecksumAsset = checksumAsset;
            ManifestPath = manifestPath;
            ManifestSha256 = manifestSha256;
            BundleSha256 = bundleSha256;
        }

        public TemporaryDirectory Temporary { get; }
        public ToolInstallLayout Layout { get; }
        public EidosReleaseInfo Release { get; }
        public EidosReleaseAsset ManifestAsset { get; }
        public EidosReleaseAsset ChecksumAsset { get; }
        public string ManifestPath { get; }
        public string ManifestSha256 { get; }
        public string BundleSha256 { get; }

        public static async Task<LoaderFixture> CreateAsync(
            string? manifestSignedDigest = null,
            string? bundleChecksum = null)
        {
            var temporary = new TemporaryDirectory();
            try
            {
                var platform = PlatformContext.Detect();
                var manifest = CreateManifest();
                var bundleName = manifest.Components[0].Artifact.Name;
                var bundlePath = System.IO.Path.Combine(temporary.Path, bundleName);
                await File.WriteAllBytesAsync(bundlePath, new byte[4096]);
                var bundleSha256 = await HashAsync(bundlePath);
                var artifact = new ToolchainComponentArtifact(bundleName, 4096, bundleSha256);
                manifest = manifest with
                {
                    Components = manifest.Components.Select(component => component with { Artifact = artifact }).ToArray()
                };
                var manifestName = $"eidos-toolchain-v{manifest.Eidosc.Version}-{platform.Rid}.json";
                var manifestPath = System.IO.Path.Combine(temporary.Path, manifestName);
                await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
                var manifestSha256 = await HashAsync(manifestPath);
                var checksumPath = System.IO.Path.Combine(temporary.Path, ReleaseAssetLocator.ChecksumAssetName);
                await File.WriteAllTextAsync(
                    checksumPath,
                    $"{manifestSha256}  {manifestName}\n{bundleChecksum ?? bundleSha256}  {bundleName}\n");
                var manifestAsset = new EidosReleaseAsset(
                    manifestName,
                    manifestPath,
                    new FileInfo(manifestPath).Length,
                    manifestSignedDigest ?? manifestSha256);
                var checksumAsset = new EidosReleaseAsset(
                    ReleaseAssetLocator.ChecksumAssetName,
                    checksumPath,
                    new FileInfo(checksumPath).Length);
                var release = new EidosReleaseInfo(
                    $"eidosc-v{manifest.Eidosc.Version}",
                    "test",
                    Draft: false,
                    PreRelease: true,
                    EidosupToolchainTestFixture.FixedTime,
                    [
                        new EidosReleaseAsset(bundleName, bundlePath, 4096, bundleSha256),
                        manifestAsset,
                        checksumAsset
                    ]);
                var layout = ToolInstallLayout.Create(
                    platform,
                    System.IO.Path.Combine(temporary.Path, "install"),
                    System.IO.Path.Combine(temporary.Path, "downloads"));
                return new LoaderFixture(
                    temporary,
                    layout,
                    release,
                    manifestAsset,
                    checksumAsset,
                    manifestPath,
                    manifestSha256,
                    bundleSha256);
            }
            catch
            {
                temporary.Dispose();
                throw;
            }
        }

        public void Dispose() => Temporary.Dispose();

        private static async Task<string> HashAsync(string path)
        {
            await using var stream = File.OpenRead(path);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
        }
    }
}
