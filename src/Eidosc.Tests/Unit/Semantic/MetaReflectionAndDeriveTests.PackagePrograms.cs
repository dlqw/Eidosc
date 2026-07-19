using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;

namespace Eidosc.Tests.Unit.Semantic;

public sealed partial class MetaReflectionAndDeriveTests
{
    [Fact]
    public void Package_extension_consumes_only_declared_resources_and_emits_a_typed_module()
    {
        const string source = """
generate_routes :: comptime meta.Query[meta.ScopeKind.Package] -> meta.Transformation {
    query => {
        resources := meta.resources_of(query);
        schema := meta.resource_content_of(resources[0]);
        meta.add_module(query, quote item {
            GeneratedRoutes :: module {
                SchemaText :: comptime $(schema);
            }
        })
    }
}

answer :: Int = 42;
""";
        var meta = new EidosMetaConfiguration
        {
            Extensions =
            [
                new EidosMetaExtensionConfiguration
                {
                    Name = "routes",
                    Entry = "generate_routes",
                    Stage = "semantic",
                    Scope = "package",
                    Inputs = ["schemas/routes.json"],
                    Capabilities = ["read-declared-resources", "emit-modules"],
                    Resources =
                    [
                        new EidosMetaResourceConfiguration
                        {
                            DeclaredInput = "schemas/routes.json",
                            RelativePath = "schemas/routes.json",
                            Exists = true,
                            Content = "{\"route\":\"/health\"}",
                            ContentHash = "resource-hash"
                        }
                    ]
                }
            ]
        };

        var result = Compile("meta_package_extension.eidos", source, options =>
        {
            options.MetaConfiguration = meta;
            options.TraceComptime = true;
        });

        Assert.True(result.Success, FormatDiagnostics(result));
        var root = Assert.IsType<ModuleDecl>(result.Ast);
        var generated = Assert.Single(
            root.Declarations.OfType<ModuleDecl>(),
            static module => module.Path.LastOrDefault() == "GeneratedRoutes");
        Assert.NotEmpty(generated.GeneratedOriginChain);
        Assert.Equal("package-extension:routes", generated.GeneratedOriginChain[^1].ClauseOccurrenceIdentity);
        var schema = Assert.Single(generated.Declarations.OfType<LetDecl>());
        Assert.True(schema.SymbolId.IsValid);
        Assert.Contains("/health", Assert.IsType<LiteralExpr>(schema.Value).RawText, StringComparison.Ordinal);
        Assert.Contains(result.ComptimeTrace, static entry =>
            entry.Kind == "query-cache" &&
            entry.Operation == "meta.resources_of" &&
            entry.Outcome == "cache-miss");
    }

