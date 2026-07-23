using Eidosc.Ast;
using Eidosc.Ast.Expressions;
using Eidosc.Diagnostic;
using Eidosc.Semantic;
using Eidosc.Symbols;
using Eidosc.Utils;

namespace Eidosc.Types;

public sealed partial class TypeInferer
{
    private Type InferSelection(SelectionExpr selection)
    {
        if (selection.Subject == null)
        {
            AddError(selection.Span, DiagnosticMessages.SelectionSubjectMustBeSupported("<missing>"), "E4023");
            return CreateErrorRecoveryType();
        }

        _ = SafeInferExpression(selection.Subject);
        var subjectNodes = selection.Subject is TupleExpr tuple
            ? tuple.Elements.Cast<EidosAstNode>().ToArray()
            : [selection.Subject];
        var subjects = new List<SelectionSubjectDesugaring>(subjectNodes.Length);
        var hasRecovery = false;
        foreach (var subjectNode in subjectNodes)
        {
            var subjectType = subjectNode.InferredType as Type ?? CreateErrorRecoveryType();
            if (!TryClassifySelectionSubject(subjectType, out var subject))
            {
                if (!ContainsErrorRecoveryType(subjectType))
                {
                    AddError(
                        subjectNode.Span,
                        DiagnosticMessages.SelectionSubjectMustBeSupported(_substitution.Apply(subjectType).ToString() ?? "<unknown>"),
                        "E4023");
                }

                hasRecovery = true;
                subject = new SelectionSubjectDesugaring
                {
                    Kind = SelectionSubjectKind.Unknown,
                    SubjectType = subjectType
                };
            }

            subjects.Add(subject);
        }

        selection.SetDesugaring(subjects);
        var positivePayloads = subjects.SelectMany(static subject => subject.PositivePayloadTypes).OfType<Type>().ToArray();
        var negativePayloads = selection.IsGroup
            ? []
            : subjects.SelectMany(static subject => subject.NegativePayloadTypes).OfType<Type>().ToArray();

        var thenType = InferSelectionArm(selection, positiveArm: true, positivePayloads, ref hasRecovery);
        var elseType = InferSelectionArm(selection, positiveArm: false, negativePayloads, ref hasRecovery);

        Type result;
        if (selection.ThenArm != null && selection.ElseArm != null)
        {
            result = JoinControlFlowTypes(
                thenType,
                elseType,
                selection.Span,
                DiagnosticMessages.SelectionBranchTypeMismatch,
                [selection.ThenArm.Span],
                selection.ElseArm.Span);
        }
        else
        {
            var presentArm = selection.ThenArm ?? selection.ElseArm;
            var presentType = selection.ThenArm != null ? thenType : elseType;
            result = JoinControlFlowTypes(
                presentType,
                BaseTypes.Unit,
                selection.Span,
                DiagnosticMessages.SelectionSingleArmMustReturnUnit,
                presentArm == null ? [] : [presentArm.Span],
                null);
        }

        return hasRecovery || ContainsErrorRecoveryType(result)
            ? CreateErrorRecoveryType()
            : result;
    }

    private Type InferSelectionArm(
        SelectionExpr selection,
        bool positiveArm,
        IReadOnlyList<Type> payloadTypes,
        ref bool hasRecovery)
    {
        var arm = positiveArm ? selection.ThenArm : selection.ElseArm;
        if (arm == null)
        {
            return BaseTypes.Unit;
        }

        var indices = positiveArm
            ? selection.ThenPlaceholderIndices
            : selection.ElsePlaceholderIndices;
        var symbols = positiveArm
            ? selection.ThenPlaceholderSymbols
            : selection.ElsePlaceholderSymbols;
        var spans = positiveArm
            ? selection.ThenPlaceholderSpans
            : selection.ElsePlaceholderSpans;
        var savedEnv = _env;
        try
        {
            foreach (var index in indices)
            {
                var payloadType = index >= 0 && index < payloadTypes.Count
                    ? _substitution.Apply(payloadTypes[index])
                    : CreateErrorRecoveryType();
                if (index < 0 || index >= payloadTypes.Count)
                {
                    if (positiveArm || !selection.IsGroup)
                    {
                        AddError(
                            spans.GetValueOrDefault(index, arm.Span),
                            DiagnosticMessages.SelectionPlaceholderOutOfRange($"_{index}", payloadTypes.Count),
                            "E4024");
                    }
                    hasRecovery = true;
                }

                if (!symbols.TryGetValue(index, out var symbolId) || !symbolId.IsValid)
                {
                    continue;
                }

                _env = _env.ExtendMono(symbolId, payloadType);
                if (_symbolTable.GetSymbol<VarSymbol>(symbolId) is { } variableSymbol)
                {
                    variableSymbol.Type = ResolveSymbolMetadataTypeId(payloadType);
                    variableSymbol.Scheme = _env.Generalize(payloadType);
                }
            }

            var armType = SafeInferExpression(arm);
            hasRecovery |= ContainsErrorRecoveryType(armType);
            return armType;
        }
        finally
        {
            _env = savedEnv;
        }
    }

