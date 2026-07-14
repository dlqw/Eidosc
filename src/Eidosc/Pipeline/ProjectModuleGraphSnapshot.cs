namespace Eidosc.Pipeline;

using System.Xml;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Types;
using Eidosc.Symbols;
using Eidosc.Types;

public sealed record ProjectModuleGraphSnapshot(
    IReadOnlyList<ProjectModuleGraphNode> Nodes,
    IReadOnlyList<IReadOnlyList<string>> TopologicalLayers)
{
    public static ProjectModuleGraphSnapshot FromDependencyGraph(ModuleDependencyGraph graph)
    {
        var nodes = graph.AllModules
            .OrderBy(static module => module, StringComparer.Ordinal)
            .Select(module => new ProjectModuleGraphNode(
                ModuleKey: module,
                SourcePaths: graph.GetSourcePathsForModuleKey(module).OrderBy(static path => path, StringComparer.Ordinal).ToArray(),
                Dependencies: graph.GetDependencies(module).OrderBy(static dependency => dependency, StringComparer.Ordinal).ToArray(),
                Dependents: graph.GetDependents(module).OrderBy(static dependent => dependent, StringComparer.Ordinal).ToArray()))
            .ToList();

        return new ProjectModuleGraphSnapshot(nodes, BuildTopologicalLayers(nodes));
    }

    private static IReadOnlyList<IReadOnlyList<string>> BuildTopologicalLayers(IReadOnlyList<ProjectModuleGraphNode> nodes)
    {
        var remainingDependencies = nodes.ToDictionary(
            static node => node.ModuleKey,
            static node => new HashSet<string>(node.Dependencies, StringComparer.Ordinal),
            StringComparer.Ordinal);
        var dependents = nodes.ToDictionary(
            static node => node.ModuleKey,
            static node => node.Dependents.ToArray(),
            StringComparer.Ordinal);

        var ready = remainingDependencies
            .Where(static pair => pair.Value.Count == 0)
            .Select(static pair => pair.Key)
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToList();
        var layers = new List<IReadOnlyList<string>>();
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        while (ready.Count > 0)
        {
            var layer = ready;
            layers.Add(layer);
            ready = [];

            foreach (var module in layer)
            {
                emitted.Add(module);
                if (!dependents.TryGetValue(module, out var directDependents))
                {
                    continue;
                }

                foreach (var dependent in directDependents)
                {
                    if (!remainingDependencies.TryGetValue(dependent, out var deps))
                    {
                        continue;
                    }

                    deps.Remove(module);
                    if (deps.Count == 0 && !emitted.Contains(dependent))
                    {
                        ready.Add(dependent);
                    }
                }
            }

            ready.Sort(StringComparer.Ordinal);
        }

        var cyclicOrUnreachable = remainingDependencies
            .Where(pair => pair.Value.Count > 0 && !emitted.Contains(pair.Key))
            .Select(static pair => pair.Key)
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToList();
        if (cyclicOrUnreachable.Count > 0)
        {
            layers.Add(cyclicOrUnreachable);
        }

        return layers;
    }
}

public sealed record ProjectModuleGraphNode(
    string ModuleKey,
    IReadOnlyList<string> SourcePaths,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> Dependents);

public sealed record ProjectModuleSignatureSnapshot(
    IReadOnlyList<ProjectModuleSignatureNode> Nodes)
{
    public static ProjectModuleSignatureSnapshot FromGraphSnapshot(
        ProjectModuleGraphSnapshot graph,
        Func<string, string?> sourceTextProvider,
        string languageVersion,
        string flagsHash)
    {
        var signatures = new Dictionary<string, ProjectModuleSignatureNode>(StringComparer.Ordinal);
        var nodesByKey = graph.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal);
        var sourceHashesByModule = ComputeSourceHashes(graph.Nodes, sourceTextProvider);
        foreach (var layer in graph.TopologicalLayers)
        {
            foreach (var moduleKey in layer.OrderBy(static key => key, StringComparer.Ordinal))
            {
                if (!nodesByKey.TryGetValue(moduleKey, out var node))
                {
                    continue;
                }

                var sourceHash = sourceHashesByModule[moduleKey];
                var dependencySignatureHash = ModuleArtifactHash.ComputeDependencySignatureHash(
                    node.Dependencies
                        .Select(dependency => signatures.TryGetValue(dependency, out var signature)
                            ? signature.SignatureHash
                            : ModuleArtifactHash.ComputeTextHash($"missing:{dependency}")));
                var signatureHash = ModuleArtifactHash.ComputeJsonHash(new
                {
                    node.ModuleKey,
                    SourceHash = sourceHash,
                    DependencySignatureHash = dependencySignatureHash,
                    LanguageVersion = languageVersion,
                    FlagsHash = flagsHash
                });

                signatures[moduleKey] = new ProjectModuleSignatureNode(
                    node.ModuleKey,
                    node.SourcePaths,
                    node.Dependencies,
                    sourceHash,
                    dependencySignatureHash,
                    signatureHash);
            }
        }

        return new ProjectModuleSignatureSnapshot(
            signatures.Values
                .OrderBy(static node => node.ModuleKey, StringComparer.Ordinal)
                .ToArray());
    }

    private static IReadOnlyDictionary<string, string> ComputeSourceHashes(
        IReadOnlyList<ProjectModuleGraphNode> nodes,
        Func<string, string?> sourceTextProvider)
    {
        if (nodes.Count < 4)
        {
            return nodes.ToDictionary(
                static node => node.ModuleKey,
                node => ComputeSourceHash(node.SourcePaths, sourceTextProvider),
                StringComparer.Ordinal);
        }

        var sourceHashes = new string[nodes.Count];
        Parallel.For(
            0,
            nodes.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) },
            index =>
            {
                sourceHashes[index] = ComputeSourceHash(nodes[index].SourcePaths, sourceTextProvider);
            });

        var result = new Dictionary<string, string>(nodes.Count, StringComparer.Ordinal);
        for (var i = 0; i < nodes.Count; i++)
        {
            result[nodes[i].ModuleKey] = sourceHashes[i];
        }

        return result;
    }

    private static string ComputeSourceHash(
        IReadOnlyList<string> sourcePaths,
        Func<string, string?> sourceTextProvider)
    {
        var parts = new List<string>();
        foreach (var sourcePath in sourcePaths.OrderBy(static path => path, StringComparer.Ordinal))
        {
            var sourceText = sourceTextProvider(sourcePath);
            var sourceHash = sourceText == null
                ? ModuleArtifactHash.ComputeTextHash($"missing-source:{sourcePath}")
                : ModuleArtifactHash.ComputeSourceHash(sourceText);
            parts.Add($"{sourcePath}\0{sourceHash}");
        }

        return ModuleArtifactHash.ComputeSourceHash(string.Join('\n', parts));
    }
}

