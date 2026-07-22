using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Symbols;
using Eidosc.Types;
using Eidosc.Utils;
using Eidosc.Ide;
using Eidosc.Parsing.Handwritten;
using Eidosc.Pipeline;
using Eidosc.Syntax;

namespace Eidosc.Semantic;

internal sealed record MaterializedMetaNode(
    EidosAstNode Node,
    int OutputIndex,
    int NestedIndex = 0,
    MetaDeclarationPlacement Placement = MetaDeclarationPlacement.AfterTarget,
    string? GenerationSlotIdentity = null);

internal enum MetaDeclarationPlacement
{
    BeforeTarget,
    AfterTarget,
    Member,
    ReplaceTarget
}

internal sealed record MetaExpansionDiagnostic(
    string Level,
    SourceSpan Span,
    string Message,
    int OutputIndex);

internal sealed record MetaExpansionMaterializationResult(
    IReadOnlyList<MaterializedMetaNode> Nodes,
    IReadOnlyList<MetaExpansionDiagnostic> Diagnostics,
    bool RemovesTarget = false);

internal sealed class MetaExpansionMaterializer(
    SymbolTable symbolTable,
    Declaration target,
    SymbolId targetModuleId,
    SourceSpan invocationSpan,
    IReadOnlyList<string>? targetPath = null)
{
    private readonly SymbolTable _symbolTable = symbolTable;
    private readonly Declaration _target = target;
    private readonly SymbolId _targetModuleId = targetModuleId;
    private readonly SourceSpan _invocationSpan = invocationSpan;
    private readonly IReadOnlyList<string>? _targetPath = targetPath;
    private readonly HashSet<string> _explicitGenerationSlots = new(StringComparer.Ordinal);

    public bool TryMaterialize(
        ComptimeValue expansionValue,
        out MetaExpansionMaterializationResult result,
        out string reason)
    {
        result = new MetaExpansionMaterializationResult([], []);
        reason = string.Empty;
        _explicitGenerationSlots.Clear();
        if (expansionValue is not ComptimeMetaObjectValue { SchemaKind: "transformation" } transformation ||
            !transformation.TryGet("edits", out var editValues) ||
            editValues is not ComptimeSequenceValue { Kind: ComptimeSequenceKind.List } edits)
        {
            reason = "generator must return typed meta.Items or a category-preserving meta.Syntax value";
            return false;
        }

        var nodes = new List<MaterializedMetaNode>();
        var diagnostics = new List<MetaExpansionDiagnostic>();
        var outputIndex = 0;
        var hasTargetMutation = false;
        var removesTarget = false;
        for (var editIndex = 0; editIndex < edits.Elements.Count; editIndex++)
        {
            if (edits.Elements[editIndex] is not ComptimeMetaObjectValue { SchemaKind: "transformation-edit" } edit ||
                !TryGetString(edit, "kind", out var editKind, out reason))
            {
                reason = $"generator output {editIndex} is not a valid typed item";
                return false;
            }

            if (editKind == "report-diagnostic")
            {
                if (!TryGetSequence(edit, "diagnostics", out var diagnosticValues, out reason))
                {
                    return false;
                }

                foreach (var diagnosticValue in diagnosticValues)
                {
                    if (diagnosticValue is not ComptimeMetaObjectValue { SchemaKind: "diagnostic" } diagnosticObject ||
                        !TryReadDiagnostic(diagnosticObject, outputIndex++, out var diagnostic, out reason))
                    {
                        reason = string.IsNullOrWhiteSpace(reason)
                            ? $"transformation diagnostic {editIndex} is invalid"
                            : reason;
                        return false;
                    }
                    diagnostics.Add(diagnostic);
                }
                continue;
            }

            if (editKind == "replace-target")
            {
                if (hasTargetMutation ||
                    !TryValidateTarget(edit, out reason) ||
                    !TryGetValue(edit, "syntax", out var replacement, out reason))
                {
                    reason = string.IsNullOrWhiteSpace(reason)
                        ? "a transformation cannot contain conflicting target mutations"
                        : reason;
                    return false;
                }

                var nodeStart = nodes.Count;
                if (!TryMaterializeDeclaration(
                        replacement,
                        outputIndex++,
                        nodes,
                        diagnostics,
                        MetaDeclarationPlacement.ReplaceTarget,
                        out reason) ||
                    nodes.Count != nodeStart + 1)
                {
                    reason = string.IsNullOrWhiteSpace(reason)
                        ? "meta.replace_target requires exactly one declaration syntax value"
                        : reason;
                    return false;
                }

                hasTargetMutation = true;
                continue;
            }

            if (editKind == "remove-target")
            {
                if (hasTargetMutation || !TryValidateTarget(edit, out reason))
                {
                    reason = string.IsNullOrWhiteSpace(reason)
                        ? "a transformation cannot contain conflicting target mutations"
                        : reason;
                    return false;
                }

                removesTarget = true;
                hasTargetMutation = true;
                continue;
            }

            if (editKind is not ("insert-before" or "insert-after" or "add-members") ||
                !TryValidateTarget(edit, out reason) ||
                !TryGetSequence(edit, "syntax", out var syntaxValues, out reason))
            {
                reason = string.IsNullOrWhiteSpace(reason)
                    ? $"unsupported generator output operation '{editKind}'"
                    : reason;
                return false;
            }

            var placement = editKind switch
            {
                "insert-before" => MetaDeclarationPlacement.BeforeTarget,
                "insert-after" => MetaDeclarationPlacement.AfterTarget,
                _ => MetaDeclarationPlacement.Member
            };
            foreach (var syntaxValue in syntaxValues)
            {
                if (!TryMaterializeDeclaration(
                        syntaxValue,
                        outputIndex++,
                        nodes,
                        diagnostics,
                        placement,
                        out reason))
                {
                    return false;
                }
            }
        }

        result = new MetaExpansionMaterializationResult(nodes, diagnostics, removesTarget);
        return true;
    }

    public bool TryMaterializeItems(
        ComptimeValue itemsValue,
        out MetaExpansionMaterializationResult result,
        out string reason)
    {
        result = new MetaExpansionMaterializationResult([], []);
        reason = string.Empty;
        _explicitGenerationSlots.Clear();
        if (itemsValue is not ComptimeSequenceValue { Kind: ComptimeSequenceKind.List } items)
        {
            reason = "derive generator must return meta.Items as a typed list";
            return false;
        }

        var nodes = new List<MaterializedMetaNode>();
        var diagnostics = new List<MetaExpansionDiagnostic>();
        for (var outputIndex = 0; outputIndex < items.Elements.Count; outputIndex++)
        {
            var item = items.Elements[outputIndex];
            if (item is ComptimeMetaObjectValue { SchemaKind: "diagnostic" } diagnosticObject)
            {
                if (!TryReadDiagnostic(diagnosticObject, outputIndex, out var diagnostic, out reason))
                {
                    return false;
                }
                diagnostics.Add(diagnostic);
                continue;
            }

            if (!TryMaterializeDeclaration(
                    item,
                    outputIndex,
                    nodes,
                    diagnostics,
                    MetaDeclarationPlacement.AfterTarget,
                    out reason))
            {
                return false;
            }
        }

        result = new MetaExpansionMaterializationResult(nodes, diagnostics);
        return true;
    }

    internal bool TryMaterializeFunctionBody(
        ComptimeMetaObjectValue functionHandle,
        FuncDef source,
        out FuncDef replacement,
        out bool hasReplacement,
        out string reason)
    {
        replacement = null!;
        hasReplacement = false;
        reason = string.Empty;
        if (!functionHandle.TryGet("identity", out var identityValue) ||
            identityValue is not ComptimeStringValue identity ||
            _symbolTable.GetSymbol(source.SymbolId) is not { } sourceSymbol ||
            !string.Equals(identity.Value, MetaComptimeIntrinsics.CreateStableIdentity(sourceSymbol, _symbolTable), StringComparison.Ordinal))
        {
            reason = "meta.Function.with_body may only return the authorized target function";
            return false;
        }

        if (!functionHandle.TryGet("replacementBody", out var replacementValue))
        {
            return true;
        }

        if (replacementValue is not ComptimeSyntaxValue { Category: SyntaxCategory.Expression } bodySyntax)
        {
            reason = "meta.Function.with_body requires a meta.Syntax[meta.Expr] replacement body";
            return false;
        }

        if (source.Body.Count == 0)
        {
            reason = "meta.Function.with_body requires a target function with at least one body branch";
            return false;
        }

        replacement = new FuncDef
        {
            Span = source.Span,
            SymbolId = source.SymbolId
        };
        replacement.SetName(source.Name);
        replacement.SetTypeParams([.. source.TypeParams]);
        if (source.Signature.Count != 1)
        {
            reason = "meta.Function.with_body requires a function with one canonical signature";
            return false;
        }
        replacement.SetSignature(source.Signature[0]);
        replacement.SetRequiredAbilities([.. source.RequiredAbilities]);
        replacement.SetComptime(source.IsComptime);

        var branches = new List<PatternBranch>(source.Body.Count);
        for (var index = 0; index < source.Body.Count; index++)
        {
            if (!TryMaterializeSyntax(
                    bodySyntax,
                    SyntaxCategory.Expression,
                    SyntaxMemberGrammar.Any,
                    out var expressionNodes,
                    out reason) ||
                expressionNodes.Count != 1 ||
                expressionNodes[0] is not Expression expression)
            {
                reason = string.IsNullOrWhiteSpace(reason)
                    ? $"meta.Function.with_body replacement must materialize exactly one expression for branch {index}"
                    : reason;
                return false;
            }

            var branch = source.Body[index] with { };
            branch.SetExpression(expression);
            branches.Add(branch);
        }

        replacement.SetBody(branches);
        hasReplacement = true;
        return true;
    }

    private bool TryValidateTarget(ComptimeMetaObjectValue edit, out string reason)
    {
        reason = string.Empty;
        if (!TryGetObject(edit, "target", out var target, out reason) ||
            target.SchemaKind != "target" ||
            !target.TryGet("targetDecl", out var targetDeclaration) ||
            targetDeclaration is not ComptimeDeclValue declaration ||
            declaration.SymbolId != _target.SymbolId)
        {
            reason = "generator output target is outside the compiler-authorized declaration";
            return false;
        }

        return true;
    }

    private bool TryMaterializeDeclaration(
        ComptimeValue declaration,
        int outputIndex,
        List<MaterializedMetaNode> nodes,
        List<MetaExpansionDiagnostic> diagnostics,
        MetaDeclarationPlacement placement,
        out string reason,
        string? generationSlotIdentity = null)
    {
        reason = string.Empty;
        if (declaration is ComptimeMetaObjectValue { SchemaKind: "slotted-output" } slotted)
        {
            if (generationSlotIdentity != null)
            {
                reason = $"generator output {outputIndex} cannot nest manual identity wrappers";
                return false;
            }
            if (!slotted.TryGet("output", out declaration) ||
                !slotted.TryGet("slot", out var slotValue) ||
                slotValue is not ComptimeMetaObjectValue { SchemaKind: "generation-slot" } slot ||
                !TryGetString(slot, "identity", out generationSlotIdentity, out reason))
            {
                reason = string.IsNullOrWhiteSpace(reason)
                    ? $"generator output {outputIndex} has an invalid generated identity"
                    : reason;
                return false;
            }
            if (!_explicitGenerationSlots.Add(generationSlotIdentity))
            {
                reason = $"transformation contains duplicate generation slot '{generationSlotIdentity}'";
                return false;
            }
        }

        if (declaration is ComptimeSyntaxValue syntax)
        {
            return TryMaterializeQuotedNodes(
                syntax,
                outputIndex,
                placement,
                nodes,
                out reason,
                generationSlotIdentity);
        }

        if (declaration is not ComptimeMetaObjectValue structured)
        {
            reason = $"generator output {outputIndex} is not a typed syntax or declaration value";
            return false;
        }

        switch (structured.SchemaKind)
        {
            case "declaration.function":
                if (!TryCreateFunction(structured, out var function, out reason))
                {
                    return false;
                }

                nodes.Add(new MaterializedMetaNode(
                    function,
                    outputIndex,
                    Placement: placement,
                    GenerationSlotIdentity: generationSlotIdentity));
                return true;

            case "declaration.instance":
                return TryCreateImplementation(
                    structured,
                    outputIndex,
                    placement,
                    nodes,
                    out reason,
                    generationSlotIdentity);

            case "declaration.comptime-value":
                if (!TryCreateComptimeValue(structured, out var comptimeValue, out reason))
                {
                    return false;
                }

                nodes.Add(new MaterializedMetaNode(
                    comptimeValue,
                    outputIndex,
                    Placement: placement,
                    GenerationSlotIdentity: generationSlotIdentity));
                return true;

            case "declaration.test":
                if (!TryCreateTest(structured, out var test, out reason))
                {
                    return false;
                }

                nodes.Add(new MaterializedMetaNode(
                    test,
                    outputIndex,
                    Placement: placement,
                    GenerationSlotIdentity: generationSlotIdentity));
                return true;

            case "declaration.module":
                if (!TryCreateModule(structured, outputIndex, diagnostics, out var module, out reason))
                {
                    return false;
                }

                nodes.Add(new MaterializedMetaNode(
                    module,
                    outputIndex,
                    Placement: placement,
                    GenerationSlotIdentity: generationSlotIdentity));
                return true;

            case "declaration.module-member":
                if (!TryGetValue(structured, "declaration", out var nested, out reason))
                {
                    return false;
                }

                return TryMaterializeDeclaration(
                    nested,
                    outputIndex,
                    nodes,
                    diagnostics,
                    placement,
                    out reason,
                    generationSlotIdentity);

            default:
                reason = $"unsupported structured declaration kind '{structured.SchemaKind}'";
                return false;
        }
    }

    private bool TryMaterializeQuotedNodes(
        ComptimeSyntaxValue syntax,
        int outputIndex,
        MetaDeclarationPlacement placement,
        List<MaterializedMetaNode> nodes,
        out string reason,
        string? generationSlotIdentity)
    {
        reason = string.Empty;
        var expectedCategory = placement == MetaDeclarationPlacement.Member
            ? SyntaxCategory.Member
            : _target is CaseTypeDef && placement == MetaDeclarationPlacement.ReplaceTarget
                ? SyntaxCategory.Member
                : SyntaxCategory.Item;
        if (syntax.Category != expectedCategory)
        {
            reason = $"{placement} on {_target.GetType().Name} requires meta.Syntax[{expectedCategory}], " +
                     $"not meta.Syntax[{syntax.Category}]";
            return false;
        }

        var memberGrammar = SyntaxMemberGrammar.Any;
        if (syntax.Category == SyntaxCategory.Member && !TryGetMemberGrammar(out memberGrammar, out reason))
        {
            return false;
        }

        if (!TryMaterializeSyntax(
                syntax,
                expectedCategory,
                memberGrammar,
                out var materializedNodes,
                out reason))
        {
            return false;
        }

        if (syntax.Category == SyntaxCategory.Item && materializedNodes.Any(static node => node is not Declaration))
        {
            reason = "quoted item syntax produced a non-declaration AST node";
            return false;
        }

        for (var index = 0; index < materializedNodes.Count; index++)
        {
            nodes.Add(new MaterializedMetaNode(
                materializedNodes[index],
                outputIndex,
                index,
                placement,
                generationSlotIdentity));
        }

        return true;
    }

    internal bool TryMaterializeSyntax(
        ComptimeSyntaxValue syntax,
        SyntaxCategory expectedCategory,
        SyntaxMemberGrammar memberGrammar,
        out IReadOnlyList<EidosAstNode> nodes,
        out string reason)
    {
        nodes = [];
        if (syntax.Category != expectedCategory)
        {
            reason = $"syntax site requires meta.Syntax[{expectedCategory}], not meta.Syntax[{syntax.Category}]";
            return false;
        }

        var sourceName = string.IsNullOrWhiteSpace(syntax.Origin.SourceUri)
            ? $"eidos-generated://quote/{expectedCategory.ToString().ToLowerInvariant()}.eidos"
            : syntax.Origin.SourceUri;
        var schema = SyntaxSchema.All.Single(entry =>
            entry.Category == syntax.Category &&
            entry.Cardinality == SyntaxCardinality.Singular);
        if (!ComptimeSyntaxEvaluator.TryParseFragments(
                schema,
                syntax.Tokens,
                syntax.TrailingTrivia,
                sourceName,
                out _,
                out var artifacts,
                out reason,
                memberGrammar) ||
            !TryApplySyntaxIdentities(artifacts, syntax.Tokens, out reason))
        {
            return false;
        }

        nodes = artifacts.Nodes;
        return true;
    }

    private bool TryGetMemberGrammar(out SyntaxMemberGrammar grammar, out string reason)
    {
        grammar = _target switch
        {
            AdtDef or CaseTypeDef => SyntaxMemberGrammar.Type,
            TraitDef => SyntaxMemberGrammar.Trait,
            InstanceDecl => SyntaxMemberGrammar.Instance,
            ModuleDecl => SyntaxMemberGrammar.Module,
            _ => SyntaxMemberGrammar.Any
        };
        if (grammar != SyntaxMemberGrammar.Any)
        {
            reason = string.Empty;
            return true;
        }

        reason = $"{_target.GetType().Name} does not own a member grammar";
        return false;
    }

    private bool TryApplySyntaxIdentities(
        ComptimeSyntaxParseArtifacts artifacts,
        IReadOnlyList<ComptimeSyntaxToken> syntaxTokens,
        out string reason)
    {
        foreach (var root in artifacts.Nodes)
        {
            var pending = new Stack<EidosAstNode>();
            var visited = new HashSet<EidosAstNode>(ReferenceEqualityComparer.Instance);
            pending.Push(root);
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                if (!visited.Add(node))
                {
                    continue;
                }

                if (TryFindSyntaxIdentity(node, artifacts.TokenPositions, syntaxTokens, out var identity))
                {
                    if (identity.SymbolId.IsValid && _symbolTable.GetSymbol(identity.SymbolId) == null)
                    {
                        reason = $"syntax identity '{identity.StableIdentity}' refers to an unavailable symbol";
                        return false;
                    }

                    if (identity.Kind == ComptimeSyntaxIdentityKind.Identifier &&
                        !IsIdentifierCategoryValidForNode(identity.Category, node))
                    {
                        reason = $"meta.Identifier category {identity.Category} cannot name {node.GetType().Name}";
                        return false;
                    }

                    var attached = new SyntaxIdentity(
                        identity.Kind switch
                        {
                            ComptimeSyntaxIdentityKind.Declaration => SyntaxIdentityKind.Declaration,
                            ComptimeSyntaxIdentityKind.Type => SyntaxIdentityKind.Type,
                            ComptimeSyntaxIdentityKind.Identifier => SyntaxIdentityKind.Identifier,
                            _ => SyntaxIdentityKind.Hygiene
                        },
                        identity.StableIdentity,
                        identity.SymbolId,
                        identity.TypeId,
                        identity.Category);
                    node.AttachSyntaxIdentity(attached);
                    if (node is CtorExpr { ConstructorPath: { } constructorPath })
                    {
                        constructorPath.AttachSyntaxIdentity(attached);
                    }
                }

                foreach (var child in AstStableNodeTraversal.GetStructuralChildren(node))
                {
                    pending.Push(child);
                }
            }
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryFindSyntaxIdentity(
        EidosAstNode node,
        IReadOnlyList<ComptimeSyntaxEvaluator.SyntaxTokenPosition> positions,
        IReadOnlyList<ComptimeSyntaxToken> syntaxTokens,
        out ComptimeSyntaxIdentity identity)
    {
        var span = node is MethodCallExpr { MemberNameSpan.Length: > 0 } methodCall
            ? methodCall.MemberNameSpan
            : node.Span;
        if (node is not (Declaration or Field or Constructor or TypeParam or IdentifierExpr or PathExpr or
            TypePath or CtorExpr or CtorPattern or VarPattern or AsPattern or FieldPattern or MethodCallExpr))
        {
            identity = null!;
            return false;
        }

        var candidates = positions
            .Where(position => position.Start >= span.Position && position.End <= span.EndPosition)
            .Select(position => syntaxTokens[position.Index].Identity)
            .OfType<ComptimeSyntaxIdentity>()
            .ToArray();
        if (candidates.Length == 0)
        {
            identity = null!;
            return false;
        }

        var pathTerminalName = node switch
        {
            PathExpr path => path.Name,
            TypePath path => path.TypeName,
            CtorExpr constructor => constructor.ConstructorName,
            CtorPattern constructor => constructor.ConstructorName,
            _ => string.Empty
        };
        if (!string.IsNullOrWhiteSpace(pathTerminalName))
        {
            var terminalCandidate = positions
                .Where(position => position.Start >= span.Position && position.End <= span.EndPosition)
                .Where(position => string.Equals(
                    syntaxTokens[position.Index].Spelling,
                    pathTerminalName,
                    StringComparison.Ordinal))
                .OrderBy(static position => position.Start)
                .Select(position => syntaxTokens[position.Index].Identity)
                .OfType<ComptimeSyntaxIdentity>()
                .FirstOrDefault();
            identity = terminalCandidate!;
            return terminalCandidate != null;
        }

        var startCandidate = positions
            .Where(position => position.Start == span.Position)
            .Select(position => syntaxTokens[position.Index].Identity)
            .OfType<ComptimeSyntaxIdentity>()
            .FirstOrDefault();
        identity = startCandidate ?? (candidates.Length == 1 ? candidates[0] : null!);
        return identity != null;
    }

    private static bool IsIdentifierCategoryValidForNode(string category, EidosAstNode node) => category switch
    {
        "Item" => node is Declaration,
        "Member" => node is Field or Constructor or CaseTypeDef or FuncDef or FuncDecl,
        "Value" => node is LetDecl or IdentifierExpr or PathExpr,
        "Type" => node is AdtDef or CaseTypeDef or TypePath,
        "Function" => node is FuncDef or FuncDecl or IdentifierExpr or PathExpr or MethodCallExpr,
        "Field" => node is Field or IdentifierExpr or MethodCallExpr,
        "Constructor" => node is Constructor or CtorExpr or CtorPattern or TypePath,
        "Parameter" or "Local" => node is VarPattern or AsPattern or FieldPattern or IdentifierExpr,
        "Module" => node is ModuleDecl or PathExpr,
        "AssociatedType" => node is AssociatedTypeDecl or TypePath,
        "AssociatedConst" => node is AssociatedConstDecl or IdentifierExpr or PathExpr,
        _ => false
    };

    private bool TryCreateFunction(
        ComptimeMetaObjectValue declaration,
        out FuncDef function,
        out string reason)
    {
        function = new FuncDef();
        reason = string.Empty;
        if (!TryGetString(declaration, "name", out var name, out reason) ||
            !ValidateGeneratedValueName(name, out reason) ||
            !TryGetSequence(declaration, "parameters", out var parameterValues, out reason) ||
            !TryGetType(declaration, "result", out var resultType, out reason) ||
            !TryGetObject(declaration, "body", out var bodyValue, out reason))
        {
            return false;
        }

        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        var parameterPatterns = new List<Pattern>(parameterValues.Count);
        var parameterTypes = new List<TypeNode>(parameterValues.Count);
        for (var index = 0; index < parameterValues.Count; index++)
        {
            if (parameterValues[index] is not ComptimeMetaObjectValue { SchemaKind: "parameter" } parameter ||
                !TryGetString(parameter, "identity", out var identity, out reason) ||
                !TryGetString(parameter, "name", out var requestedName, out reason) ||
                !TryGetType(parameter, "type", out var parameterType, out reason))
            {
                reason = string.IsNullOrWhiteSpace(reason)
                    ? $"function parameter {index} is not a Meta.Parameter"
                    : reason;
                return false;
            }

            var hygienicName = CreateHygienicName("p", identity, requestedName);
            if (!bindings.TryAdd(identity, hygienicName))
            {
                reason = $"duplicate generated parameter identity '{identity}'";
                return false;
            }

            var pattern = new VarPattern();
            pattern.SetSpan(_invocationSpan);
            pattern.SetName(hygienicName);
            parameterPatterns.Add(pattern);
            parameterTypes.Add(CreateTypeNode(parameterType));
        }

        if (!TryCreateExpression(bodyValue, bindings, out var body, out reason))
        {
            return false;
        }

        var entryPattern = parameterPatterns.Count switch
        {
            0 => CreateWildcardPattern(),
            1 => parameterPatterns[0],
            _ => CreateTuplePattern(parameterPatterns)
        };
        var branch = new PatternBranch();
        branch.SetSpan(_invocationSpan);
        branch.SetPattern(entryPattern);
        branch.SetExpression(body);

        function.SetSpan(_invocationSpan);
        function.SetName(name);
        function.SetTypeParams(CloneTargetTypeParameters());
        function.SetSignature(CreateFunctionType(parameterTypes, CreateTypeNode(resultType)));
        function.SetBody([branch]);
        return true;
    }

    private bool TryCreateImplementation(
        ComptimeMetaObjectValue declaration,
        int outputIndex,
        MetaDeclarationPlacement placement,
        List<MaterializedMetaNode> nodes,
        out string reason,
        string? generationSlotIdentity)
    {
        reason = string.Empty;
        if (_target is not (AdtDef or CaseTypeDef))
        {
            reason = "meta.instance can only target a type or case-type declaration";
            return false;
        }

        if (!TryGetDecl(declaration, "trait", out var trait, out reason) ||
            _symbolTable.GetSymbol<TraitSymbol>(trait.SymbolId) == null)
        {
            reason = string.IsNullOrWhiteSpace(reason)
                ? "meta.instance requires a trait declaration handle"
                : reason;
            return false;
        }

        if (!TryGetType(declaration, "target", out var implementationTarget, out reason) ||
            !string.Equals(
                implementationTarget.TypeRef.StableIdentity,
                MetaComptimeIntrinsics.CreateTypeTargetValue(_target, _symbolTable, _targetPath).TypeRef.StableIdentity,
                StringComparison.Ordinal))
        {
            reason = "meta.instance target must be the current derive target";
            return false;
        }

        if (!TryGetSequence(declaration, "methods", out var methodValues, out reason))
        {
            return false;
        }

        var methods = new List<FuncDef>(methodValues.Count);
        for (var methodIndex = 0; methodIndex < methodValues.Count; methodIndex++)
        {
            if (methodValues[methodIndex] is not ComptimeMetaObjectValue { SchemaKind: "declaration.function" } methodValue ||
                !TryCreateFunction(methodValue, out var method, out reason))
            {
                reason = string.IsNullOrWhiteSpace(reason)
                    ? $"implementation method {methodIndex} must be a structured function declaration"
                    : reason;
                return false;
            }

            methods.Add(method);
        }

        var traitRef = new TraitRef();
        traitRef.SetSpan(_invocationSpan);
        traitRef.SetTraitName(trait.Name);
        if (_symbolTable.Modules.TryGetOwningModule(trait.SymbolId, out var traitModule))
        {
            traitRef.ModulePath = [.. traitModule.Path];
        }

        var identity = generationSlotIdentity ??
                       $"{trait.SymbolId.Value}:{implementationTarget.TypeRef.StableIdentity}:{outputIndex}";
        var instance = new InstanceDecl();
        instance.SetSpan(_invocationSpan);
        instance.SetName(CreateHygienicCompileTimeName(identity, trait.Name));
        instance.SetTypeParams(CloneTargetTypeParameters());
        instance.SetTrait(traitRef);
        instance.SetTargetType(CreateTypeNode(implementationTarget));
        instance.SetMethods(methods);
        instance.SetMembers([.. methods]);
        nodes.Add(new MaterializedMetaNode(
            instance,
            outputIndex,
            Placement: placement,
            GenerationSlotIdentity: generationSlotIdentity));

        return true;
    }

    private bool TryCreateModule(
        ComptimeMetaObjectValue declaration,
        int outputIndex,
        List<MetaExpansionDiagnostic> diagnostics,
        out ModuleDecl module,
        out string reason)
    {
        module = new ModuleDecl();
        if (!TryGetString(declaration, "name", out var name, out reason) ||
            !TryGetSequence(declaration, "items", out var itemValues, out reason))
        {
            return false;
        }

        var path = name
            .Split(WellKnownStrings.Separators.Path, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (path.Count == 0 || path.Any(static segment =>
                segment.StartsWith("__", StringComparison.Ordinal) ||
                segment.Contains("__spec_", StringComparison.Ordinal) ||
                segment.Any(static character => !(char.IsLetterOrDigit(character) || character == '_')) ||
                !string.Equals(
                    segment,
                    NamingStyleDiagnosticBuilder.Normalize(
                        segment,
                        NamingStyleDiagnosticBuilder.NamingConvention.UpperCamelCase),
                    StringComparison.Ordinal)))
        {
            reason = $"generated module path '{name}' must contain UpperCamelCase segments outside the reserved namespace";
            return false;
        }

        var declarations = new List<Declaration>(itemValues.Count);
        for (var itemIndex = 0; itemIndex < itemValues.Count; itemIndex++)
        {
            var materialized = new List<MaterializedMetaNode>();
            if (!TryMaterializeDeclaration(
                    itemValues[itemIndex],
                    outputIndex,
                    materialized,
                    diagnostics,
                    MetaDeclarationPlacement.AfterTarget,
                    out reason) ||
                materialized.Any(static item => item.Node is not Declaration))
            {
                reason = string.IsNullOrWhiteSpace(reason)
                    ? $"meta.module item {itemIndex} is not a declaration"
                    : reason;
                return false;
            }

            declarations.AddRange(materialized.Select(static item => (Declaration)item.Node));
        }

        module.SetSpan(_invocationSpan);
        module.SetPath(path);
        module.SetDeclarations(declarations);
        reason = string.Empty;
        return true;
    }

    private bool TryCreateComptimeValue(
        ComptimeMetaObjectValue declaration,
        out LetDecl let,
        out string reason)
    {
        let = new LetDecl();
        if (!TryGetString(declaration, "name", out var name, out reason) ||
            !TryGetType(declaration, "type", out var type, out reason) ||
            !ValidateGeneratedComptimeName(name, type, out reason) ||
            !TryGetObject(declaration, "value", out var valueObject, out reason) ||
            !TryCreateExpression(valueObject, new Dictionary<string, string>(StringComparer.Ordinal), out var expression, out reason))
        {
            return false;
        }

        var pattern = new VarPattern();
        pattern.SetSpan(_invocationSpan);
        pattern.SetName(name);
        let.SetSpan(_invocationSpan);
        let.SetPattern(pattern);
        let.SetTypeAnnotation(CreateTypeNode(type));
        let.SetComptime(true);
        let.SetValue(expression);
        return true;
    }

    private bool TryCreateTest(
        ComptimeMetaObjectValue declaration,
        out FuncDef test,
        out string reason)
    {
        test = new FuncDef();
        if (!TryGetString(declaration, "name", out var name, out reason) ||
            !ValidateGeneratedValueName(name, out reason) ||
            !ValidateGeneratedTestName(name, out reason) ||
            !TryGetObject(declaration, "body", out var bodyObject, out reason) ||
            !TryCreateExpression(bodyObject, new Dictionary<string, string>(StringComparer.Ordinal), out var body, out reason))
        {
            return false;
        }

        var branch = new PatternBranch();
        branch.SetSpan(_invocationSpan);
        branch.SetPattern(CreateWildcardPattern());
        branch.SetExpression(body);
        test.SetSpan(_invocationSpan);
        test.SetName(name);
        test.SetSignature(CreateFunctionType([CreateSimpleTypePath("Unit")], CreateSimpleTypePath("Unit")));
        test.SetBody([branch]);
        return true;
    }

    private bool TryReadDiagnostic(
        ComptimeMetaObjectValue declaration,
        int outputIndex,
        out MetaExpansionDiagnostic diagnostic,
        out string reason)
    {
        diagnostic = null!;
        if (!TryGetString(declaration, "level", out var level, out reason) ||
            !TryGetObject(declaration, "span", out var spanObject, out reason) ||
            !MetaComptimeIntrinsics.TryReadSpan(spanObject, out var span) ||
            !TryGetString(declaration, "message", out var message, out reason))
        {
            reason = string.IsNullOrWhiteSpace(reason) ? "invalid structured diagnostic" : reason;
            return false;
        }

        var normalizedLevel = level.ToLowerInvariant();
        if (normalizedLevel is not ("error" or "warning"))
        {
            reason = $"unsupported structured diagnostic level '{level}'";
            return false;
        }

        diagnostic = new MetaExpansionDiagnostic(normalizedLevel, span, message, outputIndex);
        return true;
    }

    private bool TryCreateExpression(
        ComptimeMetaObjectValue expression,
        Dictionary<string, string> bindings,
        out EidosAstNode result,
        out string reason)
    {
        result = null!;
        reason = string.Empty;
        switch (expression.SchemaKind)
        {
            case "expr.parameter":
                return TryCreateHandleReference(expression, "parameter", bindings, out result, out reason);
            case "expr.binding":
                return TryCreateHandleReference(expression, "binding", bindings, out result, out reason);
            case "expr.decl":
                if (!TryGetDecl(expression, "decl", out var declaration, out reason))
                {
                    return false;
                }

                result = CreateDeclarationReference(declaration);
                return true;
            case "expr.int":
                return TryCreateLiteral<ComptimeIntegerValue>(expression, value => value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), out result, out reason);
            case "expr.bool":
                return TryCreateLiteral<ComptimeBoolValue>(expression, value => value.Value ? "true" : "false", out result, out reason);
            case "expr.string":
                return TryCreateLiteral<ComptimeStringValue>(expression, value => $"\"{EscapeString(value.Value)}\"", out result, out reason);
            case "expr.unit":
                result = CreateLiteral("()");
                return true;
            case "expr.call":
                return TryCreateCall(expression, bindings, out result, out reason);
            case "expr.constructor":
                return TryCreateConstructor(expression, "arguments", bindings, named: false, out result, out reason);
            case "expr.record-constructor":
                return TryCreateConstructor(expression, "fields", bindings, named: true, out result, out reason);
            case "expr.field":
                return TryCreateFieldAccess(expression, bindings, out result, out reason);
            case "expr.binary":
                return TryCreateBinary(expression, bindings, out result, out reason);
            case "expr.tuple":
                return TryCreateSequenceExpression<TupleExpr>(expression, bindings, out result, out reason);
            case "expr.list":
                return TryCreateSequenceExpression<ListExpr>(expression, bindings, out result, out reason);
            case "expr.match":
                return TryCreateMatch(expression, bindings, out result, out reason);
            default:
                reason = $"unsupported structured expression kind '{expression.SchemaKind}'";
                return false;
        }
    }

    private bool TryCreateHandleReference(
        ComptimeMetaObjectValue expression,
        string property,
        IReadOnlyDictionary<string, string> bindings,
        out EidosAstNode result,
        out string reason)
    {
        result = null!;
        if (!TryGetObject(expression, property, out var handle, out reason) ||
            !TryGetString(handle, "identity", out var identity, out reason) ||
            !bindings.TryGetValue(identity, out var name))
        {
            reason = string.IsNullOrWhiteSpace(reason)
                ? "structured expression references a binding outside its hygiene scope"
                : reason;
            return false;
        }

        var identifier = new IdentifierExpr();
        identifier.SetSpan(_invocationSpan);
        identifier.SetName(name);
        result = identifier;
        return true;
    }

    private bool TryCreateLiteral<T>(
        ComptimeMetaObjectValue expression,
        Func<T, string> format,
        out EidosAstNode result,
        out string reason)
        where T : ComptimeValue
    {
        result = null!;
        if (!expression.TryGet("value", out var value) || value is not T typed)
        {
            reason = $"{expression.SchemaKind} contains an invalid literal value";
            return false;
        }

        result = CreateLiteral(format(typed));
        reason = string.Empty;
        return true;
    }

    private bool TryCreateCall(
        ComptimeMetaObjectValue expression,
        Dictionary<string, string> bindings,
        out EidosAstNode result,
        out string reason)
    {
        result = null!;
        if (!TryGetObject(expression, "callee", out var calleeObject, out reason) ||
            !TryCreateExpression(calleeObject, bindings, out var callee, out reason) ||
            !TryGetSequence(expression, "arguments", out var argumentValues, out reason))
        {
            return false;
        }

        var call = new CallExpr();
        call.SetSpan(_invocationSpan);
        call.SetFunction(callee);
        foreach (var argumentValue in argumentValues)
        {
            if (argumentValue is not ComptimeMetaObjectValue argumentObject ||
                !TryCreateExpression(argumentObject, bindings, out var argument, out reason))
            {
                reason = string.IsNullOrWhiteSpace(reason) ? "invalid structured call argument" : reason;
                return false;
            }

            call.AddPositionalArg(argument);
        }

        result = call;
        return true;
    }

    private bool TryCreateConstructor(
        ComptimeMetaObjectValue expression,
        string valuesProperty,
        Dictionary<string, string> bindings,
        bool named,
        out EidosAstNode result,
        out string reason)
    {
        result = null!;
        if (!TryGetDecl(expression, "constructor", out var constructor, out reason) ||
            _symbolTable.GetSymbol<CtorSymbol>(constructor.SymbolId) == null ||
            !TryGetSequence(expression, valuesProperty, out var values, out reason))
        {
            reason = string.IsNullOrWhiteSpace(reason) ? "structured constructor requires a constructor handle" : reason;
            return false;
        }

        var ctor = new CtorExpr();
        ctor.SetSpan(_invocationSpan);
        ctor.SetConstructorPath(CreateDeclarationTypePath(constructor));
        if (!named)
        {
            foreach (var value in values)
            {
                if (value is not ComptimeMetaObjectValue valueObject ||
                    !TryCreateExpression(valueObject, bindings, out var argument, out reason))
                {
                    reason = string.IsNullOrWhiteSpace(reason) ? "invalid constructor argument" : reason;
                    return false;
                }

                ctor.AddPositionalArg(argument);
            }
        }
        else
        {
            foreach (var value in values)
            {
                if (value is not ComptimeMetaObjectValue { SchemaKind: "named-expr" } namedValue ||
                    !TryGetObject(namedValue, "field", out var field, out reason) ||
                    !TryGetString(field, "name", out var fieldName, out reason) ||
                    !TryGetObject(namedValue, "expression", out var fieldExpression, out reason) ||
                    !TryCreateExpression(fieldExpression, bindings, out var fieldValue, out reason))
                {
                    reason = string.IsNullOrWhiteSpace(reason) ? "invalid named constructor argument" : reason;
                    return false;
                }

                var fieldInit = new FieldInit();
                fieldInit.SetSpan(_invocationSpan);
                fieldInit.SetFieldName(fieldName);
                fieldInit.SetValue(fieldValue);
                ctor.AddNamedArg(fieldInit);
            }
        }

        result = ctor;
        return true;
    }

    private bool TryCreateFieldAccess(
        ComptimeMetaObjectValue expression,
        Dictionary<string, string> bindings,
        out EidosAstNode result,
        out string reason)
    {
        result = null!;
        if (!TryGetObject(expression, "subject", out var subjectObject, out reason) ||
            !TryCreateExpression(subjectObject, bindings, out var subject, out reason) ||
            !TryGetObject(expression, "field", out var field, out reason) ||
            !TryGetString(field, "name", out var fieldName, out reason))
        {
            return false;
        }

        var access = new MethodCallExpr();
        access.SetSpan(_invocationSpan);
        access.SetReceiver(subject);
        access.SetMethodName(fieldName);
        result = access;
        return true;
    }

    private bool TryCreateBinary(
        ComptimeMetaObjectValue expression,
        Dictionary<string, string> bindings,
        out EidosAstNode result,
        out string reason)
    {
        result = null!;
        if (!TryGetString(expression, "operator", out var operatorName, out reason) ||
            !TryMapBinaryOperator(operatorName, out var op) ||
            !TryGetObject(expression, "left", out var leftObject, out reason) ||
            !TryGetObject(expression, "right", out var rightObject, out reason) ||
            !TryCreateExpression(leftObject, bindings, out var left, out reason) ||
            !TryCreateExpression(rightObject, bindings, out var right, out reason))
        {
            reason = string.IsNullOrWhiteSpace(reason) ? $"unsupported structured binary operator '{operatorName}'" : reason;
            return false;
        }

        var binary = new BinaryExpr();
        binary.SetSpan(_invocationSpan);
        binary.SetOperator(op);
        binary.SetLeft(left);
        binary.SetRight(right);
        result = binary;
        return true;
    }

    private bool TryCreateSequenceExpression<T>(
        ComptimeMetaObjectValue expression,
        Dictionary<string, string> bindings,
        out EidosAstNode result,
        out string reason)
        where T : EidosAstNode, new()
    {
        result = null!;
        if (!TryGetSequence(expression, "elements", out var values, out reason))
        {
            return false;
        }

        var elements = new List<EidosAstNode>(values.Count);
        foreach (var value in values)
        {
            if (value is not ComptimeMetaObjectValue valueObject ||
                !TryCreateExpression(valueObject, bindings, out var element, out reason))
            {
                reason = string.IsNullOrWhiteSpace(reason) ? "invalid structured sequence element" : reason;
                return false;
            }

            elements.Add(element);
        }

        if (typeof(T) == typeof(TupleExpr))
        {
            result = new TupleExpr { Elements = elements };
        }
        else
        {
            var list = new ListExpr();
            list.SetSpan(_invocationSpan);
            foreach (var element in elements)
            {
                list.AddElement(element);
            }

            result = list;
        }

        return true;
    }

    private bool TryCreateMatch(
        ComptimeMetaObjectValue expression,
        Dictionary<string, string> bindings,
        out EidosAstNode result,
        out string reason)
    {
        result = null!;
        if (!TryGetObject(expression, "subject", out var subjectObject, out reason) ||
            !TryCreateExpression(subjectObject, bindings, out var subject, out reason) ||
            !TryGetSequence(expression, "branches", out var branchValues, out reason))
        {
            return false;
        }

        var match = new MatchExpr();
        match.SetSpan(_invocationSpan);
        match.SetMatchedExpression(subject);
        foreach (var branchValue in branchValues)
        {
            if (branchValue is not ComptimeMetaObjectValue { SchemaKind: "branch" } branchObject ||
                !TryGetObject(branchObject, "pattern", out var patternObject, out reason) ||
                !TryGetObject(branchObject, "expression", out var expressionObject, out reason))
            {
                reason = string.IsNullOrWhiteSpace(reason) ? "invalid structured match branch" : reason;
                return false;
            }

            var branchBindings = new Dictionary<string, string>(bindings, StringComparer.Ordinal);
            if (!TryCreatePattern(patternObject, branchBindings, out var pattern, out reason) ||
                !TryCreateExpression(expressionObject, branchBindings, out var branchExpression, out reason))
            {
                return false;
            }

            var branch = new PatternBranch();
            branch.SetSpan(_invocationSpan);
            branch.SetPattern(pattern);
            branch.SetExpression(branchExpression);
            match.AddBranch(branch);
        }

        result = match;
        return true;
    }

    private bool TryCreatePattern(
        ComptimeMetaObjectValue patternValue,
        Dictionary<string, string> bindings,
        out Pattern pattern,
        out string reason)
    {
        pattern = null!;
        reason = string.Empty;
        switch (patternValue.SchemaKind)
        {
            case "pattern.wildcard":
                pattern = CreateWildcardPattern();
                return true;
            case "pattern.binding":
                if (!TryGetObject(patternValue, "binding", out var binding, out reason) ||
                    !TryGetString(binding, "identity", out var identity, out reason) ||
                    !TryGetString(binding, "name", out var requestedName, out reason))
                {
                    return false;
                }

                var name = CreateHygienicName("b", identity, requestedName);
                if (!bindings.TryAdd(identity, name))
                {
                    reason = $"duplicate generated binding identity '{identity}'";
                    return false;
                }

                var variable = new VarPattern();
                variable.SetSpan(_invocationSpan);
                variable.SetName(name);
                pattern = variable;
                return true;
            case "pattern.constructor":
                return TryCreateConstructorPattern(patternValue, bindings, named: false, out pattern, out reason);
            case "pattern.record-constructor":
                return TryCreateConstructorPattern(patternValue, bindings, named: true, out pattern, out reason);
            default:
                reason = $"unsupported structured pattern kind '{patternValue.SchemaKind}'";
                return false;
        }
    }

    private bool TryCreateConstructorPattern(
        ComptimeMetaObjectValue patternValue,
        Dictionary<string, string> bindings,
        bool named,
        out Pattern pattern,
        out string reason)
    {
        pattern = null!;
        if (!TryGetDecl(patternValue, "constructor", out var constructor, out reason) ||
            _symbolTable.GetSymbol<CtorSymbol>(constructor.SymbolId) == null ||
            !TryGetSequence(patternValue, named ? "fields" : "patterns", out var values, out reason))
        {
            reason = string.IsNullOrWhiteSpace(reason) ? "structured constructor pattern requires a constructor handle" : reason;
            return false;
        }

        var ctorPattern = new CtorPattern();
        ctorPattern.SetSpan(_invocationSpan);
        ApplyDeclarationPath(ctorPattern, constructor);
        if (!named)
        {
            foreach (var value in values)
            {
                if (value is not ComptimeMetaObjectValue valueObject ||
                    !TryCreatePattern(valueObject, bindings, out var childPattern, out reason))
                {
                    reason = string.IsNullOrWhiteSpace(reason) ? "invalid constructor subpattern" : reason;
                    return false;
                }

                ctorPattern.AddPositionalPattern(childPattern);
            }
        }
        else
        {
            foreach (var value in values)
            {
                if (value is not ComptimeMetaObjectValue { SchemaKind: "field-pattern" } fieldPatternValue ||
                    !TryGetObject(fieldPatternValue, "field", out var field, out reason) ||
                    !TryGetString(field, "name", out var fieldName, out reason) ||
                    !TryGetObject(fieldPatternValue, "pattern", out var childValue, out reason) ||
                    !TryCreatePattern(childValue, bindings, out var childPattern, out reason))
                {
                    reason = string.IsNullOrWhiteSpace(reason) ? "invalid record constructor field pattern" : reason;
                    return false;
                }

                var fieldPattern = new FieldPattern();
                fieldPattern.SetSpan(_invocationSpan);
                fieldPattern.SetFieldName(fieldName);
                fieldPattern.SetPattern(childPattern);
                ctorPattern.AddNamedPattern(fieldPattern);
            }
        }

        pattern = ctorPattern;
        return true;
    }

    private EidosAstNode CreateDeclarationReference(ComptimeDeclValue declaration)
    {
        if (_symbolTable.Modules.TryGetOwningModule(declaration.SymbolId, out var module) &&
            module.Path.Count > 0 &&
            !module.Path.SequenceEqual(_symbolTable.Modules.GetModule(_targetModuleId)?.Path ?? []))
        {
            var path = new PathExpr();
            path.SetSpan(_invocationSpan);
            path.SetModulePath(module.Path);
            path.SetName(declaration.Name);
            return path;
        }

        var identifier = new IdentifierExpr();
        identifier.SetSpan(_invocationSpan);
        identifier.SetName(declaration.Name);
        return identifier;
    }

    private TypePath CreateDeclarationTypePath(ComptimeDeclValue declaration)
    {
        var path = new TypePath();
        path.SetSpan(_invocationSpan);
        path.SetTypeName(declaration.Name);
        if (_symbolTable.Modules.TryGetOwningModule(declaration.SymbolId, out var module))
        {
            path.ModulePath = [.. module.Path];
        }

        return path;
    }

    private void ApplyDeclarationPath(CtorPattern pattern, ComptimeDeclValue declaration)
    {
        pattern.SetConstructorName(declaration.Name);
        if (_symbolTable.Modules.TryGetOwningModule(declaration.SymbolId, out var module))
        {
            pattern.SetModulePath(module.Path);
        }
    }

    private List<TypeParam> CloneTargetTypeParameters()
    {
        return _target switch
        {
            AdtDef adt => adt.TypeParams.Select(CloneTypeParameter).ToList(),
            CaseTypeDef caseType => caseType.TypeParams.Select(CloneTypeParameter).ToList(),
            _ => []
        };
    }

    private TypeParam CloneTypeParameter(TypeParam parameter)
    {
        return new TypeParam
        {
            Name = parameter.Name,
            KindAnnotation = parameter.KindAnnotation,
            IsEffectSet = parameter.IsEffectSet,
            IsComptime = parameter.IsComptime,
            ComptimeTypeAnnotation = parameter.ComptimeTypeAnnotation == null ? null : CloneTypeNode(parameter.ComptimeTypeAnnotation),
            TraitConstraints = parameter.TraitConstraints.Select(CloneTraitRef).ToList(),
            Span = parameter.Span
        };
    }

    private TraitRef CloneTraitRef(TraitRef trait)
    {
        var clone = new TraitRef
        {
            ModulePath = [.. trait.ModulePath],
            TypeArgs = trait.TypeArgs.Select(CloneTypeNode).ToList()
        };
        if (trait.GenericArguments.Count > 0)
        {
            clone.GenericArguments = trait.GenericArguments.Select(CloneGenericArgument).ToList();
            clone.TypeArgs = clone.GenericArguments
                .OfType<TypeGenericArgumentNode>()
                .Select(static argument => argument.Type)
                .ToList();
        }

        clone.SetSpan(trait.Span);
        clone.SetTraitName(trait.TraitName);
        return clone;
    }

    private TypeNode CreateTypeNode(ComptimeTypeValue type)
    {
        if (type.TypeRef.InternalSyntax != null)
        {
            return CloneTypeNode(type.TypeRef.InternalSyntax);
        }

        var symbol = type.TypeRef.SymbolId.IsValid
            ? _symbolTable.GetSymbol(type.TypeRef.SymbolId)
            : null;
        var path = CreateSimpleTypePath(symbol?.Name ?? ExtractUnqualifiedTypeName(type.TypeRef.Name));
        path.SymbolId = type.TypeRef.SymbolId;
        if (type.TypeRef.SymbolId.IsValid &&
            _symbolTable.Modules.TryGetOwningModule(type.TypeRef.SymbolId, out var owner))
        {
            path.ModulePath = [.. owner.Path];
        }

        if (type.TypeRef.GenericArguments is { Count: > 0 } genericArguments)
        {
            path.SetGenericArguments(genericArguments.Select(CreateGenericArgumentNode));
        }
        else
        {
            path.TypeArgs = type.TypeRef.Arguments
                .Select(argument => CreateTypeNode(new ComptimeTypeValue(argument)))
                .ToList();
        }

        return path;
    }

    private TypeNode CloneTypeNode(TypeNode type)
    {
        return type switch
        {
            TypePath path => CloneTypePath(path),
            TupleType tuple => new TupleType { Elements = tuple.Elements.Select(CloneTypeNode).ToList(), Span = _invocationSpan },
            ArrowType arrow => CreateArrowType(CloneTypeNode(arrow.ParamType), CloneTypeNode(arrow.ReturnType)),
            EffectfulType effectful => new EffectfulType
            {
                InputType = CloneTypeNode(effectful.InputType),
                OutputType = effectful.OutputType == null ? null : CloneTypeNode(effectful.OutputType),
                EffectPath = [.. effectful.EffectPath],
                EffectPaths = effectful.EffectPaths.Select(static path => path.ToList()).ToList(),
                EffectPathSpans = [.. effectful.EffectPathSpans],
                Span = _invocationSpan
            },
            _ => type
        };
    }

    private TypePath CloneTypePath(TypePath path)
    {
        var clone = new TypePath
        {
            ModulePath = [.. path.ModulePath],
            TypeArgs = path.TypeArgs.Select(CloneTypeNode).ToList()
        };
        clone.SetPackageAlias(path.PackageAlias);
        clone.SetTypeName(path.TypeName);
        clone.SetSpan(_invocationSpan);
        clone.SymbolId = path.SymbolId;
        if (path.GenericArguments.Count > 0)
        {
            clone.SetGenericArguments(path.GenericArguments.Select(CloneGenericArgument));
        }

        return clone;
    }

    private GenericArgumentNode CloneGenericArgument(GenericArgumentNode argument) => argument switch
    {
        TypeGenericArgumentNode typeArgument => new TypeGenericArgumentNode
        {
            Type = CloneTypeNode(typeArgument.Type),
            Span = _invocationSpan
        },
        ValueGenericArgumentNode valueArgument => new ValueGenericArgumentNode
        {
            Expression = CloneGenericValueExpression(valueArgument.Expression),
            Span = _invocationSpan
        },
        EffectGenericArgumentNode effectArgument => new EffectGenericArgumentNode
        {
            EffectRow = CloneTypeNode(effectArgument.EffectRow),
            Span = _invocationSpan
        },
        UnresolvedGenericArgumentNode unresolved => new UnresolvedGenericArgumentNode
        {
            TypeCandidate = unresolved.TypeCandidate == null ? null : CloneTypeNode(unresolved.TypeCandidate),
            ValueCandidate = unresolved.ValueCandidate == null ? null : CloneGenericValueExpression(unresolved.ValueCandidate),
            Span = _invocationSpan
        },
        _ => argument
    };

    private GenericArgumentNode CreateGenericArgumentNode(MetaGenericArgumentRef argument)
    {
        return argument.Domain switch
        {
            MetaGenericArgumentDomain.Type when argument.Type != null => new TypeGenericArgumentNode
            {
                Type = CreateTypeNode(new ComptimeTypeValue(argument.Type)),
                Span = _invocationSpan
            },
            MetaGenericArgumentDomain.EffectRow when argument.Type != null => new EffectGenericArgumentNode
            {
                EffectRow = CreateTypeNode(new ComptimeTypeValue(argument.Type)),
                Span = _invocationSpan
            },
            MetaGenericArgumentDomain.Value => new ValueGenericArgumentNode
            {
                Expression = CreateGenericValueExpression(argument),
                Span = _invocationSpan
            },
            _ => new UnresolvedGenericArgumentNode
            {
                ValueCandidate = CreateGenericValueExpression(argument),
                Span = _invocationSpan
            }
        };
    }

    private EidosAstNode CreateGenericValueExpression(MetaGenericArgumentRef argument)
    {
        if (argument.SymbolId.IsValid)
        {
            var identifier = new IdentifierExpr();
            identifier.SetSpan(_invocationSpan);
            identifier.SetName(argument.Display);
            identifier.SymbolId = argument.SymbolId;
            return identifier;
        }

        return CreateLiteral(argument.Display);
    }

    private EidosAstNode CloneGenericValueExpression(EidosAstNode expression)
    {
        switch (expression)
        {
            case LiteralExpr literal:
                return CreateLiteral(literal.RawText);
            case IdentifierExpr identifier:
                var identifierClone = new IdentifierExpr();
                identifierClone.SetSpan(_invocationSpan);
                identifierClone.SetName(identifier.Name);
                identifierClone.SymbolId = identifier.SymbolId;
                return identifierClone;
            case PathExpr path:
                var pathClone = new PathExpr();
                pathClone.SetSpan(_invocationSpan);
                pathClone.SetPackageAlias(path.PackageAlias);
                pathClone.SetModulePath(path.ModulePath);
                pathClone.SetName(path.Name);
                pathClone.SymbolId = path.SymbolId;
                pathClone.SetTypeArgs(path.TypeArgs.Select(CloneTypeNode).ToList());
                pathClone.SetGenericArguments(path.GenericArguments.Select(CloneGenericArgument));
                return pathClone;
            case UnaryExpr unary when unary.Operand != null:
                var unaryClone = new UnaryExpr();
                unaryClone.SetSpan(_invocationSpan);
                unaryClone.SetOperator(unary.Operator);
                unaryClone.SetOperand(CloneGenericValueExpression(unary.Operand));
                return unaryClone;
            case BinaryExpr binary when binary.Left != null && binary.Right != null:
                var binaryClone = new BinaryExpr();
                binaryClone.SetSpan(_invocationSpan);
                binaryClone.SetOperator(binary.Operator);
                binaryClone.SetLeft(CloneGenericValueExpression(binary.Left));
                binaryClone.SetRight(CloneGenericValueExpression(binary.Right));
                return binaryClone;
            case CallExpr call when call.Function != null:
                var callClone = new CallExpr();
                callClone.SetSpan(_invocationSpan);
                callClone.SetFunction(CloneGenericValueExpression(call.Function));
                foreach (var argument in call.PositionalArgs)
                {
                    callClone.AddPositionalArg(CloneGenericValueExpression(argument));
                }

                return callClone;
            case TupleExpr tuple:
                return new TupleExpr
                {
                    Elements = tuple.Elements.Select(CloneGenericValueExpression).ToList(),
                    Span = _invocationSpan
                };
            case ListExpr list:
                var listClone = new ListExpr();
                listClone.SetSpan(_invocationSpan);
                foreach (var element in list.Elements)
                {
                    listClone.AddElement(CloneGenericValueExpression(element));
                }

                return listClone;
            default:
                return expression with { Span = _invocationSpan };
        }
    }

    private static string ExtractUnqualifiedTypeName(string name)
    {
        var withoutArguments = name.Split('[', 2)[0];
        return withoutArguments.Contains(WellKnownStrings.Separators.Path, StringComparison.Ordinal)
            ? withoutArguments.Split(WellKnownStrings.Separators.Path)[^1]
            : withoutArguments;
    }

    private TypePath CreateSimpleTypePath(string name)
    {
        var path = new TypePath();
        path.SetSpan(_invocationSpan);
        path.SetTypeName(name);
        return path;
    }

    private TypeNode CreateFunctionType(IReadOnlyList<TypeNode> parameters, TypeNode result)
    {
        var current = result;
        var effectiveParameters = parameters.Count == 0 ? [CreateSimpleTypePath("Unit")] : parameters;
        for (var index = effectiveParameters.Count - 1; index >= 0; index--)
        {
            current = CreateArrowType(effectiveParameters[index], current);
        }

        return current;
    }

    private ArrowType CreateArrowType(TypeNode parameter, TypeNode result)
    {
        var arrow = new ArrowType();
        arrow.SetSpan(_invocationSpan);
        arrow.SetParamType(parameter);
        arrow.SetReturnType(result);
        return arrow;
    }

    private static bool TryMapBinaryOperator(string name, out BinaryOp op)
    {
        op = name switch
        {
            "add" or "+" => BinaryOp.Add,
            "subtract" or "-" => BinaryOp.Subtract,
            "multiply" or "*" => BinaryOp.Multiply,
            "divide" or "/" => BinaryOp.Divide,
            "modulo" or "%" => BinaryOp.Modulo,
            "equal" or "==" => BinaryOp.Equal,
            "notEqual" or "!=" => BinaryOp.NotEqual,
            "less" or "<" => BinaryOp.Less,
            "lessEqual" or "<=" => BinaryOp.LessEqual,
            "greater" or ">" => BinaryOp.Greater,
            "greaterEqual" or ">=" => BinaryOp.GreaterEqual,
            "and" or "&&" => BinaryOp.And,
            "or" or "||" => BinaryOp.Or,
            "concat" or "++" => BinaryOp.Concat,
            _ => (BinaryOp)(-1)
        };
        return (int)op >= 0;
    }

    private static LiteralExpr CreateLiteral(string raw)
    {
        var literal = new LiteralExpr();
        literal.SetLiteral(raw);
        return literal;
    }

    private WildcardPattern CreateWildcardPattern()
    {
        var pattern = new WildcardPattern();
        pattern.SetSpan(_invocationSpan);
        return pattern;
    }

    private TuplePattern CreateTuplePattern(IEnumerable<Pattern> patterns)
    {
        var tuple = new TuplePattern();
        tuple.SetSpan(_invocationSpan);
        foreach (var pattern in patterns)
        {
            tuple.AddElement(pattern);
        }

        return tuple;
    }

    private static string CreateHygienicName(string prefix, string identity, string requestedName)
    {
        var hash = identity.Length >= 12 ? identity[..12] : identity;
        var suffix = new string(requestedName.Where(static ch => char.IsLetterOrDigit(ch) || ch == '_').ToArray());
        if (string.IsNullOrWhiteSpace(suffix))
        {
            suffix = prefix;
        }

        return $"meta_{prefix}_{hash}_{suffix}";
    }

    private static string CreateHygienicCompileTimeName(string identity, string requestedName)
    {
        var hash = identity.Length >= 12 ? identity[..12] : identity;
        var suffix = new string(requestedName.Where(static ch => char.IsLetterOrDigit(ch)).ToArray());
        return $"MetaInstance{hash}{(string.IsNullOrWhiteSpace(suffix) ? "Generated" : suffix)}";
    }

    private static string EscapeString(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);

    private static bool ValidateGeneratedValueName(string name, out string reason)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.StartsWith("__", StringComparison.Ordinal) ||
            name.Contains("__spec_", StringComparison.Ordinal) ||
            name.Any(static ch => !(char.IsLetterOrDigit(ch) || ch == '_')) ||
            !string.Equals(
                name,
                NamingStyleDiagnosticBuilder.Normalize(
                    name,
                    NamingStyleDiagnosticBuilder.NamingConvention.LowerSnakeCase),
                StringComparison.Ordinal))
        {
            reason = $"generated value declaration name '{name}' must use lower_snake_case outside the reserved namespace";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool ValidateGeneratedComptimeName(
        string name,
        ComptimeTypeValue declaredType,
        out string reason)
    {
        var convention = declaredType.TypeRef.TypeId == new TypeId(BaseTypes.TypeValueId) ||
                         string.Equals(declaredType.TypeRef.Name, WellKnownStrings.BuiltinTypes.Type, StringComparison.Ordinal)
            ? NamingStyleDiagnosticBuilder.NamingConvention.UpperCamelCase
            : NamingStyleDiagnosticBuilder.NamingConvention.ScreamingSnakeCase;
        if (string.IsNullOrWhiteSpace(name) ||
            name.StartsWith("__", StringComparison.Ordinal) ||
            name.Contains("__spec_", StringComparison.Ordinal) ||
            name.Any(static ch => !(char.IsLetterOrDigit(ch) || ch == '_')) ||
            !string.Equals(
                name,
                NamingStyleDiagnosticBuilder.Normalize(name, convention),
                StringComparison.Ordinal))
        {
            var expectedConvention = convention == NamingStyleDiagnosticBuilder.NamingConvention.UpperCamelCase
                ? "UpperCamelCase"
                : "SCREAMING_SNAKE_CASE";
            reason = $"generated comptime declaration name '{name}' must use {expectedConvention} outside the reserved namespace";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool ValidateGeneratedTestName(string name, out string reason)
    {
        if (!name.StartsWith("test_", StringComparison.Ordinal) || name.Length == "test_".Length)
        {
            reason = $"generated test declaration name '{name}' must start with 'test_'";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryGetValue(
        ComptimeMetaObjectValue value,
        string property,
        out ComptimeValue result,
        out string reason)
    {
        if (!value.TryGet(property, out result))
        {
            reason = $"{value.SchemaKind} requires property '{property}'";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryGetString(
        ComptimeMetaObjectValue value,
        string property,
        out string result,
        out string reason)
    {
        result = string.Empty;
        if (!value.TryGet(property, out var propertyValue) || propertyValue is not ComptimeStringValue stringValue)
        {
            reason = $"{value.SchemaKind} requires string property '{property}'";
            return false;
        }

        result = stringValue.Value;
        reason = string.Empty;
        return true;
    }

    private static bool TryGetType(
        ComptimeMetaObjectValue value,
        string property,
        out ComptimeTypeValue result,
        out string reason)
    {
        result = null!;
        if (!value.TryGet(property, out var propertyValue) || propertyValue is not ComptimeTypeValue typeValue)
        {
            reason = $"{value.SchemaKind} requires Type property '{property}'";
            return false;
        }

        result = typeValue;
        reason = string.Empty;
        return true;
    }

    private static bool TryGetDecl(
        ComptimeMetaObjectValue value,
        string property,
        out ComptimeDeclValue result,
        out string reason)
    {
        result = null!;
        if (!value.TryGet(property, out var propertyValue) || propertyValue is not ComptimeDeclValue declValue)
        {
            reason = $"{value.SchemaKind} requires declaration handle property '{property}'";
            return false;
        }

        result = declValue;
        reason = string.Empty;
        return true;
    }

    private static bool TryGetObject(
        ComptimeMetaObjectValue value,
        string property,
        out ComptimeMetaObjectValue result,
        out string reason)
    {
        result = null!;
        if (!value.TryGet(property, out var propertyValue) || propertyValue is not ComptimeMetaObjectValue objectValue)
        {
            reason = $"{value.SchemaKind} requires structured property '{property}'";
            return false;
        }

        result = objectValue;
        reason = string.Empty;
        return true;
    }

    private static bool TryGetSequence(
        ComptimeMetaObjectValue value,
        string property,
        out IReadOnlyList<ComptimeValue> result,
        out string reason)
    {
        result = [];
        if (!value.TryGet(property, out var propertyValue) ||
            propertyValue is not ComptimeSequenceValue { Kind: ComptimeSequenceKind.List } sequence)
        {
            reason = $"{value.SchemaKind} requires list property '{property}'";
            return false;
        }

        result = sequence.Elements;
        reason = string.Empty;
        return true;
    }
}
