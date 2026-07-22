using Eidosc.ProjectSystem;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Unit.ProjectSystem;

public sealed class ManifestNamingRulesTests
{
    [Theory]
    [InlineData("dev.eidos.http-client")]
    [InlineData("std")]
    [InlineData("demo2")]
    public void PackageId_AcceptsLowerKebabSegments(string value)
    {
        Assert.True(ManifestNamingRules.IsPackageId(value));
    }

    [Theory]
    [InlineData("Dev.eidos.http-client")]
    [InlineData("dev.eidos.http_client")]
    [InlineData("dev.eidos.-client")]
    [InlineData("dev.eidos.client-")]
    public void PackageId_RejectsNonCanonicalSpelling(string value)
    {
        Assert.False(ManifestNamingRules.IsPackageId(value));
    }

    [Theory]
    [InlineData("raylib")]
    [InlineData("crypto_a")]
    [InlineData("http2_client")]
    public void DependencyAlias_UsesLowerSnakeCase(string value)
    {
        Assert.True(ManifestNamingRules.IsDependencyAlias(value));
    }

    [Theory]
    [InlineData("Raylib", "raylib")]
    [InlineData("http-client", "http_client")]
    [InlineData("HTTPClient", "http_client")]
    [InlineData("crypto.a", "crypto_a")]
    public void DependencyAlias_NormalizationIsDeterministic(string source, string expected)
    {
        Assert.Equal(expected, ManifestNamingRules.NormalizeDependencyAlias(source));
    }

    [Theory]
    [InlineData("http_client")]
    [InlineData("std")]
    public void ModuleFileName_UsesLowerSnakeCase(string stem)
    {
        Assert.True(ManifestNamingRules.IsModuleFileName($"{stem}.eidos"));
    }

    [Theory]
    [InlineData("HttpClient.eidos")]
    [InlineData("http-client.eidos")]
    [InlineData("http_client.txt")]
    public void ModuleFileName_RejectsLegacySpelling(string value)
    {
        Assert.False(ManifestNamingRules.IsModuleFileName(value));
    }

    [Fact]
    public void Analyze_ReportsPackageDependencyModuleAndDirectoryNaming()
    {
        using var workspace = TestTempWorkspace.Create("manifest_naming");
        Directory.CreateDirectory(Path.Combine(workspace.Root, "src", "HttpClient"));
        File.WriteAllText(Path.Combine(workspace.Root, "src", "HttpClient", "MainModule.eidos"), "main :: Unit -> Unit { _ => () }");
        var manifest = EidosProjectManifestDocument.Parse("""
            sourceRoots = ["src"]

            [package]
            name = "Dev.eidos.http_client"
            version = "0.1.0"

            [dependencies]
            HttpClient = { path = "../http-client" }
            """, "eidos.toml");

        var diagnostics = ManifestNamingRules.Analyze(manifest, workspace.Root);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "S1107");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "S1108");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "S1105");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "S1110");
    }
}
