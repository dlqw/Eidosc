using Eidosc.Ast.Declarations;
using Eidosc.Utils;

namespace Eidosc.Semantic;

internal sealed record ClauseBindingDiagnostic(SourceSpan Span, string Message, string Code = "E3000");

internal sealed record DeclarationClauseBindingResult(
    IReadOnlyList<ClauseIR> Clauses,
    IReadOnlyList<MetaInvocationIR> MetaInvocations,
    IReadOnlyList<ClauseBindingDiagnostic> Diagnostics);

internal static class DeclarationClauseBinder
{
    public static IReadOnlyList<ClauseBindingDiagnostic> BindTree(
        ModuleDecl module,
        string languageVersion,
        CompilerOwnedSourceGrant? sourceGrant = null)
    {
        sourceGrant ??= CompilerOwnedSourceGrant.None;
        var diagnostics = new List<ClauseBindingDiagnostic>();
        BindModule(module);
        return diagnostics;

        void BindModule(ModuleDecl current)
        {
            BindDeclaration(current);
            foreach (var declaration in current.Declarations)
            {
                if (declaration is ModuleDecl nested)
                {
                    BindModule(nested);
                }
                else
                {
                    BindDeclaration(declaration);
                    if (declaration is AdtDef type)
                    {
                        foreach (var caseType in type.Cases)
                        {
                            BindCaseTree(caseType);
                        }
                    }
                }
            }
        }

        void BindCaseTree(CaseTypeDef caseType)
        {
            BindDeclaration(caseType);
            foreach (var child in caseType.Cases)
            {
                BindCaseTree(child);
            }
        }

        void BindDeclaration(Declaration declaration)
        {
            var result = Bind(declaration, languageVersion, sourceGrant);
            declaration.SetBoundClauses(result.Clauses, result.MetaInvocations);
            diagnostics.AddRange(result.Diagnostics);
        }
    }

    public static DeclarationClauseBindingResult Bind(
        Declaration declaration,
        string languageVersion,
        CompilerOwnedSourceGrant? sourceGrant = null)
    {
        sourceGrant ??= CompilerOwnedSourceGrant.None;
        var diagnostics = new List<ClauseBindingDiagnostic>();
        var clauses = new List<ClauseIR>(declaration.Clauses.Count);
        var invocations = new List<MetaInvocationIR>();
        var target = GetTarget(declaration);
        var declarationIdentity = CreateDeclarationIdentity(declaration);
        var occurrences = declaration.Clauses
            .GroupBy(static clause => clause.ClauseKind)
            .ToDictionary(static group => group.Key, static group => group.ToList());

        for (var clauseIndex = 0; clauseIndex < declaration.Clauses.Count; clauseIndex++)
        {
            var clause = declaration.Clauses[clauseIndex];
            if (!ClauseSchema.TryGet(clause.Keyword, out var spec))
            {
                diagnostics.Add(new ClauseBindingDiagnostic(clause.Span, $"unknown declaration clause '{clause.Keyword}'"));
                continue;
            }

            if ((spec.Targets & target) == 0)
            {
                diagnostics.Add(new ClauseBindingDiagnostic(
                    clause.Span,
                    $"clause '{spec.Keyword}' is not valid on {DescribeTarget(target)} declarations"));
            }

            if (!spec.Repeatable && occurrences[spec.Kind].Count > 1 &&
                !ReferenceEquals(occurrences[spec.Kind][0], clause))
            {
                diagnostics.Add(new ClauseBindingDiagnostic(clause.Span, $"clause '{spec.Keyword}' cannot be repeated"));
            }

            foreach (var requirement in spec.Requires ?? [])
            {
                if (!occurrences.ContainsKey(requirement))
                {
                    diagnostics.Add(new ClauseBindingDiagnostic(
                        clause.Span,
                        $"clause '{spec.Keyword}' requires clause '{GetKeyword(requirement)}' on the same declaration"));
                }
            }

            foreach (var conflict in spec.Conflicts ?? [])
            {
                if (occurrences.ContainsKey(conflict))
                {
                    diagnostics.Add(new ClauseBindingDiagnostic(
                        clause.Span,
                        $"clause '{spec.Keyword}' conflicts with clause '{GetKeyword(conflict)}' on the same declaration"));
                }
            }

            var hasCompilerGrant = sourceGrant.Allows(clause.Span);
            if (spec.Privilege == ClausePrivilegePolicy.ToolchainOwnedSource && !hasCompilerGrant)
            {
                diagnostics.Add(new ClauseBindingDiagnostic(
                    clause.Span,
                    $"clause '{spec.Keyword}' is reserved for toolchain-owned source"));
            }

            if (spec.MetaGeneratorOnly && !IsMetaGeneratorDeclaration(declaration))
            {
                diagnostics.Add(new ClauseBindingDiagnostic(
                    clause.Span,
                    $"clause '{spec.Keyword}' is only valid on comptime meta generator functions"));
            }

            var arguments = BindArguments(clause, spec, diagnostics);
            var occurrenceId = new ClauseOccurrenceId(declarationIdentity, clauseIndex);
            clauses.Add(new ClauseIR(
                ClauseSchema.Version,
                occurrenceId,
                spec.Kind,
                spec.Keyword,
                spec.Stage,
                spec.SourceOrder,
                clauseIndex,
                arguments,
                clause.Span,
                hasCompilerGrant));

            if (spec.ProducesMetaInvocation)
            {
                LowerMetaInvocations(clause, spec, occurrenceId, clauseIndex, arguments, invocations, diagnostics);
            }
        }

        ValidateForeignContract(declaration, occurrences, diagnostics);
        ValidateCompilerDirective(declaration, occurrences, diagnostics);
        _ = languageVersion;
        return new DeclarationClauseBindingResult(clauses, invocations, diagnostics);
    }

