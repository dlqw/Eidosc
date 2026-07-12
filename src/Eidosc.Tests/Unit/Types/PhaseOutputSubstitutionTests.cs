using Eidosc.Ast.Declarations;
using Eidosc.Pipeline;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

public class PhaseOutputSubstitutionTests
{
    [Fact]
    public void FormatSubstitution_EmitsRawResolvedStatusAndChain()
    {
        var substitution = new Substitution();
        var t0 = substitution.FreshTypeVariable();
        var t1 = substitution.FreshTypeVariable();

        substitution.Unify(t0, t1);
        substitution.Unify(t1, BaseTypes.Int);

        var output = TypeFormatter.FormatSubstitution(substitution);

        Assert.Contains("// 绑定数: 2", output);
        Assert.Contains("'t0", output);
        Assert.Contains("rewritten", output);
        Assert.Contains("//   chain:", output);
        Assert.Contains("Int", output);
    }

    [Fact]
    public void FormatSubstitution_WithAstContext_EmitsContextLines()
    {
        var substitution = new Substitution();
        var t0 = substitution.FreshTypeVariable();
        substitution.Unify(t0, BaseTypes.Bool);

        var module = new ModuleDecl
        {
            InferredType = t0
        };

        var output = TypeFormatter.FormatSubstitution(substitution, module);

        Assert.Contains("'t0", output);
        Assert.Contains("//   context: ModuleDecl @", output);
    }
}
