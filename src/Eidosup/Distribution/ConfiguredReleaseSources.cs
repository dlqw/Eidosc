using System.Text.Json;
using Eidosup.Diagnostics;

namespace Eidosup.Distribution;

public static class ConfiguredReleaseSourceFactory
{
    public static IEidosReleaseSource Create(
        DistributionSourceDescriptor descriptor,
        string stateDirectory)
    {
        return descriptor.Kind switch
        {
            DistributionSourceKind.GitHub => new Installation.GitHubReleaseClient(descriptor.Value),
            DistributionSourceKind.SignedIndex => new SignedIndexReleaseSource(
                new Uri(descriptor.Value),
                descriptor.Canonical,
                new SignedReleaseIndexVerifier(new MetadataTrustStore(stateDirectory))),
            DistributionSourceKind.OfflineBundle => new OfflineBundleReleaseSource(
                descriptor.Value,
                descriptor.Canonical,
                stateDirectory),
            _ => throw new ArgumentOutOfRangeException(nameof(descriptor))
        };
    }

    public static IEidosReleaseSource Create(
        IReadOnlyList<DistributionSourceDescriptor> descriptors,
        string stateDirectory)
    {
        if (descriptors.Count == 0)
        {
            throw new ArgumentException("At least one distribution source is required.", nameof(descriptors));
        }

        var requiredTrust = descriptors.Max(GetTrustLevel);
        var trusted = descriptors.Where(descriptor => GetTrustLevel(descriptor) == requiredTrust).ToArray();
        var sources = trusted.Select(descriptor => new IdentifiedReleaseSource(
            descriptor.Canonical,
            Create(descriptor, stateDirectory))).ToArray();
        return sources.Length == 1 ? sources[0] : new CompositeReleaseSource(sources);
    }

    private static int GetTrustLevel(DistributionSourceDescriptor descriptor) => descriptor.Kind switch
    {
        DistributionSourceKind.SignedIndex or DistributionSourceKind.OfflineBundle => 2,
        _ => 1
    };
}

public sealed class IdentifiedReleaseSource(string identity, IEidosReleaseSource inner) : IEidosReleaseSource
{
    public string Identity => identity;

    public async Task<EidosReleaseInfo> ResolveReleaseAsync(
        string? version,
        ReleaseChannel channel,
        CancellationToken cancellationToken)
    {
        var release = await inner.ResolveReleaseAsync(version, channel, cancellationToken);
        return release with { SourceIdentity = identity };
    }

    public void Dispose() => inner.Dispose();
}

public sealed class CompositeReleaseSource(IReadOnlyList<IdentifiedReleaseSource> sources) : IEidosReleaseSource
{
    private static readonly EidosupErrorCode[] PreservedFailureOrder =
    [
        EidosupErrorCode.IntegrityFailure,
        EidosupErrorCode.InvalidReleaseMetadata,
        EidosupErrorCode.UnsafeArchive,
        EidosupErrorCode.StateCorrupt,
        EidosupErrorCode.StateUnsupported
    ];

    public async Task<EidosReleaseInfo> ResolveReleaseAsync(
        string? version,
        ReleaseChannel channel,
        CancellationToken cancellationToken)
    {
        var failures = new List<Exception>();
        foreach (var source in sources)
        {
            try
            {
                return await source.ResolveReleaseAsync(version, channel, cancellationToken);
            }
            catch (Exception exception) when (exception is EidosupException or HttpRequestException or IOException)
            {
                failures.Add(exception);
            }
        }

        var aggregate = new AggregateException(failures);
        var preserved = PreservedFailureOrder
            .Select(code => failures.OfType<EidosupException>().FirstOrDefault(failure => failure.Code == code))
            .FirstOrDefault(static failure => failure != null)
            ?? failures.OfType<EidosupException>().FirstOrDefault(failure => failure.Code != EidosupErrorCode.NetworkFailure);
        if (preserved != null)
        {
            throw new EidosupException(
                preserved.Code,
                preserved.ExitCode,
                $"All {sources.Count} equally trusted distribution sources failed; the primary failure was: {preserved.Message}",
                preserved.Hint,
                aggregate);
        }

        throw new EidosupException(
            EidosupErrorCode.NetworkFailure,
            EidosupExitCodes.NetworkFailure,
            $"All {sources.Count} equally trusted distribution sources failed.",
            "Inspect verbose diagnostics, repair the configured mirrors, or select another source group.",
            aggregate);
    }

    public void Dispose()
    {
        foreach (var source in sources)
        {
            source.Dispose();
        }
    }
}

public sealed class SignedIndexReleaseSource : IEidosReleaseSource, IDisposable
{
    public const int MaximumIndexBytes = 16 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    private readonly Uri _indexUri;
    private readonly string _sourceIdentity;
    private readonly SignedReleaseIndexVerifier _verifier;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public SignedIndexReleaseSource(
        Uri indexUri,
        string sourceIdentity,
        SignedReleaseIndexVerifier verifier,
        HttpClient? httpClient = null)
    {
        _indexUri = indexUri;
        _sourceIdentity = sourceIdentity;
        _verifier = verifier;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
        _ownsHttpClient = httpClient == null;
    }

