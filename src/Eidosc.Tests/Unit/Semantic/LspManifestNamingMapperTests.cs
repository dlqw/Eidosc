using Eidosc.Cli.Lsp;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class LspManifestNamingMapperTests
{
    [Fact]
    public void Map_ProjectsManifestNamingDiagnosticsToTomlRanges()
    {
        const string text = """
[package]
name = "Acme.Core"
version = "0.7.0-alpha.1"

[dependencies]
Raylib = "0.1.0"
""";

        var diagnostics = LspManifestNamingMapper.Map(text, Path.Combine(Path.GetTempPath(), "eidos.toml"));

        var package = Assert.Single(diagnostics, diagnostic => diagnostic.Code == "S1107");
        Assert.Equal(1, package.Range.Start.Line);
        Assert.Equal(8, package.Range.Start.Character);
        Assert.Equal("manifest", package.Data!["naming.category"]);
        Assert.Equal("acme.core", package.Data["naming.expected"]);

        var alias = Assert.Single(diagnostics, diagnostic => diagnostic.Code == "S1108");
        Assert.Equal(5, alias.Range.Start.Line);
        Assert.Equal(0, alias.Range.Start.Character);
        Assert.Equal("raylib", alias.Data!["naming.expected"]);

        var actions = LspManifestNamingMapper.MapCodeActions(
            text,
            Path.Combine(Path.GetTempPath(), "eidos.toml"),
            "file:///tmp/eidos.toml",
            new LspRange
            {
                Start = new LspPosition { Line = 1, Character = 0 },
                End = new LspPosition { Line = 1, Character = 30 }
            });
        var action = Assert.Single(actions);
        Assert.Equal("Rename Acme.Core to acme.core", action.Title);
        Assert.Equal("acme.core", action.Edit!.Changes["file:///tmp/eidos.toml"][0].NewText);
    }

    [Fact]
    public void Map_InvalidToml_DoesNotThrowOrInventStyleDiagnostics()
    {
        var diagnostics = LspManifestNamingMapper.Map("[package\nname =", Path.Combine(Path.GetTempPath(), "eidos.toml"));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void MapCodeActions_DependencyAlias_UsesWorkspaceSemanticRenameAcrossSourceFiles()
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            $"eidos-lsp-alias-rename-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(tempDir, "src");
        Directory.CreateDirectory(sourceDir);
        var manifestPath = Path.Combine(tempDir, "eidos.toml");
        var sourcePath = Path.Combine(sourceDir, "main.eidos");
        var manifest = """
            manifestSchema = 3
            sourceRoots = ["src"]

            [language]
            version = "0.7.0-alpha.1"

            [package]
            name = "dev.eidos.app"
            version = "0.1.0"

            [dependencies]
            RayLib = "1.0.0"
            """;
        var source = "import RayLib.graphics\nvalue :: RayLib.graphics.Color = RayLib.graphics.default_color;\n";
        File.WriteAllText(manifestPath, manifest);
        File.WriteAllText(sourcePath, source);

        try
        {
            var diagnostics = LspManifestNamingMapper.Map(manifest, manifestPath);
            var alias = Assert.Single(diagnostics, diagnostic => diagnostic.Code == "S1108");
            var actions = LspManifestNamingMapper.MapCodeActions(
                manifest,
                manifestPath,
                new Uri(manifestPath).AbsoluteUri,
                alias.Range);

            var action = Assert.Single(actions);
            var sourceUri = new Uri(sourcePath).AbsoluteUri;
            Assert.Contains(sourceUri, action.Edit!.Changes.Keys);
            Assert.Contains(
                action.Edit.Changes[sourceUri],
                edit => edit.NewText == "ray_lib");
            Assert.Contains(
                action.Edit.Changes[new Uri(manifestPath).AbsoluteUri],
                edit => edit.NewText == "ray_lib");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void MapCodeActions_DependencyAlias_UsesUnsavedSourceDocumentText()
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            $"eidos-lsp-unsaved-alias-rename-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(tempDir, "src");
        Directory.CreateDirectory(sourceDir);
        var manifestPath = Path.Combine(tempDir, "eidos.toml");
        var sourcePath = Path.Combine(sourceDir, "main.eidos");
        var manifest = """
            manifestSchema = 3
            sourceRoots = ["src"]

            [language]
            version = "0.7.0-alpha.1"

            [package]
            name = "dev.eidos.app"
            version = "0.1.0"

            [dependencies]
            RayLib = "1.0.0"
            """;
        const string savedSource = "value :: Int = 0;\n";
        const string unsavedSource = "// unsaved import\nimport RayLib.graphics\nvalue :: Int = 0;\n";
        File.WriteAllText(manifestPath, manifest);
        File.WriteAllText(sourcePath, savedSource);

        try
        {
            var diagnostics = LspManifestNamingMapper.Map(manifest, manifestPath);
            var alias = Assert.Single(diagnostics, diagnostic => diagnostic.Code == "S1108");
            var actions = LspManifestNamingMapper.MapCodeActions(
                manifest,
                manifestPath,
                new Uri(manifestPath).AbsoluteUri,
                alias.Range,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [sourcePath] = unsavedSource
                });

            var action = Assert.Single(actions);
            var sourceEdit = Assert.Single(
                action.Edit!.Changes[new Uri(sourcePath).AbsoluteUri]);
            Assert.Equal("ray_lib", sourceEdit.NewText);
            Assert.Equal(1, sourceEdit.Range.Start.Line);
            Assert.Equal(7, sourceEdit.Range.Start.Character);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void MapCodeActions_ModuleFile_ReturnsTextAndRenameFileOperations()
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            $"eidos-lsp-module-rename-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(tempDir, "src");
        Directory.CreateDirectory(sourceDir);
        var manifestPath = Path.Combine(tempDir, "eidos.toml");
        var modulePath = Path.Combine(sourceDir, "UserProfile.eidos");
        var mainPath = Path.Combine(sourceDir, "main.eidos");
        const string manifest = """
            manifestSchema = 3
            sourceRoots = ["src"]

            [language]
            version = "0.7.0-alpha.1"

            [package]
            name = "dev.eidos.app"
            version = "0.1.0"
            """;
        File.WriteAllText(manifestPath, manifest);
        File.WriteAllText(modulePath, "UserProfile :: module {}");
        File.WriteAllText(mainPath, "import UserProfile\n");

        try
        {
            var diagnostics = LspManifestNamingMapper.Map(manifest, manifestPath);
            var module = Assert.Single(diagnostics, diagnostic => diagnostic.Code == "S1105");
            var action = Assert.Single(LspManifestNamingMapper.MapCodeActions(
                manifest,
                manifestPath,
                new Uri(manifestPath).AbsoluteUri,
                module.Range));

            Assert.Empty(action.Edit!.Changes);
            var documentChanges = action.Edit.DocumentChanges!;
            Assert.Empty(documentChanges.OfType<LspTextDocumentEdit>());
            var rename = Assert.Single(documentChanges.OfType<LspRenameFile>());
            Assert.Equal(new Uri(modulePath).AbsoluteUri, rename.OldUri);
            Assert.Equal(
                new Uri(Path.Combine(sourceDir, "user_profile.eidos")).AbsoluteUri,
                rename.NewUri);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void MapCodeActions_DependencyAlias_IncludesNewUnsavedSourceDocument()
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            $"eidos-lsp-new-unsaved-alias-rename-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(tempDir, "src");
        Directory.CreateDirectory(sourceDir);
        var manifestPath = Path.Combine(tempDir, "eidos.toml");
        var sourcePath = Path.Combine(sourceDir, "new_file.eidos");
        var manifest = """
            manifestSchema = 3
            sourceRoots = ["src"]

            [language]
            version = "0.7.0-alpha.1"

            [package]
            name = "dev.eidos.app"
            version = "0.1.0"

            [dependencies]
            RayLib = "1.0.0"
            """;
        const string unsavedSource = "import RayLib.graphics\n";
        File.WriteAllText(manifestPath, manifest);

        try
        {
            var diagnostics = LspManifestNamingMapper.Map(manifest, manifestPath);
            var alias = Assert.Single(diagnostics, diagnostic => diagnostic.Code == "S1108");
            var actions = LspManifestNamingMapper.MapCodeActions(
                manifest,
                manifestPath,
                new Uri(manifestPath).AbsoluteUri,
                alias.Range,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [sourcePath] = unsavedSource
                });

            var action = Assert.Single(actions);
            var sourceEdit = Assert.Single(
                action.Edit!.Changes[new Uri(sourcePath).AbsoluteUri]);
            Assert.Equal("ray_lib", sourceEdit.NewText);
            Assert.Equal(0, sourceEdit.Range.Start.Line);
            Assert.Equal(7, sourceEdit.Range.Start.Character);
            Assert.False(File.Exists(sourcePath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
