using System.Text.Json;
using Eidosup.Diagnostics;
using Eidosup.Distribution;
using Eidosup.Installation;

namespace Eidosup.Configuration;

public sealed record NamedDistributionSource(
    string Name,
    string Descriptor,
    int Priority,
    DateTimeOffset AddedAt);

public sealed record DistributionSourceCatalog(
    int Schema,
    IReadOnlyList<NamedDistributionSource> Sources)
{
    public const int CurrentSchema = 1;
}

public sealed class DistributionSourceCatalogStore
{
    public const string FileName = "sources.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };
    private readonly TimeSpan _lockTimeout;

    public DistributionSourceCatalogStore(TimeSpan? lockTimeout = null)
    {
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task<DistributionSourceCatalog> ReadAsync(
        ToolInstallLayout layout,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(layout.StateDirectory, FileName);
        if (!File.Exists(path))
        {
            return new DistributionSourceCatalog(DistributionSourceCatalog.CurrentSchema, []);
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var catalog = await JsonSerializer.DeserializeAsync<DistributionSourceCatalog>(stream, JsonOptions, cancellationToken);
            if (catalog is not { Schema: DistributionSourceCatalog.CurrentSchema } || catalog.Sources == null)
            {
                throw Invalid(path);
            }

            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in catalog.Sources)
            {
                if (source == null || !IsValidName(source.Name) || source.Priority is < 0 or > 1000 || source.AddedAt == default)
                {
                    throw Invalid(path);
                }

                var descriptor = DistributionSourceDescriptor.Parse(source.Descriptor);
                if (!string.Equals(source.Descriptor, descriptor.Canonical, StringComparison.Ordinal) ||
                    !identities.Add($"{source.Name}\0{source.Descriptor}"))
                {
                    throw Invalid(path);
                }
            }

            return catalog;
        }
        catch (EidosupException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or NotSupportedException)
        {
            throw Invalid(path, exception);
        }
    }

    public async Task<DistributionSourceCatalog> AddAsync(
        ToolInstallLayout layout,
        string name,
        DistributionSourceDescriptor descriptor,
        int priority,
        DateTimeOffset addedAt,
        CancellationToken cancellationToken) =>
        await AddAsync(layout, name, descriptor, priority, addedAt, dryRun: false, cancellationToken);

    public async Task<DistributionSourceCatalog> AddAsync(
        ToolInstallLayout layout,
        string name,
        DistributionSourceDescriptor descriptor,
        int priority,
        DateTimeOffset addedAt,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (!IsValidName(name) || priority is < 0 or > 1000 || addedAt == default)
        {
            throw new FormatException("A source group name must use ASCII letters, digits, '.', '_' or '-', and priority must be 0..1000.");
        }

        if (dryRun)
        {
            return Add(await ReadAsync(layout, cancellationToken), name, descriptor, priority, addedAt);
        }

        await using var operationLock = await InstallOperationLock.AcquireAsync(
            layout.LockDirectory,
            _lockTimeout,
            cancellationToken,
            operationName: "sources");
        var catalog = await ReadAsync(layout, cancellationToken);
        var updated = Add(catalog, name, descriptor, priority, addedAt);
        await WriteAsync(layout, updated, cancellationToken);
        return updated;
    }

    private static DistributionSourceCatalog Add(
        DistributionSourceCatalog catalog,
        string name,
        DistributionSourceDescriptor descriptor,
        int priority,
        DateTimeOffset addedAt)
    {
        var sources = catalog.Sources
            .Where(source => !(string.Equals(source.Name, name, StringComparison.Ordinal) &&
                               string.Equals(source.Descriptor, descriptor.Canonical, StringComparison.Ordinal)))
            .Append(new NamedDistributionSource(name, descriptor.Canonical, priority, addedAt))
            .OrderBy(static source => source.Name, StringComparer.Ordinal)
            .ThenByDescending(static source => source.Priority)
            .ThenBy(static source => source.Descriptor, StringComparer.Ordinal)
            .ToArray();
        return new DistributionSourceCatalog(DistributionSourceCatalog.CurrentSchema, sources);
    }

    public async Task<DistributionSourceCatalog> RemoveAsync(
        ToolInstallLayout layout,
        string name,
        string? descriptor,
        CancellationToken cancellationToken) =>
        await RemoveAsync(layout, name, descriptor, dryRun: false, cancellationToken);

    public async Task<DistributionSourceCatalog> RemoveAsync(
        ToolInstallLayout layout,
        string name,
        string? descriptor,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (dryRun)
        {
            return Remove(await ReadAsync(layout, cancellationToken), name, descriptor);
        }

        await using var operationLock = await InstallOperationLock.AcquireAsync(
            layout.LockDirectory,
            _lockTimeout,
            cancellationToken,
            operationName: "sources");
        var catalog = await ReadAsync(layout, cancellationToken);
        var updated = Remove(catalog, name, descriptor);
        await WriteAsync(layout, updated, cancellationToken);
        return updated;
    }

    private static DistributionSourceCatalog Remove(
        DistributionSourceCatalog catalog,
        string name,
        string? descriptor)
    {
        var canonical = descriptor == null ? null : DistributionSourceDescriptor.Parse(descriptor).Canonical;
        var sources = catalog.Sources.Where(source =>
            !string.Equals(source.Name, name, StringComparison.Ordinal) ||
            canonical != null && !string.Equals(source.Descriptor, canonical, StringComparison.Ordinal)).ToArray();
        if (sources.Length == catalog.Sources.Count)
        {
            throw new EidosupException(
                EidosupErrorCode.ToolchainUnavailable,
                EidosupExitCodes.ToolchainUnavailable,
                $"Distribution source group '{name}' does not contain the requested entry.");
        }

        return new DistributionSourceCatalog(DistributionSourceCatalog.CurrentSchema, sources);
    }

    public async Task<IReadOnlyList<DistributionSourceDescriptor>> ResolveAsync(
        ToolInstallLayout layout,
        string value,
        CancellationToken cancellationToken)
    {
        try
        {
            return [DistributionSourceDescriptor.Parse(value)];
        }
        catch (FormatException) when (IsValidName(value))
        {
            var catalog = await ReadAsync(layout, cancellationToken);
            var entries = catalog.Sources
                .Where(source => string.Equals(source.Name, value, StringComparison.Ordinal))
                .OrderByDescending(static source => source.Priority)
                .ThenBy(static source => source.Descriptor, StringComparer.Ordinal)
                .Select(source => DistributionSourceDescriptor.Parse(source.Descriptor))
                .ToArray();
            if (entries.Length == 0)
            {
                throw new EidosupException(
                    EidosupErrorCode.InvalidArgument,
                    EidosupExitCodes.InvalidArgument,
                    $"Distribution source group '{value}' is not configured.",
                    "Use 'eidosup source add <name> <descriptor>' before selecting the group.");
            }

            return entries;
        }
    }

    public static bool IsValidName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
        value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static async Task WriteAsync(
        ToolInstallLayout layout,
        DistributionSourceCatalog catalog,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(layout.StateDirectory);
        var path = Path.Combine(layout.StateDirectory, FileName);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, catalog, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static EidosupException Invalid(string path, Exception? inner = null) => new(
        EidosupErrorCode.StateCorrupt,
        EidosupExitCodes.StateCorrupt,
        $"Distribution source catalog '{path}' is invalid.",
        "Repair or restore sources.json before using a named source group.",
        inner);
}
