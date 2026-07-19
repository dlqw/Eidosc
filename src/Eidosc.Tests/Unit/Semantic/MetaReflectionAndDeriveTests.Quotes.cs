using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Pipeline;
using Eidosc.Symbols;
using Eidosc.Syntax;
using Eidosc.Types;

namespace Eidosc.Tests.Unit.Semantic;

public sealed partial class MetaReflectionAndDeriveTests
{
    [Fact]
    public void Quote_evaluates_to_canonical_typed_lossless_syntax()
    {
        const string source = """
Quoted :: comptime quote expr {
    /* retained */ 1 + $(2)
};
""";

        var result = Compile("meta_quote_expr.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(result.TypeInferer);
        var syntax = Assert.IsType<ComptimeSyntaxValue>(GetComptimeValue("Quoted", symbolTable, inferer));
        Assert.Equal(SyntaxCategory.Expression, syntax.Category);
        Assert.Contains("/* retained */", syntax.Render(), StringComparison.Ordinal);
        Assert.Contains("1 + 2", syntax.Render(), StringComparison.Ordinal);
        Assert.Contains(syntax.Tokens, static token => token.Kind == SyntaxKind.Comment);
        Assert.NotEmpty(syntax.HygieneIdentity);
        Assert.Equal(syntax.CanonicalHash, (syntax with { Tokens = [.. syntax.Tokens] }).CanonicalHash);
        var staticType = Assert.IsType<TyCon>(syntax.StaticType);
        Assert.Equal(WellKnownTypeIds.MetaSyntaxId, staticType.Id.Value);
        Assert.Equal(WellKnownTypeIds.MetaExprId, Assert.IsType<TyCon>(Assert.Single(staticType.Args)).Id.Value);
        Assert.True(ComptimeValuePayload.TryCreate(syntax, out var payload));
        Assert.True(payload.TryRestoreValue(remapper: null, out var restored));
        var restoredSyntax = Assert.IsType<ComptimeSyntaxValue>(restored);
        Assert.Equal(syntax.CanonicalText, restoredSyntax.CanonicalText);
        Assert.Equal(syntax.TrailingTrivia, restoredSyntax.TrailingTrivia);
        Assert.Equal(syntax.Origin, restoredSyntax.Origin);
        Assert.Equal(
            syntax.Tokens.Select(static token => token.Identity?.CanonicalText),
            restoredSyntax.Tokens.Select(static token => token.Identity?.CanonicalText));
    }

    [Fact]
    public void Quote_kind_is_inferred_from_expected_meta_syntax_type()
    {
        const string source = """
make :: comptime Unit -> meta.Syntax[meta.Expr] {
    _ => quote { 40 + 2 }
}

Quoted :: comptime make(());
""";

        var result = Compile("meta_quote_inferred.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var syntax = Assert.IsType<ComptimeSyntaxValue>(GetComptimeValue(
            "Quoted",
            Assert.IsType<SymbolTable>(result.SymbolTable),
            Assert.IsType<TypeInferer>(result.TypeInferer)));
        Assert.Equal(SyntaxCategory.Expression, syntax.Category);
        Assert.Contains("40 + 2", syntax.Render(), StringComparison.Ordinal);
    }

