using Eidosc.Ast.Expressions;
using Eidosc.Diagnostic;
using Eidosc.Symbols;

namespace Eidosc.Types;

public sealed partial class TypeInferer
{
    private Type InferContextualRecordLiteralWithoutExpectedType(ContextualRecordLiteralExpr literal)
    {
        AddError(
            literal.Span,
            "Cannot infer the type of contextual record literal; add a type annotation or write the constructor explicitly.");

        foreach (var field in literal.NamedArgs)
        {
            _ = AddFieldInitType(field, new Dictionary<string, Type>(StringComparer.Ordinal));
        }

        return CreateErrorRecoveryType();
    }

    private Type InferContextualRecordLiteral(ContextualRecordLiteralExpr literal, Type expectedType)
    {
        var resolvedExpected = _substitution.Apply(expectedType);
        if (ContainsErrorRecoveryType(resolvedExpected))
        {
            foreach (var field in literal.NamedArgs)
            {
                _ = AddFieldInitType(field, new Dictionary<string, Type>(StringComparer.Ordinal));
            }

            return CreateErrorRecoveryType();
        }

        if (resolvedExpected is not TyCon { Symbol.IsValid: true } expectedCon)
        {
            AddError(
                literal.Span,
                $"Contextual record literal requires a record-style ADT expected type; expected type is {resolvedExpected}.");
            return CreateErrorRecoveryType();
        }

        var bindings = GetRecordCtorTypeBindings(expectedCon.Symbol);
        if (bindings.Count == 0)
        {
            AddError(literal.Span, $"Type '{expectedCon.Name}' has no record-style constructor for contextual record literal.");
            return CreateErrorRecoveryType();
        }

        var explicitFieldNames = literal.NamedArgs
            .Where(static field => !string.IsNullOrWhiteSpace(field.FieldName))
            .Select(static field => field.FieldName)
            .ToHashSet(StringComparer.Ordinal);
        var candidates = bindings
            .Where(binding => explicitFieldNames.All(binding.NamedArgTypes.ContainsKey))
            .ToList();

        if (candidates.Count != 1)
        {
            AddError(
                literal.Span,
                candidates.Count == 0
                    ? $"Contextual record literal fields do not match any record constructor for {expectedCon.Name}."
                    : $"Contextual record literal is ambiguous for {expectedCon.Name}; write the constructor explicitly.");
            return CreateErrorRecoveryType();
        }

        var binding = candidates[0];
        var missingFields = binding.NamedArgTypes.Keys
            .Where(fieldName => !explicitFieldNames.Contains(fieldName))
            .ToList();
        if (missingFields.Count > 0)
        {
            AddError(
                literal.Span,
                $"Contextual record literal is missing field '{missingFields[0]}'.");
            return CreateErrorRecoveryType();
        }

        var typeVarEnv = CreateCtorTypeVarEnv(binding, expectedCon.Args, expectedCon.ValueArgs);
        var ctor = CreateDesugaredContextualRecordLiteralCtor(literal, binding);
        var namedArgTypes = new Dictionary<string, Type>(StringComparer.Ordinal);
        var hasRecovery = false;
        foreach (var field in ctor.NamedArgs)
        {
            hasRecovery |= AddFieldInitType(field, namedArgTypes);
        }

        UnifyCtorArgumentTypes(binding, typeVarEnv, [], namedArgTypes);
        ApplyAdtTypeParamConstraints(binding.AdtId, typeVarEnv, literal.Span);
        ApplyConstructorTypeParamConstraints(binding, typeVarEnv, literal.Span);

        var resultType = CreateAdtTypeFromBinding(binding, typeVarEnv, literal.Span);
        var unified = TryUnify(resolvedExpected, resultType, literal.Span, DiagnosticMessages.LetPatternTypeMismatch);
        var resolved = _substitution.Apply(unified);
        ctor.InferredType = resolved;
        literal.SetDesugaredCtor(ctor);
        return hasRecovery || ContainsErrorRecoveryType(resolved)
            ? CreateErrorRecoveryType()
            : resolved;
    }

    private CtorExpr CreateDesugaredContextualRecordLiteralCtor(ContextualRecordLiteralExpr literal, CtorTypeBinding binding)
    {
        var ctor = new CtorExpr();
        ctor.SetSpan(literal.Span);
        ctor.SymbolId = binding.CtorId;
        ctor.SetConstructorName(_symbolTable.GetSymbol<CtorSymbol>(binding.CtorId)?.Name ?? "");

        foreach (var field in literal.NamedArgs)
        {
            ctor.AddNamedArg(field);
        }

        return ctor;
    }
}
