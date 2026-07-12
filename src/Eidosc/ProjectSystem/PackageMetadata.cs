using Eidosc.Pipeline;

namespace Eidosc.ProjectSystem;

public sealed record PackageMetadata
{
    public string Name { get; init; } = "";
    public SemanticVersion Version { get; init; } = new(0, 1, 0);
    public string? Description { get; init; }
    public List<string> Authors { get; init; } = [];
    public string? License { get; init; }
    public List<string> Keywords { get; init; } = [];

    public bool IsValid => !string.IsNullOrWhiteSpace(Name) && Name.Contains('.');
}
