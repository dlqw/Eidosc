using Eidosc.Symbols;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Eidosc;
using Eidosc.Diagnostic;
using Eidosc.CodeGen;
using Eidosc.CodeGen.Llvm;
using Eidosc.Mir;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    public void StdBinaryImportFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_binary_import.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Binary__decode_bool", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Binary__decode_u32_le", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Binary__normalize_bytes", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Binary__checksum8", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Binary__decode_i32_be", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Binary__decode_string", StringComparison.Ordinal));
    }

    [Fact]
    public void StdJsonImportFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_json_import.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Json__hex_digit", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Json__escape_control_byte", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Json__escape_string", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Json__array_compact", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Json__field_or_null", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Json__field_or_null_compact", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Json__object_from_options", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Json__object_from_options_compact", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Json__object_from_pairs_compact", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Json__array_strings", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Json__array_strings_compact", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Json__array_ints_compact", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Json__array_bools", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Json__array_bools_compact", StringComparison.Ordinal));
    }

    [Fact]
    public void GenericIndirectFunctionReferenceFixture_RewritesToSpecializedCallWithoutE5301()
    {
        var result = RunFixtureAtLlvm(Fx("types/generic_zero_arg_partial_indirect.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Code == "E5301");
    }

    [Fact]
    public void GenericNonZeroArgPartialIndirectFixture_CompilesThroughLlvmWithoutE5301()
    {
        var result = RunFixtureAtLlvm(Fx("types/generic_nonzero_partial_indirect.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Code == "E5301");
    }

    [Fact]
    public void GenericNonZeroArgPartialTraitCopyIndirectFixture_CompilesThroughLlvmWithoutE5301()
    {
        var result = RunFixtureAtLlvm(Fx("types/generic_nonzero_partial_trait_copy_indirect.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Code == "E5301");
    }

    [Fact]
    public void GenericNonZeroArgPartialTraitCopyCtorFieldIndirectFixture_CompilesThroughLlvmWithoutBorrowOrGenericLoweringErrors()
    {
        var result = RunFixtureAtLlvm(Fx("types/generic_nonzero_partial_trait_copy_ctor_field_indirect.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          (diagnostic.Code == "E5301" ||
                           diagnostic.Code == "E1001" ||
                           diagnostic.Code == "E1002" ||
                           diagnostic.Code == "E1004"));
    }

    [Fact]
    public void GenericNonZeroArgPartialTraitCopyDerefIndirectFixture_CompilesThroughLlvmWithoutBorrowOrGenericLoweringErrors()
    {
        var result = RunFixtureAtLlvm(Fx("types/generic_nonzero_partial_trait_copy_deref_indirect.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          (diagnostic.Code == "E5301" ||
                           diagnostic.Code == "E1001" ||
                           diagnostic.Code == "E1002" ||
                           diagnostic.Code == "E1004"));
    }

    [Fact]
    public void GenericNonZeroArgPartialTraitCopyDynamicIndexIndirectFixture_CompilesThroughLlvmWithoutBorrowOrGenericLoweringErrors()
    {
        var result = RunFixtureAtLlvm(Fx("types/generic_nonzero_partial_trait_copy_dynamic_index_indirect.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          (diagnostic.Code == "E5301" ||
                           diagnostic.Code == "E1001" ||
                           diagnostic.Code == "E1004"));
    }

    [Fact]
    public void DistinctModuleFunctionsWithSameName_AreLoweredToDistinctLlvmDefinitions()
    {
        const string source = """
A :: module {
    f :: Int -> Int { x => x + 1 }
}

B :: module {
    f :: Int -> Int { x => x + 100 }
}

main :: Unit -> Int
{
    _ => A::f(1) + B::f(1)
}
""";

        var result = RunSourceAtLlvm(source, "llvm_module_function_name_collision.eidos");

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        var llvmIr = Assert.IsType<string>(result.LlvmIrText);
        Assert.Contains("@eidos_A__f", llvmIr, StringComparison.Ordinal);
        Assert.Contains("@eidos_B__f", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void EccMainTemplate_CompilesThroughLlvmWithPreludeImport()
    {
        var result = RunFixtureAtLlvm(Ecc("src/main.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("not", StringComparison.Ordinal));
    }

    [Fact]
    public void EccMainTemplate_NativeRuntimeSmoke_SucceedsWhenToolchainAvailable()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        var result = RunFixtureAtLlvm(Ecc("src/main.eidos"));
        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.NotNull(result.LlvmModule);

        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_ecc_main_native_smoke_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var targetInfo = TargetInfo.Default;
            var runtimeObjectPath = GetCachedRuntimeObjectPath(targetInfo);

            var executablePath = Path.Combine(
                tempDir,
                OperatingSystem.IsWindows() ? "ecc_main_native_smoke.exe" : "ecc_main_native_smoke");
            var compiler = CreateLlvmCompiler(targetInfo, runtimeObjectPath, tempDir);
            var nativeResult = compiler.CompileToExecutable(result.LlvmModule!, executablePath);

            Assert.True(nativeResult.Success, nativeResult.ErrorMessage);
            Assert.True(File.Exists(executablePath));

            var execution = ExecuteProcess(executablePath, workingDirectory: tempDir);
            Assert.Equal(0, execution.ExitCode);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures on CI/Windows file lock races.
            }
        }
    }

    [Fact]
    public void StdPreludeImportFixture_NativeRuntimeSmoke_ReturnsExpectedExitCode_WhenToolchainAvailable()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        var result = RunFixtureAtLlvm(Fx("stdlib/std_prelude_import.eidos"));
        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.NotNull(result.LlvmModule);

        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_prelude_native_smoke_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var targetInfo = TargetInfo.Default;
            var runtimeObjectPath = GetCachedRuntimeObjectPath(targetInfo);

            var executablePath = Path.Combine(
                tempDir,
                OperatingSystem.IsWindows() ? "std_prelude_native_smoke.exe" : "std_prelude_native_smoke");
            var compiler = CreateLlvmCompiler(targetInfo, runtimeObjectPath, tempDir);
            var nativeResult = compiler.CompileToExecutable(result.LlvmModule!, executablePath);

            Assert.True(nativeResult.Success, nativeResult.ErrorMessage);
            Assert.True(File.Exists(executablePath));

            var execution = ExecuteProcess(executablePath, workingDirectory: tempDir);
            Assert.Equal(12, execution.ExitCode);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures on CI/Windows file lock races.
            }
        }
    }

    [Theory]
    [MemberData(nameof(StdlibCapabilityNativeSmokeFixtures))]
    public void StdlibCapabilityFixtures_NativeRuntimeSmoke_ReturnExpectedExitCode_WhenToolchainAvailable(
        string fixturePath,
        string executableBaseName,
        int expectedExitCode)
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        // Network stdlib requires curl.exe for the subprocess HTTP backend.
        if (fixturePath.Contains("std_network_import", StringComparison.Ordinal) &&
            !ToolExists("curl"))
        {
            return;
        }

        var result = RunFixtureAtLlvm(fixturePath);
        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.NotNull(result.LlvmModule);

        var tempDir = Path.Combine(Path.GetTempPath(), $"{executableBaseName}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var targetInfo = TargetInfo.Default;
            var runtimeObjectPath = GetCachedRuntimeObjectPath(targetInfo);

            var executablePath = Path.Combine(
                tempDir,
                OperatingSystem.IsWindows() ? $"{executableBaseName}.exe" : executableBaseName);
            var compiler = CreateLlvmCompiler(targetInfo, runtimeObjectPath, tempDir);
            var nativeResult = compiler.CompileToExecutable(result.LlvmModule!, executablePath);

            Assert.True(nativeResult.Success, nativeResult.ErrorMessage);
            Assert.True(File.Exists(executablePath));

            var isNetworkTest = fixturePath.Contains("std_network_import", StringComparison.Ordinal);
            var execution = ExecuteProcess(
                executablePath,
                workingDirectory: tempDir,
                timeoutMs: isNetworkTest ? 90_000 : 30_000,
                environmentVariables: isNetworkTest ? CreateHttpEnvironment(httpBackend: null) : null);

            Assert.Equal(
                expectedExitCode,
                execution.ExitCode);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures on CI/Windows file lock races.
            }
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void StdlibRuntimeImportFixtures_SourceNativeSoak_RunRepeatedly_WhenToolchainAvailable()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const int repetitions = 5;
        var fixturePaths = EidosFixtureInventory.StdlibFixtureFiles()
            .Where(static path => Path.GetFileName(path).EndsWith("_runtime_import.eidos", StringComparison.Ordinal))
            .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(fixturePaths);

        var targetInfo = TargetInfo.Default;
        var runtimeDirectory = TestSourceLoader.GetFullPath("Eidosc/src/Eidosc/Runtime");

        foreach (var fixturePath in fixturePaths)
        {
            var result = RunFixtureAtLlvm(fixturePath);
            Assert.True(
                result.Success,
                string.Join(
                    Environment.NewLine,
                    result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
            Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
            Assert.NotNull(result.LlvmModule);

            var executableBaseName = Path.GetFileNameWithoutExtension(fixturePath);
            var tempDir = Path.Combine(Path.GetTempPath(), $"{executableBaseName}_soak_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                var executablePath = Path.Combine(
                    tempDir,
                    OperatingSystem.IsWindows() ? $"{executableBaseName}.exe" : executableBaseName);
                var compiler = CreateLlvmCompiler(targetInfo, runtimeDirectory, tempDir);
                var nativeResult = compiler.CompileToExecutable(result.LlvmModule!, executablePath);

                Assert.True(nativeResult.Success, nativeResult.ErrorMessage);
                Assert.True(File.Exists(executablePath));

                int? expectedExitCode = null;
                for (var iteration = 0; iteration < repetitions; iteration++)
                {
                    var execution = ExecuteProcess(executablePath, workingDirectory: tempDir, timeoutMs: 30_000);
                    Assert.True(
                        execution.ExitCode is >= 0 and < 128,
                        $"{executableBaseName} iteration {iteration + 1} exited with {execution.ExitCode}.{Environment.NewLine}{execution.StandardError}");

                    expectedExitCode ??= execution.ExitCode;
                    Assert.Equal(expectedExitCode.Value, execution.ExitCode);
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch
                {
                    // Ignore cleanup failures on CI/Windows file lock races.
                }
            }
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void StdlibMixedConcurrencyRuntimeImportFixtures_SourceNativePressureSoak_WhenToolchainAvailable()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const int repetitions = 25;
        var fixturePaths = EidosFixtureInventory.StdlibFixtureFiles()
            .Where(static path => Path.GetFileName(path).EndsWith("_runtime_import.eidos", StringComparison.Ordinal))
            .Where(static path => ImportsAllConcurrencyRuntimeModules(File.ReadAllText(path)))
            .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(fixturePaths);

        var targetInfo = TargetInfo.Default;
        var runtimeDirectory = TestSourceLoader.GetFullPath("Eidosc/src/Eidosc/Runtime");

        foreach (var fixturePath in fixturePaths)
        {
            var result = RunFixtureAtLlvm(fixturePath);
            Assert.True(
                result.Success,
                string.Join(
                    Environment.NewLine,
                    result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
            Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
            Assert.NotNull(result.LlvmModule);

            var executableBaseName = Path.GetFileNameWithoutExtension(fixturePath);
            var tempDir = Path.Combine(Path.GetTempPath(), $"{executableBaseName}_mixed_pressure_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                var executablePath = Path.Combine(
                    tempDir,
                    OperatingSystem.IsWindows() ? $"{executableBaseName}.exe" : executableBaseName);
                var compiler = CreateLlvmCompiler(targetInfo, runtimeDirectory, tempDir);
                var nativeResult = compiler.CompileToExecutable(result.LlvmModule!, executablePath);

                Assert.True(nativeResult.Success, nativeResult.ErrorMessage);
                Assert.True(File.Exists(executablePath));

                int? expectedExitCode = null;
                for (var iteration = 0; iteration < repetitions; iteration++)
                {
                    var execution = ExecuteProcess(executablePath, workingDirectory: tempDir, timeoutMs: 30_000);
                    Assert.True(
                        execution.ExitCode is > 0 and < 128,
                        $"{executableBaseName} pressure iteration {iteration + 1} exited with {execution.ExitCode}.{Environment.NewLine}{execution.StandardError}");

                    expectedExitCode ??= execution.ExitCode;
                    Assert.Equal(expectedExitCode.Value, execution.ExitCode);
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch
                {
                    // Ignore cleanup failures on CI/Windows file lock races.
                }
            }
        }
    }

    private static bool ImportsAllConcurrencyRuntimeModules(string source)
    {
        ReadOnlySpan<string> requiredImports =
        [
            "import Std::Async",
            "import Std::Barrier",
            "import Std::Channel",
            "import Std::Mutex",
            "import Std::Promise",
            "import Std::RwLock",
            "import Std::Task",
            "import Std::TaskGroup"
        ];

        foreach (var requiredImport in requiredImports)
        {
            if (!source.Contains(requiredImport, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    [Fact]
    public void ListComprehensionMainFixture_LoopLocalsUseSlotLoadStoreAcrossBackedge()
    {
        var result = RunFixtureAtLlvm(Fx("control/list_comp_main.eidos"));
        var llvmIr = Assert.IsType<string>(result.LlvmIrText);
        var irLines = llvmIr
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .ToList();
        var mainStart = irLines.FindIndex(line => line.StartsWith("define external i64 @eidos_main", StringComparison.Ordinal));
        Assert.True(mainStart >= 0, "Expected eidos_main definition in LLVM IR.");
        var mainEnd = irLines.FindIndex(mainStart + 1, line => line.StartsWith("define external", StringComparison.Ordinal));
        var mainLines = (mainEnd >= 0
                ? irLines.Skip(mainStart).Take(mainEnd - mainStart)
                : irLines.Skip(mainStart))
            .ToList();

        Assert.Contains(mainLines, line => line.Contains("alloca i64", StringComparison.Ordinal) &&
                                           line.Contains("l4_slot", StringComparison.Ordinal));
        Assert.Contains(mainLines, line => line.Contains("load i64, ptr %l4_slot", StringComparison.Ordinal));
        Assert.Contains(mainLines, line => line.Contains("store i64", StringComparison.Ordinal) &&
                                           line.Contains(", ptr %l4_slot", StringComparison.Ordinal));

        var pointerSlotLine = Assert.Single(
            mainLines,
            line => line.Contains("alloca ptr", StringComparison.Ordinal) &&
                    line.Contains("_slot", StringComparison.Ordinal) &&
                    !line.Contains("arr_slot", StringComparison.Ordinal));
        var slotNameStart = pointerSlotLine.IndexOf("%", StringComparison.Ordinal);
        var slotStemEnd = pointerSlotLine.IndexOf("_slot", StringComparison.Ordinal);
        Assert.True(slotNameStart >= 0 && slotStemEnd > slotNameStart, $"Unexpected pointer slot line: {pointerSlotLine}");
        var pointerLocalStem = pointerSlotLine.Substring(slotNameStart + 1, slotStemEnd - slotNameStart - 1);

        Assert.Contains(mainLines, line => line.Contains("@eidos_array_push", StringComparison.Ordinal) &&
                                           line.Contains($"ptr %{pointerLocalStem}_ld", StringComparison.Ordinal));
        Assert.Contains(mainLines, line => line.Contains($"store ptr %{pointerLocalStem}_push", StringComparison.Ordinal) &&
                                           line.Contains($", ptr %{pointerLocalStem}_slot", StringComparison.Ordinal));
    }

    [Fact]
    public void ListComprehensionMainFixture_MirGeneratorLoadUsesIntElementType()
    {
        var result = RunFixtureAtMir(Fx("control/list_comp_main.eidos"));
        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Mir, result.CompletedPhase);

        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        var func = Assert.Single(mirModule.Functions, function => function.Name == "main");
        var generatorLoad = func.BasicBlocks
            .SelectMany(block => block.Instructions)
            .OfType<MirLoad>()
            .First(load => load.Source is MirPlace { Kind: PlaceKind.Index, IndexAccessKind: MirIndexAccessKind.RuntimeArray });
        var loadSource = Assert.IsType<MirPlace>(generatorLoad.Source);

        Assert.Equal(new TypeId(BaseTypes.IntId), loadSource.TypeId);
    }

    [Fact]
    public void ListComprehensionMainFixture_LlvmIrCompilesToObject_WhenToolchainAvailable()
    {
        if (!ToolExists("clang") && !ToolExists("llc"))
        {
            return;
        }

        var result = RunFixtureAtLlvm(Fx("control/list_comp_main.eidos"));
        Assert.True(result.Success);
        var llvmIr = Assert.IsType<string>(result.LlvmIrText);

        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_list_comp_main_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var objectPath = Path.Combine(tempDir, OperatingSystem.IsWindows() ? "list_comp_main.obj" : "list_comp_main.o");
        try
        {
            var compiler = new LlvmCompiler(TargetInfo.Default, temporaryDirectory: tempDir);
            var compileResult = compiler.CompileToObject(llvmIr, objectPath);

            Assert.True(compileResult.Success, compileResult.ErrorMessage ?? "CompileToObject failed.");
            Assert.True(File.Exists(objectPath));
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures on CI/Windows file lock races.
            }
        }
    }

    [Fact]
    public void ListComprehensionMainFixture_NativeRuntimeSmoke_SucceedsWhenToolchainAvailable()
    {
        // Native smoke 依赖 clang；llc 可缺省（LlvmCompiler 会走 clang -x ir fallback）。
        if (!ToolExists("clang"))
        {
            return;
        }

        var result = RunFixtureAtLlvm(Fx("control/list_comp_main.eidos"));
        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.NotNull(result.LlvmModule);

        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_native_smoke_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var targetInfo = TargetInfo.Default;
            var runtimeObjectPath = GetCachedRuntimeObjectPath(targetInfo);

            var executablePath = Path.Combine(
                tempDir,
                OperatingSystem.IsWindows() ? "list_comp_native_smoke.exe" : "list_comp_native_smoke");
            var compiler = CreateLlvmCompiler(targetInfo, runtimeObjectPath, tempDir);
            var nativeResult = compiler.CompileToExecutable(result.LlvmModule!, executablePath);

            Assert.True(nativeResult.Success, nativeResult.ErrorMessage);
            Assert.True(File.Exists(executablePath));

            var execution = ExecuteProcess(executablePath, workingDirectory: tempDir);
            Assert.True(
                execution.ExitCode == 0,
                $"Executable exited with code {execution.ExitCode}. stderr: {execution.StandardError}");
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures on CI/Windows file lock races.
            }
        }
    }

    [Fact]
    public void ListComprehensionMainFixture_NativeRuntimeSourceDirectoryFallback_SucceedsWhenToolchainAvailable()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        var result = RunFixtureAtLlvm(Fx("control/list_comp_main.eidos"));
        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.NotNull(result.LlvmModule);

        var runtimeSource = ResolveRuntimeSourcePath();
        var runtimeDir = Path.GetDirectoryName(runtimeSource);
        Assert.False(string.IsNullOrWhiteSpace(runtimeDir));

        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_native_runtime_src_fallback_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var executablePath = Path.Combine(
                tempDir,
                OperatingSystem.IsWindows() ? "list_comp_native_runtime_src_fallback.exe" : "list_comp_native_runtime_src_fallback");
            var compiler = CreateLlvmCompiler(TargetInfo.Default, runtimeDir, tempDir);
            var nativeResult = compiler.CompileToExecutable(result.LlvmModule!, executablePath);

            Assert.True(nativeResult.Success, nativeResult.ErrorMessage);
            Assert.True(File.Exists(executablePath));

            var execution = ExecuteProcess(executablePath, workingDirectory: tempDir);
            Assert.True(
                execution.ExitCode == 0,
                $"Executable exited with code {execution.ExitCode}. stderr: {execution.StandardError}");
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures on CI/Windows file lock races.
            }
        }
    }

    [Fact]
    public void RuntimeHttpBackendSelection_DefaultsToLibcurl_WhenAvailable()
    {
        if (!ToolExists("clang") || !LibcurlSmokeEnabled())
        {
            return;
        }

        var baseExtraCFlags = Environment.GetEnvironmentVariable("EIDOS_RUNTIME_EXTRA_CFLAGS");
        var extraLdFlags = Environment.GetEnvironmentVariable("EIDOS_RUNTIME_EXTRA_LDFLAGS");
        if (string.IsNullOrWhiteSpace(baseExtraCFlags) || string.IsNullOrWhiteSpace(extraLdFlags))
        {
            return;
        }

        var runtimeSource = ResolveRuntimeSourcePath();
        var runtimeDir = Path.GetDirectoryName(runtimeSource);
        Assert.False(string.IsNullOrWhiteSpace(runtimeDir));

        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_http_backend_select_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var sourcePath = Path.Combine(tempDir, "http_backend_select_smoke.c");
            var executablePath = Path.Combine(
                tempDir,
                OperatingSystem.IsWindows() ? "http_backend_select_smoke.exe" : "http_backend_select_smoke");
            File.WriteAllText(
                sourcePath,
                """
                #include "eidos_runtime.h"
                #include <stdint.h>
                #include <stdlib.h>
                #include <string.h>

                #if defined(_WIN32)
                #define SET_ENV(value) _putenv(value)
                #else
                #include <unistd.h>
                static int set_env_text(const char* value) {
                    const char* equals = strchr(value, '=');
                    if (equals == NULL) {
                        return -1;
                    }

                    size_t name_length = (size_t)(equals - value);
                    char name[128];
                    if (name_length >= sizeof(name)) {
                        return -1;
                    }

                    memcpy(name, value, name_length);
                    name[name_length] = '\0';
                    return setenv(name, equals + 1, 1);
                }
                #define SET_ENV(value) set_env_text(value)
                #endif

                int main(void) {
                    if (eidos_http_backend_selected_kind() != 2) {
                        return 10;
                    }

                    if (SET_ENV("EIDOS_HTTP_BACKEND=curl") != 0) {
                        return 11;
                    }

                    if (eidos_http_backend_selected_kind() != 1) {
                        return 12;
                    }

                    if (SET_ENV("EIDOS_HTTP_BACKEND=definitely-not-a-backend") != 0) {
                        return 13;
                    }

                    if (eidos_http_backend_selected_kind() != 2) {
                        return 14;
                    }

                    if (SET_ENV("EIDOS_HTTP_BACKEND=default") != 0) {
                        return 15;
                    }

                    if (eidos_http_backend_selected_kind() != 2) {
                        return 16;
                    }

                    return 0;
                }
                """);

            var targetInfo = TargetInfo.Default;
            var clangPath = ResolveToolPath("clang");
            Assert.NotNull(clangPath);

            var argumentsBuilder = new StringBuilder()
                .Append($"-target {targetInfo.Triple} ")
                .Append(baseExtraCFlags).Append(' ')
                .Append($"-I \"{runtimeDir}\" \"{sourcePath}\" \"{runtimeSource}\" ")
                .Append(extraLdFlags).Append(' ')
                .Append($"-o \"{executablePath}\"");
            var compileResult = ExecuteProcess(clangPath!, argumentsBuilder.ToString(), workingDirectory: tempDir);
            Assert.True(
                compileResult.ExitCode == 0,
                $"clang failed with code {compileResult.ExitCode}: {compileResult.StandardError}");
            Assert.True(File.Exists(executablePath));

            var execution = ExecuteProcess(executablePath, workingDirectory: tempDir);
            Assert.Equal(0, execution.ExitCode);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures on CI/Windows file lock races.
            }
        }
    }

    [Fact]
    public void StdNetworkLocalSuccessAndRedirect_DefaultLibcurlBackend_StillAllowsExplicitCurlOverride()
    {
        if (!ToolExists("clang") || !LibcurlSmokeEnabled())
        {
            return;
        }

        using var server = new LoopbackHttpServer();
        var baseUrl = EscapeEidosStringLiteral(server.BaseUrl);

        var source = $$"""
import Std::Network
import Std::Option
import Std::Text

main :: Unit -> Int
{
    _ => {
        redirect := Network::http_get_response("{{baseUrl}}redirect");
        replyHeader := Network::http_get_response("{{baseUrl}}reply-header");
        headerBit := match Network::header_value_opt(replyHeader)("X-Reply")
        {
            Some(value) => if value == "server-value" then { 1 } else { 0 },
            None() => 0
        };
        redirectBit := if Network::status(redirect) == 200 &&
            Network::body(redirect) == "hello-from-eidos" &&
            Text::ends_with(Network::effective_url(redirect))("/ok")
            then { 1 } else { 0 };

        if redirectBit + headerBit == 2
            then { 0 }
            else { 1 }
    }
}
""";

        var preferDefaultExecution = CompileAndRunSourceAtNativeWithHttpBackend(
            source,
            "network_local_prefer_libcurl_default.eidos",
            "network_local_prefer_libcurl_default",
            null);
        var explicitCurlExecution = CompileAndRunSourceAtNativeWithHttpBackend(
            source,
            "network_local_prefer_libcurl_explicit_curl.eidos",
            "network_local_prefer_libcurl_explicit_curl",
            "curl");

        Assert.Equal(0, preferDefaultExecution.ExitCode);
        Assert.Equal(preferDefaultExecution.ExitCode, explicitCurlExecution.ExitCode);
    }

    [Fact]
    public void StdNetworkImportFixture_DefaultLibcurlBackend_StillAllowsExplicitCurlOverride()
    {
        if (!ToolExists("clang") || !LibcurlSmokeEnabled())
        {
            return;
        }

        var preferDefaultExecution = CompileAndRunFixtureAtNativeWithHttpBackend(
            Fx("stdlib/std_network_import.eidos"),
            "std_network_prefer_libcurl_default",
            null);
        var explicitCurlExecution = CompileAndRunFixtureAtNativeWithHttpBackend(
            Fx("stdlib/std_network_import.eidos"),
            "std_network_prefer_libcurl_explicit_curl",
            "curl");

        Assert.Equal(22, preferDefaultExecution.ExitCode);
        Assert.Equal(preferDefaultExecution.ExitCode, explicitCurlExecution.ExitCode);
    }

}
