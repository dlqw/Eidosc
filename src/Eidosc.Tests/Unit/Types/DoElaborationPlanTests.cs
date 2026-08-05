using Eidosc.Ast.Expressions;
using Eidosc.Hir;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Eidosc.Tests.Fixtures;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

public sealed class DoElaborationPlanTests
{
    private static readonly TestPathConfig Paths = TestPathConfig.Current;

    [Fact]
    public void Infer_IndependentPureCopyBindings_SelectsApplicativeThenJoin()
    {
        const string source = """
import std.Option
import std.Monad

main :: Unit -> Option[Int]
{
    _ => do {
        x <- Some(2)
        y <- Some(3)
        Some(x + y)
    }
}
""";

        var result = RunTypes(source, "do_applicative_independent.eidos");

        Assert.True(result.Success, FormatDiagnostics(result));
        var expression = Assert.Single(AstNodeCollector<DoExpr>.Collect(Assert.IsType<Eidosc.Ast.Declarations.ModuleDecl>(result.Ast)));
        var plan = Assert.IsType<DoElaborationPlan>(expression.ElaborationPlan);
        Assert.True(plan.HasApplicativeEvidence);
        var segment = Assert.Single(plan.Segments);
        Assert.Equal(0, segment.StartIndex);
        Assert.Equal(2, segment.Count);
        Assert.Equal(DoElaborationStrategy.ApplicativeThenJoin, segment.Strategy);
        Assert.Equal("proven-independent-pure-copy-bindings", segment.ReasonCode);
        Assert.True(plan.IsCurrent(expression));
        Assert.Equal(3, plan.Steps.Count);
        Assert.Empty(plan.DependencyEdges);
        Assert.Collection(
            plan.Evidence,
            monad =>
            {
                Assert.Equal("Monad", monad.TraitName);
                Assert.Equal("__eidos_prelude_core.Option.MonadOption", monad.InstanceIdentity);
                Assert.NotEmpty(monad.CanonicalGoal);
            },
            functor => Assert.Equal("__eidos_prelude_core.Option.FunctorOption", functor.InstanceIdentity),
            applicative => Assert.Equal("__eidos_prelude_core.Option.ApplicativeOption", applicative.InstanceIdentity));
    }

    [Fact]
    public void Infer_DependentBinding_SplitsApplicativeSegments()
    {
        const string source = """
import std.Option
import std.Monad

main :: Unit -> Option[Int]
{
    _ => do {
        x <- Some(2)
        y <- Some(x + 1)
        Some(x + y)
    }
}
""";

        var result = RunTypes(source, "do_applicative_dependent.eidos");

        Assert.True(result.Success, FormatDiagnostics(result));
        var expression = Assert.Single(AstNodeCollector<DoExpr>.Collect(Assert.IsType<Eidosc.Ast.Declarations.ModuleDecl>(result.Ast)));
        var plan = Assert.IsType<DoElaborationPlan>(expression.ElaborationPlan);
        Assert.Collection(
            plan.Segments,
            first =>
            {
                Assert.Equal(0, first.StartIndex);
                Assert.Equal(1, first.Count);
                Assert.Equal(DoElaborationStrategy.Monad, first.Strategy);
            },
            second =>
            {
                Assert.Equal(1, second.StartIndex);
                Assert.Equal(1, second.Count);
                Assert.Equal(DoElaborationStrategy.Monad, second.Strategy);
            });
        var dependency = Assert.Single(plan.DependencyEdges);
        Assert.Equal(0, dependency.ProducerBindingIndex);
        Assert.Equal(1, dependency.ConsumerBindingIndex);
        Assert.True(dependency.Symbol.IsValid);
    }

    [Fact]
    public void Infer_EffectfulIndependentBindings_KeepMonadOrder()
    {
        const string source = """
import std.Option
import std.Monad

next_option :: Unit -> Option[Int] need ffi
{
    _ => Some(2)
}

main :: Unit -> Option[Int] need ffi
{
    _ => do {
        x <- next_option()
        y <- next_option()
        Some(x + y)
    }
}
""";

        var result = RunTypes(source, "do_applicative_effect_order.eidos");

        Assert.True(result.Success, FormatDiagnostics(result));
        var expression = Assert.Single(AstNodeCollector<DoExpr>.Collect(Assert.IsType<Eidosc.Ast.Declarations.ModuleDecl>(result.Ast)));
        var segment = Assert.Single(Assert.IsType<DoElaborationPlan>(expression.ElaborationPlan).Segments);
        Assert.Equal(DoElaborationStrategy.Monad, segment.Strategy);
        Assert.Equal("effect-order-not-proven", segment.ReasonCode);
    }

