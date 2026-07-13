using Eidosc.Cli.Commands.Migrate;

namespace Eidosc.Tests.Unit.Cli;

public sealed class MigrateManifestCommandTests
{
    [Fact]
    public void MigrateText_LegacyFields_ProducesSchema3LanguageVersion()
    {
        var migrated = MigrateManifestCommand.MigrateText(
            """
            eidosVersion = 2
            sourceRoots = ["src"]

            [language]
            syntax = "legacy"

            [package]
            name = "dev.eidos.app"
            version = "0.1.0"
            """);

        Assert.Contains("manifestSchema = 3", migrated, StringComparison.Ordinal);
        Assert.Contains("[language]", migrated, StringComparison.Ordinal);
        Assert.Contains("version = \"0.5.0-alpha.1\"", migrated, StringComparison.Ordinal);
        Assert.DoesNotContain("eidosVersion", migrated, StringComparison.Ordinal);
        Assert.DoesNotContain("syntax =", migrated, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrateText_ManifestWithoutLanguage_AddsCurrentLanguage()
    {
        var migrated = MigrateManifestCommand.MigrateText(
            """
            [package]
            name = "dev.eidos.app"
            version = "0.1.0"
            """);

        Assert.StartsWith("manifestSchema = 3", migrated, StringComparison.Ordinal);
        Assert.Contains("[language]", migrated, StringComparison.Ordinal);
        Assert.Contains("version = \"0.5.0-alpha.1\"", migrated, StringComparison.Ordinal);
    }
}
