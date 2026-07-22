using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Types;
using Eidosc.Diagnostic;
using Eidosc.Parsing.Handwritten;
using Eidosc.ProjectSystem;
using Eidosc.Symbols;
using Eidosc.Syntax;
using Eidosc.Types;
using Eidosc.Utils;
using EidoscDiagnostic = Eidosc.Diagnostic.Diagnostic;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    internal bool ProcessPackageMetaAnalyzers(ModuleDecl root)
    {
        if (PackageMetaConfiguration is not { Checks.Length: > 0 } configuration)
        {
            return true;
        }

        var functions = CreatePackageMetaFunctionMap();
        var comptimeValues = EvaluateExpansionComptimeBindings(root, functions);
        var success = true;
        foreach (var entry in configuration.Checks)
        {
            if (!TryInvokePackageMetaProgram(
                    root,
                    entry,
                    ClauseStage.Body,
                    ["read-semantics", "read-bodies", "emit-diagnostics"],
                    [],
                    functions,
                    comptimeValues,
                    expectedExtension: false,
                    out _,
                    out var result,
                    out var pendingUserDiagnostics,
                    out var trace,
                    out var reason))
            {
                AddMetaExpansionDiagnostic(root.Span, $"package analyzer '{entry}' failed: {reason}", "E3630");
                success = false;
                continue;
            }

            if (result is not ComptimeSequenceValue { Kind: ComptimeSequenceKind.List } diagnostics ||
                diagnostics.Elements.Any(static element =>
                    element is not ComptimeMetaObjectValue { SchemaKind: "diagnostic" }))
            {
                AddMetaExpansionDiagnostic(
                    root.Span,
                    $"package analyzer '{entry}' must return Seq[meta.Diagnostic]",
                    "E3630");
                success = false;
                continue;
            }

            foreach (var pending in pendingUserDiagnostics)
            {
                AddMetaUserDiagnostic(pending.Level, pending.Span, pending.Message, trace);
            }
            foreach (var diagnostic in diagnostics.Elements.Cast<ComptimeMetaObjectValue>())
            {
                if (!TryAddPackageDiagnostic(diagnostic, trace, out reason))
                {
                    AddMetaExpansionDiagnostic(
                        root.Span,
                        $"package analyzer '{entry}' returned an invalid diagnostic: {reason}",
                        "E3630");
                    success = false;
                }
            }
        }

        return success;
    }

    internal bool ProcessPackageMetaExtensions(ModuleDecl root, ClauseStage stage)
    {
        if (PackageMetaConfiguration is not { Extensions.Length: > 0 } configuration)
        {
            return true;
        }

        if (stage != ClauseStage.Syntax)
        {
            return true;
        }

        var selected = configuration.Extensions;

        ResolveExpansionComptimeDependencies(root);
        var functions = CreatePackageMetaFunctionMap();
        var comptimeValues = EvaluateExpansionComptimeBindings(root, functions);
        var success = true;
        var committed = false;
        foreach (var extension in selected)
        {
            if (!TryInvokePackageMetaProgram(
                    root,
                    extension.Entry,
                    stage,
                    extension.Capabilities,
                    extension.Resources,
                    functions,
                    comptimeValues,
                    expectedExtension: true,
                    out var protocol,
                    out var transformation,
                    out var pendingUserDiagnostics,
                    out var trace,
                    out var reason))
            {
                AddMetaExpansionDiagnostic(
                    root.Span,
                    $"package extension '{extension.Name}' failed: {reason}",
                    "E3631");
                success = false;
                    continue;
            }

            if (!TryApplyPackageProtocolOutput(
                    root,
                    extension,
                    protocol.Kind,
                    transformation,
                    pendingUserDiagnostics,
                    trace,
                    out var transformationChanged,
                    out reason))
            {
                AddMetaExpansionDiagnostic(
                    root.Span,
                    $"package extension '{extension.Name}' failed: {reason}",
                    "E3631");
                success = false;
                continue;
            }

            committed |= transformationChanged;
        }

        if (committed)
        {
            ProcessMetaSyntaxSiteExpansions(root);
            ResolveModuleReferencesRecursive(root, _rootModule);
        }
        return success;
    }

    private bool TryInvokePackageMetaProgram(
        ModuleDecl root,
        string entry,
        ClauseStage stage,
        IReadOnlyList<string> capabilities,
        IReadOnlyList<EidosMetaResourceConfiguration> resources,
        IReadOnlyDictionary<SymbolId, FuncDef> functions,
        IReadOnlyDictionary<SymbolId, ComptimeValue> comptimeValues,
        bool expectedExtension,
        out CompilerMetaProtocolMatch protocol,
        out ComptimeValue result,
        out IReadOnlyList<PendingMetaUserDiagnostic> pendingUserDiagnostics,
        out string trace,
        out string reason)
    {
        protocol = null!;
        result = ComptimeUnitValue.Instance;
        pendingUserDiagnostics = [];
        trace = $"package meta program {entry} at {stage}";
        if (!TryResolvePackageMetaFunction(entry, functions, expectedExtension, out var generator, out var symbol, out protocol, out reason))
        {
            return false;
        }

        var pending = new List<PendingMetaUserDiagnostic>();
        var generatorModuleId = GetDeclarationOwnerModuleId(generator, _rootModule);
        var access = new MetaQueryAccessContext(
            generatorModuleId,
            stage,
            GetPackageMetaQueryCapabilities(capabilities),
            TargetIdentity: PackageMetaConfiguration?.Fingerprint ?? string.Empty,
            TargetTriple: MetaTargetTriple,
            RequesterIdentity: MetaComptimeIntrinsics.CreateStableIdentity(symbol, _symbolTable));
        var context = new MetaComptimeContext(
            _symbolTable,
            _adtDefinitions,
            _traitDefinitions,
            (level, span, message) => pending.Add(new PendingMetaUserDiagnostic(level, span, message)),
            ExpansionTrace: trace,
            ResourceBudget: ComptimeExecution.CreateBudget(),
            Trace: ComptimeExecution.Trace,
            TracePhase: expectedExtension ? "namer.package-extension" : "types.package-analyzer",
            Declarations: _declarationsBySymbol,
            QueryAccess: access,
            DefinitionSiteResolver: CreateDefinitionSiteSyntaxResolver(generatorModuleId));
        if (_symbolTable.Modules.GetModule(_rootModule) is not { } rootModule)
        {
            reason = "package meta protocol requires a root module";
            return false;
        }
        ComptimeValue input = MetaComptimeIntrinsics.CreatePackageHandle(rootModule);

        if (!ComptimeEvaluator.TryInvoke(
                generator,
                [input],
                comptimeValues,
                functions,
                context,
                out result,
                out reason))
        {
            reason = $"generator '{entry}' failed: {reason}; expansion trace: {trace}";
            return false;
        }

        if (pending.Count > 0 && !capabilities.Contains("emit-diagnostics", StringComparer.Ordinal))
        {
            reason = $"generator '{entry}' emitted ambient diagnostics without emit-diagnostics capability";
            return false;
        }

        pendingUserDiagnostics = pending;
        return true;
    }

    private bool TryResolvePackageMetaFunction(
        string entry,
        IReadOnlyDictionary<SymbolId, FuncDef> functions,
        bool expectedExtension,
        out FuncDef generator,
        out FuncSymbol symbol,
        out CompilerMetaProtocolMatch protocol,
        out string reason)
    {
        generator = null!;
        symbol = null!;
        protocol = null!;
        var path = entry.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        SymbolId symbolId;
        if (path.Length == 1)
        {
            var lookup = _lookupService.Lookup(path[0], LookupKind.Value, CreateLookupContext());
            symbolId = lookup.IsSuccess ? lookup.SymbolId : SymbolId.None;
        }
        else
        {
            var lookup = ResolvePathWithImports(path);
            symbolId = lookup.IsSuccess ? lookup.SymbolId : SymbolId.None;
        }
        if (!symbolId.IsValid ||
            _symbolTable.GetSymbol<FuncSymbol>(symbolId) is not { IsComptime: true } resolved ||
            !functions.TryGetValue(symbolId, out generator!))
        {
            reason = $"entry '{entry}' must resolve to a comptime function with an executable body";
            return false;
        }

        if (!CompilerMetaProtocolRegistry.TryClassify(generator, 0, _symbolTable, out protocol, out var protocolReason) ||
            (expectedExtension
                ? protocol.Kind is not (CompilerMetaProtocolKind.ExtensionItems or CompilerMetaProtocolKind.ExtensionModules)
                : protocol.Kind != CompilerMetaProtocolKind.Analyzer))
        {
            reason = string.IsNullOrWhiteSpace(protocolReason)
                ? $"entry '{entry}' does not match the compiler-managed package protocol"
                : protocolReason;
            return false;
        }

        symbol = resolved;
        reason = string.Empty;
        return true;
    }

    private Dictionary<SymbolId, FuncDef> CreatePackageMetaFunctionMap() =>
        _moduleDeclarations.Values
            .SelectMany(EnumerateDeclarations)
            .OfType<FuncDef>()
            .Where(static function => function.SymbolId.IsValid)
            .DistinctBy(static function => function.SymbolId)
            .ToDictionary(static function => function.SymbolId);

    private bool TryApplyPackageProtocolOutput(
        ModuleDecl root,
        EidosMetaExtensionConfiguration extension,
        CompilerMetaProtocolKind protocolKind,
        ComptimeValue value,
        IReadOnlyList<PendingMetaUserDiagnostic> pendingUserDiagnostics,
        string trace,
        out bool changed,
        out string reason)
    {
        changed = false;
        reason = string.Empty;
        if (protocolKind is not (CompilerMetaProtocolKind.ExtensionItems or CompilerMetaProtocolKind.ExtensionModules))
        {
            reason = $"unsupported package extension protocol '{protocolKind}'";
            return false;
        }

        var materializer = new MetaExpansionMaterializer(
            _symbolTable,
            root,
            _rootModule,
            root.Span);
        if (!materializer.TryMaterializeItems(value, out var output, out reason))
        {
            reason = $"package extension must return typed {(protocolKind == CompilerMetaProtocolKind.ExtensionModules ? "meta.Modules" : "meta.Items")}: {reason}";
            return false;
        }
        if (output.Diagnostics.Count > 0)
        {
            reason = "package extensions are emit-only; return diagnostics from a meta.Package -> Seq[meta.Diagnostic] analyzer";
            return false;
        }

        var modules = new List<ModuleDecl>();
        var itemTargets = new Dictionary<SymbolId, ModuleDecl>();
        var itemsByModule = new Dictionary<SymbolId, List<Declaration>>();
        foreach (var generated in output.Nodes.OrderBy(static node => node.OutputIndex))
        {
            if (protocolKind == CompilerMetaProtocolKind.ExtensionModules)
            {
                if (generated.Node is not ModuleDecl module)
                {
                    reason = "meta.Package -> meta.Modules may emit only module declarations";
                    return false;
                }
                modules.Add(module);
                continue;
            }

            if (generated.Node is not Declaration declaration || declaration is ModuleDecl)
            {
                reason = "meta.Package -> meta.Items may emit only non-module declarations";
                return false;
            }
            if (!itemsByModule.TryGetValue(_rootModule, out var declarations))
            {
                declarations = [];
                itemsByModule[_rootModule] = declarations;
                itemTargets[_rootModule] = root;
            }
            declarations.Add(declaration);
        }

        var materialized = modules.Select((module, index) => new MaterializedMetaNode(module, index)).ToArray();
        if (!TryValidateGeneratedMembers(root, materialized, out reason))
        {
            return false;
        }
        foreach (var (moduleId, declarations) in itemsByModule)
        {
            var targetModule = itemTargets[moduleId];
            var itemNodes = declarations
                .Select((declaration, index) => new MaterializedMetaNode(declaration, index))
                .ToArray();
            if (!TryValidateGeneratedMembers(targetModule, itemNodes, out reason))
            {
                return false;
            }
        }

        var generatorPath = extension.Entry.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        SymbolId generatorSymbolId;
        if (generatorPath.Length == 1)
        {
            var lookup = _lookupService.Lookup(generatorPath[0], LookupKind.Value, CreateLookupContext());
            generatorSymbolId = lookup.IsSuccess ? lookup.SymbolId : SymbolId.None;
        }
        else
        {
            var lookup = ResolvePathWithImports(generatorPath);
            generatorSymbolId = lookup.IsSuccess ? lookup.SymbolId : SymbolId.None;
        }
        var generatorSymbol = _symbolTable.GetSymbol(generatorSymbolId);
        var targetSymbol = _symbolTable.GetSymbol(root.SymbolId);
        for (var index = 0; index < modules.Count; index++)
        {
            var slot = $"package-extension|{extension.Name}|{index}|{PackageMetaConfiguration?.Fingerprint}";
            AttachGeneratedOriginChain(modules[index],
            [
                new GeneratedDeclarationOrigin
                {
                    StableIdentity = HashIdentity($"{slot}|{modules[index].Path.LastOrDefault()}"),
                    GenerationSlotIdentity = HashIdentity(slot),
                    GeneratorIdentity = generatorSymbol == null
                        ? extension.Entry
                        : MetaComptimeIntrinsics.CreateStableIdentity(generatorSymbol, _symbolTable),
                    TargetIdentity = targetSymbol == null
                        ? "package"
                        : MetaComptimeIntrinsics.CreateStableIdentity(targetSymbol, _symbolTable),
                    GeneratorSymbolId = generatorSymbol?.Id ?? SymbolId.None,
                    TargetSymbolId = root.SymbolId,
                    ClauseOccurrenceIdentity = $"package-extension:{extension.Name}",
                    ExpansionOutputIndex = index,
                    CanonicalArgumentsHash = HashIdentity(PackageMetaConfiguration?.Fingerprint ?? string.Empty),
                    MetaSchemaVersion = WellKnownStrings.Meta.SchemaVersion,
                    ClauseSpan = root.Span,
                    VirtualDocumentPath = $"eidos-generated://package-extension-{extension.Name}-{index}.eidos"
                }
            ]);
        }

        var itemOutputIndex = modules.Count;
        foreach (var (moduleId, declarations) in itemsByModule.OrderBy(static entry => entry.Key.Value))
        {
            var moduleTarget = _symbolTable.GetSymbol(moduleId);
            foreach (var declaration in declarations)
            {
                var slot = $"package-extension|{extension.Name}|item|{moduleId.Value}|{itemOutputIndex}|{PackageMetaConfiguration?.Fingerprint}";
                var stableIdentity = HashIdentity($"{slot}|{GetGeneratedDeclarationName(declaration)}");
                AttachGeneratedOriginChain(declaration,
                [
                    new GeneratedDeclarationOrigin
                    {
                        StableIdentity = stableIdentity,
                        GenerationSlotIdentity = HashIdentity(slot),
                        GeneratorIdentity = generatorSymbol == null
                            ? extension.Entry
                            : MetaComptimeIntrinsics.CreateStableIdentity(generatorSymbol, _symbolTable),
                        TargetIdentity = moduleTarget == null
                            ? $"module:{moduleId.Value}"
                            : MetaComptimeIntrinsics.CreateStableIdentity(moduleTarget, _symbolTable),
                        GeneratorSymbolId = generatorSymbol?.Id ?? SymbolId.None,
                        TargetSymbolId = moduleId,
                        ClauseOccurrenceIdentity = $"package-extension:{extension.Name}",
                        ExpansionOutputIndex = itemOutputIndex++,
                        CanonicalArgumentsHash = HashIdentity(PackageMetaConfiguration?.Fingerprint ?? string.Empty),
                        MetaSchemaVersion = WellKnownStrings.Meta.SchemaVersion,
                        ClauseSpan = root.Span,
                        VirtualDocumentPath = $"eidos-generated://package-extension-{extension.Name}-{stableIdentity}.eidos"
                    }
                ]);
            }
        }

        var startIndex = root.Declarations.Count;
        root.SetDeclarations([.. root.Declarations, .. modules]);
        foreach (var module in modules)
        {
            RegisterGeneratedItemDeclaration(module, _rootModule);
            if (module.SymbolId.IsValid)
            {
                ProcessImportsRecursive(module, module.SymbolId);
            }
        }
        ResolveModuleDeclarationRange(root, startIndex);
        foreach (var module in modules)
        {
            if (module.GeneratedOriginChain.LastOrDefault() is { } origin)
            {
                SetGeneratedOriginOnOwnedDeclarationSymbols(module, origin);
            }
        }
        foreach (var (moduleId, declarations) in itemsByModule.OrderBy(static entry => entry.Key.Value))
        {
            var targetModule = itemTargets[moduleId];
            var itemStartIndex = targetModule.Declarations.Count;
            targetModule.SetDeclarations([.. targetModule.Declarations, .. declarations]);
            foreach (var declaration in declarations)
            {
                RegisterGeneratedItemDeclaration(declaration, moduleId);
            }
            if (declarations.Any(static declaration => declaration is ImportDecl))
            {
                ProcessImportsRecursive(targetModule, moduleId);
            }
            ResolveModuleDeclarationRange(targetModule, itemStartIndex);
            foreach (var declaration in declarations)
            {
                if (declaration.GeneratedOriginChain.LastOrDefault() is { } origin)
                {
                    SetGeneratedOriginOnOwnedDeclarationSymbols(declaration, origin);
                }
            }
        }
        foreach (var pending in pendingUserDiagnostics)
        {
            AddMetaUserDiagnostic(pending.Level, pending.Span, pending.Message, trace);
        }
        changed = modules.Count > 0 || itemsByModule.Values.Any(static declarations => declarations.Count > 0);
        reason = string.Empty;
        return true;
    }

    private bool TryAddPackageDiagnostic(
        ComptimeMetaObjectValue diagnostic,
        string trace,
        out string reason)
    {
        if (!diagnostic.TryGet("level", out var levelValue) || levelValue is not ComptimeStringValue level ||
            !diagnostic.TryGet("span", out var spanValue) || spanValue is not ComptimeMetaObjectValue spanObject ||
            !MetaComptimeIntrinsics.TryReadSpan(spanObject, out var span) ||
            !diagnostic.TryGet("message", out var messageValue) || messageValue is not ComptimeStringValue message ||
            level.Value is not ("error" or "warning"))
        {
            reason = "diagnostic requires level, span, and message";
            return false;
        }

        span = RestorePackageMetaDiagnosticFile(span);
        var entry = level.Value == "error"
            ? EidoscDiagnostic.Error(message.Value, "E3632")
            : EidoscDiagnostic.Warning(message.Value, "W3632");
        entry.WithLabel(span, message.Value);
        entry.WithNote($"package meta trace: {trace}");
        if (diagnostic.TryGet("fix", out var fixValue) &&
            fixValue is ComptimeMetaObjectValue { SchemaKind: "fix" } fix &&
            fix.TryGet("span", out var fixSpanValue) && fixSpanValue is ComptimeMetaObjectValue fixSpanObject &&
            MetaComptimeIntrinsics.TryReadSpan(fixSpanObject, out var fixSpan) &&
            fix.TryGet("replacement", out var replacementValue) && replacementValue is ComptimeStringValue replacement)
        {
            entry.WithSuggestion(
                "Apply analyzer fix",
                SuggestionKind.StyleRewrite,
                RestorePackageMetaDiagnosticFile(fixSpan),
                replacement.Value);
        }
        _diagnostics.Add(entry);
        reason = string.Empty;
        return true;
    }

    private SourceSpan RestorePackageMetaDiagnosticFile(SourceSpan span)
    {
        if (string.IsNullOrWhiteSpace(span.FilePath) ||
            !span.FilePath.StartsWith("eidos-source://", StringComparison.Ordinal))
        {
            return span;
        }

        var module = _symbolTable.Modules.GetModules().FirstOrDefault(candidate =>
            string.Equals(
                MetaComptimeIntrinsics.CreatePublicSourceUri(candidate.Span, _symbolTable),
                span.FilePath,
                StringComparison.Ordinal));
        return module?.Span.FilePath == null
            ? span
            : new SourceSpan(
                new SourceLocation(
                    span.Position,
                    span.Location.Line,
                    span.Location.Column,
                    module.Span.FilePath),
                span.Length);
    }

    private static MetaQueryCapability GetPackageMetaQueryCapabilities(IReadOnlyList<string> capabilities)
    {
        var result = MetaQueryCapability.None;
        if (capabilities.Contains("read-syntax", StringComparer.Ordinal) ||
            capabilities.Contains("read-semantics", StringComparer.Ordinal))
        {
            result |= MetaQueryCapability.CurrentPackagePrivateShapes;
        }
        if (capabilities.Contains("read-bodies", StringComparer.Ordinal))
        {
            result |= MetaQueryCapability.CurrentPackageBodies;
        }
        if (capabilities.Contains("read-layout", StringComparer.Ordinal))
        {
            result |= MetaQueryCapability.Layout;
        }
        return result;
    }

}
