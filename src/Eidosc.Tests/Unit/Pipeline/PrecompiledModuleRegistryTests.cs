using Eidosc.Symbols;
using Eidosc.Ast.Types;
using Eidosc.Mir;
using Eidosc.Semantic;
using System.Collections.Generic;
using Eidosc.Pipeline;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Pipeline;

public class PrecompiledModuleRegistryTests
{
    [Fact]
    public void SignatureSource_KeepsSelectiveImportUsedByPublicSignature()
    {
        const string source = """
sample :: module {
    import ordering.{Ordering}

    compare :: Int -> Int -> Ordering
    {
        left => right => ordering.compare_int(left)(right)
    }
}
""";

        var result = PrecompiledModuleCache.GetOrCreateSignatureSource(
            $"selective-signature-{Guid.NewGuid():N}",
            source);

        Assert.Contains("import ordering.{Ordering}", result.Source, StringComparison.Ordinal);
        Assert.Equal(0, result.ImportRemovalCount);
    }

    [Fact]
    public void SignatureSource_RemovesSelectiveImportUnusedByPublicSignature()
    {
        const string source = """
sample :: module {
    import ordering.{Ordering}

    compare :: Int -> Int -> Int
    {
        left => right => ordering.to_int(ordering.compare_int(left)(right))
    }
}
""";

        var result = PrecompiledModuleCache.GetOrCreateSignatureSource(
            $"unused-selective-signature-{Guid.NewGuid():N}",
            source);

        Assert.DoesNotContain("import ordering.{Ordering}", result.Source, StringComparison.Ordinal);
        Assert.Equal(1, result.ImportRemovalCount);
    }

    [Fact]
    public void AuditEmbeddedStdlib_BodylessRuntimeFunctionsUseImplementationClauses()
    {
        var issues = PrecompiledStdlibDeclarationAuditor.AuditEmbeddedStdlib();

        Assert.Empty(issues);
    }

    [Fact]
    public void IntrinsicRegistry_UsesEmbeddedStdlibDeclarations()
    {
        Assert.True(IntrinsicRegistry.IsKnownIntrinsicName("array_new"));
        Assert.True(IntrinsicRegistry.IsKnownIntrinsicName("read_char"));
        Assert.True(IntrinsicRegistry.IsKnownIntrinsicName("terminal_set_raw"));
        Assert.True(IntrinsicRegistry.IsKnownIntrinsicName("terminal_restore"));
        Assert.True(IntrinsicRegistry.IsKnownIntrinsicName("sleep_ms"));
        Assert.False(IntrinsicRegistry.IsKnownIntrinsicName("\"array_new\""));
        Assert.False(IntrinsicRegistry.IsKnownIntrinsicName("not_a_runtime_intrinsic"));
    }

    [Fact]
    public void IntrinsicRegistry_CapturesDeclaredEffects()
    {
        Assert.True(IntrinsicRegistry.TryGet("file_exists", out var ioIntrinsic));
        Assert.Contains(WellKnownStrings.BuiltinAbilities.IO, ioIntrinsic.Effects);

        Assert.True(IntrinsicRegistry.TryGet("regex_compile", out var ffiIntrinsic));
        Assert.Contains(WellKnownStrings.BuiltinAbilities.FFI, ffiIntrinsic.Effects);
    }

    [Fact]
    public void IntrinsicRegistry_CapturesDeclaredSignatures()
    {
        Assert.True(IntrinsicRegistry.TryGet("ptr_load_i32", out var i32Intrinsic));
        var i32Signature = Assert.IsType<ArrowType>(i32Intrinsic.Signature);
        var i32Return = Assert.IsType<TypePath>(i32Signature.ReturnType);
        Assert.Equal(WellKnownStrings.BuiltinTypes.Int, i32Return.TypeName);
        Assert.Equal("ptr -> i32", i32Intrinsic.LlvmAbi);
        Assert.Equal("arity=0;RawPtr->Int", i32Intrinsic.SignatureKey);

        Assert.True(IntrinsicRegistry.TryGet("sleep_ms", out var sleepIntrinsic));
        var sleepSignature = Assert.IsType<ArrowType>(sleepIntrinsic.Signature);
        var sleepReturn = Assert.IsType<TypePath>(sleepSignature.ReturnType);
        Assert.Equal(WellKnownStrings.BuiltinTypes.Unit, sleepReturn.TypeName);
    }

    [Fact]
    public void IntrinsicRegistry_IndexesDeclarationsByNameAndSignature()
    {
        Assert.True(IntrinsicRegistry.TryGet("ptr_is_null", "arity=0;RawPtr->Bool", out var ptrIsNull));
        Assert.Equal("ptr_is_null", ptrIsNull.Name);
        Assert.Equal("arity=0;RawPtr->Bool", ptrIsNull.SignatureKey);

        var overloads = IntrinsicRegistry.GetOverloads("shared_clone");

        var overload = Assert.Single(overloads);
        Assert.Equal("arity=1;Ref[Shared[T]]->Shared[T]", overload.SignatureKey);
        Assert.True(IntrinsicRegistry.TryGet("shared_clone", overload.SignatureKey, out var selected));
        Assert.Equal(overload.SignatureKey, selected.SignatureKey);
    }

