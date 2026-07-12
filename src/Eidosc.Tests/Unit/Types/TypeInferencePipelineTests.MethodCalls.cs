using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

public partial class TypeInferencePipelineTests
{
    [Fact]
    public void Types_MethodCall_UsesReceiverTypeToSelectImportedCandidate()
    {
        const string source = """
import Std::Seq
import Std::Option

use_append :: Unit -> Seq[Int]
{
    _ => [1].append([2])
}
""";

        var result = RunPipeline(
            source,
            CompilationPhase.Types,
            options => options.InputFile = TestSourceLoader.GetFullPath(
                TestPathConfig.Current.TutorialExample("29_precompiled_stdlib.eidos")));

        Assert.True(result.Success, result.Diagnostics.Count > 0
            ? string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message))
            : "Expected success");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_MethodCall_UsesReceiverTypeToSelectPrecompiledCandidateWithoutImport()
    {
        const string source = """
use_append :: Unit -> Seq[Int]
{
    _ => [1].append([2])
}
""";

        var result = RunPipeline(
            source,
            CompilationPhase.Types,
            options => options.InputFile = TestSourceLoader.GetFullPath(
                TestPathConfig.Current.TutorialExample("29_precompiled_stdlib.eidos")));

        Assert.True(result.Success, result.Diagnostics.Count > 0
            ? string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message))
            : "Expected success");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_MethodCall_SelectsReceiverModuleCandidateAcrossSameNamedImports()
    {
        const string source = """
import Std::Mutex
import Std::RwLock

select_mutex_status :: Mutex::Status -> Int
{
    status => status.status_value_or(10)(20)
}

select_rwlock_status :: RwLock::Status -> Int
{
    status => status.status_value_or(10)(20)(30)
}
""";

        var result = RunPipeline(
            source,
            CompilationPhase.Types,
            options => options.InputFile = TestSourceLoader.GetFullPath(
                TestPathConfig.Current.Fixture("stdlib/std_sync_runtime_import.eidos")));

        Assert.True(result.Success, result.Diagnostics.Count > 0
            ? string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message))
            : "Expected success");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }
}

