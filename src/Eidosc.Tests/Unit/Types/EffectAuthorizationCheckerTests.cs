using System;
using System.IO;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

public partial class EffectAuthorizationCheckerTests
{
    [Fact]
    public void CompilationPipeline_EffectOperationCallWithoutCapability_ReportsE3003()
    {
        const string source = """
Emitter :: effect;

emit :: String -> Unit need Emitter
{
    _ => ()
}

main :: Unit -> Unit
{
    _ => emit("hello")
}
""";

        var result = RunPipeline(source, "ability_auth_basic.eidos");

        Assert.Equal(CompilationPhase.Effects, result.CompletedPhase);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error && diagnostic.Code == "E3003");
    }

    [Fact]
    public void CompilationPipeline_FunctionDeclaredWithCapabilitySignature_AllowsEffectOperationCall()
    {
        const string source = """
Emitter :: effect;

emit :: String -> Unit need Emitter
{
    _ => ()
}

helper :: Int -> Unit need Emitter
{
    _ => emit("hello")
}
""";

        var result = RunPipeline(source, "ability_auth_declared_capability.eidos");

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Effects, result.CompletedPhase);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error && diagnostic.Code == "E3003");
    }

    [Fact]
    public void CompilationPipeline_FfiCallInsideModuleInitializerWithoutCapability_ReportsE3003()
    {
        const string source = """
@ffi("malloc") malloc :: Int -> RawPtr

leaked :: malloc(8);

main :: Unit -> RawPtr need FFI
{
    _ => malloc(8)
}
""";

        var result = RunPipeline(source, "ability_auth_ffi_module_initializer_requires_capability.eidos");

        Assert.Equal(CompilationPhase.Effects, result.CompletedPhase);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            item => item.Level == DiagnosticLevel.Error && item.Code == "E3003");
        Assert.Contains("caller: <module-init>", diagnostic.Notes, StringComparer.Ordinal);
        Assert.Contains("callee: malloc", diagnostic.Notes, StringComparer.Ordinal);
        Assert.Contains(
            diagnostic.Notes,
            note => note.StartsWith("missing:", StringComparison.Ordinal) &&
                    note.Contains("FFI", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_FfiCallInsideEntryFunction_UsesRootCapability()
    {
        const string source = """
@ffi("malloc") malloc :: Int -> RawPtr

main :: Unit -> RawPtr need FFI
{
    _ => malloc(8)
}
""";

        var result = RunPipeline(source, "ability_auth_ffi_entry_root_capability.eidos");

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.Equal(CompilationPhase.Effects, result.CompletedPhase);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error && diagnostic.Code == "E3003");
    }

    [Fact]
    public void CompilationPipeline_NameFirstFfiCallInsideEntryFunctionWithoutNeed_ReportsE3003()
    {
        const string source = """
@ffi("malloc") malloc :: Int -> RawPtr;

main :: Unit -> RawPtr
{
    _ => malloc(8)
}
""";

        var result = RunPipeline(
            source,
            "ability_auth_name_first_ffi_entry_requires_need.eidos",
            languageVersion: EidosLanguageVersions.Current);

        Assert.Equal(CompilationPhase.Effects, result.CompletedPhase);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            item => item.Level == DiagnosticLevel.Error && item.Code == "E3003");
        Assert.Contains("caller: main", diagnostic.Notes, StringComparer.Ordinal);
        Assert.Contains("callee: malloc", diagnostic.Notes, StringComparer.Ordinal);
        Assert.Contains(
            diagnostic.Notes,
            note => note.StartsWith("missing:", StringComparison.Ordinal) &&
                    note.Contains("FFI", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_NameFirstFfiCallInsideEntryFunctionWithNeed_Succeeds()
    {
        const string source = """
@ffi("malloc") malloc :: Int -> RawPtr;

main :: Unit -> RawPtr need FFI
{
    _ => malloc(8)
}
""";

        var result = RunPipeline(
            source,
            "ability_auth_name_first_ffi_entry_need.eidos",
            languageVersion: EidosLanguageVersions.Current);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.Equal(CompilationPhase.Effects, result.CompletedPhase);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error && diagnostic.Code == "E3003");
    }

    [Fact]
    public void CompilationPipeline_NameFirstFfiHelperResultsCanFlowThroughBinaryWhenCallerHasNeed()
    {
        const string source = """
@ffi("malloc") malloc :: Int -> RawPtr;

left :: Unit -> Int need FFI
{
    _ => { p := malloc(8); 1 }
}

right :: Unit -> Int need FFI
{
    _ => { p := malloc(8); 2 }
}

main :: Unit -> Int need FFI
{
    _ => left(()) + right(())
}
""";

        var result = RunPipeline(
            source,
            "ability_auth_name_first_ffi_helper_binary.eidos",
            stopAtPhase: CompilationPhase.Hir,
            languageVersion: EidosLanguageVersions.Current);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.Equal(CompilationPhase.Hir, result.CompletedPhase);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void CompilationPipeline_FieldRead_WithSameNamedEffectOperation_DoesNotRequireCapability()
    {
        const string source = """
RangeOps :: effect;

start :: Ref[Range] -> Unit need RangeOps
{
    _ => ()
}

Range :: type
{
    start: Int, end: Int
}

read :: Ref[Range] -> Int
{
    r => r.start
}
""";

        var result = RunPipeline(source, "ability_auth_field_read_does_not_require_capability.eidos");

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Effects, result.CompletedPhase);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error && diagnostic.Code == "E3003");
    }

    [Fact]
    public void CompilationPipeline_FunctionDeclaredWithBraceEffectRow_RequiresAllDeclaredCapabilities()
    {
        const string source = """
Emitter :: effect;

emit :: String -> Unit need Emitter
{
    _ => ()
}

Logger :: effect;

log :: String -> Unit need Logger
{
    _ => ()
}

helper :: Int -> Unit need Emitter, Logger
{
    _ => {
        emit("emit");
        log("log");
    }
}

main :: Unit -> Unit
{
    _ => helper(0)
}
""";

        var result = RunPipeline(source, "ability_auth_declared_brace_ability_set.eidos");

        Assert.Equal(CompilationPhase.Effects, result.CompletedPhase);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            item => item.Level == DiagnosticLevel.Error && item.Code == "E3003");
        Assert.Contains(
            diagnostic.Notes,
            note => note.StartsWith("required:", StringComparison.Ordinal) &&
                    note.Contains("Emitter", StringComparison.Ordinal) &&
                    note.Contains("Logger", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_CurrentModuleRelativeQualifiedEffectPaths_CompileThroughAbilities()
    {
        const string source = """
Demo.Logger :: module {
    Logger :: effect;

    log :: String -> Unit need Logger
    {
        _ => ()
    }

    helper :: Int -> Unit need Logger::Logger
    {
        _ => Logger::log("hello")
    }

    main :: Unit -> Unit need Logger::Logger
    {
        _ => helper(0)
    }
}
""";

        var result = RunPipeline(source, "ability_auth_current_module_relative_qualified_paths.eidos");

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Effects, result.CompletedPhase);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Code == "E3003");
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Undefined effect 'Logger::Logger'", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Cannot resolve path 'Logger::log'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ImportedNestedQualifiedEffectPaths_CompileThroughAbilities()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_ability_auth_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var moduleDir = Path.Combine(tempDir, "Cap");
        Directory.CreateDirectory(moduleDir);

        var moduleFile = Path.Combine(moduleDir, "Io.eidos");
        var entryFile = Path.Combine(tempDir, "main.eidos");

        const string moduleSource = """
Cap.Io :: module {
    Writer :: effect;

    write :: String -> Int need Writer
    {
        _ => 0
    }
}
""";

        const string entrySource = """
import Cap.Io

run :: String -> Int need Io::Writer
{
    text => Io::write(text)
}

main :: Unit -> Int need Io::Writer
{
    _ => run("hello")
}
""";

        File.WriteAllText(moduleFile, moduleSource);
        File.WriteAllText(entryFile, entrySource);

        try
        {
            var result = RunPipeline(entrySource, entryFile);

            Assert.True(
                result.Success,
                string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
            Assert.Equal(CompilationPhase.Effects, result.CompletedPhase);
            Assert.DoesNotContain(
                result.Diagnostics,
                diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                              diagnostic.Message.Contains("Undefined effect 'Io::Writer'", StringComparison.Ordinal));
            Assert.DoesNotContain(
                result.Diagnostics,
                diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                              diagnostic.Message.Contains("Cannot resolve path 'Io::write'", StringComparison.Ordinal));
            Assert.DoesNotContain(
                result.Diagnostics,
                diagnostic => diagnostic.Level == DiagnosticLevel.Error && diagnostic.Code == "E3003");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CompilationPipeline_AncestorSourceRootImport_CompileThroughAbilities()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_ability_auth_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var sourceRoot = Path.Combine(tempDir, "src");
        var appDir = Path.Combine(sourceRoot, "App");
        var moduleDir = Path.Combine(sourceRoot, "Cap");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(moduleDir);

        var moduleFile = Path.Combine(moduleDir, "Io.eidos");
        var entryFile = Path.Combine(appDir, "main.eidos");

        const string moduleSource = """
Cap.Io :: module {
    Writer :: effect;

    write :: String -> Int need Writer
    {
        _ => 0
    }
}
""";

        const string entrySource = """
import Cap.Io

run :: String -> Int need Io::Writer
{
    text => Io::write(text)
}

main :: Unit -> Int need Io::Writer
{
    _ => run("hello")
}
""";

        File.WriteAllText(moduleFile, moduleSource);
        File.WriteAllText(entryFile, entrySource);

        try
        {
            var result = RunPipeline(entrySource, entryFile);

            Assert.True(
                result.Success,
                string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
            Assert.Equal(CompilationPhase.Effects, result.CompletedPhase);
            Assert.DoesNotContain(
                result.Diagnostics,
                diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                              diagnostic.Message.Contains("Undefined effect 'Io::Writer'", StringComparison.Ordinal));
            Assert.DoesNotContain(
                result.Diagnostics,
                diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                              diagnostic.Message.Contains("Cannot resolve path 'Io::write'", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CompilationPipeline_ExplicitImportRoot_CompileThroughAbilities()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_ability_auth_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var appDir = Path.Combine(tempDir, "App");
        var importRoot = Path.Combine(tempDir, "shared_modules");
        var moduleDir = Path.Combine(importRoot, "Cap");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(moduleDir);

        var moduleFile = Path.Combine(moduleDir, "Io.eidos");
        var entryFile = Path.Combine(appDir, "main.eidos");

        const string moduleSource = """
Cap.Io :: module {
    Writer :: effect;

    write :: String -> Int need Writer
    {
        _ => 0
    }
}
""";

        const string entrySource = """
import Cap.Io

run :: String -> Int need Io::Writer
{
    text => Io::write(text)
}

main :: Unit -> Int need Io::Writer
{
    _ => run("hello")
}
""";

        File.WriteAllText(moduleFile, moduleSource);
        File.WriteAllText(entryFile, entrySource);

        try
        {
            var result = RunPipeline(entrySource, entryFile, [importRoot]);

            Assert.True(
                result.Success,
                string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
            Assert.Equal(CompilationPhase.Effects, result.CompletedPhase);
            Assert.DoesNotContain(
                result.Diagnostics,
                diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                              diagnostic.Message.Contains("Undefined effect 'Io::Writer'", StringComparison.Ordinal));
            Assert.DoesNotContain(
                result.Diagnostics,
                diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                              diagnostic.Message.Contains("Cannot resolve path 'Io::write'", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CompilationPipeline_ProjectConfigurationImportRoot_CompileThroughAbilities()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_ability_auth_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var projectFile = Path.Combine(tempDir, "eidos.toml");
        var appDir = Path.Combine(tempDir, "App");
        var importRoot = Path.Combine(tempDir, "shared_modules");
        var moduleDir = Path.Combine(importRoot, "Cap");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(moduleDir);

        var moduleFile = Path.Combine(moduleDir, "Io.eidos");
        var entryFile = Path.Combine(appDir, "main.eidos");

        const string moduleSource = """
Cap.Io :: module {
    Writer :: effect;

    write :: String -> Int need Writer
    {
        _ => 0
    }
}
""";

        const string entrySource = """
import Cap.Io

run :: String -> Int need Io::Writer
{
    text => Io::write(text)
}

main :: Unit -> Int need Io::Writer
{
    _ => run("hello")
}
""";

        File.WriteAllText(projectFile, """
            importRoots = ["shared_modules"]

            [language]
            version = "0.4.0-alpha.1"
            """);
        File.WriteAllText(moduleFile, moduleSource);
        File.WriteAllText(entryFile, entrySource);

        try
        {
            var result = RunPipeline(entrySource, entryFile);

            Assert.True(
                result.Success,
                string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
            Assert.Equal(CompilationPhase.Effects, result.CompletedPhase);
            Assert.DoesNotContain(
                result.Diagnostics,
                diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                              diagnostic.Message.Contains("Undefined effect 'Io::Writer'", StringComparison.Ordinal));
            Assert.DoesNotContain(
                result.Diagnostics,
                diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                              diagnostic.Message.Contains("Cannot resolve path 'Io::write'", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CompilationPipeline_ProjectConfigurationSourceRoot_CompileThroughAbilities()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_ability_auth_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var projectFile = Path.Combine(tempDir, "eidos.toml");
        var appDir = Path.Combine(tempDir, "App");
        var sourceRoot = Path.Combine(tempDir, "src");
        var moduleDir = Path.Combine(sourceRoot, "Cap");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(moduleDir);

        var moduleFile = Path.Combine(moduleDir, "Io.eidos");
        var entryFile = Path.Combine(appDir, "main.eidos");

        const string moduleSource = """
Cap.Io :: module {
    Writer :: effect;

    write :: String -> Int need Writer
    {
        _ => 0
    }
}
""";

        const string entrySource = """
import Cap.Io

run :: String -> Int need Io::Writer
{
    text => Io::write(text)
}

main :: Unit -> Int need Io::Writer
{
    _ => run("hello")
}
""";

        File.WriteAllText(projectFile, """
            sourceRoots = ["src"]

            [language]
            version = "0.4.0-alpha.1"
            """);
        File.WriteAllText(moduleFile, moduleSource);
        File.WriteAllText(entryFile, entrySource);

        try
        {
            var result = RunPipeline(entrySource, entryFile);

            Assert.True(
                result.Success,
                string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
            Assert.Equal(CompilationPhase.Effects, result.CompletedPhase);
            Assert.DoesNotContain(
                result.Diagnostics,
                diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                              diagnostic.Message.Contains("Undefined effect 'Io::Writer'", StringComparison.Ordinal));
            Assert.DoesNotContain(
                result.Diagnostics,
                diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                              diagnostic.Message.Contains("Cannot resolve path 'Io::write'", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CompilationPipeline_ImportedQualifiedEffectFunctionCallWithoutCapability_ReportsStructuredE3003()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_ability_auth_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var moduleFile = Path.Combine(tempDir, "Cap.eidos");
        var entryFile = Path.Combine(tempDir, "main.eidos");

        const string moduleSource = """
Cap :: module {
    Writer :: effect;

    write :: String -> Unit need Writer
    {
        _ => ()
    }

    helper :: Int -> Unit need Cap::Writer
    {
        _ => write("from_cap")
    }
}
""";

        const string entrySource = """
import Cap

main :: Unit -> Unit
{
    _ => Cap::helper(0)
}
""";

        File.WriteAllText(moduleFile, moduleSource);
        File.WriteAllText(entryFile, entrySource);

        try
        {
            var result = RunPipeline(entrySource, entryFile);

            Assert.Equal(CompilationPhase.Effects, result.CompletedPhase);
            var diagnostic = Assert.Single(
                result.Diagnostics,
                item => item.Level == DiagnosticLevel.Error && item.Code == "E3003");
            Assert.Contains("caller: main", diagnostic.Notes, StringComparer.Ordinal);
            Assert.Contains("callee: helper", diagnostic.Notes, StringComparer.Ordinal);
            Assert.Contains(
                diagnostic.Notes,
                note => note.StartsWith("required:", StringComparison.Ordinal) &&
                        note.Contains("Writer", StringComparison.Ordinal));
            Assert.Contains(
                diagnostic.Notes,
                note => note.StartsWith("missing:", StringComparison.Ordinal) &&
                        note.Contains("Writer", StringComparison.Ordinal));
            Assert.Contains(diagnostic.Notes, note => note.StartsWith("available:", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CompilationPipeline_EffectSignatureShortEffectName_WithMultipleImportedCandidates_ReportsAmbiguousEffect()
    {
        const string source = """
A :: module {
    Writer :: effect;

    write :: String -> Unit need Writer
    {
        _ => ()
    }
}

B :: module {
    Writer :: effect;

    write :: String -> Unit need Writer
    {
        _ => ()
    }
}

import A::*
import B::*

f :: Unit -> Unit need Writer
{
    x => x
}
""";

        var result = RunPipeline(source, "ability_auth_signature_ambiguous_writer.eidos");

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Code == "E3000" &&
                          diagnostic.Message.Contains("Ambiguous effect 'Writer'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_EffectSignatureShortEffectName_WithoutImport_DoesNotFallbackToGlobalEffect()
    {
        const string source = """
A :: module {
    Writer :: effect;

    write :: String -> Unit need Writer
    {
        _ => ()
    }
}

f :: Unit -> Unit need Writer
{
    x => x
}
""";

        var result = RunPipeline(source, "ability_auth_signature_short_no_import_no_global_fallback.eidos");

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Code == "E3000" &&
                          diagnostic.Message.Contains("Undefined effect 'Writer'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_CallerUnresolvedShortEffect_DoesNotAuthorizeImportedQualifiedRequirement()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_ability_auth_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var moduleFile = Path.Combine(tempDir, "Cap.eidos");
        var entryFile = Path.Combine(tempDir, "main.eidos");

        const string moduleSource = """
Cap :: module {
    Writer :: effect;

    write :: String -> Unit need Writer
    {
        _ => ()
    }

    helper :: Int -> Unit need Cap::Writer
    {
        _ => write("x")
    }
}
""";

        const string entrySource = """
import Cap

main :: Unit -> Unit need Writer
{
    _ => Cap::helper(0)
}
""";

        File.WriteAllText(moduleFile, moduleSource);
        File.WriteAllText(entryFile, entrySource);

        try
        {
            var result = RunPipeline(entrySource, entryFile);

            Assert.False(result.Success);
            Assert.Contains(
                result.Diagnostics,
                diagnostic => diagnostic.Level == DiagnosticLevel.Error && diagnostic.Code == "E3000");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

}
