using Eidosc.Types;

namespace Eidosc.Tests.Unit.Types;

public sealed class EffectVariableUnifierTests
{
    private static readonly EffectTag IO = new() { Name = "IO" };
    private static readonly EffectTag Alloc = new() { Name = "Alloc" };

    [Fact]
    public void Bind_KeepsVariableImmutableAndStoresBindingInSolver()
    {
        var variable = new EffectVariable { Id = 1 };
        var unifier = new EffectUnifier();

        Assert.True(unifier.Bind(variable, new EffectRow([IO])));

        Assert.Equal(1, variable.Id);
        Assert.Contains(IO, unifier.ApplySubstitution(EffectRow.FromEffectVariable(variable)).Effects);
    }

    [Fact]
    public void Bind_RepeatedRequirementsAccumulateInSolverBinding()
    {
        var variable = new EffectVariable { Id = 2 };
        var unifier = new EffectUnifier();

        Assert.True(unifier.Bind(variable, new EffectRow([IO])));
        Assert.True(unifier.Bind(variable, new EffectRow([Alloc])));

        var resolved = unifier.ApplySubstitution(EffectRow.FromEffectVariable(variable));
        Assert.Equal(2, resolved.Effects.Count);
        Assert.Contains(IO, resolved.Effects);
        Assert.Contains(Alloc, resolved.Effects);
    }

    [Fact]
    public void ApplySubstitution_PreservesIndependentOpenVariables()
    {
        var first = new EffectVariable { Id = 3 };
        var second = new EffectVariable { Id = 4 };
        var unifier = new EffectUnifier();
        var row = EffectRow.FromEffectVariable(first).Union(EffectRow.FromEffectVariable(second));

        Assert.True(unifier.Bind(first, new EffectRow([IO])));

        var resolved = unifier.ApplySubstitution(row);
        Assert.Contains(IO, resolved.Effects);
        Assert.Contains(second, resolved.Variables);
        Assert.DoesNotContain(first, resolved.Variables);
    }

    [Fact]
    public void Bind_RejectsRecursiveRow()
    {
        var variable = new EffectVariable { Id = 5 };
        var unifier = new EffectUnifier();

        Assert.False(unifier.Bind(variable, new EffectRow([IO], variable)));
        Assert.Empty(unifier.GetEffectBindings());
    }
}
