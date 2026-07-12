using Eidosc.Borrow;
using Xunit;

namespace Eidosc.Tests.Unit.Borrow;

public class StructBorrowAnalyzerTests
{
    [Fact]
    public void SetFields_WithFields_ReturnsSelf()
    {
        var analyzer = new StructBorrowAnalyzer();
        var result = analyzer.SetFields(["x", "y"]);

        Assert.Same(analyzer, result);
    }

    [Fact]
    public void AnalyzePartialBorrow_SingleField_MarksAsBorrowed()
    {
        var analyzer = new StructBorrowAnalyzer()
            .SetFields(["x", "y", "z"]);

        var result = analyzer.AnalyzePartialBorrow("x");

        Assert.Contains("x", result.BorrowedFields);
        Assert.DoesNotContain("x", result.RemainingFields);
    }

    [Fact]
    public void AnalyzePartialBorrow_SingleField_OthersRemainAvailable()
    {
        var analyzer = new StructBorrowAnalyzer()
            .SetFields(["x", "y", "z"]);

        var result = analyzer.AnalyzePartialBorrow("x");

        Assert.Contains("y", result.RemainingFields);
        Assert.Contains("z", result.RemainingFields);
    }

    [Fact]
    public void AnalyzePartialBorrow_MultipleFields_TracksAllBorrowed()
    {
        var analyzer = new StructBorrowAnalyzer()
            .SetFields(["x", "y", "z"]);

        analyzer.AnalyzePartialBorrow("x");
        var result = analyzer.AnalyzePartialBorrow("y");

        Assert.Contains("x", result.BorrowedFields);
        Assert.Contains("y", result.BorrowedFields);
        Assert.DoesNotContain("z", result.BorrowedFields);
        Assert.Contains("z", result.RemainingFields);
    }

    [Fact]
    public void AnalyzeFullBorrow_AllFieldsBorrowed()
    {
        var analyzer = new StructBorrowAnalyzer()
            .SetFields(["x", "y", "z"]);

        var result = analyzer.AnalyzeFullBorrow();

        Assert.Equal(3, result.BorrowedFields.Count);
        Assert.Empty(result.RemainingFields);
    }

    [Fact]
    public void IsFieldAvailable_NotBorrowed_ReturnsTrue()
    {
        var analyzer = new StructBorrowAnalyzer()
            .SetFields(["x", "y"]);

        Assert.True(analyzer.IsFieldAvailable("x"));
    }

    [Fact]
    public void IsFieldAvailable_AfterPartialBorrow_ReturnsFalseForBorrowed()
    {
        var analyzer = new StructBorrowAnalyzer()
            .SetFields(["x", "y"]);

        analyzer.AnalyzePartialBorrow("x");

        Assert.False(analyzer.IsFieldAvailable("x"));
        Assert.True(analyzer.IsFieldAvailable("y"));
    }
}
