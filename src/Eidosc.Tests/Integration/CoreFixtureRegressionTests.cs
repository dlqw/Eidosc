using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

[Trait(TestCategories.Category, TestCategories.Integration)]
public class CoreFixtureRegressionTests
{
    public static IEnumerable<object[]> NonErrorFixtures() => EidosFixtureInventory.BorrowPhaseSuccessTheoryData();

    public static IEnumerable<object[]> ErrorFixtures() => EidosFixtureInventory.ErrorTheoryData();

    public static IEnumerable<object[]> FixtureCoverage() => EidosFixtureInventory.FixtureCoverageTheoryData();

    [Theory]
    [MemberData(nameof(NonErrorFixtures))]
    public void NonErrorFixtures_CompileThroughBorrowPhase(string fixturePath)
    {
        CompilationHelper.Fixture(fixturePath)
            .ToPhase(CompilationPhase.Borrow)
            .ShouldCompletePhaseWithoutErrors(CompilationPhase.Borrow);
    }

    [Theory]
    [MemberData(nameof(ErrorFixtures))]
    public void ErrorFixtures_ReportExpectedErrorFamilies(string fixturePath, string[] expectedCodes)
    {
        CompilationHelper.Fixture(fixturePath, isolateSingleFile: true)
            .ShouldReport(expectedCodes);
    }

    [Theory]
    [MemberData(nameof(FixtureCoverage))]
    public void FixtureCoverageMatrix_EntriesPointToExistingFixtures(EidosFixtureCoverage coverage)
    {
        Assert.True(File.Exists(TestSourceLoader.GetFullPath(coverage.ProjectRelativePath)));
        Assert.NotEqual(EidosFixtureCoverageLayer.None, coverage.Layers);
        Assert.False(string.IsNullOrWhiteSpace(coverage.Owner));
        Assert.False(string.IsNullOrWhiteSpace(coverage.KeepReason));
    }
}
