using System.Security.Cryptography;
using System.Text;
using Eidosc.Diagnostic;
using Eidosc.Utils;

namespace Eidosc.Mir.Optimize;

internal enum SpecializationFailureReason
{
    UnresolvedTypes,
    UnresolvedConstructorBinding,
    TypeInferenceFailed,
    PartialBindingIncomplete,
    NoConcreteDispatchType
}

internal sealed record SpecializationFailure(
    SpecializationFailureReason Reason,
    string TemplateKey,
    string TemplateName,
    SourceSpan TemplateSpan,
    string SignatureKey,
    string SignatureDisplay,
    string PreviewName)
{
    public string ReasonKey => Reason switch
    {
        SpecializationFailureReason.UnresolvedTypes => "unresolved-types",
        SpecializationFailureReason.UnresolvedConstructorBinding => "unresolved-constructor-binding",
        SpecializationFailureReason.TypeInferenceFailed => "type-inference-failed",
        SpecializationFailureReason.PartialBindingIncomplete => "partial-binding-incomplete",
        SpecializationFailureReason.NoConcreteDispatchType => "no-concrete-dispatch-type",
        _ => Reason.ToString()
    };

    public MirSpecializationFailureInfo ToMirInfo()
    {
        return new MirSpecializationFailureInfo
        {
            Reason = ReasonKey,
            TemplateKey = TemplateKey,
            TemplateName = TemplateName,
            SignatureKey = SignatureKey,
            SignatureDisplay = SignatureDisplay,
            PreviewName = PreviewName
        };
    }

    public Diagnostic.Diagnostic ToDiagnostic()
    {
        var diagnostic = Diagnostic.Diagnostic.Warning(
                DiagnosticMessages.SpecializationRejectedUnresolvedTypes(TemplateName, SignatureDisplay),
                "E5310")
            .WithNote(DiagnosticMessages.GenericCallWillRemainUnresolvedNote)
            .WithNote(DiagnosticMessages.MirSpecializationFailureSuggestionNote(ReasonKey))
            .WithNote(DiagnosticMessages.TemplateSpecializedNameNote(TemplateKey, PreviewName))
            .WithMetadata("phase", "mir-specialization")
            .WithMetadata("reason", ReasonKey)
            .WithMetadata("templateKey", TemplateKey)
            .WithMetadata("templateName", TemplateName)
            .WithMetadata("signatureKey", SignatureKey)
            .WithMetadata("previewName", PreviewName);

        if (HasSpan(TemplateSpan))
        {
            diagnostic.WithLabel(TemplateSpan, DiagnosticMessages.MirSpecializationFailureTemplateLabel);
        }

        return diagnostic;
    }

    private static bool HasSpan(SourceSpan span)
    {
        return span.Length > 0 || !string.IsNullOrWhiteSpace(span.FilePath);
    }
}

public sealed partial class MirGenericSpecializer
{
    private void RecordRejectedSpecialization(
        TemplateInfo template,
        SpecializationSignature signature,
        SpecializationFailureReason reason)
    {
        var specializationKey = CreateSpecializationCacheKey(template, signature);
        if (!_rejectedSpecializationsByTemplateAndSignature.Add(specializationKey))
        {
            return;
        }

        RecordRejectedSpecializationFailure(
            template.Key,
            template.TemplateSource.Name,
            template.TemplateSource.Span,
            signature.ToKeyString(),
            signature.ToString(),
            reason);
    }

    private void RecordRejectedSpecialization(
        TemplateInfo template,
        string signatureKey,
        string signatureDisplay,
        SpecializationFailureReason reason)
    {
        RecordRejectedSpecializationFailure(
            template.Key,
            template.TemplateSource.Name,
            template.TemplateSource.Span,
            signatureKey,
            signatureDisplay,
            reason);
    }

    private void RecordRejectedSpecialization(
        string templateKey,
        string templateName,
        SourceSpan templateSpan,
        string signatureKey,
        string signatureDisplay,
        SpecializationFailureReason reason)
    {
        RecordRejectedSpecializationFailure(
            templateKey,
            templateName,
            templateSpan,
            signatureKey,
            signatureDisplay,
            reason);
    }

    private void RecordRejectedSpecializationFailure(
        string templateKey,
        string templateName,
        SourceSpan templateSpan,
        string signatureKey,
        string signatureDisplay,
        SpecializationFailureReason reason)
    {
        var specializationKey = BuildSpecializationKey(templateKey, signatureKey);
        if (!_reportedRejectedSpecializationsByTemplateAndSignature.Add(specializationKey))
        {
            return;
        }

        var failure = new SpecializationFailure(
            reason,
            templateKey,
            templateName,
            templateSpan,
            signatureKey,
            signatureDisplay,
            PreviewSpecializationName(templateName, signatureKey));
        _failures.Add(failure);
        _diagnostics.Add(failure.ToDiagnostic());
    }

