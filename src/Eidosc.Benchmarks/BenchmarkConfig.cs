using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;

namespace Eidosc.Benchmarks;

public sealed class BenchmarkConfig : ManualConfig
{
    public BenchmarkConfig()
    {
        AddJob(Job.ShortRun
            .WithId("short")
            .WithWarmupCount(3)
            .WithIterationCount(8));
        AddDiagnoser(MemoryDiagnoser.Default);
    }
}