    private static void ValidateCompilerDirective(
        Declaration declaration,
        IReadOnlyDictionary<DeclarationClauseKind, List<DeclarationClause>> occurrences,
        List<ClauseBindingDiagnostic> diagnostics)
    {
        if (!occurrences.ContainsKey(DeclarationClauseKind.Compiler))
        {
            return;
        }

        if (!CompilerDirectiveIR.TryCreate(declaration.Clauses, out var directive, out var errors))
        {
            diagnostics.AddRange(errors.Select(error => new ClauseBindingDiagnostic(declaration.Span, error, "E3058")));
        }

        if (declaration is not (FuncDef or FuncDecl) &&
            (directive.Intrinsic != null || directive.LlvmAbi != null))
        {
            diagnostics.Add(new ClauseBindingDiagnostic(
                declaration.Span,
                "compiler intrinsic and llvm_abi fields are only valid on functions",
                "E3058"));
        }

        if (directive.Intrinsic != null &&
            occurrences.ContainsKey(DeclarationClauseKind.Extern))
        {
            diagnostics.Add(new ClauseBindingDiagnostic(
                declaration.Span,
                "compiler intrinsic conflicts with extern on the same declaration",
                "E3058"));
        }
    }

    private static IReadOnlyList<ClauseArgumentIR> BindArguments(
        DeclarationClause clause,
        DeclarationClauseSpec spec,
        List<ClauseBindingDiagnostic> diagnostics)
    {
        var rawArguments = clause.ArgumentTokens;
        var requiresArguments = spec.Arguments != ClauseArgumentGrammar.None;
        if (!requiresArguments && rawArguments.Count > 0)
        {
            diagnostics.Add(new ClauseBindingDiagnostic(clause.Span, $"clause '{spec.Keyword}' does not accept arguments"));
        }
        else if (requiresArguments && rawArguments.Count == 0)
        {
            diagnostics.Add(new ClauseBindingDiagnostic(clause.Span, $"clause '{spec.Keyword}' requires an argument"));
        }

        if (spec.Arguments is not (ClauseArgumentGrammar.PathList or ClauseArgumentGrammar.IdentifierList) &&
            spec.Arguments != ClauseArgumentGrammar.None &&
            rawArguments.Count > 1 &&
            spec.Arguments != ClauseArgumentGrammar.TokenIsland)
        {
            diagnostics.Add(new ClauseBindingDiagnostic(clause.Span, $"clause '{spec.Keyword}' accepts exactly one argument"));
        }

        var bound = new List<ClauseArgumentIR>(rawArguments.Count);
        for (var index = 0; index < rawArguments.Count; index++)
        {
            var raw = rawArguments[index].Trim();
            var canonical = Canonicalize(raw, spec.CanonicalArgumentType);
            var path = IsPathArgument(spec.CanonicalArgumentType)
                ? SplitPath(canonical)
                : [];
            if (!IsValidArgument(raw, spec.Arguments))
            {
                diagnostics.Add(new ClauseBindingDiagnostic(
                    clause.Span,
                    $"clause '{spec.Keyword}' has an invalid {DescribeArgument(spec.CanonicalArgumentType)} argument '{raw}'"));
            }

            bound.Add(new ClauseArgumentIR(index, spec.CanonicalArgumentType, canonical, path));
        }

        return bound;
    }