    [Fact]
    public void AuditSourceForTest_BodylessTopLevelFunctionWithoutImplementationClause_IsReported()
    {
        const string source = """
Test :: module {
    runtime_helper :: Int -> Int
}
""";

        var issue = Assert.Single(PrecompiledStdlibDeclarationAuditor.AuditSourceForTest(source, "std/test"));

        Assert.Equal("std/test", issue.ModulePath);
        Assert.Equal("runtime_helper", issue.FunctionName);
        Assert.Contains("extern or intrinsic", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditSourceForTest_BodylessTopLevelIntrinsicFunction_IsAccepted()
    {
        const string source = """
Test :: module {

    value_box[A] :: A -> RawPtr compiler(intrinsic: "value_box");

}
""";

        var issues = PrecompiledStdlibDeclarationAuditor.AuditSourceForTest(source, "std/test");

        Assert.Empty(issues);
    }

    [Fact]
    public void AuditSourceForTest_CompilerImplementedHelperUseWithoutLocalDeclaration_IsReported()
    {
        const string source = """
Test :: module {
    length :: String -> Int
    {
        text => string_length(text)
    }
}
""";

        var issue = Assert.Single(PrecompiledStdlibDeclarationAuditor.AuditSourceForTest(source, "std/test"));

        Assert.Equal("std/test", issue.ModulePath);
        Assert.Equal("string_length", issue.FunctionName);
        Assert.Contains("without a local", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditSourceForTest_FfiFunctionWithBody_IsReported()
    {
        const string source = """
Test :: module {

    runtime_helper :: Int -> Int
     need ffi extern(c, name: "runtime_helper")
{
        value => value
    }
}
""";

        var issue = Assert.Single(PrecompiledStdlibDeclarationAuditor.AuditSourceForTest(source, "std/test"));

        Assert.Equal("runtime_helper", issue.FunctionName);
        Assert.Contains("must not provide", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditSourceForTest_IntrinsicFunctionWithBody_IsReported()
    {
        const string source = """
Test :: module {

    value_box :: RawPtr -> RawPtr
     compiler(intrinsic: "value_box")
{
        value => value
    }
}
""";

        var issue = Assert.Single(PrecompiledStdlibDeclarationAuditor.AuditSourceForTest(source, "std/test"));

        Assert.Equal("value_box", issue.FunctionName);
        Assert.Contains("intrinsic", issue.Message, StringComparison.Ordinal);
        Assert.Contains("must not provide", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pipeline_ToolchainIntrinsicDeclaration_CarriesBuiltinMirIdentity()
    {
        const string source = """

value_box[A] :: A -> RawPtr compiler(intrinsic: "value_box");


ptr :: value_box[Int](1);
""";

        var inputFile = Path.Combine(
            PrecompiledModuleRegistry.GetStdlibRoot(),
            "std",
            "precompiled_intrinsic_identity.eidos");
        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = inputFile,
            ToolchainOwnedSourcePaths = [inputFile],
            StopAtPhase = CompilationPhase.Mir,
            UseColors = false
        }).Run();

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.NotNull(result.MirModule);
        var mir = result.MirModule;
        Assert.Contains(mir.Functions.SelectMany(function => function.BasicBlocks)
                .SelectMany(block => block.Instructions)
                .OfType<MirCall>(),
            call => call.Function is MirFunctionRef functionRef &&
                    MirBuiltinFunctions.TryGetIntrinsicName(functionRef, out var name) &&
                    name == "value_box");
    }

    [Fact]
    public void Pipeline_InternalIntrinsicDeclaration_IsNotWildcardImported()
    {
        const string source = """
Runtime :: module {


    hidden_len :: String -> Int compiler(internal, intrinsic: "string_length");

}

App :: module {
    import Runtime.*

    run :: String -> Int
    {
        text => hidden_len(text)
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "internal_intrinsic_visibility.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("hidden_len", StringComparison.Ordinal));
    }

    private static string FormatDiagnostics(CompilationResult result)
    {
        return string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
    }

    [Fact]
    public void Registry_AvailableModules_ContainsCoreStdModules()
    {
        var modules = PrecompiledModuleRegistry.GetAvailableModulePaths();

        Assert.Contains("std/Functions", modules);
        Assert.Contains("std/Applicative", modules);
        Assert.Contains("std/Foldable", modules);
        Assert.Contains("std/Functor", modules);
        Assert.Contains("std/Monad", modules);
        Assert.Contains("std/Traversable", modules);
        Assert.Contains("std/Ordering", modules);
        Assert.Contains("std/Option", modules);
        Assert.Contains("std/Result", modules);
        Assert.Contains("std/Range", modules);
        Assert.Contains("std/Prelude", modules);
        Assert.Contains("std/RuntimeArray", modules);
        Assert.Contains("std/Seq", modules);
        Assert.Contains("std/SeqBuilder", modules);
        Assert.Contains("std/Traits", modules);
        Assert.Contains("std/TraitInvoke", modules);
        Assert.Contains("std/Text", modules);
        Assert.Contains("std/Math", modules);
        Assert.Contains("std/FloatMath", modules);
        Assert.Contains("std/GameMath", modules);
        Assert.Contains("std/Console", modules);
        Assert.Contains("std/File", modules);
        Assert.Contains("std/Network", modules);
        Assert.Contains("std/Binary", modules);
        Assert.Contains("std/Json", modules);
    }

    [Fact]
    public void Registry_StdSeqSource_CanBeLoadedFromEmbeddedResource()
    {
        var loaded = PrecompiledModuleRegistry.TryGetSource("std/Seq", out var source);

        Assert.True(loaded);
        Assert.Contains("Seq :: module", source, StringComparison.Ordinal);
        Assert.Contains("len[T] :: Seq[T] -> Int", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_StdRuntimeArrayExports_ContainRuntimeArrayPrimitives()
    {
        var exports = PrecompiledModuleRegistry.GetExports("std/RuntimeArray");

        Assert.Contains("len", exports.Functions);
        Assert.Contains("with_capacity", exports.Functions);
        Assert.Contains("empty", exports.Functions);
        Assert.Contains("singleton", exports.Functions);
        Assert.Contains("push", exports.Functions);
        Assert.Contains("extend", exports.Functions);
        Assert.Contains("pop_last", exports.Functions);
        Assert.Contains("swap", exports.Functions);
        Assert.Contains("get", exports.Functions);
        Assert.DoesNotContain("new_raw", exports.Functions);
        Assert.DoesNotContain("push_raw", exports.Functions);
        Assert.DoesNotContain("extend_raw", exports.Functions);
        Assert.DoesNotContain("pop_raw", exports.Functions);
        Assert.DoesNotContain("swap_raw", exports.Functions);
    }

    [Fact]
    public void Registry_StdSeqExports_ContainSafeCombinators()
    {
        var exports = PrecompiledModuleRegistry.GetExports("std/Seq");

        Assert.Contains("len", exports.Functions);
        Assert.Contains("empty", exports.Functions);
        Assert.Contains("head", exports.Functions);
        Assert.Contains("head_or", exports.Functions);
        Assert.Contains("get_opt", exports.Functions);
        Assert.Contains("get_or", exports.Functions);
        Assert.Contains("tail", exports.Functions);
        Assert.Contains("tail_or", exports.Functions);
        Assert.Contains("singleton", exports.Functions);
        Assert.Contains("last", exports.Functions);
        Assert.Contains("last_or", exports.Functions);
        Assert.Contains("map", exports.Functions);
        Assert.Contains("filter", exports.Functions);
        Assert.Contains("flat_map", exports.Functions);
        Assert.Contains("fold_left", exports.Functions);
        Assert.Contains("fold_right", exports.Functions);
        Assert.Contains("find", exports.Functions);
        Assert.Contains("find_index", exports.Functions);
        Assert.Contains("any", exports.Functions);
        Assert.Contains("all", exports.Functions);
        Assert.Contains("append", exports.Functions);
        Assert.Contains("take", exports.Functions);
        Assert.Contains("drop", exports.Functions);
        Assert.Contains("count", exports.Functions);
        Assert.Contains("none", exports.Functions);
        Assert.Contains("zip", exports.Functions);
        Assert.Contains("zip_with", exports.Functions);
        Assert.Contains("concat", exports.Functions);
        Assert.Contains("reverse", exports.Functions);
        Assert.Contains("fmap", exports.Functions);
        Assert.Contains("pure", exports.Functions);
        Assert.Contains("apply", exports.Functions);
        Assert.Contains("bind", exports.Functions);
        Assert.DoesNotContain("get_from_opt", exports.Functions);
        Assert.DoesNotContain("show_items", exports.Functions);
        Assert.DoesNotContain("find_index_from", exports.Functions);
    }

    [Fact]
    public void Registry_StdSeqBuilderAndPriorityQueueExports_ContainGraphErgonomics()
    {
        var vec = PrecompiledModuleRegistry.GetExports("std/SeqBuilder");
        var queue = PrecompiledModuleRegistry.GetExports("std/PriorityQueue");

        Assert.Contains("filled", vec.Functions);
        Assert.Contains("pop_last", vec.Functions);
        Assert.Contains("swap", vec.Functions);
        Assert.Contains("MinPriorityQueue", queue.Types);
        Assert.Contains("MinPriorityEntry", queue.Types);
        Assert.Contains("min_empty", queue.Functions);
        Assert.Contains("min_singleton", queue.Functions);
        Assert.Contains("min_enqueue", queue.Functions);
        Assert.Contains("min_dequeue", queue.Functions);
        Assert.Contains("min_peek", queue.Functions);
    }

    [Fact]
    public void Registry_StdPreludeExportedFunctions_ContainsApplyAndFlip()
    {
        var functionNames = PrecompiledModuleRegistry.GetExportedFunctionNames("std/Functions");

        Assert.Contains("apply", functionNames);
        Assert.Contains("flip", functionNames);
    }

    [Fact]
    public void Registry_StdPreludeExportedFunctions_ContainPreludeSpecificHelpersOnly()
    {
        // Verify the real embedded Prelude source re-exports correctly
        var sourceLoaded = PrecompiledModuleRegistry.TryGetSource("std/Prelude", out var preludeSource);
        Assert.True(sourceLoaded, "Prelude source should be loadable");
        Assert.Contains("export import", preludeSource, StringComparison.Ordinal);
        Assert.Contains("export id[T] :: T -> T", preludeSource, StringComparison.Ordinal);

        // Use ExtractExportsForTest to bypass caching and test with full module resolution
        var moduleSources = PrecompiledModuleRegistry.GetAvailableModulePaths()
            .ToDictionary(p => p, p =>
            {
                PrecompiledModuleRegistry.TryGetSource(p, out var src);
                return src ?? "";
            });

        var exports = PrecompiledModuleRegistry.ExtractExportsForTest(
            preludeSource, "std/Prelude", moduleSources);

        // Direct utility functions defined in Prelude
        Assert.Contains("id", exports.Functions);
        Assert.Contains("const", exports.Functions);
        Assert.Contains("not", exports.Functions);
        Assert.Contains("otherwise", exports.Functions);

        // Re-exported types
        Assert.Contains("Option", exports.Types);
        Assert.Contains("Result", exports.Types);
        Assert.Contains("Ordering", exports.Types);

        // Re-exported trait names (selective import of trait names only)
        Assert.Contains("Eq", exports.Traits);
        Assert.Contains("Ord", exports.Traits);
        Assert.Contains("Show", exports.Traits);
        Assert.Contains("Clone", exports.Traits);
        Assert.Contains("Functor", exports.Traits);
        Assert.Contains("Applicative", exports.Traits);
        Assert.Contains("Monad", exports.Traits);

        // Re-exported List functions
        Assert.Contains("len", exports.Functions);
        Assert.Contains("map", exports.Functions);
        Assert.Contains("filter", exports.Functions);
        Assert.Contains("fold_left", exports.Functions);

        // Re-exported Text/File core helpers
        Assert.Contains("char_code_at_or", exports.Functions);
        Assert.Contains("char_at_or", exports.Functions);
        Assert.Contains("last_index_of_or", exports.Functions);
        Assert.Contains("exists", exports.Functions);
        Assert.Contains("read_text_or", exports.Functions);
        Assert.Contains("write_text_result", exports.Functions);
    }

    [Fact]
    public void Registry_StdPreludeExportedFunctions_ExportFuncParsesCorrectly()
    {
        var exportFuncSource = """
            Prelude :: module {
                export id[T] :: T -> T { x => x }
            }
            """;
        var exports = PrecompiledModuleRegistry.ExtractExportsForTest(exportFuncSource);
        Assert.Contains("id", exports.Functions);
    }

    [Fact]
    public void Registry_StdPreludeExportedFunctions_ExportImportParsesCorrectly()
    {
        var exportImportSource = """
            Prelude :: module {
                export import Text.{starts_with}
                export id[T] :: T -> T { x => x }
            }
            """;
        var moduleSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["std/Text"] = "Text :: module { starts_with :: String -> String -> Bool { s => p => true } }"
        };
        var exports = PrecompiledModuleRegistry.ExtractExportsForTest(
            exportImportSource, "std/Prelude", moduleSources);
        Assert.Contains("id", exports.Functions);
        Assert.Contains("starts_with", exports.Functions);
    }

    [Fact]
    public void Registry_StdOptionExports_ContainOptionTypeConstructorsAndFunctions()
    {
        var exports = PrecompiledModuleRegistry.GetExports("std/Option");

        Assert.Contains("Option", exports.Types);
        Assert.Contains("Some", exports.Constructors);
        Assert.Contains("None", exports.Constructors);
        Assert.Contains("is_some", exports.Functions);
        Assert.Contains("is_none", exports.Functions);
        Assert.Contains("map", exports.Functions);
        Assert.Contains("map_or", exports.Functions);
        Assert.Contains("and_then", exports.Functions);
        Assert.Contains("and", exports.Functions);
        Assert.Contains("or", exports.Functions);
        Assert.Contains("xor", exports.Functions);
        Assert.Contains("unwrap_or", exports.Functions);
        Assert.Contains("zip", exports.Functions);
        Assert.Contains("zip_with", exports.Functions);
        Assert.Contains("flatten", exports.Functions);
        Assert.Contains("fmap", exports.Functions);
        Assert.Contains("pure", exports.Functions);
        Assert.Contains("apply", exports.Functions);
        Assert.Contains("fold_left", exports.Functions);
        Assert.Contains("fold_right", exports.Functions);
        Assert.Contains("bind", exports.Functions);
    }

    [Fact]
    public void Registry_StdResultExports_ContainResultTypeConstructorsAndFunctions()
    {
        var exports = PrecompiledModuleRegistry.GetExports("std/Result");

        Assert.Contains("Result", exports.Types);
        Assert.Contains("With", exports.Types);
        Assert.Contains("Ok", exports.Constructors);
        Assert.Contains("Err", exports.Constructors);
        Assert.Contains("is_ok", exports.Functions);
        Assert.Contains("is_err", exports.Functions);
        Assert.Contains("map", exports.Functions);
        Assert.Contains("map_err", exports.Functions);
        Assert.Contains("fmap", exports.Functions);
        Assert.Contains("pure", exports.Functions);
        Assert.Contains("apply", exports.Functions);
        Assert.Contains("fold_left", exports.Functions);
        Assert.Contains("fold_right", exports.Functions);
        Assert.Contains("bind", exports.Functions);
        Assert.Contains("and_then", exports.Functions);
        Assert.Contains("map_or", exports.Functions);
        Assert.Contains("or_else", exports.Functions);
        Assert.Contains("unwrap_or", exports.Functions);
        Assert.Contains("flatten", exports.Functions);
        Assert.Contains("ok", exports.Functions);
        Assert.Contains("err", exports.Functions);
    }

    [Fact]
    public void Registry_StdRangeExports_ContainRecordTypeAndHelpers()
    {
        var exports = PrecompiledModuleRegistry.GetExports("std/Range");

        Assert.Contains("Range", exports.Types);
        Assert.Contains("Range", exports.Constructors);
        Assert.Contains("make", exports.Functions);
        Assert.Contains("from_start_len", exports.Functions);
        Assert.Contains("start", exports.Functions);
        Assert.Contains("end", exports.Functions);
        Assert.Contains("normalize", exports.Functions);
        Assert.Contains("is_empty", exports.Functions);
        Assert.Contains("len", exports.Functions);
        Assert.Contains("contains", exports.Functions);
        Assert.Contains("intersects", exports.Functions);
        Assert.Contains("intersection_opt", exports.Functions);
        Assert.Contains("cover", exports.Functions);
        Assert.Contains("shift", exports.Functions);
    }

    [Fact]
    public void Registry_StdTextExports_ContainSafeStringHelpers()
    {
        var exports = PrecompiledModuleRegistry.GetExports("std/Text");

        Assert.Contains("len", exports.Functions);
        Assert.Contains("empty", exports.Functions);
        Assert.Contains("from_int", exports.Functions);
        Assert.Contains("from_bool", exports.Functions);
        Assert.Contains("from_code", exports.Functions);
        Assert.Contains("clone", exports.Functions);
        Assert.Contains("char_code_at", exports.Functions);
        Assert.Contains("char_code_at_opt", exports.Functions);
        Assert.Contains("slice", exports.Functions);
        Assert.Contains("take", exports.Functions);
        Assert.Contains("drop", exports.Functions);
        Assert.Contains("take_last", exports.Functions);
        Assert.Contains("drop_last", exports.Functions);
        Assert.Contains("index_of_opt", exports.Functions);
        Assert.Contains("index_of_or", exports.Functions);
        Assert.Contains("last_index_of_opt", exports.Functions);
        Assert.Contains("last_index_of_or", exports.Functions);
        Assert.Contains("char_code_at_or", exports.Functions);
        Assert.Contains("char_at_opt", exports.Functions);
        Assert.Contains("char_at_or", exports.Functions);
        Assert.Contains("count", exports.Functions);
        Assert.Contains("concat", exports.Functions);
        Assert.DoesNotContain("contains_from", exports.Functions);
        Assert.DoesNotContain("index_of_from_opt", exports.Functions);
        Assert.DoesNotContain("last_index_of_from_opt", exports.Functions);
        Assert.DoesNotContain("count_from", exports.Functions);
        Assert.Contains("eq", exports.Functions);
        Assert.Contains("show", exports.Functions);
    }

    [Fact]
    public void Registry_StdTraitExports_ContainCoreTraits()
    {
        var exports = PrecompiledModuleRegistry.GetExports("std/Traits");

        Assert.Contains("Eq", exports.Traits);
        Assert.Contains("Ord", exports.Traits);
        Assert.Contains("Show", exports.Traits);
    }

    [Fact]
    public void Registry_FunctionalSupportModules_ContainExpectedTraitsAndHelpers()
    {
        var functor = PrecompiledModuleRegistry.GetExports("std/Functor");
        var applicative = PrecompiledModuleRegistry.GetExports("std/Applicative");
        var foldable = PrecompiledModuleRegistry.GetExports("std/Foldable");
        var monad = PrecompiledModuleRegistry.GetExports("std/Monad");
        var traversable = PrecompiledModuleRegistry.GetExports("std/Traversable");
        var traitInvoke = PrecompiledModuleRegistry.GetExports("std/TraitInvoke");

        Assert.Contains("Functor", functor.Traits);
        Assert.Contains("Applicative", applicative.Traits);
        Assert.Contains("Foldable", foldable.Traits);
        Assert.Contains("Monad", monad.Traits);
        Assert.Contains("Traversable", traversable.Traits);
        Assert.Contains("eq_value", traitInvoke.Functions);
        Assert.Contains("compare_value", traitInvoke.Functions);
        Assert.Contains("show_value", traitInvoke.Functions);
    }

    [Fact]
    public void Registry_StdOrderingExports_ContainOrderingConstructorsAndFunctions()
    {
        var exports = PrecompiledModuleRegistry.GetExports("std/Ordering");

        Assert.Contains("Ordering", exports.Types);
        Assert.Contains("Less", exports.Constructors);
        Assert.Contains("Equal", exports.Constructors);
        Assert.Contains("Greater", exports.Constructors);
        Assert.Contains("is_lt", exports.Functions);
        Assert.Contains("is_eq", exports.Functions);
        Assert.Contains("is_gt", exports.Functions);
        Assert.Contains("reverse", exports.Functions);
        Assert.Contains("then_with", exports.Functions);
        Assert.Contains("then_compare_int", exports.Functions);
        Assert.Contains("then_compare_char", exports.Functions);
        Assert.Contains("then_compare_bool", exports.Functions);
        Assert.Contains("compare_int", exports.Functions);
        Assert.Contains("compare_char", exports.Functions);
        Assert.Contains("compare_bool", exports.Functions);
        Assert.Contains("eq", exports.Functions);
        Assert.Contains("compare", exports.Functions);
        Assert.Contains("show", exports.Functions);
    }

    [Fact]
    public void Registry_NewCapabilityModules_ContainExpectedExports()
    {
        var math = PrecompiledModuleRegistry.GetExports("std/Math");
        var floatMath = PrecompiledModuleRegistry.GetExports("std/FloatMath");
        var gameMath = PrecompiledModuleRegistry.GetExports("std/GameMath");
        var console = PrecompiledModuleRegistry.GetExports("std/Console");
        var file = PrecompiledModuleRegistry.GetExports("std/File");
        var network = PrecompiledModuleRegistry.GetExports("std/Network");
        var binary = PrecompiledModuleRegistry.GetExports("std/Binary");
        var json = PrecompiledModuleRegistry.GetExports("std/Json");

        Assert.Contains("pow", math.Functions);
        Assert.Contains("gcd", math.Functions);
        Assert.Contains("wrap", math.Functions);
        Assert.Contains("align_up", math.Functions);
        Assert.Contains("smoothstep", floatMath.Functions);
        Assert.Contains("move_toward", floatMath.Functions);
        Assert.Contains("wrap", floatMath.Functions);
        Assert.Contains("IVec2", gameMath.Types);
        Assert.Contains("Vec2", gameMath.Types);
        Assert.Contains("IRect", gameMath.Types);
        Assert.Contains("Rect", gameMath.Types);
        Assert.Contains("ivec2", gameMath.Functions);
        Assert.Contains("grid_cell_rect", gameMath.Functions);
        Assert.Contains("move_toward", gameMath.Functions);
        Assert.Contains("write_line", console.Functions);
        Assert.Contains("write_text_int_line", console.Functions);
        Assert.Contains("write_text_bool_line", console.Functions);
        Assert.Contains("read_line_text", console.Functions);
        Assert.Contains("read_line_result", console.Functions);
        Assert.Contains("read_line_opt", console.Functions);
        Assert.Contains("read_line_or_empty", console.Functions);
        Assert.Contains("read_line_or", console.Functions);
        Assert.Contains("read_line_or_else", console.Functions);
        Assert.Contains("exists", file.Functions);
        Assert.Contains("read_text", file.Functions);
        Assert.Contains("read_text_or_empty", file.Functions);
        Assert.Contains("read_text_or", file.Functions);
        Assert.Contains("read_text_opt", file.Functions);
        Assert.Contains("write_text", file.Functions);
        Assert.Contains("write_text_result", file.Functions);
        Assert.Contains("last_success", file.Functions);
        Assert.Contains("last_error", file.Functions);
        Assert.Contains("HttpRequest", network.Types);
        Assert.Contains("HttpResponse", network.Types);
        Assert.Contains("HttpBytesResponse", network.Types);
        Assert.Contains("HttpRequest", network.Constructors);
        Assert.Contains("HttpResponse", network.Constructors);
        Assert.Contains("HttpBytesResponse", network.Constructors);
        Assert.Contains("request", network.Functions);
        Assert.Contains("get_request", network.Functions);
        Assert.Contains("post_text_request", network.Functions);
        Assert.Contains("post_json_request", network.Functions);
        Assert.Contains("put_text_request", network.Functions);
        Assert.Contains("put_json_request", network.Functions);
        Assert.Contains("delete_request", network.Functions);
        Assert.Contains("url_encode_component", network.Functions);
        Assert.Contains("add_query_param", network.Functions);
        Assert.Contains("with_query_param", network.Functions);
        Assert.Contains("with_header", network.Functions);
        Assert.Contains("with_bearer_auth", network.Functions);
        Assert.Contains("with_accept_json", network.Functions);
        Assert.Contains("with_connect_timeout", network.Functions);
        Assert.Contains("with_total_timeout", network.Functions);
        Assert.Contains("send", network.Functions);
        Assert.Contains("send_bytes", network.Functions);
        Assert.Contains("send_with_bytes_body", network.Functions);
        Assert.Contains("send_bytes_with_bytes_body", network.Functions);
        Assert.Contains("send_text_result", network.Functions);
        Assert.Contains("send_with_bytes_body_result", network.Functions);
        Assert.Contains("send_bytes_result", network.Functions);
        Assert.Contains("send_text_opt", network.Functions);
        Assert.Contains("send_with_bytes_body_opt", network.Functions);
        Assert.Contains("send_bytes_opt", network.Functions);
        Assert.Contains("send_bytes_with_bytes_body_result", network.Functions);
        Assert.Contains("send_bytes_with_bytes_body_opt", network.Functions);
        Assert.Contains("http_get_response", network.Functions);
        Assert.Contains("http_get_bytes_response", network.Functions);
        Assert.Contains("http_get_query_response", network.Functions);
        Assert.Contains("http_get_query_text_result", network.Functions);
        Assert.Contains("http_get_query_text_opt", network.Functions);
        Assert.Contains("http_get_text_result", network.Functions);
        Assert.Contains("http_get_text_opt", network.Functions);
        Assert.Contains("http_get_text_or_empty", network.Functions);
        Assert.Contains("http_get_bytes_result", network.Functions);
        Assert.Contains("http_get_bytes_opt", network.Functions);
        Assert.Contains("http_get_bytes_or_empty", network.Functions);
        Assert.Contains("http_post_text_response", network.Functions);
        Assert.Contains("http_post_text_result", network.Functions);
        Assert.Contains("http_post_text_opt", network.Functions);
        Assert.Contains("http_post_json_response", network.Functions);
        Assert.Contains("http_post_json_result", network.Functions);
        Assert.Contains("http_post_json_opt", network.Functions);
        Assert.Contains("http_post_bytes_text_response", network.Functions);
        Assert.Contains("http_post_bytes_text_result", network.Functions);
        Assert.Contains("http_post_bytes_text_opt", network.Functions);
        Assert.Contains("http_post_bytes_response", network.Functions);
        Assert.Contains("http_post_bytes_result", network.Functions);
        Assert.Contains("http_post_bytes_opt", network.Functions);
        Assert.Contains("http_put_text_response", network.Functions);
        Assert.Contains("http_put_text_result", network.Functions);
        Assert.Contains("http_put_text_opt", network.Functions);
        Assert.Contains("http_put_json_response", network.Functions);
        Assert.Contains("http_put_json_result", network.Functions);
        Assert.Contains("http_put_json_opt", network.Functions);
        Assert.Contains("http_put_bytes_text_response", network.Functions);
        Assert.Contains("http_put_bytes_text_result", network.Functions);
        Assert.Contains("http_put_bytes_text_opt", network.Functions);
        Assert.Contains("http_put_bytes_response", network.Functions);
        Assert.Contains("http_put_bytes_result", network.Functions);
        Assert.Contains("http_put_bytes_opt", network.Functions);
        Assert.Contains("http_delete_response", network.Functions);
        Assert.Contains("http_delete_text_result", network.Functions);
        Assert.Contains("http_delete_text_opt", network.Functions);
        Assert.Contains("ok", network.Functions);
        Assert.Contains("status", network.Functions);
        Assert.Contains("body", network.Functions);
        Assert.Contains("headers", network.Functions);
        Assert.Contains("header_value_opt", network.Functions);
        Assert.Contains("header_value_or_empty", network.Functions);
        Assert.Contains("effective_url", network.Functions);
        Assert.Contains("content_type", network.Functions);
        Assert.Contains("error", network.Functions);
        Assert.Contains("is_success_status", network.Functions);
        Assert.Contains("bytes_ok", network.Functions);
        Assert.Contains("bytes_status", network.Functions);
        Assert.Contains("body_bytes", network.Functions);
        Assert.Contains("bytes_headers", network.Functions);
        Assert.Contains("bytes_effective_url", network.Functions);
        Assert.Contains("bytes_content_type", network.Functions);
        Assert.Contains("bytes_error", network.Functions);
        Assert.Contains("bytes_is_success_status", network.Functions);
        Assert.Contains("connect_timeout_seconds", network.Functions);
        Assert.Contains("total_timeout_seconds", network.Functions);
        Assert.Contains("decode_bool", binary.Functions);
        Assert.Contains("decode_u32_le", binary.Functions);
        Assert.Contains("decode_string", binary.Functions);
        Assert.Contains("encode_u32_le", binary.Functions);
        Assert.Contains("bytes_to_string", binary.Functions);
        Assert.DoesNotContain("empty", binary.Functions);
        Assert.DoesNotContain("append", binary.Functions);
        Assert.DoesNotContain("push", binary.Functions);
        Assert.DoesNotContain("concat", binary.Functions);
        Assert.DoesNotContain("byte", binary.Functions);
        Assert.DoesNotContain("string_to_bytes_loop", binary.Functions);
        Assert.DoesNotContain("bytes_to_string_exact", binary.Functions);
        Assert.Contains("escape_string", json.Functions);
        Assert.Contains("object", json.Functions);
        Assert.DoesNotContain("escape_loop", json.Functions);
        Assert.DoesNotContain("hex_digit", json.Functions);
        Assert.DoesNotContain("escape_control_byte", json.Functions);
        Assert.DoesNotContain("escape_byte", json.Functions);
        Assert.DoesNotContain("join_with", json.Functions);
    }

    [Fact]
    public void ExtractExportsForTest_TracksFunctionsTypesTraitsEffectsAndConstructors()
    {
        const string source = """
Test :: module {
    Show :: trait {
        show :: Self -> String
    }

    Logger :: effect;

    Option[T] :: type { Some:: type(T) , None :: type {} }
    UserId :: type = Int;

    map[T, U] :: Option[T] -> (T -> U) -> Option[U]
    {
        Some(value) => f => Some(f(value)),
        None => _ => None
    }
}
""";

        var exports = PrecompiledModuleRegistry.ExtractExportsForTest(source);

        Assert.Equal(["map"], exports.Functions);
        Assert.Equal(["Option", "UserId"], exports.Types);
        Assert.Equal(["Show"], exports.Traits);
        Assert.Equal(["Logger"], exports.Effects);
        Assert.Equal(["None", "Some"], exports.Constructors);
    }

    [Fact]
    public void ExtractExportsForTest_SkipsFunctionsWithInternalClause()
    {
        const string source = """
Test :: module {

    helper :: Int -> Int
     compiler(internal)
{
        x => x
    }

    public_api :: Int -> Int
    {
        x => helper(x)
    }
}
""";

        var exports = PrecompiledModuleRegistry.ExtractExportsForTest(source);

        Assert.DoesNotContain("helper", exports.Functions);
        Assert.Contains("public_api", exports.Functions);
    }

    [Fact]
    public void ExtractExportsForTest_ExplicitExportMode_OnlyIncludesExportedNames()
    {
        const string source = """
Demo.Api :: module {
    export answer :: Int = 42;
    hidden_answer :: Int = 0;

    export id :: Int -> Int
    {
        x => x
    }

    secret :: Int -> Int
    {
        x => x + 1
    }

    export Writer :: effect;

    Hidden :: effect;

    export Maybe[T] :: type { Some:: type(T) , None :: type {} }
    HiddenMaybe[T] :: type { HiddenSome:: type(T) , HiddenNone :: type {} }
}
""";

        var exports = PrecompiledModuleRegistry.ExtractExportsForTest(source);

        Assert.Equal(["answer"], exports.Values);
        Assert.Equal(["id"], exports.Functions);
        Assert.Equal(["Maybe"], exports.Types);
        Assert.Equal(["Writer"], exports.Effects);
        Assert.Equal(["None", "Some"], exports.Constructors);
        Assert.DoesNotContain("hidden_answer", exports.Values);
        Assert.DoesNotContain("secret", exports.Functions);
        Assert.DoesNotContain("Hidden", exports.Effects);
        Assert.DoesNotContain("HiddenMaybe", exports.Types);
        Assert.DoesNotContain("HiddenSome", exports.Constructors);
    }

    [Fact]
    public void ExtractExportsForTest_ExportImportModuleAlias_ReexportsModuleAndTraitMethods()
    {
        var moduleSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Core/Applicative"] = """
Core.Applicative :: module {
    export Applicative[F: kind2] :: trait {
        pure[A] :: A -> F[A]
        apply[A, B] :: F[A -> B] -> F[A] -> F[B]
    }
}
""",
            ["Demo/Facade"] = """
Demo.Facade :: module {
    export App :: import Core.Applicative;
}
"""
        };

        var exports = PrecompiledModuleRegistry.ExtractExportsForTest(
            moduleSources["Demo/Facade"],
            "Demo/Facade",
            moduleSources);

        Assert.True(exports.Modules.TryGetValue("App", out var targetModulePath));
        Assert.Equal("Core/Applicative", targetModulePath);
        Assert.Contains("pure", exports.Functions);
        Assert.Contains("apply", exports.Functions);
        Assert.True(PrecompiledModuleRegistry.ExportedOwnerDefinesMemberForTest(
            moduleSources["Demo/Facade"],
            "App",
            "pure",
            "Demo/Facade",
            moduleSources));
    }

    [Fact]
    public void ExtractExportsForTest_SelectiveReexport_AliasesEffectAndTypeOwners()
    {
        var moduleSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Core/Io"] = """
Core.Io :: module {
    export Writer :: effect;

    export write :: String -> Int need Writer
    {
        _ => 0
    }
}
""",
            ["Core/Option"] = """
Core.Option :: module {
    export Option[T] :: type { Some:: type(T) , None :: type {} }
}
""",
            ["Demo/Facade"] = """
Demo.Facade :: module {
    export import Core.Io.{Writer as W, write}
    export import Core.Option.{Option as Maybe}
}
"""
        };

        var exports = PrecompiledModuleRegistry.ExtractExportsForTest(
            moduleSources["Demo/Facade"],
            "Demo/Facade",
            moduleSources);

        Assert.Contains("W", exports.Effects);
        Assert.Contains("write", exports.Functions);
        Assert.Contains("Maybe", exports.Types);
        Assert.Contains("None", exports.Constructors);
        Assert.Contains("Some", exports.Constructors);
        Assert.True(PrecompiledModuleRegistry.ExportedOwnerDefinesMemberForTest(
            moduleSources["Demo/Facade"],
            "Maybe",
            "Some",
            "Demo/Facade",
            moduleSources));
    }
}
