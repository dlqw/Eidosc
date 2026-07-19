using Eidosc.Ast.Declarations;
using Eidosc.Ast.Patterns;
using Eidosc.Parsing.Handwritten;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Semantic;
using Eidosc.Symbols;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class DeclarationClauseBindingTests
{
    [Fact]
    public void Generated_identity_registry_distinguishes_noop_from_payload_conflict()
    {
        var registry = new GeneratedDeclarationIdentityRegistry();

        Assert.Equal(
            GeneratedDeclarationIdentityRegistration.Added,
            registry.Register("stable-slot", "payload-a"));
        Assert.Equal(
            GeneratedDeclarationIdentityRegistration.Unchanged,
            registry.Register("stable-slot", "payload-a"));
        Assert.Equal(
            GeneratedDeclarationIdentityRegistration.Conflict,
            registry.Register("stable-slot", "payload-b"));
    }

    [Fact]
    public void Generated_identity_registry_prepares_and_commits_a_batch_atomically()
    {
        var registry = new GeneratedDeclarationIdentityRegistry();
        registry.Register("existing", "payload-a");

        Assert.False(registry.TryPrepareBatch(
            [
                new GeneratedDeclarationIdentityCandidate("new-slot", "payload-new"),
                new GeneratedDeclarationIdentityCandidate("existing", "payload-conflict")
            ],
            out _,
            out var conflictIdentity));
        Assert.Equal("existing", conflictIdentity);
        Assert.Equal(
            GeneratedDeclarationIdentityRegistration.Added,
            registry.Register("new-slot", "payload-new"));

        Assert.True(registry.TryPrepareBatch(
            [
                new GeneratedDeclarationIdentityCandidate("second", "payload-second"),
                new GeneratedDeclarationIdentityCandidate("second", "payload-second")
            ],
            out var prepared,
            out conflictIdentity));
        Assert.Empty(conflictIdentity);
        Assert.Collection(
            prepared,
            static entry => Assert.Equal(GeneratedDeclarationIdentityRegistration.Added, entry.Registration),
            static entry => Assert.Equal(GeneratedDeclarationIdentityRegistration.Unchanged, entry.Registration));
        registry.CommitBatch(prepared);
        Assert.Equal(
            GeneratedDeclarationIdentityRegistration.Unchanged,
            registry.Register("second", "payload-second"));
    }

    [Fact]
    public void Schema_entries_define_the_complete_versioned_contract()
    {
        Assert.Equal("clause-schema-v2", ClauseSchema.Version);
        Assert.NotEmpty(ClauseSchema.Entries);
        Assert.Equal(ClauseSchema.Entries.Count, ClauseSchema.Entries.Values.Select(static spec => spec.Kind).Distinct().Count());

        foreach (var (keyword, spec) in ClauseSchema.Entries)
        {
            Assert.Equal(keyword, spec.Keyword);
            Assert.NotEqual(DeclarationClauseTarget.None, spec.Targets);
            Assert.NotNull(spec.Migration);
            Assert.False(string.IsNullOrWhiteSpace(spec.Migration!.RuleId));
            Assert.True(Enum.IsDefined(spec.CanonicalArgumentType));
            Assert.True(Enum.IsDefined(spec.Stage));
            Assert.True(Enum.IsDefined(spec.SourceOrder));
            Assert.True(Enum.IsDefined(spec.Privilege));
            Assert.True(Enum.IsDefined(spec.Adapter));
        }
    }

    [Fact]
    public void Attachment_groups_bound_entries_by_language_responsibility()
    {
        var declaration = CreateFunction(
            Clause(DeclarationClauseKind.Need, "need", "ffi"),
            Clause(DeclarationClauseKind.Extern, "extern", "c"),
            Clause(DeclarationClauseKind.Transparent, "transparent"),
            Clause(DeclarationClauseKind.Compiler, "compiler", "internal"));

        var result = Bind(declaration, CompilerOwnedSourceGrant.Create([SourcePath]));
        declaration.SetBoundClauses(result.Clauses, result.MetaInvocations);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(
            [DeclarationClauseKind.Need],
            declaration.Attachment.GetAdapterEntries(DeclarationAttachmentAdapterKind.SignatureComponent)
                .Select(static clause => clause.Kind));
        Assert.Equal(
            [DeclarationClauseKind.Transparent],
            declaration.Attachment.GetAdapterEntries(DeclarationAttachmentAdapterKind.TypedTag)
                .Select(static clause => clause.Kind));
        Assert.Equal(
            [DeclarationClauseKind.Extern],
            declaration.Attachment.GetAdapterEntries(DeclarationAttachmentAdapterKind.ForeignContract)
                .Select(static clause => clause.Kind));
        Assert.Equal(
            [DeclarationClauseKind.Compiler],
            declaration.Attachment.GetAdapterEntries(DeclarationAttachmentAdapterKind.CompilerDirective)
                .Select(static clause => clause.Kind));
        Assert.Equal("c", Assert.IsType<ForeignContractIR>(declaration.Attachment.ForeignContract).Abi);
        Assert.True(Assert.IsType<CompilerDirectiveIR>(declaration.Attachment.CompilerDirective).IsInternal);
        Assert.Empty(declaration.Attachment.GetAdapterEntries(DeclarationAttachmentAdapterKind.RemovedSurface));
    }

    [Fact]
    public void Derive_list_uses_one_clause_identity_with_stable_argument_sub_indices()
    {
        var declaration = CreateType(
            Clause(DeclarationClauseKind.Derive, "derive", "Eq", "Show"));

        var result = Bind(declaration);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.MetaInvocations.Count);
        Assert.All(result.MetaInvocations, invocation =>
        {
            Assert.Equal(MetaInvocationOwner.CompilerDerive, invocation.Owner);
            Assert.Equal(0, invocation.OccurrenceId.ClauseIndex);
            Assert.NotNull(invocation.CompilerGrant);
        });
        Assert.Equal([0, 1], result.MetaInvocations.Select(static invocation => invocation.OccurrenceId.ArgumentSubIndex));
        Assert.Equal(result.MetaInvocations[0].OccurrenceId.DeclarationIdentity, result.MetaInvocations[1].OccurrenceId.DeclarationIdentity);
    }

    [Fact]
    public void Interleaved_derive_and_expand_clauses_preserve_source_order()
    {
        var expand = Clause(DeclarationClauseKind.Expand, "expand", "inspect");
        var syntax = new MetaInvocationSyntax();
        syntax.SetGeneratorPath(["inspect"]);
        syntax.SetSpan(expand.Span);
        expand.SetMetaInvocation(syntax);
        var declaration = CreateType(
            Clause(DeclarationClauseKind.Derive, "derive", "Eq"),
            expand,
            Clause(DeclarationClauseKind.Derive, "derive", "Show"));

        var result = Bind(declaration);

        Assert.Empty(result.Diagnostics);
        Assert.Equal([0, 1, 2], result.MetaInvocations.Select(static invocation => invocation.SourceOrder));
        Assert.Equal(
            [MetaInvocationOwner.CompilerDerive, MetaInvocationOwner.UserExpand, MetaInvocationOwner.CompilerDerive],
            result.MetaInvocations.Select(static invocation => invocation.Owner));
        Assert.Equal(["Eq", "inspect", "Show"], result.MetaInvocations.Select(static invocation => invocation.GeneratorPath.Single()));
    }

    [Fact]
    public void Extern_c_requires_exact_ffi_need_and_a_bodyless_function()
    {
        var missingFfi = CreateFunction(
            Clause(DeclarationClauseKind.Need, "need", "io"),
            Clause(DeclarationClauseKind.Extern, "extern", "c"));
        var bodyful = CreateFunctionWithBody(
            Clause(DeclarationClauseKind.Need, "need", "ffi"),
            Clause(DeclarationClauseKind.Extern, "extern", "c"));

        var missingFfiResult = Bind(missingFfi);
        var bodyfulResult = Bind(bodyful);

        Assert.Contains(missingFfiResult.Diagnostics, diagnostic => diagnostic.Message.Contains("must explicitly declare 'need ffi'", StringComparison.Ordinal));
        Assert.Contains(bodyfulResult.Diagnostics, diagnostic => diagnostic.Message.Contains("cannot have an Eidos function body", StringComparison.Ordinal));
    }

    [Fact]
    public void Foreign_contract_rejects_unknown_fields_and_conflicts_with_intrinsic()
    {
        var malformed = CreateFunction(
            Clause(DeclarationClauseKind.Need, "need", "ffi"),
            Clause(DeclarationClauseKind.Extern, "extern", "c", "unknown: \"native\""));
        var conflicting = CreateFunction(
            Clause(DeclarationClauseKind.Need, "need", "ffi"),
            Clause(DeclarationClauseKind.Extern, "extern", "c"),
            Clause(DeclarationClauseKind.Compiler, "compiler", "intrinsic: \"llvm.foo\""));

        var malformedResult = Bind(malformed);
        var conflictResult = Bind(conflicting, CompilerOwnedSourceGrant.Create([SourcePath]));

        Assert.Contains(malformedResult.Diagnostics, diagnostic => diagnostic.Message.Contains("unknown extern field", StringComparison.Ordinal));
        Assert.Contains(conflictResult.Diagnostics, diagnostic => diagnostic.Message.Contains("conflicts with extern", StringComparison.Ordinal));
    }

    [Fact]
    public void Ordering_clauses_are_restricted_to_comptime_meta_generators()
    {
        var ordinary = CreateFunction(Clause(DeclarationClauseKind.Before, "before", "normalize"));
        var generator = CreateFunction(Clause(DeclarationClauseKind.Before, "before", "normalize"));
        generator.SetComptime(true);

        var ordinaryResult = Bind(ordinary);
        var generatorResult = Bind(generator);

        Assert.Contains(ordinaryResult.Diagnostics, diagnostic => diagnostic.Message.Contains("only valid on comptime meta generator", StringComparison.Ordinal));
        Assert.DoesNotContain(generatorResult.Diagnostics, diagnostic => diagnostic.Message.Contains("only valid on comptime meta generator", StringComparison.Ordinal));
    }

    [Fact]
    public void Compiler_private_privilege_is_an_exact_unforgeable_source_grant()
    {
        var ordinary = CreateType(Clause(DeclarationClauseKind.Compiler, "compiler", "internal"));
        var pathSpoof = CreateType(
            ClauseAt(
                Path.Combine(Path.GetTempPath(), "Stdlib", "Precompiled", "std", "spoof.eidos"),
                DeclarationClauseKind.Compiler,
                "compiler",
                "internal"));
        var granted = CreateType(Clause(DeclarationClauseKind.Compiler, "compiler", "internal"));

        var ordinaryResult = Bind(ordinary);
        var spoofResult = Bind(pathSpoof);
        var grantedResult = Bind(granted, CompilerOwnedSourceGrant.Create([SourcePath]));
        var generatedResult = Bind(granted, CompilerOwnedSourceGrant.None);

        Assert.Contains(ordinaryResult.Diagnostics, diagnostic => diagnostic.Message.Contains("reserved for toolchain-owned source", StringComparison.Ordinal));
        Assert.Contains(spoofResult.Diagnostics, diagnostic => diagnostic.Message.Contains("reserved for toolchain-owned source", StringComparison.Ordinal));
        Assert.DoesNotContain(grantedResult.Diagnostics, diagnostic => diagnostic.Message.Contains("reserved for toolchain-owned source", StringComparison.Ordinal));
        Assert.Contains(generatedResult.Diagnostics, diagnostic => diagnostic.Message.Contains("reserved for toolchain-owned source", StringComparison.Ordinal));
        Assert.True(grantedResult.Clauses.Single().HasCompilerOwnedSourceGrant);
    }

    [Fact]
    public void Parser_preserves_where_and_case_clauses_as_typed_lossless_ir()
    {
        const string source = """
Tree[T] :: type
    derive Eq
    where T: Eq
{
    Leaf[T] :: type
        case Tree[T]
        where T: Eq
    {}
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = SourcePath,
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Parser,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == global::Eidosc.Diagnostic.DiagnosticLevel.Error);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var tree = Assert.IsType<AdtDef>(Assert.Single(module.Declarations));
        Assert.Equal([DeclarationClauseKind.Derive, DeclarationClauseKind.Where], tree.Clauses.Select(static clause => clause.ClauseKind));
        Assert.Equal("T:Eq", tree.Clauses[1].ArgumentTokens.Single());

        var leaf = Assert.Single(tree.Cases);
        Assert.Equal([DeclarationClauseKind.Case, DeclarationClauseKind.Where], leaf.Clauses.Select(static clause => clause.ClauseKind));
        Assert.Equal("Tree[T]", leaf.Clauses[0].ArgumentTokens.Single());
        Assert.Equal("T:Eq", leaf.Clauses[1].ArgumentTokens.Single());

        var bindingDiagnostics = DeclarationClauseBinder.BindTree(module, EidosLanguageVersions.Current);
        Assert.Empty(bindingDiagnostics);
        Assert.Equal([DeclarationClauseKind.Case, DeclarationClauseKind.Where], leaf.BoundClauses.Select(static clause => clause.Kind));
    }

    [Fact]
    public void Parser_lowers_typed_tag_groups_into_attachment_clauses()
    {
        const string source = """
@[repr(c), derive(Eq, Show), expand(trace)]
User :: type {}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = SourcePath,
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Parser,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == global::Eidosc.Diagnostic.DiagnosticLevel.Error);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var user = Assert.IsType<AdtDef>(Assert.Single(module.Declarations));
        Assert.Empty(user.Attributes);
        Assert.Equal(
            [DeclarationClauseKind.Repr, DeclarationClauseKind.Derive, DeclarationClauseKind.Expand],
            user.Clauses.Select(static clause => clause.ClauseKind));
        Assert.Equal(["Eq", "Show"], user.Clauses[1].ArgumentTokens);
        Assert.Equal(["trace"], Assert.IsType<MetaInvocationSyntax>(user.Clauses[2].MetaInvocation).GeneratorPath);
    }

    [Fact]
    public void Parser_rejects_non_tag_adapters_inside_typed_tag_groups()
    {
        const string source = """
@[extern(c)]
malloc :: Int -> RawPtr need ffi;
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = SourcePath,
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Parser,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("is not a typed declaration tag", StringComparison.Ordinal));
    }

    [Fact]
    public void Parser_rejects_flat_foreign_contract_syntax()
    {
        const string source = "malloc :: Int -> RawPtr need ffi extern c link_name \"malloc\";";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = SourcePath,
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Parser,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("extern uses the structured form", StringComparison.Ordinal));
    }

    [Fact]
    public void Parser_rejects_operator_function_clauses_and_accepts_symbolic_declarations()
    {
        const string source = """
(|+|) :: Int -> Int -> Int { left => right => left + right }
legacy :: Int -> Int -> Int operator infixl 4 { left => right => left + right }
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = SourcePath,
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Parser,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        var module = Assert.IsType<ModuleDecl>(result.Ast);
        Assert.Contains(module.Declarations.OfType<FuncDef>(), static function => function.Name == "|+|");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Level == global::Eidosc.Diagnostic.DiagnosticLevel.Error);
    }

    [Fact]
    public void Parser_rejects_flat_compiler_private_directives()
    {
        const string source = "legacy :: Unit -> Unit internal intrinsic \"unit\";";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = SourcePath,
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Parser,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("structured compiler(...) directive", StringComparison.Ordinal));
    }

    [Fact]
    public void Compiler_protocol_registry_classifies_resolved_type_shapes_without_name_matching()
    {
        const string source = """
syntax_pass :: comptime meta.Syntax[meta.Item] -> meta.Syntax[meta.Item] { value => value }
derive_pass :: comptime meta.Type -> meta.Items { _ => [] }
body_pass :: comptime meta.Function -> meta.Function { value => value }
analyze_pass :: comptime meta.Package -> Seq[meta.Diagnostic] { _ => [] }
extend_items :: comptime meta.Package -> meta.Items { _ => [] }
extend_modules :: comptime meta.Package -> meta.Modules { _ => [] }
build_pass :: comptime build.Inputs -> build.Graph { _ => build.graph(build.emit(build.session()), [], []) }
pure_pass :: comptime Int -> Bool { _ => true }
runtime_pass :: Int -> Bool { _ => true }
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = SourcePath,
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Namer,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == global::Eidosc.Diagnostic.DiagnosticLevel.Error);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        AssertProtocol("syntax_pass", CompilerMetaProtocolKind.SyntaxExpansion);
        AssertProtocol("derive_pass", CompilerMetaProtocolKind.Derive);
        AssertProtocol("body_pass", CompilerMetaProtocolKind.BodyTransform);
        AssertProtocol("analyze_pass", CompilerMetaProtocolKind.Analyzer);
        AssertProtocol("extend_items", CompilerMetaProtocolKind.ExtensionItems);
        AssertProtocol("extend_modules", CompilerMetaProtocolKind.ExtensionModules);
        AssertProtocol("build_pass", CompilerMetaProtocolKind.BuildHost);
        AssertProtocol("pure_pass", CompilerMetaProtocolKind.PureComptime);
        var runtime = Assert.Single(module.Declarations.OfType<FuncDef>(), static function => function.Name == "runtime_pass");
        Assert.False(CompilerMetaProtocolRegistry.TryClassify(runtime, 0, symbolTable, out _, out _));

        const string removedSurface = "legacy :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation { _ => meta.keep() }";
        var removed = new CompilationPipeline(removedSurface, new CompilationOptions
        {
            InputFile = SourcePath,
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();
        Assert.Contains(removed.Diagnostics, static diagnostic =>
            diagnostic.Level == global::Eidosc.Diagnostic.DiagnosticLevel.Error &&
            (diagnostic.Message.Contains("Target", StringComparison.Ordinal) ||
             diagnostic.Message.Contains("Transformation", StringComparison.Ordinal)));

        void AssertProtocol(string name, CompilerMetaProtocolKind expected)
        {
            var function = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == name);
            Assert.True(CompilerMetaProtocolRegistry.TryClassify(function, 0, symbolTable, out var protocol, out var reason), reason);
            Assert.Equal(expected, protocol.Kind);
        }
    }

    [Fact]
    public void User_derive_uses_meta_type_to_items_protocol_without_target_surface()
    {
        const string source = """
derive_empty :: comptime meta.Type -> meta.Items { _ => [] }

Subject :: type expand derive_empty {
    value :: Int
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = SourcePath,
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.DoesNotContain(result.Diagnostics, static diagnostic =>
            diagnostic.Message.Contains("Target", StringComparison.Ordinal) ||
            diagnostic.Message.Contains("Transformation", StringComparison.Ordinal));
    }

    [Fact]
    public void Syntax_expand_uses_same_category_meta_syntax_protocol()
    {
        const string source = """
identity_expr :: comptime meta.Syntax[meta.Expr] -> meta.Syntax[meta.Expr] { _ => quote expr { 1 } }

main :: Unit -> Int {
    _ => expand identity_expr()
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = SourcePath,
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Body_expand_uses_meta_function_protocol_and_preserves_the_contract()
    {
        const string source = """
identity_body :: comptime meta.Function -> meta.Function { value => value }

work :: Int -> Int expand identity_body {
    value => value
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = SourcePath,
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Value_clause_zone_precedes_the_initializer_and_reaches_the_unified_scheduler()
    {
        const string source = """
report :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    input => meta.report([
        meta.diagnostic("warning", meta.span_of(input), "value expanded")
    ])
}

ANSWER :: comptime
    expand report
= 42;
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = SourcePath,
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == global::Eidosc.Diagnostic.DiagnosticLevel.Error);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "W3611" && diagnostic.Message == "value expanded");
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var value = Assert.IsType<LetDecl>(Assert.Single(module.Declarations, static declaration => declaration is LetDecl));
        Assert.True(value.IsComptime);
        Assert.Equal(DeclarationClauseKind.Expand, Assert.Single(value.BoundClauses).Kind);
    }

    [Fact]
    public void Generic_where_clause_before_the_binding_token_is_not_accepted_as_a_compatibility_form()
    {
        const string source = "Box[T] where T: Eq :: type {};";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = SourcePath,
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Parser,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == global::Eidosc.Diagnostic.DiagnosticLevel.Error);
    }

    [Fact]
    public void Block_declaration_rejects_a_post_body_clause()
    {
        const string source = "sample :: module {} derive Eq";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = SourcePath,
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Parser,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("declaration clauses must appear before the body", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(DeclarationClauseKind.Repr, "repr", "\"c\"")]
    [InlineData(DeclarationClauseKind.ProofUnfold, "proof_unfold", "1invalid")]
    public void Clause_argument_grammar_rejects_wrong_token_categories(
        DeclarationClauseKind kind,
        string keyword,
        string argument)
    {
        Declaration declaration = kind == DeclarationClauseKind.Repr
            ? CreateType(Clause(kind, keyword, argument))
            : CreateFunction(Clause(kind, keyword, argument));

        var result = Bind(declaration);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("has an invalid", StringComparison.Ordinal));
    }

    private const string SourcePath = "clause-contract.eidos";

    private static DeclarationClauseBindingResult Bind(
        Declaration declaration,
        CompilerOwnedSourceGrant? grant = null) =>
        DeclarationClauseBinder.Bind(declaration, EidosLanguageVersions.Current, grant);

    private static AdtDef CreateType(params DeclarationClause[] clauses)
    {
        var declaration = new AdtDef();
        declaration.SetName("Contract");
        declaration.SetSpan(Span(SourcePath));
        declaration.SetClauses([.. clauses]);
        return declaration;
    }

    private static FuncDecl CreateFunction(params DeclarationClause[] clauses)
    {
        var declaration = new FuncDecl();
        declaration.SetName("contract");
        declaration.SetSpan(Span(SourcePath));
        declaration.SetClauses([.. clauses]);
        return declaration;
    }

    private static FuncDef CreateFunctionWithBody(params DeclarationClause[] clauses)
    {
        var declaration = new FuncDef();
        declaration.SetName("contract");
        declaration.SetSpan(Span(SourcePath));
        declaration.SetBody([new PatternBranch()]);
        declaration.SetClauses([.. clauses]);
        return declaration;
    }

    private static DeclarationClause Clause(
        DeclarationClauseKind kind,
        string keyword,
        params string[] arguments) =>
        ClauseAt(SourcePath, kind, keyword, arguments);

    private static DeclarationClause ClauseAt(
        string sourcePath,
        DeclarationClauseKind kind,
        string keyword,
        params string[] arguments)
    {
        var clause = new DeclarationClause();
        clause.SetKind(kind, keyword);
        clause.SetSpan(Span(sourcePath));
        foreach (var argument in arguments)
        {
            clause.AddArgument(argument);
        }
        return clause;
    }

    private static SourceSpan Span(string sourcePath) =>
        new(new SourceLocation(0, 0, 0, sourcePath), 1);
}
