using Eidosc.Ast.Declarations;
using Eidosc.Ast.Patterns;
using Eidosc.Cli.Lsp;
using Eidosc.Ide;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Eidosc.Symbols;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public sealed partial class MetaReflectionAndDeriveTests
{
    [Fact]
    public void LayoutReflection_RequiresExplicitTargetAndCompletedLayoutFact()
    {
        const string primitiveSource = """
IntLayout :: comptime meta.layout_of(Int, "x86_64-pc-windows-msvc");
AliasLayout :: comptime meta.layout_of(Int, "linux-x64");
""";
        var primitiveResult = Compile("meta_layout_primitive.eidos", primitiveSource);
        Assert.True(primitiveResult.Success, FormatDiagnostics(primitiveResult));
        var symbolTable = Assert.IsType<SymbolTable>(primitiveResult.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(primitiveResult.TypeInferer);
        var layout = Assert.IsType<ComptimeMetaObjectValue>(GetComptimeValue("IntLayout", symbolTable, inferer));
        Assert.Equal(
            WellKnownTypeIds.MetaLayoutId,
            Assert.IsType<TyCon>(layout.StaticType).Id.Value);
        Assert.True(layout.TryGet("size", out var size));
        Assert.Equal(8, Assert.IsType<ComptimeIntegerValue>(size).Value);
        var aliasLayout = Assert.IsType<ComptimeMetaObjectValue>(GetComptimeValue("AliasLayout", symbolTable, inferer));
        Assert.True(aliasLayout.TryGet("target", out var aliasTarget));
        Assert.Equal("x86_64-pc-linux-gnu", Assert.IsType<ComptimeStringValue>(aliasTarget).Value);

        const string incompleteSource = """
Payload :: type {
    value:: Int
}
PayloadLayout :: comptime meta.layout_of(Payload, "x86_64-pc-windows-msvc");
""";
        var incompleteResult = Compile("meta_layout_incomplete.eidos", incompleteSource);
        Assert.False(incompleteResult.Success);
        Assert.Contains(incompleteResult.Diagnostics, static diagnostic =>
            diagnostic.Code == "E4016" &&
            diagnostic.Message.Contains("not complete in the reflection phase", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeTrace_RecordsCallsQueriesDiagnosticsAndCacheEvents()
    {
        const string source = """
deriveReport :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => {
        target := meta.target_type_of(input);
        meta.warning(meta.span_of(input), meta.name_of(target));
        meta.keep()
    }
}


Subject :: type  expand deriveReport
{
    value:: Int
}
""";

        var first = Compile("meta_trace.eidos", source, options =>
        {
            options.TraceComptime = true;
            options.EnableLiveStateCache = true;
        });
        var second = Compile("meta_trace.eidos", source, options =>
        {
            options.TraceComptime = true;
            options.EnableLiveStateCache = true;
        });

        Assert.True(first.Success, FormatDiagnostics(first));
        Assert.True(second.Success, FormatDiagnostics(second));
        Assert.Contains(first.ComptimeTrace, static entry =>
            entry.Kind == "call" && entry.Operation == "deriveReport" && entry.Outcome == "success");
        Assert.Contains(first.ComptimeTrace, static entry =>
            entry.Kind == "query" && entry.Operation == "meta.name_of" && entry.Outcome == "success");
        Assert.Contains(first.ComptimeTrace, static entry =>
            entry.Kind == "diagnostic" && entry.Operation == "meta.warning" && entry.Outcome == "success");
        Assert.Contains(second.ComptimeTrace, static entry =>
            entry.Kind == "cache" && entry.Operation == "live-state:Namer" && entry.Outcome == "hit");
    }

    [Fact]
    public void ComptimeBudgets_ProduceDeterministicDiagnostics()
    {
        const string source = """
deriveBudget :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => {
        meta.warning(meta.span_of(input), "first");
        meta.warning(meta.span_of(input), "second");
        meta.keep()
    }
}


Subject :: type  expand deriveBudget
{
    value:: Int
}
""";

        var diagnosticLimited = Compile("meta_diagnostic_budget.eidos", source, options =>
            options.ComptimeDiagnosticBudget = 1);
        Assert.False(diagnosticLimited.Success);
        Assert.Contains(diagnosticLimited.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3601" &&
            diagnostic.Message.Contains("diagnostic count budget exceeded", StringComparison.Ordinal));

        var fuelLimited = Compile("meta_fuel_budget.eidos", source, options =>
            options.ComptimeFuelBudget = 1);
        Assert.False(fuelLimited.Success);
        Assert.Contains(fuelLimited.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3601" &&
            diagnostic.Message.Contains("fuel budget exceeded", StringComparison.Ordinal));
    }

    [Fact]
    public void LayoutReflection_RejectsUnsupportedExplicitTarget()
    {
        const string source = """
Layout :: comptime meta.layout_of(Int, "unknown64-target");
""";

        var result = Compile("meta_layout_unknown64_target.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Message.Contains("unsupported explicit layout target", StringComparison.Ordinal));
    }

    [Fact]
    public void UserDerive_RequiresProtocolSignatureAndRunsDistinctGeneratorsToFixedPoint()
    {
        const string invalidSource = """
invalid :: comptime Int -> Int {
    value => value
}


Subject :: type  expand invalid
{
    value:: Int
}
""";
        var invalid = Compile("meta_invalid_generator.eidos", invalidSource);
        Assert.False(invalid.Success);
        Assert.Contains(invalid.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3600" &&
            diagnostic.Message.Contains("not a compiler generator protocol", StringComparison.Ordinal));

        const string fixedPointSource = """
deriveFirst :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => meta.add_after(input, [meta.function("first", [], Int, meta.expr_int(1))])
}

deriveSecond :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => meta.add_after(input, [meta.function("second", [], Int, meta.expr_int(2))])
}


Subject :: type  expand deriveFirst expand deriveSecond
{
    value:: Int
}

read :: Unit -> Int {
    _ => first() + second()
}
""";
        var fixedPoint = Compile("meta_distinct_generators.eidos", fixedPointSource);
        Assert.True(fixedPoint.Success, FormatDiagnostics(fixedPoint));
        var symbolTable = Assert.IsType<SymbolTable>(fixedPoint.SymbolTable);
        Assert.Contains(symbolTable.Symbols.Values.OfType<FuncSymbol>(), static symbol =>
            symbol.Name == "first" && symbol.GeneratedOrigin != null);
        Assert.Contains(symbolTable.Symbols.Values.OfType<FuncSymbol>(), static symbol =>
            symbol.Name == "second" && symbol.GeneratedOrigin != null);
    }

    [Fact]
    public void LspMapper_NavigatesReferencesToGeneratedVirtualDocument()
    {
        const string source = """
deriveAnswer :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => meta.add_after(input, [meta.function("answer", [], Int, meta.expr_int(42))])
}


Subject :: type  expand deriveAnswer
{
    value:: Int
}

read :: Unit -> Int {
    _ => answer()
}
""";

        var result = Compile("meta_lsp_generated.eidos", source);
        Assert.True(result.Success, FormatDiagnostics(result));
        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var (line, character) = FindPosition(source, "answer()", useLast: true);

        var definition = LspSemanticMapper.MapDefinition(snapshot, line, character);
        Assert.NotNull(definition);
        Assert.StartsWith("eidos-generated://", definition.Uri, StringComparison.Ordinal);
        var references = LspSemanticMapper.MapReferences(snapshot, line, character);
        Assert.Contains(references, static location =>
            location.Uri.StartsWith("eidos-generated://", StringComparison.Ordinal));
    }

    [Fact]
    public void UserDerive_GeneratedImplementationLowersThroughHir()
    {
        const string source = """
Marker :: trait {
    marker :: Self -> String
}

deriveMarker :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => {
        target := meta.target_type_of(input);
        parameter := meta.parameter("value", target);
        method := meta.function("marker", [parameter], String, meta.expr_string(meta.name_of(target)));
        meta.add_after(input, [meta.implementation(meta.declaration_of(Marker), target, [method])])
    }
}


User :: type  expand deriveMarker
{
    name:: String,
    age:: Int
}

UserInfo :: comptime meta.shape_of(User);
UserKind :: comptime meta.kind_of(UserInfo);

main :: Unit -> String {
    _ => marker(User { name: "Ada", age: 36 })
}
""";

        var result = Compile("meta_generated_hir.eidos", source, options =>
            options.StopAtPhase = CompilationPhase.Hir);

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.Equal(CompilationPhase.Hir, result.CompletedPhase);
        Assert.NotNull(result.HirModule);
    }

    private static ComptimeValue GetComptimeValue(
        string name,
        SymbolTable symbolTable,
        TypeInferer inferer)
    {
        var symbol = Assert.Single(symbolTable.Symbols.Values.OfType<VarSymbol>(), symbol => symbol.Name == name);
        return Assert.Contains(symbol.Id, inferer.ComptimeValues);
    }

    private static string ReadTypeInfoKind(
        string name,
        SymbolTable symbolTable,
        TypeInferer inferer)
    {
        var shape = Assert.IsType<ComptimeAdtValue>(GetComptimeValue(name, symbolTable, inferer));
        var info = ReadShapePayload(shape);
        Assert.True(info.TryGet("kind", out var kind));
        return Assert.IsType<ComptimeStringValue>(kind).Value;
    }

    private static ComptimeMetaObjectValue ReadShapePayload(ComptimeAdtValue shape) =>
        Assert.IsType<ComptimeMetaObjectValue>(Assert.Single(shape.PositionalValues));

    private static string[] ReadTypeNames(string name, SymbolTable symbolTable, TypeInferer inferer) =>
        Assert.IsType<ComptimeSequenceValue>(GetComptimeValue(name, symbolTable, inferer))
            .Elements
            .Select(static value => Assert.IsType<ComptimeTypeValue>(value).TypeRef.Name)
            .ToArray();

    private static string[] ReadFieldNames(string name, SymbolTable symbolTable, TypeInferer inferer) =>
        Assert.IsType<ComptimeSequenceValue>(GetComptimeValue(name, symbolTable, inferer))
            .Elements
            .Select(static value =>
            {
                var field = Assert.IsType<ComptimeMetaObjectValue>(value);
                Assert.True(field.TryGet("name", out var fieldName));
                return Assert.IsType<ComptimeStringValue>(fieldName).Value;
            })
            .ToArray();

    private static CompilationResult Compile(
        string fileName,
        string source,
        Action<CompilationOptions>? configure = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "eidosc_meta_reflection_tests");
        Directory.CreateDirectory(tempDir);
        var inputFile = Path.Combine(tempDir, fileName);
        File.WriteAllText(inputFile, source);
        var options = new CompilationOptions
        {
            InputFile = inputFile,
            StopAtPhase = CompilationPhase.Types,
            UseColors = false,
            AllowLegacyMetaSurface = true
        };
        configure?.Invoke(options);
        return new CompilationPipeline(source, options).Run();
    }

    private static CompilationResult CompileWorkspace(
        string entryRelativePath,
        params (string RelativePath, string Source)[] files)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_meta_workspace_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            foreach (var (relativePath, source) in files)
            {
                var path = Path.Combine(tempDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, source);
            }

            var entry = Path.Combine(tempDir, entryRelativePath.Replace('/', Path.DirectorySeparatorChar));
            return new CompilationPipeline(File.ReadAllText(entry), new CompilationOptions
            {
                InputFile = entry,
                StopAtPhase = CompilationPhase.Types,
                ImportSearchRoots = [tempDir],
                UseColors = false,
                AllowLegacyMetaSurface = true
            }).Run();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string FormatDiagnostics(CompilationResult result) => string.Join(
        "; ",
        result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));

    private static (int Line, int Character) FindPosition(string source, string needle, bool useLast)
    {
        var index = useLast
            ? source.LastIndexOf(needle, StringComparison.Ordinal)
            : source.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0);

        var line = 0;
        var lineStart = 0;
        for (var current = 0; current < index; current++)
        {
            if (source[current] != '\n')
            {
                continue;
            }

            line++;
            lineStart = current + 1;
        }

        return (line, index - lineStart + 1);
    }
}