    private static void LowerMetaInvocations(
        DeclarationClause clause,
        DeclarationClauseSpec spec,
        ClauseOccurrenceId clauseId,
        int sourceOrder,
        IReadOnlyList<ClauseArgumentIR> arguments,
        List<MetaInvocationIR> invocations,
        List<ClauseBindingDiagnostic> diagnostics)
    {
        if (spec.CompilerOwnedInvocation)
        {
            foreach (var argument in arguments)
            {
                var path = argument.Path.Count > 0 ? argument.Path : SplitPath(argument.CanonicalText);
                if (path.Count == 0)
                {
                    continue;
                }

                invocations.Add(new MetaInvocationIR(
                    ClauseSchema.Version,
                    clauseId with { ArgumentSubIndex = argument.Index },
                    MetaInvocationOwner.CompilerDerive,
                    spec.Stage,
                    sourceOrder,
                    path,
                    [],
                    clause.Span,
                    CompilerOwnedInvocationGrant.Create()));
            }
            return;
        }

        var syntax = clause.MetaInvocation;
        if (syntax == null || syntax.GeneratorPath.Count == 0)
        {
            diagnostics.Add(new ClauseBindingDiagnostic(clause.Span, "expand requires a resolved meta generator invocation"));
            return;
        }

        invocations.Add(new MetaInvocationIR(
            ClauseSchema.Version,
            clauseId,
            MetaInvocationOwner.UserExpand,
            spec.Stage,
            sourceOrder,
            syntax.GeneratorPath.ToArray(),
            syntax.ExplicitArguments.ToArray(),
            clause.Span,
            null));
    }

    private static void ValidateForeignContract(
        Declaration declaration,
        IReadOnlyDictionary<DeclarationClauseKind, List<DeclarationClause>> occurrences,
        List<ClauseBindingDiagnostic> diagnostics)
    {
        if (!occurrences.TryGetValue(DeclarationClauseKind.Extern, out var externClauses))
        {
            return;
        }

        var externClause = externClauses[0];
        if (!ForeignContractIR.TryCreate(externClause, out var contract, out var contractErrors))
        {
            diagnostics.AddRange(contractErrors.Select(error => new ClauseBindingDiagnostic(externClause.Span, error, "E3057")));
        }

        if (!string.Equals(contract.Abi, "c", StringComparison.Ordinal))
        {
            diagnostics.Add(new ClauseBindingDiagnostic(externClause.Span, "extern currently requires ABI 'c'", "E3057"));
        }

        var hasFfiNeed = occurrences.TryGetValue(DeclarationClauseKind.Need, out var needClauses) &&
                         needClauses.SelectMany(static clause => clause.ArgumentTokens)
                             .Any(static argument => string.Equals(argument.Trim(), "ffi", StringComparison.Ordinal));
        if (!hasFfiNeed)
        {
            diagnostics.Add(new ClauseBindingDiagnostic(
                externClause.Span,
                $"foreign declaration '{GetDeclarationName(declaration)}' must explicitly declare 'need ffi'",
                "E3056"));
        }

        if (declaration is FuncDef { Body.Count: > 0 })
        {
            diagnostics.Add(new ClauseBindingDiagnostic(
                externClause.Span,
                $"foreign declaration '{GetDeclarationName(declaration)}' cannot have an Eidos function body",
                "E3050"));
        }
    }

    internal static string CreateDeclarationIdentity(Declaration declaration)
    {
        if (declaration.GeneratedOriginChain.LastOrDefault() is { } origin)
        {
            return $"generated:{origin.StableIdentity}:{GetTarget(declaration)}:{GetDeclarationName(declaration)}";
        }

        var filePath = string.IsNullOrWhiteSpace(declaration.Span.FilePath)
            ? "<memory>"
            : declaration.Span.FilePath!.Replace('\\', '/');
        return $"decl:{filePath}:{declaration.Span.Position}:{GetTarget(declaration)}:{GetDeclarationName(declaration)}";
    }

    private static bool IsMetaGeneratorDeclaration(Declaration declaration) => declaration switch
    {
        FuncDef function => function.IsComptime,
        FuncDecl function => function.IsComptime,
        _ => false
    };

    private static bool IsValidArgument(string raw, ClauseArgumentGrammar grammar) => grammar switch
    {
        ClauseArgumentGrammar.None => raw.Length == 0,
        ClauseArgumentGrammar.String => IsQuoted(raw),
        ClauseArgumentGrammar.Identifier or ClauseArgumentGrammar.IdentifierList => IsIdentifier(raw),
        ClauseArgumentGrammar.Path or ClauseArgumentGrammar.PathList => IsPath(raw),
        ClauseArgumentGrammar.MetaInvocation => raw.Length > 0,
        ClauseArgumentGrammar.TokenIsland => raw.Length > 0,
        _ => false
    };

    private static string Canonicalize(string raw, ClauseCanonicalArgumentType type)
    {
        if (type == ClauseCanonicalArgumentType.String && IsQuoted(raw))
        {
            return raw[1..^1];
        }

        return raw;
    }

