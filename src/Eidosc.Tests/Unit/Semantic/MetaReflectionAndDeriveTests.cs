using Eidosc.Ast.Declarations;
using Eidosc.Ast.Patterns;
using Eidosc.Ide;
using Eidosc.Cli.Lsp;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Eidosc.Symbols;
using Eidosc.Types;
using System.Text.Json;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public sealed partial class MetaReflectionAndDeriveTests
{
    [Fact]
    public void ClauseReflection_ExposesTypedOccurrencesArgumentsAndStage()
    {
        const string source = """
Subject :: type derive Eq, Show {
    value:: Int
}

work :: Int -> Int transparent {
    value => value
}

SubjectInfo :: comptime meta.shape_of(Subject);
SubjectClauses :: comptime meta.clauses_of(SubjectInfo);
FirstKeyword :: comptime meta.clause_keyword_of(SubjectClauses[0]);
FirstKind :: comptime meta.clause_kind_of(SubjectClauses[0]);
FirstStage :: comptime meta.clause_stage_of(SubjectClauses[0]);
FirstOrder :: comptime meta.clause_source_order_of(SubjectClauses[0]);
FirstArguments :: comptime meta.clause_arguments_of(SubjectClauses[0]);
FirstArgumentText :: comptime meta.clause_argument_text_of(FirstArguments[0]);
SecondArgumentOccurrence :: comptime meta.clause_argument_occurrence_of(FirstArguments[1]);
WorkInfo :: comptime meta.shape_of(meta.declaration_of(work));
WorkClauses :: comptime meta.clauses_of(WorkInfo);
WorkClauseKeyword :: comptime meta.clause_keyword_of(WorkClauses[0]);
""";

        var result = Compile("meta_typed_clause_reflection.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(result.TypeInferer);
        var clauses = Assert.IsType<ComptimeSequenceValue>(GetComptimeValue("SubjectClauses", symbolTable, inferer));
        var clause = Assert.IsType<ComptimeMetaObjectValue>(Assert.Single(clauses.Elements));
        Assert.Equal("clause", clause.SchemaKind);
        Assert.Equal(WellKnownTypeIds.MetaClauseId, Assert.IsType<TyCon>(clause.StaticType).Id.Value);
        Assert.True(clause.TryGet("arguments", out var reflectedArguments));
        var arguments = Assert.IsType<ComptimeSequenceValue>(reflectedArguments);
        Assert.Equal(2, arguments.Elements.Count);
        Assert.All(arguments.Elements, argument =>
        {
            var typedArgument = Assert.IsType<ComptimeMetaObjectValue>(argument);
            Assert.Equal("clause-argument", typedArgument.SchemaKind);
            Assert.Equal(WellKnownTypeIds.MetaClauseArgumentId, Assert.IsType<TyCon>(typedArgument.StaticType).Id.Value);
        });
        Assert.Equal("derive", Assert.IsType<ComptimeStringValue>(GetComptimeValue("FirstKeyword", symbolTable, inferer)).Value);
        Assert.Equal("derive", Assert.IsType<ComptimeStringValue>(GetComptimeValue("FirstKind", symbolTable, inferer)).Value);
        Assert.Equal("transparent", Assert.IsType<ComptimeStringValue>(GetComptimeValue("WorkClauseKeyword", symbolTable, inferer)).Value);
        Assert.Equal("Eq", Assert.IsType<ComptimeStringValue>(GetComptimeValue("FirstArgumentText", symbolTable, inferer)).Value);
        Assert.Equal(0, Assert.IsType<ComptimeIntegerValue>(GetComptimeValue("FirstOrder", symbolTable, inferer)).Value);
        Assert.EndsWith(":arg:1", Assert.IsType<ComptimeStringValue>(GetComptimeValue("SecondArgumentOccurrence", symbolTable, inferer)).Value, StringComparison.Ordinal);
        var stage = Assert.IsType<ComptimeAdtValue>(GetComptimeValue("FirstStage", symbolTable, inferer));
        Assert.Equal("Semantic", stage.ConstructorName);
        Assert.Equal(WellKnownTypeIds.MetaStageId, Assert.IsType<TyCon>(stage.StaticType).Id.Value);
    }

    [Fact]
    public void ClauseReflection_LiveStateRestorePreservesTypedClauseAndInvocationIdentity()
    {
        const string source = """
Subject :: type derive Eq, Show {
    value:: Int
}

SubjectInfo :: comptime meta.shape_of(Subject);
SubjectClauses :: comptime meta.clauses_of(SubjectInfo);
""";

        var first = Compile("meta_typed_clause_restore.eidos", source, options =>
        {
            options.EnableLiveStateCache = true;
            options.TraceComptime = true;
        });
        var second = Compile("meta_typed_clause_restore.eidos", source, options =>
        {
            options.EnableLiveStateCache = true;
            options.TraceComptime = true;
        });

        Assert.True(first.Success, FormatDiagnostics(first));
        Assert.True(second.Success, FormatDiagnostics(second));
        var firstType = Assert.Single(Assert.IsType<ModuleDecl>(first.Ast).Declarations.OfType<AdtDef>());
        var secondType = Assert.Single(Assert.IsType<ModuleDecl>(second.Ast).Declarations.OfType<AdtDef>());
        var namerPayload = AstNamerStatePayload.Create(first.Ast);
        var serializedPayload = JsonSerializer.Serialize(namerPayload);
        var restoredPayload = Assert.IsType<AstNamerStatePayload>(
            JsonSerializer.Deserialize<AstNamerStatePayload>(serializedPayload));
        Assert.True(restoredPayload.HasValidHash());
        var clausePayload = Assert.Single(
            restoredPayload.Entries,
            entry => entry.SymbolId == firstType.SymbolId.Value && entry.DeclarationClauses != null)
            .DeclarationClauses!;
        Assert.Equal(ClauseSchema.Version, Assert.Single(clausePayload.Clauses).SchemaVersion);
        Assert.Equal([0, 1], clausePayload.MetaInvocations.Select(static invocation => invocation.ArgumentSubIndex));
        Assert.All(clausePayload.MetaInvocations, static invocation => Assert.True(invocation.HasCompilerGrant));
        Assert.Equal(
            firstType.BoundClauses.Select(static clause => (
                clause.SchemaVersion,
                Occurrence: clause.OccurrenceId.ToString(),
                clause.Kind,
                clause.Keyword,
                clause.Stage,
                clause.SourceOrderBehavior,
                clause.SourceOrder,
                Arguments: string.Join("|", clause.Arguments.Select(static argument =>
                    $"{argument.Index}:{argument.Type}:{argument.CanonicalText}:{string.Join('.', argument.Path)}")),
                clause.HasCompilerOwnedSourceGrant)),
            secondType.BoundClauses.Select(static clause => (
                clause.SchemaVersion,
                Occurrence: clause.OccurrenceId.ToString(),
                clause.Kind,
                clause.Keyword,
                clause.Stage,
                clause.SourceOrderBehavior,
                clause.SourceOrder,
                Arguments: string.Join("|", clause.Arguments.Select(static argument =>
                    $"{argument.Index}:{argument.Type}:{argument.CanonicalText}:{string.Join('.', argument.Path)}")),
                clause.HasCompilerOwnedSourceGrant)));
        Assert.Equal(
            firstType.MetaInvocations.Select(static invocation => (
                invocation.SchemaVersion,
                Occurrence: invocation.OccurrenceId.ToString(),
                invocation.Owner,
                invocation.Stage,
                invocation.SourceOrder,
                Generator: string.Join(".", invocation.GeneratorPath),
                HasCompilerGrant: invocation.CompilerGrant != null)),
            secondType.MetaInvocations.Select(static invocation => (
                invocation.SchemaVersion,
                Occurrence: invocation.OccurrenceId.ToString(),
                invocation.Owner,
                invocation.Stage,
                invocation.SourceOrder,
                Generator: string.Join(".", invocation.GeneratorPath),
                HasCompilerGrant: invocation.CompilerGrant != null)));

        var firstTable = Assert.IsType<SymbolTable>(first.SymbolTable);
        var secondTable = Assert.IsType<SymbolTable>(second.SymbolTable);
        var firstClauses = GetComptimeValue("SubjectClauses", firstTable, Assert.IsType<TypeInferer>(first.TypeInferer));
        var secondClauses = GetComptimeValue("SubjectClauses", secondTable, Assert.IsType<TypeInferer>(second.TypeInferer));
        Assert.Equal(firstClauses.CanonicalText, secondClauses.CanonicalText);
        Assert.Contains(second.ComptimeTrace, static entry =>
            entry.Kind == "cache" && entry.Operation == "live-state:Namer" && entry.Outcome == "hit");
    }

    [Fact]
    public void FunctionExpand_UsesTheUnifiedDeclarationTargetScheduler()
    {
        const string source = """
report :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => meta.report([
        meta.diagnostic("warning", meta.span_of(input), "function target expanded")
    ])
}

work :: Int -> Int expand report {
    value => value
}
""";

        var result = Compile("meta_function_target.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "W3611" && diagnostic.Message == "function target expanded");
    }

    [Fact]
    public void GeneratorOrderingClauses_FormADeterministicDagBeforeSourceOrder()
    {
        const string source = """
first :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation
    after second
{
    input => meta.report([
        meta.diagnostic("warning", meta.span_of(input), "first")
    ])
}

second :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => meta.report([
        meta.diagnostic("warning", meta.span_of(input), "second")
    ])
}

Subject :: type expand first expand second {}
""";

        var result = Compile("meta_ordering_dag.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var warnings = result.Diagnostics
            .Where(diagnostic => diagnostic.Code == "W3611")
            .Select(static diagnostic => diagnostic.Message)
            .ToArray();
        Assert.Equal(["second", "first"], warnings);
    }

    [Fact]
    public void GeneratorRequires_OrdersTheMatchingOccurrenceBeforeTheRequester()
    {
        const string source = """
finalize :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation
    requires normalize
{
    input => meta.report([
        meta.diagnostic("warning", meta.span_of(input), "finalize")
    ])
}

normalize :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => meta.report([
        meta.diagnostic("warning", meta.span_of(input), "normalize")
    ])
}

Subject :: type expand finalize expand normalize {}
""";

        var result = Compile("meta_ordering_requires.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var warnings = result.Diagnostics
            .Where(diagnostic => diagnostic.Code == "W3611")
            .Select(static diagnostic => diagnostic.Message)
            .ToArray();
        Assert.Equal(["normalize", "finalize"], warnings);
    }

    [Fact]
    public void GeneratorRequires_RejectsAVisibleDeclarationWithoutAMatchingOccurrence()
    {
        const string source = """
finalize :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation
    requires normalize
{
    input => meta.report([
        meta.diagnostic("warning", meta.span_of(input), "must not execute")
    ])
}

normalize :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    _ => meta.keep()
}

Subject :: type expand finalize {}
""";

        var result = Compile("meta_ordering_missing_requirement.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "E3613" &&
            diagnostic.Message.Contains("requires expansion 'normalize'", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Message == "must not execute");
    }

    [Fact]
    public void GeneratorOrdering_ResolvesQualifiedGeneratorPathsBySymbolIdentity()
    {
        const string source = """
pipeline :: module {
    normalize :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
        input => meta.report([
            meta.diagnostic("warning", meta.span_of(input), "normalize")
        ])
    }
}

finalize :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation
    after pipeline.normalize
{
    input => meta.report([
        meta.diagnostic("warning", meta.span_of(input), "finalize")
    ])
}

Subject :: type expand finalize expand pipeline.normalize {}
""";

        var result = Compile("meta_ordering_qualified.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var warnings = result.Diagnostics
            .Where(diagnostic => diagnostic.Code == "W3611")
            .Select(static diagnostic => diagnostic.Message)
            .ToArray();
        Assert.Equal(["normalize", "finalize"], warnings);
    }

    [Fact]
    public void GeneratorOrdering_DoesNotConflateEqualFinalSegmentsFromDifferentModules()
    {
        const string source = """
left :: module {
    run :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
        input => meta.report([
            meta.diagnostic("warning", meta.span_of(input), "left")
        ])
    }
}

right :: module {
    run :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
        input => meta.report([
            meta.diagnostic("warning", meta.span_of(input), "right")
        ])
    }
}

finalize :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation
    after left.run
{
    input => meta.report([
        meta.diagnostic("warning", meta.span_of(input), "finalize")
    ])
}

Subject :: type expand finalize expand left.run expand right.run {}
""";

        var result = Compile("meta_ordering_symbol_identity.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var warnings = result.Diagnostics
            .Where(diagnostic => diagnostic.Code == "W3611")
            .Select(static diagnostic => diagnostic.Message)
            .ToArray();
        Assert.Equal(["left", "finalize", "right"], warnings);
    }

    [Fact]
    public void GeneratorOrdering_UsesTheTargetStageFromTheResolvedGeneratorSignature()
    {
        const string source = """
body_pass :: comptime meta.Target[meta.Stage.Body] -> meta.Transformation {
    input => meta.report([
        meta.diagnostic("warning", meta.span_of(input), "body")
    ])
}

semantic_pass :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation
    after body_pass
{
    input => meta.report([
        meta.diagnostic("warning", meta.span_of(input), "semantic")
    ])
}

Subject :: type expand body_pass expand semantic_pass {}
""";

        var result = Compile("meta_ordering_stage.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var warnings = result.Diagnostics
            .Where(diagnostic => diagnostic.Code == "W3611")
            .Select(static diagnostic => diagnostic.Message)
            .ToArray();
        Assert.Equal(["semantic", "body"], warnings);
        var subject = Assert.IsType<ModuleDecl>(result.Ast).Declarations
            .OfType<AdtDef>()
            .Single(static declaration => declaration.Name == "Subject");
        Assert.Equal(
            [ClauseStage.Body, ClauseStage.Semantic],
            subject.BoundClauses.Select(static clause => clause.Stage));
        Assert.Equal(
            [ClauseStage.Body, ClauseStage.Semantic],
            subject.MetaInvocations.Select(static invocation => invocation.Stage));
    }

    [Fact]
    public void GeneratorOrderingCycle_IsRejectedDeterministically()
    {
        const string source = """
first :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation
    after second
{
    _ => meta.keep()
}

second :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation
    after first
{
    _ => meta.keep()
}

Subject :: type expand first expand second {}
""";

        var result = Compile("meta_ordering_cycle.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "E3610" &&
            diagnostic.Message.Contains("ordering cycle", StringComparison.Ordinal));
    }

    [Fact]
    public void UserDerive_GeneratesTraitImplementationWithStableOrigin()
    {
        const string source = """
Marker :: trait {
    marker :: Self -> String
}

deriveMarker :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => {
        target := meta.target_type_of(input);
        parameter := meta.parameter("value", target);
        method := meta.function(
            "marker",
            [parameter],
            String,
            meta.expr_string(meta.name_of(target))
        );
        meta.add_after(input, [
            meta.implementation(
                meta.declaration_of(Marker),
                target,
                [method]
            )
        ])
    }
}


User :: type  expand deriveMarker
{
    name:: String,
    age:: Int
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
User :: type { name:: String, age:: Int }

find_field_type :: comptime Option[meta.Field] -> Type {
    Some(field) => meta.type_of(field),
    None => Unit
}

Info :: comptime meta.shape_of(User);
HasName :: comptime meta.has_field(User, "name");
NameType :: comptime find_field_type(meta.find_field(User, "name"));
""";

        var result = Compile("meta_type_info.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(result.TypeInferer);
        var shape = Assert.IsType<ComptimeAdtValue>(GetComptimeValue("Info", symbolTable, inferer));
        Assert.Equal("Nominal", shape.ConstructorName);
        var info = ReadShapePayload(shape);
        Assert.Equal("nominal-shape", info.SchemaKind);
        Assert.True(info.TryGet("kind", out var kind));
        Assert.Equal("nominal", Assert.IsType<ComptimeStringValue>(kind).Value);

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
    callback:: Int -> String,
    reference:: Ref[Int]
}

find_field_type :: comptime Option[meta.Field] -> Type {
    Some(field) => meta.type_of(field),
    None => Unit
}

CallbackType :: comptime find_field_type(meta.find_field(Holder, "callback"));
ReferenceType :: comptime find_field_type(meta.find_field(Holder, "reference"));
FunctionInfo :: comptime meta.shape_of(CallbackType);
ReferenceInfo :: comptime meta.shape_of(ReferenceType);
TraitInfo :: comptime meta.shape_of(Marker);
""";

        var result = Compile("meta_type_categories.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(result.TypeInferer);
        Assert.Equal("function", ReadTypeInfoKind("FunctionInfo", symbolTable, inferer));
        Assert.Equal("reference", ReadTypeInfoKind("ReferenceInfo", symbolTable, inferer));
        Assert.Equal("trait", ReadTypeInfoKind("TraitInfo", symbolTable, inferer));

        var functionShape = Assert.IsType<ComptimeAdtValue>(GetComptimeValue("FunctionInfo", symbolTable, inferer));
        Assert.Equal("Function", functionShape.ConstructorName);
        var functionInfo = ReadShapePayload(functionShape);
        Assert.True(functionInfo.TryGet("functionParameters", out var parameters));
        Assert.Single(Assert.IsType<ComptimeSequenceValue>(parameters).Elements);
        Assert.True(functionInfo.TryGet("functionResult", out var resultType));
        Assert.Equal("String", Assert.IsType<ComptimeTypeValue>(resultType).TypeRef.Name);

        var referenceShape = Assert.IsType<ComptimeAdtValue>(GetComptimeValue("ReferenceInfo", symbolTable, inferer));
        Assert.Equal("Reference", referenceShape.ConstructorName);
        var referenceInfo = ReadShapePayload(referenceShape);
        Assert.True(referenceInfo.TryGet("referenceMutable", out var mutable));
        Assert.False(Assert.IsType<ComptimeBoolValue>(mutable).Value);
        Assert.True(referenceInfo.TryGet("referenceReferent", out var referent));
        Assert.Equal("Int", Assert.IsType<ComptimeTypeValue>(referent).TypeRef.Name);
    }

    [Fact]
    public void AttributeBuilder_IsNotPartOfThe07MetaSurface()
    {
        const string source = """
deriveLoop :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => meta.add_after(input, [
        meta.attribute(meta.target_declaration_of(input), "derive", ["deriveLoop"])
    ])
}


Looped :: type  expand deriveLoop
{
    value:: Int
}
""";

        var result = Compile("meta_derive_cycle.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3000" &&
            diagnostic.Message.Contains("Cannot resolve path 'meta.attribute'", StringComparison.Ordinal));
    }

    [Fact]
    public void UserDerive_PreservesTypeAndValueGenericTargetIdentity()
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


Box[comptime N: Int, comptime T: Type] :: type  expand deriveMarker
{
    value:: T
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
Point :: type { x:: Int, y:: Int }

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
    export deriveAnswer :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
        input => meta.add_after(input, [
            meta.function("answer", [], Int, meta.expr_int(42))
        ])
    }
}
"""),
            ("main.eidos", """
import Tools.Generators.{deriveAnswer}


Subject :: type  expand deriveAnswer
{
    value:: Int
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
deriveAnswer :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => meta.add_after(input, [meta.function("answer", [], Int, meta.expr_int(42))])
}


Subject :: type  expand deriveAnswer
{
    value:: Int
}

Info :: comptime meta.shape_of(Subject);
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
    public void ClosedCaseReflection_PreservesHierarchyFieldsRelationsAndCacheIdentity()
    {
        const string source = """
Anim :: type {
    name :: String,

    Mammal :: type {
        warm :: Bool,

        Dog :: type {
            breed :: String,
        },

        Cat :: type {},
    },

    Reptile :: type {
        Snake :: type {},
    },
}

Direct :: comptime meta.cases_of(Anim);
MammalLeaves :: comptime meta.leaf_cases_of(Anim.Mammal);
AllLeaves :: comptime meta.leaf_cases_of(Anim);
DogParent :: comptime meta.parent_type_of(Anim.Mammal.Dog);
DogConstructor :: comptime meta.constructor_of(Anim.Mammal.Dog);
DogRoundTrip :: comptime meta.case_type_of(DogConstructor);
MammalDeclaredFields :: comptime meta.declared_fields_of(Anim.Mammal);
DogDeclaredFields :: comptime meta.declared_fields_of(Anim.Mammal.Dog);
DogFields :: comptime meta.fields_of(Anim.Mammal.Dog);
DogIsMammal :: comptime meta.is_subtype(Anim.Mammal.Dog, Anim.Mammal);
DogIsAnim :: comptime meta.is_subtype(Anim.Mammal.Dog, Anim);
MammalJoin :: comptime meta.join_type_of(Anim.Mammal.Dog, Anim.Mammal.Cat);
""";

        var result = Compile("meta_closed_case_reflection.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(result.TypeInferer);

        Assert.Equal(["Mammal", "Reptile"], ReadTypeNames("Direct", symbolTable, inferer));
        Assert.Equal(["Dog", "Cat"], ReadTypeNames("MammalLeaves", symbolTable, inferer));
        Assert.Equal(["Dog", "Cat", "Snake"], ReadTypeNames("AllLeaves", symbolTable, inferer));
        Assert.Equal("Mammal", Assert.IsType<ComptimeTypeValue>(GetComptimeValue("DogParent", symbolTable, inferer)).TypeRef.Name);
        Assert.Equal("Dog", Assert.IsType<ComptimeTypeValue>(GetComptimeValue("DogRoundTrip", symbolTable, inferer)).TypeRef.Name);
        Assert.Equal(["warm"], ReadFieldNames("MammalDeclaredFields", symbolTable, inferer));
        Assert.Equal(["breed"], ReadFieldNames("DogDeclaredFields", symbolTable, inferer));
        Assert.Equal(["name", "warm", "breed"], ReadFieldNames("DogFields", symbolTable, inferer));
        Assert.True(Assert.IsType<ComptimeBoolValue>(GetComptimeValue("DogIsMammal", symbolTable, inferer)).Value);
        Assert.True(Assert.IsType<ComptimeBoolValue>(GetComptimeValue("DogIsAnim", symbolTable, inferer)).Value);
        Assert.Equal("Mammal", Assert.IsType<ComptimeTypeValue>(GetComptimeValue("MammalJoin", symbolTable, inferer)).TypeRef.Name);

        foreach (var name in new[]
                 {
                     "Direct", "MammalLeaves", "AllLeaves", "DogParent", "DogConstructor", "DogRoundTrip",
                     "MammalDeclaredFields", "DogDeclaredFields", "DogFields", "DogIsMammal", "DogIsAnim", "MammalJoin"
                 })
        {
            var original = GetComptimeValue(name, symbolTable, inferer);
            Assert.True(ComptimeValuePayload.TryCreate(original, out var payload));
            Assert.True(payload.TryRestoreValue(remapper: null, out var restored));
            Assert.True(original.StructuralEquals(restored));
        }
    }

    [Fact]
    public void ClosedCaseReflection_PreservesSpecializedEffectArgumentsAcrossParentJoinAndSubtype()
    {
        const string source = """
io :: effect;
Alloc :: effect;

Envelope[E: effects] :: type {
    Branch[F: effects] :: type {
        Leaf :: type {},
        Other :: type {},
    },
}

Parent :: comptime meta.parent_type_of(Envelope[io].Branch[Alloc].Leaf);
Join :: comptime meta.join_type_of(
    Envelope[io].Branch[Alloc].Leaf,
    Envelope[io].Branch[Alloc].Other);
GoodSubtype :: comptime meta.is_subtype(
    Envelope[io].Branch[Alloc].Leaf,
    Envelope[io].Branch[Alloc]);
BadSubtype :: comptime meta.is_subtype(
    Envelope[io].Branch[Alloc].Leaf,
    Envelope[io].Branch[io]);
""";

        var result = Compile("meta_closed_case_effect_specialization.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(result.TypeInferer);
        var parent = Assert.IsType<ComptimeTypeValue>(GetComptimeValue("Parent", symbolTable, inferer));
        var join = Assert.IsType<ComptimeTypeValue>(GetComptimeValue("Join", symbolTable, inferer));
        Assert.Equal("Branch<io, Alloc>", parent.TypeRef.Name);
        Assert.Equal(parent.TypeRef.StableIdentity, join.TypeRef.StableIdentity);
        Assert.Equal(
            [MetaGenericArgumentDomain.EffectRow, MetaGenericArgumentDomain.EffectRow],
            parent.TypeRef.GenericArguments!.Select(static argument => argument.Domain));
        Assert.Equal(["io", "Alloc"], parent.TypeRef.GenericArguments!.Select(static argument => argument.Display));
        Assert.True(Assert.IsType<ComptimeBoolValue>(GetComptimeValue("GoodSubtype", symbolTable, inferer)).Value);
        Assert.False(Assert.IsType<ComptimeBoolValue>(GetComptimeValue("BadSubtype", symbolTable, inferer)).Value);
    }

    [Fact]
    public void ClosedCaseReflection_PreservesSpecializedGadtAndValueArguments()
    {
        const string source = """
Envelope[T, comptime N: Int] :: type {
    Branch[A, comptime M: Int] :: type case Envelope[A, N] {
        Leaf :: type {},
        Other :: type {},
    },
}

Parent :: comptime meta.parent_type_of(Envelope[String, 1].Branch[String, 2].Leaf);
Join :: comptime meta.join_type_of(
    Envelope[String, 1].Branch[String, 2].Leaf,
    Envelope[String, 1].Branch[String, 2].Other);
GoodSubtype :: comptime meta.is_subtype(
    Envelope[String, 1].Branch[String, 2].Leaf,
    Envelope[String, 1].Branch[String, 2]);
BadSubtype :: comptime meta.is_subtype(
    Envelope[String, 1].Branch[String, 2].Leaf,
    Envelope[String, 1].Branch[String, 3]);
""";

        var result = Compile("meta_closed_case_gadt_value_specialization.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(result.TypeInferer);
        var parent = Assert.IsType<ComptimeTypeValue>(GetComptimeValue("Parent", symbolTable, inferer));
        var join = Assert.IsType<ComptimeTypeValue>(GetComptimeValue("Join", symbolTable, inferer));
        Assert.Equal(parent.TypeRef.StableIdentity, join.TypeRef.StableIdentity);
        Assert.Equal(
            [
                MetaGenericArgumentDomain.Type,
                MetaGenericArgumentDomain.Value,
                MetaGenericArgumentDomain.Type,
                MetaGenericArgumentDomain.Value
            ],
            parent.TypeRef.GenericArguments!.Select(static argument => argument.Domain));
        Assert.Equal(
            ["String", "1", "String", "2"],
            parent.TypeRef.GenericArguments!.Select(static argument => argument.Display));
        Assert.True(Assert.IsType<ComptimeBoolValue>(GetComptimeValue("GoodSubtype", symbolTable, inferer)).Value);
        Assert.False(Assert.IsType<ComptimeBoolValue>(GetComptimeValue("BadSubtype", symbolTable, inferer)).Value);
    }

    [Fact]
    public void StructuredExpansion_MaterializesAllSupportedDeclarationCategories()
    {
        const string source = """
deriveArtifacts :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => meta.combine([
        meta.add_after(input, [
            meta.comptime_value("ANSWER", Int, meta.expr_int(42)),
            meta.test("test_generated", meta.expr_unit()),
            meta.module_member(meta.function("generated_value", [], Int, meta.expr_int(7)))
        ]),
        meta.report([
            meta.diagnostic("warning", meta.span_of(input), "generated warning")
        ])
    ])
}


Subject :: type  expand deriveArtifacts
{
    value:: Int
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
        Assert.Equal(42, Assert.IsType<ComptimeIntegerValue>(GetComptimeValue("ANSWER", symbolTable, inferer)).Value);
        Assert.Contains(symbolTable.Symbols.Values.OfType<FuncSymbol>(), static symbol =>
            symbol.Name == "test_generated" && symbol.GeneratedOrigin != null);
        Assert.Contains(symbolTable.Symbols.Values.OfType<FuncSymbol>(), static symbol =>
            symbol.Name == "generated_value" && symbol.GeneratedOrigin != null);
    }

    [Fact]
    public void Transformation_InsertionsUseExplicitAnchorsAndPreserveEditOrder()
    {
        const string source = """
generate_siblings :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => meta.combine([
        meta.add_after(input, [meta.function("after_subject", [], Int, meta.expr_int(2))]),
        meta.add_before(input, [meta.function("before_subject", [], Int, meta.expr_int(1))])
    ])
}

Subject :: type expand generate_siblings {}
""";

        var result = Compile("meta_explicit_anchors.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var declarationNames = module.Declarations.Select(static declaration => declaration switch
        {
            FuncDef function => function.Name,
            AdtDef type => type.Name,
            _ => null
        }).OfType<string>().ToArray();
        Assert.Equal(
            ["generate_siblings", "before_subject", "Subject", "after_subject"],
            declarationNames);
    }

    [Fact]
    public void BodyTransformation_CanReplaceAFunctionWhilePreservingItsContract()
    {
        const string source = """
replace_body :: comptime meta.Target[meta.Stage.Body] -> meta.Transformation {
    input => {
        parameter := meta.parameter("value", Int);
        replacement := meta.function("work", [parameter], Int, meta.expr_int(7));
        meta.replace_target(input, replacement)
    }
}

work :: Int -> Int expand replace_body {
    value => value
}
""";

        var result = Compile("meta_replace_body.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var work = Assert.IsType<ModuleDecl>(result.Ast).Declarations
            .OfType<FuncDef>()
            .Single(static function => function.Name == "work");
        var body = Assert.IsType<Eidosc.Ast.Expressions.LiteralExpr>(Assert.Single(work.Body).Expression);
        Assert.Equal(7L, Convert.ToInt64(body.Value, System.Globalization.CultureInfo.InvariantCulture));
        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        Assert.NotNull(Assert.IsType<FuncSymbol>(table.GetSymbol(work.SymbolId)).GeneratedOrigin);
    }

    [Fact]
    public void TransformationValidation_IsAtomicWhenALaterEditHasTheWrongCategory()
    {
        const string source = """
invalid_edit :: comptime meta.Target[meta.Stage.Body] -> meta.Transformation {
    input => meta.combine([
        meta.add_after(input, [meta.function("must_not_exist", [], Int, meta.expr_int(1))]),
        meta.replace_target(input, meta.comptime_value("WRONG", Int, meta.expr_int(2)))
    ])
}

work :: Int -> Int expand invalid_edit {
    value => value
}
""";

        var result = Compile("meta_atomic_edit.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "E3601" &&
            diagnostic.Message.Contains("target category", StringComparison.Ordinal));
        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        Assert.DoesNotContain(table.Symbols.Values, static symbol => symbol.Name == "must_not_exist");
        Assert.Contains(table.Symbols.Values.OfType<FuncSymbol>(), static symbol => symbol.Name == "work");
        var work = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<FuncDef>(),
            static function => function.Name == "work");
        Assert.All(EnumerateAstNodes(work), static node => Assert.Empty(node.GeneratedOriginChain));
    }

    [Fact]
    public void SemanticTransformation_RejectsShapeEditsAndCommitsNeitherEarlierItemsNorDiagnostics()
    {
        const string source = """
invalid_shape :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => meta.combine([
        meta.report([meta.diagnostic("warning", meta.span_of(input), "must not be reported")]),
        meta.add_after(input, [meta.function("must_not_exist", [], Int, meta.expr_int(1))]),
        meta.add_members(input, [quote member { generated :: Int }])
    ])
}

Subject :: type expand invalid_shape {
    value :: Int
}
""";

        var result = Compile("meta_semantic_shape_atomic.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3616" &&
            diagnostic.Message.Contains("field", StringComparison.OrdinalIgnoreCase) &&
            diagnostic.Message.Contains("Syntax", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "W3611" && diagnostic.Message == "must not be reported");
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var subject = Assert.Single(module.Declarations.OfType<AdtDef>(), static type => type.Name == "Subject");
        Assert.Equal(["value"], subject.Fields.Select(static field => field.Name));
        Assert.DoesNotContain(
            Assert.IsType<SymbolTable>(result.SymbolTable).Symbols.Values,
            static symbol => symbol.Name == "must_not_exist" || symbol.Name == "generated");
    }

    [Fact]
    public void TransformationValidation_RejectsALaterModuleCollisionWithoutCommittingEarlierEdits()
    {
        const string source = """
existing :: Unit -> Int {
    _ => 0
}

invalid_collision :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => meta.combine([
        meta.add_after(input, [meta.function("must_not_exist", [], Int, meta.expr_int(1))]),
        meta.add_after(input, [meta.function("existing", [], Int, meta.expr_int(2))])
    ])
}

Subject :: type expand invalid_collision {}
""";

        var result = Compile("meta_module_collision_atomic.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3616" &&
            diagnostic.Message.Contains("overload", StringComparison.OrdinalIgnoreCase));
        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        Assert.DoesNotContain(table.Symbols.Values, static symbol => symbol.Name == "must_not_exist");
        Assert.Single(table.Symbols.Values.OfType<FuncSymbol>(), static symbol => symbol.Name == "existing");
    }

    [Fact]
    public void TransformationValidation_RejectsConflictingTargetMutationsBeforeCommit()
    {
        const string source = """
conflict :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => {
        parameter := meta.parameter("value", Int);
        replacement := meta.function("work", [parameter], Int, meta.expr_int(7));
        meta.combine([
            meta.replace_target(input, replacement),
            meta.remove_target(input)
        ])
    }
}

work :: Int -> Int expand conflict {
    value => value
}
""";

        var result = Compile("meta_conflicting_target_mutations.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3602" &&
            diagnostic.Message.Contains("conflicting target mutations", StringComparison.OrdinalIgnoreCase));
        var work = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<FuncDef>(),
            static function => function.Name == "work");
        Assert.IsType<Eidosc.Ast.Expressions.IdentifierExpr>(Assert.Single(work.Body).Expression);
    }

    [Fact]
    public void BodyTransformation_InsertsOnlyPrivateHelperFunctions()
    {
        const string source = """
add_helper :: comptime meta.Target[meta.Stage.Body] -> meta.Transformation {
    input => meta.add_after(input, [meta.function("body_helper", [], Int, meta.expr_int(7))])
}

work :: Int -> Int expand add_helper {
    value => value
}
""";

        var result = Compile("meta_body_private_helper.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var helper = Assert.Single(
            Assert.IsType<SymbolTable>(result.SymbolTable).Symbols.Values.OfType<FuncSymbol>(),
            static symbol => symbol.Name == "body_helper");
        Assert.False(helper.IsPublic);
        Assert.NotNull(helper.GeneratedOrigin);
    }

    [Fact]
    public void GeneratedIdentity_IncludesCanonicalExplicitArguments()
    {
        const string sourceTemplate = """
generate_answer :: comptime Int -> meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    _ => input => meta.add_after(input, [meta.function("answer", [], Int, meta.expr_int(42))])
}

Subject :: type expand generate_answer(ARGUMENT) {}
""";
        var first = Compile("meta_argument_identity.eidos", sourceTemplate.Replace("ARGUMENT", "1", StringComparison.Ordinal));
        var repeated = Compile("meta_argument_identity.eidos", sourceTemplate.Replace("ARGUMENT", "1", StringComparison.Ordinal));
        var changed = Compile("meta_argument_identity.eidos", sourceTemplate.Replace("ARGUMENT", "2", StringComparison.Ordinal));

        Assert.True(first.Success, FormatDiagnostics(first));
        Assert.True(repeated.Success, FormatDiagnostics(repeated));
        Assert.True(changed.Success, FormatDiagnostics(changed));
        var firstOrigin = Assert.Single(Assert.IsType<SymbolTable>(first.SymbolTable).Symbols.Values.OfType<FuncSymbol>(),
            static symbol => symbol.Name == "answer").GeneratedOrigin!;
        var repeatedOrigin = Assert.Single(Assert.IsType<SymbolTable>(repeated.SymbolTable).Symbols.Values.OfType<FuncSymbol>(),
            static symbol => symbol.Name == "answer").GeneratedOrigin!;
        var changedOrigin = Assert.Single(Assert.IsType<SymbolTable>(changed.SymbolTable).Symbols.Values.OfType<FuncSymbol>(),
            static symbol => symbol.Name == "answer").GeneratedOrigin!;
        Assert.Equal(firstOrigin.CanonicalArgumentsHash, repeatedOrigin.CanonicalArgumentsHash);
        Assert.Equal(firstOrigin.StableIdentity, repeatedOrigin.StableIdentity);
        Assert.NotEqual(firstOrigin.CanonicalArgumentsHash, changedOrigin.CanonicalArgumentsHash);
        Assert.NotEqual(firstOrigin.StableIdentity, changedOrigin.StableIdentity);
    }

    [Fact]
    public void SyntaxTransformation_CanRemoveAnExplicitlyAuthorizedFunctionTarget()
    {
        const string source = """
remove_target :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => meta.remove_target(input)
}

obsolete :: Int -> Int expand remove_target {
    value => value
}
""";

        var result = Compile("meta_remove_target.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        Assert.DoesNotContain(module.Declarations.OfType<FuncDef>(), static function => function.Name == "obsolete");
        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        Assert.DoesNotContain(table.Symbols.Values, static symbol => symbol.Name == "obsolete");
    }

    [Fact]
    public void StructuredExpansion_RejectsInvalidGeneratedFunctionName()
    {
        const string source = """
deriveBad :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => meta.add_after(input, [meta.function("badFunction", [], Int, meta.expr_int(42))])
}


Subject :: type expand deriveBad { value:: Int }
""";

        var result = Compile("meta_generated_bad_function_name.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3602" &&
            diagnostic.Message.Contains("badFunction", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("lower_snake_case", StringComparison.Ordinal));
    }

    [Fact]
    public void StructuredExpansion_RejectsInvalidGeneratedComptimeName()
    {
        const string source = """
deriveBad :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => meta.add_after(input, [meta.comptime_value("answer", Int, meta.expr_int(42))])
}


Subject :: type expand deriveBad { value:: Int }
""";

        var result = Compile("meta_generated_bad_comptime_name.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3602" &&
            diagnostic.Message.Contains("answer", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("SCREAMING_SNAKE_CASE", StringComparison.Ordinal));
    }

    [Fact]
    public void StructuredExpansion_RejectsGeneratedTestWithoutTestPrefix()
    {
        const string source = """
deriveBad :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => meta.add_after(input, [meta.test("generated_test", meta.expr_unit())])
}


Subject :: type expand deriveBad { value:: Int }
""";

        var result = Compile("meta_generated_bad_test_name.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3602" &&
            diagnostic.Message.Contains("generated_test", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("test_", StringComparison.Ordinal));
    }

    [Fact]
    public void StructuredExpansion_AllowsUpperCamelCaseTypeValuedComptimeName()
    {
        var symbolTable = new SymbolTable();
        var targetSymbolId = symbolTable.DeclareAdt(
            "Subject",
            default,
            isPublic: true);
        var target = new Eidosc.Ast.Declarations.AdtDef
        {
            SymbolId = targetSymbolId
        };
        target.SetName("Subject");

        var typeValue = new ComptimeTypeValue(new MetaTypeRef(
            MetaTypeKind.Primitive,
            WellKnownStrings.BuiltinTypes.Type,
            "builtin:Type",
            SymbolId.None,
            new TypeId(BaseTypes.TypeValueId),
            []));
        var declaration = new ComptimeMetaObjectValue(
            "declaration.comptime-value",
            [
                new ComptimeNamedValue("name", new ComptimeStringValue("GeneratedType")),
                new ComptimeNamedValue("type", typeValue),
                new ComptimeNamedValue(
                    "value",
                    new ComptimeMetaObjectValue(
                        "expr.int",
                        [new ComptimeNamedValue("value", new ComptimeIntegerValue(42))]))
            ]);
        var targetValue = new ComptimeMetaObjectValue(
            "target",
            [new ComptimeNamedValue(
                "targetDecl",
                MetaComptimeIntrinsics.CreateDeclValue(
                    Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(targetSymbolId)),
                    symbolTable))]);
        var edit = new ComptimeMetaObjectValue(
            "transformation-edit",
            [
                new ComptimeNamedValue("kind", new ComptimeStringValue("insert-after")),
                new ComptimeNamedValue("target", targetValue),
                new ComptimeNamedValue(
                    "syntax",
                    new ComptimeSequenceValue(ComptimeSequenceKind.List, [declaration]))
            ]);
        var expansion = new ComptimeMetaObjectValue(
            "transformation",
            [new ComptimeNamedValue(
                "edits",
                new ComptimeSequenceValue(ComptimeSequenceKind.List, [edit]))]);
        var materializer = new MetaExpansionMaterializer(
            symbolTable,
            target,
            SymbolId.None,
            default);

        Assert.True(materializer.TryMaterialize(expansion, out var result, out var reason), reason);
        var generated = Assert.IsType<LetDecl>(Assert.Single(result.Nodes).Node);
        Assert.Equal("GeneratedType", Assert.IsType<VarPattern>(generated.Pattern).Name);
    }

}
