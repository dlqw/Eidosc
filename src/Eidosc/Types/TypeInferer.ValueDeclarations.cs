using Eidosc.Symbols;
using Eidosc.Ast.Declarations;
using Eidosc.Diagnostic;
using Eidosc.Semantic;
using Eidosc.Utils;

namespace Eidosc.Types;

public sealed partial class TypeInferer
{
    /// <summary>
    /// 推断 let 模式绑定的类型
    /// </summary>
    private void InferLetDecl(LetDecl letDecl)
    {
        var type = letDecl.Value == null
            ? CreateMissingInitializerRecoveryType("Let binding", null, letDecl.Span)
            : letDecl.TypeAnnotation != null
                ? ConvertTypeInCurrentTypeParamContext(letDecl.TypeAnnotation)
                : _substitution.FreshTypeVariable();

        Type valueType;
        if (letDecl.Value != null)
        {
            var savedAllowComptimeFunctionReferences = _allowComptimeFunctionReferences;
            _allowComptimeFunctionReferences = savedAllowComptimeFunctionReferences || letDecl.IsComptime;
            try
            {
                valueType = letDecl.TypeAnnotation != null
                    ? InferExpressionWithExpectedType(letDecl.Value, type)
                    : SafeInferExpression(letDecl.Value);
            }
            finally
            {
                _allowComptimeFunctionReferences = savedAllowComptimeFunctionReferences;
            }
        }
        else
        {
            valueType = type;
        }

        if (letDecl.Value != null)
        {
            if (!TryInsertAutoDeref(type, valueType, letDecl.Value, letDecl.SetValue))
            {
                valueType = TryUnify(type, valueType, letDecl.Value.Span, DiagnosticMessages.LetPatternTypeMismatch);
            }

            _constraintGenerator.Generate(letDecl.Value);

            if (letDecl.IsComptime)
            {
                var evaluated = ComptimeEvaluator.TryEvaluate(
                    letDecl.Value,
                    _comptimeValues,
                    _functionDefinitionsBySymbol,
                    _substitution.Apply,
                    CreateMetaComptimeContext($"comptime binding at {letDecl.Span}"),
                    out var comptimeValue,
                    out var reason);
                if (!evaluated)
                {
                    var code = reason.Contains("not complete in the reflection phase", StringComparison.Ordinal)
                        ? "E4016"
                        : TypeErrorCode;
                    AddError(
                        letDecl.Value.Span,
                        DiagnosticMessages.ComptimeBindingRhsMustBeEvaluable(reason),
                        code);
                }
                else if (letDecl.SymbolId.IsValid)
                {
                    _comptimeValues[letDecl.SymbolId] = comptimeValue;
                }
            }
        }

        if (letDecl.Pattern != null)
        {
            var patternType = InferPattern(letDecl.Pattern, valueType);
            valueType = TryUnify(patternType, valueType, letDecl.Pattern.Span, DiagnosticMessages.LetPatternTypeMismatch);
        }

        var resolvedType = _substitution.Apply(valueType);
        if (letDecl.SymbolId.IsValid)
        {
            var scheme = _env.Generalize(resolvedType);
            _env = _env.Extend(letDecl.SymbolId, scheme);
            UpdateVariableSymbolType(letDecl.SymbolId, resolvedType, scheme);
        }

        letDecl.InferredType = resolvedType;
    }

