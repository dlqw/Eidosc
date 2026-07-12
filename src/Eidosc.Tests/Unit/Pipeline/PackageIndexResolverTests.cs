using Eidosc.ProjectSystem;
using System.Diagnostics;

namespace Eidosc.Tests.Unit.Pipeline;

public sealed class PackageIndexResolverTests
{
    [Fact]
    public void ResolveVersion_UsesIndexAndSelectsHighestStableTag()
    {
        using var temp = new TempDirectory();
        var packageRepo = CreatePackageRepo(temp.Path, "json", ["v1.0.0", "v1.2.0", "v1.3.0-alpha"]);
        var indexRepo = CreateIndexRepo(temp.Path, packageRepo);
        var previousIndex = Environment.GetEnvironmentVariable(PackageIndexResolver.IndexUrlEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(PackageIndexResolver.IndexUrlEnvironmentVariable, indexRepo);
            var config = new EidosProjectConfiguration
            {
                VersionedDependencies = new Dictionary<string, DependencySpec>(StringComparer.Ordinal)
                {
                    ["Json"] = new() { Version = "^1.0.0" }
                },
                NoImplicitStdlib = true
            };

            var graph = new PackageDependencyResolver(temp.Path).Resolve(config);
            var resolved = graph.Packages["Json"];

            Assert.Equal(DependencySourceKind.Registry, resolved.Source);
            Assert.Equal("1.2.0", resolved.Version);
            Assert.Equal("v1.2.0", resolved.Tag);
            Assert.Equal(packageRepo, resolved.GitUrl);
            Assert.False(string.IsNullOrWhiteSpace(resolved.Commit));
            Assert.Contains(resolved.SourceRoots, Directory.Exists);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PackageIndexResolver.IndexUrlEnvironmentVariable, previousIndex);
        }
    }

    [Fact]
    public void ResolveVersion_AllowsPreReleaseWhenRangeNamesPreRelease()
    {
        using var temp = new TempDirectory();
        var packageRepo = CreatePackageRepo(temp.Path, "json", ["v1.0.0", "v1.3.0-alpha"]);
        var indexRepo = CreateIndexRepo(temp.Path, packageRepo);
        var previousIndex = Environment.GetEnvironmentVariable(PackageIndexResolver.IndexUrlEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(PackageIndexResolver.IndexUrlEnvironmentVariable, indexRepo);
            var resolver = new PackageIndexResolver();

            var resolved = resolver.Resolve("Json", "1.3.0-alpha");

            Assert.Equal("1.3.0-alpha", resolved.Version.ToString());
            Assert.Equal("v1.3.0-alpha", resolved.Tag);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PackageIndexResolver.IndexUrlEnvironmentVariable, previousIndex);
        }
    }

    [Fact]
    public void ResolveVersion_ReportsWhenNoTagSatisfiesRange()
    {
        using var temp = new TempDirectory();
        var packageRepo = CreatePackageRepo(temp.Path, "json", ["v1.0.0"]);
        var indexRepo = CreateIndexRepo(temp.Path, packageRepo);
        var previousIndex = Environment.GetEnvironmentVariable(PackageIndexResolver.IndexUrlEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(PackageIndexResolver.IndexUrlEnvironmentVariable, indexRepo);
            var resolver = new PackageIndexResolver();

            var ex = Assert.Throws<InvalidOperationException>(() => resolver.Resolve("Json", "^2.0.0"));

            Assert.Contains("no git tag satisfying version range", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PackageIndexResolver.IndexUrlEnvironmentVariable, previousIndex);
        }
    }

    [Fact]
    public void ResolveVersion_ReusesRegistryLockWhenRangeStillMatches()
    {
        using var temp = new TempDirectory();
        var packageRepo = CreatePackageRepo(temp.Path, "json", ["v1.0.0", "v1.2.0"]);
        var indexRepo = CreateIndexRepo(temp.Path, packageRepo);
        var previousIndex = Environment.GetEnvironmentVariable(PackageIndexResolver.IndexUrlEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(PackageIndexResolver.IndexUrlEnvironmentVariable, indexRepo);
            var config = new EidosProjectConfiguration
            {
                VersionedDependencies = new Dictionary<string, DependencySpec>(StringComparer.Ordinal)
                {
                    ["Json"] = new() { Version = "^1.0.0" }
                },
                NoImplicitStdlib = true
            };
            var resolver = new PackageDependencyResolver(temp.Path);
            var first = resolver.Resolve(config).Packages["Json"];
            var lockFile = new EidosLockFile();
            lockFile.Packages["Json"] = new LockedPackage
            {
                Source = "registry",
                RegistryName = "Json",
                RegistryIndex = indexRepo,
                Git = first.GitUrl,
                Tag = first.Tag,
                Commit = first.Commit,
                Version = first.Version
            };

            var second = resolver.Resolve(config, lockFile).Packages["Json"];

            Assert.Equal(first.Version, second.Version);
            Assert.Equal(first.Tag, second.Tag);
            Assert.Equal(first.Commit, second.Commit);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PackageIndexResolver.IndexUrlEnvironmentVariable, previousIndex);
        }
    }

    private static string CreatePackageRepo(string root, string name, IReadOnlyList<string> tags)
    {
        var repo = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        File.WriteAllText(Path.Combine(repo, EidosProjectConfigurationLoader.DefaultFileName),
            """
            manifestSchema = 3

            [package]
            name = "dev.eidos.test"
            version = "0.1.0"
            """);
        File.WriteAllText(Path.Combine(repo, "src", "Lib.eidos"), "Lib :: module { val answer = 42; }");
        RunGit(repo, "init");
        RunGit(repo, "config", "user.name", "Eidos Test");
        RunGit(repo, "config", "user.email", "eidos-test@example.invalid");
        RunGit(repo, "add", ".");
        RunGit(repo, "commit", "-m", "initial");
        foreach (var tag in tags)
            RunGit(repo, "tag", tag);
        return repo;
    }

    private static string CreateIndexRepo(string root, string packageRepo)
    {
        var repo = Path.Combine(root, "index");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "packages.toml"),
            $$"""
            [packages.Json]
            repo = "{{packageRepo.Replace("\\", "\\\\", StringComparison.Ordinal)}}"
            defaultTarget = "lib"
            """);
        RunGit(repo, "init");
        RunGit(repo, "config", "user.name", "Eidos Test");
        RunGit(repo, "config", "user.email", "eidos-test@example.invalid");
        RunGit(repo, "add", ".");
        RunGit(repo, "commit", "-m", "index");
        return repo;
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git.");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30000);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(" ", arguments)} failed: {stderr}");
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"eidosc_package_index_{Guid.NewGuid():N}");

        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
