namespace Eidosc.Types;

/// <summary>
/// Immutable identity for an open effect-row variable.
/// Bindings are owned by an <see cref="EffectUnifier"/>, never by the variable.
/// </summary>
public sealed record EffectVariable
{
    public required int Id { get; init; }

    public string Name => $"e{Id}";

    public override string ToString() => Name;
}

public sealed class EffectVariableGenerator
{
    private int _nextId;

    public EffectVariable Fresh() => new() { Id = _nextId++ };
}
