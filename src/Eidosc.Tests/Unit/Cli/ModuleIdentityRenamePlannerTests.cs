using Eidosc.ProjectSystem;
using Eidosc.Semantic;

namespace Eidosc.Tests.Unit.Cli;

public sealed class ModuleIdentityRenamePlannerTests
{
    [Fact]
    public void CreatePlan_NormalizesPhysicalModuleIdentityAndAllSemanticPaths()
    {
        var tempDir = CreateProject();
        var oldModulePath = Path.Combine(tempDir, "src", "HttpAPI", "UserProfile.eidos");
        var newModulePath = Path.Combine(tempDir, "src", "http_api", "user_profile.eidos");
        Directory.CreateDirectory(Path.GetDirectoryName(oldModulePath)!);
        File.WriteAllText(oldModulePath, """
            HttpAPI.UserProfile :: module {
                User :: type { value :: Int }
            }
            """);
        File.WriteAllText(Path.Combine(tempDir, "src", "main.eidos"), """
            import HttpAPI.UserProfile

            keep :: HttpAPI.UserProfile.User -> HttpAPI.UserProfile.User
            {
                value => value
            }

            // HttpAPI.UserProfile is not a module reference.
            label :: String = "HttpAPI.UserProfile";
            """);
        File.AppendAllText(Path.Combine(tempDir, "eidos.toml"), """

            [[targets]]
            name = "library"
            entry = "src/HttpAPI/UserProfile.eidos"
            kind = "library"
            """);

        try
        {
            var plan = ModuleIdentityRenamePlanner.CreatePlan(tempDir, includePathDependencies: false);

            Assert.True(plan.CanApply, string.Join(Environment.NewLine, plan.Diagnostics));
            Assert.Equal("ready", plan.Status);
            var move = Assert.Single(plan.Packages.Single().Moves);
            Assert.Equal(Path.GetFullPath(oldModulePath), move.SourcePath);
            Assert.Equal(Path.GetFullPath(newModulePath), move.DestinationPath);

            ModuleIdentityRenamePlanner.ApplyPlan(plan);

            Assert.False(File.Exists(oldModulePath));
            Assert.True(File.Exists(newModulePath));
            Assert.StartsWith(
                "HttpApi.UserProfile :: module",
                File.ReadAllText(newModulePath),
                StringComparison.Ordinal);
            var main = File.ReadAllText(Path.Combine(tempDir, "src", "main.eidos"));
            Assert.Contains("import HttpApi.UserProfile", main, StringComparison.Ordinal);
            Assert.Contains("HttpApi.UserProfile.User", main, StringComparison.Ordinal);
            Assert.Contains("// HttpAPI.UserProfile is not", main, StringComparison.Ordinal);
            Assert.Contains("\"HttpAPI.UserProfile\"", main, StringComparison.Ordinal);
            Assert.Contains(
                "entry = \"src/http_api/user_profile.eidos\"",
                File.ReadAllText(Path.Combine(tempDir, "eidos.toml")),
                StringComparison.Ordinal);
            Assert.Equal(
                Path.GetFullPath(newModulePath),
                WorkspaceModuleLocator.ResolveImportModuleFile(
                    Path.Combine(tempDir, "src", "main.eidos"),
                    ["HttpApi", "UserProfile"]));
            Assert.Equal(
                "HttpApi/UserProfile",
                WorkspaceModuleLocator.TryGetModulePathFromRoot(
                    Path.Combine(tempDir, "src"),
                    newModulePath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CreatePlan_NormalizedDestinationCollision_BlocksWithoutWriting()
    {
        var tempDir = CreateProject();
        var sourceRoot = Path.Combine(tempDir, "src");
        var first = Path.Combine(sourceRoot, "UserProfile.eidos");
        var second = Path.Combine(sourceRoot, "user_profile.eidos");
        File.WriteAllText(first, "UserProfile :: module {}");
        File.WriteAllText(second, "UserProfile :: module {}");

        try
        {
            var plan = ModuleIdentityRenamePlanner.CreatePlan(tempDir, includePathDependencies: false);

            Assert.False(plan.CanApply);
            Assert.Equal("blocked", plan.Status);
            Assert.Contains(
                plan.Diagnostics,
                diagnostic => diagnostic.Contains("destination", StringComparison.Ordinal));
            Assert.Throws<InvalidOperationException>(() => ModuleIdentityRenamePlanner.ApplyPlan(plan));
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(second));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ApplyPlan_ChangedSource_RejectsAllMovesAndEdits()
    {
        var tempDir = CreateProject();
        var oldModulePath = Path.Combine(tempDir, "src", "UserProfile.eidos");
        File.WriteAllText(oldModulePath, "UserProfile :: module {}");
        var manifestPath = Path.Combine(tempDir, "eidos.toml");
        var originalManifest = File.ReadAllText(manifestPath);

        try
        {
            var plan = ModuleIdentityRenamePlanner.CreatePlan(tempDir, includePathDependencies: false);
            File.AppendAllText(oldModulePath, "\n# changed");

            Assert.Throws<InvalidOperationException>(() => ModuleIdentityRenamePlanner.ApplyPlan(plan));
            Assert.True(File.Exists(oldModulePath));
            Assert.False(File.Exists(Path.Combine(tempDir, "src", "user_profile.eidos")));
            Assert.Equal(originalManifest, File.ReadAllText(manifestPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CreatePlan_DirectoryModule_PreservesModFileConvention()
    {
        var tempDir = CreateProject();
        var oldPath = Path.Combine(tempDir, "src", "HttpAPI", "mod.eidos");
        Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!);
        File.WriteAllText(oldPath, "HttpAPI :: module {}");
        File.WriteAllText(Path.Combine(tempDir, "src", "main.eidos"), "import HttpAPI\n");

        try
        {
            var plan = ModuleIdentityRenamePlanner.CreatePlan(tempDir, includePathDependencies: false);

            Assert.True(plan.CanApply, string.Join(Environment.NewLine, plan.Diagnostics));
            var move = Assert.Single(plan.Packages.Single().Moves);
            Assert.EndsWith(
                Path.Combine("http_api", "mod.eidos"),
                move.DestinationPath,
                StringComparison.Ordinal);

            ModuleIdentityRenamePlanner.ApplyPlan(plan);

            Assert.True(File.Exists(Path.Combine(tempDir, "src", "http_api", "mod.eidos")));
            Assert.Contains(
                "import HttpApi",
                File.ReadAllText(Path.Combine(tempDir, "src", "main.eidos")),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void MigrateNames_ModulesDryRun_DoesNotWrite()
    {
        var tempDir = CreateProject();
        var oldPath = Path.Combine(tempDir, "src", "UserProfile.eidos");
        File.WriteAllText(oldPath, "UserProfile :: module {}");

        try
        {
            var result = Eidosc.Cli.Commands.Migrate.MigrateNamesCommand.Run(
                new Eidosc.Cli.Commands.Migrate.MigrateNamesOptions
                {
                    Path = tempDir,
                    Modules = true,
                    NoPathDependencies = true,
                    DryRun = true
                });

            Assert.Equal(0, result);
            Assert.True(File.Exists(oldPath));
            Assert.False(File.Exists(Path.Combine(tempDir, "src", "user_profile.eidos")));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string CreateProject()
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            $"eidos-module-identity-rename-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempDir, "src"));
        File.WriteAllText(Path.Combine(tempDir, "eidos.toml"), """
            manifestSchema = 3
            sourceRoots = ["src"]

            [language]
            version = "0.7.0-alpha.1"

            [package]
            name = "dev.eidos.module-rename"
            version = "0.1.0"
            """);
        return tempDir;
    }
}
