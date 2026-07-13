using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Eidosup.Commands;
using Eidosup.Configuration;
using Eidosup.Diagnostics;
using Eidosup.Distribution;
using Eidosup.Installation;
using Eidosup.Proxies;
using Eidosup.SelfManagement;
using Eidosup.Toolchains;
using NSec.Cryptography;

namespace Eidosc.Tests.Unit.Eidosup;

[Collection("Eidosup environment")]
public sealed class EidosupWp2Tests
{
    private const string AssetHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("custom:local", ToolchainSpecKind.Custom)]
    [InlineData("CUSTOM:dev_1", ToolchainSpecKind.Custom)]
    public void ToolchainSpec_ParsesCustomSelectors(string value, ToolchainSpecKind kind)
    {
        var spec = ToolchainSpec.Parse(value);

        Assert.Equal(kind, spec.Kind);
        Assert.StartsWith("custom:", spec.Canonical, StringComparison.Ordinal);
        Assert.Equal(ToolchainSelectorKind.Custom, spec.SelectorKind);
    }

    [Theory]
    [InlineData("preview@linux-arm64", "preview@linux-arm64", "linux-arm64")]
    [InlineData("0.4.0-alpha.2@win-x64", "0.4.0-alpha.2@win-x64", "win-x64")]
    public void ToolchainSpec_ParsesExplicitHostRid(string value, string canonical, string rid)
    {
        var spec = ToolchainSpec.Parse(value);

        Assert.Equal(canonical, spec.Canonical);
        Assert.Equal(rid, spec.HostRid);
    }

    [Fact]
    public async Task StateStore_MigratesSchemaOneWithoutLosingManagedState()
    {
        using var fixture = new EidosupToolchainTestFixture();
        var toolchain = await fixture.CreateToolchainAsync("0.4.0-alpha.2", AssetHash);
        var store = new ToolchainStateStore(static () => EidosupToolchainTestFixture.FixedTime);
        var state = await store.RegisterInstallAsync(
            fixture.Layout,
            toolchain.Directory,
            ReleaseChannel.Preview,
            CancellationToken.None);
        var path = Path.Combine(fixture.Layout.StateDirectory, ToolchainStateStore.FileName);
        var json = JsonSerializer.SerializeToNode(state, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!.AsObject();
        json["schema"] = 1;
        json.Remove("customToolchains");
        json.Remove("overrides");
        await File.WriteAllTextAsync(path, json.ToJsonString());

        var migrated = await store.InitializeAsync(fixture.Layout, CancellationToken.None);

        Assert.Equal(3, migrated.Schema);
        Assert.Single(migrated.Toolchains);
        Assert.Empty(migrated.CustomToolchains);
        Assert.Empty(migrated.Overrides);
        Assert.Contains("\"schema\": 3", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolver_ProjectFileWinsOverSameDirectoryOverride()
    {
        using var fixture = new EidosupToolchainTestFixture();
        var preview = await fixture.CreateToolchainAsync("0.4.0-alpha.2", AssetHash);
        var stable = await fixture.CreateToolchainAsync(
            "0.4.0",
            "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789");
        var store = new ToolchainStateStore(static () => EidosupToolchainTestFixture.FixedTime);
        await store.RegisterInstallAsync(fixture.Layout, preview.Directory, ReleaseChannel.Preview, CancellationToken.None);
        await store.RegisterInstallAsync(fixture.Layout, stable.Directory, ReleaseChannel.Stable, CancellationToken.None);
        var project = Path.Combine(fixture.Root, "project");
        var nested = Path.Combine(project, "src", "nested");
        Directory.CreateDirectory(nested);
        await store.SetOverrideAsync(fixture.Layout, project, "stable", CancellationToken.None);
        await File.WriteAllTextAsync(
            Path.Combine(project, ProjectToolchainConfigurationReader.FileName),
            "[toolchain]\nchannel = \"preview\"\nprofile = \"default\"\ncomponents = []\ntargets = []\n");

        var resolved = await new ToolchainResolver().ResolveAsync(
            fixture.Layout,
            "eidosc",
            null,
            CancellationToken.None,
            nested);

        Assert.Equal("preview", resolved.Selector);
        Assert.Equal(ToolchainSelectionSource.ProjectFile, resolved.SelectionSource);
        Assert.Equal(Path.Combine(project, ProjectToolchainConfigurationReader.FileName), resolved.SelectionSourcePath);
    }

    [Theory]
    [InlineData("channel = \"preview\"\n[toolchain]\nchannel = \"preview\"\n", "inside [toolchain]")]
    [InlineData("[other]\nchannel = \"preview\"\n", "Unknown or empty TOML section")]
    public async Task ProjectToolchainFile_RejectsUnknownStructure(
        string content,
        string expectedMessage)
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, ProjectToolchainConfigurationReader.FileName);
        await File.WriteAllTextAsync(path, content);

        var exception = await Assert.ThrowsAsync<EidosupException>(() =>
            ProjectToolchainConfigurationReader.ReadAsync(path, CancellationToken.None));

        Assert.Equal(EidosupErrorCode.InvalidArgument, exception.Code);
        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectToolchainFile_AcceptsWp3ProfileComponentsAndTargets()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, ProjectToolchainConfigurationReader.FileName);
        await File.WriteAllTextAsync(
            path,
            "[toolchain]\nchannel = \"preview\"\nprofile = \"minimal\"\ncomponents = [\"eidos-docs\"]\ntargets = [\"linux-arm64\"]\n");

        var configuration = await ProjectToolchainConfigurationReader.ReadAsync(path, CancellationToken.None);

        Assert.Equal("minimal", configuration.Profile);
        Assert.Equal(["eidos-docs"], configuration.Components);
        Assert.Equal(["linux-arm64"], configuration.Targets);
    }

    [Fact]
    public async Task Resolver_EnvironmentWinsOverProjectAndDefault()
    {
        using var fixture = new EidosupToolchainTestFixture();
        var first = await fixture.CreateToolchainAsync("0.4.0-alpha.2", AssetHash);
        var second = await fixture.CreateToolchainAsync(
            "0.4.0-alpha.3",
            "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789");
        var store = new ToolchainStateStore(static () => EidosupToolchainTestFixture.FixedTime);
        await store.RegisterInstallAsync(fixture.Layout, first.Directory, ReleaseChannel.Preview, CancellationToken.None);
        await store.RegisterInstallAsync(fixture.Layout, second.Directory, null, CancellationToken.None);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, ProjectToolchainConfigurationReader.FileName),
            "[toolchain]\nchannel = \"preview\"\n");
        var previous = Environment.GetEnvironmentVariable("EIDOSUP_TOOLCHAIN");
        try
        {
            Environment.SetEnvironmentVariable("EIDOSUP_TOOLCHAIN", "0.4.0-alpha.3");
            var resolved = await new ToolchainResolver().ResolveAsync(
                fixture.Layout,
                "eidosc",
                null,
                CancellationToken.None,
                fixture.Root);

            Assert.Equal("0.4.0-alpha.3", resolved.Selector);
            Assert.Equal(ToolchainSelectionSource.Environment, resolved.SelectionSource);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EIDOSUP_TOOLCHAIN", previous);
        }
    }

    [Fact]
    public async Task CustomToolchain_LinkResolveAndUnlinkNeverDeletesExternalBuild()
    {
        using var fixture = new EidosupToolchainTestFixture();
        var customRoot = Path.Combine(fixture.Root, "compiler-build");
        Directory.CreateDirectory(Path.Combine(customRoot, "runtime"));
        Directory.CreateDirectory(Path.Combine(customRoot, "stdlib"));
        await File.WriteAllTextAsync(Path.Combine(customRoot, PlatformContext.Detect().ExecutableName), "binary");
        var manager = new ToolchainManager(clock: static () => EidosupToolchainTestFixture.FixedTime);

        var linked = await manager.LinkCustomAsync(fixture.Options, "local", customRoot, false, CancellationToken.None);
        await manager.SetDefaultAsync(fixture.Options, ToolchainSpec.Parse("custom:local"), false, CancellationToken.None);
        var resolved = await manager.ResolveAsync(
            fixture.Options,
            "eidosc",
            ToolchainSpec.Parse("custom:local"),
            CancellationToken.None,
            fixture.Root);
        await manager.SetDefaultAsync(fixture.Options, null, false, CancellationToken.None);
        await manager.UnlinkCustomAsync(fixture.Options, "local", false, CancellationToken.None);

        Assert.Equal(linked.ToolchainId, resolved.ToolchainId);
        Assert.True(resolved.IsCustom);
        Assert.True(Directory.Exists(customRoot));
        Assert.Empty((await manager.ListAsync(fixture.Options, CancellationToken.None)).CustomToolchains);
    }

    [Fact]
    public async Task CompatibilityVerifier_RejectsProjectLanguageOutsideToolchainRange()
    {
        using var fixture = new EidosupToolchainTestFixture();
        var customRoot = Path.Combine(fixture.Root, "compiler-build");
        Directory.CreateDirectory(Path.Combine(customRoot, "runtime"));
        Directory.CreateDirectory(Path.Combine(customRoot, "stdlib"));
        await File.WriteAllTextAsync(Path.Combine(customRoot, PlatformContext.Detect().ExecutableName), "binary");
        await File.WriteAllTextAsync(
            Path.Combine(customRoot, ToolchainCompatibilityVerifier.CompatibilityFileName),
            "{\"schema\":1,\"component\":\"eidosc\",\"version\":\"0.4.0-alpha.2\",\"language\":{\"supported\":\">=0.4.0-alpha.1 <0.5.0\"}}");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "eidos.toml"), "[language]\nversion = \"0.5.0\"\n");
        var manager = new ToolchainManager(clock: static () => EidosupToolchainTestFixture.FixedTime);
        await manager.LinkCustomAsync(fixture.Options, "local", customRoot, false, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<EidosupException>(() => manager.ResolveAsync(
            fixture.Options,
            "eidosc",
            ToolchainSpec.Parse("custom:local"),
            CancellationToken.None,
            fixture.Root));

        Assert.Equal(EidosupErrorCode.ToolchainUnavailable, exception.Code);
        Assert.Contains("requires '0.5.0'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsStore_RoundTripsValidatedSourceAndModes()
    {
        using var fixture = new EidosupToolchainTestFixture();
        var store = new EidosupSettingsStore();
        var expected = EidosupSettings.Default() with
        {
            Source = "index:https://dist.example.test/eidos/index.json",
            AutoInstall = AutoInstallMode.Disable,
            AutoSelfUpdate = AutoSelfUpdateMode.Enable
        };

        await store.WriteAsync(fixture.Layout, expected, CancellationToken.None);
        var actual = await store.ReadAsync(fixture.Layout, CancellationToken.None);

        Assert.Equal(expected, actual);
        Assert.DoesNotContain("token", await File.ReadAllTextAsync(Path.Combine(fixture.Layout.RootDirectory, EidosupSettingsStore.FileName)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SettingsStore_PreservesHashInQuotedOfflinePathAndRejectsUnknownKeys()
    {
        using var fixture = new EidosupToolchainTestFixture();
        var store = new EidosupSettingsStore();
        var expected = EidosupSettings.Default() with
        {
            Source = DistributionSourceDescriptor.Parse($"offline:{Path.Combine(fixture.Root, "bundle#one")}").Canonical
        };
        await store.WriteAsync(fixture.Layout, expected, CancellationToken.None);

        Assert.Equal(expected, await store.ReadAsync(fixture.Layout, CancellationToken.None));

        var path = Path.Combine(fixture.Layout.RootDirectory, EidosupSettingsStore.FileName);
        await File.AppendAllTextAsync(path, "unknown = \"value\"\n");
        var exception = await Assert.ThrowsAsync<EidosupException>(() =>
            store.ReadAsync(fixture.Layout, CancellationToken.None));
        Assert.Equal(EidosupErrorCode.StateUnsupported, exception.Code);
    }

    [Fact]
    public async Task SourceCatalog_StoresMultipleMirrorsAndSignedTrustDominatesGitHubFallback()
    {
        using var fixture = new EidosupToolchainTestFixture();
        var store = new DistributionSourceCatalogStore();
        await store.AddAsync(
            fixture.Layout,
            "corp",
            DistributionSourceDescriptor.Parse("github:dlqw/Eidosc"),
            1000,
            EidosupToolchainTestFixture.FixedTime,
            CancellationToken.None);
        await store.AddAsync(
            fixture.Layout,
            "corp",
            DistributionSourceDescriptor.Parse("index:https://dist.example.test/index.json"),
            10,
            EidosupToolchainTestFixture.FixedTime,
            CancellationToken.None);

        var resolved = await store.ResolveAsync(fixture.Layout, "corp", CancellationToken.None);
        using var source = ConfiguredReleaseSourceFactory.Create(resolved, fixture.Layout.StateDirectory);

        Assert.Equal(2, resolved.Count);
        var identified = Assert.IsType<IdentifiedReleaseSource>(source);
        Assert.Equal("index:https://dist.example.test/index.json", identified.Identity);
    }

    [Fact]
    public async Task SourceCatalog_DryRunReturnsProposedCatalogWithoutWritingState()
    {
        using var fixture = new EidosupToolchainTestFixture();
        var store = new DistributionSourceCatalogStore();

        var proposed = await store.AddAsync(
            fixture.Layout,
            "corp",
            DistributionSourceDescriptor.Parse("index:https://dist.example.test/index.json"),
            100,
            EidosupToolchainTestFixture.FixedTime,
            dryRun: true,
            CancellationToken.None);

        Assert.Single(proposed.Sources);
        Assert.False(File.Exists(Path.Combine(fixture.Layout.StateDirectory, DistributionSourceCatalogStore.FileName)));
        Assert.Empty((await store.ReadAsync(fixture.Layout, CancellationToken.None)).Sources);
    }

    [Fact]
    public async Task CompositeReleaseSource_PreservesMetadataTrustFailureWhenEveryMirrorFails()
    {
        using var source = new CompositeReleaseSource(
        [
            new IdentifiedReleaseSource("index:first", new ThrowingReleaseSource(new EidosupException(
                EidosupErrorCode.InvalidReleaseMetadata,
                EidosupExitCodes.InvalidRelease,
                "The signed index has no trusted signature."))),
            new IdentifiedReleaseSource("index:second", new ThrowingReleaseSource(new HttpRequestException("offline")))
        ]);

        var exception = await Assert.ThrowsAsync<EidosupException>(() => source.ResolveReleaseAsync(
            null,
            ReleaseChannel.Preview,
            CancellationToken.None));

        Assert.Equal(EidosupErrorCode.InvalidReleaseMetadata, exception.Code);
        Assert.Equal(EidosupExitCodes.InvalidRelease, exception.ExitCode);
        Assert.IsType<AggregateException>(exception.InnerException);
    }

    [Fact]
    public async Task SignedIndexVerifier_RejectsRollbackAfterTrustedVersionAdvances()
    {
        using var temporary = new TemporaryDirectory();
        using var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport
        });
        var keyId = "test-root";
        var encodedPublic = Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey));
        var previous = Environment.GetEnvironmentVariable("EIDOSUP_TRUSTED_ED25519_KEYS");
        try
        {
            Environment.SetEnvironmentVariable("EIDOSUP_TRUSTED_ED25519_KEYS", $"{keyId}={encodedPublic}");
            var verifier = new SignedReleaseIndexVerifier(
                new MetadataTrustStore(temporary.Path),
                static () => DateTimeOffset.Parse("2026-07-13T00:00:00Z"));
            await verifier.VerifyAsync("index:test", Sign(CreateIndex(2), keyId, key), CancellationToken.None);

            var exception = await Assert.ThrowsAsync<EidosupException>(() => verifier.VerifyAsync(
                "index:test",
                Sign(CreateIndex(1), keyId, key),
                CancellationToken.None));

            Assert.Equal(EidosupErrorCode.InvalidReleaseMetadata, exception.Code);
            Assert.Contains("older", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EIDOSUP_TRUSTED_ED25519_KEYS", previous);
        }
    }

    [Fact]
    public async Task SignedIndexVerifier_AcceptsVerifiedKeyRotationAndRejectsExpiredMetadata()
    {
        using var temporary = new TemporaryDirectory();
        using var root = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        using var rotated = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var previous = Environment.GetEnvironmentVariable("EIDOSUP_TRUSTED_ED25519_KEYS");
        try
        {
            Environment.SetEnvironmentVariable(
                "EIDOSUP_TRUSTED_ED25519_KEYS",
                $"root={Convert.ToBase64String(root.PublicKey.Export(KeyBlobFormat.RawPublicKey))}");
            var verifier = new SignedReleaseIndexVerifier(
                new MetadataTrustStore(temporary.Path),
                static () => DateTimeOffset.Parse("2026-07-13T00:00:00Z"));
            var first = CreateIndex(1) with
            {
                NextKeys = [new MetadataPublicKey(
                    "rotated",
                    "ed25519",
                    Convert.ToBase64String(rotated.PublicKey.Export(KeyBlobFormat.RawPublicKey)))]
            };
            await verifier.VerifyAsync("index:rotation", Sign(first, "root", root), CancellationToken.None);
            await verifier.VerifyAsync("index:rotation", Sign(first, "root", root), CancellationToken.None);
            var second = CreateIndex(2) with { NextKeys = first.NextKeys };
            await verifier.VerifyAsync("index:rotation", Sign(second, "rotated", rotated), CancellationToken.None);

            var revoked = await Assert.ThrowsAsync<EidosupException>(() => verifier.VerifyAsync(
                "index:rotation",
                Sign(CreateIndex(3), "root", root),
                CancellationToken.None));
            Assert.Contains("No trusted", revoked.Message, StringComparison.Ordinal);

            var expired = CreateIndex(3) with
            {
                NextKeys = first.NextKeys,
                ExpiresAt = DateTimeOffset.Parse("2026-07-12T12:00:00Z")
            };
            var exception = await Assert.ThrowsAsync<EidosupException>(() => verifier.VerifyAsync(
                "index:rotation",
                Sign(expired, "rotated", rotated),
                CancellationToken.None));

            Assert.Contains("expired", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EIDOSUP_TRUSTED_ED25519_KEYS", previous);
        }
    }

    [Fact]
    public async Task OfflineZipSource_RejectsSymbolicLinksBeforeMetadataLoading()
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = Path.Combine(temporary.Path, "unsafe-offline.zip");
        using (var stream = File.Create(archivePath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("assets/link");
            entry.ExternalAttributes = (0xA000 | 0x1FF) << 16;
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("target");
        }

        using var source = new OfflineBundleReleaseSource(
            archivePath,
            $"offline:{archivePath}",
            Path.Combine(temporary.Path, "state"));

        var exception = await Assert.ThrowsAsync<EidosupException>(() => source.ResolveReleaseAsync(
            null,
            ReleaseChannel.Preview,
            CancellationToken.None));

        Assert.Equal(EidosupErrorCode.UnsafeArchive, exception.Code);
    }

    [Fact]
    public async Task OfflineBundle_ImportsVerifiesAndExportsCompleteBundle()
    {
        using var fixture = new EidosupToolchainTestFixture();
        using var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var previous = Environment.GetEnvironmentVariable("EIDOSUP_TRUSTED_ED25519_KEYS");
        var content = Path.Combine(fixture.Root, "bundle-content");
        Directory.CreateDirectory(Path.Combine(content, "assets"));
        var bundleAssetPath = Path.Combine(content, "assets", "eidosc-v0.4.0-alpha.3-win-x64.zip");
        await File.WriteAllTextAsync(bundleAssetPath, "bundle");
        var checksumPath = Path.Combine(content, "assets", "SHA256SUMS");
        await File.WriteAllTextAsync(checksumPath, $"{Hash(bundleAssetPath)}  {Path.GetFileName(bundleAssetPath)}\n");
        var index = CreateIndex(1) with
        {
            Releases = [new SignedReleaseEntry(
                "eidosc-v0.4.0-alpha.3",
                "eidosc-v0.4.0-alpha.3",
                true,
                DateTimeOffset.Parse("2026-07-12T00:00:00Z"),
                [
                    new SignedReleaseAsset(Path.GetFileName(bundleAssetPath), "assets/" + Path.GetFileName(bundleAssetPath), new FileInfo(bundleAssetPath).Length, Hash(bundleAssetPath)),
                    new SignedReleaseAsset("SHA256SUMS", "assets/SHA256SUMS", new FileInfo(checksumPath).Length, Hash(checksumPath))
                ])]
        };
        var envelope = Sign(index, "bundle", key);
        await File.WriteAllTextAsync(
            Path.Combine(content, "index.json"),
            JsonSerializer.Serialize(envelope, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var archive = Path.Combine(fixture.Root, "bundle.zip");
        ZipFile.CreateFromDirectory(content, archive);
        try
        {
            Environment.SetEnvironmentVariable(
                "EIDOSUP_TRUSTED_ED25519_KEYS",
                $"bundle={Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey))}");
            var manager = new OfflineBundleManager();
            var imported = await manager.ImportAsync(fixture.Layout, archive, CancellationToken.None);
            var exported = Path.Combine(fixture.Root, "exported.zip");
            await manager.ExportAsync(fixture.Layout, imported.Id, exported, CancellationToken.None);

            Assert.True(File.Exists(exported));
            Assert.StartsWith("offline:", imported.Source, StringComparison.Ordinal);
            using var exportedArchive = ZipFile.OpenRead(exported);
            Assert.Contains(exportedArchive.Entries, static entry => entry.FullName == "index.json");
        }
        finally
        {
            Environment.SetEnvironmentVariable("EIDOSUP_TRUSTED_ED25519_KEYS", previous);
        }
    }

    [Fact]
    public async Task DownloadManager_CopiesAndVerifiesFileUriAssets()
    {
        using var temporary = new TemporaryDirectory();
        var source = Path.Combine(temporary.Path, "asset.bin");
        await File.WriteAllTextAsync(source, "payload");
        var digest = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source))).ToLowerInvariant();
        using var manager = new DownloadManager();

        var result = await manager.DownloadArtifactAsync(
            new EidosReleaseAsset("asset.bin", new Uri(source).AbsoluteUri, new FileInfo(source).Length),
            Path.Combine(temporary.Path, "cache"),
            digest,
            CancellationToken.None);

        Assert.Equal(digest, result.Sha256);
        Assert.Equal("payload", await File.ReadAllTextAsync(result.Path));
    }

    [Fact]
    public async Task CacheClean_RemovesOfflineBundlesAtomicallyAndAllIncludesPartials()
    {
        using var fixture = new EidosupToolchainTestFixture();
        var contentDirectory = Path.Combine(fixture.Layout.CacheDirectory, "aa");
        Directory.CreateDirectory(contentDirectory);
        var content = Path.Combine(contentDirectory, new string('a', 64));
        await File.WriteAllBytesAsync(content, new byte[10]);
        var offline = Path.Combine(fixture.Layout.DownloadDirectory, "offline", new string('b', 64));
        Directory.CreateDirectory(Path.Combine(offline, "assets"));
        var index = Path.Combine(offline, "index.json");
        var asset = Path.Combine(offline, "assets", "bundle.zip");
        await File.WriteAllBytesAsync(index, new byte[8]);
        await File.WriteAllBytesAsync(asset, new byte[12]);
        var old = DateTime.UtcNow.AddDays(-2);
        File.SetLastAccessTimeUtc(index, old);
        File.SetLastAccessTimeUtc(asset, old);
        Directory.SetLastAccessTimeUtc(offline, old);
        File.SetLastAccessTimeUtc(content, DateTime.UtcNow);

        var manager = new ArtifactCacheManager();
        var bounded = manager.Clean(fixture.Layout, maximumBytes: 10, all: false, dryRun: false);

        Assert.Equal(30, bounded.BytesBefore);
        Assert.Equal(10, bounded.BytesAfter);
        Assert.Equal(2, bounded.FilesRemoved);
        Assert.False(Directory.Exists(offline));
        Assert.True(File.Exists(content));

        var partial = content + ".partial";
        await File.WriteAllBytesAsync(partial, new byte[5]);
        var complete = manager.Clean(fixture.Layout, maximumBytes: null, all: true, dryRun: false);

        Assert.Equal(15, complete.BytesBefore);
        Assert.Equal(0, complete.BytesAfter);
        Assert.Equal(2, complete.FilesRemoved);
        Assert.False(File.Exists(content));
        Assert.False(File.Exists(partial));
    }

    [Fact]
    public async Task SelfUninstall_DeletesOnlyOwnedManagedPathsAndPreservesCustomExternalBuild()
    {
        using var fixture = new EidosupToolchainTestFixture();
        var managed = await fixture.CreateToolchainAsync("0.4.0-alpha.2", AssetHash);
        var store = new ToolchainStateStore(static () => EidosupToolchainTestFixture.FixedTime);
        await store.RegisterInstallAsync(fixture.Layout, managed.Directory, ReleaseChannel.Preview, CancellationToken.None);
        var external = Path.Combine(fixture.Root, "external-build");
        Directory.CreateDirectory(Path.Combine(external, "runtime"));
        Directory.CreateDirectory(Path.Combine(external, "stdlib"));
        await File.WriteAllTextAsync(Path.Combine(external, PlatformContext.Detect().ExecutableName), "binary");
        await store.LinkCustomAsync(
            fixture.Layout,
            CustomToolchain.ValidateAndCreate("local", external, EidosupToolchainTestFixture.FixedTime),
            CancellationToken.None);
        Directory.CreateDirectory(fixture.Layout.BinDirectory);
        var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        const string ownedBinary = "owned-binary";
        var ownedDigest = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ownedBinary))).ToLowerInvariant();
        await File.WriteAllTextAsync(Path.Combine(fixture.Layout.BinDirectory, "eidosup" + extension), ownedBinary);
        await File.WriteAllTextAsync(Path.Combine(fixture.Layout.BinDirectory, "eidosc" + extension), ownedBinary);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Layout.BinDirectory, ShimInstaller.ManifestFileName),
            JsonSerializer.Serialize(new
            {
                schema = ShimInstaller.ManifestSchema,
                managerFile = "eidosup" + extension,
                shimFile = "eidosc" + extension,
                sha256 = ownedDigest
            }));
        await new EidosupSettingsStore().WriteAsync(fixture.Layout, EidosupSettings.Default(), CancellationToken.None);

        await SelfLifecycleManager.DeleteOwnedAsync(fixture.Layout, keepToolchains: false, CancellationToken.None);

        Assert.True(Directory.Exists(external));
        Assert.False(Directory.Exists(managed.Directory));
        Assert.False(Directory.Exists(fixture.Layout.BinDirectory));
        Assert.False(Directory.Exists(fixture.Layout.StateDirectory));
    }

    [Fact]
    public async Task SelfUninstall_RefusesToDeleteManagedDirectoryThatNoLongerVerifies()
    {
        using var fixture = new EidosupToolchainTestFixture();
        var managed = await fixture.CreateToolchainAsync("0.4.0-alpha.2", AssetHash);
        var store = new ToolchainStateStore(static () => EidosupToolchainTestFixture.FixedTime);
        await store.RegisterInstallAsync(fixture.Layout, managed.Directory, ReleaseChannel.Preview, CancellationToken.None);
        var source = Path.Combine(fixture.Root, OperatingSystem.IsWindows() ? "manager-source.exe" : "manager-source");
        await File.WriteAllTextAsync(source, "manager");
        await new ShimInstaller(source).InstallAsync(fixture.Layout, dryRun: false, CancellationToken.None);
        await File.AppendAllTextAsync(Path.Combine(managed.Directory, PlatformContext.Detect().ExecutableName), "modified");

        var exception = await Assert.ThrowsAsync<EidosupException>(() =>
            SelfLifecycleManager.DeleteOwnedAsync(fixture.Layout, keepToolchains: false, CancellationToken.None));

        Assert.Equal(EidosupErrorCode.StateCorrupt, exception.Code);
        Assert.True(Directory.Exists(managed.Directory));
        Assert.True(Directory.Exists(fixture.Layout.BinDirectory));
    }

    [Fact]
    public async Task SelfUpdateScheduler_UsesConfiguredModeAndChecksOnlyOncePerInterval()
    {
        using var fixture = new EidosupToolchainTestFixture();
        await new EidosupSettingsStore().WriteAsync(
            fixture.Layout,
            EidosupSettings.Default() with { AutoSelfUpdate = AutoSelfUpdateMode.CheckOnly },
            CancellationToken.None);
        var calls = 0;
        var scheduler = new SelfUpdateScheduler(
            (layout, checkOnly, token) =>
            {
                calls++;
                Assert.True(checkOnly);
                return Task.FromResult(new SelfUpdateResult(SelfUpdateStatus.Current, "0.3.0-alpha.1", "0.3.0-alpha.1", null));
            },
            static () => DateTimeOffset.Parse("2026-07-13T00:00:00Z"),
            TimeSpan.FromHours(24));

        var first = await scheduler.RunIfDueAsync(fixture.Layout, CancellationToken.None);
        var second = await scheduler.RunIfDueAsync(fixture.Layout, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SelfUpdateStagingCleanup_PreservesRunningCandidateAndDeletesOtherFiles()
    {
        using var fixture = new EidosupToolchainTestFixture();
        var staging = Path.Combine(fixture.Layout.StateDirectory, "self-update");
        Directory.CreateDirectory(staging);
        var running = Path.Combine(staging, "running-candidate.exe");
        var stale = Path.Combine(staging, "stale-candidate.exe");
        await File.WriteAllTextAsync(running, "running");
        await File.WriteAllTextAsync(stale, "stale");

        SelfLifecycleManager.CleanupStagedFiles(fixture.Layout, running);

        Assert.True(File.Exists(running));
        Assert.False(File.Exists(stale));
    }

    [Theory]
    [InlineData("bash", "complete -F _eidosup")]
    [InlineData("fish", "complete -c eidosup")]
    [InlineData("zsh", "#compdef eidosup")]
    [InlineData("powershell", "Register-ArgumentCompleter")]
    public void CompletionGenerator_EmitsDeterministicScripts(string shell, string marker)
    {
        var first = ShellCompletionGenerator.Generate(shell);
        var second = ShellCompletionGenerator.Generate(shell);

        Assert.Equal(first, second);
        Assert.Contains(marker, first, StringComparison.Ordinal);
        Assert.Contains("toolchain", first, StringComparison.Ordinal);
        Assert.Contains("component", first, StringComparison.Ordinal);
        Assert.Contains("target", first, StringComparison.Ordinal);
        Assert.Contains("doc", first, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileScriptWriter_RemoveBlockIsSymmetricAndPreservesUserContent()
    {
        var original = "export BEFORE=1\nexport AFTER=2\n";
        var plan = new EnvironmentPlan("/home/user/.eidos", null, ["/home/user/.eidos/bin"]);
        var configured = ProfileScriptWriter.UpsertBlock(original, ProfileScriptWriter.BuildUnixProfileBlock(plan));

        var removed = ProfileScriptWriter.RemoveBlock(configured);

        Assert.Equal(original, removed);
    }

    private static SignedReleaseIndex CreateIndex(long version) => new(
        1,
        version,
        DateTimeOffset.Parse("2026-07-12T00:00:00Z"),
        DateTimeOffset.Parse("2026-08-12T00:00:00Z"),
        [],
        [new SignedReleaseEntry(
            "eidosc-v0.4.0-alpha.3",
            "eidosc-v0.4.0-alpha.3",
            true,
            DateTimeOffset.Parse("2026-07-12T00:00:00Z"),
            [new SignedReleaseAsset(
                "SHA256SUMS",
                "https://dist.example.test/SHA256SUMS",
                1,
                new string('0', 64))])]);

    private static SignedReleaseIndexEnvelope Sign(SignedReleaseIndex index, string keyId, Key key)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(index, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
            WriteIndented = false
        });
        return new SignedReleaseIndexEnvelope(
            index,
            [new MetadataSignature(keyId, "ed25519", Convert.ToBase64String(SignatureAlgorithm.Ed25519.Sign(key, payload)))]);
    }

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"eidosup-wp2-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class ThrowingReleaseSource(Exception exception) : IEidosReleaseSource
    {
        public Task<EidosReleaseInfo> ResolveReleaseAsync(
            string? version,
            ReleaseChannel channel,
            CancellationToken cancellationToken) => Task.FromException<EidosReleaseInfo>(exception);

        public void Dispose()
        {
        }
    }
}
