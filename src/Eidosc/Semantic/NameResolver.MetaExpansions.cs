using System.Security.Cryptography;
using System.Text;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Types;
using Eidosc.Diagnostic;
using Eidosc.Symbols;
using Eidosc.Types;
using Eidosc.Utils;
using EidoscDiagnostic = Eidosc.Diagnostic.Diagnostic;
using EidoscDiagnosticLevel = Eidosc.Diagnostic.DiagnosticLevel;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private const int MaxDeriveExpansionCount = 256;
    private const int MaxGeneratedDeclarationCount = 2048;
    private const int MaxMetaDiagnosticCount = 128;

    private void ProcessDeferredDeriveExpansions(ModuleDecl root)
    {
        if (_deferredDeriveInvocations.Count == 0)
        {
            return;
        }

        var functions = _moduleDeclarations.Values
            .SelectMany(EnumerateDeclarations)
            .OfType<FuncDef>()
            .Where(static function => function.SymbolId.IsValid)
            .DistinctBy(static function => function.SymbolId)
            .ToDictionary(static function => function.SymbolId);
        ResolveExpansionComptimeDependencies(root);
        var comptimeValues = EvaluateExpansionComptimeBindings(root, functions);
        var processedExpansionCount = 0;
        var generatedDeclarationCount = 0;
        var emittedDiagnosticCount = 0;
        for (var invocationIndex = 0; invocationIndex < _deferredDeriveInvocations.Count; invocationIndex++)
        {
            if (++processedExpansionCount > MaxDeriveExpansionCount)
            {
                AddMetaExpansionDiagnostic(
                    _deferredDeriveInvocations[invocationIndex].Attribute.Span,
                    "derive expansion count exceeded the compiler budget",
                    "E3607");
                break;
            }

            var invocation = _deferredDeriveInvocations[invocationIndex];
            using var moduleScope = PushResolutionModuleScope(invocation.ModuleId);
            using var currentModuleScope = PushCurrentModuleScope(invocation.ModuleId);
            if (!TryResolveDeriveGenerator(invocation, out var generator, out var generatorSymbol, out var reason))
            {
                AddMetaExpansionDiagnostic(invocation.Attribute.Span, reason, "E3600");
                continue;
            }

            var generatorIdentity = MetaComptimeIntrinsics.CreateStableIdentity(generatorSymbol, _symbolTable);
            var targetSymbol = _symbolTable.GetSymbol(invocation.Target.SymbolId);
            if (targetSymbol == null)
            {
                AddMetaExpansionDiagnostic(invocation.Attribute.Span, "derive target has no stable declaration symbol", "E3600");
                continue;
            }

            var targetIdentity = MetaComptimeIntrinsics.CreateStableIdentity(targetSymbol, _symbolTable);
            var cycleKey = $"{generatorIdentity}|{targetIdentity}";
            if (invocation.Ancestors.Contains(cycleKey, StringComparer.Ordinal))
            {
                AddMetaExpansionDiagnostic(
                    invocation.Attribute.Span,
                    $"derive expansion cycle detected for generator '{generator.Name}' and target '{invocation.Target.Name}'",
                    "E3604");
                continue;
            }

            var deriveInput = MetaComptimeIntrinsics.CreateDeriveInput(
                invocation.Target,
                generatorSymbol,
                invocation.Attribute.Span,
                invocation.AttributeOccurrenceIndex,
                _symbolTable);
            var trace = $"@derive({generator.Name}) on {invocation.Target.Name}";
            var metaContext = new MetaComptimeContext(
                _symbolTable,
                _adtDefinitions,
                _traitDefinitions,
                (level, span, message) => AddMetaUserDiagnostic(level, span, message, trace),
                deriveInput,
                trace,
                ComptimeExecution.CreateBudget(),
                ComptimeExecution.Trace,
                "namer.meta-expansion");

            if (!ComptimeEvaluator.TryInvoke(
                    generator,
                    [deriveInput],
                    comptimeValues,
                    functions,
                    metaContext,
                    out var expansionValue,
                    out reason))
            {
                AddMetaExpansionDiagnostic(
                    invocation.Attribute.Span,
                    $"derive generator '{generator.Name}' failed: {reason}; expansion trace: {trace}",
                    "E3601");
                continue;
            }

            var materializer = new MetaExpansionMaterializer(
                _symbolTable,
                invocation.Target,
                invocation.ModuleId,
                invocation.Attribute.Span);
            if (!materializer.TryMaterialize(expansionValue, out var materialization, out reason))
            {
                AddMetaExpansionDiagnostic(
                    invocation.Attribute.Span,
                    $"derive generator '{generator.Name}' returned an invalid expansion: {reason}",
                    "E3602");
                continue;
            }

            foreach (var diagnostic in materialization.Diagnostics)
            {
                if (++emittedDiagnosticCount > MaxMetaDiagnosticCount)
                {
                    AddMetaExpansionDiagnostic(
                        invocation.Attribute.Span,
                        "meta expansion diagnostic count exceeded the compiler budget",
                        "E3609");
                    return;
                }

                AddMaterializedDiagnostic(diagnostic, trace);
            }

            var ancestorChain = invocation.Ancestors.Concat([cycleKey]).ToArray();
            foreach (var attachment in materialization.Attachments)
            {
                ApplyMetaAttributeAttachment(root, invocation, attachment, ancestorChain);
            }

            foreach (var materialized in materialization.Declarations)
            {
                if (++generatedDeclarationCount > MaxGeneratedDeclarationCount)
                {
                    AddMetaExpansionDiagnostic(
                        invocation.Attribute.Span,
                        "generated declaration count exceeded the compiler budget",
                        "E3608");
                    return;
                }

                var origin = CreateGeneratedOrigin(
                    invocation,
                    generatorSymbol,
                    targetSymbol,
                    deriveInput,
                    materialized.OutputIndex,
                    materialized.NestedIndex);
                if (!_generatedDeclarationIdentities.Add(origin.StableIdentity))
                {
                    AddMetaExpansionDiagnostic(
                        invocation.Attribute.Span,
                        $"duplicate generated declaration stable identity '{origin.StableIdentity}'",
                        "E3605");
                    continue;
                }

                if (!_moduleDeclarations.TryGetValue(invocation.ModuleId, out var module))
                {
                    AddMetaExpansionDiagnostic(invocation.Attribute.Span, "derive target module is unavailable", "E3600");
                    continue;
                }

                module.Declarations.Add(materialized.Declaration);
                CollectDeclaration(materialized.Declaration);
                if (!materialized.Declaration.SymbolId.IsValid)
                {
                    continue;
                }

                _symbolTable.AddMemberToModule(invocation.ModuleId, materialized.Declaration.SymbolId);
                if (_symbolTable.GetSymbol(materialized.Declaration.SymbolId) is { } generatedSymbol)
                {
                    _symbolTable.UpdateSymbol(generatedSymbol with { GeneratedOrigin = origin });
                }

                if (materialized.Declaration is FuncDef generatedFunction && generatedFunction.SymbolId.IsValid)
                {
                    functions[generatedFunction.SymbolId] = generatedFunction;
                }
            }
        }
    }

    private void ResolveExpansionComptimeDependencies(ModuleDecl root)
    {
        foreach (var declaration in _moduleDeclarations.Values.SelectMany(EnumerateDeclarations))
        {
            if (!declaration.SymbolId.IsValid || _metaResolvedComptimeSymbols.Contains(declaration.SymbolId))
            {
                continue;
            }

            var isComptimeDeclaration = declaration is FuncDef { IsComptime: true } or LetDecl { IsComptime: true };
            if (!isComptimeDeclaration)
            {
                continue;
            }

            var moduleId = _symbolTable.Modules.TryGetOwningModuleId(declaration.SymbolId, out var ownerModuleId)
                ? ownerModuleId
                : root.SymbolId;
            using var moduleScope = PushResolutionModuleScope(moduleId);
            using var currentModuleScope = PushCurrentModuleScope(moduleId);
            ResolveDeclarationReferences(declaration);
            _metaResolvedComptimeSymbols.Add(declaration.SymbolId);
        }
    }

    private Dictionary<SymbolId, ComptimeValue> EvaluateExpansionComptimeBindings(
        ModuleDecl root,
        IReadOnlyDictionary<SymbolId, FuncDef> functions)
    {
        var values = new Dictionary<SymbolId, ComptimeValue>();
        var metaContext = new MetaComptimeContext(
            _symbolTable,
            _adtDefinitions,
            _traitDefinitions,
            (level, span, message) => AddMetaUserDiagnostic(level, span, message, "top-level comptime evaluation"),
            ResourceBudget: ComptimeExecution.CreateBudget(),
            Trace: ComptimeExecution.Trace,
            TracePhase: "namer.comptime-binding");
        foreach (var binding in _moduleDeclarations.Values
                     .SelectMany(EnumerateDeclarations)
                     .OfType<LetDecl>()
                     .DistinctBy(static binding => binding.SymbolId))
        {
            if (!binding.IsComptime || !binding.SymbolId.IsValid || binding.Value == null)
            {
                continue;
            }

            if (ComptimeEvaluator.TryEvaluate(
                    binding.Value,
                    values,
                    functions,
                    resolveType: null,
                    metaContext,
                    out var value,
                    out _))
            {
                values[binding.SymbolId] = value;
            }
        }

        return values;
    }

    private bool TryResolveDeriveGenerator(
        DeferredDeriveInvocation invocation,
        out FuncDef generator,
        out FuncSymbol generatorSymbol,
        out string reason)
    {
        generator = null!;
        generatorSymbol = null!;
        reason = string.Empty;
        SymbolId symbolId = SymbolId.None;
        if (invocation.ArgumentIndex < invocation.Attribute.Arguments.Count)
        {
            var argument = invocation.Attribute.Arguments[invocation.ArgumentIndex];
            ResolveExpressionReferences(argument);
            symbolId = argument switch
            {
                IdentifierExpr identifier => identifier.SymbolId,
                PathExpr path => path.SymbolId,
                _ => SymbolId.None
            };
        }

        if (!symbolId.IsValid)
        {
            var path = ParsePathText(invocation.GeneratorText);
            if (path.Count == 1)
            {
                var lookup = _lookupService.Lookup(path[0], LookupKind.Value, CreateLookupContext());
                symbolId = lookup.IsSuccess ? lookup.SymbolId : SymbolId.None;
            }
            else if (path.Count > 1)
            {
                var resolved = ResolvePathWithImports(path);
                symbolId = resolved.IsSuccess ? resolved.SymbolId : SymbolId.None;
            }
        }

        if (!symbolId.IsValid ||
            _symbolTable.GetSymbol<FuncSymbol>(symbolId) is not { } symbol ||
            !symbol.IsComptime)
        {
            reason = $"@derive({invocation.GeneratorText}) must reference a comptime-only function";
            return false;
        }

        generator = _moduleDeclarations.Values
            .SelectMany(EnumerateDeclarations)
            .OfType<FuncDef>()
            .FirstOrDefault(function => function.SymbolId == symbolId)!;
        if (generator == null)
        {
            reason = $"@derive({invocation.GeneratorText}) cannot execute a signature-only or compiler-internal function";
            return false;
        }

        if (_metaResolvedComptimeSymbols.Add(generator.SymbolId))
        {
            var generatorModuleId = _symbolTable.Modules.TryGetOwningModuleId(generator.SymbolId, out var ownerModuleId)
                ? ownerModuleId
                : invocation.ModuleId;
            using var generatorModuleScope = PushResolutionModuleScope(generatorModuleId);
            using var currentGeneratorModuleScope = PushCurrentModuleScope(generatorModuleId);
            ResolveFuncDefReferences(generator);
        }

        if (!HasDeriveProtocolSignature(generator))
        {
            reason = $"derive generator '{generator.Name}' must have signature 'comptime Meta::DeriveInput -> Meta::Expansion'";
            return false;
        }

        generatorSymbol = symbol;
        return true;
    }

    private bool HasDeriveProtocolSignature(FuncDef generator)
    {
        if (generator.Signature.Count != 1 || generator.Signature[0] is not ArrowType arrow)
        {
            return false;
        }

        return IsMetaType(arrow.ParamType, WellKnownTypeIds.MetaDeriveInputId) &&
               IsMetaType(arrow.ReturnType, WellKnownTypeIds.MetaExpansionId);
    }

    private bool IsMetaType(TypeNode type, int typeId)
    {
        return type is TypePath path &&
               path.SymbolId.IsValid &&
               _symbolTable.GetSymbol(path.SymbolId)?.TypeId == new TypeId(typeId);
    }

    private void ApplyMetaAttributeAttachment(
        ModuleDecl root,
        DeferredDeriveInvocation parentInvocation,
        MetaAttributeAttachment attachment,
        IReadOnlyList<string> ancestorChain)
    {
        var targetDeclaration = EnumerateDeclarations(root)
            .FirstOrDefault(declaration => declaration.SymbolId == attachment.Target.SymbolId);
        if (targetDeclaration == null)
        {
            AddMetaExpansionDiagnostic(
                parentInvocation.Attribute.Span,
                $"generated attribute target '{attachment.Target.Name}' is not a declaration in this compilation",
                "E3603");
            return;
        }

        var semanticAttribute = attachment.Name is "impl" or "ffi" or "operator" or "cstruct" or "borrow" or "intrinsic";
        if (semanticAttribute)
        {
            AddMetaExpansionDiagnostic(
                parentInvocation.Attribute.Span,
                $"generated semantic attribute '@{attachment.Name}' would require an earlier compiler phase; use a structured declaration output instead",
                "E3606");
            return;
        }

        var attribute = new Eidosc.Ast.Attribute();
        attribute.SetSpan(parentInvocation.Attribute.Span);
        attribute.SetName(attachment.Name);
        foreach (var argument in attachment.Arguments)
        {
            attribute.AddArgumentText(argument);
        }

        targetDeclaration.SetAttributes([.. targetDeclaration.Attributes, attribute]);
        if (!string.Equals(attachment.Name, WellKnownStrings.Keywords.Derive, StringComparison.Ordinal))
        {
            return;
        }

        if (targetDeclaration is not AdtDef adt)
        {
            AddMetaExpansionDiagnostic(
                parentInvocation.Attribute.Span,
                "generated @derive attribute can only target an ADT declaration",
                "E3603");
            return;
        }

        var occurrenceIndex = adt.Attributes.Count - 1;
        for (var argumentIndex = 0; argumentIndex < attachment.Arguments.Count; argumentIndex++)
        {
            var generatorText = attachment.Arguments[argumentIndex];
            if (NormalizeBuiltinDeriveTraitName(generatorText) is { } builtin)
            {
                var generated = GenerateDerivedImpl(adt, builtin, attribute.Span);
                if (generated != null)
                {
                    RegisterGeneratedDerivedFunction(generated);
                }

                continue;
            }

            _deferredDeriveInvocations.Add(new DeferredDeriveInvocation(
                adt,
                parentInvocation.ModuleId,
                attribute,
                occurrenceIndex,
                argumentIndex,
                generatorText,
                ancestorChain));
        }
    }

    private GeneratedDeclarationOrigin CreateGeneratedOrigin(
        DeferredDeriveInvocation invocation,
        FuncSymbol generator,
        Symbol target,
        ComptimeMetaObjectValue deriveInput,
        int outputIndex,
        int nestedIndex)
    {
        var generatorIdentity = MetaComptimeIntrinsics.CreateStableIdentity(generator, _symbolTable);
        var targetIdentity = MetaComptimeIntrinsics.CreateStableIdentity(target, _symbolTable);
        var canonicalArgumentsHash = deriveInput.CanonicalHash;
        var identityMaterial = string.Join(
            "|",
            generatorIdentity,
            targetIdentity,
            invocation.AttributeOccurrenceIndex,
            outputIndex,
            nestedIndex,
            canonicalArgumentsHash,
            WellKnownStrings.Meta.SchemaVersion);
        var stableIdentity = HashIdentity(identityMaterial);
        return new GeneratedDeclarationOrigin
        {
            StableIdentity = stableIdentity,
            GeneratorIdentity = generatorIdentity,
            TargetIdentity = targetIdentity,
            GeneratorSymbolId = generator.Id,
            TargetSymbolId = target.Id,
            AttributeOccurrenceIndex = invocation.AttributeOccurrenceIndex,
            ExpansionOutputIndex = outputIndex,
            CanonicalArgumentsHash = canonicalArgumentsHash,
            MetaSchemaVersion = WellKnownStrings.Meta.SchemaVersion,
            AttributeSpan = invocation.Attribute.Span,
            VirtualDocumentPath = $"eidos-generated://{stableIdentity}.eidos"
        };
    }

    private void AddMetaUserDiagnostic(
        MetaDiagnosticLevel level,
        SourceSpan span,
        string message,
        string trace)
    {
        var diagnosticLevel = level == MetaDiagnosticLevel.Error
            ? EidoscDiagnosticLevel.Error
            : EidoscDiagnosticLevel.Warning;
        var code = level == MetaDiagnosticLevel.Error ? "E3610" : "W3610";
        var diagnostic = new EidoscDiagnostic(diagnosticLevel, message, code);
        diagnostic.WithLabel(span, message);
        diagnostic.WithNote($"meta expansion trace: {trace}");
        _diagnostics.Add(diagnostic);
    }

    private void AddMaterializedDiagnostic(MetaExpansionDiagnostic diagnostic, string trace)
    {
        var level = diagnostic.Level == "error" ? EidoscDiagnosticLevel.Error : EidoscDiagnosticLevel.Warning;
        var code = diagnostic.Level == "error" ? "E3611" : "W3611";
        var entry = new EidoscDiagnostic(level, diagnostic.Message, code);
        entry.WithLabel(diagnostic.Span, diagnostic.Message);
        entry.WithNote($"meta expansion trace: {trace}; output index: {diagnostic.OutputIndex}");
        _diagnostics.Add(entry);
    }

    private void AddMetaExpansionDiagnostic(SourceSpan span, string message, string code)
    {
        var diagnostic = new EidoscDiagnostic(EidoscDiagnosticLevel.Error, message, code);
        diagnostic.WithLabel(span, message);
        _diagnostics.Add(diagnostic);
    }

    private static string HashIdentity(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static IEnumerable<Declaration> EnumerateDeclarations(ModuleDecl module)
    {
        foreach (var declaration in module.Declarations)
        {
            yield return declaration;
            if (declaration is ModuleDecl nested)
            {
                foreach (var child in EnumerateDeclarations(nested))
                {
                    yield return child;
                }
            }
        }
    }
}