    private void InferLetQuestionDecl(LetQuestionDecl letQuestionDecl)
    {
        var valueType = letQuestionDecl.Value != null
            ? SafeInferExpression(letQuestionDecl.Value)
            : CreateMissingShapeRecoveryType(letQuestionDecl.Span, DiagnosticMessages.LetQuestionRequiresValueExpression);

        if (letQuestionDecl.Value != null)
        {
            _constraintGenerator.Generate(letQuestionDecl.Value);
        }

        if (letQuestionDecl.Pattern == null)
        {
            AddError(letQuestionDecl.Span, DiagnosticMessages.LetQuestionRequiresBindingPattern);
            letQuestionDecl.InferredType = CreateErrorRecoveryType();
            return;
        }

        if (!TryClassifyLetQuestionType(
                valueType,
                out var bindingKind,
                out var adtId,
                out var successPayloadType,
                out var failurePayloadType))
        {
            if (!ContainsErrorRecoveryType(valueType))
            {
                AddError(letQuestionDecl.Value?.Span ?? letQuestionDecl.Span, DiagnosticMessages.LetQuestionRhsMustBeOptionOrResult);
            }

            var recovered = CreateErrorRecoveryType();
            InferPattern(letQuestionDecl.Pattern, recovered);
            letQuestionDecl.InferredType = recovered;
            return;
        }

        var patternType = InferPattern(letQuestionDecl.Pattern, successPayloadType);
        successPayloadType = TryUnify(
            patternType,
            successPayloadType,
            letQuestionDecl.Pattern.Span,
            DiagnosticMessages.LetPatternTypeMismatch);

        var shortCircuitReturnType = ValidateLetQuestionReturnType(
            letQuestionDecl,
            bindingKind,
            adtId,
            failurePayloadType);

        if (!TryGetLetQuestionConstructors(
                adtId,
                bindingKind,
                out var successConstructor,
                out var failureConstructor))
        {
            var typeName = bindingKind == LetQuestionBindingKind.Option ? "Option" : "Result";
            var constructorName = bindingKind == LetQuestionBindingKind.Option ? "Some/None" : "Ok/Err";
            AddError(letQuestionDecl.Span, DiagnosticMessages.LetQuestionMissingConstructor(typeName, constructorName));
        }

        var resolvedFailurePayloadType = failurePayloadType != null
            ? _substitution.Apply(failurePayloadType)
            : null;
        UpdateLetQuestionFailureBindingSymbol(letQuestionDecl, resolvedFailurePayloadType);

        letQuestionDecl.SetDesugaring(
            bindingKind,
            successConstructor,
            failureConstructor,
            _substitution.Apply(successPayloadType),
            resolvedFailurePayloadType,
            shortCircuitReturnType);
        letQuestionDecl.InferredType = BaseTypes.Unit;
    }

    private bool TryClassifyLetQuestionType(
        Type valueType,
        out LetQuestionBindingKind bindingKind,
        out SymbolId adtId,
        out Type successPayloadType,
        out Type? failurePayloadType)
    {
        bindingKind = LetQuestionBindingKind.Unknown;
        adtId = SymbolId.None;
        successPayloadType = CreateErrorRecoveryType();
        failurePayloadType = null;

        var resolvedType = _substitution.Apply(valueType);
        while (resolvedType is TyVar { Instance: { } instance })
        {
            resolvedType = _substitution.Apply(instance);
        }

        if (resolvedType is not TyCon tyCon)
        {
            return false;
        }

        if (IsLetQuestionTyCon(tyCon, "Option", expectedArity: 1, out adtId))
        {
            bindingKind = LetQuestionBindingKind.Option;
            successPayloadType = _substitution.Apply(tyCon.Args[0]);
            return true;
        }

        if (IsLetQuestionTyCon(tyCon, "Result", expectedArity: 2, out adtId))
        {
            bindingKind = LetQuestionBindingKind.Result;
            successPayloadType = _substitution.Apply(tyCon.Args[0]);
            failurePayloadType = _substitution.Apply(tyCon.Args[1]);
            return true;
        }

        return false;
    }

    private bool IsLetQuestionTyCon(TyCon tyCon, string expectedName, int expectedArity, out SymbolId adtId)
    {
        adtId = SymbolId.None;
        if (tyCon.Args.Count != expectedArity)
        {
            return false;
        }

        if (tyCon.Symbol.IsValid &&
            _symbolTable.GetSymbol<AdtSymbol>(tyCon.Symbol) is { } adtSymbol &&
            string.Equals(adtSymbol.Name, expectedName, StringComparison.Ordinal))
        {
            adtId = tyCon.Symbol;
            return true;
        }

        if (!string.Equals(tyCon.Name, expectedName, StringComparison.Ordinal))
        {
            return false;
        }

        adtId = tyCon.Symbol;
        if (!adtId.IsValid &&
            _symbolTable.LookupType(expectedName) is { } lookupId &&
            lookupId.IsValid)
        {
            adtId = lookupId;
        }

        return true;
    }