    [Fact]
    public void Package_extension_add_items_targets_a_typed_current_package_module()
    {
        const string source = """
generate_item :: comptime meta.Query[meta.ScopeKind.Package] -> meta.Transformation {
    query => {
        package := meta.package_of(query);
        target_module := meta.modules_of(package)[0];
        name := meta.identifier("generated_answer", meta.IdentifierCategory.Function);
        meta.add_items(query, target_module, [quote item {
            $(name) :: Unit -> Int { _ => 42 }
        }])
    }
}

answer :: Int = 1;
""";
        var meta = new EidosMetaConfiguration
        {
            Extensions =
            [
                new EidosMetaExtensionConfiguration
                {
                    Name = "items",
                    Entry = "generate_item",
                    Stage = "semantic",
                    Scope = "package",
                    Capabilities = ["read-semantics", "emit-items"]
                }
            ]
        };

        var result = Compile("meta_package_add_items.eidos", source, options =>
        {
            options.MetaConfiguration = meta;
        });

        Assert.True(result.Success, FormatDiagnostics(result));
        var generated = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<FuncDef>(),
            static function => function.Name == "generated_answer");
        Assert.True(generated.SymbolId.IsValid);
        Assert.NotEmpty(generated.GeneratedOriginChain);
        Assert.Equal("package-extension:items", generated.GeneratedOriginChain[^1].ClauseOccurrenceIdentity);
    }

    [Fact]
    public void Package_extension_add_items_requires_emit_items_without_partial_commit()
    {
        const string source = """
generate_item :: comptime meta.Query[meta.ScopeKind.Package] -> meta.Transformation {
    query => {
        package := meta.package_of(query);
        target_module := meta.modules_of(package)[0];
        name := meta.identifier("must_not_exist", meta.IdentifierCategory.Function);
        meta.add_items(query, target_module, [quote item {
            $(name) :: Unit -> Int { _ => 42 }
        }])
    }
}

answer :: Int = 1;
""";
        var meta = new EidosMetaConfiguration
        {
            Extensions =
            [
                new EidosMetaExtensionConfiguration
                {
                    Name = "items",
                    Entry = "generate_item",
                    Stage = "semantic",
                    Scope = "package",
                    Capabilities = ["read-semantics"]
                }
            ]
        };

        var result = Compile("meta_package_add_items_capability.eidos", source, options =>
        {
            options.MetaConfiguration = meta;
        });

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Message.Contains("emit-items", StringComparison.Ordinal));
        Assert.DoesNotContain(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<FuncDef>(),
            static function => function.Name == "must_not_exist");
    }

    [Fact]
    public void Package_extension_add_items_rejects_batch_collisions_atomically()
    {
        const string source = """
generate_items :: comptime meta.Query[meta.ScopeKind.Package] -> meta.Transformation {
    query => {
        package := meta.package_of(query);
        target_module := meta.modules_of(package)[0];
        first_name := meta.identifier("first_generated", meta.IdentifierCategory.Function);
        duplicate_a := meta.identifier("duplicate_generated", meta.IdentifierCategory.Function);
        duplicate_b := meta.identifier("duplicate_generated", meta.IdentifierCategory.Function);
        meta.add_items(query, target_module, [
            quote item { $(first_name) :: Unit -> Int { _ => 1 } },
            quote item { $(duplicate_a) :: Unit -> Int { _ => 2 } },
            quote item { $(duplicate_b) :: Unit -> Int { _ => 3 } }
        ])
    }
}

answer :: Int = 1;
""";
        var meta = new EidosMetaConfiguration
        {
            Extensions =
            [
                new EidosMetaExtensionConfiguration
                {
                    Name = "items",
                    Entry = "generate_items",
                    Stage = "semantic",
                    Scope = "package",
                    Capabilities = ["read-semantics", "emit-items"]
                }
            ]
        };

        var result = Compile("meta_package_add_items_atomic.eidos", source, options =>
        {
            options.MetaConfiguration = meta;
        });

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Message.Contains("duplicate_generated", StringComparison.Ordinal));
        var functions = Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<FuncDef>().ToArray();
        Assert.DoesNotContain(functions, static function => function.Name == "first_generated");
        Assert.DoesNotContain(functions, static function => function.Name == "duplicate_generated");
    }

    [Fact]
    public void Package_extension_live_state_reruns_only_when_its_declared_resource_changes()
    {
        const string source = """
generate_routes :: comptime meta.Query[meta.ScopeKind.Package] -> meta.Transformation {
    query => {
        resource := meta.resources_of(query)[0];
        schema := meta.resource_content_of(resource);
        meta.add_module(query, quote item {
            GeneratedRoutesCache :: module {
                SchemaText :: comptime $(schema);
            }
        })
    }
}

answer :: Int = 42;
""";

        var first = Compile("meta_package_extension_cache.eidos", source, options =>
        {
            options.MetaConfiguration = CreateResourceExtensionConfiguration(
                "{\"route\":\"/health\"}",
                "resource-health");
            options.EnableLiveStateCache = true;
            options.EnableDetailedProfiling = true;
            options.TraceComptime = true;
        });
        var unchanged = Compile("meta_package_extension_cache.eidos", source, options =>
        {
            options.MetaConfiguration = CreateResourceExtensionConfiguration(
                "{\"route\":\"/health\"}",
                "resource-health");
            options.EnableLiveStateCache = true;
            options.EnableDetailedProfiling = true;
            options.TraceComptime = true;
        });
        var changed = Compile("meta_package_extension_cache.eidos", source, options =>
        {
            options.MetaConfiguration = CreateResourceExtensionConfiguration(
                "{\"route\":\"/ready\"}",
                "resource-ready");
            options.EnableLiveStateCache = true;
            options.EnableDetailedProfiling = true;
            options.TraceComptime = true;
        });

        Assert.True(first.Success, FormatDiagnostics(first));
        Assert.True(unchanged.Success, FormatDiagnostics(unchanged));
        Assert.True(changed.Success, FormatDiagnostics(changed));
        Assert.Equal(1, unchanged.ProfilingCounters.GetValueOrDefault("Build.liveState.Types.hits"));
        Assert.Equal(0, changed.ProfilingCounters.GetValueOrDefault("Build.liveState.Types.hits"));
        Assert.Contains(unchanged.ComptimeTrace, static entry =>
            entry.Kind == "cache" &&
            entry.Operation == "live-state:Types" &&
            entry.Outcome == "hit");
        Assert.Contains(changed.ComptimeTrace, static entry =>
            entry.Kind == "query-cache" &&
            entry.Operation == "meta.resources_of" &&
            entry.Outcome == "cache-miss");

        Assert.Contains("/health", GetGeneratedSchemaText(unchanged), StringComparison.Ordinal);
        Assert.Contains("/ready", GetGeneratedSchemaText(changed), StringComparison.Ordinal);
    }

    [Fact]
    public void Package_syntax_extension_runs_before_semantic_type_collection()
    {
        const string source = """
syntax_extension :: comptime meta.Query[meta.ScopeKind.Package] -> meta.Transformation {
    query => {
        module_name := meta.identifier("GeneratedSyntaxStage", meta.IdentifierCategory.Module);
        function_name := meta.identifier("value", meta.IdentifierCategory.Function);
        meta.add_module(query, quote item {
            $(module_name) :: module {
                $(function_name) :: Unit -> Int { _ => 7 }
            }
        })
    }
}

marker :: Unit -> Int { _ => GeneratedSyntaxStage.value() }
""";

        var result = Compile("meta_package_extension_syntax_stage.eidos", source, options =>
        {
            options.StopAtPhase = CompilationPhase.Types;
            options.MetaConfiguration = new EidosMetaConfiguration
            {
                Extensions =
                [
                    new EidosMetaExtensionConfiguration
                    {
                        Name = "syntax-stage",
                        Entry = "syntax_extension",
                        Stage = "syntax",
                        Capabilities = ["emit-modules"]
                    }
                ]
            };
        });

        Assert.True(result.Success, FormatDiagnostics(result));
        var generated = Assert.Single(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<ModuleDecl>(),
            static module => module.Path.LastOrDefault() == "GeneratedSyntaxStage");
        Assert.True(generated.SymbolId.IsValid);
        Assert.True(Assert.Single(generated.Declarations.OfType<FuncDef>()).SymbolId.IsValid);
    }

    [Fact]
    public void Package_body_extension_runs_at_the_declared_stage()
    {
        const string source = """
body_extension :: comptime meta.Query[meta.ScopeKind.Package] -> meta.Transformation {
    _ => meta.report([
        meta.diagnostic("warning", meta.span_of(meta.declaration_of(marker)), "body-stage-extension")
    ])
}

marker :: Unit -> Int { _ => 42 }
""";

        var result = Compile("meta_package_extension_body_stage.eidos", source, options =>
        {
            options.MetaConfiguration = new EidosMetaConfiguration
            {
                Extensions =
                [
                    new EidosMetaExtensionConfiguration
                    {
                        Name = "body-stage",
                        Entry = "body_extension",
                        Stage = "body",
                        Capabilities = ["emit-diagnostics"]
                    }
                ]
            };
        });

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "W3632" && diagnostic.Message == "body-stage-extension");
    }

    [Fact]
    public void Package_layout_extension_runs_at_the_declared_stage()
    {
        const string source = """
layout_extension :: comptime meta.Query[meta.ScopeKind.Package] -> meta.Transformation {
    _ => meta.report([
        meta.diagnostic("warning", meta.span_of(meta.declaration_of(marker)), "layout-stage-extension")
    ])
}

marker :: Unit -> Int { _ => 42 }
""";

        var result = Compile("meta_package_extension_layout_stage.eidos", source, options =>
        {
            options.StopAtPhase = CompilationPhase.Mir;
            options.MetaConfiguration = new EidosMetaConfiguration
            {
                Extensions =
                [
                    new EidosMetaExtensionConfiguration
                    {
                        Name = "layout-stage",
                        Entry = "layout_extension",
                        Stage = "layout",
                        Capabilities = ["read-layout", "emit-diagnostics"]
                    }
                ]
            };
        });

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "W3632" && diagnostic.Message == "layout-stage-extension");
    }

    [Fact]
    public void Package_extension_rejects_structural_output_after_the_semantic_stage_atomically()
    {
        const string source = """
late_extension :: comptime meta.Query[meta.ScopeKind.Package] -> meta.Transformation {
    query => meta.add_module(query, quote item { ForbiddenLateModule :: module {} })
}

marker :: Unit -> Int { _ => 42 }
""";

        var result = Compile("meta_package_extension_late_output.eidos", source, options =>
        {
            options.MetaConfiguration = new EidosMetaConfiguration
            {
                Extensions =
                [
                    new EidosMetaExtensionConfiguration
                    {
                        Name = "late",
                        Entry = "late_extension",
                        Stage = "body",
                        Capabilities = ["emit-modules"]
                    }
                ]
            };
        });

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3631" &&
            diagnostic.Message.Contains("not permitted after the Semantic stage", StringComparison.Ordinal));
        Assert.DoesNotContain(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<ModuleDecl>(),
            static module => module.Path.LastOrDefault() == "ForbiddenLateModule");
    }

    [Fact]
    public void Package_extension_add_items_rejects_structural_output_after_the_semantic_stage_atomically()
    {
        const string source = """
late_extension :: comptime meta.Query[meta.ScopeKind.Package] -> meta.Transformation {
    query => {
        package := meta.package_of(query);
        target_module := meta.modules_of(package)[0];
        name := meta.identifier("forbidden_late_item", meta.IdentifierCategory.Function);
        meta.add_items(query, target_module, [quote item {
            $(name) :: Unit -> Int { _ => 42 }
        }])
    }
}

marker :: Unit -> Int { _ => 42 }
""";

        var result = Compile("meta_package_add_items_late_output.eidos", source, options =>
        {
            options.MetaConfiguration = new EidosMetaConfiguration
            {
                Extensions =
                [
                    new EidosMetaExtensionConfiguration
                    {
                        Name = "late-items",
                        Entry = "late_extension",
                        Stage = "body",
                        Capabilities = ["read-semantics", "emit-items"]
                    }
                ]
            };
        });

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3631" &&
            diagnostic.Message.Contains("not permitted after the Semantic stage", StringComparison.Ordinal));
        Assert.DoesNotContain(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<FuncDef>(),
            static function => function.Name == "forbidden_late_item");
    }

    [Fact]
    public void Package_analyzer_queries_reference_graph_and_publishes_an_automatic_fix()
    {
        const string source = """
target :: Unit -> Int { _ => 41 }
caller :: Unit -> Int { _ => target() }

enforce_calls :: comptime meta.Query[meta.ScopeKind.Package] -> Seq[meta.Diagnostic] {
    query => {
        references := meta.references_to(meta.declaration_of(target), query);
        span := meta.span_of(references[0]);
        [meta.diagnostic_with_fix(
            "warning",
            span,
            "replace direct target call",
            meta.fix(span, "replacement"))]
    }

}
""";

        var result = Compile("meta_package_analyzer.eidos", source, options =>
        {
            options.MetaConfiguration = new EidosMetaConfiguration
            {
                Checks = ["enforce_calls"]
            };
            options.TraceComptime = true;
        });

        Assert.True(result.Success, FormatDiagnostics(result));
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic => diagnostic.Code == "W3632");
        Assert.Equal("replace direct target call", diagnostic.Message);
        var suggestion = Assert.Single(diagnostic.Suggestions);
        Assert.Equal("replacement", suggestion.Replacement);
        Assert.NotNull(suggestion.Span);
        Assert.Contains(result.ComptimeTrace, static entry =>
            entry.Kind == "query-cache" &&
            entry.Operation == "meta.references_to");
    }

    [Fact]
    public void Package_analyzer_accepts_the_compiler_managed_package_protocol()
    {
        const string source = """
check_package :: comptime meta.Package -> Seq[meta.Diagnostic] { _ => [] }
""";

        var result = Compile("meta_package_analyzer_protocol.eidos", source, options =>
        {
            options.MetaConfiguration = new EidosMetaConfiguration
            {
                Checks = ["check_package"]
            };
        });

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void Package_extension_accepts_the_compiler_managed_items_protocol()
    {
        const string source = """
emit_items :: comptime meta.Package -> meta.Items { _ => [] }
""";

        var result = Compile("meta_package_extension_protocol.eidos", source, options =>
        {
            options.MetaConfiguration = new EidosMetaConfiguration
            {
                Extensions =
                [
                    new EidosMetaExtensionConfiguration
                    {
                        Name = "items",
                        Entry = "emit_items",
                        Stage = "semantic",
                        Scope = "package"
                    }
                ]
            };
        });

        Assert.True(result.Success, FormatDiagnostics(result));
    }

    [Fact]
    public void Package_extension_capabilities_reject_undeclared_resource_and_module_access()
    {
        const string source = """
generate_routes :: comptime meta.Query[meta.ScopeKind.Package] -> meta.Transformation {
    query => meta.add_module(query, quote item { Generated :: module {} })
}
""";

        var result = Compile("meta_package_extension_capability.eidos", source, options =>
        {
            options.MetaConfiguration = new EidosMetaConfiguration
            {
                Extensions =
                [
                    new EidosMetaExtensionConfiguration
                    {
                        Name = "routes",
                        Entry = "generate_routes",
                        Capabilities = []
                    }
                ]
            };
        });

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E3631" &&
            diagnostic.Message.Contains("emit-modules", StringComparison.Ordinal));
        Assert.DoesNotContain(
            Assert.IsType<ModuleDecl>(result.Ast).Declarations.OfType<ModuleDecl>(),
            static module => module.Path.LastOrDefault() == "Generated");
    }

    private static EidosMetaConfiguration CreateResourceExtensionConfiguration(string content, string contentHash) =>
        new()
        {
            Extensions =
            [
                new EidosMetaExtensionConfiguration
                {
                    Name = "routes-cache",
                    Entry = "generate_routes",
                    Stage = "semantic",
                    Scope = "package",
                    Inputs = ["schemas/routes.json"],
                    Capabilities = ["read-declared-resources", "emit-modules"],
                    Resources =
                    [
                        new EidosMetaResourceConfiguration
                        {
                            DeclaredInput = "schemas/routes.json",
                            RelativePath = "schemas/routes.json",
                            Exists = true,
                            Content = content,
                            ContentHash = contentHash
                        }
                    ]
                }
            ]
        };

    private static string GetGeneratedSchemaText(CompilationResult result)
    {
        var root = Assert.IsType<ModuleDecl>(result.Ast);
        var generated = Assert.Single(
            root.Declarations.OfType<ModuleDecl>(),
            static module => module.Path.LastOrDefault() == "GeneratedRoutesCache");
        var declaration = Assert.Single(generated.Declarations.OfType<LetDecl>());
        Assert.True(declaration.SymbolId.IsValid);
        return Assert.IsType<LiteralExpr>(declaration.Value).RawText;
    }
}