    [Fact]
    public void Infer_NonCopyIndependentBindings_KeepMonadOwnershipOrder()
    {
        const string source = """
import std.Option
import std.Monad

main :: Unit -> Option[String]
{
    _ => do {
        x <- Some("left")
        y <- Some("right")
        Some(x ++ y)
    }
}
""";

        var result = RunTypes(source, "do_applicative_non_copy.eidos");

        Assert.True(result.Success, FormatDiagnostics(result));
        var expression = Assert.Single(AstNodeCollector<DoExpr>.Collect(Assert.IsType<Eidosc.Ast.Declarations.ModuleDecl>(result.Ast)));
        var segment = Assert.Single(Assert.IsType<DoElaborationPlan>(expression.ElaborationPlan).Segments);
        Assert.Equal(DoElaborationStrategy.Monad, segment.Strategy);
        Assert.Equal("ownership-reuse-not-proven", segment.ReasonCode);
    }

    [Fact]
    public void Infer_RefutableResultBindingWithoutAlternativeEvidence_IsRejected()
    {
        const string source = """
import std.Result
import std.Monad

main :: Unit -> Result[Int, String]
{
    _ => do {
        7 <- Ok(7)
        Ok(7)
    }
}
""";

        var result = RunTypes(source, "do_result_missing_alternative.eidos");

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("does not implement trait 'Alternative'", StringComparison.Ordinal));
    }

    [Fact]
    public void Infer_SeqOfOption_PreservesNestedElementType()
    {
        const string source = """
import std.Option
import std.Seq
import std.Monad

options :: Unit -> Seq[Option[Int]]
{
    _ => [Some(1)]
}

main :: Unit -> Seq[Int]
{
    _ => do {
        bound <- options();
        [1]
    }
}
""";

        var result = RunTypes(source, "do_seq_option_element.eidos");

        Assert.True(result.Success, FormatDiagnostics(result));
        var expression = Assert.Single(AstNodeCollector<DoExpr>.Collect(Assert.IsType<Eidosc.Ast.Declarations.ModuleDecl>(result.Ast)));
        var firstStep = Assert.IsType<DoElaborationPlan>(expression.ElaborationPlan).Steps[0];
        Assert.Contains("Option", firstStep.OutputTypeIdentity, StringComparison.Ordinal);
    }

    [Fact]
    public void Infer_DoWithoutMonadEvidence_IsRejectedDuringTypes()
    {
        const string source = """
import std.Monad

Box[A] :: type { Box:: type(A) }

main :: Unit -> Box[Int]
{
    _ => do {
        x <- Box(2)
        Box(x)
    }
}
""";

        var result = RunTypes(source, "do_missing_monad.eidos");

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("does not implement trait 'Monad'", StringComparison.Ordinal));
    }

    [Fact]
    public void PreludeCoreImage_ExportsFunctionalInstanceHeads()
    {
        var indexErrors = PreludeCoreImageRegistry.ValidateInstanceModuleIndex();
        Assert.True(indexErrors.Count == 0, string.Join(Environment.NewLine, indexErrors));

        var option = new TyCon { Name = "Option" };

        Assert.Equal(
            ["__eidos_prelude_core.Option.MonadOption"],
            PreludeCoreImageRegistry.GetInstanceCandidates("Monad", option));
        Assert.Equal(
            ["__eidos_prelude_core.Option.ApplicativeOption"],
            PreludeCoreImageRegistry.GetInstanceCandidates("Applicative", option));
        Assert.Equal(
            ["__eidos_prelude_core.Option.FunctorOption"],
            PreludeCoreImageRegistry.GetInstanceCandidates("Functor", option));

        var resultWithString = new TyCon { Name = "With", Args = [BaseTypes.String] };
        Assert.Equal(
            ["__eidos_prelude_core.Result.MonadResultWithE"],
            PreludeCoreImageRegistry.GetInstanceCandidates("Monad", resultWithString));
        Assert.Empty(PreludeCoreImageRegistry.GetInstanceCandidates(
            "Monad",
            new TyCon { Name = "Option", Args = [BaseTypes.Int] }));
    }

    private static CompilationResult RunTypes(string source, string fileName)
    {
        return new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = TestSourceLoader.GetFullPath(Paths.Fixture("basic/literals.eidos")),
            LanguageVersion = TestSourceLoader.GetLanguageVersion(Paths.Fixture("basic/literals.eidos")),
            StopAtPhase = CompilationPhase.Types,
            UseColors = false,
            PackageImportRoots = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [WellKnownStrings.Std.Module] = []
            }
        }).Run();
    }

    private static string FormatDiagnostics(CompilationResult result) => string.Join(
        Environment.NewLine,
        result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
}
