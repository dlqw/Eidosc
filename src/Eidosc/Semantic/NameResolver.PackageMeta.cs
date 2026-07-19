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
                    expectedTransformation: false,
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

        var selected = configuration.Extensions
            .Where(extension => ParsePackageMetaStage(extension.Stage) == stage)
            .ToArray();
        if (selected.Length == 0)
        {
            return true;
        }

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
                    expectedTransformation: true,
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

            if (!TryApplyPackageTransformation(
                    root,
                    extension,
                    stage,
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
        bool expectedTransformation,
        out ComptimeValue result,
        out IReadOnlyList<PendingMetaUserDiagnostic> pendingUserDiagnostics,
        out string trace,
        out string reason)
    {
        result = ComptimeUnitValue.Instance;
        pendingUserDiagnostics = [];
        trace = $"package meta program {entry} at {stage}";
        if (!TryResolvePackageMetaFunction(entry, functions, expectedTransformation, out var generator, out var symbol, out var protocol, out reason))
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
            TracePhase: expectedTransformation ? "namer.package-extension" : "types.package-analyzer",
            Declarations: _declarationsBySymbol,
            QueryAccess: access,
            DefinitionSiteResolver: CreateDefinitionSiteSyntaxResolver(generatorModuleId));
        ComptimeValue input;
        if (protocol.Kind == CompilerMetaProtocolKind.Analyzer)
        {
            if (_symbolTable.Modules.GetModule(_rootModule) is not { } rootModule)
            {
                reason = "package analyzer requires a root module";
                return false;
            }
            input = MetaComptimeIntrinsics.CreatePackageHandle(rootModule);
        }
        else
        {
            if (!TryCreatePackageQuery(root, stage, capabilities, resources, context, out var query, out reason))
            {
                return false;
            }
            input = query;
        }

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
        bool expectedTransformation,
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

        if (CompilerMetaProtocolRegistry.TryClassify(
                generator,
                0,
                _symbolTable,
                out protocol,
                out _) &&
            ((!expectedTransformation && protocol.Kind == CompilerMetaProtocolKind.Analyzer) ||
             (expectedTransformation && protocol.Kind is CompilerMetaProtocolKind.ExtensionItems or CompilerMetaProtocolKind.ExtensionModules)))
        {
            symbol = resolved;
            reason = string.Empty;
            return true;
        }

        if (generator.Signature.Count != 1 ||
            generator.Signature[0] is not ArrowType arrow ||
            !IsPackageQueryType(arrow.ParamType) ||
            (expectedTransformation
                ? !IsMetaType(arrow.ReturnType, WellKnownTypeIds.MetaTransformationId)
                : !IsMetaDiagnosticSequence(arrow.ReturnType)))
        {
            reason = expectedTransformation
                ? $"entry '{entry}' must have type meta.Query[meta.ScopeKind.Package] -> meta.Transformation"
                : $"entry '{entry}' must have type meta.Query[meta.ScopeKind.Package] -> Seq[meta.Diagnostic]";
            return false;
        }

        symbol = resolved;
        protocol = new CompilerMetaProtocolMatch(
            CompilerMetaProtocolKind.LegacyTransformation,
            ClauseStage.Semantic);
        reason = string.Empty;
        return true;
    }

    private bool IsPackageQueryType(TypeNode node)
    {
        if (node is not TypePath { TypeArgs.Count: 1 } query ||
            !IsMetaType(query, WellKnownTypeIds.MetaQueryId) ||
            query.TypeArgs[0] is not TypePath marker)
        {
            return false;
        }

        return string.Equals(marker.TypeName, "Package", StringComparison.Ordinal) &&
               (marker.ModulePath.Contains(WellKnownStrings.Meta.Types.ScopeKind, StringComparer.Ordinal) ||
                _symbolTable.GetSymbol<AdtSymbol>(marker.SymbolId) is { ParentAdt.IsValid: true } package &&
                _symbolTable.GetSymbol<AdtSymbol>(package.ParentAdt)?.TypeId.Value == WellKnownTypeIds.MetaScopeKindId);
    }

    private bool IsMetaDiagnosticSequence(TypeNode node) =>
        node is TypePath { TypeArgs.Count: 1 } sequence &&
        string.Equals(sequence.TypeName, WellKnownStrings.BuiltinTypes.Seq, StringComparison.Ordinal) &&
        IsMetaType(sequence.TypeArgs[0], WellKnownTypeIds.MetaDiagnosticId);

    private Dictionary<SymbolId, FuncDef> CreatePackageMetaFunctionMap() =>
        _moduleDeclarations.Values
            .SelectMany(EnumerateDeclarations)
            .OfType<FuncDef>()
            .Where(static function => function.SymbolId.IsValid)
            .DistinctBy(static function => function.SymbolId)
            .ToDictionary(static function => function.SymbolId);

    private bool TryCreatePackageQuery(
        ModuleDecl root,
        ClauseStage stage,
        IReadOnlyList<string> capabilities,
        IReadOnlyList<EidosMetaResourceConfiguration> resources,
        MetaComptimeContext context,
        out ComptimeMetaObjectValue query,
        out string reason)
    {
        query = null!;
        if (_symbolTable.Modules.GetModule(_rootModule) is not { } rootModule)
        {
            reason = "package query requires a root module";
            return false;
        }

        var package = MetaComptimeIntrinsics.CreatePackageHandle(rootModule);
        if (!MetaComptimeIntrinsics.TryCreateScopeForPackageQuery(package, context, out var scope, out reason))
        {
            return false;
        }

        var resourceValues = resources.Select(resource => (ComptimeValue)new ComptimeMetaObjectValue(
            "resource",
            [
                new ComptimeNamedValue("declaredInput", new ComptimeStringValue(resource.DeclaredInput)),
                new ComptimeNamedValue("path", new ComptimeStringValue(resource.RelativePath)),
                new ComptimeNamedValue("exists", new ComptimeBoolValue(resource.Exists)),
                new ComptimeNamedValue("content", new ComptimeStringValue(resource.Content ?? string.Empty)),
                new ComptimeNamedValue("contentHash", new ComptimeStringValue(resource.ContentHash))
            ])
        {
            StaticType = MetaSchemaRegistry.MetaType(
                WellKnownStrings.Meta.Types.Resource,
                WellKnownTypeIds.MetaResourceId)
        }).ToArray();
        var capabilityValues = capabilities
            .Select(static capability => (ComptimeValue)new ComptimeStringValue(capability))
            .ToArray();
        var packageScopeKind = _symbolTable.Symbols.Values
            .OfType<AdtSymbol>()
            .FirstOrDefault(symbol =>
                string.Equals(symbol.Name, "Package", StringComparison.Ordinal) &&
                symbol.ParentAdt.IsValid &&
                _symbolTable.GetSymbol<AdtSymbol>(symbol.ParentAdt)?.TypeId.Value == WellKnownTypeIds.MetaScopeKindId);
        query = new ComptimeMetaObjectValue(
            "package-query",
            [
                new ComptimeNamedValue("identity", new ComptimeStringValue(
                    HashIdentity($"package-query|{PackageMetaConfiguration?.Fingerprint}|{stage}|{string.Join(',', capabilities)}"))),
                new ComptimeNamedValue("package", package),
                new ComptimeNamedValue("scope", scope),
                new ComptimeNamedValue("stage", new ComptimeStringValue(stage.ToString().ToLowerInvariant())),
                new ComptimeNamedValue("capabilities", new ComptimeSequenceValue(ComptimeSequenceKind.List, capabilityValues)),
                new ComptimeNamedValue("resources", new ComptimeSequenceValue(ComptimeSequenceKind.List, resourceValues)),
                new ComptimeNamedValue("root", MetaComptimeIntrinsics.CreateDeclValue(
                    _symbolTable.GetSymbol(root.SymbolId)!,
                    _symbolTable))
            ])
        {
            StaticType = MetaSchemaRegistry.MetaType(
                WellKnownStrings.Meta.Types.Query,
                WellKnownTypeIds.MetaQueryId)
                with
                {
                    Args =
                    [
                        packageScopeKind == null
                            ? MetaSchemaRegistry.MetaType("Package", WellKnownTypeIds.MetaScopeKindId)
                            : new TyCon { Name = packageScopeKind.Name, Id = packageScopeKind.TypeId }
                    ]
                }
        };
        reason = string.Empty;
        return true;
    }

    private bool TryApplyPackageTransformation(
        ModuleDecl root,
        EidosMetaExtensionConfiguration extension,
        ClauseStage stage,
        ComptimeValue value,
        IReadOnlyList<PendingMetaUserDiagnostic> pendingUserDiagnostics,
        string trace,
        out bool changed,
        out string reason)
    {
        changed = false;
        reason = string.Empty;
        if (value is not ComptimeMetaObjectValue { SchemaKind: "transformation" } transformation ||
            !transformation.TryGet("edits", out var editValue) ||
            editValue is not ComptimeSequenceValue { Kind: ComptimeSequenceKind.List } edits)
        {
            reason = "extension must return an opaque meta.Transformation";
            return false;
        }

        var modules = new List<ModuleDecl>();
        var itemTargets = new Dictionary<SymbolId, ModuleDecl>();
        var itemsByModule = new Dictionary<SymbolId, List<Declaration>>();
        var diagnostics = new List<ComptimeMetaObjectValue>();
        var materializer = new MetaExpansionMaterializer(
            _symbolTable,
            root,
            _rootModule,
            root.Span);
        foreach (var editValueItem in edits.Elements)
        {
            if (editValueItem is not ComptimeMetaObjectValue { SchemaKind: "transformation-edit" } edit ||
                !edit.TryGet("kind", out var kindValue) ||
                kindValue is not ComptimeStringValue kind)
            {
                reason = "package transformation contains an invalid edit";
                return false;
            }

            if (kind.Value == "report-diagnostic")
            {
                if (!extension.Capabilities.Contains("emit-diagnostics", StringComparer.Ordinal) ||
                    !edit.TryGet("diagnostics", out var diagnosticValue) ||
                    diagnosticValue is not ComptimeSequenceValue diagnosticSequence ||
                    diagnosticSequence.Elements.Any(static element =>
                        element is not ComptimeMetaObjectValue { SchemaKind: "diagnostic" }))
                {
                    reason = "package diagnostic edit requires emit-diagnostics and typed diagnostics";
                    return false;
                }
                diagnostics.AddRange(diagnosticSequence.Elements.Cast<ComptimeMetaObjectValue>());
                continue;
            }

            if (kind.Value is "add-module" or "add-items" && stage > ClauseStage.Semantic)
            {
                reason = $"package extension {kind.Value} edits are not permitted after the Semantic stage; current stage is {stage}";
                return false;
            }

            if (kind.Value == "add-items")
            {
                if (!extension.Capabilities.Contains("emit-items", StringComparer.Ordinal) ||
                    !edit.TryGet("module", out var moduleValue) ||
                    !MetaComptimeIntrinsics.TryResolveModuleHandle(moduleValue, _symbolTable, out var moduleSymbol) ||
                    !_moduleDeclarations.TryGetValue(moduleSymbol.Id, out var targetModule) ||
                    !IsCurrentPackageModule(moduleSymbol) ||
                    !edit.TryGet("syntax", out var syntaxValue) ||
                    syntaxValue is not ComptimeSequenceValue { Kind: ComptimeSequenceKind.List } itemSyntax)
                {
                    reason = "package extension add-items edit requires emit-items, a current-package module, and typed item syntax";
                    return false;
                }

                if (!itemsByModule.TryGetValue(moduleSymbol.Id, out var declarations))
                {
                    declarations = [];
                    itemsByModule[moduleSymbol.Id] = declarations;
                    itemTargets[moduleSymbol.Id] = targetModule;
                }

                foreach (var itemValue in itemSyntax.Elements)
                {
                    if (itemValue is not ComptimeSyntaxValue syntax ||
                        !materializer.TryMaterializeSyntax(
                            syntax,
                            SyntaxCategory.Item,
                            SyntaxMemberGrammar.Any,
                            out var nodes,
                            out reason) ||
                        nodes.Any(static node => node is not Declaration or ModuleDecl))
                    {
                        reason = string.IsNullOrWhiteSpace(reason)
                            ? "package extension add-items edit must contain non-module item declarations"
                            : reason;
                        return false;
                    }

                    declarations.AddRange(nodes.Cast<Declaration>());
                }
                continue;
            }

            if (kind.Value == "add-module" &&
                extension.Capabilities.Contains("emit-modules", StringComparer.Ordinal) &&
                edit.TryGet("syntax", out var moduleSyntaxValue) &&
                moduleSyntaxValue is ComptimeSyntaxValue moduleSyntax &&
                materializer.TryMaterializeSyntax(
                    moduleSyntax,
                    SyntaxCategory.Item,
                    SyntaxMemberGrammar.Any,
                    out var moduleNodes,
                    out reason) &&
                moduleNodes.Count == 1 &&
                moduleNodes[0] is ModuleDecl module)
            {
                modules.Add(module);
                continue;
            }

            reason = string.IsNullOrWhiteSpace(reason)
                ? $"package extension contains unsupported or unauthorized edit '{kind.Value}'"
                : reason;
            return false;
        }

        var materialized = modules.Select((module, index) => new MaterializedMetaNode(module, index)).ToArray();
        if (!TryValidateGeneratedMembers(root, materialized, out reason) ||
            !TryValidateGeneratedModuleDeclarationCollisions(
                _rootModule,
                modules.Cast<Declaration>().ToArray(),
                null,
                out reason))
        {
            return false;
        }
        foreach (var (moduleId, declarations) in itemsByModule)
        {
            var targetModule = itemTargets[moduleId];
            var itemNodes = declarations
                .Select((declaration, index) => new MaterializedMetaNode(declaration, index))
                .ToArray();
            if (!TryValidateGeneratedMembers(targetModule, itemNodes, out reason) ||
                !TryValidateGeneratedModuleDeclarationCollisions(
                    moduleId,
                    declarations,
                    null,
                    out reason))
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
        }
        foreach (var pending in pendingUserDiagnostics)
        {
            AddMetaUserDiagnostic(pending.Level, pending.Span, pending.Message, trace);
        }
        foreach (var diagnostic in diagnostics)
        {
            if (!TryAddPackageDiagnostic(diagnostic, trace, out reason))
            {
                throw new InvalidOperationException($"validated package diagnostic commit failed: {reason}");
            }
        }

        changed = modules.Count > 0 || itemsByModule.Values.Any(static declarations => declarations.Count > 0);
        reason = string.Empty;
        return true;
    }

    private bool IsCurrentPackageModule(ModuleSymbol module)
    {
        if (_symbolTable.Modules.GetModule(_rootModule) is not { } rootModule)
        {
            return false;
        }

        var rootPackage = rootModule.PackageInstanceKey ?? rootModule.PackageAlias ?? ModuleIdentity.CurrentPackageInstanceKey;
        var modulePackage = module.PackageInstanceKey ?? module.PackageAlias ?? ModuleIdentity.CurrentPackageInstanceKey;
        return string.Equals(rootPackage, modulePackage, StringComparison.Ordinal);
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

    private static ClauseStage ParsePackageMetaStage(string stage) => stage switch
    {
        "syntax" => ClauseStage.Syntax,
        "body" => ClauseStage.Body,
        "layout" => ClauseStage.Layout,
        _ => ClauseStage.Semantic
    };
}
