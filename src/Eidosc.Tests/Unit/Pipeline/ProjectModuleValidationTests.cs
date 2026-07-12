using Eidosc.Symbols;
using Eidosc.ProjectSystem;
using Eidosc.Pipeline;

namespace Eidosc.Tests.Unit.Pipeline;

public class ProjectModuleValidationTests
{
    [Fact]
    public void CompilationPipeline_SameFileNestedModuleImport_ResolvesKnownModuleBeforeFilesystemLookup()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_project_model_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var entryFile = Path.Combine(tempDir, "main.eidos");
        const string source = """
Cap.Io :: module
{
    export Writer :: effect;

    export write :: String -> Int need Writer
    {
        _ => 0
    }
}

Demo.Main :: module
{
    import Cap.Io

    run :: String -> Int need Io::Writer
    {
        text => Io::write(text)
    }
}
""";

        File.WriteAllText(entryFile, source);

        try
        {
            var result = new CompilationPipeline(source, new CompilationOptions
            {
                InputFile = entryFile,
                StopAtPhase = CompilationPhase.Namer,
                NoImplicitPrelude = true,
                UseColors = false
            }).Run();

            Assert.True(result.Success, FormatDiagnostics(result));
            Assert.NotNull(result.SymbolTable);
            Assert.True(result.SymbolTable.Modules.ModulePaths.ContainsKey("Cap/Io"));
            Assert.True(result.SymbolTable.Modules.ModulePaths.ContainsKey("Demo/Main"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CompilationPipeline_SameFileModuleReexports_ResolveKnownModulesBeforeFilesystemLookup()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_project_model_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var entryFile = Path.Combine(tempDir, "main.eidos");
        const string source = """
Demo.Base :: module
{
    export answer :: Int = 40;

    export add_two :: Int -> Int
    {
        x => x + 2
    }

    export Writer :: effect;

    export write :: String -> Int need Writer
    {
        _ => 0
    }
}

Demo.Facade :: module
{
    export BaseApi :: import Demo.Base;
    export import Demo.Base::{Writer as W, write}
}

Demo.Main :: module
{
    import Demo.Facade
    import Demo.Facade::{W}

    run :: String -> Int need Facade::W
    {
        text => Facade::write(text)
    }
}
""";

        File.WriteAllText(entryFile, source);

        try
        {
            var result = new CompilationPipeline(source, new CompilationOptions
            {
                InputFile = entryFile,
                StopAtPhase = CompilationPhase.Namer,
                NoImplicitPrelude = true,
                UseColors = false
            }).Run();

            Assert.True(result.Success, FormatDiagnostics(result));
            Assert.NotNull(result.SymbolTable);
            Assert.True(result.SymbolTable.Modules.ModulePaths.ContainsKey("Demo/Base"));
            Assert.True(result.SymbolTable.Modules.ModulePaths.ContainsKey("Demo/Facade"));
            Assert.True(result.SymbolTable.Modules.ModulePaths.ContainsKey("Demo/Main"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CompilationPipeline_ImportedFileWithMismatchedModuleDeclaration_ReportsProjectModelError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_project_model_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var projectFile = Path.Combine(tempDir, "eidos.toml");
        var appDir = Path.Combine(tempDir, "App");
        var moduleDir = Path.Combine(tempDir, "src", "Cap");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(moduleDir);

        var moduleFile = Path.Combine(moduleDir, "Io.eidos");
        var entryFile = Path.Combine(appDir, "main.eidos");

        const string moduleSource = """
Other.Io :: module
{
    id :: Int -> Int
    {
        x => x
    }
}
""";

        const string entrySource = """
import Cap.Io

main :: Unit -> Int
{
    _ => Io::id(0)
}
""";

        File.WriteAllText(projectFile, """sourceRoots = ["src"]""");
        File.WriteAllText(moduleFile, moduleSource);
        File.WriteAllText(entryFile, entrySource);

        try
        {
            var result = new CompilationPipeline(entrySource, new CompilationOptions
            {
                InputFile = entryFile,
                StopAtPhase = CompilationPhase.Namer,
                UseColors = false
            }).Run();

            Assert.False(result.Success);
            Assert.Contains(
                result.Diagnostics,
                diagnostic => diagnostic.Code == "E3000" &&
                              diagnostic.Message.Contains("does not declare module 'Cap/Io'", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CompilationPipeline_ImportedFileWithDuplicateModulePathDeclarations_ReportsProjectModelError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_project_model_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var projectFile = Path.Combine(tempDir, "eidos.toml");
        var appDir = Path.Combine(tempDir, "App");
        var moduleDir = Path.Combine(tempDir, "src", "Cap");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(moduleDir);

        var moduleFile = Path.Combine(moduleDir, "Io.eidos");
        var entryFile = Path.Combine(appDir, "main.eidos");

        const string moduleSource = """
Cap.Io :: module
{
    first :: Int -> Int
    {
        x => x
    }
}

Cap.Io :: module
{
    second :: Int -> Int
    {
        x => x + 1
    }
}
""";

        const string entrySource = """
import Cap.Io

main :: Unit -> Int
{
    _ => Io::first(0)
}
""";

        File.WriteAllText(projectFile, """sourceRoots = ["src"]""");
        File.WriteAllText(moduleFile, moduleSource);
        File.WriteAllText(entryFile, entrySource);

        try
        {
            var result = new CompilationPipeline(entrySource, new CompilationOptions
            {
                InputFile = entryFile,
                StopAtPhase = CompilationPhase.Namer,
                UseColors = false
            }).Run();

            Assert.False(result.Success);
            Assert.Contains(
                result.Diagnostics,
                diagnostic => diagnostic.Code == "E3000" &&
                              diagnostic.Message.Contains("Duplicate module path 'Cap/Io'", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CompilationPipeline_UnresolvedImport_ReportsSearchedProjectRoots()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_project_model_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var projectFile = Path.Combine(tempDir, "eidos.toml");
        var appDir = Path.Combine(tempDir, "App");
        var sourceRoot = Path.Combine(tempDir, "src");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(sourceRoot);

        var entryFile = Path.Combine(appDir, "main.eidos");
        const string entrySource = """
import Cap.Missing

main :: Unit -> Int
{
    _ => 0
}
""";

        File.WriteAllText(projectFile, """sourceRoots = ["src"]""");
        File.WriteAllText(entryFile, entrySource);

        try
        {
            var result = new CompilationPipeline(entrySource, new CompilationOptions
            {
                InputFile = entryFile,
                StopAtPhase = CompilationPhase.Namer,
                UseColors = false
            }).Run();

            var diagnostic = Assert.Single(
                result.Diagnostics,
                diagnostic => diagnostic.Code == "E3000" &&
                              diagnostic.Message.Contains(
                                  "Unable to resolve imported module 'Cap::Missing'",
                                  StringComparison.Ordinal));

            Assert.Contains(diagnostic.Notes, note => note == $"entry file: {entryFile}");
            Assert.Contains(diagnostic.Notes, note => note == $"searched root: {appDir}");
            Assert.Contains(diagnostic.Notes, note => note == $"searched root: {sourceRoot}");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CompilationPipeline_MultipleSourceRoots_ResolvesImportedModule()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_project_model_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var projectFile = Path.Combine(tempDir, "eidos.toml");
        var appDir = Path.Combine(tempDir, "App");
        var generatedModuleDir = Path.Combine(tempDir, "generated", "Cap");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(generatedModuleDir);

        var moduleFile = Path.Combine(generatedModuleDir, "Io.eidos");
        var entryFile = Path.Combine(appDir, "main.eidos");
        const string moduleSource = """
Cap.Io :: module
{
    export id :: Int -> Int
    {
        x => x
    }
}
""";

        const string entrySource = """
import Cap.Io

main :: Unit -> Int
{
    _ => Io::id(1)
}
""";

        File.WriteAllText(projectFile, """sourceRoots = ["src", "generated"]""");
        File.WriteAllText(moduleFile, moduleSource);
        File.WriteAllText(entryFile, entrySource);

        try
        {
            var result = new CompilationPipeline(entrySource, new CompilationOptions
            {
                InputFile = entryFile,
                StopAtPhase = CompilationPhase.Namer,
                NoImplicitPrelude = true,
                UseColors = false
            }).Run();

            Assert.True(result.Success, FormatDiagnostics(result));
            Assert.NotNull(result.SymbolTable);
            Assert.True(result.SymbolTable.Modules.ModulePaths.ContainsKey("Cap/Io"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CompilationPipeline_DuplicateModuleAcrossSourceRoots_ReportsCandidateFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_project_model_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var projectFile = Path.Combine(tempDir, "eidos.toml");
        var appDir = Path.Combine(tempDir, "App");
        var sourceModuleDir = Path.Combine(tempDir, "src", "Cap");
        var generatedModuleDir = Path.Combine(tempDir, "generated", "Cap");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(sourceModuleDir);
        Directory.CreateDirectory(generatedModuleDir);

        var sourceModuleFile = Path.Combine(sourceModuleDir, "Io.eidos");
        var generatedModuleFile = Path.Combine(generatedModuleDir, "Io.eidos");
        var entryFile = Path.Combine(appDir, "main.eidos");
        const string firstModuleSource = """
Cap.Io :: module
{
    export first :: Int -> Int
    {
        x => x
    }
}
""";

        const string secondModuleSource = """
Cap.Io :: module
{
    export second :: Int -> Int
    {
        x => x
    }
}
""";

        const string entrySource = """
import Cap.Io

main :: Unit -> Int
{
    _ => 0
}
""";

        File.WriteAllText(projectFile, """sourceRoots = ["src", "generated"]""");
        File.WriteAllText(sourceModuleFile, firstModuleSource);
        File.WriteAllText(generatedModuleFile, secondModuleSource);
        File.WriteAllText(entryFile, entrySource);

        try
        {
            var result = new CompilationPipeline(entrySource, new CompilationOptions
            {
                InputFile = entryFile,
                StopAtPhase = CompilationPhase.Namer,
                NoImplicitPrelude = true,
                UseColors = false
            }).Run();

            Assert.False(result.Success);
            var diagnostic = Assert.Single(
                result.Diagnostics,
                diagnostic => diagnostic.Code == "E3000" &&
                              diagnostic.Message.Contains("Duplicate module path 'Cap/Io'", StringComparison.Ordinal));
            Assert.Contains(diagnostic.Notes, note => note == $"file: {sourceModuleFile}");
            Assert.Contains(diagnostic.Notes, note => note == $"file: {generatedModuleFile}");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CompilationPipeline_MultipleImportRoots_ResolvesImportedModule()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_project_model_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var projectFile = Path.Combine(tempDir, "eidos.toml");
        var appDir = Path.Combine(tempDir, "App");
        var sharedModuleDir = Path.Combine(tempDir, "shared_b", "Cap");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(Path.Combine(tempDir, "src"));
        Directory.CreateDirectory(Path.Combine(tempDir, "shared_a"));
        Directory.CreateDirectory(sharedModuleDir);

        var moduleFile = Path.Combine(sharedModuleDir, "Io.eidos");
        var entryFile = Path.Combine(appDir, "main.eidos");
        const string moduleSource = """
Cap.Io :: module
{
    export id :: Int -> Int
    {
        x => x
    }
}
""";

        const string entrySource = """
import Cap.Io

main :: Unit -> Int
{
    _ => Io::id(2)
}
""";

        File.WriteAllText(
            projectFile,
            """
            sourceRoots = ["src"]
            importRoots = ["shared_a", "shared_b"]
            """);
        File.WriteAllText(moduleFile, moduleSource);
        File.WriteAllText(entryFile, entrySource);

        try
        {
            var result = new CompilationPipeline(entrySource, new CompilationOptions
            {
                InputFile = entryFile,
                StopAtPhase = CompilationPhase.Namer,
                NoImplicitPrelude = true,
                UseColors = false
            }).Run();

            Assert.True(result.Success, FormatDiagnostics(result));
            Assert.NotNull(result.SymbolTable);
            Assert.True(result.SymbolTable.Modules.ModulePaths.ContainsKey("Cap/Io"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CompilationPipeline_ExplicitImportRootDuplicateWithSourceRoot_ReportsCandidateFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_project_model_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var projectFile = Path.Combine(tempDir, "eidos.toml");
        var appDir = Path.Combine(tempDir, "App");
        var sourceModuleDir = Path.Combine(tempDir, "src", "Cap");
        var explicitModuleDir = Path.Combine(tempDir, "manual_modules", "Cap");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(sourceModuleDir);
        Directory.CreateDirectory(explicitModuleDir);

        var sourceModuleFile = Path.Combine(sourceModuleDir, "Io.eidos");
        var explicitModuleFile = Path.Combine(explicitModuleDir, "Io.eidos");
        var entryFile = Path.Combine(appDir, "main.eidos");
        const string sourceModule = """
Cap.Io :: module
{
    export first :: Int -> Int
    {
        x => x
    }
}
""";

        const string explicitModule = """
Cap.Io :: module
{
    export second :: Int -> Int
    {
        x => x
    }
}
""";

        const string entrySource = """
import Cap.Io

main :: Unit -> Int
{
    _ => 0
}
""";

        File.WriteAllText(
            projectFile,
            """
            sourceRoots = ["src"]
            importRoots = ["vendor"]
            """);
        File.WriteAllText(sourceModuleFile, sourceModule);
        File.WriteAllText(explicitModuleFile, explicitModule);
        File.WriteAllText(entryFile, entrySource);

        try
        {
            var importResolution = EidosProjectConfigurationLoader.ResolveImportSearchRoots(
                entryFile,
                [Path.Combine(tempDir, "manual_modules")]);
            var result = new CompilationPipeline(entrySource, new CompilationOptions
            {
                InputFile = entryFile,
                ImportSearchRoots = importResolution.EffectiveSearchRoots,
                StopAtPhase = CompilationPhase.Namer,
                NoImplicitPrelude = true,
                UseColors = false
            }).Run();

            Assert.True(importResolution.UsesExplicitImportRoots);
            Assert.False(result.Success);
            var diagnostic = Assert.Single(
                result.Diagnostics,
                diagnostic => diagnostic.Code == "E3000" &&
                              diagnostic.Message.Contains("Duplicate module path 'Cap/Io'", StringComparison.Ordinal));
            Assert.Contains(diagnostic.Notes, note => note == $"file: {sourceModuleFile}");
            Assert.Contains(diagnostic.Notes, note => note == $"file: {explicitModuleFile}");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CompilationPipeline_ProjectDependencyAlias_ResolvesPackageQualifiedImport()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_project_model_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var appProjectDir = Path.Combine(tempDir, "App");
        var sharedProjectDir = Path.Combine(tempDir, "Shared");
        var appModuleDir = Path.Combine(appProjectDir, "src", "App");
        var sharedModuleDir = Path.Combine(sharedProjectDir, "src", "Shared");
        Directory.CreateDirectory(appModuleDir);
        Directory.CreateDirectory(sharedModuleDir);

        var appEntry = Path.Combine(appModuleDir, "main.eidos");
        var sharedModule = Path.Combine(sharedModuleDir, "Tools.eidos");
        const string appSource = """
App.Main :: module
{
    import shared::Shared.Tools

    main :: Unit -> Int
    {
        _ => Tools::answer(())
    }
}
""";

        const string sharedSource = """
Shared.Tools :: module
{
    export answer :: Unit -> Int
    {
        _ => 42
    }
}
""";

        File.WriteAllText(appEntry, appSource);
        File.WriteAllText(sharedModule, sharedSource);
        File.WriteAllText(
            Path.Combine(sharedProjectDir, "eidos.toml"),
            """
            sourceRoots = ["src"]
            defaultTarget = "shared"

            [[targets]]
            name = "shared"
            kind = "library"
            entry = "src/Shared/Tools.eidos"
            """);
        File.WriteAllText(
            Path.Combine(appProjectDir, "eidos.toml"),
            """
            sourceRoots = ["src"]
            defaultTarget = "app"

            [[targets]]
            name = "app"
            kind = "executable"
            entry = "src/App/main.eidos"

            [dependencies.shared]
            path = "../Shared"
            """);

        try
        {
            var resolved = EidosProjectGraphResolver.ResolveTarget(appProjectDir);
            var result = new CompilationPipeline(appSource, new CompilationOptions
            {
                InputFile = resolved.EntryFilePath,
                ImportSearchRoots = resolved.ImportResolution.EffectiveSearchRoots,
                PackageImportRoots = resolved.PackageImportRoots,
                StopAtPhase = CompilationPhase.Namer,
                NoImplicitPrelude = true,
                UseColors = false
            }).Run();

            Assert.True(result.Success, FormatDiagnostics(result));
            Assert.NotNull(result.SymbolTable);
            Assert.True(result.SymbolTable.Modules.ModulePaths.ContainsKey("shared::Shared/Tools"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string FormatDiagnostics(CompilationResult result)
    {
        return string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}"));
    }
}