public sealed record ProjectModuleSignatureNode(
    string ModuleKey,
    IReadOnlyList<string> SourcePaths,
    IReadOnlyList<string> Dependencies,
    string SourceHash,
    string DependencySignatureHash,
    string SignatureHash);

public sealed record ProjectModuleSemanticSignatureSnapshot(
    string SchemaVersion,
    IReadOnlyList<ProjectModuleSemanticSignatureNode> Nodes)
{
    public const string CurrentSchemaVersion = "semantic-signature-snapshot-v1";

    public static ProjectModuleSemanticSignatureSnapshot FromGraphSnapshot(
        ProjectModuleGraphSnapshot graph,
        IReadOnlyDictionary<string, ModuleDecl> modulesByKey,
        string languageVersion,
        string flagsHash)
    {
        var signatures = new Dictionary<string, ProjectModuleSemanticSignatureNode>(StringComparer.Ordinal);
        var nodesByKey = graph.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal);
        foreach (var layer in graph.TopologicalLayers)
        {
            foreach (var moduleKey in layer.OrderBy(static key => key, StringComparer.Ordinal))
            {
                if (!nodesByKey.TryGetValue(moduleKey, out var graphNode))
                {
                    continue;
                }

                var declarations = modulesByKey.TryGetValue(moduleKey, out var moduleDecl)
                    ? ProjectModuleSemanticSignatureBuilder.Build(moduleKey, moduleDecl)
                    : [ProjectModuleSemanticDeclarationSignature.MissingModule(moduleKey)];
                var exportSurfaceHash = ModuleArtifactHash.ComputeJsonHash(declarations);
                var dependencySemanticSignatureHash = ModuleArtifactHash.ComputeDependencySignatureHash(
                    graphNode.Dependencies.Select(dependency => signatures.TryGetValue(dependency, out var signature)
                        ? signature.SemanticSignatureHash
                        : ModuleArtifactHash.ComputeTextHash($"missing-semantic:{dependency}")));
                var semanticSignatureHash = ModuleArtifactHash.ComputeJsonHash(new
                {
                    graphNode.ModuleKey,
                    ExportSurfaceHash = exportSurfaceHash,
                    DependencySemanticSignatureHash = dependencySemanticSignatureHash,
                    LanguageVersion = languageVersion,
                    FlagsHash = flagsHash
                });

                signatures[moduleKey] = new ProjectModuleSemanticSignatureNode(
                    moduleKey,
                    graphNode.Dependencies,
                    declarations,
                    exportSurfaceHash,
                    dependencySemanticSignatureHash,
                    semanticSignatureHash);
            }
        }

        return new ProjectModuleSemanticSignatureSnapshot(
            CurrentSchemaVersion,
            signatures.Values
                .OrderBy(static node => node.ModuleKey, StringComparer.Ordinal)
                .ToArray());
    }
}

public sealed record ProjectModuleSemanticSignatureNode(
    string ModuleKey,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<ProjectModuleSemanticDeclarationSignature> Declarations,
    string ExportSurfaceHash,
    string DependencySemanticSignatureHash,
    string SemanticSignatureHash);

public sealed record ProjectModuleSemanticDeclarationSignature(
    string Kind,
    string Name,
    IReadOnlyList<string> TypeParams,
    string Signature,
    IReadOnlyList<string> Members,
    IReadOnlyList<string> Attributes,
    bool IsExported,
    bool IsExternal)
{
    public static ProjectModuleSemanticDeclarationSignature MissingModule(string moduleKey) =>
        new(
            "missing-module",
            moduleKey,
            [],
            "",
            [],
            [],
            IsExported: false,
            IsExternal: false);
}

