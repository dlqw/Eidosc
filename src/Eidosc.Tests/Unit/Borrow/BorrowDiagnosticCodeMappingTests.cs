using System.Reflection;
using Eidosc.Borrow;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Borrow;

public class BorrowDiagnosticCodeMappingTests
{
    [Theory]
    [InlineData(BorrowErrorKind.MultipleMutableBorrows, "E1002")]
    [InlineData(BorrowErrorKind.ReborrowAsMutable, "E1002")]
    [InlineData(BorrowErrorKind.MutableWhileImmutableBorrowed, "E1002")]
    [InlineData(BorrowErrorKind.ImmutableWhileMutableBorrowed, "E1002")]
    [InlineData(BorrowErrorKind.MutateWhileBorrowed, "E1002")]
    [InlineData(BorrowErrorKind.UseAfterMove, "E1001")]
    [InlineData(BorrowErrorKind.BorrowedWhileReturned, "E1004")]
    [InlineData(BorrowErrorKind.ReadCapabilityDenied, "E1011")]
    [InlineData(BorrowErrorKind.WriteCapabilityDenied, "E1012")]
    [InlineData(BorrowErrorKind.MoveCapabilityDenied, "E1013")]
    public void MapBorrowCode_ReturnsExpectedErrorCode(BorrowErrorKind kind, string expectedCode)
    {
        var method = typeof(CompilationPipeline).GetMethod("MapBorrowCode", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var actualCode = method!.Invoke(null, [kind]) as string;

        Assert.Equal(expectedCode, actualCode);
    }
}
