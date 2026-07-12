namespace Eidosup.Distribution;

public interface IEidosReleaseSource : IDisposable
{
    Task<EidosReleaseInfo> ResolveReleaseAsync(
        string? version,
        ReleaseChannel channel,
        CancellationToken cancellationToken);
}
