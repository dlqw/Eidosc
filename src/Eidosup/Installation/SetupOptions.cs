using Eidosup.Distribution;

namespace Eidosup.Installation;

public sealed class SetupOptions
{
    public string? Version { get; init; }

    public string Repository { get; init; } = "dlqw/Eidosc";

    public string? InstallRoot { get; init; }

    public string? DownloadRoot { get; init; }

    public bool SkipEidosc { get; init; }

    public bool SkipClang { get; init; }

    public bool SkipEnvironmentConfiguration { get; init; }

    public ReleaseChannel Channel { get; init; } = ReleaseChannel.Preview;

    public bool DryRun { get; init; }

    public bool Force { get; init; }
}
