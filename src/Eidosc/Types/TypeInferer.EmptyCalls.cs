using Eidosc.Ast;

namespace Eidosc.Types;

public sealed partial class TypeInferer
{
    private bool TryResolveEmptyCall(
        Type functionType,
        EidosAstNode? callee,
        Substitution substitution,
        out EmptyCallResolution resolution)
    {
        var resolvedType = substitution.Apply(functionType);
        if (resolvedType is not TyFun function)
        {
            resolution = default;
            return false;
        }

        if (function.Params.Count == 0)
        {
            resolution = new EmptyCallResolution(
                EmptyCallResolutionKind.ZeroArgument,
                substitution.Apply(function.Result),
                SynthesizedUnitArgumentCount: 0);
            return true;
        }

        var firstParam = substitution.Apply(function.Params[0]);
        if (!IsUnitType(firstParam))
        {
            resolution = default;
            return false;
        }

        var resultType = function.Params.Count == 1
            ? function.Result
            : new TyFun
            {
                Params = CopyParamsFrom(function.Params, 1),
                Result = function.Result,
                Effects = function.Effects
            };

        var kind = IsExternalFfiCallee(callee)
            ? EmptyCallResolutionKind.FfiUnitElision
            : EmptyCallResolutionKind.UnitSugar;
        resolution = new EmptyCallResolution(
            kind,
            substitution.Apply(resultType),
            kind == EmptyCallResolutionKind.UnitSugar ? 1 : 0);
        return true;
    }

    private static List<Type> CopyParamsFrom(IReadOnlyList<Type> parameters, int startIndex)
    {
        if (startIndex >= parameters.Count)
        {
            return [];
        }

        var result = new List<Type>(parameters.Count - startIndex);
        for (var i = startIndex; i < parameters.Count; i++)
        {
            result.Add(parameters[i]);
        }

        return result;
    }

    private static List<Type> CopyParamsFrom(IReadOnlyList<Type> parameters, int startIndex, int count)
    {
        if (count <= 0 || startIndex >= parameters.Count)
        {
            return [];
        }

        var endExclusive = Math.Min(parameters.Count, startIndex + count);
        var result = new List<Type>(endExclusive - startIndex);
        for (var i = startIndex; i < endExclusive; i++)
        {
            result.Add(parameters[i]);
        }

        return result;
    }
}
