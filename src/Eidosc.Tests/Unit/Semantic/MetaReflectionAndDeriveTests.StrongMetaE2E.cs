using Eidosc.Ast.Declarations;
using Eidosc.Pipeline;
using Eidosc.Symbols;

namespace Eidosc.Tests.Unit.Semantic;

public sealed partial class MetaReflectionAndDeriveTests
{
    [Fact]
    public void Semantic_transform_reads_closed_case_gadt_trait_and_effect_facts_and_emits_impl_and_module()
    {
        const string source = """
io :: effect;

Printable :: trait {
    show :: Self -> String need io
}

inspect_expr :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    target => {
        root := meta.target_type_of(target);
        leaves := meta.leaf_cases_of(root);
        first := leaves[0];
        second := leaves[1];
        first_fields := meta.fields_of(first);
        parent := meta.parent_type_of(first);
        joined := meta.join_type_of(first, second);
        subtype := meta.is_subtype(first, parent);
        trait_shape := meta.shape_of(Printable);
        required_effects := meta.effects_of(meta.declaration_of(Printable.show));
        effect_name := meta.name_of(required_effects[0]);

        instance_name := meta.identifier("GeneratedPrintableExpr", meta.IdentifierCategory.Item);
        method_name := meta.identifier("show", meta.IdentifierCategory.Function);
        module_name := meta.identifier("GeneratedExprMeta", meta.IdentifierCategory.Module);
        summary_name := meta.identifier("summary", meta.IdentifierCategory.Function);

        meta.add_after(target, [
            quote item {
                $(instance_name) :: instance Printable {
                    $(method_name) :: Expr[Int] -> String need io {
                        _ => $(effect_name)
                    }
                }
            },
            quote item {
                $(module_name) :: module {
                    $(summary_name) :: Unit -> String {
                        _ => $(meta.name_of(joined))
                    }
                }
            }
        ])
    }
}

Expr[T] :: type expand inspect_expr {
    span :: Int,

    IntLiteral :: type case Expr[Int] {
        value :: Int,
    },
    Add :: type case Expr[Int] {
        left :: Int,
        right :: Int,
    },
    Text :: type case Expr[String] {
        value :: String,
    },
}

read_summary :: Unit -> String {
    _ => GeneratedExprMeta.summary()
}
""";

        var result = Compile("meta_strong_e2e_closed_case.eidos", source, options =>
        {
            options.StopAtPhase = CompilationPhase.Types;
            options.TraceComptime = true;
        });

        Assert.True(result.Success, FormatDiagnostics(result));
        var root = Assert.IsType<ModuleDecl>(result.Ast);
        var generatedInstance = Assert.Single(
            root.Declarations.OfType<InstanceDecl>(),
            static instance => instance.Name == "GeneratedPrintableExpr");
        var generatedMethod = Assert.Single(generatedInstance.Methods);
        Assert.Equal("show", generatedMethod.Name);
        Assert.Contains(generatedMethod.RequiredAbilities, static effect => effect.Path.SequenceEqual(["io"]));
        Assert.NotEmpty(generatedInstance.GeneratedOriginChain);

        var generatedModule = Assert.Single(
            root.Declarations.OfType<ModuleDecl>(),
            static module => module.Path.LastOrDefault() == "GeneratedExprMeta");
        var summary = Assert.Single(generatedModule.Declarations.OfType<FuncDef>());
        Assert.Equal("summary", summary.Name);
        Assert.True(summary.SymbolId.IsValid);
        Assert.NotEmpty(generatedModule.GeneratedOriginChain);

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        Assert.True(symbolTable.PathResolver.Resolve(
            ["GeneratedExprMeta", "summary"],
            root.SymbolId).IsSuccess);
        Assert.Contains(symbolTable.Symbols.Values.OfType<ImplSymbol>(), static instance =>
            instance.Name == "GeneratedPrintableExpr" && instance.GeneratedOrigin != null);

        var operations = result.ComptimeTrace
            .Where(static entry => entry.Kind == "query-cache")
            .Select(static entry => entry.Operation)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("meta.leaf_cases_of", operations);
        Assert.Contains("meta.fields_of", operations);
        Assert.Contains("meta.parent_type_of", operations);
        Assert.Contains("meta.join_type_of", operations);
        Assert.Contains("meta.is_subtype", operations);
        Assert.Contains("meta.shape_of", operations);
        Assert.Contains("meta.effects_of", operations);
    }
}
