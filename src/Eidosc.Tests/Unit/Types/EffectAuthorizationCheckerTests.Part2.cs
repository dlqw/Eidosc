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
    public void CompilationPipeline_CallerQualifiedEffect_AuthorizesImportedQualifiedRequirement()
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

    helper :: Int -> Unit need Cap.Writer
    {
        _ => write("x")
    }
}
""";

        const string entrySource = """
import Cap

main :: Unit -> Unit need Cap.Writer
{
    _ => Cap.helper(0)
}
""";

        File.WriteAllText(moduleFile, moduleSource);
        File.WriteAllText(entryFile, entrySource);

        try
        {
            var result = RunPipeline(entrySource, entryFile);

            Assert.True(
                result.Success,
                string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message} [{string.Join("; ", diagnostic.Notes)}]")));
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
    public void CompilationPipeline_ModuleAliasQualifiedEffect_AuthorizesImportedQualifiedRequirement()
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

    helper :: Int -> Unit need Cap.Writer
    {
        _ => write("x")
    }
}
""";

        const string entrySource = """
C :: import Cap;

main :: Unit -> Unit need C.Writer
{
    _ => C.helper(0)
}
""";

        File.WriteAllText(moduleFile, moduleSource);
        File.WriteAllText(entryFile, entrySource);

        try
        {
            var result = RunPipeline(entrySource, entryFile);

            Assert.True(result.Success);
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
    public void CompilationPipeline_ModuleAliasImportOnly_DoesNotEnableBareEffectShortName()
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

    helper :: Int -> Unit need Cap.Writer
    {
        _ => write("x")
    }
}
""";

        const string entrySource = """
C :: import Cap;

main :: Unit -> Unit need Writer
{
    _ => C.helper(0)
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

    [Fact]
    public void CompilationPipeline_WildcardImportedEffect_AllowsBareShortNameEffectSignature()
    {
        const string source = """
Cap :: module {
    Writer :: effect;

    write :: String -> Unit need Writer
    {
        _ => ()
    }
}

import Cap.*

main :: Unit -> Unit need Writer
{
    x => x
}
""";

        var result = RunPipeline(source, "ability_auth_wildcard_import_short_signature.eidos");

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error && diagnostic.Code == "E3000");
    }

    [Fact]
    public void CompilationPipeline_EffectfulCallbackCanBuildCfnWhenCallerHasNeed()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_ability_auth_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var inputFile = Path.Combine(tempDir, "effectful_callback_cfn.eidos");
        WriteNameFirstManifest(tempDir);

        const string source = """
import Std.FFI

int_compare :: RawPtr -> RawPtr -> Int need FFI
{
    a => b => {
        va := ptr_load_as[Int](a);
        vb := ptr_load_as[Int](b);
        va - vb
    }
}

main :: Unit -> Int need FFI
{
    _ => {
        cmp := cfn_from(int_compare);
        0
    }
}
""";

        File.WriteAllText(inputFile, source);

        try
        {
            var result = RunPipeline(
                source,
                inputFile,
                stopAtPhase: CompilationPhase.Hir,
                languageVersion: EidosLanguageVersions.Current);

            Assert.True(
                result.Success,
                string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message} [{string.Join("; ", diagnostic.Notes)}]")));
            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CompilationPipeline_EffectfulCallbackCanMatchTaskAwaitRawMethodShape()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_ability_auth_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var inputFile = Path.Combine(tempDir, "effectful_callback_task_await_raw.eidos");
        WriteNameFirstManifest(tempDir);

        const string source = """
import Std.FFI
import Std.Task

store_result :: RawPtr -> RawPtr -> Unit need FFI
{
    slot => value => ptr_store_as[RawPtr](slot, value)
}

main :: Unit -> Unit need FFI
{
    _ => {
        payload := ptr_null();
        task := Task.completed_raw[RawPtr](payload);
        slot := ptr_null();
        awaited := task.await_raw(store_result(slot));
        ()
    }
}
""";

        File.WriteAllText(inputFile, source);

        try
        {
            var result = RunPipeline(
                source,
                inputFile,
                stopAtPhase: CompilationPhase.Hir,
                languageVersion: EidosLanguageVersions.Current);

            Assert.True(
                result.Success,
                string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message} [{string.Join("; ", diagnostic.Notes)}]")));
            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CompilationPipeline_IndexExpressionIndexEffectWithoutNeed_ReportsCapability()
    {
        const string source = """
Emitter :: effect;

emit :: Unit -> Int need Emitter
{
    _ => 0
}

main :: Unit -> Int
{
    _ => [10, 20][emit(())]
}
""";

        var result = RunPipeline(source, "ability_auth_index_expr_effect.eidos");

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error && diagnostic.Code == "E3003");
    }

    [Fact]
    public void CompilationPipeline_InfixCallEffectfulCalleeWithoutNeed_ReportsCapability()
    {
        const string source = """
Emitter :: effect;

emit :: Int -> Int need Emitter
{
    _ => 0
}

effect_add :: Int -> Int -> Int need Emitter
{
    left => right => emit(left) + right
}

main :: Unit -> Int
{
    _ => 1 `effect_add` 2
}
""";

        var result = RunPipeline(source, "ability_auth_infix_call_effect.eidos");

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error && diagnostic.Code == "E3003");
    }

    private static void WriteNameFirstManifest(string tempDir)
    {
        File.WriteAllText(
            Path.Combine(tempDir, "eidos.toml"),
            """
            manifestSchema = 3
            sourceRoots = ["."]
            [language]
            version = "0.6.0-alpha.1"
            """);
    }

    private static CompilationResult RunPipeline(
        string source,
        string inputFile,
        IReadOnlyList<string>? importSearchRoots = null,
        CompilationPhase stopAtPhase = CompilationPhase.Effects,
        string? languageVersion = null)
    {
        var options = new CompilationOptions
        {
            InputFile = inputFile,
            StopAtPhase = stopAtPhase,
            UseColors = false,
            ImportSearchRoots = importSearchRoots?.ToArray() ?? [],
            LanguageVersion = languageVersion ?? EidosLanguageVersions.Current
        };

        return new CompilationPipeline(source, options).Run();
    }
}
