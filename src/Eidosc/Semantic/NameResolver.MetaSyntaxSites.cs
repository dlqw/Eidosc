using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Parsing.Handwritten;
using Eidosc.Symbols;
using Eidosc.Syntax;
using Eidosc.Types;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private void ResolveExpandExpressionReferences(ExpandExpr expansion)
    {
        if (expansion.ExpandedExpression != null)
        {
            ResolveExpressionReferences(expansion.ExpandedExpression);
            return;
        }

        foreach (var argument in expansion.Invocation.ExplicitArguments)
        {
            ResolveExpressionReferences(argument);
        }

        RegisterMetaSyntaxSite(expansion, expansion, SyntaxCategory.Expression);
    }

    private Pattern ResolveExpandPatternBindings(
        ExpandPattern expansion,
        bool isMutableBinding,
        bool isComptimeBinding,
        bool isParameter)
    {
        if (expansion.ExpandedPattern != null)
        {
            return ResolvePatternBindings(
                expansion.ExpandedPattern,
                isMutableBinding,
                isComptimeBinding,
                isParameter);
        }

        foreach (var argument in expansion.Invocation.ExplicitArguments)
        {
            ResolveExpressionReferences(argument);
        }

        RegisterMetaSyntaxSite(
            expansion,
            expansion,
            SyntaxCategory.Pattern,
            isMutableBinding,
            isComptimeBinding,
            isParameter);
        return expansion;
    }

    private void ResolveExpandTypeReferences(ExpandType expansion)
    {
        if (expansion.ExpandedType != null)
        {
            ResolveTypeReferences(expansion.ExpandedType);
            return;
        }

        foreach (var argument in expansion.Invocation.ExplicitArguments)
        {
            ResolveExpressionReferences(argument);
        }

        RegisterMetaSyntaxSite(expansion, expansion, SyntaxCategory.Type);
    }

    private void ResolveExpandStatementReferences(ExpandStmt expansion, BlockExpr owner)
    {
        foreach (var argument in expansion.Invocation.ExplicitArguments)
        {
            ResolveExpressionReferences(argument);
        }

        RegisterMetaSyntaxSite(
            expansion,
            expansion,
            SyntaxCategory.Statement,
            statementOwner: owner,
            statementIndex: owner.Statements.FindIndex(statement => ReferenceEquals(statement, expansion)));
    }

    private void ResolveExpandDeclarationReferences(ExpandDeclaration expansion)
    {
        foreach (var argument in expansion.Invocation.ExplicitArguments)
        {
            ResolveExpressionReferences(argument);
        }

        if (expansion.SiteCategory != SyntaxCategory.Item ||
            !_moduleDeclarations.TryGetValue(_currentModule, out var owner))
        {
            AddMetaExpansionDiagnostic(
                expansion.Span,
                "item expand is not attached to an owning module",
                "E3620");
            return;
        }

        RegisterMetaSyntaxSite(
            expansion,
            expansion,
            SyntaxCategory.Item,
            boundaryOverride: owner,
            itemOwner: owner,
            itemIndex: owner.Declarations.FindIndex(declaration => ReferenceEquals(declaration, expansion)));
    }

    private void ResolveExpandMemberReferences(ExpandDeclaration expansion, Declaration owner)
    {
        foreach (var argument in expansion.Invocation.ExplicitArguments)
        {
            ResolveExpressionReferences(argument);
        }

        if (expansion.SiteCategory != SyntaxCategory.Member)
        {
            AddMetaExpansionDiagnostic(
                expansion.Span,
                "member expand has an invalid syntax category",
                "E3620");
            return;
        }

        var members = GetMemberOrder(owner);
        RegisterMetaSyntaxSite(
            expansion,
            expansion,
            SyntaxCategory.Member,
            boundaryOverride: owner,
            memberOwner: owner,
            memberIndex: members.FindIndex(member => ReferenceEquals(member, expansion)));
    }

    private void RegisterMetaSyntaxSite(
        EidosAstNode siteNode,
        IMetaSyntaxSite site,
        SyntaxCategory category,
        bool isMutablePatternBinding = false,
        bool isComptimePatternBinding = false,
        bool isParameterPatternBinding = false,
        BlockExpr? statementOwner = null,
        int statementIndex = -1,
        Declaration? boundaryOverride = null,
        ModuleDecl? itemOwner = null,
        int itemIndex = -1,
        Declaration? memberOwner = null,
        int memberIndex = -1)
    {
        if (site.IsMaterialized || !_registeredMetaSyntaxSites.Add(siteNode))
        {
            return;
        }

        var scope = _symbolTable.CurrentScope;
        var boundary = boundaryOverride ?? FindSyntaxSiteBoundary(siteNode);
        if (scope == null || boundary == null)
        {
            AddMetaExpansionDiagnostic(
                siteNode.Span,
                $"{FormatSyntaxSiteKind(category)} expand requires a lexical scope and declaration boundary",
                "E3620");
            return;
        }

        _metaSyntaxSiteOccurrences.Add(new MetaSyntaxSiteOccurrence(
            siteNode,
            site,
            category,
            boundary,
            _currentModule,
            scope,
            isMutablePatternBinding,
            isComptimePatternBinding,
            isParameterPatternBinding,
            statementOwner,
            statementIndex,
            itemOwner,
            itemIndex,
            memberOwner,
            memberIndex));
    }

    private Declaration? FindSyntaxSiteBoundary(EidosAstNode siteNode)
    {
        if (siteNode.GeneratedOriginChain.LastOrDefault() is { TargetSymbolId.IsValid: true } origin &&
            _declarationsBySymbol.TryGetValue(origin.TargetSymbolId, out var generatedBoundary))
        {
            return generatedBoundary;
        }

        var siteSpan = siteNode.Span;
        return _declarationsBySymbol.Values
            .Where(declaration =>
                declaration.SymbolId.IsValid &&
                declaration.Span.FilePath == siteSpan.FilePath &&
                declaration.Span.Position <= siteSpan.Position &&
                declaration.Span.EndPosition >= siteSpan.EndPosition)
            .OrderBy(static declaration => declaration.Span.Length)
            .ThenBy(static declaration => declaration.SymbolId.Value)
            .FirstOrDefault();
    }

    private bool ProcessMetaSyntaxSiteExpansions(ModuleDecl root)
    {
        var changed = false;
        var processed = 0;
        MetaSyntaxSiteOccurrence? lastProducer = null;
        for (var round = 1; round <= MaxMetaExpansionRoundCount; round++)
        {
            var pending = _metaSyntaxSiteOccurrences
                .Where(static occurrence => !occurrence.Site.IsMaterialized)
                .OrderBy(static occurrence => occurrence.SiteNode.Span.FilePath, StringComparer.Ordinal)
                .ThenBy(static occurrence => occurrence.SiteNode.Span.Position)
                .ToArray();
            if (pending.Length == 0)
            {
                return changed;
            }

            ResolveExpansionComptimeDependencies(root);
            var functions = _moduleDeclarations.Values
                .SelectMany(EnumerateDeclarations)
                .OfType<FuncDef>()
                .Where(static function => function.SymbolId.IsValid)
                .DistinctBy(static function => function.SymbolId)
                .ToDictionary(static function => function.SymbolId);
            var comptimeValues = EvaluateExpansionComptimeBindings(root, functions);
            var roundChanged = false;
            var roundFailed = false;
            foreach (var occurrence in pending)
            {
                lastProducer = occurrence;
                if (++processed > MaxDeriveExpansionCount)
                {
                    AddMetaExpansionDiagnostic(
                        occurrence.SiteNode.Span,
                        "syntax-site expansion count exceeded the compiler budget",
                        "E3621");
                    return changed;
                }

                if (!TryExpandSyntaxSite(
                        occurrence,
                        functions,
                        comptimeValues,
                        out var expandedNodes,
                        out var reason))
                {
                    AddMetaExpansionDiagnostic(occurrence.SiteNode.Span, reason, "E3620");
                    roundFailed = true;
                    continue;
                }

                bool applied;
                using (PushCurrentModuleScope(occurrence.ModuleId))
                using (_symbolTable.PushScopeGuard(occurrence.Scope))
                {
                    applied = ApplyMaterializedSyntaxSite(
                        occurrence,
                        expandedNodes,
                        functions,
                        out reason);
                }
                if (!applied)
                {
                    AddMetaExpansionDiagnostic(occurrence.SiteNode.Span, reason, "E3620");
                    roundFailed = true;
                    continue;
                }
                occurrence.Site.SetMaterializedNodes(expandedNodes);
                using (PushCurrentModuleScope(occurrence.ModuleId))
                using (_symbolTable.PushScopeGuard(occurrence.Scope))
                {
                    ResolveMaterializedSyntaxSite(occurrence, expandedNodes);
                }
                roundChanged = true;
                changed = true;
            }

            if (!roundChanged || roundFailed)
            {
                return changed;
            }
        }

        if (lastProducer != null)
        {
            AddNonConvergentSyntaxSiteDiagnostic(lastProducer, MaxMetaExpansionRoundCount);
        }
        return changed;
    }

    private void AddNonConvergentSyntaxSiteDiagnostic(
        MetaSyntaxSiteOccurrence occurrence,
        int round)
    {
        var targetName = _symbolTable.GetSymbol(occurrence.Boundary.SymbolId)?.Name ?? "<unknown>";
        var originChain = occurrence.SiteNode.GeneratedOriginChain.Count > 0
            ? string.Join(
                " -> ",
                occurrence.SiteNode.GeneratedOriginChain.Select(static origin => origin.StableIdentity))
            : "<source>";
        AddMetaExpansionDiagnostic(
            occurrence.SiteNode.Span,
            $"meta expansion did not converge in stage '{ClauseStage.Syntax}' at round {round}: " +
            $"syntax-site expansions continued to produce nested sites; producer " +
            $"'{occurrence.Site.Invocation.GeneratorDisplayName}', target '{targetName}', " +
            $"origin chain '{originChain}'",
            "E3617");
    }

    private bool ApplyMaterializedSyntaxSite(
        MetaSyntaxSiteOccurrence occurrence,
        IReadOnlyList<EidosAstNode> expandedNodes,
        Dictionary<SymbolId, FuncDef> functions,
        out string reason)
    {
        if (occurrence.Category == SyntaxCategory.Statement)
        {
            if (occurrence.StatementOwner == null ||
                !occurrence.StatementOwner.ReplaceStatement(occurrence.SiteNode, expandedNodes))
            {
                reason = "statement expansion site is no longer attached to its lexical block";
                return false;
            }
        }
        else if (occurrence.Category == SyntaxCategory.Item)
        {
            if (occurrence.ItemOwner == null || occurrence.SiteNode is not ExpandDeclaration placeholder)
            {
                reason = "item expansion site is not attached to an owning module";
                return false;
            }

            if (expandedNodes.Any(static node => node is not Declaration))
            {
                reason = "item expansion produced a non-declaration syntax node";
                return false;
            }

            var declarations = expandedNodes.Cast<Declaration>().ToArray();
            var materialized = declarations
                .Select((node, index) => new MaterializedMetaNode(node, index))
                .ToArray();
            if (!TryValidateGeneratedMembers(occurrence.ItemOwner, materialized, out reason) ||
                !TryValidateGeneratedModuleDeclarationCollisions(
                    occurrence.ModuleId,
                    declarations.Where(static declaration => declaration is not ExpandDeclaration).ToArray(),
                    replacedSymbols: null,
                    out reason))
            {
                return false;
            }
            if (!occurrence.ItemOwner.ReplaceDeclaration(placeholder, declarations))
            {
                reason = "item expansion site is no longer attached to its owning module";
                return false;
            }
        }
        else if (occurrence.Category == SyntaxCategory.Member)
        {
            if (occurrence.MemberOwner == null || occurrence.SiteNode is not ExpandDeclaration placeholder)
            {
                reason = "member expansion site is not attached to a member-owning declaration";
                return false;
            }

            var materialized = expandedNodes
                .Select((node, index) => new MaterializedMetaNode(node, index))
                .ToArray();
            if (!TryValidateGeneratedMembers(occurrence.MemberOwner, materialized, out reason) ||
                !TryValidateExpandedMemberOrder(occurrence.MemberOwner, placeholder, expandedNodes, out reason))
            {
                return false;
            }

            if (!ReplaceMemberExpansion(occurrence.MemberOwner, placeholder, expandedNodes))
            {
                reason = "member expansion site is no longer attached to its owning declaration";
                return false;
            }

            foreach (var node in expandedNodes)
            {
                if (node is ExpandDeclaration { SiteCategory: SyntaxCategory.Member })
                {
                    continue;
                }

                if (node.GeneratedOriginChain.LastOrDefault() is not { } origin)
                {
                    throw new InvalidOperationException("materialized member is missing its generated origin");
                }

                var insertionIndex = GetTypedMemberIndex(occurrence.MemberOwner, node);
                var applied = occurrence.MemberOwner switch
                {
                    AdtDef or CaseTypeDef => TryApplyGeneratedTypeMember(
                        occurrence.MemberOwner,
                        node,
                        origin,
                        out reason,
                        insertionIndex),
                    TraitDef trait => TryApplyGeneratedTraitMember(
                        trait,
                        node,
                        origin,
                        functions,
                        out reason,
                        insertionIndex),
                    InstanceDecl instance => TryApplyGeneratedInstanceMember(
                        instance,
                        node,
                        origin,
                        functions,
                        out reason,
                        insertionIndex),
                    _ => false
                };
                if (!applied)
                {
                    throw new InvalidOperationException(
                        $"validated member expansion commit failed: {reason}");
                }
            }
        }

        reason = string.Empty;
        return true;
    }

    private void ResolveMaterializedSyntaxSite(
        MetaSyntaxSiteOccurrence occurrence,
        IReadOnlyList<EidosAstNode> expandedNodes)
    {
        switch (occurrence.Category)
        {
            case SyntaxCategory.Expression:
                ResolveExpressionReferences(expandedNodes[0]);
                break;
            case SyntaxCategory.Pattern when expandedNodes[0] is Pattern pattern:
                ResolvePatternBindings(
                    pattern,
                    occurrence.IsMutablePatternBinding,
                    occurrence.IsComptimePatternBinding,
                    occurrence.IsParameterPatternBinding);
                break;
            case SyntaxCategory.Type when expandedNodes[0] is TypeNode type:
                ResolveTypeReferences(type);
                break;
            case SyntaxCategory.Statement:
                foreach (var declaration in expandedNodes.OfType<Declaration>())
                {
                    CollectDeclaration(declaration);
                }

                if (occurrence.StatementOwner != null)
                {
                    ResolveBlockStatementRange(
                        occurrence.StatementOwner,
                        Math.Max(0, occurrence.StatementIndex));
                }
                break;
            case SyntaxCategory.Item when occurrence.ItemOwner != null:
                foreach (var node in expandedNodes.Cast<Declaration>())
                {
                    RegisterGeneratedItemDeclaration(node, occurrence.ModuleId);
                }

                ResolveModuleDeclarationRange(
                    occurrence.ItemOwner,
                    Math.Max(0, occurrence.ItemIndex));
                break;
            case SyntaxCategory.Member when occurrence.MemberOwner != null:
                ResolveMemberRange(
                    occurrence.MemberOwner,
                    Math.Max(0, occurrence.MemberIndex));
                break;
        }
    }

    private static List<EidosAstNode> GetMemberOrder(Declaration owner) => owner switch
    {
        AdtDef adt => adt.Members,
        CaseTypeDef caseType => caseType.Members,
        TraitDef trait => trait.Members,
        InstanceDecl instance => instance.Members,
        _ => throw new InvalidOperationException($"{owner.GetType().Name} does not own syntax members")
    };

    private static bool ReplaceMemberExpansion(
        Declaration owner,
        ExpandDeclaration placeholder,
        IReadOnlyList<EidosAstNode> members) => owner switch
    {
        AdtDef adt => adt.ReplaceMemberExpansion(placeholder, members),
        CaseTypeDef caseType => caseType.ReplaceMemberExpansion(placeholder, members),
        TraitDef trait => trait.ReplaceMemberExpansion(placeholder, members),
        InstanceDecl instance => instance.ReplaceMemberExpansion(placeholder, members),
        _ => false
    };

    private static int GetTypedMemberIndex(Declaration owner, EidosAstNode member)
    {
        var members = GetMemberOrder(owner);
        var lexicalIndex = members.FindIndex(candidate => ReferenceEquals(candidate, member));
        if (lexicalIndex < 0)
        {
            throw new InvalidOperationException("generated member is missing from lexical member order");
        }

        return members.Take(lexicalIndex).Count(candidate =>
            (candidate, member) switch
            {
                (Field, Field) => true,
                (CaseTypeDef, CaseTypeDef) => true,
                (FuncDef, FuncDef) => true,
                (AssociatedTypeDecl, AssociatedTypeDecl) => true,
                (AssociatedConstDecl, AssociatedConstDecl) => true,
                _ => false
            });
    }

    private static bool TryValidateExpandedMemberOrder(
        Declaration owner,
        ExpandDeclaration placeholder,
        IReadOnlyList<EidosAstNode> expandedNodes,
        out string reason)
    {
        if (owner is not (AdtDef or CaseTypeDef))
        {
            reason = string.Empty;
            return true;
        }

        var members = GetMemberOrder(owner).ToList();
        var index = members.FindIndex(member => ReferenceEquals(member, placeholder));
        if (index < 0)
        {
            reason = "member expansion site is no longer attached to its type body";
            return false;
        }
        members.RemoveAt(index);
        members.InsertRange(index, expandedNodes);

        var seenCase = false;
        foreach (var member in members)
        {
            if (member is CaseTypeDef)
            {
                seenCase = true;
            }
            else if (seenCase && member is Field field)
            {
                reason = $"generated field '{field.Name}' would appear after a case type; fields must precede cases";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private bool ResolveMemberRange(Declaration owner, int startIndex) => owner switch
    {
        AdtDef adt => ResolveTypeMemberRange(adt, startIndex),
        CaseTypeDef caseType => ResolveTypeMemberRange(caseType, startIndex),
        TraitDef trait => ResolveTraitMemberRange(trait, startIndex),
        InstanceDecl instance => ResolveInstanceMemberRange(instance, startIndex),
        _ => throw new InvalidOperationException($"{owner.GetType().Name} does not own syntax members")
    };

    private bool ResolveTypeMemberRange(Declaration owner, int startIndex)
    {
        var members = GetMemberOrder(owner);
        for (var index = startIndex; index < members.Count; index++)
        {
            switch (members[index])
            {
                case ExpandDeclaration expansion:
                    ResolveExpandMemberReferences(expansion, owner);
                    return false;

                case Field { Type: not null } field:
                    ResolveTypeReferences(field.Type);
                    break;

                case CaseTypeDef caseType:
                    if (!ResolveClosedCaseReference(caseType))
                    {
                        return false;
                    }
                    break;
            }
        }

        return true;
    }

    private bool ResolveTraitMemberRange(TraitDef trait, int startIndex, bool resolveBodies = true)
    {
        _traitSignatureDepth++;
        try
        {
            var members = trait.Members;
            for (var index = startIndex; index < members.Count; index++)
            {
                switch (members[index])
                {
                    case ExpandDeclaration expansion:
                        ResolveExpandMemberReferences(expansion, trait);
                        return false;
                    case AssociatedTypeDecl associatedType:
                        ResolveAssociatedTypeReferences(associatedType);
                        break;
                    case AssociatedConstDecl associatedConst:
                        ResolveAssociatedConstReferences(associatedConst, resolveValue: resolveBodies);
                        break;
                    case FuncDef method:
                        ResolveFuncDefReferences(method, resolveBodies);
                        break;
                }
            }
        }
        finally
        {
            _traitSignatureDepth--;
        }

        return true;
    }

    private void ResolveModuleDeclarationRange(ModuleDecl module, int startIndex)
    {
        for (var index = startIndex; index < module.Declarations.Count; index++)
        {
            var declaration = module.Declarations[index];
            if (declaration is ExpandDeclaration expansion)
            {
                ResolveExpandDeclarationReferences(expansion);
                return;
            }

            if (declaration is ModuleDecl nested && nested.SymbolId.IsValid)
            {
                ResolveModuleReferencesRecursive(nested, nested.SymbolId);
            }
            else
            {
                ResolveDeclarationReferences(declaration);
            }
        }
    }

    private void RegisterGeneratedItemDeclaration(Declaration declaration, SymbolId moduleId)
    {
        if (declaration is ModuleDecl module)
        {
            DeclareModuleTree(module);
            if (module.SymbolId.IsValid)
            {
                CollectModuleDeclarationsRecursive(module, module.SymbolId);
            }
            return;
        }

        CollectDeclaration(declaration, isGeneratedSource: true);
        if (!declaration.SymbolId.IsValid)
        {
            return;
        }

        _symbolTable.AddMemberToModule(moduleId, declaration.SymbolId);
        if (declaration.IsExported)
        {
            TryAddExportBinding(
                moduleId,
                new ModuleBindingEntry
                {
                    Name = GetExportBindingName(declaration.SymbolId),
                    SymbolId = declaration.SymbolId,
                    Kind = GetExportResolutionKind(declaration.SymbolId)
                },
                declaration.Span);
        }
    }

    private void ResolveBlockStatementRange(BlockExpr block, int startIndex)
    {
        for (var index = startIndex; index < block.Statements.Count; index++)
        {
            var statement = block.Statements[index];
            if (statement is Declaration declaration)
            {
                ResolveDeclarationReferences(declaration);
            }
            else if (statement is ExpandStmt expansion)
            {
                ResolveExpandStatementReferences(expansion, block);
                return;
            }
            else
            {
                ResolveExpressionReferences(statement);
            }
        }
    }

    private bool TryExpandSyntaxSite(
        MetaSyntaxSiteOccurrence occurrence,
        IReadOnlyDictionary<SymbolId, FuncDef> functions,
        IReadOnlyDictionary<SymbolId, ComptimeValue> comptimeValues,
        out IReadOnlyList<EidosAstNode> expandedNodes,
        out string reason)
    {
        expandedNodes = [];
        using var currentModule = PushCurrentModuleScope(occurrence.ModuleId);
        using var lexicalScope = _symbolTable.PushScopeGuard(occurrence.Scope);
        if (!TryResolveSyntaxSiteGenerator(
                occurrence,
                out var generator,
                out var generatorSymbol,
                out var parameterTypes,
                out var usesTypedProtocol,
                out reason))
        {
            return false;
        }

        if (_symbolTable.GetSymbol(occurrence.Boundary.SymbolId) is not { } boundarySymbol)
        {
            reason = "syntax-site boundary has no stable declaration symbol";
            return false;
        }

        var generatorIdentity = MetaComptimeIntrinsics.CreateStableIdentity(generatorSymbol, _symbolTable);
        var boundaryIdentity = MetaComptimeIntrinsics.CreateStableIdentity(boundarySymbol, _symbolTable);
        var siteKind = FormatSyntaxSiteKind(occurrence.Category);
        var trace = $"expand {generator.Name} at {siteKind} site {occurrence.SiteNode.Span.Position}";
        var generatorModuleId = GetDeclarationOwnerModuleId(generator, occurrence.ModuleId);
        var pendingDiagnostics = new List<PendingMetaUserDiagnostic>();
        var metaContext = new MetaComptimeContext(
            _symbolTable,
            _adtDefinitions,
            _traitDefinitions,
            (level, span, message) => pendingDiagnostics.Add(new PendingMetaUserDiagnostic(level, span, message)),
            ExpansionTrace: trace,
            ResourceBudget: ComptimeExecution.CreateBudget(),
            Trace: ComptimeExecution.Trace,
            TracePhase: "namer.meta-syntax-site",
            Declarations: _declarationsBySymbol,
            QueryAccess: new MetaQueryAccessContext(
                generatorModuleId,
                ClauseStage.Syntax,
                MetaQueryCapability.CurrentPackagePrivateShapes,
                boundaryIdentity,
                MetaTargetTriple,
                TargetSymbolId: boundarySymbol.Id,
                RequesterIdentity: generatorIdentity),
            DefinitionSiteResolver: CreateDefinitionSiteSyntaxResolver(generatorModuleId));

        var invocationArguments = new List<ComptimeValue>();
        for (var index = 0; index < occurrence.Site.Invocation.ExplicitArguments.Count; index++)
        {
            var argument = occurrence.Site.Invocation.ExplicitArguments[index];
            if (TryGetSyntaxParameterCategory(parameterTypes[index], out var captureCategory))
            {
                if (!ComptimeSyntaxCapture.TryCapture(
                        argument,
                        captureCategory,
                        _sourceText,
                        _symbolTable,
                        trace,
                        out var captured,
                        out reason))
                {
                    reason = $"syntax argument {index + 1} capture failed: {reason}";
                    return false;
                }

                invocationArguments.Add(captured);
                continue;
            }

            if (!ComptimeEvaluator.TryEvaluate(
                    argument,
                    comptimeValues,
                    functions,
                    resolveType: null,
                    metaContext,
                    out var argumentValue,
                    out reason))
            {
                reason = $"meta argument {index + 1} for generator '{generator.Name}' failed: {reason}";
                return false;
            }

            invocationArguments.Add(argumentValue);
        }

        if (usesTypedProtocol)
        {
            invocationArguments.Add(ComptimeSyntaxCapture.CreatePlaceholder(
                occurrence.Category,
                occurrence.SiteNode.Span,
                _symbolTable,
                trace));
        }
        else
        {
            var boundary = MetaComptimeIntrinsics.CreateDeclValue(boundarySymbol, _symbolTable);
            var site = MetaComptimeIntrinsics.CreateSiteValue(
                boundary,
                siteKind,
                occurrence.SiteNode.Span,
                _symbolTable,
                CollectVisibleScopeLayers(occurrence.Scope));
            invocationArguments.Add(site);
        }
        if (!ComptimeEvaluator.TryInvoke(
                generator,
                invocationArguments,
                comptimeValues,
                functions,
                metaContext,
                out var expansionValue,
                out reason))
        {
            reason = $"syntax-site generator '{generator.Name}' failed: {reason}; expansion trace: {trace}";
            return false;
        }

        if (!TryGetSyntaxSiteResults(
                expansionValue,
                occurrence.Category,
                out var syntaxResults,
                out reason))
        {
            reason = $"{siteKind} generator '{generator.Name}' returned an invalid result: {reason}";
            return false;
        }

        var materializer = new MetaExpansionMaterializer(
            _symbolTable,
            occurrence.Boundary,
            occurrence.ModuleId,
            occurrence.SiteNode.Span);
        var nodes = new List<EidosAstNode>();
        foreach (var syntax in syntaxResults)
        {
            if (!materializer.TryMaterializeSyntax(
                    syntax,
                    occurrence.Category,
                    GetSyntaxMemberGrammar(occurrence),
                    out var fragmentNodes,
                    out reason) ||
                fragmentNodes.Count != 1)
            {
                reason = string.IsNullOrWhiteSpace(reason)
                    ? $"each {siteKind} syntax result must materialize exactly one {siteKind}"
                    : reason;
                return false;
            }

            nodes.Add(fragmentNodes[0]);
        }

        if (occurrence.Category is not (SyntaxCategory.Item or SyntaxCategory.Member or SyntaxCategory.Statement) &&
            nodes.Count != 1)
        {
            reason = $"{siteKind} generator must materialize exactly one {siteKind}";
            return false;
        }

        expandedNodes = nodes;
        var argumentsHash = HashIdentity(string.Join(
            "|",
            invocationArguments.Select(static argument => argument.CanonicalText)));
        var slotMaterial = $"{generatorIdentity}|{boundaryIdentity}|site:{occurrence.SiteNode.Span.Position}|" +
                           $"{occurrence.Category}|{WellKnownStrings.Meta.SchemaVersion}";
        var inherited = occurrence.SiteNode.GeneratedOriginChain.Count > 0
            ? occurrence.SiteNode.GeneratedOriginChain
            : occurrence.Boundary.GeneratedOriginChain.Count > 0
                ? occurrence.Boundary.GeneratedOriginChain
            : boundarySymbol.GeneratedOrigin == null
                ? []
                : [boundarySymbol.GeneratedOrigin];
        for (var outputIndex = 0; outputIndex < nodes.Count; outputIndex++)
        {
            var outputSlot = $"{slotMaterial}|output:{outputIndex}";
            var origin = new GeneratedDeclarationOrigin
            {
                StableIdentity = HashIdentity($"{outputSlot}|{argumentsHash}"),
                GenerationSlotIdentity = HashIdentity(outputSlot),
                GeneratorIdentity = generatorIdentity,
                TargetIdentity = boundaryIdentity,
                GeneratorSymbolId = generatorSymbol.Id,
                TargetSymbolId = boundarySymbol.Id,
                ClauseOccurrenceIdentity = $"site:{occurrence.SiteNode.Span.Position}:{occurrence.SiteNode.Span.Length}",
                ExpansionOutputIndex = outputIndex,
                CanonicalArgumentsHash = argumentsHash,
                MetaSchemaVersion = WellKnownStrings.Meta.SchemaVersion,
                ClauseSpan = occurrence.SiteNode.Span,
                VirtualDocumentPath =
                    $"eidos-generated://site-{occurrence.SiteNode.Span.Position}-{outputIndex}.eidos"
            };
            AttachGeneratedOriginChain(nodes[outputIndex], [.. inherited, origin]);
        }
        foreach (var diagnostic in pendingDiagnostics)
        {
            AddMetaUserDiagnostic(diagnostic.Level, diagnostic.Span, diagnostic.Message, trace);
        }

        reason = string.Empty;
        return true;
    }

    private static SyntaxMemberGrammar GetSyntaxMemberGrammar(MetaSyntaxSiteOccurrence occurrence) =>
        occurrence.Category != SyntaxCategory.Member
            ? SyntaxMemberGrammar.Any
            : occurrence.MemberOwner switch
            {
                AdtDef or CaseTypeDef => SyntaxMemberGrammar.Type,
                TraitDef => SyntaxMemberGrammar.Trait,
                InstanceDecl => SyntaxMemberGrammar.Instance,
                _ => SyntaxMemberGrammar.Any
            };

    private static bool TryGetSyntaxSiteResults(
        ComptimeValue value,
        SyntaxCategory category,
        out IReadOnlyList<ComptimeSyntaxValue> syntaxes,
        out string reason)
    {
        if (value is ComptimeSyntaxValue syntax)
        {
            if (syntax.Category != category)
            {
                syntaxes = [];
                reason = $"expected meta.Syntax[meta.{FormatSyntaxMarker(category)}]";
                return false;
            }

            syntaxes = [syntax];
            reason = string.Empty;
            return true;
        }

        if (category is not (SyntaxCategory.Item or SyntaxCategory.Member or SyntaxCategory.Statement) ||
            value is not ComptimeSequenceValue { Kind: ComptimeSequenceKind.List } sequence)
        {
            syntaxes = [];
            reason = $"expected meta.Syntax[meta.{FormatSyntaxMarker(category)}]";
            return false;
        }

        var results = new List<ComptimeSyntaxValue>(sequence.Elements.Count);
        foreach (var element in sequence.Elements)
        {
            if (element is not ComptimeSyntaxValue elementSyntax || elementSyntax.Category != category)
            {
                syntaxes = [];
                reason = $"expected Seq[meta.Syntax[meta.{FormatSyntaxMarker(category)}]]";
                return false;
            }

            results.Add(elementSyntax);
        }

        syntaxes = results;
        reason = string.Empty;
        return true;
    }

    private bool TryResolveSyntaxSiteGenerator(
        MetaSyntaxSiteOccurrence occurrence,
        out FuncDef generator,
        out FuncSymbol generatorSymbol,
        out IReadOnlyList<TypeNode> parameterTypes,
        out bool usesTypedProtocol,
        out string reason)
    {
        generator = null!;
        generatorSymbol = null!;
        parameterTypes = [];
        usesTypedProtocol = false;
        var path = occurrence.Site.Invocation.GeneratorPath;
        var display = occurrence.Site.Invocation.GeneratorDisplayName;
        SymbolId symbolId;
        if (path.Count == 1)
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
            _symbolTable.GetSymbol<FuncSymbol>(symbolId) is not { IsComptime: true } symbol)
        {
            reason = $"expand {display} must reference a comptime-only function";
            return false;
        }

        generator = _moduleDeclarations.Values
            .SelectMany(EnumerateDeclarations)
            .OfType<FuncDef>()
            .FirstOrDefault(function => function.SymbolId == symbolId)!;
        if (generator == null)
        {
            reason = $"expand {display} cannot execute a signature-only or compiler-internal function";
            return false;
        }

        if (_metaResolvedComptimeSymbols.Add(generator.SymbolId))
        {
            var generatorModuleId = GetDeclarationOwnerModuleId(generator, occurrence.ModuleId);
            using var generatorModuleScope = PushResolutionModuleScope(generatorModuleId);
            using var currentGeneratorModuleScope = PushCurrentModuleScope(generatorModuleId);
            ResolveFuncDefReferences(generator);
        }

        if (CompilerMetaProtocolRegistry.TryClassify(
                generator,
                occurrence.Site.Invocation.ExplicitArguments.Count,
                _symbolTable,
                out var protocol,
                out var protocolReason) &&
            protocol.Kind == CompilerMetaProtocolKind.SyntaxExpansion)
        {
            parameterTypes = GetFunctionParameterTypes(generator)
                .Take(occurrence.Site.Invocation.ExplicitArguments.Count)
                .ToArray();
            usesTypedProtocol = true;
            generatorSymbol = symbol;
            reason = string.Empty;
            return true;
        }

        if (!TryGetSyntaxSiteSignature(
                generator,
                occurrence.Site.Invocation.ExplicitArguments.Count,
                occurrence.Category,
                out parameterTypes,
                out usesTypedProtocol))
        {
            reason = $"syntax-site generator '{generator.Name}' must accept the explicit arguments followed by " +
                     $"meta.Syntax[meta.{FormatSyntaxMarker(occurrence.Category)}] and return " +
                     $"meta.Syntax[meta.{FormatSyntaxMarker(occurrence.Category)}]" +
                     (string.IsNullOrWhiteSpace(protocolReason) ? string.Empty : $" (protocol: {protocolReason})");
            return false;
        }

        generatorSymbol = symbol;
        reason = string.Empty;
        return true;
    }

    private static IReadOnlyList<TypeNode> GetFunctionParameterTypes(FuncDef function)
    {
        var parameters = new List<TypeNode>();
        var result = function.Signature[0];
        while (result is ArrowType arrow)
        {
            parameters.Add(arrow.ParamType);
            result = arrow.ReturnType;
        }

        return parameters;
    }

    private bool TryGetSyntaxSiteSignature(
        FuncDef generator,
        int explicitArgumentCount,
        SyntaxCategory category,
        out IReadOnlyList<TypeNode> explicitParameters,
        out bool usesTypedProtocol)
    {
        explicitParameters = [];
        usesTypedProtocol = false;
        if (generator.Signature.Count != 1)
        {
            return false;
        }

        var parameters = new List<TypeNode>();
        var result = generator.Signature[0];
        while (result is ArrowType arrow)
        {
            parameters.Add(arrow.ParamType);
            result = arrow.ReturnType;
        }

        if (parameters.Count == explicitArgumentCount + 1 &&
            parameters[^1] is TypePath typedInput &&
            TryGetSyntaxParameterCategory(typedInput, out var typedCategory) &&
            typedCategory == category &&
            TryGetSyntaxSiteResultCategory(result, out var typedResultCategory, out var typedIsSequence) &&
            typedResultCategory == category &&
            !typedIsSequence)
        {
            explicitParameters = parameters.Take(explicitArgumentCount).ToArray();
            usesTypedProtocol = true;
            return true;
        }

        if (parameters.Count != explicitArgumentCount + 1 ||
            parameters[^1] is not TypePath { TypeArgs.Count: 1 } site ||
            !IsMetaType(site, WellKnownTypeIds.MetaSiteId) ||
            !IsSyntaxMarker(site.TypeArgs[0], category) ||
            !TryGetSyntaxSiteResultCategory(result, out var resultCategory, out var isSequence) ||
            resultCategory != category ||
            isSequence && category is not (SyntaxCategory.Item or SyntaxCategory.Member or SyntaxCategory.Statement))
        {
            return false;
        }

        explicitParameters = parameters.Take(explicitArgumentCount).ToArray();
        return true;
    }

    private bool TryGetSyntaxSiteResultCategory(
        TypeNode result,
        out SyntaxCategory category,
        out bool isSequence)
    {
        isSequence = false;
        if (result is TypePath { TypeArgs.Count: 1 } sequence &&
            string.Equals(sequence.TypeName, WellKnownStrings.BuiltinTypes.Seq, StringComparison.Ordinal))
        {
            result = sequence.TypeArgs[0];
            isSequence = true;
        }

        if (result is TypePath { TypeArgs.Count: 1 } syntax &&
            IsMetaType(syntax, WellKnownTypeIds.MetaSyntaxId))
        {
            foreach (var candidate in new[]
                     {
                         SyntaxCategory.Item,
                         SyntaxCategory.Member,
                         SyntaxCategory.Statement,
                         SyntaxCategory.Expression,
                         SyntaxCategory.Pattern,
                         SyntaxCategory.Type
                     })
            {
                if (IsSyntaxMarker(syntax.TypeArgs[0], candidate))
                {
                    category = candidate;
                    return true;
                }
            }
        }

        category = default;
        return false;
    }

    private bool TryGetSyntaxParameterCategory(TypeNode parameter, out SyntaxCategory category)
    {
        if (parameter is TypePath { TypeArgs.Count: 1 } syntax &&
            IsMetaType(syntax, WellKnownTypeIds.MetaSyntaxId))
        {
            foreach (var candidate in new[]
                     {
                         SyntaxCategory.Item,
                         SyntaxCategory.Member,
                         SyntaxCategory.Statement,
                         SyntaxCategory.Expression,
                         SyntaxCategory.Pattern,
                         SyntaxCategory.Type
                     })
            {
                if (IsSyntaxMarker(syntax.TypeArgs[0], candidate))
                {
                    category = candidate;
                    return true;
                }
            }
        }

        category = default;
        return false;
    }

    private bool IsSyntaxMarker(TypeNode node, SyntaxCategory category)
    {
        var typeId = category switch
        {
            SyntaxCategory.Item => WellKnownTypeIds.MetaItemId,
            SyntaxCategory.Member => WellKnownTypeIds.MetaMemberId,
            SyntaxCategory.Statement => WellKnownTypeIds.MetaStmtId,
            SyntaxCategory.Expression => WellKnownTypeIds.MetaExprId,
            SyntaxCategory.Pattern => WellKnownTypeIds.MetaPatternId,
            SyntaxCategory.Type => WellKnownTypeIds.MetaTypeSyntaxId,
            _ => -1
        };
        return typeId >= 0 && IsMetaType(node, typeId);
    }

    private static string FormatSyntaxMarker(SyntaxCategory category) => category switch
    {
        SyntaxCategory.Item => "Item",
        SyntaxCategory.Member => "Member",
        SyntaxCategory.Statement => "Stmt",
        SyntaxCategory.Expression => "Expr",
        SyntaxCategory.Pattern => "Pattern",
        SyntaxCategory.Type => "TypeSyntax",
        _ => category.ToString()
    };

    private static string FormatSyntaxSiteKind(SyntaxCategory category) => category switch
    {
        SyntaxCategory.Item => "item",
        SyntaxCategory.Member => "member",
        SyntaxCategory.Statement => "statement",
        SyntaxCategory.Expression => "expression",
        SyntaxCategory.Pattern => "pattern",
        SyntaxCategory.Type => "type",
        _ => category.ToString().ToLowerInvariant()
    };

    private IReadOnlyList<IReadOnlyList<Symbol>> CollectVisibleScopeLayers(Scope scope)
    {
        var layers = new List<IReadOnlyList<Symbol>>();
        for (var current = scope; current != null; current = current.Parent)
        {
            if (current.Kind == ScopeKind.Module)
            {
                break;
            }

            var ids = current.GetLocalBindings().Values
                .Concat(current.GetLocalFunctionOverloads().Values.SelectMany(static overloads => overloads))
                .Concat(current.GetLocalTypes().Values)
                .Concat(current.GetLocalTraits().Values)
                .Concat(current.GetLocalAbilities().Values)
                .Concat(current.GetLocalConstructors().Values)
                .Distinct()
                .ToArray();
            layers.Add(ids
                .Select(_symbolTable.GetSymbol)
                .Where(static symbol => symbol != null)
                .Select(static symbol => symbol!)
                .ToArray());
        }

        return layers;
    }
}
