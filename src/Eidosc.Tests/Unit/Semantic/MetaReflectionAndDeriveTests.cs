using Eidosc.Ide;
using Eidosc.Cli.Lsp;
using Eidosc.Pipeline;
using Eidosc.Symbols;
using Eidosc.Types;
using System.Text.Json;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class MetaReflectionAndDeriveTests
{
    [Fact]
    public void UserDerive_GeneratesTraitImplementationWithStableOrigin()
    {
        const string source = """
Marker :: trait {
    marker :: Self -> String
}

deriveMarker :: comptime Meta::DeriveInput -> Meta::Expansion {
    input => {
        target := Meta::target(input);
        parameter := Meta::parameter("value", target);
        method := Meta::function(
            "marker",
            [parameter],
            String,
            Meta::exprString(Meta::typeName(target))
        );
        Meta::expansion([
            Meta::implementation(
                Meta::decl(Marker),
                target,
                [method]
            )
        ])
    }
}

@derive(deriveMarker)
User :: type {
    name: String,
    age: Int
}

readMarker :: User -> String {
    value => marker(value)
}
""";

        var first = Compile("meta_user_derive.eidos", source);
        var second = Compile("meta_user_derive.eidos", source);

        Assert.True(first.Success, FormatDiagnostics(first));
        Assert.True(second.Success, FormatDiagnostics(second));

        var firstSymbolTable = Assert.IsType<SymbolTable>(first.SymbolTable);
        var secondSymbolTable = Assert.IsType<SymbolTable>(second.SymbolTable);
        var generated = Assert.Single(firstSymbolTable.Symbols.Values.OfType<FuncSymbol>(), static symbol =>
            symbol.Name == "marker" && symbol.IsTraitImplementation && symbol.GeneratedOrigin != null);
        var regenerated = Assert.Single(secondSymbolTable.Symbols.Values.OfType<FuncSymbol>(), static symbol =>
            symbol.Name == "marker" && symbol.IsTraitImplementation && symbol.GeneratedOrigin != null);

        Assert.Equal(generated.GeneratedOrigin!.StableIdentity, regenerated.GeneratedOrigin!.StableIdentity);
        Assert.StartsWith("eidos-generated://", generated.GeneratedOrigin.VirtualDocumentPath, StringComparison.Ordinal);

        var user = Assert.Single(firstSymbolTable.Symbols.Values.OfType<AdtSymbol>(), static symbol => symbol.Name == "User");
        var marker = Assert.Single(firstSymbolTable.Symbols.Values.OfType<TraitSymbol>(), static symbol => symbol.Name == "Marker");
        Assert.NotNull(firstSymbolTable.LookupImplForTrait(user.TypeId, marker.Id));

        var snapshot = IdeSemanticSnapshotBuilder.Build(first);
        var generatedEntry = Assert.Single(snapshot.Symbols, symbol => symbol.SymbolId == generated.Id.Value);
        Assert.True(generatedEntry.IsGenerated);
        Assert.Equal(generated.GeneratedOrigin.VirtualDocumentPath, generatedEntry.GeneratedOrigin?.VirtualDocumentPath);
        Assert.Contains(snapshot.Completions, completion =>
            completion.SymbolId == generated.Id.Value && completion.IsGenerated);
        Assert.Contains(snapshot.Occurrences, occurrence =>
            occurrence.SymbolId == generated.Id.Value &&
            occurrence.Role == "definition" &&
            occurrence.Span.FilePath == generated.GeneratedOrigin.VirtualDocumentPath);
        var generatedDocument = Assert.Single(snapshot.GeneratedDocuments, document =>
            document.Uri == generated.GeneratedOrigin.VirtualDocumentPath);
        Assert.StartsWith("marker :: ", generatedDocument.Content, StringComparison.Ordinal);
        Assert.Contains(generated.GeneratedOrigin.StableIdentity, generatedDocument.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void TypeInfo_PreservesConstructorAndFieldSourceOrder()
    {
        const string source = """
User :: type {
    User { name: String, age: Int }
}

Info :: comptime Meta::typeInfo(User);
HasName :: comptime Meta::hasField(User, "name");
NameType :: comptime Meta::fieldType(User, "name");
""";

        var result = Compile("meta_type_info.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(result.TypeInferer);
        var info = Assert.IsType<ComptimeMetaObjectValue>(GetComptimeValue("Info", symbolTable, inferer));
        Assert.Equal("type-info", info.SchemaKind);
        Assert.True(info.TryGet("kind", out var kind));
        Assert.Equal("adt", Assert.IsType<ComptimeStringValue>(kind).Value);

        Assert.True(info.TryGet("constructors", out var constructorsValue));
        var constructors = Assert.IsType<ComptimeSequenceValue>(constructorsValue);
        var constructor = Assert.IsType<ComptimeMetaObjectValue>(Assert.Single(constructors.Elements));
        Assert.True(constructor.TryGet("fields", out var fieldsValue));
        var fields = Assert.IsType<ComptimeSequenceValue>(fieldsValue);
        Assert.Equal(
            ["name", "age"],
            fields.Elements.Select(field =>
            {
                var fieldInfo = Assert.IsType<ComptimeMetaObjectValue>(field);
                Assert.True(fieldInfo.TryGet("name", out var fieldName));
                return Assert.IsType<ComptimeStringValue>(fieldName).Value;
            }).ToArray());

        Assert.True(Assert.IsType<ComptimeBoolValue>(GetComptimeValue("HasName", symbolTable, inferer)).Value);
        var nameType = Assert.IsType<ComptimeTypeValue>(GetComptimeValue("NameType", symbolTable, inferer));
        Assert.Equal("String", nameType.TypeRef.Name);
    }

    [Fact]
    public void TypeInfo_ExposesTraitFunctionAndReferenceCategories()
    {
        const string source = """
Marker :: trait {
    marker :: Self -> Int
}

Holder :: type {
    callback: Int -> String,
    reference: Ref[Int]
}

CallbackType :: comptime Meta::fieldType(Holder, "callback");
ReferenceType :: comptime Meta::fieldType(Holder, "reference");
FunctionInfo :: comptime Meta::typeInfo(CallbackType);
ReferenceInfo :: comptime Meta::typeInfo(ReferenceType);
TraitInfo :: comptime Meta::typeInfo(Marker);
""";

        var result = Compile("meta_type_categories.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(result.TypeInferer);
        Assert.Equal("function", ReadTypeInfoKind("FunctionInfo", symbolTable, inferer));
        Assert.Equal("reference", ReadTypeInfoKind("ReferenceInfo", symbolTable, inferer));
        Assert.Equal("trait", ReadTypeInfoKind("TraitInfo", symbolTable, inferer));

        var functionInfo = Assert.IsType<ComptimeMetaObjectValue>(GetComptimeValue("FunctionInfo", symbolTable, inferer));
        Assert.True(functionInfo.TryGet("functionParameters", out var parameters));
        Assert.Single(Assert.IsType<ComptimeSequenceValue>(parameters).Elements);
        Assert.True(functionInfo.TryGet("functionResult", out var resultType));
        Assert.Equal("String", Assert.IsType<ComptimeTypeValue>(resultType).TypeRef.Name);

        var referenceInfo = Assert.IsType<ComptimeMetaObjectValue>(GetComptimeValue("ReferenceInfo", symbolTable, inferer));
        Assert.True(referenceInfo.TryGet("referenceMutable", out var mutable));
        Assert.False(Assert.IsType<ComptimeBoolValue>(mutable).Value);
        Assert.True(referenceInfo.TryGet("referenceReferent", out var referent));
        Assert.Equal("Int", Assert.IsType<ComptimeTypeValue>(referent).TypeRef.Name);
    }

    [Fact]
    public void UserDerive_SelfAttachmentCycle_IsDiagnosed()
    {
        const string source = """
deriveLoop :: comptime Meta::DeriveInput -> Meta::Expansion {
    input => Meta::expansion([
        Meta::attribute(Meta::targetDecl(input), "derive", ["deriveLoop"])
    ])
}

@derive(deriveLoop)
Looped :: type {
    value: Int
}
""";

        var result = Compile("meta_derive_cycle.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3604" &&
            diagnostic.Message.Contains("derive expansion cycle", StringComparison.Ordinal));
    }

    [Fact]
    public void UserDerive_PreservesTypeAndValueGenericTargetIdentity()
    {
        const string source = """
Marker :: trait {
    marker :: Self -> String
}

deriveMarker :: comptime Meta::DeriveInput -> Meta::Expansion {
    input => {
        target := Meta::target(input);
        parameter := Meta::parameter("value", target);
        method := Meta::function("marker", [parameter], String, Meta::exprString(Meta::typeName(target)));
        Meta::expansion([Meta::implementation(Meta::decl(Marker), target, [method])])
    }
}

@derive(deriveMarker)
Box[comptime N: Int, comptime T: Type] :: type {
    value: T
}
""";

        var result = Compile("meta_generic_derive.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var generated = Assert.Single(symbolTable.Symbols.Values.OfType<FuncSymbol>(), static symbol =>
            symbol.Name == "marker" && symbol.IsTraitImplementation && symbol.GeneratedOrigin != null);
        Assert.Equal(2, generated.TypeParams.Count);
        Assert.Equal(
            [GenericParameterKind.Value, GenericParameterKind.Type],
            generated.TypeParams
                .Select(symbolTable.GetSymbol<TypeParamSymbol>)
                .Select(static parameter => Assert.IsType<TypeParamSymbol>(parameter).ParameterKind)
                .ToArray());
    }

    [Fact]
    public void ComptimeAdtFieldRead_EvaluatesNamedField()
    {
        const string source = """
Point :: type {
    Point { x: Int, y: Int }
}

Origin :: comptime Point { x: 3, y: 4 };
readX :: comptime Point -> Int {
    point => point.x
}
X :: comptime readX(Origin);
""";

        var result = Compile("comptime_field_read.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(result.TypeInferer);
        Assert.Equal(3, Assert.IsType<ComptimeIntegerValue>(GetComptimeValue("X", symbolTable, inferer)).Value);
    }

    [Fact]
    public void UserDerive_ResolvesImportedGenerator()
    {
        var result = CompileWorkspace(
            "main.eidos",
            ("Tools/Generators.eidos", """
Tools.Generators :: module {
    export deriveAnswer :: comptime Meta::DeriveInput -> Meta::Expansion {
        _ => Meta::expansion([
            Meta::function("answer", [], Int, Meta::exprInt(42))
        ])
    }
}
"""),
            ("main.eidos", """
import Tools.Generators::{deriveAnswer}

@derive(deriveAnswer)
Subject :: type {
    value: Int
}

read :: Unit -> Int {
    _ => answer()
}
"""));

        Assert.True(result.Success, FormatDiagnostics(result));
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        Assert.Contains(symbolTable.Symbols.Values.OfType<FuncSymbol>(), static symbol =>
            symbol.Name == "answer" && symbol.GeneratedOrigin != null);
    }

    [Fact]
    public void MetaValuesAndGeneratedOrigin_RoundTripThroughCachePayloads()
    {
        const string source = """
deriveAnswer :: comptime Meta::DeriveInput -> Meta::Expansion {
    _ => Meta::expansion([Meta::function("answer", [], Int, Meta::exprInt(42))])
}

@derive(deriveAnswer)
Subject :: type {
    value: Int
}

Info :: comptime Meta::typeInfo(Subject);
""";

        var result = Compile("meta_cache_roundtrip.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(result.TypeInferer);
        var info = GetComptimeValue("Info", symbolTable, inferer);
        Assert.True(ComptimeValuePayload.TryCreate(info, out var valuePayload));
        Assert.True(valuePayload.TryRestoreValue(remapper: null, out var restoredInfo));
        Assert.True(info.StructuralEquals(restoredInfo));

        var originalGenerated = Assert.Single(
            symbolTable.Symbols.Values.OfType<FuncSymbol>(),
            static symbol => symbol.Name == "answer" && symbol.GeneratedOrigin != null);
        var symbolPayload = SymbolPayload.Create(originalGenerated);
        var restoredPayload = JsonSerializer.Deserialize<SymbolPayload>(JsonSerializer.Serialize(symbolPayload));
        Assert.NotNull(restoredPayload?.GeneratedOrigin);
        Assert.Equal(
            originalGenerated.GeneratedOrigin!.StableIdentity,
            restoredPayload!.GeneratedOrigin!.StableIdentity);
    }

    [Fact]
    public void StructuredExpansion_MaterializesAllSupportedDeclarationCategories()
    {
        const string source = """
deriveArtifacts :: comptime Meta::DeriveInput -> Meta::Expansion {
    input => Meta::expansion([
        Meta::comptimeValue("Answer", Int, Meta::exprInt(42)),
        Meta::test("generated_test", Meta::exprUnit()),
        Meta::moduleMember(Meta::function("generated_value", [], Int, Meta::exprInt(7))),
        Meta::attribute(Meta::targetDecl(input), "doc", ["generated"]),
        Meta::diagnostic("warning", Meta::deriveSpan(input), "generated warning")
    ])
}

@derive(deriveArtifacts)
Subject :: type {
    value: Int
}

read :: Unit -> Int {
    _ => generated_value()
}
""";

        var result = Compile("meta_structured_expansion.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "W3611" && diagnostic.Message == "generated warning");
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(result.TypeInferer);
        Assert.Equal(42, Assert.IsType<ComptimeIntegerValue>(GetComptimeValue("Answer", symbolTable, inferer)).Value);
        Assert.Contains(symbolTable.Symbols.Values.OfType<FuncSymbol>(), static symbol =>
            symbol.Name == "generated_test" && symbol.GeneratedOrigin != null);
        Assert.Contains(symbolTable.Symbols.Values.OfType<FuncSymbol>(), static symbol =>
            symbol.Name == "generated_value" && symbol.GeneratedOrigin != null);
        var subject = Assert.Single(result.Ast!.Declarations.OfType<Eidosc.Ast.Declarations.AdtDef>(), static adt => adt.Name == "Subject");
        Assert.Contains(subject.Attributes, static attribute => attribute.Name == "doc");
    }

    [Fact]
    public void LayoutReflection_RequiresExplicitTargetAndCompletedLayoutFact()
    {
        const string primitiveSource = """
IntLayout :: comptime Meta::layoutOf(Int, "x86_64-pc-windows-msvc");
""";
        var primitiveResult = Compile("meta_layout_primitive.eidos", primitiveSource);
        Assert.True(primitiveResult.Success, FormatDiagnostics(primitiveResult));
        var symbolTable = Assert.IsType<SymbolTable>(primitiveResult.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(primitiveResult.TypeInferer);
        var layout = Assert.IsType<ComptimeMetaObjectValue>(GetComptimeValue("IntLayout", symbolTable, inferer));
        Assert.True(layout.TryGet("size", out var size));
        Assert.Equal(8, Assert.IsType<ComptimeIntegerValue>(size).Value);

        const string incompleteSource = """
Payload :: type {
    value: Int
}
PayloadLayout :: comptime Meta::layoutOf(Payload, "x86_64-pc-windows-msvc");
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
deriveReport :: comptime Meta::DeriveInput -> Meta::Expansion {
    input => {
        target := Meta::target(input);
        Meta::warning(Meta::deriveSpan(input), Meta::typeName(target));
        Meta::expansion([])
    }
}

@derive(deriveReport)
Subject :: type {
    value: Int
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
            entry.Kind == "query" && entry.Operation == "Meta::typeName" && entry.Outcome == "success");
        Assert.Contains(first.ComptimeTrace, static entry =>
            entry.Kind == "diagnostic" && entry.Operation == "Meta::warning" && entry.Outcome == "success");
        Assert.Contains(second.ComptimeTrace, static entry =>
            entry.Kind == "cache" && entry.Operation == "live-state:Namer" && entry.Outcome == "hit");
    }

    [Fact]
    public void ComptimeBudgets_ProduceDeterministicDiagnostics()
    {
        const string source = """
deriveBudget :: comptime Meta::DeriveInput -> Meta::Expansion {
    input => {
        Meta::warning(Meta::deriveSpan(input), "first");
        Meta::warning(Meta::deriveSpan(input), "second");
        Meta::expansion([])
    }
}

@derive(deriveBudget)
Subject :: type {
    value: Int
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
Layout :: comptime Meta::layoutOf(Int, "unknown-target");
""";

        var result = Compile("meta_layout_unknown_target.eidos", source);

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

@derive(invalid)
Subject :: type {
    value: Int
}
""";
        var invalid = Compile("meta_invalid_generator.eidos", invalidSource);
        Assert.False(invalid.Success);
        Assert.Contains(invalid.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3600" &&
            diagnostic.Message.Contains("Meta::DeriveInput -> Meta::Expansion", StringComparison.Ordinal));

        const string fixedPointSource = """
deriveFirst :: comptime Meta::DeriveInput -> Meta::Expansion {
    _ => Meta::expansion([Meta::function("first", [], Int, Meta::exprInt(1))])
}

deriveSecond :: comptime Meta::DeriveInput -> Meta::Expansion {
    _ => Meta::expansion([Meta::function("second", [], Int, Meta::exprInt(2))])
}

@derive(deriveFirst, deriveSecond)
Subject :: type {
    value: Int
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
deriveAnswer :: comptime Meta::DeriveInput -> Meta::Expansion {
    _ => Meta::expansion([Meta::function("answer", [], Int, Meta::exprInt(42))])
}

@derive(deriveAnswer)
Subject :: type {
    value: Int
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

deriveMarker :: comptime Meta::DeriveInput -> Meta::Expansion {
    input => {
        target := Meta::target(input);
        parameter := Meta::parameter("value", target);
        method := Meta::function("marker", [parameter], String, Meta::exprString(Meta::typeName(target)));
        Meta::expansion([Meta::implementation(Meta::decl(Marker), target, [method])])
    }
}

@derive(deriveMarker)
User :: type {
    name: String,
    age: Int
}

UserInfo :: comptime Meta::typeInfo(User);
UserKind :: comptime Meta::typeKind(UserInfo);

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
        var info = Assert.IsType<ComptimeMetaObjectValue>(GetComptimeValue(name, symbolTable, inferer));
        Assert.True(info.TryGet("kind", out var kind));
        return Assert.IsType<ComptimeStringValue>(kind).Value;
    }

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
            UseColors = false
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
                UseColors = false
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
