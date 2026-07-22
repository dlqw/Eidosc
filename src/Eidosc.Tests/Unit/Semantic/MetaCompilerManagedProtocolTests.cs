using System.Text.Json;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ide;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Semantic;
using Eidosc.Symbols;
using Eidosc.Syntax;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class MetaCompilerManagedProtocolTests
{
    [Fact]
    public void Protocol_registry_classifies_current_signatures_without_legacy_surface()
    {
        const string source = """
syntax_pass :: comptime meta.Syntax[meta.Item] -> meta.Syntax[meta.Item] { value => value }
derive_pass :: comptime meta.Type -> meta.Items { _ => [] }
body_pass :: comptime meta.Function -> meta.Function { value => value }
analyze_pass :: comptime meta.Package -> Seq[meta.Diagnostic] { _ => [] }
extend_items :: comptime meta.Package -> meta.Items { _ => [] }
extend_modules :: comptime meta.Package -> meta.Modules { _ => [] }
build_pass :: comptime build.Inputs -> build.Graph { _ => build.graph(build.emit(build.session()), [], []) }
""";

        var result = Compile(source, CompilationPhase.Namer);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Level == global::Eidosc.Diagnostic.DiagnosticLevel.Error);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        AssertProtocol(module, symbolTable, "syntax_pass", CompilerMetaProtocolKind.SyntaxExpansion);
        AssertProtocol(module, symbolTable, "derive_pass", CompilerMetaProtocolKind.Derive);
        AssertProtocol(module, symbolTable, "body_pass", CompilerMetaProtocolKind.BodyTransform);
        AssertProtocol(module, symbolTable, "analyze_pass", CompilerMetaProtocolKind.Analyzer);
        AssertProtocol(module, symbolTable, "extend_items", CompilerMetaProtocolKind.ExtensionItems);
        AssertProtocol(module, symbolTable, "extend_modules", CompilerMetaProtocolKind.ExtensionModules);
        AssertProtocol(module, symbolTable, "build_pass", CompilerMetaProtocolKind.BuildHost);
    }

    [Theory]
    [InlineData("Eq")]
    [InlineData("Show")]
    [InlineData("Ord")]
    [InlineData("Hash")]
    [InlineData("Clone")]
    [InlineData("Copy")]
    public void Protocol_registry_classifies_builtin_and_user_derive_through_one_kind(string traitName)
    {
        Assert.True(CompilerMetaProtocolRegistry.TryClassifyBuiltinDerive(
            traitName,
            out var builtin,
            out var normalizedTrait));
        Assert.Equal(CompilerMetaProtocolKind.Derive, builtin.Kind);
        Assert.Equal(ClauseStage.Semantic, builtin.EarliestStage);
        Assert.Equal(traitName, normalizedTrait);

        const string source = "derive_pass :: comptime meta.Type -> meta.Items { _ => [] }";
        var result = Compile(source, CompilationPhase.Namer);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        AssertProtocol(module, symbolTable, "derive_pass", builtin.Kind);
    }

    [Fact]
    public void Protocol_registry_rejects_unknown_builtin_derive()
    {
        Assert.False(CompilerMetaProtocolRegistry.TryClassifyBuiltinDerive(
            "Unknown",
            out _,
            out _));
    }

    [Fact]
    public void Legacy_target_transformation_and_query_fixture_is_rejected()
    {
        const string source = """
legacy :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => meta.keep(input)
}
""";

        var result = Compile(source, CompilationPhase.Types);

        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Level == global::Eidosc.Diagnostic.DiagnosticLevel.Error &&
            (diagnostic.Message.Contains("Target", StringComparison.Ordinal) ||
             diagnostic.Message.Contains("Transformation", StringComparison.Ordinal)));
    }

    [Fact]
    public void Typed_derive_tag_uses_meta_type_to_items_protocol()
    {
        const string source = """
derive_empty :: comptime meta.Type -> meta.Items { _ => [] }

@[expand(derive_empty)]
Subject :: type {
    value :: Int
}
""";

        var result = Compile(source, CompilationPhase.Types);

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.DoesNotContain(result.Diagnostics, static diagnostic =>
            diagnostic.Message.Contains("Target", StringComparison.Ordinal) ||
            diagnostic.Message.Contains("Transformation", StringComparison.Ordinal));
    }

    [Fact]
    public void Typed_derive_materializes_structured_generated_items()
    {
        const string source = """
derive_answer :: comptime meta.Type -> meta.Items {
    _ => [meta.function("answer", [], Int, meta.expr_int(42))]
}

@[expand(derive_answer)]
Subject :: type {}
""";

        var result = Compile(source, CompilationPhase.Types);

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.Contains(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<FuncDef>(),
            function => function.Name == "answer");
    }

    [Fact]
    public void Typed_derive_materializes_high_level_semantic_instance()
    {
        const string source = """
Marker :: trait {
    marker :: Self -> String
}

derive_marker :: comptime meta.Type -> meta.Items {
    target => {
        parameter := meta.parameter("value", target);
        method := meta.function(
            "marker",
            [parameter],
            String,
            meta.expr_string(meta.name_of(target))
        );
        [meta.instance(meta.declaration_of(Marker), target, [method])]
    }
}

@[expand(derive_marker)]
Subject :: type {}
""";

        var result = Compile(source, CompilationPhase.Types);

        Assert.True(result.Success, FormatDiagnostics(result));
        var instance = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<InstanceDecl>(),
            static declaration => declaration.Name.Contains("Marker", StringComparison.Ordinal));
        Assert.NotEmpty(instance.GeneratedOriginChain);
        Assert.Single(instance.Methods, static method => method.Name == "marker");
    }

    [Fact]
    public void Removed_meta_implementation_builder_is_not_a_compatibility_alias()
    {
        const string source = """
Marker :: trait { marker :: Self -> String }
derive_marker :: comptime meta.Type -> meta.Items {
    target => [meta.implementation(meta.declaration_of(Marker), target, [])]
}
@[expand(derive_marker)]
Subject :: type {}
""";

        var result = Compile(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Message.Contains("implementation", StringComparison.Ordinal));
    }

    [Fact]
    public void Typed_quote_values_cover_every_public_grammar_category()
    {
        const string source = """
ItemValue :: comptime quote item { generated :: Unit -> Int { _ => 1 } };
ItemsValue :: comptime quote items {
    first :: Unit -> Int { _ => 1 }
    second :: Unit -> Int { _ => 2 }
};
MemberValue :: comptime quote member { value :: Int };
MembersValue :: comptime quote members { first :: Int, second :: Bool };
StatementValue :: comptime quote stmt { 42; };
ExpressionValue :: comptime quote expr { 40 + 2 };
PatternValue :: comptime quote pattern { _ };
TypeValue :: comptime quote type { Seq[Int] };
TokensValue :: comptime quote tokens { if ??? { value } };
""";

        var result = Compile(source, CompilationPhase.Types);

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void Typed_derive_materializes_quoted_items_with_stable_origin_and_virtual_document()
    {
        const string source = """
derive_answer :: comptime meta.Type -> meta.Items {
    _ => quote items {
        generated_answer :: Unit -> Int { _ => 42 }
    }
}

@[expand(derive_answer)]
Subject :: type {}
""";

        var result = Compile(source, CompilationPhase.Types);

        Assert.True(result.Success, FormatDiagnostics(result));
        var generated = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<FuncDef>(),
            static function => function.Name == "generated_answer");
        var origin = Assert.Single(generated.GeneratedOriginChain);
        Assert.NotEmpty(origin.StableIdentity);
        Assert.NotEmpty(origin.GenerationSlotIdentity);
        Assert.StartsWith("eidos-generated://", origin.VirtualDocumentPath, StringComparison.Ordinal);
        Assert.Equal(0, origin.ExpansionOutputIndex);
        Assert.Equal(6, origin.MetaSchemaVersion);

        var document = Assert.Single(
            IdeSemanticSnapshotBuilder.Build(result).GeneratedDocuments,
            entry => entry.Uri == origin.VirtualDocumentPath);
        Assert.Equal(origin.StableIdentity, document.StableIdentity);
        Assert.Contains("generated_answer", document.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Typed_derive_quote_splices_preserve_fragment_order_and_declaration_identity()
    {
        const string source = """
helper :: Unit -> Int { _ => 39 }
HelperDecl :: comptime meta.declaration_of(helper);
First :: comptime quote item {
    first :: Unit -> Int { _ => 1 }
};
Rest :: comptime quote items {
    second :: Unit -> Int { _ => 2 }
    answer :: Unit -> Int { _ => $(HelperDecl)(()) + 3 }
};

derive_all :: comptime meta.Type -> meta.Items {
    _ => quote items {
        $(First)
        ..$(Rest)
    }
}

@[expand(derive_all)]
Subject :: type {}
""";

        var result = Compile(source, CompilationPhase.Types);

        Assert.True(result.Success, FormatDiagnostics(result));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var generated = module.Declarations
            .OfType<FuncDef>()
            .Where(static function => function.Name is "first" or "second" or "answer")
            .ToArray();
        Assert.Equal(["first", "second", "answer"], generated.Select(static function => function.Name));
        Assert.Equal([0, 1, 2], generated.Select(static function => Assert.Single(function.GeneratedOriginChain).ExpansionOutputIndex));

        var helper = Assert.Single(module.Declarations.OfType<FuncDef>(), static function => function.Name == "helper");
        var answer = Assert.Single(generated, static function => function.Name == "answer");
        var binary = Assert.IsType<BinaryExpr>(Assert.Single(answer.Body).Expression);
        var call = Assert.IsType<CallExpr>(binary.Left);
        var reference = Assert.IsType<IdentifierExpr>(call.Function);
        Assert.Equal(helper.SymbolId, reference.SymbolId);
        Assert.Equal(SyntaxIdentityKind.Declaration, reference.AttachedSyntaxIdentity?.Kind);
    }

    [Fact]
    public void Typed_derive_generated_identity_is_deterministic_and_includes_explicit_arguments()
    {
        const string sourceTemplate = """
generate_answer :: comptime Int -> meta.Type -> meta.Items {
    answer => _ => [meta.function("generated_answer", [], Int, meta.expr_int(answer))]
}

@[expand(generate_answer(ARGUMENT))]
Subject :: type {}
""";

        var first = Compile(sourceTemplate.Replace("ARGUMENT", "1", StringComparison.Ordinal), CompilationPhase.Types);
        var repeated = Compile(sourceTemplate.Replace("ARGUMENT", "1", StringComparison.Ordinal), CompilationPhase.Types);
        var changed = Compile(sourceTemplate.Replace("ARGUMENT", "2", StringComparison.Ordinal), CompilationPhase.Types);

        Assert.True(first.Success, FormatDiagnostics(first));
        Assert.True(repeated.Success, FormatDiagnostics(repeated));
        Assert.True(changed.Success, FormatDiagnostics(changed));
        var firstOrigin = GetGeneratedFunctionOrigin(first, "generated_answer");
        var repeatedOrigin = GetGeneratedFunctionOrigin(repeated, "generated_answer");
        var changedOrigin = GetGeneratedFunctionOrigin(changed, "generated_answer");
        Assert.Equal(firstOrigin.CanonicalArgumentsHash, repeatedOrigin.CanonicalArgumentsHash);
        Assert.Equal(firstOrigin.GenerationSlotIdentity, repeatedOrigin.GenerationSlotIdentity);
        Assert.Equal(firstOrigin.StableIdentity, repeatedOrigin.StableIdentity);
        Assert.NotEqual(firstOrigin.CanonicalArgumentsHash, changedOrigin.CanonicalArgumentsHash);
        Assert.Equal(firstOrigin.GenerationSlotIdentity, changedOrigin.GenerationSlotIdentity);
        Assert.NotEqual(firstOrigin.StableIdentity, changedOrigin.StableIdentity);
    }

    [Fact]
    public void Typed_derive_generated_origin_round_trips_through_symbol_cache_payload()
    {
        const string source = """
derive_answer :: comptime meta.Type -> meta.Items {
    _ => [meta.function("generated_answer", [], Int, meta.expr_int(42))]
}

@[expand(derive_answer)]
Subject :: type {}
""";

        var result = Compile(source, CompilationPhase.Types);

        Assert.True(result.Success, FormatDiagnostics(result));
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var generated = Assert.Single(
            symbolTable.Symbols.Values.OfType<FuncSymbol>(),
            static symbol => symbol.Name == "generated_answer" && symbol.GeneratedOrigin != null);
        var payload = SymbolPayload.Create(generated);
        var restored = JsonSerializer.Deserialize<SymbolPayload>(JsonSerializer.Serialize(payload));
        var originalOrigin = Assert.IsType<GeneratedDeclarationOrigin>(generated.GeneratedOrigin);
        var restoredOrigin = Assert.IsType<GeneratedDeclarationOriginPayload>(restored?.GeneratedOrigin);
        Assert.Equal(originalOrigin.StableIdentity, restoredOrigin.StableIdentity);
        Assert.Equal(originalOrigin.GenerationSlotIdentity, restoredOrigin.GenerationSlotIdentity);
        Assert.Equal(originalOrigin.CanonicalArgumentsHash, restoredOrigin.CanonicalArgumentsHash);
        Assert.Equal(originalOrigin.VirtualDocumentPath, restoredOrigin.VirtualDocumentPath);
        Assert.Equal(originalOrigin.MetaSchemaVersion, restoredOrigin.MetaSchemaVersion);
    }

    [Fact]
    public void Typed_quote_rejects_non_syntax_splice_in_item_slot()
    {
        const string source = """
Bad :: comptime quote item { $(42) };
""";

        var result = Compile(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Message.Contains("quote item", StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Message.Contains("item syntax", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Typed_derive_quote_keeps_local_bindings_hygienic()
    {
        const string source = """
derive_answer :: comptime meta.Type -> meta.Items {
    _ => quote items {
        generated_answer :: Unit -> Int {
            _ => {
                local := 41;
                local + 1
            }
        }
    }
}

@[expand(derive_answer)]
Subject :: type {}
""";

        var result = Compile(source, CompilationPhase.Types);

        Assert.True(result.Success, FormatDiagnostics(result));
        var answer = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<FuncDef>(),
            static function => function.Name == "generated_answer");
        var block = Assert.IsType<BlockExpr>(Assert.Single(answer.Body).Expression);
        var local = Assert.IsType<LetDecl>(block.Statements[0]);
        var binding = Assert.IsType<VarPattern>(local.Pattern);
        var binary = Assert.IsType<BinaryExpr>(block.Statements[1]);
        var reference = Assert.IsType<IdentifierExpr>(binary.Left);
        Assert.Equal(binding.SymbolId, reference.SymbolId);
        Assert.Equal(binding.AttachedSyntaxIdentity?.StableIdentity, reference.AttachedSyntaxIdentity?.StableIdentity);
        Assert.Equal(SyntaxIdentityKind.Hygiene, reference.AttachedSyntaxIdentity?.Kind);
    }

    [Fact]
    public void Typed_derive_rejects_duplicate_public_generated_identity_atomically()
    {
        const string source = """
derive_duplicate :: comptime meta.Type -> meta.Items {
    _ => [
        meta.function("answer", [], Int, meta.expr_int(1)),
        meta.function("answer", [], Int, meta.expr_int(2))
    ]
}

@[expand(derive_duplicate)]
Subject :: type {}
""";

        var result = Compile(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3616" &&
            diagnostic.Message.Contains("answer", StringComparison.Ordinal));
        Assert.DoesNotContain(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<FuncDef>(),
            static function => function.Name == "answer");
    }

    [Fact]
    public void Typed_body_expand_tag_uses_meta_function_protocol()
    {
        const string source = """
identity_body :: comptime meta.Function -> meta.Function { value => value }

@[expand(identity_body)]
work :: Int -> Int {
    value => value
}
""";

        var result = Compile(source, CompilationPhase.Types);

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void Syntax_expand_site_uses_category_preserving_protocol()
    {
        const string source = """
identity_expr :: comptime meta.Syntax[meta.Expr] -> meta.Syntax[meta.Expr] { _ => quote expr { 1 } }

main :: Unit -> Int {
    _ => expand identity_expr()
}
""";

        var result = Compile(source, CompilationPhase.Types);

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void Syntax_expand_materializes_every_contextual_site_category()
    {
        var cases = new (string Name, string Source)[]
        {
            ("item", """
make_item :: comptime meta.Syntax[meta.Item] -> meta.Syntax[meta.Item] {
    _ => quote item { generated :: Unit -> Int { _ => 42 } }
}
expand make_item();
"""),
            ("member", """
make_member :: comptime meta.Syntax[meta.Member] -> meta.Syntax[meta.Member] {
    _ => quote member { generated :: Int }
}
Subject :: type { expand make_member(); }
"""),
            ("statement", """
make_statement :: comptime meta.Syntax[meta.Stmt] -> meta.Syntax[meta.Stmt] {
    _ => quote stmt { 20; }
}
answer :: Int = { expand make_statement(); 22 };
"""),
            ("expression", """
make_expression :: comptime meta.Syntax[meta.Expr] -> meta.Syntax[meta.Expr] {
    _ => quote expr { 42 }
}
answer :: Int = expand make_expression();
"""),
            ("pattern", """
make_pattern :: comptime meta.Syntax[meta.Pattern] -> meta.Syntax[meta.Pattern] {
    _ => quote pattern { _ }
}
is_zero :: Int -> Bool { expand make_pattern() => false }
"""),
            ("type", """
make_type :: comptime meta.Syntax[meta.TypeSyntax] -> meta.Syntax[meta.TypeSyntax] {
    _ => quote type { Int }
}
answer :: expand make_type() = 42;
""")
        };

        foreach (var (name, source) in cases)
        {
            var result = Compile(source, CompilationPhase.Types);
            Assert.True(result.Success, $"{name}: {FormatDiagnostics(result)}");
        }
    }

    [Fact]
    public void Syntax_expand_rejects_cross_category_protocol_without_partial_output()
    {
        const string source = """
wrong_category :: comptime meta.Syntax[meta.Expr] -> meta.Syntax[meta.Pattern] {
    _ => quote pattern { _ }
}
answer :: Int = expand wrong_category();
""";

        var result = Compile(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3620" &&
            diagnostic.Message.Contains("wrong_category", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("meta.Syntax", StringComparison.Ordinal));
    }

    [Fact]
    public void Body_expand_emits_origin_and_virtual_document_for_revalidated_body()
    {
        const string source = """
replace_body :: comptime meta.Function -> meta.Function {
    function => function.with_body(quote expr { 42 })
}

@[expand(replace_body)]
work[T] :: Ref[T] -> Int {
    _ => 1
}
""";

        var result = Compile(source, CompilationPhase.Types);

        Assert.True(result.Success, FormatDiagnostics(result));
        var work = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<FuncDef>(),
            static function => function.Name == "work");
        Assert.Single(work.TypeParams);
        var body = Assert.IsType<LiteralExpr>(Assert.Single(work.Body).Expression);
        var origin = Assert.Single(body.GeneratedOriginChain);
        Assert.StartsWith("eidos-generated://", origin.VirtualDocumentPath, StringComparison.Ordinal);
        Assert.Contains(
            IdeSemanticSnapshotBuilder.Build(result).GeneratedDocuments,
            document => document.Uri == origin.VirtualDocumentPath &&
                        document.Content.Contains("42", StringComparison.Ordinal));
    }

    [Fact]
    public void Meta_scheduler_reaches_a_fixed_point_for_nested_typed_syntax_sites()
    {
        const string source = """
make_inner_statement :: comptime meta.Syntax[meta.Stmt] -> meta.Syntax[meta.Stmt] {
    _ => quote stmt { 20; }
}
make_outer_statement :: comptime meta.Syntax[meta.Stmt] -> meta.Syntax[meta.Stmt] {
    _ => quote stmt { expand make_inner_statement(); }
}
make_inner_item :: comptime meta.Syntax[meta.Item] -> meta.Syntax[meta.Item] {
    _ => quote item { generated :: Unit -> Int { _ => 22 } }
}
make_outer_item :: comptime meta.Syntax[meta.Item] -> meta.Syntax[meta.Item] {
    _ => quote item { expand make_inner_item(); }
}

expand make_outer_item();
answer :: Int = {
    expand make_outer_statement();
    42
};
""";

        var result = Compile(source, CompilationPhase.Types);

        Assert.True(result.Success, FormatDiagnostics(result));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var generated = Assert.Single(
            module.Declarations.OfType<FuncDef>(),
            static function => function.Name == "generated");
        Assert.Equal(2, generated.GeneratedOriginChain.Count);
        var answer = Assert.Single(
            module.Declarations.OfType<LetDecl>(),
            static declaration => declaration.Pattern is VarPattern { Name: "answer" });
        var block = Assert.IsType<BlockExpr>(answer.Value);
        var generatedStatement = Assert.IsType<LiteralExpr>(block.Statements[0]);
        Assert.Equal("20", generatedStatement.RawText);
        Assert.Equal(2, generatedStatement.GeneratedOriginChain.Count);
        Assert.DoesNotContain(module.Declarations, static declaration => declaration is ExpandDeclaration);
        Assert.DoesNotContain(block.Statements, static statement => statement is ExpandStmt);
    }

    [Fact]
    public void Meta_scheduler_reports_non_convergent_nested_generation_deterministically()
    {
        const string source = """
repeat_statement :: comptime meta.Syntax[meta.Stmt] -> meta.Syntax[meta.Stmt] {
    _ => quote stmt { expand repeat_statement(); }
}

answer :: Int = {
    expand repeat_statement();
    42
};
""";

        var first = Compile(source, CompilationPhase.Types);
        var repeated = Compile(source, CompilationPhase.Types);

        Assert.False(first.Success);
        Assert.False(repeated.Success);
        var firstDiagnostic = Assert.Single(first.Diagnostics, static diagnostic =>
            diagnostic.Message.Contains("meta expansion did not converge", StringComparison.Ordinal));
        var repeatedDiagnostic = Assert.Single(repeated.Diagnostics, static diagnostic =>
            diagnostic.Message.Contains("meta expansion did not converge", StringComparison.Ordinal));
        Assert.Equal(firstDiagnostic.Code, repeatedDiagnostic.Code);
        Assert.Equal(firstDiagnostic.Message, repeatedDiagnostic.Message);
        Assert.Contains("repeat_statement", firstDiagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("answer", firstDiagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Meta_query_cache_records_hits_and_round_trips_canonical_dependencies()
    {
        const string source = """
work :: Int -> Int { value => value }
WorkDecl :: comptime meta.declaration_of(work);
FirstName :: comptime meta.name_of(WorkDecl);
SecondName :: comptime meta.name_of(WorkDecl);
""";

        var result = Compile(source, static options =>
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

        var modulePayload = Assert.Single(
            Assert.IsAssignableFrom<IReadOnlyList<ModuleTypesStatePayload>>(result.ModuleTypesStatePayloads)
                .Select(static payload => payload.MetaQueries)
                .Where(static payload => payload.Dependencies.Any(dependency => dependency.CacheHit))
                .DistinctBy(static payload => payload.Hash));
        Assert.True(modulePayload.HasValidHash());
        Assert.NotEmpty(modulePayload.CacheEntries);
        Assert.True(modulePayload.TryRestoreState(
            null,
            out var restoredEntries,
            out var restoredDependencies,
            out var failure), failure);
        var restoredState = new MetaQueryState();
        Assert.True(restoredState.TryRestoreState(restoredEntries, restoredDependencies, out failure), failure);
        Assert.Equal(modulePayload.CacheEntries.Count, restoredState.SnapshotCacheEntries().Count);
        Assert.Contains(restoredState.SnapshotDependencies(), static dependency => dependency.CacheHit);

        var livePayload = Assert.IsType<CompilationLiveStatePayload>(result.CompilationLiveStatePayload);
        Assert.True(livePayload.MetaQueries.HasValidHash());
        Assert.Equal(
            modulePayload.CacheEntries
                .Select(static entry => $"{entry.Key}:{entry.ResultHash}:{entry.ResultBytes}")
                .Order(StringComparer.Ordinal),
            livePayload.MetaQueries.CacheEntries
                .Select(static entry => $"{entry.Key}:{entry.ResultHash}:{entry.ResultBytes}")
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Package_extension_emits_typed_items_without_query_or_transformation_values()
    {
        const string source = """
extend_root :: comptime meta.Package -> meta.Items {
    _ => [meta.function("generated_answer", [], Int, meta.expr_int(42))]
}

main :: Unit -> Int { _ => 0 }
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "meta-package-extension.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = false,
            UseColors = false,
            MetaConfiguration = new EidosMetaConfiguration
            {
                Extensions =
                [
                    new EidosMetaExtensionConfiguration
                    {
                        Name = "typed-items",
                        Entry = "extend_root"
                    }
                ]
            }
        }).Run();

        Assert.True(result.Success, FormatDiagnostics(result));
        var generated = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<FuncDef>(),
            function => function.Name == "generated_answer");
        Assert.NotEmpty(generated.GeneratedOriginChain);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic =>
            diagnostic.Message.Contains("Query", StringComparison.Ordinal) ||
            diagnostic.Message.Contains("Transformation", StringComparison.Ordinal));
    }

    [Fact]
    public void Package_extension_reads_only_declared_resources_and_invalidates_generated_identity()
    {
        const string source = """
generate_resource :: comptime meta.Package -> meta.Items {
    package => {
        resources := meta.resources_of(package);
        resource := resources[0];
        [meta.function(meta.resource_content_of(resource), [], Int, meta.expr_int(1))]
    }
}

main :: Unit -> Int { _ => 0 }
""";

        static EidosMetaConfiguration Configuration(string content, string contentHash, bool allowResourceRead) => new()
        {
            Extensions =
            [
                new EidosMetaExtensionConfiguration
                {
                    Name = "resource",
                    Entry = "generate_resource",
                    Capabilities = allowResourceRead ? ["read-declared-resources", "emit-items"] : ["emit-items"],
                    Resources =
                    [
                        new EidosMetaResourceConfiguration
                        {
                            DeclaredInput = "schema/name.txt",
                            RelativePath = "schema/name.txt",
                            Content = content,
                            ContentHash = contentHash,
                            Exists = true
                        }
                    ]
                }
            ]
        };

        var first = Compile(source, options =>
            options.MetaConfiguration = Configuration("first_generated", "hash-first", allowResourceRead: true));
        var changed = Compile(source, options =>
            options.MetaConfiguration = Configuration("second_generated", "hash-second", allowResourceRead: true));
        var denied = Compile(source, options =>
            options.MetaConfiguration = Configuration("forbidden_generated", "hash-denied", allowResourceRead: false));

        Assert.True(first.Success, FormatDiagnostics(first));
        Assert.True(changed.Success, FormatDiagnostics(changed));
        Assert.Contains(
            Assert.IsType<ModuleDecl>(first.Ast).Declarations.OfType<FuncDef>(),
            static function => function.Name == "first_generated");
        Assert.Contains(
            Assert.IsType<ModuleDecl>(changed.Ast).Declarations.OfType<FuncDef>(),
            static function => function.Name == "second_generated");
        Assert.NotEqual(
            GetGeneratedFunctionOrigin(first, "first_generated").StableIdentity,
            GetGeneratedFunctionOrigin(changed, "second_generated").StableIdentity);
        Assert.Contains(denied.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3631" &&
            diagnostic.Message.Contains("read-declared-resources", StringComparison.Ordinal));
        Assert.DoesNotContain(
            Assert.IsType<ModuleDecl>(denied.Ast).Declarations.OfType<FuncDef>(),
            static function => function.Name == "forbidden_generated");
    }

    [Fact]
    public void Package_analyzer_emits_structured_diagnostic_with_fix()
    {
        const string source = """
check_package :: comptime meta.Package -> Seq[meta.Diagnostic] {
    _ => [meta.diagnostic_with_fix(
        "warning",
        meta.span_of(meta.declaration_of(Subject)),
        "package analyzer finding",
        meta.fix(meta.span_of(meta.declaration_of(Subject)), "main"))]
}

Subject :: type {}
main :: Unit -> Int { _ => 0 }
""";

        var result = Compile(source, options =>
        {
            options.MetaConfiguration = new EidosMetaConfiguration
            {
                Checks = ["check_package"]
            };
        });

        Assert.True(result.Success, FormatDiagnostics(result));
        var diagnostic = Assert.Single(result.Diagnostics, static candidate => candidate.Code == "W3632");
        Assert.Equal("package analyzer finding", diagnostic.Message);
        var fix = Assert.Single(diagnostic.Suggestions);
        Assert.Equal("main", fix.Replacement);
        Assert.True(fix.Span.HasValue);
        Assert.EndsWith("meta-compiler-managed.eidos", fix.Span.Value.FilePath, StringComparison.Ordinal);
    }

    [Fact]
    public void Package_analyzer_rejects_emit_protocol_without_mutating_package()
    {
        const string source = """
mutate_package :: comptime meta.Package -> meta.Items {
    _ => [meta.function("forbidden", [], Int, meta.expr_int(1))]
}

main :: Unit -> Int { _ => 0 }
""";

        var result = Compile(source, options =>
        {
            options.MetaConfiguration = new EidosMetaConfiguration
            {
                Checks = ["mutate_package"]
            };
        });

        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3630" &&
            diagnostic.Message.Contains("compiler-managed package protocol", StringComparison.Ordinal));
        Assert.DoesNotContain(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<FuncDef>(),
            static function => function.Name == "forbidden");
    }

    [Fact]
    public void Package_extension_emits_typed_generated_module_with_high_level_constructor()
    {
        const string source = """
generate_schema :: comptime meta.Package -> meta.Modules {
    _ => [meta.module("Generated.Schema", quote items {
        answer :: Unit -> Int { _ => 42 }
    })]
}

main :: Unit -> Int { _ => 0 }
""";

        var result = Compile(source, options =>
        {
            options.MetaConfiguration = new EidosMetaConfiguration
            {
                Extensions =
                [
                    new EidosMetaExtensionConfiguration
                    {
                        Name = "schema",
                        Entry = "generate_schema"
                    }
                ]
            };
        });

        Assert.True(result.Success, FormatDiagnostics(result));
        var generated = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<ModuleDecl>(),
            static module => module.Path.SequenceEqual(["Generated", "Schema"]));
        Assert.NotEmpty(generated.GeneratedOriginChain);
        Assert.Single(generated.Declarations.OfType<FuncDef>(), static function => function.Name == "answer");
        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        Assert.Contains(
            snapshot.GeneratedDocuments,
            document => document.TargetIdentity.Contains("module", StringComparison.OrdinalIgnoreCase) ||
                        document.Content.Contains("answer", StringComparison.Ordinal));
    }

    [Fact]
    public void Typed_tags_are_the_only_declaration_attachment_form_for_generators()
    {
        const string source = """
@[repr(c), derive(Copy), expand(trace)]
Subject :: type {}
""";

        var result = Compile(source, CompilationPhase.Parser);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Level == global::Eidosc.Diagnostic.DiagnosticLevel.Error);
        var subject = Assert.IsType<AdtDef>(Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<AdtDef>(),
            declaration => declaration.Name == "Subject"));
        Assert.Equal(
            [DeclarationClauseKind.Repr, DeclarationClauseKind.Derive, DeclarationClauseKind.Expand],
            subject.Clauses.Select(static clause => clause.ClauseKind));
    }

    private static CompilationResult Compile(string source, CompilationPhase stopAtPhase) =>
        new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "meta-compiler-managed.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = stopAtPhase,
            NoImplicitPrelude = false,
            UseColors = false
        }).Run();

    private static CompilationResult Compile(string source, Action<CompilationOptions> configure)
    {
        var options = new CompilationOptions
        {
            InputFile = "meta-compiler-managed.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = false,
            UseColors = false
        };
        configure(options);
        return new CompilationPipeline(source, options).Run();
    }

    private static void AssertProtocol(
        ModuleDecl module,
        SymbolTable symbolTable,
        string name,
        CompilerMetaProtocolKind expected)
    {
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), declaration => declaration.Name == name);
        Assert.True(
            CompilerMetaProtocolRegistry.TryClassify(function, 0, symbolTable, out var protocol, out var reason),
            reason);
        Assert.Equal(expected, protocol.Kind);
    }

    private static GeneratedDeclarationOrigin GetGeneratedFunctionOrigin(CompilationResult result, string name)
    {
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var function = Assert.Single(
            symbolTable.Symbols.Values.OfType<FuncSymbol>(),
            symbol => symbol.Name == name && symbol.GeneratedOrigin != null);
        return Assert.IsType<GeneratedDeclarationOrigin>(function.GeneratedOrigin);
    }

    private static string FormatDiagnostics(CompilationResult result) =>
        string.Join("; ", result.Diagnostics.Select(static diagnostic => diagnostic.Message));
}
