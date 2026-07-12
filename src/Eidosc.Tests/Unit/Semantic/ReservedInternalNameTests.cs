using System.Collections.Generic;
using System.Linq;
using Eidosc.Diagnostic;
using Eidosc.Ide;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public class ReservedInternalNameTests
{
    public static IEnumerable<object[]> ReservedDeclarationCases()
    {
        yield return
        [
            "specialization marker",
            """
foo__spec_bar :: Int -> Int
{
    x => x
}
"""
        ];
    }
    [Theory]
    [MemberData(nameof(ReservedDeclarationCases))]
    public void CompilationPipeline_UserDeclarationWithReservedInternalName_ReportsE3055(
        string _,
        string source)
    {
        var result = RunNamer(source);

        Assert.False(result.Success);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error && diagnostic.Code == "E3055");
        Assert.Contains("compiler-reserved internal prefix", diagnostic.Message);
        Assert.Contains(
            diagnostic.Helps,
            help => help.Contains("reserved for generated code", StringComparison.Ordinal));
    }

    [Fact]
    public void IdeSemanticSnapshot_ReservedInternalNameDiagnostic_MapsCodeAndHelp()
    {
        const string source = "foo__spec_bar :: Int -> Int { x => x };";
        var result = RunNamer(source);

        Assert.False(result.Success);

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var diagnostic = Assert.Single(snapshot.Diagnostics, item => item.Code == "E3055");
        Assert.Equal("error", diagnostic.Severity);
        Assert.Contains("compiler-reserved internal prefix", diagnostic.Message);
        Assert.NotNull(diagnostic.Span);
    }

    private static CompilationResult RunNamer(string source)
    {
        return new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "reserved_internal_name_tests.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();
    }
}