    private Type ValidateLetQuestionReturnType(
        LetQuestionDecl letQuestionDecl,
        LetQuestionBindingKind bindingKind,
        SymbolId adtId,
        Type? failurePayloadType)
    {
        if (!TryGetCurrentFunctionReturnType(out var currentReturnType))
        {
            AddError(letQuestionDecl.Span, DiagnosticMessages.LetQuestionOutsideFunction);
            return CreateErrorRecoveryType();
        }

        var expectedReturnType = bindingKind switch
        {
            LetQuestionBindingKind.Option => new TyCon
            {
                Name = "Option",
                Symbol = adtId,
                Args = [_substitution.FreshTypeVariable()]
            },
            LetQuestionBindingKind.Result => new TyCon
            {
                Name = "Result",
                Symbol = adtId,
                Args = [_substitution.FreshTypeVariable(), failurePayloadType ?? CreateErrorRecoveryType()]
            },
            _ => CreateErrorRecoveryType()
        };

        var diagnostic = bindingKind == LetQuestionBindingKind.Option
            ? DiagnosticMessages.LetQuestionOptionRequiresOptionReturn
            : DiagnosticMessages.LetQuestionResultRequiresResultReturn;

        var result = TryUnify(
            expectedReturnType,
            currentReturnType,
            letQuestionDecl.Span,
            diagnostic);
        return _substitution.Apply(result);
    }

    private bool TryGetLetQuestionConstructors(
        SymbolId adtId,
        LetQuestionBindingKind bindingKind,
        out SymbolId successConstructor,
        out SymbolId failureConstructor)
    {
        var successName = bindingKind == LetQuestionBindingKind.Option ? "Some" : "Ok";
        var failureName = bindingKind == LetQuestionBindingKind.Option ? "None" : "Err";
        var successFound = TryGetAdtConstructor(adtId, successName, out successConstructor);
        var failureFound = TryGetAdtConstructor(adtId, failureName, out failureConstructor);
        return successFound && failureFound;
    }

    private bool TryGetAdtConstructor(SymbolId adtId, string constructorName, out SymbolId constructorId)
    {
        constructorId = SymbolId.None;
        if (!adtId.IsValid ||
            _symbolTable.GetSymbol<AdtSymbol>(adtId) is not { } adtSymbol)
        {
            return false;
        }

        foreach (var candidateId in adtSymbol.Constructors)
        {
            if (_symbolTable.GetSymbol<CtorSymbol>(candidateId) is { } ctorSymbol &&
                string.Equals(ctorSymbol.Name, constructorName, StringComparison.Ordinal))
            {
                constructorId = candidateId;
                return true;
            }
        }

        return false;
    }

    private void UpdateLetQuestionFailureBindingSymbol(LetQuestionDecl letQuestionDecl, Type? failurePayloadType)
    {
        if (failurePayloadType == null ||
            !letQuestionDecl.FailureBindingSymbolId.IsValid ||
            _symbolTable.GetSymbol<VarSymbol>(letQuestionDecl.FailureBindingSymbolId) is not { } variableSymbol)
        {
            return;
        }

        var resolvedType = _substitution.Apply(failurePayloadType);
        variableSymbol.Type = ResolveSymbolMetadataTypeId(resolvedType);
        variableSymbol.Scheme = _env.Generalize(resolvedType);
    }

    private Type CreateMissingInitializerRecoveryType(string declarationKind, string? name, SourceSpan span)
    {
        var displayName = string.IsNullOrWhiteSpace(name) ? string.Empty : $" '{name}'";
        return CreateMissingShapeRecoveryType(span, DiagnosticMessages.DeclarationRequiresInitializer(declarationKind, displayName));
    }

    private void UpdateVariableSymbolType(SymbolId symbolId, Type type, TypeScheme scheme)
    {
        if (!symbolId.IsValid ||
            _symbolTable.GetSymbol<VarSymbol>(symbolId) is not { } variableSymbol)
        {
            return;
        }

        variableSymbol.Type = ResolveSymbolMetadataTypeId(type);
        variableSymbol.Scheme = scheme;
    }
}