    private void RetainSpecializationFailuresForOutputFunctions(IReadOnlyList<MirFunc> outputFunctions)
    {
        if (_failures.Count == 0 && _diagnostics.Count == 0)
        {
            return;
        }

        var retainedTemplateKeys = CollectOutputSpecializationFailureTemplateKeys(outputFunctions);
        if (retainedTemplateKeys.Count == 0)
        {
            _failures.Clear();
            _diagnostics.RemoveAll(static diagnostic => diagnostic.Metadata.ContainsKey("templateKey"));
            return;
        }

        _failures.RemoveAll(failure => !retainedTemplateKeys.Contains(failure.TemplateKey));
        _diagnostics.RemoveAll(diagnostic =>
            diagnostic.Metadata.TryGetValue("templateKey", out var templateKey) &&
            !retainedTemplateKeys.Contains(templateKey));
    }

    private HashSet<string> CollectOutputSpecializationFailureTemplateKeys(IReadOnlyList<MirFunc> outputFunctions)
    {
        var retainedTemplateKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var function in outputFunctions)
        {
            if (TryResolveTemplateKey(function, out var functionTemplateKey))
            {
                retainedTemplateKeys.Add(functionTemplateKey);
            }

            VisitFunctionRefs(function, functionRef =>
            {
                if (TryResolveTemplateKey(functionRef, out var referencedTemplateKey))
                {
                    retainedTemplateKeys.Add(referencedTemplateKey);
                }

                if (TryBuildTraitDispatchFailureTemplateKey(function, functionRef, out var traitDispatchTemplateKey))
                {
                    retainedTemplateKeys.Add(traitDispatchTemplateKey);
                }
            });
        }

        return retainedTemplateKeys;
    }

    private static bool TryBuildTraitDispatchFailureTemplateKey(
        MirFunc containingFunction,
        MirFunctionRef functionRef,
        out string templateKey)
    {
        templateKey = string.Empty;

        if (MirFunctionIdentity.TryGetStableKey(functionRef.FunctionId, out templateKey))
        {
            return true;
        }

        if (functionRef.SymbolId.IsValid)
        {
            templateKey = MirFunctionIdentity.GetStableKey(functionRef.Name, functionRef.SymbolId);
            return true;
        }

        if (functionRef.TraitOwnerId.IsValid &&
            !string.IsNullOrWhiteSpace(functionRef.Name))
        {
            templateKey = $"trait:{functionRef.TraitOwnerId.Value}:{functionRef.Name}";
            return true;
        }

        if (containingFunction.TraitInvokeHelperTraitId.IsValid &&
            !string.IsNullOrWhiteSpace(functionRef.Name))
        {
            templateKey = $"trait:{containingFunction.TraitInvokeHelperTraitId.Value}:{functionRef.Name}";
            return true;
        }

        return false;
    }

    private static string BuildSpecializationKey(string templateKey, string signatureKey)
    {
        return $"{templateKey}|{signatureKey}";
    }

    private string BuildUnresolvedCallSignatureKey(
        MirCall call,
        IReadOnlyList<MirOperand> combinedArguments,
        IReadOnlyDictionary<LocalId, TypeId> localTypes)
    {
        var targetType = call.Target == null
            ? TypeId.None
            : ResolvePlaceType(call.Target, localTypes);
        var argumentTypes = combinedArguments
            .Select(argument => ResolveOperandType(argument, localTypes).Value.ToString());
        var typeArgumentKey = call.Function is MirFunctionRef functionRef
            ? BuildTypeArgumentKey(functionRef.TypeArgumentIds)
            : "[]";
        return $"inference-failed:{targetType.Value}|{string.Join(",", argumentTypes)}|{typeArgumentKey}";
    }

    private string BuildUnresolvedCallSignatureDisplay(
        MirCall call,
        IReadOnlyList<MirOperand> combinedArguments,
        IReadOnlyDictionary<LocalId, TypeId> localTypes)
    {
        var targetType = call.Target == null
            ? TypeId.None
            : ResolvePlaceType(call.Target, localTypes);
        var argumentTypes = combinedArguments
            .Select(argument => ResolveOperandType(argument, localTypes).Value.ToString());
        var typeArgumentKey = call.Function is MirFunctionRef functionRef
            ? BuildTypeArgumentKey(functionRef.TypeArgumentIds)
            : "[]";
        return $"return:{targetType.Value} args:[{string.Join(",", argumentTypes)}] typeArgs:{typeArgumentKey}";
    }

    private bool SignatureContainsOpenConstructorBinding(SpecializationSignature signature)
    {
        if (ContainsOpenConstructorBinding(signature.ReturnType))
        {
            return true;
        }

        return signature.ParameterTypes.Any(ContainsOpenConstructorBinding);
    }

    private static string PreviewSpecializationName(string templateName, string signatureKey)
    {
        var normalizedBase = string.IsNullOrWhiteSpace(templateName) ? "generic" : templateName;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(signatureKey));
        var hashFragment = Convert.ToHexString(hash.AsSpan(0, 6));
        return $"{normalizedBase}{WellKnownStrings.InternalNames.SpecializationMarker}{hashFragment}";
    }
}
