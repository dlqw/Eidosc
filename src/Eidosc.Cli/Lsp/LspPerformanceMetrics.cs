namespace Eidosc.Cli.Lsp;

internal sealed record LspPerformanceSnapshot(
    long SnapshotCompileCount,
    long SnapshotCacheHitCount,
    int DirectoryScanCount,
    double SnapshotAccessP50Milliseconds,
    double SnapshotAccessP95Milliseconds,
    double SnapshotAccessP99Milliseconds,
    double DependencyFingerprintTotalMilliseconds,
    int SnapshotAccessSampleCount);

internal sealed class LspPerformanceMetrics
{
    private readonly object _sync = new();
    private readonly List<double> _snapshotAccessMilliseconds = [];
    private long _snapshotCompileCount;
    private long _snapshotCacheHitCount;
    private long _dependencyFingerprintTicks;

    public void RecordCompile() => Interlocked.Increment(ref _snapshotCompileCount);

    public void RecordCacheHit() => Interlocked.Increment(ref _snapshotCacheHitCount);

    public void RecordSnapshotAccess(TimeSpan elapsed)
    {
        lock (_sync)
        {
            _snapshotAccessMilliseconds.Add(elapsed.TotalMilliseconds);
        }
    }

    public void RecordDependencyFingerprint(TimeSpan elapsed) =>
        Interlocked.Add(ref _dependencyFingerprintTicks, elapsed.Ticks);

    public LspPerformanceSnapshot CreateSnapshot(int directoryScanCount)
    {
        double[] samples;
        lock (_sync)
        {
            samples = [.. _snapshotAccessMilliseconds.Order()];
        }

        return new LspPerformanceSnapshot(
            SnapshotCompileCount: Interlocked.Read(ref _snapshotCompileCount),
            SnapshotCacheHitCount: Interlocked.Read(ref _snapshotCacheHitCount),
            DirectoryScanCount: directoryScanCount,
            SnapshotAccessP50Milliseconds: Percentile(samples, 0.50),
            SnapshotAccessP95Milliseconds: Percentile(samples, 0.95),
            SnapshotAccessP99Milliseconds: Percentile(samples, 0.99),
            DependencyFingerprintTotalMilliseconds: TimeSpan.FromTicks(
                Interlocked.Read(ref _dependencyFingerprintTicks)).TotalMilliseconds,
            SnapshotAccessSampleCount: samples.Length);
    }

    private static double Percentile(IReadOnlyList<double> orderedSamples, double percentile)
    {
        if (orderedSamples.Count == 0)
        {
            return 0;
        }

        var index = Math.Clamp(
            (int)Math.Ceiling(orderedSamples.Count * percentile) - 1,
            0,
            orderedSamples.Count - 1);
        return orderedSamples[index];
    }
}
