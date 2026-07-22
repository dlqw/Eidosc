using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ide;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Semantic;
using Eidosc.Symbols;
using Eidosc.Syntax;
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

    private static string FormatDiagnostics(CompilationResult result) =>
        string.Join("; ", result.Diagnostics.Select(static diagnostic => diagnostic.Message));
}
