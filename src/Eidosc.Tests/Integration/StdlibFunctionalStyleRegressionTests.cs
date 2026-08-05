using System.Text.RegularExpressions;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed partial class StdlibFunctionalStyleRegressionTests
{
    private static readonly HashSet<string> ImperativeKernelFileNames =
    [
        "binary_heap.eidos",
        "functions.eidos",
        "hash_map.eidos",
        "seq.eidos",
        "seq_builder.eidos"
    ];

    [Fact]
    public void DerivedModules_KeepMutableControlFlowInsideExplicitKernels()
    {
        var violations = EidosFixtureInventory.StdlibPrecompiledFiles()
            .Where(static file => !ImperativeKernelFileNames.Contains(Path.GetFileName(file)))
            .SelectMany(static file => File.ReadLines(file)
                .Select((line, index) => new { File = file, Line = line, Number = index + 1 }))
            .Where(static item => ImperativeStatementRegex().IsMatch(item.Line))
            .Select(static item => $"{Path.GetFileName(item.File)}:{item.Number}: {item.Line.Trim()}")
            .ToArray();

        Assert.True(
            violations.Length == 0,
            $"Mutable control flow escaped the explicit Std implementation kernels:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    [Fact]
    public void ConcreteSequenceTransforms_UseFluentFunctionalComposition()
    {
        var violations = EidosFixtureInventory.StdlibPrecompiledFiles()
            .SelectMany(static file => File.ReadLines(file)
                .Select((line, index) => new { File = file, Line = line, Number = index + 1 }))
            .Where(static item => PrefixSequenceTransformRegex().IsMatch(item.Line))
            .Select(static item => $"{Path.GetFileName(item.File)}:{item.Number}: {item.Line.Trim()}")
            .ToArray();

        Assert.True(
            violations.Length == 0,
            $"Concrete sequence transforms must use fluent composition:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    [GeneratedRegex(@"^\s*(?:mut\s+|loop\b|while\b)", RegexOptions.CultureInvariant)]
    private static partial Regex ImperativeStatementRegex();

    [GeneratedRegex(@"\b(?:(?:Seq|Option|Result|Either|Functor)\.map|Seq\.(?:filter|flat_map|fold_left|fold_right))\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixSequenceTransformRegex();
}
