using Eidosc.Symbols;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Types;
using Eidosc.Diagnostic;
using Eidosc.Utils;
using EidoscDiagnostic = Eidosc.Diagnostic.Diagnostic;
using EidoscDiagnosticLevel = Eidosc.Diagnostic.DiagnosticLevel;
using EidoscSuggestionKind = Eidosc.Diagnostic.SuggestionKind;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private PatternDiagnosticContextScope PushPatternDiagnosticContext(string segment)
    {
        _patternDiagnosticContext.Add(segment);
        return new PatternDiagnosticContextScope(_patternDiagnosticContext);
    }

    private ModuleScopeGuard PushCollectionModuleScope(SymbolId parentModuleId, SymbolId moduleId)
    {
        return EnterModuleScopeForCollection(moduleId, parentModuleId)
            ? new ModuleScopeGuard(_symbolTable)
            : default;
    }

    private ModuleScopeGuard PushResolutionModuleScope(SymbolId moduleId)
    {
        return EnterModuleScopeForResolution(moduleId)
            ? new ModuleScopeGuard(_symbolTable)
            : default;
    }

    private CurrentModuleScope PushCurrentModuleScope(SymbolId moduleId)
    {
        var previousModule = _currentModule;
        _currentModule = moduleId;
        return new CurrentModuleScope(this, previousModule);
    }

    private void AddPatternError(SourceSpan span, string message)
    {
        if (_patternDiagnosticContext.Count == 0)
        {
            AddError(span, message);
            return;
        }

        var contextPath = string.Join(" > ", _patternDiagnosticContext);
        AddError(span, DiagnosticMessages.PatternDiagnosticWithContext(message, contextPath));
    }

    private void AddUndefinedIdentifierError(SourceSpan span, string name)
    {
        if (string.Equals(name, WellKnownStrings.Keywords.Resume, StringComparison.Ordinal))
        {
            var resumeMessage = DiagnosticMessages.ResumeOutsideHandlerBranch;
            var resumeDiagnostic = new EidoscDiagnostic(EidoscDiagnosticLevel.Error, resumeMessage, "E3012");
            resumeDiagnostic.WithLabel(span, resumeMessage);
            _diagnostics.Add(resumeDiagnostic);
            return;
        }

        var message = DiagnosticMessages.UndefinedIdentifier(name);
        var diag = new EidoscDiagnostic(EidoscDiagnosticLevel.Error, message, "E3000");
        diag.WithLabel(span, message);

        foreach (var suggestion in BuildImportSuggestions(name, span))
        {
            diag.WithSuggestion(
                suggestion.Message,
                EidoscSuggestionKind.AddImport,
                suggestion.Span,
                suggestion.Replacement);
        }

        _diagnostics.Add(diag);
    }

    private void AddPathResolutionError(
        SourceSpan span,
        IReadOnlyList<string> path,
        string message,
        bool requireTypeLikeTarget = false)
    {
        var diag = new EidoscDiagnostic(EidoscDiagnosticLevel.Error, message, "E3000");
        diag.WithLabel(span, message);

        foreach (var suggestion in BuildQualifiedPathImportSuggestions(
                     path,
                     span,
                     requireTypeLikeTarget ? ImportSuggestionTargetKind.TypeLike : ImportSuggestionTargetKind.Any))
        {
            diag.WithSuggestion(
                suggestion.Message,
                EidoscSuggestionKind.AddImport,
                suggestion.Span,
                suggestion.Replacement);
        }

        _diagnostics.Add(diag);
    }

    private void AddUndefinedTraitError(SourceSpan span, TraitRef traitRef, string message)
    {
        var diag = new EidoscDiagnostic(EidoscDiagnosticLevel.Error, message, "E3000");
        diag.WithLabel(span, message);

        var path = new List<string>(traitRef.ModulePath);
        if (!string.IsNullOrWhiteSpace(traitRef.TraitName))
        {
            path.Add(traitRef.TraitName);
        }

        foreach (var suggestion in BuildQualifiedPathImportSuggestions(path, span, ImportSuggestionTargetKind.TypeLike))
        {
            diag.WithSuggestion(
                suggestion.Message,
                EidoscSuggestionKind.AddImport,
                suggestion.Span,
                suggestion.Replacement);
        }

        _diagnostics.Add(diag);
    }

    private void AddUndefinedEffectError(SourceSpan span, string abilityDisplayName, string message)
    {
        var diag = new EidoscDiagnostic(EidoscDiagnosticLevel.Error, message, "E3000");
        diag.WithLabel(span, message);

        var path = abilityDisplayName
            .Replace(WellKnownStrings.Separators.ModulePath, WellKnownStrings.Separators.Path, StringComparison.Ordinal)
            .Split(WellKnownStrings.Separators.Path, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();

        IEnumerable<ImportSuggestionCandidate> suggestions = path.Count switch
        {
            0 => [],
            1 => BuildImportSuggestions(path[0], span),
            _ => BuildQualifiedPathImportSuggestions(path, span, ImportSuggestionTargetKind.Effect)
        };

        foreach (var suggestion in suggestions)
        {
            diag.WithSuggestion(
                suggestion.Message,
                EidoscSuggestionKind.AddImport,
                suggestion.Span,
                suggestion.Replacement);
        }

        _diagnostics.Add(diag);
    }

    private void AddUndefinedImplTraitError(SourceSpan span, ImplTraitReference traitRef, string message)
    {
        var diag = new EidoscDiagnostic(EidoscDiagnosticLevel.Error, message, "E3000");
        diag.WithLabel(span, message);

        foreach (var suggestion in BuildImplTraitImportSuggestions(traitRef, span))
        {
            diag.WithSuggestion(
                suggestion.Message,
                EidoscSuggestionKind.AddImport,
                suggestion.Span,
                suggestion.Replacement);
        }

        _diagnostics.Add(diag);
    }

    private void AddError(SourceSpan span, string message)
    {
        var diag = new EidoscDiagnostic(EidoscDiagnosticLevel.Error, message, "E3000");
        diag.WithLabel(span, message);
        _diagnostics.Add(diag);
    }

    private void AddError(SourceSpan span, string message, string code)
    {
        var diag = new EidoscDiagnostic(EidoscDiagnosticLevel.Error, message, code);
        diag.WithLabel(span, message);
        _diagnostics.Add(diag);
    }

    private void AddReservedInternalNameError(SourceSpan span, string name, string prefix, string declarationKind)
    {
        var message = DiagnosticMessages.ReservedInternalNameDeclaration(name, prefix, declarationKind);
        var diag = new EidoscDiagnostic(EidoscDiagnosticLevel.Error, message, "E3055");
        diag.WithLabel(span, message);
        diag.WithHelp(DiagnosticMessages.ReservedInternalNameDeclarationHelp);
        _diagnostics.Add(diag);
    }

    private IEnumerable<ImportSuggestionCandidate> BuildImportSuggestions(string name, SourceSpan errorSpan)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            yield break;
        }

        var candidates = CollectImportCandidateModules(name);

        if (candidates.Count == 0)
        {
            yield break;
        }

        foreach (var modulePath in candidates.OrderBy(static path => path, StringComparer.Ordinal))
        {
            if (TryCreateImportSuggestionCandidate(modulePath, name, errorSpan) is not { } suggestion)
            {
                continue;
            }

            yield return suggestion;
        }
    }

    private IEnumerable<ImportSuggestionCandidate> BuildQualifiedPathImportSuggestions(
        IReadOnlyList<string> path,
        SourceSpan errorSpan,
        ImportSuggestionTargetKind targetKind)
    {
        if (path.Count < 2 || path.Any(string.IsNullOrWhiteSpace))
        {
            yield break;
        }

        var moduleLeafName = path[0];
        var relativePath = path.Skip(1).ToList();
        var candidates = CollectQualifiedImportCandidateModules(moduleLeafName, relativePath, targetKind);

        foreach (var modulePath in candidates.OrderBy(static path => path, StringComparer.Ordinal))
        {
            if (TryCreateModuleImportSuggestionCandidate(modulePath, errorSpan) is not { } suggestion)
            {
                continue;
            }

            yield return suggestion;
        }
    }

    private IEnumerable<ImportSuggestionCandidate> BuildImplTraitImportSuggestions(
        ImplTraitReference traitRef,
        SourceSpan errorSpan)
    {
        if (traitRef.Path.Count == 1)
        {
            foreach (var suggestion in BuildImportSuggestions(traitRef.Path[0], errorSpan))
            {
                yield return suggestion;
            }

            yield break;
        }

        foreach (var suggestion in BuildQualifiedPathImportSuggestions(
                     traitRef.Path,
                     errorSpan,
                     ImportSuggestionTargetKind.TypeLike))
        {
            yield return suggestion;
        }
    }

    private HashSet<string> CollectImportCandidateModules(string name)
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidateModulePath in ModuleImportSuggestionLocator.FindPrecompiledImportCandidateModules(name))
        {
            candidates.Add(NormalizeSuggestionModulePath(candidateModulePath));
        }

        foreach (var candidateModulePath in FindLoadedImportCandidateModules(name))
        {
            candidates.Add(NormalizeSuggestionModulePath(candidateModulePath));
        }

        if (TryGetCurrentInputFilePath() is { } currentInputFile)
        {
            foreach (var candidateModulePath in ModuleImportSuggestionLocator.FindWorkspaceImportCandidateModules(
                         currentInputFile,
                         _importSearchRoots,
                         name))
            {
                candidates.Add(NormalizeSuggestionModulePath(candidateModulePath));
            }
        }

        return candidates;
    }

    private HashSet<string> CollectQualifiedImportCandidateModules(
        string moduleLeafName,
        IReadOnlyList<string> relativePath,
        ImportSuggestionTargetKind targetKind)
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidateModulePath in ModuleImportSuggestionLocator.FindPrecompiledQualifiedImportCandidateModules(
                     moduleLeafName,
                     relativePath,
                     targetKind))
        {
            candidates.Add(NormalizeSuggestionModulePath(candidateModulePath));
        }

        foreach (var candidateModulePath in FindLoadedQualifiedImportCandidateModules(
                     moduleLeafName,
                     relativePath,
                     targetKind))
        {
            candidates.Add(NormalizeSuggestionModulePath(candidateModulePath));
        }

        if (TryGetCurrentInputFilePath() is { } currentInputFile)
        {
            foreach (var candidateModulePath in ModuleImportSuggestionLocator.FindWorkspaceQualifiedImportCandidateModules(
                         currentInputFile,
                         _importSearchRoots,
                         moduleLeafName,
                         relativePath,
                         targetKind))
            {
                candidates.Add(NormalizeSuggestionModulePath(candidateModulePath));
            }
        }

        return candidates;
    }

    private ImportSuggestionCandidate? TryCreateImportSuggestionCandidate(string modulePath, string name, SourceSpan errorSpan)
    {
        if (TryGetCurrentModuleDecl() is not { } module)
        {
            return null;
        }

        return ImportSuggestionComposer.TryCreateMemberSuggestion(module, _sourceText, errorSpan, modulePath, name) is { } suggestion
            ? new ImportSuggestionCandidate(suggestion.Message, suggestion.Span, suggestion.Replacement)
            : null;
    }

    private ImportSuggestionCandidate? TryCreateModuleImportSuggestionCandidate(string modulePath, SourceSpan errorSpan)
    {
        if (TryGetCurrentModuleDecl() is not { } module)
        {
            return null;
        }

        return ImportSuggestionComposer.TryCreateModuleSuggestion(module, _sourceText, errorSpan, modulePath) is { } suggestion
            ? new ImportSuggestionCandidate(suggestion.Message, suggestion.Span, suggestion.Replacement)
            : null;
    }

    private IEnumerable<string> FindLoadedImportCandidateModules(string name)
    {
        foreach (var modulePathEntry in _symbolTable.Modules.ModulePaths)
        {
            var moduleId = modulePathEntry.Value;
            if (!_currentModule.IsValid || moduleId == _currentModule)
            {
                continue;
            }

            var module = _symbolTable.Modules.GetModule(moduleId);
            if (module?.Path == null || module.Path.Count == 0)
            {
                continue;
            }

            foreach (var binding in _symbolTable.Modules.GetAccessibleBindings(moduleId, _currentModule))
            {
                var symbol = _symbolTable.GetSymbol(binding.SymbolId);
                if (symbol == null ||
                    !string.Equals(binding.Name, name, StringComparison.Ordinal) ||
                    !IsImportableMemberSymbol(symbol))
                {
                    continue;
                }

                yield return string.Join(WellKnownStrings.Operators.Divide, module.Path);
                break;
            }
        }
    }

    private IEnumerable<string> FindLoadedQualifiedImportCandidateModules(
        string moduleLeafName,
        IReadOnlyList<string> relativePath,
        ImportSuggestionTargetKind targetKind)
    {
        foreach (var modulePathEntry in _symbolTable.Modules.ModulePaths)
        {
            var moduleId = modulePathEntry.Value;
            if (!_currentModule.IsValid || moduleId == _currentModule)
            {
                continue;
            }

            var module = _symbolTable.Modules.GetModule(moduleId);
            if (module?.Path is not { Count: > 0 } modulePath ||
                !string.Equals(modulePath[^1], moduleLeafName, StringComparison.Ordinal))
            {
                continue;
            }

            if (!LoadedModuleMatchesQualifiedImportCandidate(moduleId, relativePath, targetKind))
            {
                continue;
            }

            yield return string.Join(WellKnownStrings.Operators.Divide, modulePath);
        }
    }

    private bool LoadedModuleMatchesQualifiedImportCandidate(
        SymbolId moduleId,
        IReadOnlyList<string> relativePath,
        ImportSuggestionTargetKind targetKind)
    {
        if (TryResolveImportableLoadedModulePath(moduleId, relativePath, out var symbol))
        {
            return targetKind switch
            {
                ImportSuggestionTargetKind.TypeLike => IsTypeLikeImportableMemberSymbol(symbol),
                ImportSuggestionTargetKind.Effect => IsEffectImportableMemberSymbol(symbol),
                _ => IsImportableMemberSymbol(symbol)
            };
        }

        return false;
    }

    private bool TryResolveImportableLoadedModulePath(
        SymbolId moduleId,
        IReadOnlyList<string> relativePath,
        out Symbol resolvedSymbol)
    {
        resolvedSymbol = null!;
        if (!moduleId.IsValid || relativePath.Count == 0)
        {
            return false;
        }

        if (TryFindPublicModuleMember(moduleId, relativePath[0], out var memberSymbol))
        {
            if (relativePath.Count == 1)
            {
                resolvedSymbol = memberSymbol;
                return true;
            }

            return TryResolveImportableLoadedMemberPath(
                memberSymbol,
                relativePath.Skip(1).ToList(),
                out resolvedSymbol);
        }

        if (relativePath.Count == 1 &&
            TryFindPublicSameNamedTraitOrEffectMember(moduleId, relativePath[0], out resolvedSymbol))
        {
            return true;
        }

        return false;
    }

    private bool TryResolveImportableLoadedMemberPath(
        Symbol symbol,
        IReadOnlyList<string> remainingPath,
        out Symbol resolvedSymbol)
    {
        resolvedSymbol = null!;
        if (remainingPath.Count == 0)
        {
            return false;
        }

        switch (symbol)
        {
            case ModuleSymbol module:
                return TryResolveImportableLoadedModulePath(module.Id, remainingPath, out resolvedSymbol);

            case TraitSymbol trait when remainingPath.Count == 1:
                return TryFindPublicTraitMethod(trait, remainingPath[0], out resolvedSymbol);

            case AdtSymbol adt when remainingPath.Count == 1:
                return TryFindPublicConstructor(adt, remainingPath[0], out resolvedSymbol);

            default:
                return false;
        }
    }

    private bool TryFindPublicModuleMember(SymbolId moduleId, string memberName, out Symbol symbol)
    {
        symbol = null!;
        if (_symbolTable.Modules.TryLookupAccessibleBinding(moduleId, memberName, _currentModule, out var binding))
        {
            var candidate = _symbolTable.GetSymbol(binding.SymbolId);
            if (candidate == null)
            {
                return false;
            }

            symbol = candidate;
            return true;
        }

        return false;
    }

    private bool TryFindPublicSameNamedTraitOrEffectMember(SymbolId moduleId, string memberName, out Symbol symbol)
    {
        symbol = null!;
        var module = _symbolTable.Modules.GetModule(moduleId);
        if (module?.Path is not { Count: > 0 } modulePath)
        {
            return false;
        }

        var ownerName = modulePath[^1];
        if (!_symbolTable.Modules.TryLookupAccessibleBinding(moduleId, ownerName, _currentModule, out var ownerBinding))
        {
            return false;
        }

        var ownerSymbol = _symbolTable.GetSymbol(ownerBinding.SymbolId);
        if (ownerSymbol is TraitSymbol trait &&
            TryFindPublicTraitMethod(trait, memberName, out symbol))
        {
            return true;
        }

        return false;
    }

    private static bool PrecompiledOwnerDefinesMember(
        string modulePath,
        string ownerName,
        string memberName)
    {
        return PrecompiledModuleRegistry.ExportedOwnerDefinesMember(modulePath, ownerName, memberName);
    }

    private bool TryFindPublicTraitMethod(TraitSymbol trait, string memberName, out Symbol symbol)
    {
        symbol = null!;
        foreach (var methodId in trait.Methods)
        {
            if (_symbolTable.GetSymbol(methodId) is not FuncSymbol method ||
                !method.IsPublic ||
                !string.Equals(method.Name, memberName, StringComparison.Ordinal))
            {
                continue;
            }

            symbol = method;
            return true;
        }

        return false;
    }

    private bool TryFindPublicConstructor(AdtSymbol adt, string ctorName, out Symbol symbol)
    {
        symbol = null!;
        foreach (var ctorId in adt.Constructors)
        {
            if (_symbolTable.GetSymbol(ctorId) is not CtorSymbol ctor ||
                !ctor.IsPublic ||
                !string.Equals(ctor.Name, ctorName, StringComparison.Ordinal))
            {
                continue;
            }

            symbol = ctor;
            return true;
        }

        return false;
    }

    private string? TryGetCurrentInputFilePath()
    {
        if (TryGetCurrentModuleDecl() is not { } module ||
            string.IsNullOrWhiteSpace(module.Span.FilePath))
        {
            return null;
        }

        return Path.GetFullPath(module.Span.FilePath);
    }

    private ModuleDecl? TryGetCurrentModuleDecl()
    {
        return _currentModule.IsValid && _moduleDeclarations.TryGetValue(_currentModule, out var module)
            ? module
            : null;
    }

    private static string NormalizeSuggestionModulePath(string modulePath)
    {
        if (string.IsNullOrWhiteSpace(modulePath))
        {
            return modulePath;
        }

        var normalized = modulePath.Replace('\\', '/');
        const string precompiledMarker = "/Stdlib/Precompiled/";
        var markerIndex = normalized.IndexOf(precompiledMarker, StringComparison.Ordinal);
        return markerIndex >= 0
            ? normalized[(markerIndex + precompiledMarker.Length)..]
            : normalized;
    }

    private static bool IsImportableMemberSymbol(Symbol symbol)
    {
        return symbol is FuncSymbol or AdtSymbol or TraitSymbol or EffectSymbol or CtorSymbol or ModuleSymbol;
    }

    private static bool IsTypeLikeImportableMemberSymbol(Symbol symbol)
    {
        return symbol is AdtSymbol or TraitSymbol;
    }

    private static bool IsEffectImportableMemberSymbol(Symbol symbol)
    {
        return symbol is EffectSymbol;
    }

    private void AddWarning(SourceSpan span, string message, string code, string label, string? help = null)
    {
        var diag = new EidoscDiagnostic(EidoscDiagnosticLevel.Warning, message, code);
        diag.WithLabel(span, label);
        if (!string.IsNullOrWhiteSpace(help))
        {
            diag.WithHelp(help);
        }

        _diagnostics.Add(diag);
    }

    private readonly struct PatternDiagnosticContextScope(List<string> context) : IDisposable
    {
        private readonly List<string> _context = context;

        public void Dispose()
        {
            if (_context.Count > 0)
            {
                _context.RemoveAt(_context.Count - 1);
            }
        }
    }

    private readonly struct ModuleScopeGuard(SymbolTable? symbolTable) : IDisposable
    {
        private readonly SymbolTable? _symbolTable = symbolTable;

        public void Dispose()
        {
            _symbolTable?.PopScope();
        }
    }

    private readonly struct CurrentModuleScope(NameResolver? resolver, SymbolId previousModule) : IDisposable
    {
        private readonly NameResolver? _resolver = resolver;
        private readonly SymbolId _previousModule = previousModule;

        public void Dispose()
        {
            if (_resolver != null)
            {
                _resolver._currentModule = _previousModule;
            }
        }
    }
}