    private static bool IsPathArgument(ClauseCanonicalArgumentType type) => type is
        ClauseCanonicalArgumentType.Type or
        ClauseCanonicalArgumentType.Effect or
        ClauseCanonicalArgumentType.Trait or
        ClauseCanonicalArgumentType.Declaration or
        ClauseCanonicalArgumentType.Generator;

    private static IReadOnlyList<string> SplitPath(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var segments = new List<string>();
        var start = 0;
        var depth = 0;
        for (var index = 0; index < text.Length; index++)
        {
            depth += text[index] switch
            {
                '[' or '(' => 1,
                ']' or ')' => -1,
                _ => 0
            };
            if (text[index] != '.' || depth != 0)
            {
                continue;
            }

            segments.Add(text[start..index]);
            start = index + 1;
        }
        segments.Add(text[start..]);
        return segments.Where(static segment => segment.Length > 0).ToArray();
    }

    private static bool IsPath(string text)
    {
        var segments = SplitPath(text);
        return segments.Count > 0 && segments.All(static segment =>
        {
            var bracket = segment.IndexOf('[');
            var head = bracket < 0 ? segment : segment[..bracket];
            return IsIdentifier(head) && (bracket < 0 || segment.EndsWith(']'));
        });
    }

    private static bool IsIdentifier(string text)
    {
        if (string.IsNullOrEmpty(text) || !(text[0] == '_' || char.IsLetter(text[0])))
        {
            return false;
        }

        return text.Skip(1).All(static character => character == '_' || char.IsLetterOrDigit(character));
    }

    private static bool IsQuoted(string text) =>
        text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '\'' && text[^1] == '\''));

    private static DeclarationClauseTarget GetTarget(Declaration declaration) => declaration switch
    {
        CaseTypeDef => DeclarationClauseTarget.CaseType,
        AdtDef => DeclarationClauseTarget.Type,
        FuncDef or FuncDecl => DeclarationClauseTarget.Function,
        TraitDef => DeclarationClauseTarget.Trait,
        InstanceDecl => DeclarationClauseTarget.Instance,
        EffectDef => DeclarationClauseTarget.Effect,
        ModuleDecl => DeclarationClauseTarget.Module,
        LetDecl => DeclarationClauseTarget.Value,
        ImportDecl => DeclarationClauseTarget.Import,
        ProofDecl => DeclarationClauseTarget.Proof,
        _ => DeclarationClauseTarget.None
    };

    private static string DescribeTarget(DeclarationClauseTarget target) => target switch
    {
        DeclarationClauseTarget.Type => "type",
        DeclarationClauseTarget.Function => "function",
        DeclarationClauseTarget.Trait => "trait",
        DeclarationClauseTarget.Instance => "instance",
        DeclarationClauseTarget.Effect => "effect",
        DeclarationClauseTarget.Module => "module",
        DeclarationClauseTarget.CaseType => "case type",
        DeclarationClauseTarget.Value => "value",
        DeclarationClauseTarget.Import => "import",
        DeclarationClauseTarget.Proof => "proof",
        DeclarationClauseTarget.AssociatedType => "associated type",
        DeclarationClauseTarget.AssociatedConst => "associated constant",
        DeclarationClauseTarget.Field => "field",
        DeclarationClauseTarget.Constructor => "constructor",
        _ => "this"
    };

    private static string DescribeArgument(ClauseCanonicalArgumentType type) => type switch
    {
        ClauseCanonicalArgumentType.None => "empty",
        ClauseCanonicalArgumentType.String => "string",
        ClauseCanonicalArgumentType.Identifier => "identifier",
        ClauseCanonicalArgumentType.Abi => "ABI",
        ClauseCanonicalArgumentType.MetaInvocation => "meta invocation",
        _ => type.ToString().ToLowerInvariant()
    };

    private static string GetKeyword(DeclarationClauseKind kind) =>
        ClauseSchema.TryGet(kind, out var spec) ? spec.Keyword : kind.ToString();

    private static string GetDeclarationName(Declaration declaration) => declaration switch
    {
        AdtDef type => type.Name,
        CaseTypeDef caseType => caseType.Name,
        FuncDef function => function.Name,
        FuncDecl function => function.Name,
        TraitDef trait => trait.Name,
        EffectDef effect => effect.Name,
        LetDecl { Pattern: Ast.Patterns.VarPattern variable } => variable.Name,
        ImportDecl import => import.Alias ?? string.Join(WellKnownStrings.Separators.Path, import.ModulePath),
        ProofDecl proof => proof.Name,
        _ => declaration.GetType().Name
    };
}