    private bool TryClassifySelectionSubject(Type type, out SelectionSubjectDesugaring subject)
    {
        var resolvedType = _substitution.Apply(type);
        while (resolvedType is TyVar { Instance: { } instance })
        {
            resolvedType = _substitution.Apply(instance);
        }

        if (resolvedType is TyCon { Id.Value: BaseTypes.BoolId } ||
            resolvedType is TyCon { Name: WellKnownStrings.BuiltinTypes.Bool })
        {
            subject = new SelectionSubjectDesugaring
            {
                Kind = SelectionSubjectKind.Bool,
                SubjectType = resolvedType
            };
            return true;
        }

        if (resolvedType is not TyCon tyCon)
        {
            subject = new SelectionSubjectDesugaring();
            return false;
        }

        if (TryPromoteClosedCaseToRoot(tyCon, out var rootType))
        {
            tyCon = rootType;
        }

        var kind = tyCon.Name switch
        {
            "Option" when tyCon.Args.Count == 1 => SelectionSubjectKind.Option,
            "Result" when tyCon.Args.Count == 2 => SelectionSubjectKind.Result,
            "Either" when tyCon.Args.Count == 2 => SelectionSubjectKind.Either,
            _ => SelectionSubjectKind.Unknown
        };
        if (kind == SelectionSubjectKind.Unknown || !IsCanonicalSelectionAdt(tyCon, kind, out var adtId))
        {
            subject = new SelectionSubjectDesugaring();
            return false;
        }

        var (positiveName, negativeName) = kind switch
        {
            SelectionSubjectKind.Option => ("Some", "None"),
            SelectionSubjectKind.Result => ("Ok", "Err"),
            SelectionSubjectKind.Either => ("Right", "Left"),
            _ => (string.Empty, string.Empty)
        };
        if (!TryGetAdtConstructor(adtId, positiveName, out var positiveConstructor) ||
            !TryGetAdtConstructor(adtId, negativeName, out var negativeConstructor))
        {
            subject = new SelectionSubjectDesugaring();
            return false;
        }

        var positivePayloads = kind switch
        {
            SelectionSubjectKind.Option => new object[] { _substitution.Apply(tyCon.Args[0]) },
            SelectionSubjectKind.Result => new object[] { _substitution.Apply(tyCon.Args[0]) },
            SelectionSubjectKind.Either => new object[] { _substitution.Apply(tyCon.Args[1]) },
            _ => []
        };
        var negativePayloads = kind switch
        {
            SelectionSubjectKind.Result => new object[] { _substitution.Apply(tyCon.Args[1]) },
            SelectionSubjectKind.Either => new object[] { _substitution.Apply(tyCon.Args[0]) },
            _ => []
        };
        subject = new SelectionSubjectDesugaring
        {
            Kind = kind,
            PositiveConstructorSymbolId = positiveConstructor,
            NegativeConstructorSymbolId = negativeConstructor,
            SubjectType = tyCon,
            PositivePayloadTypes = positivePayloads,
            NegativePayloadTypes = negativePayloads
        };
        return true;
    }

    private bool IsCanonicalSelectionAdt(
        TyCon type,
        SelectionSubjectKind kind,
        out SymbolId adtId)
    {
        adtId = type.Symbol;
        if (!adtId.IsValid || _symbolTable.GetSymbol<AdtSymbol>(adtId) is not { } adt)
        {
            return false;
        }

        var expectedModule = kind.ToString();
        if (!string.Equals(adt.Name, expectedModule, StringComparison.Ordinal))
        {
            return false;
        }

        if (PrecompiledModuleRegistry.TryGetModulePathFromSourcePath(adt.Span.FilePath, out var modulePath) &&
            string.Equals(modulePath, expectedModule, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return _symbolTable.Modules.TryGetOwningModule(adtId, out var owner) &&
               string.Equals(owner.PackageAlias, WellKnownStrings.Std.Module, StringComparison.OrdinalIgnoreCase) &&
               owner.Path.Count > 0 &&
               string.Equals(owner.Path[^1], expectedModule, StringComparison.OrdinalIgnoreCase);
    }
}
