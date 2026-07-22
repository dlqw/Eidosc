using Eidosc.ProjectSystem;

namespace Eidosc.Tests.Unit.ProjectSystem;

public sealed class EidosProjectLanguageVersionTests
{
    [Fact]
    public void LoadFromPath_ManifestWithoutLanguageVersion_DefaultsToCurrentLanguage()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(tempDir, EidosProjectConfigurationLoader.DefaultFileName),
                """
                manifestSchema = 3
                sourceRoots = ["src"]
                """);

            var loaded = EidosProjectConfigurationLoader.LoadFromPath(tempDir);

            Assert.Equal(EidosLanguageVersions.Current, loaded.Configuration.LanguageVersion);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void LoadFromPath_ManifestWithCurrentVersion_LoadsLanguageVersion()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(tempDir, EidosProjectConfigurationLoader.DefaultFileName),
                """
                manifestSchema = 3

                [language]
                version = "0.7.0-alpha.1"
                """);

            var loaded = EidosProjectConfigurationLoader.LoadFromPath(tempDir);

            Assert.Equal(EidosLanguageVersions.Current, loaded.Configuration.LanguageVersion);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void ToToml_LanguageVersion_WritesLanguageSectionAndSchema()
    {
        var manifest = new EidosProjectManifestDocument
        {
            Language = new EidosProjectLanguageManifestDocument
            {
                Version = EidosLanguageVersions.Current
            }
        };

        var text = manifest.ToToml();

        Assert.Contains("[language]", text, StringComparison.Ordinal);
        Assert.Contains("version = \"0.7.0-alpha.1\"", text, StringComparison.Ordinal);
        Assert.Contains("manifestSchema = 3", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ToToml_ExplicitManifestSchema_WritesTopLevelVersion()
    {
        var manifest = new EidosProjectManifestDocument
        {
            ManifestSchema = 3
        };

        var text = manifest.ToToml();

        Assert.Contains("manifestSchema = 3", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("eidosVersion = 2")]
    [InlineData("[language]\nsyntax = \"legacy\"")]
    public void LoadFromPath_RemovedVersionFields_ReportsMigrationError(string manifestText)
    {
        var tempDir = CreateTempDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(tempDir, EidosProjectConfigurationLoader.DefaultFileName),
                manifestText);

            var error = Assert.Throws<InvalidOperationException>(
                () => EidosProjectConfigurationLoader.LoadFromPath(tempDir));

            Assert.Contains("Removed manifest version field", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void LoadFromPath_InvalidPackageVersion_DoesNotSilentlyNormalize()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(tempDir, EidosProjectConfigurationLoader.DefaultFileName),
                """
                manifestSchema = 3

                [language]
                version = "0.7.0-alpha.1"

                [package]
                name = "dev.eidos.invalid"
                version = "1.2"
                """);

            var error = Assert.Throws<InvalidOperationException>(
                () => EidosProjectConfigurationLoader.LoadFromPath(tempDir));

            Assert.Contains("valid SemVer 2.0.0", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    private static string CreateTempDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_syntax_version_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    private static void DeleteTempDirectory(string tempDir)
    {
        try
        {
            Directory.Delete(tempDir, recursive: true);
        }
        catch
        {
        }
    }
}