internal static class ProjectModuleSemanticSignatureBuilder
{
    public static IReadOnlyList<ProjectModuleSemanticDeclarationSignature> Build(
        string moduleKey,
        ModuleDecl moduleDecl)
    {
        var exportedOnly = moduleDecl.UsesExplicitExports;
        return moduleDecl.Declarations
            .Where(declaration => ShouldIncludeDeclaration(declaration, exportedOnly))
            .SelectMany(declaration => BuildDeclarationSignatures(moduleKey, declaration))
            .OrderBy(static signature => signature.Kind, StringComparer.Ordinal)
            .ThenBy(static signature => signature.Name, StringComparer.Ordinal)
            .ThenBy(static signature => signature.Signature, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool ShouldIncludeDeclaration(Declaration declaration, bool exportedOnly)
    {
        return declaration is not ModuleDecl &&
               (!exportedOnly || declaration.IsExported);
    }

    private static IEnumerable<ProjectModuleSemanticDeclarationSignature> BuildDeclarationSignatures(
        string moduleKey,
        Declaration declaration)
    {
        switch (declaration)
        {
            case FuncDef function:
                yield return CreateFunction("function", function.Name, function.TypeParams, function.Signature, function.RequiredAbilities, function.Attributes, function.IsExported, function.Body.Count == 0);
                yield break;
            case FuncDecl function:
                yield return CreateFunction("function-declaration", function.Name, function.TypeParams, function.Signature, function.RequiredAbilities, function.Attributes, function.IsExported, isExternal: false);
                yield break;
            case AdtDef adt:
                yield return CreateAdt(adt);
                yield break;
            case TraitDef trait:
                yield return CreateTrait(trait);
                yield break;
            case EffectDef ability:
                yield return CreateEffect(ability);
                yield break;
            case InstanceDecl instance:
                yield return CreateInstance(instance);
                yield break;
            case ImportDecl import when import.IsExported:
                yield return CreateExportedImport(import);
                yield break;
            case LetDecl let:
                yield return CreateLet(moduleKey, let);
                yield break;
        }
    }

    private static ProjectModuleSemanticDeclarationSignature CreateFunction(
        string kind,
        string name,
        IReadOnlyList<TypeParam> typeParams,
        IReadOnlyList<TypeNode> signature,
        IReadOnlyList<EffectRequirementNode> requiredAbilities,
        IReadOnlyList<Attribute> attributes,
        bool isExported,
        bool isExternal)
    {
        var renderedSignature = string.Join(" | ", signature.Select(RenderType));
        var required = requiredAbilities.Count == 0
            ? ""
            : $" need {string.Join(",", requiredAbilities.Select(RenderEffectRequirement).OrderBy(static value => value, StringComparer.Ordinal))}";
        return new ProjectModuleSemanticDeclarationSignature(
            kind,
            name,
            RenderTypeParams(typeParams),
            renderedSignature + required,
            [],
            RenderAttributes(attributes),
            isExported,
            isExternal || HasFfiAttribute(attributes));
    }

    private static ProjectModuleSemanticDeclarationSignature CreateAdt(AdtDef adt)
    {
        var members = new List<string>();
        if (adt.IsTypeAlias)
        {
            members.Add($"alias:{RenderType(adt.AliasTarget)}");
        }

        members.AddRange(adt.Fields
            .Select(static field => $"field:{field.Name}:{RenderType(field.Type)}"));
        members.AddRange(adt.Constructors
            .Select(RenderConstructor));

        return new ProjectModuleSemanticDeclarationSignature(
            adt.IsTypeAlias ? "type-alias" : "type",
            adt.Name,
            RenderTypeParams(adt.TypeParams),
            "",
            members.OrderBy(static member => member, StringComparer.Ordinal).ToArray(),
            RenderAttributes(adt.Attributes),
            adt.IsExported,
            IsExternal: false);
    }

    private static ProjectModuleSemanticDeclarationSignature CreateTrait(TraitDef trait)
    {
        var members = new List<string>();
        members.AddRange(trait.SuperTraits.Select(static traitRef => $"super:{RenderTraitRef(traitRef)}"));
        members.AddRange(trait.AssociatedTypes.Select(static associated => $"associated-type:{associated.Name}:{string.Join(",", RenderTypeParams(associated.TypeParams))}:{RenderType(associated.ValueType)}"));
        members.AddRange(trait.AssociatedConsts.Select(static associated => $"associated-const:{associated.Name}:{RenderType(associated.Type)}"));
        members.AddRange(trait.Methods.Select(method => $"method:{method.Name}:{string.Join(",", RenderTypeParams(method.TypeParams))}:{string.Join(" | ", method.Signature.Select(RenderType))}"));

        return new ProjectModuleSemanticDeclarationSignature(
            "trait",
            trait.Name,
            RenderTypeParams(trait.TypeParams),
            "",
            members.OrderBy(static member => member, StringComparer.Ordinal).ToArray(),
            RenderAttributes(trait.Attributes),
            trait.IsExported,
            IsExternal: false);
    }

    private static ProjectModuleSemanticDeclarationSignature CreateEffect(EffectDef ability)
    {
        return new ProjectModuleSemanticDeclarationSignature(
            "effect",
            ability.Name,
            [],
            "",
            [],
            RenderAttributes(ability.Attributes),
            ability.IsExported,
            IsExternal: false);
    }

    private static ProjectModuleSemanticDeclarationSignature CreateInstance(InstanceDecl instance)
    {
        var members = new List<string>();
        members.AddRange(instance.Methods.Select(method => $"method:{method.Name}:{string.Join(",", RenderTypeParams(method.TypeParams))}:{string.Join(" | ", method.Signature.Select(RenderType))}"));
        members.AddRange(instance.AssociatedTypes.Select(static associated => $"associated-type:{associated.Name}:{string.Join(",", RenderTypeParams(associated.TypeParams))}:{RenderType(associated.ValueType)}"));
        members.AddRange(instance.AssociatedConsts.Select(static associated => $"associated-const:{associated.Name}:{RenderType(associated.Type)}"));
        members.AddRange(instance.ConstructorBridgeFacts.Select(static fact => $"constructor-fact:{fact.ConstructorName}"));

        return new ProjectModuleSemanticDeclarationSignature(
            "instance",
            instance.Name,
            RenderTypeParams(instance.TypeParams),
            $"{RenderTraitRef(instance.Trait)} for {RenderType(instance.TargetType)}",
            members.OrderBy(static member => member, StringComparer.Ordinal).ToArray(),
            RenderAttributes(instance.Attributes),
            instance.IsExported,
            IsExternal: false);
    }

    private static ProjectModuleSemanticDeclarationSignature CreateExportedImport(ImportDecl import)
    {
        var members = import.Kind == ImportKind.Selective
            ? import.SelectiveImports
                .Select(static item => item.Alias == null ? item.Name : $"{item.Name} as {item.Alias}")
                .OrderBy(static member => member, StringComparer.Ordinal)
                .ToArray()
            : [];
        return new ProjectModuleSemanticDeclarationSignature(
            "exported-import",
            RenderImportPath(import),
            [],
            $"{import.Kind}:{import.Alias ?? ""}",
            members,
            [],
            IsExported: true,
            IsExternal: false);
    }

    private static ProjectModuleSemanticDeclarationSignature CreateLet(string moduleKey, LetDecl let)
    {
        return new ProjectModuleSemanticDeclarationSignature(
            "value",
            RenderLetName(moduleKey, let),
            [],
            $"{(let.IsMutable ? "mut " : "")}{RenderType(let.TypeAnnotation)}",
            [],
            RenderAttributes(let.Attributes),
            let.IsExported,
            IsExternal: false);
    }

    private static string RenderLetName(string moduleKey, LetDecl let)
    {
        if (let.Pattern != null)
        {
            var xml = RenderAstNode(let.Pattern);
            return string.IsNullOrWhiteSpace(xml)
                ? $"{moduleKey}:value@{let.Span.Position}"
                : xml;
        }

        return $"{moduleKey}:value@{let.Span.Position}";
    }

    private static string RenderConstructor(Constructor constructor)
    {
        var positional = constructor.PositionalArgs.Select(RenderType);
        var named = constructor.NamedArgs.Select(static field => $"{field.Name}:{RenderType(field.Type)}");
        return $"constructor:{constructor.Name}[{string.Join(",", RenderTypeParams(constructor.TypeParams))}]({string.Join(",", positional.Concat(named))})->{RenderType(constructor.ReturnType)}";
    }

    private static string RenderImportPath(ImportDecl import)
    {
        var parts = import.ToQualifiedModulePath();
        return string.Join(WellKnownStrings.Separators.Path, parts);
    }

    private static IReadOnlyList<string> RenderTypeParams(IReadOnlyList<TypeParam> typeParams)
    {
        return typeParams
            .Select(static typeParam => $"{typeParam.Name}:{typeParam.GetKindText()}")
            .OrderBy(static typeParam => typeParam, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> RenderAttributes(IReadOnlyList<Attribute> attributes)
    {
        return attributes
            .Select(static attribute => $"{attribute.Name}({string.Join(",", attribute.ArgumentTexts.OrderBy(static text => text, StringComparer.Ordinal))})")
            .OrderBy(static attribute => attribute, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool HasFfiAttribute(IReadOnlyList<Attribute> attributes) =>
        attributes.Any(static attribute => string.Equals(attribute.Name, WellKnownStrings.Keywords.Ffi, StringComparison.Ordinal));

    private static string RenderEffectRequirement(EffectRequirementNode requirement) =>
        string.Join(WellKnownStrings.Separators.Path, requirement.Path);

    private static string RenderTraitRef(TraitRef? traitRef) =>
        traitRef == null ? "" : RenderAstNode(traitRef);

    private static string RenderType(TypeNode? typeNode) =>
        typeNode == null ? "" : RenderAstNode(typeNode);

    private static string RenderAstNode(EidosAstNode node)
    {
        var document = new XmlDocument();
        var element = node.ToXmlElement(document);
        document.AppendChild(document.ImportNode(element, deep: true));
        return document.OuterXml;
    }
}

public sealed record ProjectModuleTypedSemanticSnapshot(
    string SchemaVersion,
    IReadOnlyList<ProjectModuleTypedSemanticNode> Nodes)
{
    public const string CurrentSchemaVersion = "typed-semantic-snapshot-v3";

    public static ProjectModuleTypedSemanticSnapshot FromGraphSnapshot(
        ProjectModuleGraphSnapshot graph,
        SymbolTable symbolTable,
        string languageVersion,
        string flagsHash)
    {
        var signatures = new Dictionary<string, ProjectModuleTypedSemanticNode>(StringComparer.Ordinal);
        var nodesByKey = graph.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal);
        foreach (var layer in graph.TopologicalLayers)
        {
            foreach (var moduleKey in layer.OrderBy(static key => key, StringComparer.Ordinal))
            {
                if (!nodesByKey.TryGetValue(moduleKey, out var graphNode))
                {
                    continue;
                }

                var declarations = ProjectModuleTypedSemanticSignatureBuilder.Build(moduleKey, symbolTable);
                var localSurfaceDeclarations = declarations
                    .Select(static declaration => new
                    {
                        declaration.Kind,
                        declaration.CanonicalName,
                        declaration.CanonicalType,
                        declaration.IsPublic,
                        declaration.CanonicalFacts,
                        declaration.CanonicalHash
                    })
                    .DistinctBy(static declaration => declaration.CanonicalHash)
                    .OrderBy(static declaration => declaration.Kind, StringComparer.Ordinal)
                    .ThenBy(static declaration => declaration.CanonicalName, StringComparer.Ordinal)
                    .ThenBy(static declaration => declaration.CanonicalType, StringComparer.Ordinal)
                    .ThenBy(static declaration => declaration.IsPublic)
                    .ThenBy(static declaration => declaration.CanonicalHash, StringComparer.Ordinal)
                    .ToArray();
                var localSurfaceHash = ModuleArtifactHash.ComputeJsonHash(localSurfaceDeclarations);
                var dependencyTypedSemanticHash = ModuleArtifactHash.ComputeDependencySignatureHash(
                    graphNode.Dependencies.Select(dependency => signatures.TryGetValue(dependency, out var signature)
                        ? signature.TypedSemanticHash
                        : ModuleArtifactHash.ComputeTextHash($"missing-typed-semantic:{dependency}")));
                var typedSemanticHash = ModuleArtifactHash.ComputeJsonHash(new
                {
                    graphNode.ModuleKey,
                    LocalSurfaceHash = localSurfaceHash,
                    DependencyTypedSemanticHash = dependencyTypedSemanticHash,
                    LanguageVersion = languageVersion,
                    FlagsHash = flagsHash,
                    SchemaVersion = CurrentSchemaVersion
                });

                signatures[moduleKey] = new ProjectModuleTypedSemanticNode(
                    moduleKey,
                    graphNode.Dependencies,
                    declarations,
                    localSurfaceHash,
                    dependencyTypedSemanticHash,
                    typedSemanticHash);
            }
        }

        return new ProjectModuleTypedSemanticSnapshot(
            CurrentSchemaVersion,
            signatures.Values
                .OrderBy(static node => node.ModuleKey, StringComparer.Ordinal)
                .ToArray());
    }
}

public sealed record ProjectModuleTypedSemanticNode(
    string ModuleKey,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<ProjectModuleTypedSemanticDeclaration> Declarations,
    string LocalSurfaceHash,
    string DependencyTypedSemanticHash,
    string TypedSemanticHash);

public sealed record ProjectModuleTypedSemanticDeclaration(
    string Kind,
    string Name,
    string CanonicalName,
    string CanonicalType,
    int SymbolId,
    int TypeId,
    bool IsPublic,
    IReadOnlyList<string> CanonicalFacts,
    string CanonicalHash);

internal static class ProjectModuleTypedSemanticSignatureBuilder
{
    public static IReadOnlyList<ProjectModuleTypedSemanticDeclaration> Build(
        string moduleKey,
        SymbolTable symbolTable)
    {
        var moduleId = symbolTable.Modules.ModulePaths.TryGetValue(moduleKey, out var resolvedModuleId)
            ? resolvedModuleId
            : SymbolId.None;
        if (!moduleId.IsValid)
        {
            return [];
        }

        return EnumerateModuleSurfaceSymbols(symbolTable, moduleId)
            .Where(static symbol => symbol != null)
            .Cast<Symbol>()
            .DistinctBy(static symbol => symbol.Id)
            .Select(symbol => CreateDeclaration(symbolTable, symbol))
            .OrderBy(static declaration => declaration.Kind, StringComparer.Ordinal)
            .ThenBy(static declaration => declaration.Name, StringComparer.Ordinal)
            .ThenBy(static declaration => declaration.SymbolId)
            .ToArray();
    }

    private static IEnumerable<Symbol?> EnumerateModuleSurfaceSymbols(SymbolTable symbolTable, SymbolId moduleId)
    {
        foreach (var memberId in symbolTable.Modules.GetModuleMembers(moduleId))
        {
            var symbol = symbolTable.GetSymbol(memberId);
            yield return symbol;

            if (symbol is ModuleSymbol)
            {
                foreach (var nestedMember in EnumerateModuleSurfaceSymbols(symbolTable, memberId))
                {
                    yield return nestedMember;
                }
            }
        }

        foreach (var binding in symbolTable.Modules.GetModuleExports(moduleId))
        {
            yield return symbolTable.GetSymbol(binding.SymbolId);
        }
    }

    private static ProjectModuleTypedSemanticDeclaration CreateDeclaration(
        SymbolTable symbolTable,
        Symbol symbol)
    {
        var canonicalFacts = BuildFacts(symbolTable, symbol, canonical: true)
            .OrderBy(static fact => fact, StringComparer.Ordinal)
            .ToArray();
        var canonicalName = FormatCanonicalSymbolName(symbolTable, symbol);
        var canonicalType = FormatCanonicalType(symbolTable, symbol.TypeId);
        var canonicalHash = ModuleArtifactHash.ComputeJsonHash(new
        {
            symbol.Kind,
            CanonicalName = canonicalName,
            CanonicalType = canonicalType,
            symbol.IsPublic,
            Facts = canonicalFacts
        });

        return new ProjectModuleTypedSemanticDeclaration(
            symbol.Kind.ToString(),
            symbol.Name,
            canonicalName,
            canonicalType,
            symbol.Id.Value,
            symbol.TypeId.Value,
            symbol.IsPublic,
            canonicalFacts,
            canonicalHash);
    }

    private static IEnumerable<string> BuildFacts(SymbolTable symbolTable, Symbol symbol, bool canonical)
    {
        yield return $"moduleLevel:{symbol.IsModuleLevel}";
        yield return $"typeResolved:{symbol.IsTypeResolved}";

        switch (symbol)
        {
            case FuncSymbol function:
                foreach (var fact in BuildFunctionFacts(symbolTable, function, canonical))
                {
                    yield return fact;
                }

                break;
            case AdtSymbol adt:
                foreach (var fact in BuildAdtFacts(symbolTable, adt, canonical))
                {
                    yield return fact;
                }

                break;
            case CtorSymbol constructor:
                foreach (var fact in BuildConstructorFacts(symbolTable, constructor, canonical))
                {
                    yield return fact;
                }

                break;
            case TraitSymbol trait:
                foreach (var fact in BuildTraitFacts(symbolTable, trait, canonical))
                {
                    yield return fact;
                }

                break;
            case EffectSymbol ability:
                foreach (var fact in BuildEffectFacts(symbolTable, ability, canonical))
                {
                    yield return fact;
                }

                break;
            case ImplSymbol impl:
                foreach (var fact in BuildImplFacts(symbolTable, impl, canonical))
                {
                    yield return fact;
                }

                break;
            case VarSymbol variable:
                yield return $"mutable:{variable.IsMutable}";
                yield return $"comptime:{variable.IsComptime}";
                yield return $"parameter:{variable.IsParameter}";
                yield return $"patternBound:{variable.IsPatternBound}";
                yield return $"bindingMode:{variable.BindingMode}";
                yield return $"valueType:{FormatType(symbolTable, variable.Type, canonical)}";
                yield return $"scheme:{variable.Scheme?.ToString() ?? ""}";
                break;
            case FieldSymbol field:
                yield return $"fieldType:{FormatType(symbolTable, field.FieldType, canonical)}";
                yield return $"owner:{FormatSymbol(symbolTable, field.OwnerType, canonical)}";
                yield return $"index:{field.Index}";
                break;
            case TypeParamSymbol typeParam:
                yield return $"kind:{typeParam.KindAnnotation}";
                yield return $"parameterKind:{typeParam.ParameterKind}";
                yield return $"comptime:{typeParam.IsComptime}";
                yield return $"comptimeType:{typeParam.ComptimeTypeAnnotation ?? ""}";
                yield return $"traitConstraints:{FormatSymbols(symbolTable, typeParam.TraitConstraints, canonical)}";
                break;
        }
    }

    private static IEnumerable<string> BuildFunctionFacts(
        SymbolTable symbolTable,
        FuncSymbol function,
        bool canonical)
    {
        yield return $"typeParams:{FormatSymbols(symbolTable, function.TypeParams, canonical)}";
        yield return $"parameters:{FormatSymbols(symbolTable, function.Parameters, canonical)}";
        yield return $"paramTypes:{FormatTypes(symbolTable, function.ParamTypes, canonical)}";
        yield return $"returnType:{FormatType(symbolTable, function.ReturnType, canonical)}";
        yield return $"abilities:{FormatEffectIds(function.Effects)}";
        yield return $"implicitAbilities:{string.Join(",", function.ImplicitAbilities.OrderBy(static value => value, StringComparer.Ordinal))}";
        yield return $"hasBody:{function.HasBody}";
        yield return $"comptime:{function.IsComptime}";
        yield return $"ownerTrait:{FormatSymbol(symbolTable, function.OwnerTrait ?? SymbolId.None, canonical)}";
        yield return $"traitSelfPosition:{function.TraitSelfPosition}";
        yield return $"traitSelfParameterIndices:{string.Join(",", function.TraitSelfParameterIndices.OrderBy(static value => value))}";
        yield return $"traitSelfInResult:{function.TraitSelfInResult}";
        yield return $"traitMethodRole:{function.TraitMethodRole}";
        yield return $"defaultImplementation:{function.IsDefaultImplementation}";
        yield return $"traitImplementation:{function.IsTraitImplementation}";
        yield return $"external:{function.IsExternal}";
        yield return $"externalSymbol:{function.ExternalSymbolName ?? ""}";
        yield return $"externalLibrary:{function.ExternalLibrary ?? ""}";
        yield return $"cstructAccessor:{function.IsCStructAccessor}";
        yield return $"cstructOffset:{function.CStructFieldOffset}";
        yield return $"cstructFieldType:{FormatType(symbolTable, function.CStructFieldTypeId, canonical)}";
        yield return $"cstructGetter:{function.IsCStructGetter}";
        yield return $"intrinsic:{function.IntrinsicName ?? ""}";
        yield return $"builtinIntrinsicRole:{function.BuiltinIntrinsicRole}";
        if (function.EffectSummary is { } effectSummary)
        {
            yield return $"declaredEffects:{FormatEffectRow(symbolTable, effectSummary.DeclaredUpperBound, canonical)}";
            yield return $"inferredEffects:{FormatEffectRow(symbolTable, effectSummary.InferredEffects, canonical)}";
        }
    }

    private static string FormatEffectRow(
        SymbolTable symbolTable,
        EffectRow row,
        bool canonical)
    {
        var tags = row.Effects
            .Select(effect => effect.Symbol.IsValid
                ? FormatSymbol(symbolTable, effect.Symbol, canonical)
                : effect.Name)
            .OrderBy(static value => value, StringComparer.Ordinal);
        var variables = Enumerable.Range(0, row.Variables.Count)
            .Select(static index => $"$e{index}");
        return string.Join(",", tags.Concat(variables));
    }

    private static IEnumerable<string> BuildAdtFacts(
        SymbolTable symbolTable,
        AdtSymbol adt,
        bool canonical)
    {
        yield return $"typeParams:{FormatSymbols(symbolTable, adt.TypeParams, canonical)}";
        yield return $"constructors:{FormatSymbols(symbolTable, adt.Constructors, canonical)}";
        yield return $"fields:{FormatSymbols(symbolTable, adt.Fields, canonical)}";
        yield return $"aliasTarget:{FormatType(symbolTable, adt.AliasTarget ?? TypeId.None, canonical)}";
        yield return $"cstruct:{adt.IsCStruct}";

        if (adt.CStructLayoutInfo == null)
        {
            yield break;
        }

        yield return $"cstructSize:{adt.CStructLayoutInfo.TotalSize}";
        yield return $"cstructAlignment:{adt.CStructLayoutInfo.Alignment}";
        foreach (var field in adt.CStructLayoutInfo.Fields)
        {
            var fieldTypeName = symbolTable.GetSymbolByTypeId(field.TypeId)?.Name ?? "";
            yield return $"cstructField:{field.Name}:{FormatType(symbolTable, field.TypeId, canonical)}:{fieldTypeName}:{field.Offset}:{field.Size}:{field.Alignment}";
        }
    }

    private static IEnumerable<string> BuildConstructorFacts(
        SymbolTable symbolTable,
        CtorSymbol constructor,
        bool canonical)
    {
        yield return $"ownerAdt:{FormatSymbol(symbolTable, constructor.OwnerAdt, canonical)}";
        yield return $"typeParams:{FormatSymbols(symbolTable, constructor.TypeParams, canonical)}";
        yield return $"positionalArgs:{FormatTypes(symbolTable, constructor.PositionalArgs, canonical)}";
        yield return $"signatureText:{constructor.SignatureText ?? ""}";
        yield return $"namedFields:{FormatSymbols(symbolTable, constructor.NamedFields, canonical)}";
        yield return $"nullary:{constructor.IsNullary}";
    }

    private static IEnumerable<string> BuildTraitFacts(
        SymbolTable symbolTable,
        TraitSymbol trait,
        bool canonical)
    {
        yield return $"typeParams:{FormatSymbols(symbolTable, trait.TypeParams, canonical)}";
        yield return $"methods:{FormatSymbols(symbolTable, trait.Methods, canonical)}";
        yield return $"associatedTypes:{FormatSymbols(symbolTable, trait.AssociatedTypes, canonical)}";
        yield return $"parentTraits:{FormatSymbols(symbolTable, trait.ParentTraits, canonical)}";
        yield return $"selfPosition:{trait.SelfPosition}";
    }

    private static IEnumerable<string> BuildEffectFacts(
        SymbolTable symbolTable,
        EffectSymbol ability,
        bool canonical)
    {
        yield break;
    }

    private static IEnumerable<string> BuildImplFacts(
        SymbolTable symbolTable,
        ImplSymbol impl,
        bool canonical)
    {
        yield return $"trait:{FormatSymbol(symbolTable, impl.Trait, canonical)}";
        yield return $"implementingType:{FormatType(symbolTable, impl.ImplementingType, canonical)}";
        yield return $"canonicalImplementingType:{impl.CanonicalImplementingType}";
        yield return $"implementingTypeDisplay:{impl.ImplementingTypeDisplay}";
        yield return $"implementingTypeKey:{FormatImplTypeRefKey(symbolTable, impl.ImplementingTypeKey, canonical)}";
        yield return $"methods:{FormatSymbols(symbolTable, impl.Methods, canonical)}";
        yield return $"runtimeMethods:{impl.HasRuntimeMethods}";
        yield return $"traitMethodImplementations:{string.Join(",", impl.TraitMethodImplementations.OrderBy(pair => FormatSymbol(symbolTable, pair.Key, canonical), StringComparer.Ordinal).Select(pair => $"{FormatSymbol(symbolTable, pair.Key, canonical)}->{FormatSymbol(symbolTable, pair.Value, canonical)}"))}";
        yield return $"traitTypeArgs:{string.Join(",", impl.TraitTypeArgs)}";
        yield return $"traitTypeArgKeys:{string.Join(",", impl.TraitTypeArgKeys.Select(key => FormatImplTypeRefKey(symbolTable, key, canonical)))}";
        yield return $"canonicalTraitTypeArgs:{string.Join(",", impl.CanonicalTraitTypeArgs)}";
        yield return $"canonicalTraitTypeArgKeys:{string.Join(",", impl.CanonicalTraitTypeArgKeys.Select(key => FormatImplTypeRefKey(symbolTable, key, canonical)))}";
        yield return $"implementingTypeRequirements:{string.Join(",", impl.ImplementingTypeRequirements.Select(requirement => FormatImplRequirement(symbolTable, requirement, canonical)).OrderBy(static value => value, StringComparer.Ordinal))}";
        yield return $"autoDerived:{impl.IsAutoDerived}";
    }

    private static string FormatImplRequirement(
        SymbolTable symbolTable,
        ImplTypeArgTraitRequirement requirement,
        bool canonical)
    {
        return $"{requirement.TypeArgIndex}:{FormatSymbol(symbolTable, requirement.Trait, canonical)}:{requirement.TraitName}:{string.Join(",", requirement.TraitTypeArgs)}:{string.Join(",", requirement.TraitTypeArgKeys.Select(key => FormatImplTypeRefKey(symbolTable, key, canonical)))}";
    }

    private static string FormatCanonicalSymbolName(SymbolTable symbolTable, Symbol symbol)
    {
        if (symbolTable.Modules.TryGetOwningModule(symbol.Id, out var module))
        {
            return $"{ModuleRegistry.FormatModuleFullName(module)}::{symbol.Kind}:{symbol.Name}";
        }

        return $"{symbol.Kind}:{symbol.Name}";
    }

    private static string FormatCanonicalType(SymbolTable symbolTable, TypeId typeId)
    {
        if (!typeId.IsValid)
        {
            return "";
        }

        var builtin = typeId.Value switch
        {
            WellKnownTypeIds.IntId => WellKnownStrings.BuiltinTypes.Int,
            WellKnownTypeIds.FloatId => WellKnownStrings.BuiltinTypes.Float,
            WellKnownTypeIds.BoolId => WellKnownStrings.BuiltinTypes.Bool,
            WellKnownTypeIds.StringId => WellKnownStrings.BuiltinTypes.String,
            WellKnownTypeIds.CharId => WellKnownStrings.BuiltinTypes.Char,
            WellKnownTypeIds.UnitId => WellKnownStrings.BuiltinTypes.Unit,
            WellKnownTypeIds.NeverId => WellKnownStrings.BuiltinTypes.Never,
            WellKnownTypeIds.RawPtrId => WellKnownStrings.BuiltinTypes.RawPtr,
            WellKnownTypeIds.CfnId => WellKnownStrings.BuiltinTypes.Cfn,
            _ => ""
        };
        if (!string.IsNullOrWhiteSpace(builtin))
        {
            return builtin;
        }

        return symbolTable.GetSymbolByTypeId(typeId) is { } symbol
            ? FormatCanonicalSymbolName(symbolTable, symbol)
            : $"unknown-type:{typeId.Value}";
    }

    private static string FormatSymbols(SymbolTable symbolTable, IEnumerable<SymbolId> ids, bool canonical) =>
        string.Join(
            ",",
            ids.Select(id => FormatSymbol(symbolTable, id, canonical))
                .Where(value => !canonical || !string.IsNullOrWhiteSpace(value))
                .OrderBy(static value => value, StringComparer.Ordinal));

    private static string FormatSymbol(SymbolTable symbolTable, SymbolId id, bool canonical)
    {
        if (!id.IsValid)
        {
            return canonical ? "" : "0";
        }

        if (!canonical)
        {
            return id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return symbolTable.GetSymbol(id) is { } symbol
            ? FormatCanonicalSymbolName(symbolTable, symbol)
            : $"missing-symbol:{id.Value}";
    }

    private static string FormatTypes(SymbolTable symbolTable, IEnumerable<TypeId> ids, bool canonical) =>
        string.Join(
            ",",
            ids.Select(typeId => FormatType(symbolTable, typeId, canonical))
                .Where(value => !canonical || !string.IsNullOrWhiteSpace(value)));

    private static string FormatType(SymbolTable symbolTable, TypeId typeId, bool canonical) =>
        canonical ? FormatCanonicalType(symbolTable, typeId) : FormatTypeId(typeId);

    private static string FormatImplTypeRefKey(
        SymbolTable symbolTable,
        ImplTypeRefKey key,
        bool canonical)
    {
        if (key.IsEmpty)
        {
            return "";
        }

        if (!canonical)
        {
            return key.ToString();
        }

        if (key.ValueArgument is { } valueArgument)
        {
            return valueArgument.ToString();
        }

        var head = !string.IsNullOrWhiteSpace(key.Text)
            ? key.Text
            : key.TypeId.IsValid
                ? FormatCanonicalType(symbolTable, key.TypeId)
                : key.SymbolId.IsValid
                    ? FormatSymbol(symbolTable, key.SymbolId, canonical: true)
                    : "";
        return key.TypeArguments.IsDefaultOrEmpty
            ? head
            : $"{head}[{string.Join(",", key.TypeArguments.Select(argument => FormatImplTypeRefKey(symbolTable, argument, canonical: true)))}]";
    }

    private static string FormatSymbolIds(IEnumerable<SymbolId> ids) =>
        string.Join(",", ids.Select(static id => id.Value).OrderBy(static value => value));

    private static string FormatEffectIds(IEnumerable<EffectId> ids) =>
        string.Join(",", ids.Select(static id => id.Value).OrderBy(static value => value));

    private static string FormatTypeIds(IEnumerable<TypeId> ids) =>
        string.Join(",", ids.Select(static typeId => FormatTypeId(typeId)));

    private static string FormatTypeId(TypeId typeId) => typeId.IsValid ? typeId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0";
}
