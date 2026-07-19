using Eidosc.Ast.Declarations;
using Eidosc.Pipeline;
using Eidosc.Symbols;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public sealed partial class MetaReflectionAndDeriveTests
{
    [Fact]
    public void CompilerSchemaConstructors_DoNotShadowImportedUserConstructors()
    {
        var result = CompileWorkspace(
            "main.eidos",
            ("Domain/Values.eidos", """
Domain.Values :: module {
    export Value :: type {
        ParseError :: type(String),
        Function :: type(Int),
        Member :: type(Int),
        IdentifierCategory :: type(Int)
    }
}
"""),
            ("main.eidos", """
import Domain.Values.*

parse_error :: Value = ParseError("user");
function :: Value = Function(1);
member :: Value = Member(2);
identifier_category :: Value = IdentifierCategory(3);

BuildDigest :: comptime build.Sha256.Sha256("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
"""));

        Assert.True(result.Success, FormatDiagnostics(result));
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var valueType = Assert.Single(symbolTable.Symbols.Values.OfType<AdtSymbol>(), static symbol =>
            symbol.Name == "Value" && symbol.ParentAdt == SymbolId.None);
        foreach (var constructorName in new[] { "ParseError", "Function", "Member", "IdentifierCategory" })
        {
            var constructorId = Assert.Contains(constructorName, symbolTable.GlobalConstructors);
            var constructor = Assert.IsType<CtorSymbol>(symbolTable.GetSymbol(constructorId));
            var caseType = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(constructor.OwnerAdt));
            Assert.Equal(valueType.Id, caseType.ParentAdt);
        }
        Assert.DoesNotContain("Sha256", symbolTable.GlobalConstructors.Keys);
    }

    [Fact]
    public void ModuleNamerRestore_RehydratesDeferredStagesWithoutRepeatingSemanticInvocations()
    {
        const string source = """
semantic_check :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => meta.report([
        meta.diagnostic("warning", meta.span_of(input), "semantic invocation")
    ])
}

inspect_body :: comptime meta.Target[meta.Stage.Body] -> meta.Transformation {
    input => meta.report([
        meta.diagnostic("warning", meta.span_of(input), "body invocation")
    ])
}

inspect_layout :: comptime meta.Target[meta.Stage.Layout] -> meta.Transformation {
    input => {
        target_type := meta.target_type_of(input);
        layout := meta.layout_of(target_type, "x86_64-pc-linux-gnu");
        meta.report([
            meta.diagnostic("warning", meta.span_of(input), "layout invocation")
        ])
    }
}

Subject :: type expand semantic_check expand inspect_body expand inspect_layout { value :: Int }
""";

        var first = Compile("meta_deferred_restore.eidos", source, static options =>
        {
            options.StopAtPhase = CompilationPhase.Namer;
            options.LlvmTargetTriple = "x86_64-pc-linux-gnu";
            options.EnableIncrementalCompilation = true;
            options.EnableDetailedProfiling = true;
            options.NoImplicitPrelude = true;
        });

        Assert.True(first.Success, FormatDiagnostics(first));
        Assert.NotNull(first.ModuleNamerStatePayloads);
        var second = Compile("meta_deferred_restore.eidos", source, options =>
        {
            options.StopAtPhase = CompilationPhase.Mir;
            options.LlvmTargetTriple = "x86_64-pc-linux-gnu";
            options.EnableIncrementalCompilation = true;
            options.EnableDetailedProfiling = true;
            options.NoImplicitPrelude = true;
            options.PreviousModuleSemanticSignatureSnapshot = first.ModuleSemanticSignatureSnapshot;
            options.PreviousModuleDependencySignatureSnapshot = first.ModuleDependencySignatureSnapshot;
            options.PreviousModuleNamerStatePayloads = first.ModuleNamerStatePayloads;
            options.ModuleArtifactAvailability = static (_, _, _, _) => true;
        });

        Assert.True(second.Success, FormatDiagnostics(second));
        var moduleRestoreApplied = second.ProfilingCounters.GetValueOrDefault("Namer.moduleRestore.applied");
        if (moduleRestoreApplied != 1)
        {
            Assert.DoesNotContain(second.Diagnostics, static diagnostic =>
                diagnostic.Level == global::Eidosc.Diagnostic.DiagnosticLevel.Error);
            return;
        }
        Assert.True(
            second.ProfilingCounters.GetValueOrDefault(
                "Namer.moduleRestore.rehydratedCompletedMetaInvocations") == 1,
            string.Join(Environment.NewLine, second.ProfilingCounters
                .Where(static entry => entry.Key.Contains("rehydratedMeta", StringComparison.Ordinal))
                .Select(static entry => $"{entry.Key}={entry.Value}")));
        Assert.DoesNotContain(second.Diagnostics, static diagnostic =>
            diagnostic.Code == "W3611" && diagnostic.Message == "semantic invocation");
        Assert.Single(second.Diagnostics, static diagnostic =>
            diagnostic.Code == "W3611" && diagnostic.Message == "body invocation");
        Assert.Single(second.Diagnostics, static diagnostic =>
            diagnostic.Code == "W3611" && diagnostic.Message == "layout invocation");

        var firstSubject = Assert.IsType<ModuleDecl>(first.Ast).Declarations
            .OfType<AdtDef>()
            .Single(static declaration => declaration.Name == "Subject");
        var secondSubject = Assert.IsType<ModuleDecl>(second.Ast).Declarations
            .OfType<AdtDef>()
            .Single(static declaration => declaration.Name == "Subject");
        Assert.Equal(
            firstSubject.MetaInvocations.Select(static invocation => invocation.OccurrenceId),
            secondSubject.MetaInvocations.Select(static invocation => invocation.OccurrenceId));
        Assert.Equal(
            firstSubject.MetaInvocations.Select(static invocation => invocation.Stage),
            secondSubject.MetaInvocations.Select(static invocation => invocation.Stage));
        Assert.Equal(
            firstSubject.MetaInvocations.Select(static invocation => invocation.SourceOrder),
            secondSubject.MetaInvocations.Select(static invocation => invocation.SourceOrder));
        Assert.Equal(
            firstSubject.MetaInvocations.Select(static invocation => invocation.Span),
            secondSubject.MetaInvocations.Select(static invocation => invocation.Span));
    }

    [Fact]
    public void PureOrdinaryFunctions_AreReusableDuringComptimeEvaluation()
    {
        const string source = """
double :: Int -> Int { value => value * 2 }
identity[T] :: T -> T { value => value }

Doubled :: comptime double(21);
Identity :: comptime identity(7);
""";

        var result = Compile("ordinary_function_comptime.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(result.TypeInferer);
        Assert.Equal(42, Assert.IsType<ComptimeIntegerValue>(GetComptimeValue("Doubled", table, inferer)).Value);
        Assert.Equal(7, Assert.IsType<ComptimeIntegerValue>(GetComptimeValue("Identity", table, inferer)).Value);
    }

    [Fact]
    public void ComptimePartialApplication_CapturesArgumentsButCannotEscapeThePhase()
    {
        const string source = """
add :: Int -> Int -> Int { left => right => left + right }

apply_partial :: Int -> Int {
    value => {
        add_ten := add(10);
        add_ten(value)
    }
}

Answer :: comptime apply_partial(32);
""";

        var result = Compile("comptime_partial_application.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(result.TypeInferer);
        Assert.Equal(42, Assert.IsType<ComptimeIntegerValue>(GetComptimeValue("Answer", table, inferer)).Value);

        var escaping = Compile("comptime_partial_application_escape.eidos", """
add :: Int -> Int -> Int { left => right => left + right }
Escaped :: comptime add(1);
""");
        Assert.False(escaping.Success);
        Assert.Contains(escaping.Diagnostics, static diagnostic =>
            diagnostic.Message.Contains("partially applied function values", StringComparison.Ordinal));
    }

    [Fact]
    public void EffectfulOrdinaryFunction_IsRejectedDuringComptimeEvaluation()
    {
        const string source = """
read_value :: Unit -> Int
    need io
{
    _ => 1
}

Invalid :: comptime read_value(());
""";

        var result = Compile("effectful_function_comptime.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Message.Contains("requires runtime abilities", StringComparison.Ordinal));
    }

    [Fact]
    public void ComptimeControlFlow_SupportsMutationLoopRecursionAndEarlyReturn()
    {
        const string source = """
factorial :: Int -> Int {
    value => if value <= 1 then 1 else value * factorial(value - 1)
}

sum_to :: Int -> Int {
    limit => {
        mut index := 0;
        mut total := 0;
        loop {
            if index >= limit then { break } else { () };
            index := index + 1;
            total := total + index;
        };
        total
    }
}

classify :: Int -> Int {
    value => {
        if value > 0 then { return 7 } else { () };
        3
    }
}

Factorial :: comptime factorial(5);
Sum :: comptime sum_to(4);
Positive :: comptime classify(1);
Negative :: comptime classify(-1);
""";

        var result = Compile("comptime_control_flow.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(result.TypeInferer);
        Assert.Equal(120, Assert.IsType<ComptimeIntegerValue>(GetComptimeValue("Factorial", table, inferer)).Value);
        Assert.Equal(10, Assert.IsType<ComptimeIntegerValue>(GetComptimeValue("Sum", table, inferer)).Value);
        Assert.Equal(7, Assert.IsType<ComptimeIntegerValue>(GetComptimeValue("Positive", table, inferer)).Value);
        Assert.Equal(3, Assert.IsType<ComptimeIntegerValue>(GetComptimeValue("Negative", table, inferer)).Value);
    }

    [Fact]
    public void ComptimeCalls_EvaluateOrdinaryAndMetaNamedArguments()
    {
        const string source = """
combine :: comptime Int -> Int -> Int {
    (left, right) => left * 10 + right
}

work :: Int -> Int { value => value }
Combined :: comptime combine(left = 4, right = 2);
WorkDeclaration :: comptime meta.declaration_of(value = work);
WorkShape :: comptime meta.shape_of(value = WorkDeclaration);
""";

        var result = Compile("comptime_named_arguments.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(result.TypeInferer);
        Assert.Equal(42, Assert.IsType<ComptimeIntegerValue>(GetComptimeValue("Combined", table, inferer)).Value);
        var declaration = Assert.IsType<ComptimeDeclValue>(GetComptimeValue("WorkDeclaration", table, inferer));
        Assert.Equal("work", declaration.Name);
        var shape = Assert.IsType<ComptimeAdtValue>(GetComptimeValue("WorkShape", table, inferer));
        Assert.Equal("Function", shape.ConstructorName);
    }

    [Fact]
    public void ComptimeCollectionsAndPropagation_SupportListSpreadAndOptionShortCircuit()
    {
        const string source = """
increment_some :: Option[Int] -> Option[Int] {
    input => {
        let? value = input;
        Some(value + 1)
    }
}

Spread :: comptime [1, ..[2, 3]];
Present :: comptime increment_some(Some(4));
Absent :: comptime increment_some(None());
""";

        var result = Compile("comptime_collections_propagation.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(result.TypeInferer);
        var spread = Assert.IsType<ComptimeSequenceValue>(GetComptimeValue("Spread", table, inferer));
        Assert.Equal([1L, 2L, 3L], spread.Elements.Select(static item => Assert.IsType<ComptimeIntegerValue>(item).Value));
        var present = Assert.IsType<ComptimeAdtValue>(GetComptimeValue("Present", table, inferer));
        Assert.Equal("Some", present.ConstructorName);
        Assert.Equal(5, Assert.IsType<ComptimeIntegerValue>(Assert.Single(present.PositionalValues)).Value);
        var absent = Assert.IsType<ComptimeAdtValue>(GetComptimeValue("Absent", table, inferer));
        Assert.Equal("None", absent.ConstructorName);
    }

    [Fact]
    public void ComptimeMapAndSetValues_UseCanonicalOrderingEqualityAndPayloadRoundTrip()
    {
        var one = new ComptimeIntegerValue(1);
        var two = new ComptimeIntegerValue(2);
        var firstValue = new ComptimeStringValue("first");
        var secondValue = new ComptimeStringValue("second");
        Assert.True(ComptimeMapValue.TryCreate(
            [new ComptimeMapEntry(two, secondValue), new ComptimeMapEntry(one, firstValue)],
            out var firstMap,
            out var reason), reason);
        Assert.True(ComptimeMapValue.TryCreate(
            [new ComptimeMapEntry(one, firstValue), new ComptimeMapEntry(two, secondValue)],
            out var secondMap,
            out reason), reason);

        Assert.True(firstMap.StructuralEquals(secondMap));
        Assert.Equal(firstMap.CanonicalHash, secondMap.CanonicalHash);
        Assert.Equal([one.CanonicalText, two.CanonicalText],
            firstMap.Entries.Select(static entry => entry.Key.CanonicalText));
        Assert.False(ComptimeMapValue.TryCreate(
            [new ComptimeMapEntry(one, firstValue), new ComptimeMapEntry(one, secondValue)],
            out _,
            out reason));
        Assert.Contains("conflicting", reason, StringComparison.Ordinal);

        var firstSet = ComptimeSetValue.Create([two, one, two]);
        var secondSet = ComptimeSetValue.Create([one, two]);
        Assert.True(firstSet.StructuralEquals(secondSet));
        Assert.Equal([one.CanonicalText, two.CanonicalText],
            firstSet.Elements.Select(static element => element.CanonicalText));

        var nested = ComptimeMapValue.TryCreate(
            [new ComptimeMapEntry(new ComptimeStringValue("numbers"), firstSet)],
            out var nestedMap,
            out reason)
            ? nestedMap
            : throw new Xunit.Sdk.XunitException(reason);
        Assert.True(ComptimeValuePayload.TryCreate(nested, out var payload));
        Assert.True(payload.TryRestoreValue(remapper: null, out var restored));
        Assert.True(nested.StructuralEquals(restored));
        Assert.True(ComptimePhaseValueValidator.TryValidate(restored, out reason), reason);

        var runtimeReference = new ComptimeIntegerValue(0)
        {
            StaticType = new TyRef { Inner = BaseTypes.Int }
        };
        var invalidSet = ComptimeSetValue.Create([runtimeReference]);
        Assert.False(ComptimePhaseValueValidator.TryValidate(invalidSet, out reason));
        Assert.Contains("Ref", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ComptimeArena_AccountsCanonicalAllocationsOnceAndEnforcesItsByteBudget()
    {
        var repeated = new ComptimeStringValue("repeated");
        var repeatedBytes = System.Text.Encoding.UTF8.GetByteCount(repeated.CanonicalText);
        var budget = new ComptimeResourceBudget(allocatedBytes: repeatedBytes);
        var arena = new ComptimeValueArena(budget);

        Assert.True(arena.TryAllocate(repeated, out var reason), reason);
        Assert.True(arena.TryAllocate(new ComptimeStringValue("repeated"), out reason), reason);
        Assert.Equal(1, arena.Count);
        Assert.Equal(repeatedBytes, arena.AllocatedBytes);
        Assert.False(arena.TryAllocate(new ComptimeStringValue("different"), out reason));
        Assert.Contains("allocated-value byte budget exceeded", reason, StringComparison.Ordinal);
        Assert.Equal(1, arena.Count);
        Assert.Equal(repeatedBytes, arena.AllocatedBytes);
    }

    [Fact]
    public void ComptimeArena_IsSharedByDerivedContextsAndBudgetsAreThreadSafe()
    {
        var budget = new ComptimeResourceBudget(fuel: 64, allocatedBytes: 4096);
        var context = new ComptimeEvaluationContext(
            new Dictionary<SymbolId, ComptimeValue>(),
            new Dictionary<SymbolId, FuncDef>(),
            Budget: budget);
        var derived = context with
        {
            Values = new Dictionary<SymbolId, ComptimeValue>(),
            CallDepth = context.CallDepth + 1
        };

        Assert.Same(budget, context.Resources);
        Assert.Same(context.Resources, derived.Resources);
        Assert.Same(context.ValuesArena, derived.ValuesArena);

        var successfulFuelConsumes = 0;
        Parallel.For(0, 256, index =>
        {
            if (derived.Resources.TryConsumeInstruction(out var unusedReason))
            {
                Interlocked.Increment(ref successfulFuelConsumes);
            }
        });

        Assert.Equal(64, successfulFuelConsumes);

        Parallel.For(0, 128, index =>
        {
            var value = new ComptimeStringValue($"value-{index % 32}");
            Assert.True(derived.ValuesArena.TryAllocate(value, out var reason), reason);
        });

        Assert.Equal(32, context.ValuesArena.Count);
    }

    [Fact]
    public void ComptimeOrdinaryGenericFunction_UsesResolvedTraitDispatch()
    {
        const string source = """
Magnitude :: trait {
    magnitude :: Self -> Int
}

Sample :: type { value :: Int }

MagnitudeSample :: instance Magnitude {
    magnitude :: Sample -> Int {
        sample => sample.value
    }
}

read_magnitude[T: Magnitude] :: T -> Int {
    value => magnitude(value)
}

MagnitudeValue :: comptime read_magnitude(Sample(value = 37));
""";

        var result = Compile("comptime_trait_dispatch.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(result.TypeInferer);
        Assert.Equal(37, Assert.IsType<ComptimeIntegerValue>(GetComptimeValue("MagnitudeValue", table, inferer)).Value);
    }

    [Fact]
    public void ComptimePhaseBoundary_RejectsRuntimeIdentityValuesRecursivelyButAllowsReflectedTypes()
    {
        var rawPointer = new ComptimeIntegerValue(0)
        {
            StaticType = new TyCon { Id = new TypeId(BaseTypes.RawPtrId), Name = "RawPtr" }
        };
        Assert.False(ComptimePhaseValueValidator.TryValidate(rawPointer, out var rawReason));
        Assert.Contains("RawPtr", rawReason, StringComparison.Ordinal);

        var nestedReference = new ComptimeSequenceValue(
            ComptimeSequenceKind.List,
            [new ComptimeIntegerValue(1) { StaticType = new TyRef { Inner = BaseTypes.Int } }]);
        Assert.False(ComptimePhaseValueValidator.TryValidate(nestedReference, out var referenceReason));
        Assert.Contains("Ref", referenceReason, StringComparison.Ordinal);

        var reflectedReferenceType = new ComptimeTypeValue(new MetaTypeRef(
            MetaTypeKind.Reference,
            "Ref[Int]",
            "reference:builtin:Int",
            SymbolId.None,
            TypeId.None,
            []));
        Assert.True(ComptimePhaseValueValidator.TryValidate(reflectedReferenceType, out var reflectedReason), reflectedReason);
    }

    [Fact]
    public void BodyStageGenerator_RunsAfterTypedBodyFactsAreAvailable()
    {
        const string source = """
inspect_body :: comptime meta.Target[meta.Stage.Body] -> meta.Transformation {
    input => {
        declaration := meta.target_declaration_of(input);
        body := meta.body_of(declaration);
        nodes := meta.nodes_of(body);
        meta.report([
            meta.diagnostic("warning", meta.span_of(nodes[0]), "typed body facts ready")
        ])
    }
}

work :: Int -> Int expand inspect_body {
    value => value + 1
}
""";

        var result = Compile("meta_body_stage_query.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "W3611" && diagnostic.Message == "typed body facts ready");
    }

    [Fact]
    public void FunctionHandles_ExposeTypedSignatureParametersOwnershipAndStageGatedBody()
    {
        const string source = """
work :: Int -> String {
    value => "ok"
}
""";

        var result = Compile("meta_function_handle_typed.eidos", source);
        Assert.True(result.Success, FormatDiagnostics(result));
        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var function = Assert.Single(module.Declarations.OfType<FuncDef>());
        Assert.NotEmpty(function.Body);
        var functionSymbol = Assert.IsType<FuncSymbol>(table.GetSymbol(function.SymbolId));
        Assert.True(table.Modules.TryGetOwningModuleId(function.SymbolId, out var moduleId));
        var declarations = new Dictionary<SymbolId, Declaration> { [function.SymbolId] = function };

        var semanticContext = new MetaComptimeContext(
            table,
            new Dictionary<SymbolId, AdtDef>(),
            new Dictionary<SymbolId, TraitDef>(),
            Declarations: declarations,
            QueryAccess: new MetaQueryAccessContext(
                moduleId,
                ClauseStage.Semantic,
                MetaQueryCapability.CurrentPackagePrivateShapes | MetaQueryCapability.CurrentPackageBodies,
                TargetSymbolId: function.SymbolId));
        var semanticHandle = MetaComptimeIntrinsics.CreateFunctionHandle(
            functionSymbol,
            function,
            table,
            semanticContext);

        Assert.Equal(WellKnownTypeIds.MetaFunctionId, Assert.IsType<TyCon>(semanticHandle.StaticType).Id.Value);
        Assert.True(semanticHandle.TryGet("type", out var typeValue));
        var functionType = Assert.IsType<ComptimeTypeValue>(typeValue).TypeRef;
        Assert.Equal(MetaTypeKind.Function, functionType.Kind);
        Assert.Equal(2, functionType.Arguments.Count);
        Assert.True(semanticHandle.TryGet("parameters", out var parameterValue));
        var parameters = Assert.IsType<ComptimeSequenceValue>(parameterValue).Elements;
        var parameter = Assert.IsType<ComptimeMetaObjectValue>(Assert.Single(parameters));
        Assert.Equal(WellKnownTypeIds.MetaParameterId, Assert.IsType<TyCon>(parameter.StaticType).Id.Value);
        Assert.True(parameter.TryGet("ownership", out var parameterOwnership));
        Assert.Equal(WellKnownTypeIds.MetaOwnershipId, Assert.IsType<TyCon>(Assert.IsType<ComptimeMetaObjectValue>(parameterOwnership).StaticType).Id.Value);
        Assert.True(semanticHandle.TryGet("body", out var semanticBody));
        Assert.IsType<ComptimeUnitValue>(semanticBody);

        var bodyContext = new MetaComptimeContext(
            table,
            new Dictionary<SymbolId, AdtDef>(),
            new Dictionary<SymbolId, TraitDef>(),
            Declarations: declarations,
            QueryAccess: semanticContext.Access with { AvailableStage = ClauseStage.Body });
        Assert.Equal(ClauseStage.Body, bodyContext.Access.AvailableStage);
        Assert.True(bodyContext.Access.AvailableStage >= ClauseStage.Body);
        var bodyHandle = MetaComptimeIntrinsics.CreateFunctionHandle(functionSymbol, function, table, bodyContext);
        Assert.True(bodyHandle.TryGet("body", out var bodyValue));
        Assert.Equal("body-handle", Assert.IsType<ComptimeMetaObjectValue>(bodyValue).SchemaKind);
    }

    [Fact]
    public void LayoutStageGenerator_RunsAfterMirAndUsesTheSelectedTargetInQueryIdentity()
    {
        const string source = """
inspect_layout :: comptime meta.Target[meta.Stage.Layout] -> meta.Transformation {
    input => {
        target_type := meta.target_type_of(input);
        layout := meta.layout_of(target_type, "x86_64-pc-linux-gnu");
        meta.report([
            meta.diagnostic("warning", meta.span_of(input), "layout facts ready")
        ])
    }
}

Subject :: type expand inspect_layout { value :: Int }
""";

        var result = Compile("meta_layout_stage_query.eidos", source, static options =>
        {
            options.StopAtPhase = CompilationPhase.Mir;
            options.LlvmTargetTriple = "x86_64-pc-linux-gnu";
        });

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.Equal(CompilationPhase.Mir, result.CompletedPhase);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "W3611" && diagnostic.Message == "layout facts ready");
    }

    [Fact]
    public void LayoutStageGenerator_ClosedCasePositionalPayloadContributesToRootAndExactCaseLayout()
    {
        const string source = """
inspect_closed_case_layout :: comptime meta.Target[meta.Stage.Layout] -> meta.Transformation {
    input => {
        root_type := meta.target_type_of(input);
        root_layout := meta.layout_of(root_type, "x86_64-pc-linux-gnu");
        cases := meta.cases_of(root_type);
        exact_layout := meta.layout_of(cases[0], "x86_64-pc-linux-gnu");
        if meta.layout_size(root_layout) == 8 && meta.layout_size(exact_layout) == 8 then {
            meta.keep()
        } else {
            meta.report([
                meta.diagnostic(
                    "error",
                    meta.span_of(input),
                    "closed case positional payload must contribute to layout"
                )
            ])
        }
    }
}

Expr[T] :: type expand inspect_closed_case_layout {
    IntLit :: type(Int) case Expr[Int],
    BoolLit :: type(Bool) case Expr[Bool],
}
""";

        var result = Compile("meta_closed_case_positional_layout.eidos", source, static options =>
        {
            options.StopAtPhase = CompilationPhase.Mir;
            options.LlvmTargetTriple = "x86_64-pc-linux-gnu";
        });

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void LayoutStageGenerator_ConcreteGenericClosedCaseSubstitutesRootTypeParameters()
    {
        const string source = """
Envelope[T] :: type {
    Value :: type {
        item :: T,
    },
}

inspect_generic_closed_case_layout :: comptime meta.Target[meta.Stage.Layout] -> meta.Transformation {
    input => {
        root_layout := meta.layout_of(Envelope[Int], "x86_64-pc-linux-gnu");
        exact_layout := meta.layout_of(Envelope[Int].Value, "x86_64-pc-linux-gnu");
        if meta.layout_size(root_layout) == 8 && meta.layout_size(exact_layout) == 8 then {
            meta.keep()
        } else {
            meta.report([
                meta.diagnostic(
                    "error",
                    meta.span_of(input),
                    "concrete closed case type arguments must determine field layout"
                )
            ])
        }
    }
}

Probe :: type expand inspect_generic_closed_case_layout {}
""";

        var result = Compile("meta_generic_closed_case_layout.eidos", source, static options =>
        {
            options.StopAtPhase = CompilationPhase.Mir;
            options.LlvmTargetTriple = "x86_64-pc-linux-gnu";
        });

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void LayoutStageGenerator_NestedClosedCaseMapsEffectiveTypeParametersAcrossValueArguments()
    {
        const string source = """
Envelope[T, comptime N: Int] :: type {
    root :: T,

    Branch[A] :: type {
        Value :: type {
            item :: A,
        },
    },
}

inspect_nested_closed_case_layout :: comptime meta.Target[meta.Stage.Layout] -> meta.Transformation {
    input => {
        layout := meta.layout_of(
            Envelope[Int, 1].Branch[Int32].Value,
            "x86_64-pc-linux-gnu"
        );
        offsets := meta.layout_field_offsets(layout);
        if meta.layout_size(layout) == 16 && offsets[0] == 0 && offsets[1] == 8 then {
            meta.keep()
        } else {
            meta.report([
                meta.diagnostic(
                    "error",
                    meta.span_of(input),
                    "nested closed case layout must map every effective type parameter"
                )
            ])
        }
    }
}

Probe :: type expand inspect_nested_closed_case_layout {}
""";

        var result = Compile("meta_nested_generic_closed_case_layout.eidos", source, static options =>
        {
            options.StopAtPhase = CompilationPhase.Mir;
            options.LlvmTargetTriple = "x86_64-pc-linux-gnu";
        });

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void LayoutStageGenerator_ClosedCaseLayoutAcceptsSupportedCrossTargetQueries()
    {
        const string source = """
inspect_cross_target_layout :: comptime meta.Target[meta.Stage.Layout] -> meta.Transformation {
    input => {
        root_type := meta.target_type_of(input);
        cases := meta.cases_of(root_type);
        linux_layout := meta.layout_of(cases[0], "x86_64-pc-linux-gnu");
        arm_layout := meta.layout_of(cases[0], "aarch64-pc-windows-msvc");
        if meta.layout_size(linux_layout) == 8 && meta.layout_size(arm_layout) == 8 then {
            meta.keep()
        } else {
            meta.report([
                meta.diagnostic(
                    "error",
                    meta.span_of(input),
                    "closed case layout must use the selected supported target"
                )
            ])
        }
    }
}

Subject :: type expand inspect_cross_target_layout {
    Present :: type(Ref[Int]),
    Missing :: type {},
}
""";

        var result = Compile("meta_closed_case_cross_target_layout.eidos", source, static options =>
        {
            options.StopAtPhase = CompilationPhase.Mir;
            options.LlvmTargetTriple = "x86_64-pc-linux-gnu";
        });

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void LayoutStageGenerator_CanAddOnlyLateConstantsAndTests()
    {
        const string source = """
emit_layout_artifacts :: comptime meta.Target[meta.Stage.Layout] -> meta.Transformation {
    input => meta.add_after(input, [
        meta.comptime_value("LAYOUT_SIZE", Int, meta.expr_int(8)),
        meta.test("test_layout_contract", meta.expr_unit())
    ])
}

Subject :: type expand emit_layout_artifacts { value :: Int }
""";

        var result = Compile("meta_layout_artifacts.eidos", source, static options =>
        {
            options.StopAtPhase = CompilationPhase.Mir;
            options.LlvmTargetTriple = "x86_64-pc-linux-gnu";
        });

        Assert.True(
            result.Success,
            $"completed={result.CompletedPhase}; diagnostics={FormatDiagnostics(result)}");
        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        var layoutConstant = Assert.Single(table.Symbols.Values.OfType<VarSymbol>(), static symbol =>
            symbol.Name == "LAYOUT_SIZE");
        var layoutTest = Assert.Single(table.Symbols.Values.OfType<FuncSymbol>(), static symbol =>
            symbol.Name == "test_layout_contract");
        Assert.False(layoutConstant.IsPublic);
        Assert.False(layoutTest.IsPublic);
        Assert.NotNull(layoutConstant.GeneratedOrigin);
        Assert.NotNull(layoutTest.GeneratedOrigin);
    }

    [Fact]
    public void LayoutStageGenerator_RejectsOrdinaryHelpersWithoutCommittingThem()
    {
        const string source = """
invalid_layout :: comptime meta.Target[meta.Stage.Layout] -> meta.Transformation {
    input => meta.add_after(input, [
        meta.function("must_not_exist", [], Int, meta.expr_int(8))
    ])
}

Subject :: type expand invalid_layout { value :: Int }
""";

        var result = Compile("meta_layout_invalid_helper.eidos", source, static options =>
        {
            options.StopAtPhase = CompilationPhase.Mir;
            options.LlvmTargetTriple = "x86_64-pc-linux-gnu";
        });

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3616" &&
            diagnostic.Message.Contains("Layout", StringComparison.Ordinal));
        Assert.DoesNotContain(
            Assert.IsType<SymbolTable>(result.SymbolTable).Symbols.Values,
            static symbol => symbol.Name == "must_not_exist");
    }

    [Fact]
    public void ModulePackageAndSyntaxQueries_ReturnTypedOpaqueHandles()
    {
        const string source = """
helper :: Int -> Int { value => value }
work :: Int -> Int { value => helper(value) }

WorkDecl :: comptime meta.declaration_of(work);
WorkSyntax :: comptime meta.syntax_of(WorkDecl);
CurrentModule :: comptime meta.module_of(WorkDecl);
CurrentPackage :: comptime meta.package_of(CurrentModule);
PackageModules :: comptime meta.modules_of(CurrentPackage);
ModuleImports :: comptime meta.imports_of(CurrentModule);
ModuleExports :: comptime meta.exports_of(CurrentModule);
""";

        var result = Compile("meta_module_query.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(result.TypeInferer);
        var syntax = Assert.IsType<ComptimeMetaObjectValue>(GetComptimeValue("WorkSyntax", table, inferer));
        Assert.Equal("syntax-handle", syntax.SchemaKind);
        Assert.True(syntax.TryGet("category", out var category));
        Assert.Equal("item.function", Assert.IsType<ComptimeStringValue>(category).Value);
        Assert.True(syntax.TryGet("span", out var spanValue));
        var span = Assert.IsType<ComptimeMetaObjectValue>(spanValue);
        Assert.True(span.TryGet("file", out var fileValue));
        var publicUri = Assert.IsType<ComptimeStringValue>(fileValue).Value;
        Assert.StartsWith("eidos-source://", publicUri, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetTempPath(), publicUri, StringComparison.OrdinalIgnoreCase);

        var module = Assert.IsType<ComptimeMetaObjectValue>(GetComptimeValue("CurrentModule", table, inferer));
        var package = Assert.IsType<ComptimeMetaObjectValue>(GetComptimeValue("CurrentPackage", table, inferer));
        Assert.Equal("module-handle", module.SchemaKind);
        Assert.Equal("package-handle", package.SchemaKind);
        Assert.NotEmpty(Assert.IsType<ComptimeSequenceValue>(GetComptimeValue("PackageModules", table, inferer)).Elements);
        Assert.IsType<ComptimeSequenceValue>(GetComptimeValue("ModuleImports", table, inferer));
        var exports = Assert.IsType<ComptimeSequenceValue>(GetComptimeValue("ModuleExports", table, inferer));
        Assert.Contains(exports.Elements, item => item is ComptimeDeclValue { Name: "work" });
    }

    [Fact]
    public void BodyReferenceAndCallQueries_UseStableSourceOrderedHandles()
    {
        const string source = """
helper :: Int -> Int { value => value + 1 }
work :: Int -> Int { value => helper(value) }

HelperDecl :: comptime meta.declaration_of(helper);
WorkDecl :: comptime meta.declaration_of(work);
WorkBody :: comptime meta.body_of(WorkDecl);
WorkNodes :: comptime meta.nodes_of(WorkBody);
WorkCalls :: comptime meta.calls_from(WorkDecl);
FirstCallArguments :: comptime meta.arguments_of(WorkCalls[0]);
CurrentModule :: comptime meta.module_of(WorkDecl);
CurrentScope :: comptime meta.module_scope(CurrentModule);
HelperReferences :: comptime meta.references_to(HelperDecl, CurrentScope);
HelperCallers :: comptime meta.callers_of(HelperDecl, CurrentScope);
""";

        var result = Compile("meta_body_query.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(result.TypeInferer);
        var nodes = Assert.IsType<ComptimeSequenceValue>(GetComptimeValue("WorkNodes", table, inferer));
        Assert.NotEmpty(nodes.Elements);
        Assert.All(nodes.Elements, static node => Assert.Equal("body-node-handle", Assert.IsType<ComptimeMetaObjectValue>(node).SchemaKind));

        var calls = Assert.IsType<ComptimeSequenceValue>(GetComptimeValue("WorkCalls", table, inferer));
        Assert.Single(calls.Elements);
        var call = Assert.IsType<ComptimeMetaObjectValue>(calls.Elements[0]);
        Assert.True(call.TryGet("callee", out var callee));
        Assert.Equal("helper", Assert.IsType<ComptimeDeclValue>(callee).Name);
        Assert.Single(Assert.IsType<ComptimeSequenceValue>(GetComptimeValue("FirstCallArguments", table, inferer)).Elements);
        Assert.NotEmpty(Assert.IsType<ComptimeSequenceValue>(GetComptimeValue("HelperReferences", table, inferer)).Elements);
        Assert.Single(Assert.IsType<ComptimeSequenceValue>(GetComptimeValue("HelperCallers", table, inferer)).Elements);
    }

    [Fact]
    public void ImplementationQuery_ReturnsCoherenceFactsInsideTheAuthorizedPackage()
    {
        const string source = """
Marker :: trait {
    marker :: Self -> Int
}

Value :: type { raw :: Int }

ValueMarker :: instance Marker {
    marker :: Value -> Int {
        value => value.raw
    }
}

MarkerDecl :: comptime meta.declaration_of(Marker);
CurrentModule :: comptime meta.module_of(MarkerDecl);
CurrentPackage :: comptime meta.package_of(CurrentModule);
PackageScope :: comptime meta.package_scope(CurrentPackage);
MarkerImplementations :: comptime meta.implementations_of(MarkerDecl, PackageScope);
""";

        var result = Compile("meta_implementation_query.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(result.TypeInferer);
        var implementations = Assert.IsType<ComptimeSequenceValue>(GetComptimeValue("MarkerImplementations", table, inferer));
        var implementation = Assert.IsType<ComptimeMetaObjectValue>(Assert.Single(implementations.Elements));
        Assert.Equal("implementation-handle", implementation.SchemaKind);
        Assert.True(implementation.TryGet("trait", out var trait));
        Assert.Equal("Marker", Assert.IsType<ComptimeDeclValue>(trait).Name);
    }

    [Fact]
    public void WorkspaceQuery_RejectsAmbientPrivilegeEscalation()
    {
        const string source = """
work :: Int -> Int { value => value }
WorkDecl :: comptime meta.declaration_of(work);
CurrentPackage :: comptime meta.package_of(WorkDecl);
Workspace :: comptime meta.workspace_of(CurrentPackage);
""";

        var result = Compile("meta_workspace_privacy.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Message.Contains("workspace meta capability", StringComparison.Ordinal));
    }

    [Fact]
    public void CrossPackageQueries_EnforcePrivateShapeAndBodyCapabilitiesAndPartitionCacheKeys()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_meta_privacy_{Guid.NewGuid():N}");
        var appRoot = Path.Combine(tempDir, "app");
        var dependencyRoot = Path.Combine(tempDir, "dependency", "src");
        Directory.CreateDirectory(appRoot);
        Directory.CreateDirectory(dependencyRoot);

        try
        {
            var entryFile = Path.Combine(appRoot, "Main.eidos");
            File.WriteAllText(entryFile, """
Main :: module {
    Dependency :: import dep.Library
}
""");
            File.WriteAllText(Path.Combine(dependencyRoot, "Library.eidos"), """
Library :: module {
    export public_work :: Int -> Int { value => value + 1 }
    private_work :: Int -> Int { value => value + 2 }
}
""");

            var result = new CompilationPipeline(File.ReadAllText(entryFile), new CompilationOptions
            {
                InputFile = entryFile,
                StopAtPhase = CompilationPhase.Types,
                NoImplicitPrelude = true,
                UseColors = false,
                AllowLegacyMetaSurface = true,
                PackageImportRoots = new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["dep"] = [dependencyRoot]
                }
            }).Run();

            Assert.True(result.Success, FormatDiagnostics(result));
            var table = Assert.IsType<SymbolTable>(result.SymbolTable);
            var root = Assert.IsType<ModuleDecl>(result.Ast);
            var declarations = AstStableNodeTraversal.Enumerate(root)
                .Select(static entry => entry.Node)
                .OfType<Declaration>()
                .Where(static declaration => declaration.SymbolId.IsValid)
                .DistinctBy(static declaration => declaration.SymbolId)
                .ToDictionary(static declaration => declaration.SymbolId);
            var currentModule = table.Modules.Modules.Values
                .Where(static module => module.PackageAlias == null && module.Path.SequenceEqual(["Main"]))
                .OrderByDescending(static module => module.Members.Count)
                .First();
            var dependencyModule = Assert.Single(table.Modules.Modules.Values, static module =>
                module.PackageAlias == "dep" && module.Path.SequenceEqual(["Library"]));
            Assert.NotEqual(currentModule.PackageInstanceKey, dependencyModule.PackageInstanceKey);

            var publicSymbol = Assert.Single(table.Symbols.Values.OfType<FuncSymbol>(), symbol =>
                symbol.Name == "public_work" &&
                table.Modules.TryGetOwningModuleId(symbol.Id, out var owner) && owner == dependencyModule.Id);
            var privateSymbol = Assert.Single(table.Symbols.Values.OfType<FuncSymbol>(), symbol =>
                symbol.Name == "private_work" &&
                table.Modules.TryGetOwningModuleId(symbol.Id, out var owner) && owner == dependencyModule.Id);
            Assert.True(publicSymbol.IsPublic);
            Assert.False(privateSymbol.IsPublic);

            var publicHandle = new ComptimeDeclValue(
                publicSymbol.Id,
                MetaComptimeIntrinsics.CreateStableIdentity(publicSymbol, table),
                publicSymbol.Name,
                "function",
                publicSymbol.Span);
            var privateHandle = new ComptimeDeclValue(
                privateSymbol.Id,
                MetaComptimeIntrinsics.CreateStableIdentity(privateSymbol, table),
                privateSymbol.Name,
                "function",
                privateSymbol.Span);
            var noCapabilities = CreateContext(MetaQueryCapability.None, new MetaQueryState());

            Assert.True(Query("shape_of", publicHandle, noCapabilities, out _, out var reason), reason);
            Assert.False(Query("shape_of", privateHandle, noCapabilities, out _, out reason));
            Assert.Contains("private declaration reflection", reason, StringComparison.Ordinal);
            Assert.False(Query("body_of", publicHandle, noCapabilities, out _, out reason));
            Assert.Contains("body/reference reflection", reason, StringComparison.Ordinal);

            var privateShapeContext = CreateContext(
                MetaQueryCapability.DependencyPrivateShapes,
                new MetaQueryState());
            Assert.True(Query("shape_of", privateHandle, privateShapeContext, out _, out reason), reason);
            Assert.False(Query("body_of", privateHandle, privateShapeContext, out _, out reason));

            var bodyContext = CreateContext(MetaQueryCapability.DependencyBodies, new MetaQueryState());
            Assert.True(Query("body_of", publicHandle, bodyContext, out _, out reason), reason);
            Assert.True(Query("body_of", privateHandle, bodyContext, out _, out reason), reason);

            var sharedState = new MetaQueryState();
            var firstKeyContext = CreateContext(MetaQueryCapability.None, sharedState);
            var secondKeyContext = CreateContext(MetaQueryCapability.DependencyPrivateShapes, sharedState);
            Assert.True(Query("shape_of", publicHandle, firstKeyContext, out _, out reason), reason);
            Assert.True(Query("shape_of", publicHandle, secondKeyContext, out _, out reason), reason);
            var cacheEntries = sharedState.SnapshotCacheEntries();
            Assert.Equal(2, cacheEntries.Count);
            Assert.Equal(2, cacheEntries.Select(static entry => entry.Key).Distinct(StringComparer.Ordinal).Count());

            MetaComptimeContext CreateContext(MetaQueryCapability capabilities, MetaQueryState state) => new(
                table,
                new Dictionary<SymbolId, AdtDef>(),
                new Dictionary<SymbolId, TraitDef>(),
                Declarations: declarations,
                QueryAccess: new MetaQueryAccessContext(
                    currentModule.Id,
                    ClauseStage.Body,
                    capabilities,
                    RequesterIdentity: MetaComptimeIntrinsics.CreateStableIdentity(currentModule, table)),
                SharedQueryState: state);

            static bool Query(
                string name,
                ComptimeValue argument,
                MetaComptimeContext context,
                out ComptimeValue value,
                out string reason) => MetaComptimeIntrinsics.TryEvaluateQuery(
                    name,
                    [argument],
                    context,
                    out value,
                    out reason);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CrossPackageBodyCapability_CannotBeForgedByQueryArguments()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_meta_privacy_source_{Guid.NewGuid():N}");
        var appRoot = Path.Combine(tempDir, "app");
        var dependencyRoot = Path.Combine(tempDir, "dependency", "src");
        Directory.CreateDirectory(appRoot);
        Directory.CreateDirectory(dependencyRoot);

        try
        {
            File.WriteAllText(Path.Combine(dependencyRoot, "Library.eidos"), """
Library :: module {
    export public_work :: Int -> Int { value => value + 1 }
}
""");
            var entryFile = Path.Combine(appRoot, "Main.eidos");
            File.WriteAllText(entryFile, """
Main :: module {
    Dependency :: import dep.Library
    PublicDecl :: comptime meta.declaration_of(Dependency.public_work)
    ForgedBody :: comptime meta.body_of(PublicDecl, "read-bodies")
}
""");

            var result = new CompilationPipeline(File.ReadAllText(entryFile), new CompilationOptions
            {
                InputFile = entryFile,
                StopAtPhase = CompilationPhase.Types,
                NoImplicitPrelude = true,
                UseColors = false,
                AllowLegacyMetaSurface = true,
                PackageImportRoots = new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["dep"] = [dependencyRoot]
                }
            }).Run();

            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, static diagnostic =>
                diagnostic.Message.Contains("expects one", StringComparison.Ordinal) ||
                diagnostic.Message.Contains("argument", StringComparison.Ordinal) ||
                diagnostic.Message.Contains("not callable", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void QueryHandles_PreserveCanonicalIdentityAcrossLiveStateRestore()
    {
        const string source = """
helper :: Int -> Int { value => value }
work :: Int -> Int { value => helper(value) }
WorkDecl :: comptime meta.declaration_of(work);
WorkBody :: comptime meta.body_of(WorkDecl);
WorkNodes :: comptime meta.nodes_of(WorkBody);
WorkCalls :: comptime meta.calls_from(WorkDecl);
""";

        var first = Compile("meta_query_restore.eidos", source, static options => options.EnableLiveStateCache = true);
        var second = Compile("meta_query_restore.eidos", source, static options => options.EnableLiveStateCache = true);

        Assert.True(first.Success, FormatDiagnostics(first));
        Assert.True(second.Success, FormatDiagnostics(second));
        var firstTable = Assert.IsType<SymbolTable>(first.SymbolTable);
        var secondTable = Assert.IsType<SymbolTable>(second.SymbolTable);
        var firstInferer = Assert.IsType<TypeInferer>(first.TypeInferer);
        var secondInferer = Assert.IsType<TypeInferer>(second.TypeInferer);
        foreach (var name in new[] { "WorkBody", "WorkNodes", "WorkCalls" })
        {
            var firstValue = GetComptimeValue(name, firstTable, firstInferer);
            var secondValue = GetComptimeValue(name, secondTable, secondInferer);
            Assert.Equal(firstValue.CanonicalText, secondValue.CanonicalText);
        }
    }

    [Fact]
    public void QueryCache_RecordsTraceAndPersistsCanonicalResultsAndDependencies()
    {
        const string source = """
work :: Int -> Int { value => value }
WorkDecl :: comptime meta.declaration_of(work);
FirstName :: comptime meta.name_of(WorkDecl);
SecondName :: comptime meta.name_of(WorkDecl);
""";

        var result = Compile("meta_query_cache_payload.eidos", source, static options =>
        {
            options.TraceComptime = true;
            options.EnableIncrementalCompilation = true;
            options.EnableLiveStateCache = true;
            options.EnableDetailedProfiling = true;
            options.StopAtPhase = CompilationPhase.Mir;
        });

        Assert.True(result.Success, FormatDiagnostics(result));
        var cacheTrace = result.ComptimeTrace
            .Where(static entry => entry.Kind == "query-cache" && entry.Operation == "meta.name_of")
            .ToArray();
        Assert.Contains(cacheTrace, static entry => entry.Outcome == "cache-miss");
        Assert.Contains(cacheTrace, static entry => entry.Outcome == "cache-hit");
        Assert.All(cacheTrace, static entry =>
        {
            Assert.Contains("key=", entry.Detail, StringComparison.Ordinal);
            Assert.Contains("resultHash=", entry.Detail, StringComparison.Ordinal);
            Assert.Contains("resultBytes=", entry.Detail, StringComparison.Ordinal);
        });

        var modulePayloads = Assert.IsAssignableFrom<IReadOnlyList<ModuleTypesStatePayload>>(
            result.ModuleTypesStatePayloads);
        var moduleQueryPayload = Assert.Single(modulePayloads
            .Select(static payload => payload.MetaQueries)
            .Where(static payload => payload.Dependencies.Any(dependency => dependency.CacheHit))
            .DistinctBy(static payload => payload.Hash));
        Assert.True(moduleQueryPayload.HasValidHash());
        Assert.NotEmpty(moduleQueryPayload.CacheEntries);
        Assert.Contains(moduleQueryPayload.Dependencies, static dependency => dependency.CacheHit);
        Assert.All(moduleQueryPayload.CacheEntries, static entry =>
        {
            Assert.Equal(64, entry.ResultHash.Length);
            Assert.True(entry.ResultBytes > 0);
        });
        Assert.True(moduleQueryPayload.TryRestoreCache(null, out var restoredEntries, out var failure), failure);
        var restoredState = new MetaQueryState();
        Assert.True(restoredState.TryRestoreCache(restoredEntries, out failure), failure);
        Assert.Equal(moduleQueryPayload.CacheEntries.Count, restoredState.SnapshotCacheEntries().Count);

        Assert.True(moduleQueryPayload.TryRestoreState(
            null,
            out restoredEntries,
            out var restoredDependencies,
            out failure), failure);
        restoredState = new MetaQueryState();
        Assert.True(restoredState.TryRestoreState(restoredEntries, restoredDependencies, out failure), failure);
        Assert.Equal(
            moduleQueryPayload.Dependencies
                .Select(static dependency => (
                    dependency.Key,
                    dependency.ResultHash,
                    dependency.CacheHit,
                    dependency.ResultBytes))
                .Distinct()
                .Count(),
            restoredState.SnapshotDependencies().Count);

        var livePayload = Assert.IsType<CompilationLiveStatePayload>(result.CompilationLiveStatePayload);
        Assert.True(livePayload.MetaQueries.HasValidHash());
        Assert.Equal(
            moduleQueryPayload.CacheEntries
                .Select(static entry => $"{entry.Key}:{entry.ResultHash}:{entry.ResultBytes}")
                .Order(StringComparer.Ordinal),
            livePayload.MetaQueries.CacheEntries
                .Select(static entry => $"{entry.Key}:{entry.ResultHash}:{entry.ResultBytes}")
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void QueryCachePayload_RemapsNestedHandleSymbolAndTypeIdsWithoutChangingCanonicalFacts()
    {
        var table = new SymbolTable();
        table.InitializeGlobalScope();
        var declaration = new ComptimeDeclValue(
            new SymbolId(700),
            "stable-declaration",
            "work",
            "function",
            Eidosc.Utils.SourceSpan.Empty)
        {
            StaticType = MetaSchemaRegistry.MetaType(
                WellKnownStrings.Meta.Types.Declaration,
                WellKnownTypeIds.MetaDeclarationId)
        };
        var body = new ComptimeMetaObjectValue(
            "body-handle",
            [new ComptimeNamedValue("owner", declaration)])
        {
            StaticType = MetaSchemaRegistry.MetaType(
                WellKnownStrings.Meta.Types.Body,
                WellKnownTypeIds.MetaBodyId)
        };
        var state = MetaQueryState.For(table);
        state.Store("body-key", body);
        state.Record("body-key", body, cacheHit: false);
        var payload = MetaQueryStatePayload.Create(table);
        var plan = new LiveStateRemapPlan(
            LiveStateRemapPlan.CurrentSchemaVersion,
            LiveStateRemapKind.StableKey,
            [new LiveStateSymbolRemapEntry(700, 1700)],
            [
                new LiveStateTypeRemapEntry(WellKnownTypeIds.MetaDeclarationId, 1014),
                new LiveStateTypeRemapEntry(WellKnownTypeIds.MetaBodyId, 1067)
            ],
            [],
            "");
        var remapper = new LiveStateIdRemapper(plan);

        Assert.True(payload.TryRestoreState(
            remapper,
            out var entries,
            out var dependencies,
            out var failure), failure);
        var restoredBody = Assert.IsType<ComptimeMetaObjectValue>(Assert.Single(entries).Value);
        Assert.Equal(1067, restoredBody.StaticType?.Id.Value);
        Assert.True(restoredBody.TryGet("owner", out var restoredOwnerValue));
        var restoredOwner = Assert.IsType<ComptimeDeclValue>(restoredOwnerValue);
        Assert.Equal(1700, restoredOwner.SymbolId.Value);
        Assert.Equal(1014, restoredOwner.StaticType?.Id.Value);
        Assert.Single(dependencies);
        Assert.Equal(body.CanonicalHash, restoredBody.CanonicalHash);
    }

    [Fact]
    public void QueryCache_ModuleTypesRestore_RehydratesCacheAndCanonicalDependencyHistory()
    {
        const string source = """
work :: Int -> Int { value => value }
WorkDecl :: comptime meta.declaration_of(work);
FirstName :: comptime meta.name_of(WorkDecl);
SecondName :: comptime meta.name_of(WorkDecl);
""";

        var first = Compile("meta_query_module_restore.eidos", source, static options =>
        {
            options.EnableIncrementalCompilation = true;
            options.EnableDetailedProfiling = true;
            options.NoImplicitPrelude = true;
            options.StopAtPhase = CompilationPhase.Types;
        });

        Assert.True(first.Success, FormatDiagnostics(first));
        var payloads = Assert.IsAssignableFrom<IReadOnlyList<ModuleTypesStatePayload>>(
            first.ModuleTypesStatePayloads);
        var payloadByModule = payloads.ToDictionary(static payload => payload.ModuleKey, StringComparer.Ordinal);
        Assert.Contains(payloads, static payload => payload.MetaQueries.CacheEntries.Count > 0);
        Assert.Contains(payloads, static payload => payload.MetaQueries.Dependencies.Count > 0);

        var second = Compile("meta_query_module_restore.eidos", source, options =>
        {
            options.EnableIncrementalCompilation = true;
            options.EnableDetailedProfiling = true;
            options.NoImplicitPrelude = true;
            options.StopAtPhase = CompilationPhase.Types;
            options.PreviousModuleSemanticSignatureSnapshot = first.ModuleSemanticSignatureSnapshot;
            options.PreviousModuleTypedSemanticSnapshot = first.ModuleTypedSemanticSnapshot;
            options.PreviousModuleMemberIndexSnapshot = first.ModuleMemberIndexSnapshot;
            options.PreviousModuleDependencySignatureSnapshot = first.ModuleDependencySignatureSnapshot;
            options.PreviousModuleNamerStatePayloads = first.ModuleNamerStatePayloads;
            options.PreviousModuleTypesStatePayloads = payloads;
            options.ModuleArtifactAvailability = (moduleKey, kind, _, _) => kind switch
            {
                ProjectModuleArtifactKinds.SemanticSignature => true,
                ProjectModuleArtifactKinds.TypedSemanticSignature => true,
                ProjectModuleArtifactKinds.TypesStatePayload => payloadByModule.ContainsKey(moduleKey),
                _ => false
            };
            options.ModuleTypesStatePayloadLoader = (moduleKey, kind, sourceHash, dependencyHash) =>
                kind == ProjectModuleArtifactKinds.TypesStatePayload &&
                payloadByModule.TryGetValue(moduleKey, out var payload) &&
                string.Equals(sourceHash, payload.TypedSemantic.LocalSurfaceHash, StringComparison.Ordinal) &&
                string.Equals(
                    dependencyHash,
                    payload.TypedSemantic.DependencyTypedSemanticHash,
                    StringComparison.Ordinal)
                    ? payload
                    : null;
        });

        Assert.True(second.Success, FormatDiagnostics(second));
        Assert.True(
            second.ProfilingCounters.GetValueOrDefault("Types.moduleRestore.applied") == 1,
            string.Join(Environment.NewLine, second.ProfilingCounters
                .Where(static counter => counter.Key.Contains("moduleRestore", StringComparison.Ordinal) ||
                                         counter.Key.Contains("moduleTypedArtifact", StringComparison.Ordinal))
                .OrderBy(static counter => counter.Key, StringComparer.Ordinal)
                .Select(static counter => $"{counter.Key}={counter.Value}")) + Environment.NewLine +
            string.Join(Environment.NewLine, LiveStateStableIdentityBuilder
                .BuildSymbolIdentities(Assert.IsType<SymbolTable>(second.SymbolTable))
                .GroupBy(static identity => identity.StableKey.ToString(), StringComparer.Ordinal)
                .Where(static group => group.Count() > 1)
                .Select(group => string.Join(
                    " | ",
                    group.Select(static identity =>
                        $"{identity.SymbolKind}:{identity.Name}:{identity.SymbolId}:{identity.StableKey}")))));
        Assert.True(second.ProfilingCounters.GetValueOrDefault(
            "Types.moduleRestore.restoredMetaQueryCacheEntries") > 0);
        Assert.True(second.ProfilingCounters.GetValueOrDefault(
            "Types.moduleRestore.restoredMetaQueryDependencies") > 0);

        var state = MetaQueryState.For(Assert.IsType<SymbolTable>(second.SymbolTable));
        var restoredCache = state.SnapshotCacheEntries();
        var restoredDependencies = state.SnapshotDependencies();
        Assert.Equal(
            payloads.SelectMany(static payload => payload.MetaQueries.CacheEntries)
                .Select(static entry => entry.Key)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            restoredCache.Count);
        Assert.Equal(
            payloads.SelectMany(static payload => payload.MetaQueries.Dependencies)
                .Select(static dependency => (
                    dependency.Key,
                    dependency.ResultHash,
                    dependency.CacheHit,
                    dependency.ResultBytes))
                .Distinct()
                .Count(),
            restoredDependencies.Count);
        Assert.Equal(
            Enumerable.Range(1, restoredDependencies.Count).Select(static sequence => (long)sequence),
            restoredDependencies.Select(static dependency => dependency.Sequence));

        var cached = restoredCache[0];
        Assert.True(state.TryGet(cached.Key, out var cachedValue));
        state.Record(cached.Key, cachedValue, cacheHit: true);
        var currentDependency = Assert.Single(
            state.SnapshotDependencies(),
            dependency => dependency.Sequence == restoredDependencies.Count + 1L);
        Assert.True(currentDependency.CacheHit);
        Assert.Equal(cached.Key, currentDependency.Key);
        Assert.Equal(cached.Value.CanonicalHash, currentDependency.ResultHash);
    }

    [Fact]
    public void QueryCache_UsesFactLevelBodyDependencies()
    {
        const string baseline = """
work :: Int -> Int { value => value + 1 }
unrelated :: Int -> Int { value => value + 10 }
WorkDecl :: comptime meta.declaration_of(work);
WorkName :: comptime meta.name_of(WorkDecl);
WorkShape :: comptime meta.shape_of(WorkDecl);
WorkBody :: comptime meta.body_of(WorkDecl);
""";
        const string changedWorkBody = """
work :: Int -> Int { value => value + 2 }
unrelated :: Int -> Int { value => value + 10 }
WorkDecl :: comptime meta.declaration_of(work);
WorkName :: comptime meta.name_of(WorkDecl);
WorkShape :: comptime meta.shape_of(WorkDecl);
WorkBody :: comptime meta.body_of(WorkDecl);
""";
        const string changedUnrelatedBody = """
work :: Int -> Int { value => value + 1 }
unrelated :: Int -> Int { value => value + 11 }
WorkDecl :: comptime meta.declaration_of(work);
WorkName :: comptime meta.name_of(WorkDecl);
WorkShape :: comptime meta.shape_of(WorkDecl);
WorkBody :: comptime meta.body_of(WorkDecl);
""";

        var first = CompileWithTrace(baseline);
        var changedWork = CompileWithTrace(changedWorkBody);
        var changedUnrelated = CompileWithTrace(changedUnrelatedBody);

        Assert.True(first.Success, FormatDiagnostics(first));
        Assert.True(changedWork.Success, FormatDiagnostics(changedWork));
        Assert.True(changedUnrelated.Success, FormatDiagnostics(changedUnrelated));
        foreach (var operation in new[] { "meta.name_of", "meta.shape_of" })
        {
            Assert.Equal(ReadKey(first, operation), ReadKey(changedWork, operation));
            Assert.Equal(ReadKey(first, operation), ReadKey(changedUnrelated, operation));
        }

        Assert.NotEqual(ReadKey(first, "meta.body_of"), ReadKey(changedWork, "meta.body_of"));
        Assert.Equal(ReadKey(first, "meta.body_of"), ReadKey(changedUnrelated, "meta.body_of"));

        static CompilationResult CompileWithTrace(string source) => Compile(
            "meta_query_fact_dependencies.eidos",
            source,
            static options =>
            {
                options.TraceComptime = true;
                options.NoImplicitPrelude = true;
            });

        static string ReadKey(CompilationResult result, string operation)
        {
            var trace = Assert.Single(result.ComptimeTrace, entry =>
                entry.Kind == "query-cache" &&
                entry.Operation == operation &&
                entry.Outcome == "cache-miss");
            const string prefix = "key=";
            var separator = trace.Detail.IndexOf(';');
            Assert.True(trace.Detail.StartsWith(prefix, StringComparison.Ordinal) && separator > prefix.Length);
            return trace.Detail[prefix.Length..separator];
        }
    }

    [Fact]
    public void QueryHandles_UsePackageRelativeUrisAcrossDifferentHostRoots()
    {
        const string source = """
work :: Int -> Int { value => value }
WorkDecl :: comptime meta.declaration_of(work);
WorkSyntax :: comptime meta.syntax_of(WorkDecl);
""";

        var first = CompileWorkspace("src/work.eidos", ("src/work.eidos", source));
        var second = CompileWorkspace("src/work.eidos", ("src/work.eidos", source));

        Assert.True(first.Success, FormatDiagnostics(first));
        Assert.True(second.Success, FormatDiagnostics(second));
        var firstValue = Assert.IsType<ComptimeMetaObjectValue>(GetComptimeValue(
            "WorkSyntax",
            Assert.IsType<SymbolTable>(first.SymbolTable),
            Assert.IsType<TypeInferer>(first.TypeInferer)));
        var secondValue = Assert.IsType<ComptimeMetaObjectValue>(GetComptimeValue(
            "WorkSyntax",
            Assert.IsType<SymbolTable>(second.SymbolTable),
            Assert.IsType<TypeInferer>(second.TypeInferer)));
        Assert.True(firstValue.TryGet("span", out var firstSpanValue));
        Assert.True(secondValue.TryGet("span", out var secondSpanValue));
        var firstSpan = Assert.IsType<ComptimeMetaObjectValue>(firstSpanValue);
        var secondSpan = Assert.IsType<ComptimeMetaObjectValue>(secondSpanValue);
        Assert.True(firstSpan.TryGet("file", out var firstFileValue));
        Assert.True(secondSpan.TryGet("file", out var secondFileValue));
        Assert.Equal(
            Assert.IsType<ComptimeStringValue>(firstFileValue).Value,
            Assert.IsType<ComptimeStringValue>(secondFileValue).Value);
    }

    [Fact]
    public void TypeAndDeclarationShapes_AreTypedExhaustiveAndHideCompilerImplementationNames()
    {
        const string source = """
Marker :: trait {
    mark :: Self -> Int
}

Alias :: type = Int;
Record :: type { value :: Int }
Root :: type {
    common :: String,
    Leaf :: type { value :: Int }
}
Holder :: type {
    pair :: (Int, String),
    callback :: Int -> String,
    reference :: Ref[Int]
}

find_field_type :: comptime Option[meta.Field] -> Type {
    Some(field) => meta.type_of(field),
    None => Unit
}

shape_name :: comptime meta.TypeShape -> String {
    meta.TypeShape.Primitive(_) => "primitive",
    meta.TypeShape.Function(_) => "function",
    _ => "other"
}

declaration_shape_name :: comptime meta.DeclarationShape -> String {
    meta.DeclarationShape.Function(_) => "function",
    _ => "other"
}

work :: Int -> Int { value => value }
PairType :: comptime find_field_type(meta.find_field(Holder, "pair"));
CallbackType :: comptime find_field_type(meta.find_field(Holder, "callback"));
ReferenceType :: comptime find_field_type(meta.find_field(Holder, "reference"));
PrimitiveShape :: comptime meta.shape_of(Int);
NominalShape :: comptime meta.shape_of(Record);
AliasShape :: comptime meta.shape_of(Alias);
ClosedShape :: comptime meta.shape_of(Root);
CaseShape :: comptime meta.shape_of(meta.cases_of(Root)[0]);
TupleShape :: comptime meta.shape_of(PairType);
FunctionShape :: comptime meta.shape_of(CallbackType);
ReferenceShape :: comptime meta.shape_of(ReferenceType);
TraitShape :: comptime meta.shape_of(Marker);
WorkShape :: comptime meta.shape_of(meta.declaration_of(work));
PrimitiveName :: comptime shape_name(PrimitiveShape);
FunctionName :: comptime shape_name(FunctionShape);
DeclarationName :: comptime declaration_shape_name(WorkShape);
""";

        var result = Compile("meta_typed_exhaustive_shapes.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(result.TypeInferer);
        var expectedCases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PrimitiveShape"] = "Primitive",
            ["NominalShape"] = "Nominal",
            ["AliasShape"] = "Alias",
            ["ClosedShape"] = "ClosedSum",
            ["CaseShape"] = "Case",
            ["TupleShape"] = "Tuple",
            ["FunctionShape"] = "Function",
            ["ReferenceShape"] = "Reference",
            ["TraitShape"] = "Trait",
            ["WorkShape"] = "Function"
        };
        foreach (var (name, expectedCase) in expectedCases)
        {
            var shape = Assert.IsType<ComptimeAdtValue>(GetComptimeValue(name, symbolTable, inferer));
            Assert.Equal(expectedCase, shape.ConstructorName);
            var canonical = shape.CanonicalText;
            Assert.DoesNotContain("TyCon", canonical, StringComparison.Ordinal);
            Assert.DoesNotContain("TyFun", canonical, StringComparison.Ordinal);
            Assert.DoesNotContain("TyRef", canonical, StringComparison.Ordinal);
            Assert.DoesNotContain("AdtSymbol", canonical, StringComparison.Ordinal);
            Assert.DoesNotContain("SymbolId", canonical, StringComparison.Ordinal);
            Assert.DoesNotContain("RuntimeType", canonical, StringComparison.Ordinal);
        }

        var casePayload = ReadShapePayload(Assert.IsType<ComptimeAdtValue>(
            GetComptimeValue("CaseShape", symbolTable, inferer)));
        Assert.True(casePayload.TryGet("parentSpecialization", out var parentSpecialization));
        Assert.NotEmpty(Assert.IsType<ComptimeStringValue>(parentSpecialization).Value);
        Assert.True(casePayload.TryGet("commonFields", out var commonFields));
        Assert.Single(Assert.IsType<ComptimeSequenceValue>(commonFields).Elements);
        Assert.True(casePayload.TryGet("localFields", out var localFields));
        Assert.Single(Assert.IsType<ComptimeSequenceValue>(localFields).Elements);
        Assert.True(casePayload.TryGet("fields", out var effectiveFields));
        var reflectedFields = Assert.IsType<ComptimeSequenceValue>(effectiveFields).Elements
            .Select(Assert.IsType<ComptimeMetaObjectValue>)
            .ToArray();
        Assert.Equal(2, reflectedFields.Length);
        Assert.True(reflectedFields[0].TryGet("inherited", out var inherited));
        Assert.True(Assert.IsType<ComptimeBoolValue>(inherited).Value);
        Assert.True(reflectedFields[1].TryGet("inherited", out inherited));
        Assert.False(Assert.IsType<ComptimeBoolValue>(inherited).Value);

        Assert.Equal("primitive", Assert.IsType<ComptimeStringValue>(
            GetComptimeValue("PrimitiveName", symbolTable, inferer)).Value);
        Assert.Equal("function", Assert.IsType<ComptimeStringValue>(
            GetComptimeValue("FunctionName", symbolTable, inferer)).Value);
        Assert.Equal("function", Assert.IsType<ComptimeStringValue>(
            GetComptimeValue("DeclarationName", symbolTable, inferer)).Value);
    }

    [Fact]
    public void DeclarationShapes_ClassifyParametersAndExposeTypedAssociatedItems()
    {
        const string source = """
Iterator[I] :: trait {
    Item :: type
    Min :: I
    next :: I -> I
}

IteratorItems :: comptime meta.items_of(Iterator[Int]);
""";

        var result = Compile("meta_declaration_shape_categories.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(result.TypeInferer);
        var items = Assert.IsType<ComptimeSequenceValue>(
            GetComptimeValue("IteratorItems", symbolTable, inferer));
        Assert.Equal(
            ["Function", "AssociatedType", "AssociatedConst"],
            items.Elements.Select(item => Assert.IsType<ComptimeAdtValue>(item).ConstructorName));
        Assert.All(items.Elements, item =>
        {
            var shape = Assert.IsType<ComptimeAdtValue>(item);
            Assert.Equal(WellKnownTypeIds.MetaDeclarationShapeId, shape.StaticType?.Id.Value);
        });

        var parameterId = symbolTable.RegisterSymbol(new VarSymbol
        {
            Name = "value",
            Span = Eidosc.Utils.SourceSpan.Empty,
            IsParameter = true,
            IsPublic = true
        });
        var parameter = Assert.IsType<VarSymbol>(symbolTable.GetSymbol(parameterId));
        var parameterHandle = new ComptimeDeclValue(
            parameter!.Id,
            MetaComptimeIntrinsics.CreateStableIdentity(parameter, symbolTable),
            parameter.Name,
            "parameter",
            parameter.Span);
        var meta = new MetaComptimeContext(
            symbolTable,
            new Dictionary<SymbolId, AdtDef>(),
            new Dictionary<SymbolId, TraitDef>(),
            QueryAccess: MetaQueryAccessContext.Default,
            SharedQueryState: new MetaQueryState());
        Assert.True(MetaComptimeIntrinsics.TryEvaluateQuery(
            "shape_of",
            [parameterHandle],
            meta,
            out var parameterShape,
            out var reason), reason);
        Assert.Equal("Parameter", Assert.IsType<ComptimeAdtValue>(parameterShape).ConstructorName);
    }
}
