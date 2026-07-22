using Eidosc.Ast.Types;

namespace Eidosc.Ast.Declarations;

internal static class ClosedCaseConstructorProjection
{
    public static IReadOnlyList<Constructor> Create(
        CaseTypeDef caseType,
        IReadOnlyList<Field> inheritedFields,
        IReadOnlyList<TypeParam> inheritedTypeParams)
    {
        ArgumentNullException.ThrowIfNull(caseType);
        ArgumentNullException.ThrowIfNull(inheritedFields);
        ArgumentNullException.ThrowIfNull(inheritedTypeParams);

        var constructors = new List<Constructor>();
        AddLeafProjections(caseType, inheritedFields, inheritedTypeParams, constructors);
        return constructors;
    }

    private static void AddLeafProjections(
        CaseTypeDef caseType,
        IReadOnlyList<Field> inheritedFields,
        IReadOnlyList<TypeParam> inheritedTypeParams,
        List<Constructor> constructors)
    {
        var effectiveFields = inheritedFields.Concat(caseType.Fields).ToList();
        var effectiveTypeParams = inheritedTypeParams.Concat(caseType.TypeParams).ToList();
        if (!caseType.IsLeaf)
        {
            foreach (var child in caseType.Cases)
            {
                AddLeafProjections(child, effectiveFields, effectiveTypeParams, constructors);
            }
            return;
        }

        var constructor = new Constructor();
        constructor.SetName(caseType.Name);
        constructor.SetTypeParams(effectiveTypeParams);
        constructor.SetSpan(caseType.Span);
        foreach (var positional in caseType.PositionalFields)
        {
            constructor.AddPositionalArg(positional);
        }
        foreach (var field in effectiveFields)
        {
            constructor.AddNamedArg(field);
        }
        constructor.SetReturnType(caseType.ParentSpecialization);
        constructors.Add(constructor);
    }
}
