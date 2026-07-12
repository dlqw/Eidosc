using Eidosc.CodeGen;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void StdSeqBuilderToSeq_GenericSnapshotRequiresCloneConstraint()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
import Std::Seq
import Std::Trait
import Std::SeqBuilder

snapshot[A: Trait::Clone] :: SeqBuilder::SeqBuilder[A] -> Seq[A]
{
    vec => SeqBuilder::to_seq(vec)
}

main :: Unit -> Int
{
    _ => {
        vec := SeqBuilder::push(SeqBuilder::empty[Int](()))(41)
        Seq::get_or(snapshot[Int](vec))(0)(0)
    }
}
""";

        var execution = CompileAndRunSourceAtNativeWithContext(
            source,
            StdlibListImportInputFile(),
            "std_seq_builder_to_seq_clone_constraint");

        Assert.Equal(41, execution.ExitCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void StdSeqBuilder_PreservesAppendOrder()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
import Std::SeqBuilder

main :: Unit -> Int
{
    _ => {
        builder := SeqBuilder::push_seq(SeqBuilder::push(SeqBuilder::empty[Int](()))(1))([2, 3])
        xs := SeqBuilder::freeze(builder)
        xs[0] * 100 + xs[1] * 10 + xs[2]
    }
}
""";

        var execution = CompileAndRunSourceAtNativeWithContext(
            source,
            StdlibListImportInputFile(),
            "std_seq_builder_order");

        Assert.Equal(123, execution.ExitCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void StdHashMapHashSet_NonCopyStringOperationsCompileThroughLlvm()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
import Std::HashMap
import Std::HashSet

main :: Unit -> Int
{
    _ => {
        map0 := HashMap::empty[String, String](())
        map1 := HashMap::insert(map0)("alpha")("one")
        map2 := HashMap::insert(map1)("beta")("two")
        map3 := HashMap::insert(map2)("alpha")("uno")
        set0 := HashSet::from_seq[String](["alpha", "beta", "alpha"])
        if HashMap::len(map3) == 2 &&
           HashMap::get_or(map3)("alpha")("missing") == "uno" &&
           HashSet::len(set0) == 2 &&
           HashSet::contains(set0)("beta")
        then { 42 }
        else { 1 }
    }
}
""";

        var execution = CompileAndRunSourceAtNativeWithContext(
            source,
            StdlibListImportInputFile(),
            "std_hash_containers_noncopy_string");

        Assert.Equal(42, execution.ExitCode);
    }

    private static ProcessExecutionResult CompileAndRunSourceAtNativeWithContext(
        string source,
        string contextInputFile,
        string executableBaseName)
    {
        var result = RunSourceAtLlvm(source, contextInputFile);

        Assert.True(
            result.Success,
            $"Completed={result.CompletedPhase}, Errors={result.ErrorCount}, Warnings={result.WarningCount}{Environment.NewLine}" +
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
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

            return ExecuteProcess(executablePath, workingDirectory: tempDir);
        }
        finally
        {
            DeleteDirectoryQuietly(tempDir);
        }
    }
}