    [Fact]
    public void Quote_item_materializes_through_transformation_without_text_builder_fallback()
    {
        const string source = """
derive_answer :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => {
        name := meta.identifier("answer", meta.IdentifierCategory.Function);
        meta.add_after(input, [quote item {
            $(name) :: Unit -> Int {
                _ => 42
            }
        }])
    }
}

Subject :: type expand derive_answer {
    value :: Int
}

read :: Unit -> Int {
    _ => answer(())
}
""";

        var result = Compile("meta_quote_item_transform.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.Contains(
            Assert.IsType<SymbolTable>(result.SymbolTable).Symbols.Values.OfType<FuncSymbol>(),
            static symbol => symbol.Name == "answer" && symbol.GeneratedOrigin != null);
        var answer = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<FuncDef>(),
            static function => function.Name == "answer");
        Assert.Equal(SyntaxIdentityKind.Identifier, answer.AttachedSyntaxIdentity?.Kind);
        Assert.Equal("Function", answer.AttachedSyntaxIdentity?.Category);
    }

    [Fact]
    public void Quote_member_adds_a_field_to_its_authorized_type_owner()
    {
        const string source = """
add_field :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => {
        name := meta.identifier("generated", meta.IdentifierCategory.Field);
        meta.add_members(input, [quote member {
            $(name) :: Int
        }])
    }
}

Subject :: type expand add_field {
    value :: Int
}
""";

        var result = Compile("meta_quote_field_member.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var subject = Assert.Single(module.Declarations.OfType<AdtDef>(), static type =>
            type.Name == "Subject");
        Assert.DoesNotContain(module.Declarations.Cast<EidosAstNode>(), static node => node is Field);
        var generated = Assert.Single(subject.Fields, static field => field.Name == "generated");
        Assert.Equal(SyntaxIdentityKind.Identifier, generated.AttachedSyntaxIdentity?.Kind);
        Assert.Equal("Field", generated.AttachedSyntaxIdentity?.Category);

        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        var symbol = Assert.IsType<FieldSymbol>(table.GetSymbol(generated.SymbolId));
        Assert.Equal(subject.SymbolId, symbol.OwnerType);
        Assert.NotNull(symbol.GeneratedOrigin);
    }

    [Fact]
    public void Quote_member_field_collision_is_rejected_without_mutating_the_type()
    {
        const string source = """
add_field :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => {
        name := meta.identifier("generated", meta.IdentifierCategory.Field);
        meta.add_members(input, [quote member { $(name) :: Int }])
    }
}

Subject :: type expand add_field {
    generated :: Int
}
""";

        var result = Compile("meta_quote_field_collision.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            (diagnostic.Code is "E3601" or "E3616") &&
            (diagnostic.Message.Contains("collides", StringComparison.OrdinalIgnoreCase) ||
             diagnostic.Message.Contains("already contains", StringComparison.OrdinalIgnoreCase)));
        var subject = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<AdtDef>(),
            static type => type.Name == "Subject");
        Assert.Single(subject.Fields, static field => field.Name == "generated");
    }

    [Fact]
    public void Quote_member_adds_a_trait_method_with_trait_symbol_ownership()
    {
        const string source = """
add_method :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => {
        name := meta.identifier("generated", meta.IdentifierCategory.Function);
        meta.add_members(input, [quote member {
            $(name) :: Unit -> Int { _ => 42 }
        }])
    }
}

Subject :: trait expand add_method {
    existing :: Unit -> Int
}
""";

        var result = Compile("meta_quote_trait_method.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var subject = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<TraitDef>(),
            static trait => trait.Name == "Subject");
        var method = Assert.Single(subject.Methods, static method => method.Name == "generated");
        var symbol = Assert.IsType<FuncSymbol>(
            Assert.IsType<SymbolTable>(result.SymbolTable).GetSymbol(method.SymbolId));
        Assert.Equal(subject.SymbolId, symbol.OwnerTrait);
        Assert.NotNull(symbol.GeneratedOrigin);
    }

    [Fact]
    public void Quote_members_add_trait_associated_items_with_owned_symbols()
    {
        const string source = """
add_items :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => {
        item_name := meta.identifier("Item", meta.IdentifierCategory.AssociatedType);
        max_name := meta.identifier("MAX", meta.IdentifierCategory.AssociatedConst);
        meta.add_members(input, [
            quote member { $(item_name) :: type },
            quote member { $(max_name) :: Int }
        ])
    }
}

Subject :: trait expand add_items {}
""";

        var result = Compile("meta_quote_trait_associated_members.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var trait = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<TraitDef>(),
            static declaration => declaration.Name == "Subject");
        var associatedType = Assert.Single(trait.AssociatedTypes);
        var associatedConst = Assert.Single(trait.AssociatedConsts);
        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        var traitSymbol = Assert.IsType<TraitSymbol>(table.GetSymbol(trait.SymbolId));
        var typeSymbol = Assert.IsType<AssociatedTypeSymbol>(table.GetSymbol(associatedType.SymbolId));
        var constSymbol = Assert.IsType<AssociatedConstSymbol>(table.GetSymbol(associatedConst.SymbolId));
        Assert.Equal(trait.SymbolId, typeSymbol.OwnerTrait);
        Assert.Equal(trait.SymbolId, constSymbol.OwnerTrait);
        Assert.Contains(typeSymbol.Id, traitSymbol.AssociatedTypes);
        Assert.Contains(constSymbol.Id, traitSymbol.AssociatedConsts);
        Assert.NotNull(typeSymbol.GeneratedOrigin);
        Assert.NotNull(constSymbol.GeneratedOrigin);
    }

    [Fact]
    public void Quote_member_adds_a_closed_case_and_constructor_projection()
    {
        const string source = """
add_case :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => {
        name := meta.identifier("Generated", meta.IdentifierCategory.Type);
        meta.add_members(input, [quote member {
            $(name) :: type { payload :: Int }
        }])
    }
}

Subject :: type expand add_case {
    common :: Int
}
""";

        var result = Compile("meta_quote_case_member.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var subject = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<AdtDef>(),
            static type => type.Name == "Subject");
        var generatedCase = Assert.Single(subject.Cases, static caseType =>
            caseType.Name == "Generated");
        var constructor = Assert.Single(subject.Constructors, static constructor =>
            constructor.Name == "Generated");
        Assert.Equal(generatedCase.ConstructorSymbolId, constructor.SymbolId);
        Assert.Collection(
            constructor.NamedArgs,
            static field => Assert.Equal("common", field.Name),
            static field => Assert.Equal("payload", field.Name));

        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        var caseSymbol = Assert.IsType<AdtSymbol>(table.GetSymbol(generatedCase.SymbolId));
        Assert.Equal(subject.SymbolId, caseSymbol.ParentAdt);
        Assert.NotNull(caseSymbol.GeneratedOrigin);
        Assert.NotNull(Assert.IsType<CtorSymbol>(table.GetSymbol(constructor.SymbolId)).GeneratedOrigin);
    }

    [Fact]
    public void Quote_member_rejects_a_closed_case_after_the_syntax_stage_without_mutation()
    {
        const string source = """
add_case_too_late :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => meta.add_members(input, [quote member {
        Generated :: type { payload :: Int }
    }])
}

Subject :: type expand add_case_too_late {
    common :: Int
}
""";

        var result = Compile("meta_quote_case_member_too_late.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3616" &&
            diagnostic.Message.Contains("closed case", StringComparison.OrdinalIgnoreCase) &&
            diagnostic.Message.Contains("Syntax", StringComparison.Ordinal));
        var subject = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<AdtDef>(),
            static type => type.Name == "Subject");
        Assert.Empty(subject.Cases);
        Assert.DoesNotContain(
            Assert.IsType<SymbolTable>(result.SymbolTable).Symbols.Values,
            static symbol => symbol.Name == "Generated" && symbol.GeneratedOrigin != null);
    }

    [Fact]
    public void Quote_member_can_turn_a_leaf_case_into_a_nested_closed_sum()
    {
        const string source = """
add_nested_case :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => {
        name := meta.identifier("Generated", meta.IdentifierCategory.Type);
        meta.add_members(input, [quote member {
            $(name) :: type { payload :: Int }
        }])
    }
}

Subject :: type {
    common :: Int,
    Branch :: type expand add_nested_case {
        branch_value :: Int
    }
}
""";

        var result = Compile("meta_quote_nested_case_member.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var subject = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<AdtDef>(),
            static type => type.Name == "Subject");
        var branch = Assert.Single(subject.Cases, static caseType => caseType.Name == "Branch");
        Assert.False(branch.ConstructorSymbolId.IsValid);
        var generated = Assert.Single(branch.Cases, static caseType => caseType.Name == "Generated");
        var constructor = Assert.Single(subject.Constructors);
        Assert.Equal("Generated", constructor.Name);
        Assert.Equal(generated.ConstructorSymbolId, constructor.SymbolId);
        Assert.Collection(
            constructor.NamedArgs,
            static field => Assert.Equal("common", field.Name),
            static field => Assert.Equal("branch_value", field.Name),
            static field => Assert.Equal("payload", field.Name));
    }

    [Fact]
    public void Quote_member_adds_a_declaration_inside_its_module_target()
    {
        const string source = """
Child :: module expand add_module_item {
    add_module_item :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
        input => {
            name := meta.identifier("generated", meta.IdentifierCategory.Function);
            meta.add_members(input, [quote member {
                $(name) :: Unit -> Int { _ => 42 }
            }])
        }
    }
}
""";

        var result = Compile("meta_quote_module_member.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var child = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<ModuleDecl>(),
            static module => module.Path.SequenceEqual(["Child"]));
        var generated = Assert.Single(child.Declarations.OfType<FuncDef>(), static function =>
            function.Name == "generated");
        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        Assert.True(table.Modules.TryGetOwningModuleId(generated.SymbolId, out var owner));
        Assert.Equal(child.SymbolId, owner);
        Assert.NotNull(Assert.IsType<FuncSymbol>(table.GetSymbol(generated.SymbolId)).GeneratedOrigin);
    }

    [Fact]
    public void Quote_member_adds_an_instance_method_before_impl_registration()
    {
        const string source = """
add_show :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => {
        name := meta.identifier("show", meta.IdentifierCategory.Function);
        meta.add_members(input, [quote member {
            $(name) :: Person -> String { _ => "generated" }
        }])
    }
}

Show :: trait {
    show :: Self -> String
}

Person :: type {
    name :: String
}

ShowPerson :: instance Show expand add_show {}
""";

        var result = Compile("meta_quote_instance_member.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var instance = Assert.Single(module.Declarations.OfType<InstanceDecl>(), static declaration =>
            declaration.Name == "ShowPerson");
        var method = Assert.Single(instance.Methods, static method => method.Name == "show");
        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        var impl = Assert.IsType<ImplSymbol>(table.GetSymbol(instance.SymbolId));
        Assert.Contains(method.SymbolId, impl.Methods);
        Assert.NotNull(Assert.IsType<FuncSymbol>(table.GetSymbol(method.SymbolId)).GeneratedOrigin);
    }

    [Fact]
    public void Quote_members_add_instance_associated_items_before_impl_registration()
    {
        const string source = """
add_items :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => {
        item_name := meta.identifier("Item", meta.IdentifierCategory.AssociatedType);
        max_name := meta.identifier("MAX", meta.IdentifierCategory.AssociatedConst);
        meta.add_members(input, [
            quote member { $(item_name) :: type = Int },
            quote member { $(max_name) :: Int = 42 }
        ])
    }
}

Bounded[T] :: trait {
    Item :: type
    MAX :: T
}

BoundedInt :: instance Bounded[Int] expand add_items {}
""";

        var result = Compile("meta_quote_instance_associated_members.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var trait = Assert.Single(module.Declarations.OfType<TraitDef>());
        var instance = Assert.Single(module.Declarations.OfType<InstanceDecl>());
        var associatedType = Assert.Single(instance.AssociatedTypes);
        var associatedConst = Assert.Single(instance.AssociatedConsts);
        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        var impl = Assert.IsType<ImplSymbol>(table.GetSymbol(instance.SymbolId));
        var typeSymbol = Assert.IsType<AssociatedTypeSymbol>(table.GetSymbol(associatedType.SymbolId));
        var constSymbol = Assert.IsType<AssociatedConstSymbol>(table.GetSymbol(associatedConst.SymbolId));
        Assert.Equal(instance.SymbolId, typeSymbol.OwnerImpl);
        Assert.Equal(instance.SymbolId, constSymbol.OwnerImpl);
        Assert.Equal(trait.SymbolId, typeSymbol.OwnerTrait);
        Assert.Equal(trait.SymbolId, constSymbol.OwnerTrait);
        Assert.Contains(typeSymbol.Id, impl.AssociatedTypes);
        Assert.Contains(constSymbol.Id, impl.AssociatedConsts);
        Assert.NotNull(typeSymbol.GeneratedOrigin);
        Assert.NotNull(constSymbol.GeneratedOrigin);
    }

    [Fact]
    public void Quote_items_are_split_at_parser_node_boundaries_and_support_empty_sequences()
    {
        const string source = """
Items :: comptime quote items {
    choose :: Bool -> Int {
        flag => if flag then { 1 } else { 2 }
    }

    answer :: Unit -> Int {
        _ => 42
    }
};

Empty :: comptime quote items {};
""";

        var result = Compile("meta_quote_items.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(result.TypeInferer);
        var items = Assert.IsType<ComptimeSequenceValue>(GetComptimeValue("Items", symbolTable, inferer));
        Assert.Equal(2, items.Elements.Count);
        var choose = Assert.IsType<ComptimeSyntaxValue>(items.Elements[0]);
        var answer = Assert.IsType<ComptimeSyntaxValue>(items.Elements[1]);
        Assert.Contains("else", choose.Render(), StringComparison.Ordinal);
        Assert.DoesNotContain("answer", choose.Render(), StringComparison.Ordinal);
        Assert.Contains("answer", answer.Render(), StringComparison.Ordinal);
        Assert.Empty(Assert.IsType<ComptimeSequenceValue>(GetComptimeValue("Empty", symbolTable, inferer)).Elements);
    }

    [Fact]
    public void Quote_many_splice_is_validated_after_expansion_and_preserves_fragment_count()
    {
        const string source = """
Base :: comptime quote items {
    first :: Unit -> Int { _ => 1 }
    second :: Unit -> Int { _ => 2 }
};

Combined :: comptime quote items {
    ..$(Base)
    third :: Unit -> Int { _ => 3 }
};
""";

        var result = Compile("meta_quote_many_items.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var combined = Assert.IsType<ComptimeSequenceValue>(GetComptimeValue(
            "Combined",
            Assert.IsType<SymbolTable>(result.SymbolTable),
            Assert.IsType<TypeInferer>(result.TypeInferer)));
        Assert.Equal(3, combined.Elements.Count);
        Assert.Collection(
            combined.Elements.Select(static element => Assert.IsType<ComptimeSyntaxValue>(element).Render()),
            static text => Assert.Contains("first", text, StringComparison.Ordinal),
            static text => Assert.Contains("second", text, StringComparison.Ordinal),
            static text => Assert.Contains("third", text, StringComparison.Ordinal));
    }

    [Fact]
    public void Quote_splice_final_grammar_rejects_a_value_in_an_item_slot()
    {
        const string source = """
Bad :: comptime quote item { $(42) };
""";

        var result = Compile("meta_quote_bad_item_splice.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Message.Contains("quote item", StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Message.Contains("item syntax", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Quote_spliced_declaration_handle_preserves_symbol_identity_through_materialization()
    {
        const string source = """
helper :: Unit -> Int { _ => 41 }
HelperDecl :: comptime meta.declaration_of(helper);

derive_answer :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => meta.add_after(input, [quote item {
        answer :: Unit -> Int {
            _ => $(HelperDecl)(()) + 1
        }
    }])
}

Subject :: type expand derive_answer { value :: Int }
""";

        var result = Compile("meta_quote_handle_identity.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var helper = Assert.Single(module.Declarations.OfType<FuncDef>(), static function =>
            function.Name == "helper");
        var answer = Assert.Single(module.Declarations.OfType<FuncDef>(), static function =>
            function.Name == "answer");
        var binary = Assert.IsType<BinaryExpr>(Assert.Single(answer.Body).Expression);
        var call = Assert.IsType<CallExpr>(binary.Left);
        var reference = Assert.IsType<IdentifierExpr>(call.Function);
        Assert.Equal(helper.SymbolId, reference.SymbolId);
        Assert.Equal(SyntaxIdentityKind.Declaration, reference.AttachedSyntaxIdentity?.Kind);
    }

    [Fact]
    public void Quote_local_binding_and_reference_share_hygiene_identity()
    {
        const string source = """
derive_answer :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => {
        name := meta.identifier("answer", meta.IdentifierCategory.Function);
        meta.add_after(input, [quote item {
            $(name) :: Unit -> Int {
                _ => {
                    local := 41;
                    local + 1
                }
            }
        }])
    }
}

Subject :: type expand derive_answer {}
""";

        var result = Compile("meta_quote_local_hygiene.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var answer = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<FuncDef>(),
            static function => function.Name == "answer");
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
    public void Quote_raw_item_name_is_hygienic_and_not_visible_as_a_public_binding()
    {
        const string source = """
derive_hidden :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => meta.add_after(input, [quote item {
        hidden :: Unit -> Int { _ => 42 }
    }])
}

Subject :: type expand derive_hidden {}
read :: Unit -> Int { _ => hidden(()) }
""";

        var result = Compile("meta_quote_raw_public_name.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Message.Contains("hidden", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("undefined", StringComparison.OrdinalIgnoreCase));
        var hidden = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<FuncDef>(),
            static function => function.Name == "hidden");
        var symbol = Assert.IsType<FuncSymbol>(
            Assert.IsType<SymbolTable>(result.SymbolTable).GetSymbol(hidden.SymbolId));
        Assert.StartsWith("meta_hygiene_", symbol.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void Quote_raw_reference_resolves_at_the_generator_definition_site()
    {
        var result = CompileWorkspace(
            "main.eidos",
            ("Tools/Generators.eidos", """
Tools.Generators :: module {
    export helper :: Unit -> Int { _ => 41 }

    export derive_answer :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
        input => {
            name := meta.identifier("answer", meta.IdentifierCategory.Function);
            meta.add_after(input, [quote item {
                $(name) :: Unit -> Int { _ => helper(()) + 1 }
            }])
        }
    }
}
"""),
            ("main.eidos", """
import Tools.Generators.{derive_answer}

helper :: Unit -> Int { _ => 0 }
Subject :: type expand derive_answer {}
"""));

        Assert.True(result.Success, FormatDiagnostics(result));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var localHelper = Assert.Single(module.Declarations.OfType<FuncDef>(), static function =>
            function.Name == "helper");
        var answer = Assert.Single(module.Declarations.OfType<FuncDef>(), static function =>
            function.Name == "answer");
        var binary = Assert.IsType<BinaryExpr>(Assert.Single(answer.Body).Expression);
        var call = Assert.IsType<CallExpr>(binary.Left);
        var reference = Assert.IsType<IdentifierExpr>(call.Function);
        Assert.Equal(SyntaxIdentityKind.Declaration, reference.AttachedSyntaxIdentity?.Kind);
        Assert.NotEqual(localHelper.SymbolId, reference.SymbolId);
    }

    [Fact]
    public void Quote_raw_reference_does_not_capture_the_target_call_site()
    {
        var result = CompileWorkspace(
            "main.eidos",
            ("Tools/Generators.eidos", """
Tools.Generators :: module {
    export derive_answer :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
        input => {
            name := meta.identifier("answer", meta.IdentifierCategory.Function);
            meta.add_after(input, [quote item {
                $(name) :: Unit -> Int { _ => target_only(()) }
            }])
        }
    }
}
"""),
            ("main.eidos", """
import Tools.Generators.{derive_answer}

target_only :: Unit -> Int { _ => 42 }
Subject :: type expand derive_answer {}
"""));

        var answer = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<FuncDef>(),
            static function => function.Name == "answer");
        var reference = Assert.IsType<IdentifierExpr>(
            Assert.IsType<CallExpr>(Assert.Single(answer.Body).Expression).Function);
        Assert.Equal(SyntaxIdentityKind.Hygiene, reference.AttachedSyntaxIdentity?.Kind);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Message.Contains("hygienic identifier 'target_only'", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("meta.resolve_at", StringComparison.Ordinal));
    }

    [Fact]
    public void Quote_qualified_definition_site_path_preserves_each_resolved_segment_identity()
    {
        const string source = """
Tools :: module {}

Tools.Helpers :: module {
    export helper :: Unit -> Int { _ => 42 }
}

Qualified :: comptime quote expr { Tools.Helpers.helper(()) };
""";

        var result = Compile("meta_quote_qualified_definition_site.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        var syntax = Assert.IsType<ComptimeSyntaxValue>(GetComptimeValue(
            "Qualified",
            table,
            Assert.IsType<TypeInferer>(result.TypeInferer)));
        var segments = syntax.Tokens
            .Where(static token => token.Spelling is "Tools" or "Helpers" or "helper")
            .ToArray();
        Assert.Equal(3, segments.Length);
        Assert.All(segments, static segment =>
        {
            Assert.NotNull(segment.Identity);
            Assert.NotEqual(ComptimeSyntaxIdentityKind.Hygiene, segment.Identity!.Kind);
            Assert.True(segment.Identity.SymbolId.IsValid);
            Assert.False(string.IsNullOrWhiteSpace(segment.Identity.StableIdentity));
        });
        Assert.Equal(3, segments.Select(static segment => segment.Identity!.SymbolId).Distinct().Count());
    }

    [Fact]
    public void Quote_nested_same_name_bindings_keep_distinct_lexical_identities()
    {
        const string source = """
derive_answer :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => {
        name := meta.identifier("answer", meta.IdentifierCategory.Function);
        meta.add_after(input, [quote item {
            $(name) :: Unit -> Int {
                _ => {
                    value := 1;
                    inner := {
                        value := 2;
                        value
                    };
                    value + inner
                }
            }
        }])
    }
}

Subject :: type expand derive_answer {}
""";

        var result = Compile("meta_quote_nested_hygiene.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var answer = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<FuncDef>(),
            static function => function.Name == "answer");
        var body = Assert.IsType<BlockExpr>(Assert.Single(answer.Body).Expression);
        var outerBinding = Assert.IsType<VarPattern>(Assert.IsType<LetDecl>(body.Statements[0]).Pattern);
        var innerBlock = Assert.IsType<BlockExpr>(Assert.IsType<LetDecl>(body.Statements[1]).Value);
        var innerBinding = Assert.IsType<VarPattern>(Assert.IsType<LetDecl>(innerBlock.Statements[0]).Pattern);
        var innerReference = Assert.IsType<IdentifierExpr>(innerBlock.Statements[1]);
        var outerReference = Assert.IsType<IdentifierExpr>(Assert.IsType<BinaryExpr>(body.Statements[2]).Left);

        Assert.NotEqual(outerBinding.SymbolId, innerBinding.SymbolId);
        Assert.NotEqual(
            outerBinding.AttachedSyntaxIdentity?.StableIdentity,
            innerBinding.AttachedSyntaxIdentity?.StableIdentity);
        Assert.Equal(outerBinding.SymbolId, outerReference.SymbolId);
        Assert.Equal(innerBinding.SymbolId, innerReference.SymbolId);
    }

    [Fact]
    public void Quote_lambda_parameter_and_body_reference_share_hygiene_identity()
    {
        const string source = """
derive_answer :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => {
        name := meta.identifier("answer", meta.IdentifierCategory.Function);
        meta.add_after(input, [quote item {
            $(name) :: Unit -> Int {
                _ => {
                    apply := { value => value + 1 };
                    apply(41)
                }
            }
        }])
    }
}

Subject :: type expand derive_answer {}
""";

        var result = Compile("meta_quote_lambda_hygiene.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var answer = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<FuncDef>(),
            static function => function.Name == "answer");
        var body = Assert.IsType<BlockExpr>(Assert.Single(answer.Body).Expression);
        var lambdaBlock = Assert.IsType<BlockExpr>(Assert.IsType<LetDecl>(body.Statements[0]).Value);
        var lambda = Assert.IsType<LambdaExpr>(lambdaBlock.ResultExpression);
        var parameter = Assert.IsType<VarPattern>(Assert.Single(lambda.Parameters));
        var reference = Assert.IsType<IdentifierExpr>(Assert.IsType<BinaryExpr>(lambda.Body).Left);

        Assert.Equal(parameter.SymbolId, reference.SymbolId);
        Assert.Equal(
            parameter.AttachedSyntaxIdentity?.StableIdentity,
            reference.AttachedSyntaxIdentity?.StableIdentity);
        Assert.Equal(SyntaxIdentityKind.Hygiene, reference.AttachedSyntaxIdentity?.Kind);
    }

    [Fact]
    public void Quote_or_pattern_alternatives_register_their_hygiene_identities_to_one_binding()
    {
        const string source = """
derive_answer :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => {
        name := meta.identifier("answer", meta.IdentifierCategory.Function);
        meta.add_after(input, [quote item {
            $(name) :: Int -> Int {
                value | value => value
            }
        }])
    }
}

Subject :: type expand derive_answer {}
""";

        var result = Compile("meta_quote_or_pattern_hygiene.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var answer = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<FuncDef>(),
            static function => function.Name == "answer");
        var branch = answer.Body[0];
        var alternatives = Assert.IsType<OrPattern>(branch.Pattern).Alternatives
            .Select(static alternative => Assert.IsType<VarPattern>(alternative))
            .ToArray();
        var reference = Assert.IsType<IdentifierExpr>(branch.Expression);

        Assert.Equal(2, alternatives.Length);
        Assert.All(alternatives, alternative => Assert.Equal(reference.SymbolId, alternative.SymbolId));
        Assert.All(alternatives, static alternative =>
            Assert.Equal(SyntaxIdentityKind.Hygiene, alternative.AttachedSyntaxIdentity?.Kind));
        Assert.Equal(SyntaxIdentityKind.Hygiene, reference.AttachedSyntaxIdentity?.Kind);
    }

    [Fact]
    public void Meta_identifier_creates_an_explicit_public_identity_and_rejects_reserved_names()
    {
        const string source = """
PublicName :: comptime meta.identifier("generated_answer", meta.IdentifierCategory.Function);
""";
        var result = Compile("meta_identifier.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var identifier = Assert.IsType<ComptimeMetaObjectValue>(GetComptimeValue(
            "PublicName",
            Assert.IsType<SymbolTable>(result.SymbolTable),
            Assert.IsType<TypeInferer>(result.TypeInferer)));
        Assert.Equal("identifier", identifier.SchemaKind);
        Assert.True(identifier.TryGet("spelling", out var spelling));
        Assert.Equal("generated_answer", Assert.IsType<ComptimeStringValue>(spelling).Value);

        var invalid = Compile(
            "meta_identifier_reserved.eidos",
            "Bad :: comptime meta.identifier(\"__generated\", meta.IdentifierCategory.Function);");
        Assert.False(invalid.Success);
        Assert.Contains(invalid.Diagnostics, static diagnostic =>
            diagnostic.Message.Contains("reserved", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Meta_site_resolve_and_text_parse_apis_return_canonical_results()
    {
        const string source = """
seed :: Unit -> Int { _ => 7 }
SeedDecl :: comptime meta.declaration_of(seed);
Site :: comptime meta.site_of(SeedDecl);
Resolved :: comptime meta.resolve_at(Site, "seed");
Origin :: comptime meta.origin_of(SeedDecl);
ParsedExpr :: comptime meta.parse_expr("40 + 2", Origin);
ParsedItems :: comptime meta.parse_items("first :: Unit -> Int { _ => 1 } second :: Unit -> Int { _ => 2 }", Origin);
ParseError :: comptime meta.parse_expr("if", Origin);
""";

        var result = Compile("meta_syntax_apis.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(result.TypeInferer);
        var resolved = Assert.IsType<ComptimeAdtValue>(GetComptimeValue("Resolved", table, inferer));
        Assert.Equal("Ok", resolved.ConstructorName);
        Assert.Equal("seed", Assert.IsType<ComptimeDeclValue>(Assert.Single(resolved.PositionalValues)).Name);

        var parsedExpr = Assert.IsType<ComptimeAdtValue>(GetComptimeValue("ParsedExpr", table, inferer));
        Assert.Equal("Ok", parsedExpr.ConstructorName);
        Assert.Contains(
            "40 + 2",
            Assert.IsType<ComptimeSyntaxValue>(Assert.Single(parsedExpr.PositionalValues)).Render(),
            StringComparison.Ordinal);

        var parsedItems = Assert.IsType<ComptimeAdtValue>(GetComptimeValue("ParsedItems", table, inferer));
        var items = Assert.IsType<ComptimeSequenceValue>(Assert.Single(parsedItems.PositionalValues));
        Assert.Equal(2, items.Elements.Count);

        var parseError = Assert.IsType<ComptimeAdtValue>(GetComptimeValue("ParseError", table, inferer));
        Assert.Equal("Err", parseError.ConstructorName);
        Assert.Equal(
            "ParseError",
            Assert.IsType<ComptimeAdtValue>(Assert.Single(parseError.PositionalValues)).ConstructorName);
    }

    [Fact]
    public void Meta_resolve_at_searches_the_boundary_scope_and_returns_a_typed_field_handle()
    {
        const string source = """
Record :: type {
    value :: Int
}

RecordDecl :: comptime meta.declaration_of(Record);
RecordSite :: comptime meta.site_of(RecordDecl);
ResolvedField :: comptime meta.resolve_at(RecordSite, "value");
""";

        var result = Compile("meta_resolve_typed_refinements.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(result.TypeInferer);

        var resolvedField = Assert.IsType<ComptimeAdtValue>(GetComptimeValue("ResolvedField", table, inferer));
        Assert.Equal("Ok", resolvedField.ConstructorName);
        var field = Assert.IsType<ComptimeDeclValue>(Assert.Single(resolvedField.PositionalValues));
        Assert.Equal("value", field.Name);
        Assert.Equal(
            WellKnownTypeIds.MetaFieldId,
            Assert.IsType<TyCon>(field.StaticType).Id.Value);

    }

    [Fact]
    public void Meta_declaration_of_returns_a_typed_parameter_handle()
    {
        const string source = """
reflect_parameter :: comptime Int -> meta.Declaration {
    value => meta.declaration_of(value)
}

ReflectedParameter :: comptime reflect_parameter(42);
""";

        var result = Compile("meta_parameter_refinement.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(result.TypeInferer);
        var parameter = Assert.IsType<ComptimeDeclValue>(GetComptimeValue("ReflectedParameter", table, inferer));
        Assert.Equal("value", parameter.Name);
        Assert.Equal(
            WellKnownTypeIds.MetaParameterId,
            Assert.IsType<TyCon>(parameter.StaticType).Id.Value);
    }

    [Fact]
    public void Meta_parameter_builder_returns_a_typed_parameter_value()
    {
        const string source = """
ParameterValue :: comptime meta.parameter("value", Int);
""";

        var result = Compile("meta_parameter_builder_typed.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        var inferer = Assert.IsType<TypeInferer>(result.TypeInferer);
        var parameter = Assert.IsType<ComptimeMetaObjectValue>(GetComptimeValue("ParameterValue", table, inferer));
        Assert.Equal(
            WellKnownTypeIds.MetaParameterId,
            Assert.IsType<TyCon>(parameter.StaticType).Id.Value);
    }

    [Fact]
    public void Expression_expand_captures_typed_syntax_and_materializes_a_hygienic_expression()
    {
        const string source = """
twice :: comptime meta.Syntax[meta.Expr] -> meta.Site[meta.Expr] -> meta.Syntax[meta.Expr] {
    expression => _ => quote expr {
        {
            value := $(expression);
            value + value
        }
    }
}

answer :: Int = expand twice(21);
""";

        var result = Compile("meta_expression_expand.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var answer = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<LetDecl>(),
            static declaration => declaration.Pattern is VarPattern { Name: "answer" });
        var expansion = Assert.IsType<ExpandExpr>(answer.Value);
        var block = Assert.IsType<BlockExpr>(expansion.ExpandedExpression);
        Assert.Equal(2, block.Statements.Count);
        Assert.All(
            EnumerateAstNodes(block).OfType<IdentifierExpr>().Where(static identifier => identifier.Name == "value"),
            static identifier => Assert.True(identifier.SymbolId.IsValid));
        Assert.NotEmpty(block.GeneratedOriginChain);
    }

    [Fact]
    public void Expression_expand_site_resolves_call_site_locals_to_typed_handles()
    {
        const string source = """
use_site :: comptime meta.Site[meta.Expr] -> meta.Syntax[meta.Expr] {
    site => match meta.resolve_at(site, "local") {
        Ok(declaration) => quote expr { $(declaration) },
        Err(_) => quote expr { 0 }
    }
}

answer :: Int = {
    local := 41;
    expand use_site()
};
""";

        var result = Compile("meta_expression_site_resolve.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var answer = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<LetDecl>(),
            static declaration => declaration.Pattern is VarPattern { Name: "answer" });
        var body = Assert.IsType<BlockExpr>(answer.Value);
        var local = Assert.IsType<LetDecl>(body.Statements[0]);
        var localPattern = Assert.IsType<VarPattern>(local.Pattern);
        var expansion = Assert.IsType<ExpandExpr>(body.Statements[1]);
        var resolved = Assert.IsType<IdentifierExpr>(expansion.ExpandedExpression);
        Assert.Equal("local", resolved.Name);
        Assert.Equal(localPattern.SymbolId, resolved.SymbolId);
        Assert.NotEmpty(resolved.GeneratedOriginChain);
    }

    [Fact]
    public void Pattern_expand_materializes_and_type_checks_the_generated_pattern()
    {
        const string source = """
make_wildcard :: comptime meta.Site[meta.Pattern] -> meta.Syntax[meta.Pattern] {
    _ => quote pattern { _ }
}

is_zero :: Int -> Bool {
    expand make_wildcard() => false
}
""";

        var result = Compile("meta_pattern_expand.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var function = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<FuncDef>(),
            static declaration => declaration.Name == "is_zero");
        var pattern = Assert.IsType<WildcardPattern>(Assert.Single(function.Body).Pattern);
        Assert.NotNull(pattern.InferredType);
        Assert.NotEmpty(pattern.GeneratedOriginChain);
    }

    [Fact]
    public void Type_expand_materializes_and_type_checks_the_generated_type()
    {
        const string source = """
make_int :: comptime meta.Site[meta.TypeSyntax] -> meta.Syntax[meta.TypeSyntax] {
    _ => quote type { Int }
}

answer :: expand make_int() = 42;
""";

        var result = Compile("meta_type_expand.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var answer = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<LetDecl>(),
            static declaration => declaration.Pattern is VarPattern { Name: "answer" });
        var expansion = Assert.IsType<ExpandType>(answer.TypeAnnotation);
        var expandedType = Assert.IsType<TypePath>(expansion.ExpandedType);
        Assert.Equal("Int", expandedType.TypeName);
        Assert.True(expandedType.SymbolId.IsValid);
        Assert.NotNull(expansion.InferredType);
        Assert.NotEmpty(expandedType.GeneratedOriginChain);
    }

    [Fact]
    public void Syntax_site_expand_rejects_a_generator_from_another_syntax_category()
    {
        const string source = """
make_expression :: comptime meta.Site[meta.Expr] -> meta.Syntax[meta.Expr] {
    _ => quote expr { 42 }
}

answer :: expand make_expression() = 42;
""";

        var result = Compile("meta_expand_category_mismatch.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3620" &&
            diagnostic.Message.Contains("make_expression", StringComparison.Ordinal));
    }

    [Fact]
    public void Syntax_site_expand_requires_the_compiler_supplied_site_parameter()
    {
        const string source = """
make_expression :: comptime Int -> meta.Syntax[meta.Expr] {
    value => quote expr { $(value) }
}

answer :: Int = expand make_expression(42);
""";

        var result = Compile("meta_expand_missing_site_parameter.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3620" &&
            diagnostic.Message.Contains("make_expression", StringComparison.Ordinal));
    }

    [Fact]
    public void Syntax_site_expand_rejects_a_wrong_return_category()
    {
        const string source = """
make_pattern :: comptime meta.Site[meta.Expr] -> meta.Syntax[meta.Pattern] {
    _ => quote pattern { _ }
}

answer :: Int = expand make_pattern();
""";

        var result = Compile("meta_expand_wrong_return_category.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3620" &&
            diagnostic.Message.Contains("make_pattern", StringComparison.Ordinal));
    }

    [Fact]
    public void Ordinary_comptime_calls_do_not_implicitly_capture_syntax_arguments()
    {
        const string source = """
consume_syntax :: comptime meta.Syntax[meta.Expr] -> Int {
    _ => 42
}

Value :: comptime consume_syntax(1 + 2);
""";

        var result = Compile("meta_ordinary_call_no_syntax_capture.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Message.Contains("Syntax", StringComparison.Ordinal) ||
            diagnostic.Message.Contains("type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Statement_expand_atomically_splices_a_sequence_into_the_lexical_block()
    {
        const string source = """
make_bindings :: comptime meta.Site[meta.Stmt] -> Seq[meta.Syntax[meta.Stmt]] {
    _ => [
        quote stmt { 20; },
        quote stmt { 22; }
    ]
}

answer :: Int = {
    expand make_bindings();
    40 + 2
};
""";

        var result = Compile("meta_statement_expand.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var answer = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<LetDecl>(),
            static declaration => declaration.Pattern is VarPattern { Name: "answer" });
        var block = Assert.IsType<BlockExpr>(answer.Value);
        Assert.Equal(3, block.Statements.Count);
        Assert.IsType<LiteralExpr>(block.Statements[0]);
        Assert.IsType<LiteralExpr>(block.Statements[1]);
        var sum = Assert.IsType<BinaryExpr>(block.ResultExpression);
        Assert.Equal("40", Assert.IsType<LiteralExpr>(sum.Left).RawText);
        Assert.Equal("2", Assert.IsType<LiteralExpr>(sum.Right).RawText);
        Assert.All(block.Statements.Take(2), static statement => Assert.NotEmpty(statement.GeneratedOriginChain));
    }

    [Fact]
    public void Item_expand_atomically_splices_a_sequence_into_the_owning_module()
    {
        const string source = """
make_items :: comptime meta.Site[meta.Item] -> Seq[meta.Syntax[meta.Item]] {
    _ => quote items {
        generated_first :: Unit -> Int { _ => 20 }
        generated_second :: Unit -> Int { _ => 22 }
    }
}

expand make_items();
answer :: Int = 42;
""";

        var result = Compile("meta_item_expand.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        Assert.DoesNotContain(module.Declarations, static declaration => declaration is ExpandDeclaration);
        var generated = module.Declarations
            .OfType<FuncDef>()
            .Where(static function => function.Name.StartsWith("generated_", StringComparison.Ordinal))
            .OrderBy(static function => function.Name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, generated.Length);
        Assert.All(generated, static function =>
        {
            Assert.True(function.SymbolId.IsValid);
            Assert.NotEmpty(function.GeneratedOriginChain);
        });
    }

    [Fact]
    public void Member_expand_splices_type_members_in_lexical_order()
    {
        const string source = """
make_members :: comptime meta.Site[meta.Member] -> Seq[meta.Syntax[meta.Member]] {
    _ => [
        quote member { generated_field :: Int },
        quote member { GeneratedCase :: type { payload :: Int } }
    ]
}

Subject :: type {
    existing :: Int,
    expand make_members();
    Tail :: type { tail :: Int }
}
""";

        var result = Compile("meta_type_member_expand.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var subject = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<AdtDef>(),
            static declaration => declaration.Name == "Subject");
        Assert.Equal(["existing", "generated_field"], subject.Fields.Select(static field => field.Name));
        Assert.Equal(["GeneratedCase", "Tail"], subject.Cases.Select(static caseType => caseType.Name));
        Assert.DoesNotContain(subject.Members, static member => member is ExpandDeclaration);
        Assert.NotEmpty(subject.Fields[1].GeneratedOriginChain);
        Assert.NotEmpty(subject.Cases[0].GeneratedOriginChain);
        Assert.True(subject.Fields[1].SymbolId.IsValid);
        Assert.True(subject.Cases[0].SymbolId.IsValid);
    }

    [Fact]
    public void Member_expand_generates_trait_and_instance_methods()
    {
        const string source = """
make_trait_method :: comptime meta.Site[meta.Member] -> meta.Syntax[meta.Member] {
    _ => quote member { transform :: Int -> Int }
}

make_instance_method :: comptime meta.Site[meta.Member] -> meta.Syntax[meta.Member] {
    _ => quote member { transform :: Int -> Int { _ => 42 } }
}

Transformer :: trait {
    expand make_trait_method();
}

TransformerInt :: instance Transformer {
    expand make_instance_method();
}
""";

        var result = Compile("meta_associated_member_expand.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var trait = Assert.Single(module.Declarations.OfType<TraitDef>());
        var instance = Assert.Single(module.Declarations.OfType<InstanceDecl>());
        var traitMethod = Assert.Single(trait.Methods, static method => method.Name == "transform");
        var instanceMethod = Assert.Single(instance.Methods, static method => method.Name == "transform");
        Assert.True(traitMethod.SymbolId.IsValid);
        Assert.True(instanceMethod.SymbolId.IsValid);
        Assert.NotEmpty(traitMethod.GeneratedOriginChain);
        Assert.NotEmpty(instanceMethod.GeneratedOriginChain);
        Assert.DoesNotContain(trait.Members, static member => member is ExpandDeclaration);
        Assert.DoesNotContain(instance.Members, static member => member is ExpandDeclaration);
    }

    [Fact]
    public void Member_expand_category_mismatch_is_atomic()
    {
        const string source = """
make_invalid_members :: comptime meta.Site[meta.Member] -> Seq[meta.Syntax[meta.Member]] {
    _ => [
        quote member { generated_field :: Int },
        quote member { generated_method :: Unit -> Int { _ => 42 } }
    ]
}

Subject :: type {
    existing :: Int,
    expand make_invalid_members();
}
""";

        var result = Compile("meta_member_expand_atomic_mismatch.eidos", source);

        Assert.False(result.Success);
        var subject = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<AdtDef>(),
            static declaration => declaration.Name == "Subject");
        Assert.Equal(["existing"], subject.Fields.Select(static field => field.Name));
        Assert.DoesNotContain(subject.Fields, static field => field.Name == "generated_field");
        Assert.Contains(subject.Members, static member => member is ExpandDeclaration);
    }

    [Fact]
    public void Member_expand_reaches_a_fixed_point_and_accepts_an_empty_sequence()
    {
        const string source = """
make_empty :: comptime meta.Site[meta.Member] -> Seq[meta.Syntax[meta.Member]] {
    _ => []
}

make_inner :: comptime meta.Site[meta.Member] -> meta.Syntax[meta.Member] {
    _ => quote member { generated :: Int }
}

make_outer :: comptime meta.Site[meta.Member] -> meta.Syntax[meta.Member] {
    _ => quote member { expand make_inner(); }
}

Subject :: type {
    existing :: Int,
    expand make_empty();
    expand make_outer();
    Tail :: type {}
}
""";

        var result = Compile("meta_member_expand_fixed_point.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var subject = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<AdtDef>(),
            static declaration => declaration.Name == "Subject");
        Assert.Equal(["existing", "generated"], subject.Fields.Select(static field => field.Name));
        Assert.DoesNotContain(subject.Members, static member => member is ExpandDeclaration);
        Assert.Equal(2, subject.Fields[1].GeneratedOriginChain.Count);
    }

    [Fact]
    public void Statement_and_item_expand_reach_nested_fixed_points()
    {
        const string source = """
make_inner_stmt :: comptime meta.Site[meta.Stmt] -> meta.Syntax[meta.Stmt] {
    _ => quote stmt { 20; }
}

make_outer_stmt :: comptime meta.Site[meta.Stmt] -> meta.Syntax[meta.Stmt] {
    _ => quote stmt { expand make_inner_stmt(); }
}

make_inner_item :: comptime meta.Site[meta.Item] -> meta.Syntax[meta.Item] {
    _ => quote item { generated :: Unit -> Int { _ => 22 } }
}

make_outer_item :: comptime meta.Site[meta.Item] -> meta.Syntax[meta.Item] {
    _ => quote item { expand make_inner_item(); }
}

expand make_outer_item();

answer :: Int = {
    expand make_outer_stmt();
    42
};
""";

        var result = Compile("meta_statement_item_expand_fixed_point.eidos", source);

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
    public void Syntax_site_expand_reports_non_convergent_nested_generation()
    {
        const string source = """
repeat_statement :: comptime meta.Site[meta.Stmt] -> meta.Syntax[meta.Stmt] {
    _ => quote stmt { expand repeat_statement(); }
}

answer :: Int = {
    expand repeat_statement();
    42
};
""";

        var result = Compile("meta_statement_expand_non_convergent.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Message.Contains("meta expansion did not converge", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("syntax-site expansions continued to produce nested sites", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("producer 'repeat_statement'", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("target 'answer'", StringComparison.Ordinal) &&
            !diagnostic.Message.Contains("origin chain '<source>'", StringComparison.Ordinal));
    }

    [Fact]
    public void Item_expand_rejects_a_late_name_collision_without_partial_output()
    {
        const string source = """
GeneratedName :: comptime meta.identifier("generated", meta.IdentifierCategory.Function);
ExistingName :: comptime meta.identifier("existing", meta.IdentifierCategory.Function);

make_items :: comptime meta.Site[meta.Item] -> Seq[meta.Syntax[meta.Item]] {
    _ => [
        quote item { $(GeneratedName) :: Unit -> Int { _ => 1 } },
        quote item { $(ExistingName) :: Unit -> Int { _ => 2 } }
    ]
}

existing :: Unit -> Int { _ => 0 }
expand make_items();
""";

        var result = Compile("meta_item_expand_atomic_collision.eidos", source);

        Assert.False(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        Assert.DoesNotContain(
            module.Declarations.OfType<FuncDef>(),
            static declaration => declaration.Name == "generated");
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Message.Contains("existing", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Syntax_schema_covers_every_concrete_ast_node_and_quote_kind()
    {
        var astTypes = typeof(EidosAstNode).Assembly.GetTypes()
            .Where(static type => !type.IsAbstract && typeof(EidosAstNode).IsAssignableFrom(type))
            .ToArray();

        Assert.NotEmpty(astTypes);
        Assert.All(astTypes, static type => Assert.NotEmpty(SyntaxSchema.GetNodeCategories(type)));
        Assert.Equal(Enum.GetValues<QuoteKind>().Length, SyntaxSchema.All.Count);
        Assert.Equal(SyntaxSchema.All.Count, SyntaxSchema.All.Select(static entry => entry.SourceName).Distinct().Count());
    }
}
