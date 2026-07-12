namespace Eidosc.Types;

public sealed record FunctionEffectSummary(
    EffectRow DeclaredUpperBound,
    EffectRow InferredEffects);