    public async Task<EidosReleaseInfo> ResolveReleaseAsync(
        string? version,
        ReleaseChannel channel,
        CancellationToken cancellationToken)
    {
        var envelope = await LoadAsync(cancellationToken);
        await _verifier.VerifyAsync(_sourceIdentity, envelope, cancellationToken);
        var releases = envelope.Payload.Releases.Select(entry => new EidosReleaseInfo(
            entry.TagName,
            entry.Name,
            Draft: false,
            entry.PreRelease,
            entry.PublishedAt,
            entry.Assets.Select(asset => new EidosReleaseAsset(
                asset.Name,
                ResolveAssetUrl(asset.Url),
                asset.Size,
                asset.Sha256)).ToArray())).ToArray();
        if (version != null)
        {
            var expected = SemanticVersion.Parse(Installation.ReleaseAssetLocator.NormalizeVersion(version));
            var release = releases.SingleOrDefault(candidate =>
                              string.Equals(candidate.NormalizedVersion, expected.ToString(), StringComparison.Ordinal))
                          ?? throw new EidosupException(
                              EidosupErrorCode.ReleaseNotFound,
                              EidosupExitCodes.ReleaseNotFound,
                              $"Version '{expected}' was not found in signed source '{_sourceIdentity}'.");
            return ReleaseSelector.ValidateExact(release, expected, _sourceIdentity);
        }

        return ReleaseSelector.Select(releases, channel, _sourceIdentity);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<SignedReleaseIndexEnvelope> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_indexUri.IsFile)
            {
                if (new FileInfo(_indexUri.LocalPath).Length > MaximumIndexBytes)
                {
                    throw new InvalidDataException($"The signed index exceeds {MaximumIndexBytes} bytes.");
                }

                await using var file = File.OpenRead(_indexUri.LocalPath);
                return await JsonSerializer.DeserializeAsync<SignedReleaseIndexEnvelope>(file, JsonOptions, cancellationToken)
                       ?? throw new JsonException("The signed index was empty.");
            }

            using var response = await _httpClient.GetAsync(
                _indexUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumIndexBytes)
            {
                throw new InvalidDataException($"The signed index exceeds {MaximumIndexBytes} bytes.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var bounded = await ReadBoundedAsync(stream, cancellationToken);
            return await JsonSerializer.DeserializeAsync<SignedReleaseIndexEnvelope>(bounded, JsonOptions, cancellationToken)
                   ?? throw new JsonException("The signed index was empty.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            throw new EidosupException(
                EidosupErrorCode.InvalidReleaseMetadata,
                EidosupExitCodes.InvalidRelease,
                $"Failed to load signed release index '{_indexUri}'.",
                "Check the source, proxy, and metadata schema, then retry.",
                exception);
        }
    }

    private static async Task<MemoryStream> ReadBoundedAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        var destination = new MemoryStream();
        try
        {
            var buffer = new byte[32 * 1024];
            var total = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    destination.Position = 0;
                    return destination;
                }

                total = checked(total + read);
                if (total > MaximumIndexBytes)
                {
                    throw new InvalidDataException($"The signed index exceeds {MaximumIndexBytes} bytes.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        catch
        {
            destination.Dispose();
            throw;
        }
    }

    private string ResolveAssetUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out var uri))
        {
            throw new FormatException($"Release asset URL '{value}' is invalid.");
        }

        return uri.IsAbsoluteUri ? uri.AbsoluteUri : new Uri(_indexUri, uri).AbsoluteUri;
    }
}

public sealed class OfflineBundleReleaseSource : IEidosReleaseSource, IDisposable
{
    private readonly string _bundlePath;
    private readonly string _sourceIdentity;
    private readonly string _stateDirectory;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private string? _temporaryDirectory;
    private SignedIndexReleaseSource? _source;
    private bool _disposed;

    public OfflineBundleReleaseSource(string bundlePath, string sourceIdentity, string stateDirectory)
    {
        var path = Path.GetFullPath(bundlePath);
        if (!Directory.Exists(path) &&
            (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase)))
        {
            throw new FileNotFoundException("Offline bundle directory or ZIP does not exist.", path);
        }

        _bundlePath = path;
        _sourceIdentity = sourceIdentity;
        _stateDirectory = stateDirectory;
    }

    public async Task<EidosReleaseInfo> ResolveReleaseAsync(
        string? version,
        ReleaseChannel channel,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureInitializedAsync(cancellationToken);
        return await _source!.ResolveReleaseAsync(version, channel, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source?.Dispose();
        _initializationLock.Dispose();
        if (_temporaryDirectory != null && Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_source != null)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_source != null)
            {
                return;
            }

            var root = _bundlePath;
            if (File.Exists(_bundlePath))
            {
                var temporary = Path.Combine(Path.GetTempPath(), $"eidosup-offline-{Guid.NewGuid():N}");
                Directory.CreateDirectory(temporary);
                try
                {
                    await new Installation.SafeZipExtractor().ExtractAsync(
                        _bundlePath,
                        temporary,
                        cancellationToken);
                    _temporaryDirectory = temporary;
                    root = temporary;
                }
                catch
                {
                    Directory.Delete(temporary, recursive: true);
                    throw;
                }
            }

            var indexPath = Path.Combine(root, "index.json");
            if (!File.Exists(indexPath))
            {
                throw new EidosupException(
                    EidosupErrorCode.InvalidReleaseMetadata,
                    EidosupExitCodes.InvalidRelease,
                    $"Offline bundle '{_bundlePath}' does not contain index.json.");
            }

            _source = new SignedIndexReleaseSource(
                new Uri(indexPath),
                _sourceIdentity,
                new SignedReleaseIndexVerifier(new MetadataTrustStore(_stateDirectory)));
        }
        catch
        {
            if (_source == null && _temporaryDirectory != null)
            {
                Directory.Delete(_temporaryDirectory, recursive: true);
                _temporaryDirectory = null;
            }

            throw;
        }
        finally
        {
            _initializationLock.Release();
        }
    }
}
