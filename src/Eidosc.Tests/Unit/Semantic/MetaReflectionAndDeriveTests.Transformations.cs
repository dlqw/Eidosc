using Eidosc.Ast.Declarations;
using Eidosc.Ast.Types;
using Eidosc.Symbols;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public sealed partial class MetaReflectionAndDeriveTests
{
    [Fact]
    public void SyntaxReplacement_CanChangeAFunctionPublicShapeWithinItsSealedCategory()
    {
        const string source = """
replace_signature :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => {
        parameter := meta.parameter("flag", Bool);
        replacement := meta.function("work", [parameter], String, meta.expr_string("changed"));
        meta.replace_target(input, replacement)
    }
}

work :: Int -> Int expand replace_signature {
    value => value
}

use :: Unit -> String {
    _ => work(true)
}
""";

        var result = Compile("meta_syntax_replace_function_shape.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var work = Assert.Single(module.Declarations.OfType<FuncDef>(), static function => function.Name == "work");
        Assert.Single(work.GeneratedOriginChain);
        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        var workSymbol = Assert.IsType<FuncSymbol>(table.GetSymbol(work.SymbolId));
        var signature = Assert.IsType<ArrowType>(Assert.Single(work.Signature));
        Assert.Equal("Bool", Assert.IsType<TypePath>(signature.ParamType).TypeName);
        Assert.Equal("String", Assert.IsType<TypePath>(signature.ReturnType).TypeName);
        Assert.NotNull(workSymbol.GeneratedOrigin);
    }

    [Fact]
    public void SyntaxReplacement_CanReplaceAnEntireTypeAndItsOwnedSymbolGraph()
    {
        const string source = """
replace_type :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => {
        type_name := meta.identifier("Replacement", meta.IdentifierCategory.Type);
        field_name := meta.identifier("value", meta.IdentifierCategory.Field);
        meta.replace_target(input, quote item {
            $(type_name) :: type {
                $(field_name) :: String
            }
        })
    }
}

Subject :: type expand replace_type {
    old :: Int
}

read :: Replacement -> String {
    value => value.value
}
""";

        var result = Compile("meta_syntax_replace_type.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        Assert.DoesNotContain(module.Declarations.OfType<AdtDef>(), static type => type.Name == "Subject");
        var replacement = Assert.Single(module.Declarations.OfType<AdtDef>(), static type =>
            type.Name == "Replacement");
        Assert.Equal(["value"], replacement.Fields.Select(static field => field.Name));
        Assert.All(EnumerateAstNodes(replacement), static node => Assert.Single(node.GeneratedOriginChain));

        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        Assert.DoesNotContain(table.Symbols.Values.OfType<AdtSymbol>(), static symbol => symbol.Name == "Subject");
        var replacementSymbol = Assert.IsType<AdtSymbol>(table.GetSymbol(replacement.SymbolId));
        Assert.NotNull(replacementSymbol.GeneratedOrigin);
        var fieldSymbol = Assert.IsType<FieldSymbol>(table.GetSymbol(Assert.Single(replacement.Fields).SymbolId));
        Assert.Equal(replacement.SymbolId, fieldSymbol.OwnerType);
    }

    [Fact]
    public void BodyReplacement_PreservesAndSupportsGenericFunctionContracts()
    {
        const string source = """
rewrite_generic :: comptime meta.Target[meta.Stage.Body] -> meta.Transformation {
    input => meta.replace_target(input, quote item {
        identity[T] :: T -> T {
            value => value
        }
    })
}

identity[T] :: T -> T expand rewrite_generic {
    value => value
}
""";

        var result = Compile("meta_body_replace_generic.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var identity = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<FuncDef>(),
            static function => function.Name == "identity");
        Assert.Single(identity.TypeParams);
        Assert.Single(identity.GeneratedOriginChain);
        Assert.NotNull(Assert.IsType<FuncSymbol>(
            Assert.IsType<SymbolTable>(result.SymbolTable).GetSymbol(identity.SymbolId)).GeneratedOrigin);
    }

    [Fact]
    public void SyntaxReplacement_SupportsTraitEffectAndInstanceItemCategories()
    {
        const string source = """
Show :: trait {
    show :: Self -> String
}

Person :: type {}

replace_trait :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => {
        name := meta.identifier("ReplacementTrait", meta.IdentifierCategory.Item);
        meta.replace_target(input, quote item { $(name) :: trait {} })
    }
}

replace_effect :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => {
        name := meta.identifier("ReplacementEffect", meta.IdentifierCategory.Item);
        meta.replace_target(input, quote item { $(name) :: effect; })
    }
}

replace_instance :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => {
        name := meta.identifier("ReplacementInstance", meta.IdentifierCategory.Item);
        meta.replace_target(input, quote item {
            $(name) :: instance Show {
                show :: Person -> String { _ => "replacement" }
            }
        })
    }
}

OriginalTrait :: trait expand replace_trait {}
OriginalEffect :: effect expand replace_effect;
OriginalInstance :: instance Show expand replace_instance {
    show :: Person -> String { _ => "original" }
}
""";

        var result = Compile("meta_syntax_replace_item_categories.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        Assert.Contains(module.Declarations.OfType<TraitDef>(), static declaration =>
            declaration.Name == "ReplacementTrait" && declaration.GeneratedOriginChain.Count == 1);
        Assert.Contains(module.Declarations.OfType<EffectDef>(), static declaration =>
            declaration.Name == "ReplacementEffect" && declaration.GeneratedOriginChain.Count == 1);
        Assert.Contains(module.Declarations.OfType<InstanceDecl>(), static declaration =>
            declaration.Name == "ReplacementInstance" && declaration.GeneratedOriginChain.Count == 1);
        Assert.DoesNotContain(module.Declarations, static declaration => declaration switch
        {
            TraitDef trait => trait.Name == "OriginalTrait",
            EffectDef effect => effect.Name == "OriginalEffect",
            InstanceDecl instance => instance.Name == "OriginalInstance",
            _ => false
        });
    }

    [Fact]
    public void SyntaxReplacement_SupportsCompileTimeValueCategory()
    {
        const string source = """
replace_value :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => meta.replace_target(input, meta.comptime_value("NEW_VALUE", Int, meta.expr_int(42)))
}

OLD_VALUE :: comptime expand replace_value = 1;
""";

        var result = Compile("meta_syntax_replace_value.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        Assert.DoesNotContain(module.Declarations.OfType<LetDecl>(), static declaration =>
            declaration.Pattern is Eidosc.Ast.Patterns.VarPattern { Name: "OLD_VALUE" });
        var replacement = Assert.Single(module.Declarations.OfType<LetDecl>(), static declaration =>
            declaration.Pattern is Eidosc.Ast.Patterns.VarPattern { Name: "NEW_VALUE" });
        Assert.Single(replacement.GeneratedOriginChain);
        Assert.NotNull(Assert.IsType<VarSymbol>(
            Assert.IsType<SymbolTable>(result.SymbolTable).GetSymbol(replacement.SymbolId)).GeneratedOrigin);
    }

    [Fact]
    public void SyntaxReplacement_ReplacesNestedClosedCaseAndConstructorProjection()
    {
        const string source = """
replace_case :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => {
        case_name := meta.identifier("Original", meta.IdentifierCategory.Type);
        field_name := meta.identifier("value", meta.IdentifierCategory.Field);
        meta.replace_target(input, quote member {
            $(case_name) :: type {
                $(field_name) :: String
            }
        })
    }
}

Tree :: type {
    Original :: type expand replace_case {
        old :: Int
    }
}
""";

        var result = Compile("meta_syntax_replace_case.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var tree = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<AdtDef>(),
            static type => type.Name == "Tree");
        var replacement = Assert.Single(tree.Cases);
        Assert.Equal("Original", replacement.Name);
        Assert.Equal("value", Assert.Single(replacement.Fields).Name);
        Assert.Single(replacement.GeneratedOriginChain);
        var constructor = Assert.Single(tree.Constructors);
        Assert.Equal("Original", constructor.Name);
        Assert.Single(constructor.GeneratedOriginChain);

        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        Assert.Single(table.Symbols.Values.OfType<AdtSymbol>(), static symbol => symbol.Name == "Original");
        var caseSymbol = Assert.IsType<AdtSymbol>(table.GetSymbol(replacement.SymbolId));
        Assert.Equal(tree.SymbolId, caseSymbol.ParentAdt);
        Assert.Equal(replacement.ConstructorSymbolId, caseSymbol.CaseConstructor);
        Assert.NotNull(caseSymbol.GeneratedOrigin);
        Assert.NotNull(Assert.IsType<CtorSymbol>(table.GetSymbol(replacement.ConstructorSymbolId)).GeneratedOrigin);
    }

    [Fact]
    public void SyntaxReplacement_ReplacesTraitAndInstanceMethodsInsideTheirOwners()
    {
        const string source = """
rewrite_trait_method :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => meta.replace_target(input, quote item {
        changed :: Bool -> String { _ => "ok" }
    })
}

rewrite_instance_method :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => meta.replace_target(input, quote item {
        show :: Person -> String { _ => "replacement" }
    })
}

Owner :: trait {
    original :: Int -> Int expand rewrite_trait_method {
        value => value
    }
}

Show :: trait {
    show :: Self -> String
}

Person :: type {}

ShowPerson :: instance Show {
    show :: Person -> String expand rewrite_instance_method { _ => "original" }
}
""";

        var result = Compile("meta_syntax_replace_nested_methods.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var owner = Assert.Single(module.Declarations.OfType<TraitDef>(), static trait => trait.Name == "Owner");
        var changed = Assert.Single(owner.Methods);
        Assert.Equal("changed", changed.Name);
        Assert.Single(changed.GeneratedOriginChain);

        var instance = Assert.Single(module.Declarations.OfType<InstanceDecl>());
        var show = Assert.Single(instance.Methods);
        Assert.Equal("show", show.Name);
        Assert.Single(show.GeneratedOriginChain);

        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        var ownerSymbol = Assert.IsType<TraitSymbol>(table.GetSymbol(owner.SymbolId));
        Assert.Equal([changed.SymbolId], ownerSymbol.Methods);
        Assert.NotNull(Assert.IsType<FuncSymbol>(table.GetSymbol(changed.SymbolId)).GeneratedOrigin);

        var instanceSymbol = Assert.IsType<ImplSymbol>(table.GetSymbol(instance.SymbolId));
        Assert.Equal([show.SymbolId], instanceSymbol.Methods);
        Assert.NotNull(Assert.IsType<FuncSymbol>(table.GetSymbol(show.SymbolId)).GeneratedOrigin);
        Assert.DoesNotContain(table.Symbols.Values.OfType<FuncSymbol>(), static symbol => symbol.Name == "original");
    }

    [Fact]
    public void SyntaxReplacement_TreatsFunctionDeclarationsAndDefinitionsAsOneCategory()
    {
        const string source = """
define_function :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => meta.replace_target(input, quote item {
        defined :: Bool -> String { _ => "defined" }
    })
}

declare_function :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => meta.replace_target(input, quote item {
        declared :: String -> Bool;
    })
}

old_declaration :: Int -> Int expand define_function;
old_definition :: Int -> Int expand declare_function { value => value }
""";

        var result = Compile("meta_syntax_replace_function_forms.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        Assert.DoesNotContain(module.Declarations.OfType<FuncDecl>(), static function =>
            function.Name == "old_declaration");
        Assert.DoesNotContain(module.Declarations.OfType<FuncDef>(), static function =>
            function.Name == "old_definition");

        var definition = Assert.Single(module.Declarations.OfType<FuncDef>(), static function =>
            function.Name == "defined");
        var declaration = Assert.Single(module.Declarations.OfType<FuncDecl>(), static function =>
            function.Name == "declared");
        Assert.Single(definition.GeneratedOriginChain);
        Assert.Single(declaration.GeneratedOriginChain);
        Assert.NotNull(Assert.IsType<FuncSymbol>(
            Assert.IsType<SymbolTable>(result.SymbolTable).GetSymbol(definition.SymbolId)).GeneratedOrigin);
    }

    [Fact]
    public void SyntaxReplacement_ReplacesANestedModuleAndItsOwnedSymbolGraph()
    {
        const string source = """
Original :: module expand replace_module {
    replace_module :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
        input => meta.replace_target(input, quote item {
            Replacement :: module {
                generated :: Unit -> String { _ => "ok" }
            }
        })
    }

    old :: Unit -> Int { _ => 1 }
    Nested :: module {
        stale :: Unit -> Bool { _ => false }
    }
}
""";

        var result = Compile("meta_syntax_replace_module.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var root = Assert.IsType<ModuleDecl>(result.Ast);
        Assert.DoesNotContain(root.Declarations.OfType<ModuleDecl>(), static module =>
            module.Path.SequenceEqual(["Original"]));
        var replacement = Assert.Single(root.Declarations.OfType<ModuleDecl>(), static module =>
            module.Path.SequenceEqual(["Replacement"]));
        var generated = Assert.Single(replacement.Declarations.OfType<FuncDef>());
        Assert.Equal("generated", generated.Name);
        Assert.All(EnumerateAstNodes(replacement), static node => Assert.Single(node.GeneratedOriginChain));

        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        Assert.Null(table.Modules.LookupModuleByPath(["Original"]));
        Assert.Null(table.Modules.LookupModuleByPath(["Nested"]));
        Assert.Equal(replacement.SymbolId, table.Modules.LookupModuleByPath(["Replacement"]));
        Assert.DoesNotContain(table.Symbols.Values.OfType<FuncSymbol>(), static symbol =>
            symbol.Name is "old" or "stale");
        Assert.NotNull(Assert.IsType<ModuleSymbol>(table.GetSymbol(replacement.SymbolId)).GeneratedOrigin);
        Assert.NotNull(Assert.IsType<FuncSymbol>(table.GetSymbol(generated.SymbolId)).GeneratedOrigin);
    }

    [Fact]
    public void SyntaxRemoval_SupportsTypeClosedCaseTraitMethodAndModuleTargets()
    {
        const string source = """
remove :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => meta.remove_target(input)
}

Obsolete :: type expand remove {
    old :: Int
}

Tree :: type {
    Gone :: type expand remove { value :: Int },
    Kept :: type { value :: String }
}

Owner :: trait {
    gone :: Int -> Int expand remove { value => value }
    kept :: Bool -> Bool { value => value }
}

OldModule :: module expand remove_module {
    remove_module :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
        input => meta.remove_target(input)
    }
    stale :: Unit -> Int { _ => 1 }
}
""";

        var result = Compile("meta_syntax_remove_categories.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var root = Assert.IsType<ModuleDecl>(result.Ast);
        Assert.DoesNotContain(root.Declarations.OfType<AdtDef>(), static type => type.Name == "Obsolete");
        Assert.DoesNotContain(root.Declarations.OfType<ModuleDecl>(), static module =>
            module.Path.SequenceEqual(["OldModule"]));

        var tree = Assert.Single(root.Declarations.OfType<AdtDef>(), static type => type.Name == "Tree");
        Assert.Equal(["Kept"], tree.Cases.Select(static caseType => caseType.Name));
        Assert.Equal(["Kept"], tree.Constructors.Select(static constructor => constructor.Name));

        var owner = Assert.Single(root.Declarations.OfType<TraitDef>(), static trait => trait.Name == "Owner");
        Assert.Equal(["kept"], owner.Methods.Select(static method => method.Name));

        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        Assert.Null(table.Modules.LookupModuleByPath(["OldModule"]));
        Assert.DoesNotContain(table.Symbols.Values, static symbol =>
            symbol.Name is "Obsolete" or "Gone" or "gone" or "stale" or "remove_module");
        var treeSymbol = Assert.IsType<AdtSymbol>(table.GetSymbol(tree.SymbolId));
        Assert.Equal([Assert.Single(tree.Cases).SymbolId], treeSymbol.DirectCases);
        var ownerSymbol = Assert.IsType<TraitSymbol>(table.GetSymbol(owner.SymbolId));
        Assert.Equal([Assert.Single(owner.Methods).SymbolId], ownerSymbol.Methods);
    }

    [Fact]
    public void TransformationPreflight_RejectsOverlappingGeneratedInstanceWithoutPartialCommit()
    {
        const string source = """
Marker :: trait {
    marker :: Self -> String
}

User :: type {}

Existing :: instance Marker {
    marker :: User -> String { _ => "existing" }
}

emit_conflict :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => meta.add_after(input, [
        quote item { helper_must_not_exist :: Unit -> Int { _ => 1 } },
        quote item {
            Generated :: instance Marker {
                marker :: User -> String { _ => "generated" }
            }
        }
    ])
}

Seed :: type expand emit_conflict {}
""";

        var result = Compile("meta_generated_instance_coherence.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3616" &&
            diagnostic.Message.Contains("overlaps instance 'Existing'", StringComparison.Ordinal));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        Assert.DoesNotContain(module.Declarations.OfType<FuncDef>(), static function =>
            function.Name == "helper_must_not_exist");
        Assert.DoesNotContain(module.Declarations.OfType<InstanceDecl>(), static instance =>
            instance.Name == "Generated");
        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        Assert.DoesNotContain(table.Symbols.Values.OfType<FuncSymbol>(), static symbol =>
            symbol.Name == "helper_must_not_exist");
        Assert.DoesNotContain(table.Symbols.Values.OfType<ImplSymbol>(), static symbol =>
            symbol.Name == "Generated");
    }

    [Fact]
    public void TransformationPreflight_RejectsGeneratedInstanceOverlapWithoutPartialCommit()
    {
        const string source = """
Marker :: trait {
    mark :: Self -> Bool
}

Person :: type {}

Existing :: instance Marker {
    mark :: Person -> Bool { _ => true }
}

emit_conflict :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => meta.add_after(input, [quote item {
        Generated :: instance Marker {
            mark :: Person -> Bool { _ => false }
        }
    }])
}

Seed :: type expand emit_conflict {}
""";

        var result = Compile("meta_generated_function_impl_coherence.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3616" &&
            diagnostic.Message.Contains("overlaps instance 'Existing'", StringComparison.Ordinal));
        Assert.DoesNotContain(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<InstanceDecl>(),
            static instance => instance.Name == "Generated");
    }

    [Theory]
    [InlineData("Box[T] :: type {}", "T", "Box[T]")]
    [InlineData("Buffer[comptime N: Int] :: type {}", "comptime N: Int", "Buffer[N]")]
    [InlineData("Envelope[E: effects] :: type {}", "E: effects", "Envelope[E]")]
    public void TransformationPreflight_RejectsGeneratedGenericImplHeadOverlap(
        string carrierDeclaration,
        string instanceParameter,
        string carrierUse)
    {
        var source = """
Show :: trait {
    show :: Self -> String
}

CARRIER_DECLARATION

Existing[INSTANCE_PARAMETER] :: instance Show {
    show :: CARRIER_USE -> String { _ => "existing" }
}

emit_conflict :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => meta.add_after(input, [quote item {
        Generated[INSTANCE_PARAMETER] :: instance Show {
            show :: CARRIER_USE -> String { _ => "generated" }
        }
    }])
}

Seed :: type expand emit_conflict {}
"""
            .Replace("CARRIER_DECLARATION", carrierDeclaration, StringComparison.Ordinal)
            .Replace("INSTANCE_PARAMETER", instanceParameter, StringComparison.Ordinal)
            .Replace("CARRIER_USE", carrierUse, StringComparison.Ordinal);

        var result = Compile("meta_generated_generic_impl_coherence.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3616" &&
            diagnostic.Message.Contains("overlaps instance 'Existing'", StringComparison.Ordinal));
        Assert.DoesNotContain(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<InstanceDecl>(),
            static instance => instance.Name == "Generated");
    }

    [Theory]
    [InlineData("trait")]
    [InlineData("type")]
    [InlineData("case")]
    public void TransformationPreflight_RejectsNominalRemovalThatWouldInvalidateAnImpl(string targetKind)
    {
        var source = targetKind switch
        {
            "trait" => """
remove :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation { input => meta.remove_target(input) }
Marker :: trait expand remove { mark :: Self -> Bool }
Person :: type {}
Existing :: instance Marker { mark :: Person -> Bool { _ => true } }
""",
            "type" => """
remove :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation { input => meta.remove_target(input) }
Marker :: trait { mark :: Self -> Bool }
Person :: type expand remove {}
Existing :: instance Marker { mark :: Person -> Bool { _ => true } }
""",
            _ => """
remove :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation { input => meta.remove_target(input) }
Marker :: trait { mark :: Self -> Bool }
Tree :: type { Leaf :: type expand remove {} }
Existing :: instance Marker { mark :: Tree.Leaf -> Bool { _ => true } }
"""
        };

        var result = Compile($"meta_remove_impl_{targetKind}.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3615" &&
            diagnostic.Message.Contains("would invalidate existing", StringComparison.Ordinal));
        Assert.Single(Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<InstanceDecl>());
    }

    [Fact]
    public void TransformationPreflight_RejectsNominalReplacementThatWouldInvalidateAnImpl()
    {
        const string source = """
replace :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => meta.replace_target(input, quote item { Person :: type { value :: Int } })
}

Marker :: trait { mark :: Self -> Bool }
Person :: type expand replace {}
Existing :: instance Marker { mark :: Person -> Bool { _ => true } }
""";

        var result = Compile("meta_replace_impl_type.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3614" &&
            diagnostic.Message.Contains("would invalidate existing", StringComparison.Ordinal));
        var person = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<AdtDef>(),
            static type => type.Name == "Person");
        Assert.Empty(person.Fields);
    }

    [Fact]
    public void TransformationPreflight_RejectsRemovingARequiredInstanceMethodAtomically()
    {
        const string source = """
remove :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation { input => meta.remove_target(input) }
Marker :: trait { mark :: Self -> Bool }
Person :: type {}
Existing :: instance Marker {
    mark :: Person -> Bool expand remove { _ => true }
}
""";

        var result = Compile("meta_remove_required_instance_method.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3615" &&
            diagnostic.Message.Contains("incoherent", StringComparison.Ordinal));
        Assert.Single(
            Assert.Single(Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<InstanceDecl>()).Methods,
            static method => method.Name == "mark");
    }

    [Fact]
    public void TransformationPreflight_RejectsInvalidInstanceMethodReplacementAtomically()
    {
        const string source = """
replace :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => meta.replace_target(input, quote item {
        mark :: Person -> String { _ => "invalid" }
    })
}

Marker :: trait { mark :: Self -> Bool }
Person :: type {}
Existing :: instance Marker {
    mark :: Person -> Bool expand replace { _ => true }
}
""";

        var result = Compile("meta_replace_invalid_instance_method.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3614" &&
            diagnostic.Message.Contains("signature", StringComparison.Ordinal));
        var method = Assert.Single(
            Assert.Single(Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<InstanceDecl>()).Methods,
            static method => method.Name == "mark");
        Assert.Contains("Person", string.Join(" ", method.Signature.Select(static type => type.ToString())));
    }

    [Fact]
    public void SyntaxRemoval_RemovesASourceInstanceAndItsEvidence()
    {
        const string source = """
remove :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation { input => meta.remove_target(input) }
Marker :: trait { mark :: Self -> Bool }
Person :: type {}
MarkerPerson :: instance Marker expand remove {
    mark :: Person -> Bool { _ => true }
}
""";

        var result = Compile("meta_remove_function_impl.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        var markerId = table.LookupTrait("Marker");
        Assert.True(markerId.HasValue);
        Assert.Empty(table.GetImplsForTrait(markerId.Value));
        Assert.DoesNotContain(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<InstanceDecl>(),
            static instance => instance.Name == "MarkerPerson");
    }

    [Fact]
    public void GeneratorRequires_IsScopedToTheSameTargetAndStageDomain()
    {
        const string source = """
normalize :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    _ => meta.keep()
}

finalize :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation
    requires normalize
{
    input => meta.report([
        meta.diagnostic("warning", meta.span_of(input), "must not execute")
    ])
}

First :: type expand normalize {}
Second :: type expand finalize {}
""";

        var result = Compile("meta_ordering_target_domain.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3613" &&
            diagnostic.Message.Contains("requires expansion 'normalize'", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, static diagnostic =>
            diagnostic.Message == "must not execute");
    }

    [Fact]
    public void GeneratedReplacement_InheritsCompositionAncestorsForCycleDetection()
    {
        const string source = """
repeat :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => meta.replace_target(input, quote item {
        Seed :: type expand repeat {}
    })
}

Seed :: type expand repeat {}
""";

        var result = Compile("meta_generated_replacement_cycle.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3604" &&
            diagnostic.Message.Contains("expansion cycle", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "E3607");
    }

    [Fact]
    public void GeneratedReplacement_CanRequireAnAlreadyCompletedPassInItsLogicalTargetDomain()
    {
        const string source = """
finalize :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation
    requires normalize
{
    input => meta.report([
        meta.diagnostic("warning", meta.span_of(input), "finalized")
    ])
}

normalize :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => meta.replace_target(input, quote item {
        Seed :: type expand finalize {}
    })
}

Seed :: type expand normalize {}
""";

        var result = Compile("meta_generated_requires_completed.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "W3611" && diagnostic.Message == "finalized");
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "E3613");
    }

    [Fact]
    public void TransformationPreflight_RejectsTopLevelStageRegressionWithoutPartialCommit()
    {
        const string source = """
early :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    _ => meta.keep()
}

emit_regression :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => meta.add_after(input, [
        quote item { helper_must_not_exist :: Unit -> Int { _ => 1 } },
        quote item { Regressed :: type expand early {} }
    ])
}

Seed :: type expand emit_regression {}
""";

        var result = Compile("meta_stage_regression_top_level.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3616" &&
            diagnostic.Message.Contains("requested stage 'Syntax'", StringComparison.Ordinal));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        Assert.DoesNotContain(module.Declarations.OfType<FuncDef>(), static function =>
            function.Name == "helper_must_not_exist");
        Assert.DoesNotContain(module.Declarations.OfType<AdtDef>(), static type => type.Name == "Regressed");
    }

    [Fact]
    public void TransformationPreflight_RejectsNestedStageRegressionWithoutPartialCommit()
    {
        var nestedItems = new[]
        {
            "GeneratedType :: type { Child :: type expand early {} }",
            "GeneratedTrait :: trait { run :: Unit -> Unit expand early { value => value } }",
            "GeneratedModule :: module { Child :: type expand early {} }"
        };

        for (var index = 0; index < nestedItems.Length; index++)
        {
            var source = """
early :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    _ => meta.keep()
}

emit_regression :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => meta.add_after(input, [
        quote item { helper_must_not_exist :: Unit -> Int { _ => 1 } },
        quote item { GENERATED_ITEM }
    ])
}

Seed :: type expand emit_regression {}
""".Replace("GENERATED_ITEM", nestedItems[index], StringComparison.Ordinal);

            var result = Compile($"meta_stage_regression_nested_{index}.eidos", source);

            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, static diagnostic =>
                diagnostic.Code == "E3616" &&
                diagnostic.Message.Contains("requested stage 'Syntax'", StringComparison.Ordinal));
            var module = Assert.IsType<ModuleDecl>(result.Ast);
            Assert.DoesNotContain(module.Declarations.OfType<FuncDef>(), static function =>
                function.Name == "helper_must_not_exist");
            Assert.DoesNotContain(module.Declarations, static declaration => declaration switch
            {
                AdtDef type => type.Name == "GeneratedType",
                TraitDef trait => trait.Name == "GeneratedTrait",
                ModuleDecl nested => nested.Path.SequenceEqual(["GeneratedModule"]),
                _ => false
            });
        }
    }

    [Fact]
    public void FixedPoint_ReexecutesAnOccurrenceWhenItsCanonicalInputChanges()
    {
        const string source = """
observe :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => match meta.fields_of(meta.target_type_of(input)) {
        [] => meta.keep(),
        [_, .._] => meta.add_after(input, [meta.function("observed", [], Bool, meta.expr_bool(true))])
    }
}

add_late :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => meta.add_members(input, [quote member { late :: Int }])
}

Subject :: type expand observe expand add_late {}
""";

        var result = Compile("meta_fixed_point_changed_input.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        Assert.Single(module.Declarations.OfType<FuncDef>(), static function => function.Name == "observed");
        var subject = Assert.Single(module.Declarations.OfType<AdtDef>(), static type => type.Name == "Subject");
        Assert.Single(subject.Fields);
    }

    [Fact]
    public void FixedPoint_ReportsDeterministicIdentityConflictWhenPayloadChanges()
    {
        const string source = """
emit_answer :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => match meta.fields_of(meta.target_type_of(input)) {
        [] => meta.add_after(input, [
            meta.with_slot(meta.function("answer", [], Int, meta.expr_int(1)), meta.slot_from("answer"))
        ]),
        [_, .._] => meta.add_after(input, [
            meta.with_slot(meta.function("answer", [], Int, meta.expr_int(2)), meta.slot_from("answer"))
        ])
    }
}

add_late :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => meta.add_members(input, [quote member { late :: Int }])
}

Subject :: type expand emit_answer expand add_late {}
""";

        var result = Compile("meta_fixed_point_identity_conflict.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3605" &&
            diagnostic.Message.Contains("identity conflict", StringComparison.Ordinal));
        Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<FuncDef>(),
            static function => function.Name == "answer");
    }

    [Fact]
    public void StableSlot_TreatsTheSamePayloadAsANoOpAcrossFixedPointRounds()
    {
        const string source = """
emit_answer :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => match meta.fields_of(meta.target_type_of(input)) {
        [] => meta.add_after(input, [
            meta.with_slot(meta.function("answer", [], Int, meta.expr_int(1)), meta.slot_from("answer"))
        ]),
        [_, .._] => meta.add_after(input, [
            meta.with_slot(meta.function("answer", [], Int, meta.expr_int(1)), meta.slot_from("answer"))
        ])
    }
}

add_late :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => meta.add_members(input, [quote member { late :: Int }])
}

Subject :: type expand emit_answer expand add_late {}
""";

        var result = Compile("meta_fixed_point_slot_noop.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<FuncDef>(),
            static function => function.Name == "answer");
    }

    [Fact]
    public void StableSlot_PreservesMappedOutputIdentityWhenCollectionOrderChanges()
    {
        const string source = """
emit_fields :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => match meta.fields_of(meta.target_type_of(input)) {
        [first] => meta.add_after(input, [
            meta.with_slot(meta.function("from_first", [], Int, meta.expr_int(1)), meta.slot_from(first))
        ]),
        [first, second, .._] => meta.add_after(input, [
            meta.with_slot(meta.function("from_second", [], Int, meta.expr_int(2)), meta.slot_from(second)),
            meta.with_slot(meta.function("from_first", [], Int, meta.expr_int(1)), meta.slot_from(first))
        ]),
        _ => meta.keep()
    }
}

add_late :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => meta.add_members(input, [quote member { second :: Int }])
}

Subject :: type expand emit_fields expand add_late {
    first :: Int
}
""";

        var result = Compile("meta_stable_slot_reordered_collection.eidos", source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var functions = Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<FuncDef>().ToArray();
        Assert.Single(functions, static function => function.Name == "from_first");
        Assert.Single(functions, static function => function.Name == "from_second");
    }

    [Fact]
    public void StableSlot_RejectsDuplicateSlotsWithinOneTransformation()
    {
        const string source = """
duplicate_slots :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => meta.add_after(input, [
        meta.with_slot(meta.function("first", [], Int, meta.expr_int(1)), meta.slot_from("shared")),
        meta.with_slot(meta.function("second", [], Int, meta.expr_int(2)), meta.slot_from("shared"))
    ])
}

Seed :: type expand duplicate_slots {}
""";

        var result = Compile("meta_duplicate_generation_slot.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3602" &&
            diagnostic.Message.Contains("duplicate generation slot", StringComparison.Ordinal));
        Assert.DoesNotContain(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<FuncDef>(),
            static function => function.Name is "first" or "second");
    }

    [Theory]
    [InlineData("()")]
    [InlineData("meta.span_of(input)")]
    public void StableSlot_RejectsUnstableSlotSources(string slotSource)
    {
        var source = """
invalid_slot :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => meta.add_after(input, [
        meta.with_slot(meta.function("answer", [], Int, meta.expr_int(1)), meta.slot_from(SLOT_SOURCE))
    ])
}

Seed :: type expand invalid_slot {}
""".Replace("SLOT_SOURCE", slotSource, StringComparison.Ordinal);

        var result = Compile("meta_unstable_generation_slot.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3601" &&
            diagnostic.Message.Contains("canonical user scalar keys", StringComparison.Ordinal));
    }

    [Fact]
    public void FixedPoint_ReportsNonConvergenceBeforeTheResourceBudget()
    {
        const string source = """
grow :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => meta.add_members(input, [quote member { Spawn :: type expand grow {} }])
}

Seed :: type expand grow {}
""";

        var result = Compile("meta_fixed_point_non_convergent.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3617" &&
            diagnostic.Message.Contains("round 32", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("producer 'grow'", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("origin chain", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "E3607");
    }

    [Fact]
    public void GeneratedBeforeClause_RejectsAnAlreadyCompletedPass()
    {
        const string source = """
late :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation
    before normalize
{
    _ => meta.keep()
}

normalize :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => meta.replace_target(input, quote item {
        Seed :: type expand late {}
    })
}

Seed :: type expand normalize {}
""";

        var result = Compile("meta_generated_before_completed.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3612" &&
            diagnostic.Message.Contains("before already completed expansion 'normalize'", StringComparison.Ordinal));
    }

    [Fact]
    public void GeneratedOrderingClauses_ReportACycleInTheNewTargetDomain()
    {
        const string source = """
first :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation
    before second
{
    _ => meta.keep()
}

second :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation
    before first
{
    _ => meta.keep()
}

seed_pass :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => meta.replace_target(input, quote item {
        Seed :: type expand first expand second {}
    })
}

Seed :: type expand seed_pass {}
""";

        var result = Compile("meta_generated_ordering_cycle.eidos", source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3610" &&
            diagnostic.Message.Contains("ordering cycle", StringComparison.Ordinal));
    }
}
