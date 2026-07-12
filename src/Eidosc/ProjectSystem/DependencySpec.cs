using Eidosc.Pipeline;

namespace Eidosc.ProjectSystem;

public sealed record DependencySpec
{
    public string? Path { get; init; }
    public string? Git { get; init; }
    public string? Tag { get; init; }
    public string? Branch { get; init; }
    public string? Commit { get; init; }
    public string? Version { get; init; }
    public string? Target { get; init; }

    public DependencySourceKind SourceKind
    {
        get
        {
            if (Path != null) return DependencySourceKind.Path;
            if (Git != null) return DependencySourceKind.Git;
            if (Version != null) return DependencySourceKind.Version;
            return DependencySourceKind.Unknown;
        }
    }

    public string DisplayName
    {
        get
        {
            return SourceKind switch
            {
                DependencySourceKind.Path => $"path:{Path}",
                DependencySourceKind.Git => Tag != null ? $"{Git}#{Tag}"
                    : Branch != null ? $"{Git}#{Branch}"
                    : Commit != null ? $"{Git}@{Commit[..Math.Min(8, Commit.Length)]}"
                    : Git ?? "",
                DependencySourceKind.Registry => Version ?? "",
                DependencySourceKind.Version => Version ?? "",
                _ => "<unknown>"
            };
        }
    }
}

public enum DependencySourceKind
{
    Unknown,
    Path,
    Git,
    Registry,
    Version
}
