using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

public sealed class TypeInfererGovernanceTests
{
    private const int MaxLinesPerPartial = 2_000;

    [Fact]
    public void TypeInfererPartials_StayBelowGovernanceThreshold()
    {
        var typesRoot = TestSourceLoader.GetFullPath("Eidosc/src/Eidosc/Types");
        var files = Directory
            .EnumerateFiles(typesRoot, "TypeInferer*.cs", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(files);

        var offenders = files
            .Select(path => new
            {
                Path = Path.GetRelativePath(typesRoot, path),
                Lines = File.ReadLines(path).Count()
            })
            .Where(file => file.Lines >= MaxLinesPerPartial)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"TypeInferer partial files must stay below {MaxLinesPerPartial} lines: {string.Join(", ", offenders.Select(file => $"{file.Path} ({file.Lines})"))}");
    }
}
