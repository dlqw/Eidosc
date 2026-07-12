using Eidosc.ProjectSystem;

namespace Eidosc.Cli.Commands.Pkg;

internal static class PkgLockEntryWriter
{
    public static LockedPackage CreateLockedPackage(ResolvedPackage package, string projectDirectory)
    {
        return new LockedPackage
        {
            Source = package.Source switch
            {
                DependencySourceKind.Path => "path",
                DependencySourceKind.Git => "git",
                DependencySourceKind.Registry => "registry",
                DependencySourceKind.Version => "embedded",
                _ => "unknown"
            },
            Path = package.Source == DependencySourceKind.Path
                ? Path.GetRelativePath(projectDirectory, package.ResolvedPath ?? "")
                : null,
            Git = package.GitUrl,
            RegistryName = package.RegistryName,
            RegistryIndex = package.RegistryIndexUrl,
            Commit = package.Commit,
            Tag = package.Tag,
            Branch = package.Branch,
            Version = package.Version,
            ContentHash = package.ContentHash
        };
    }

    public static string DescribeSource(ResolvedPackage package, string projectDirectory)
    {
        return package.Source switch
        {
            DependencySourceKind.Path => Resources.CliMessages.PkgPathSource(
                Path.GetRelativePath(projectDirectory, package.ResolvedPath ?? "")),
            DependencySourceKind.Git => Resources.CliMessages.PkgGitSource(
                $"{package.GitUrl}#{package.Tag ?? package.Branch ?? ShortCommit(package.Commit)}"),
            DependencySourceKind.Registry => Resources.CliMessages.PkgVersionSource(
                $"{package.Version} ({package.GitUrl}#{package.Tag} {ShortCommit(package.Commit)})"),
            DependencySourceKind.Version => Resources.CliMessages.PkgVersionSource(package.Version ?? ""),
            _ => Resources.CliMessages.PkgUnknownSourceKind
        };
    }

    public static string DescribeLockedSource(LockedPackage package)
    {
        return package.Source switch
        {
            "path" => Resources.CliMessages.PkgPathSource(package.Path ?? ""),
            "git" => Resources.CliMessages.PkgGitSource(
                $"{package.Git ?? ""}#{package.Tag ?? package.Branch ?? ShortCommit(package.Commit)}"),
            "registry" => Resources.CliMessages.PkgVersionSource(
                $"{package.Version} ({package.Git ?? ""}#{package.Tag} {ShortCommit(package.Commit)})"),
            "embedded" => $"{Resources.CliMessages.PkgEmbeddedSourceKind}: {package.Version}",
            _ => package.Source
        };
    }

    private static string? ShortCommit(string? commit)
    {
        return commit == null ? null : commit[..Math.Min(8, commit.Length)];
    }
}
