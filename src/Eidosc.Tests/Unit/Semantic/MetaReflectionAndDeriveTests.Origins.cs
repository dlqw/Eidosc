using System.Text.Json;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Patterns;
using Eidosc.Hir;
using Eidosc.Pipeline;
using Eidosc.Symbols;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public sealed partial class MetaReflectionAndDeriveTests
{
    [Fact]
    public void GeneratedOriginChain_PropagatesAcrossAstHirXmlAndHirState()
    {
        const string source = """
generate_answer :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => meta.add_after(input, [meta.comptime_value("ANSWER", Int, meta.expr_int(42))])
}

Subject :: type expand generate_answer {}
""";

        var result = Compile("meta_generated_origin_chain.eidos", source, options =>
            options.StopAtPhase = CompilationPhase.Hir);

        Assert.True(result.Success, FormatDiagnostics(result));
        var astModule = Assert.IsType<ModuleDecl>(result.Ast);
        var generatedAst = Assert.Single(astModule.Declarations.OfType<LetDecl>(), static declaration =>
            declaration.Pattern is VarPattern { Name: "ANSWER" });
        var astNodes = EnumerateAstNodes(generatedAst).ToArray();
        Assert.NotEmpty(astNodes);
        Assert.All(astNodes, static node => Assert.Single(node.GeneratedOriginChain));

        var origin = Assert.Single(generatedAst.GeneratedOriginChain);
        var xml = generatedAst.ToXml();
        Assert.Contains("GeneratedOriginChain", xml, StringComparison.Ordinal);
        Assert.Contains(origin.StableIdentity, xml, StringComparison.Ordinal);
        Assert.Contains($"metaSchemaVersion=\"{origin.MetaSchemaVersion}\"", xml, StringComparison.Ordinal);
        Assert.Contains($"argumentsHash=\"{origin.CanonicalArgumentsHash}\"", xml, StringComparison.Ordinal);

        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var generatedHir = Assert.Single(hirModule.Declarations.OfType<HirVal>(), static declaration =>
            declaration.Name == "ANSWER");
        AssertOriginChain([origin], generatedHir.GeneratedOriginChain);
        AssertOriginChain([origin], generatedHir.Initializer.GeneratedOriginChain);
        AssertOriginChain([origin], generatedHir.Pattern.GeneratedOriginChain);

        var payloadModule = new HirModule
        {
            Name = "OriginRoundTrip",
            Path = ["OriginRoundTrip"],
            Declarations = [generatedHir]
        };
        var payload = ModuleHirStatePayload.Create(payloadModule);
        Assert.True(payload.IsRestorable);
        var serialized = JsonSerializer.Serialize(payload);
        var restoredPayload = Assert.IsType<ModuleHirStatePayload>(
            JsonSerializer.Deserialize<ModuleHirStatePayload>(serialized));
        Assert.True(restoredPayload.TryRestore(out var restoredModule));
        var restored = Assert.Single(restoredModule.Declarations.OfType<HirVal>());
        AssertOriginChain([origin], restored.GeneratedOriginChain);
        AssertOriginChain([origin], restored.Initializer.GeneratedOriginChain);
        AssertOriginChain([origin], restored.Pattern.GeneratedOriginChain);
    }

    [Fact]
    public void NestedGeneratedTarget_AppendsAncestorOriginBeforeCurrentOrigin()
    {
        const string source = """
generate_nested_output :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => meta.add_after(input, [meta.function("nested_output", [], Int, meta.expr_int(7))])
}

generate_nested_target :: comptime meta.Target[meta.Stage.Syntax] -> meta.Transformation {
    input => meta.add_after(input, [quote item {
        Nested :: type expand generate_nested_output {}
    }])
}

Seed :: type expand generate_nested_target {}
""";

        var result = Compile("meta_nested_generated_origin_chain.eidos", source, options =>
            options.StopAtPhase = CompilationPhase.Hir);

        Assert.True(result.Success, FormatDiagnostics(result));
        var astModule = Assert.IsType<ModuleDecl>(result.Ast);
        var nestedTarget = Assert.Single(astModule.Declarations.OfType<AdtDef>(), static declaration =>
            declaration.Name == "Nested");
        var nestedOutput = Assert.Single(astModule.Declarations.OfType<FuncDef>(), static declaration =>
            declaration.Name == "nested_output");
        var ancestor = Assert.Single(nestedTarget.GeneratedOriginChain);
        Assert.Equal(2, nestedOutput.GeneratedOriginChain.Count);
        Assert.Equal(ancestor.StableIdentity, nestedOutput.GeneratedOriginChain[0].StableIdentity);
        Assert.NotEqual(
            nestedOutput.GeneratedOriginChain[0].StableIdentity,
            nestedOutput.GeneratedOriginChain[1].StableIdentity);
        Assert.All(EnumerateAstNodes(nestedOutput), static node => Assert.Equal(2, node.GeneratedOriginChain.Count));

        var hirModule = Assert.IsType<HirModule>(result.HirModule);
        var hirTarget = Assert.Single(hirModule.Declarations.OfType<HirAdt>(), static declaration =>
            declaration.Name == "Nested");
        var hirOutput = Assert.Single(hirModule.Declarations.OfType<HirFunc>(), static declaration =>
            declaration.Name == "nested_output");
        AssertOriginChain(nestedTarget.GeneratedOriginChain, hirTarget.GeneratedOriginChain);
        AssertOriginChain(nestedOutput.GeneratedOriginChain, hirOutput.GeneratedOriginChain);
        Assert.NotNull(hirOutput.Body);
        AssertOriginChain(nestedOutput.GeneratedOriginChain, hirOutput.Body!.GeneratedOriginChain);
    }

    private static IEnumerable<EidosAstNode> EnumerateAstNodes(EidosAstNode root)
    {
        var pending = new Stack<EidosAstNode>();
        var visited = new HashSet<EidosAstNode>(ReferenceEqualityComparer.Instance);
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (!visited.Add(node))
            {
                continue;
            }

            yield return node;
            foreach (var child in AstStableNodeTraversal.GetStructuralChildren(node))
            {
                pending.Push(child);
            }
        }
    }

    private static void AssertOriginChain(
        IReadOnlyList<GeneratedDeclarationOrigin> expected,
        IReadOnlyList<GeneratedDeclarationOrigin> actual) =>
        Assert.Equal(
            expected.Select(GeneratedDeclarationOriginPayload.Create).ToArray(),
            actual.Select(GeneratedDeclarationOriginPayload.Create).ToArray());
}
