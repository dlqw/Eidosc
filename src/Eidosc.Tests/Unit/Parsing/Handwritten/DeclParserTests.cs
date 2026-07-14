using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Parsing.Handwritten;
using Eidosc.ProjectSystem;
using Eidosc.Utilities;
using Eidosc.Utils;

namespace Eidosc.Tests.Unit.Parsing.Handwritten;

public sealed class DeclParserTests
{
    [Fact]
    public void Parse_let_decl_with_value()
    {
        var ctx = MakeCtx("let", Ident("x"), "=", Num("42"), ";");
        var parser = new DeclParser(ctx);
        var result = parser.ParseTopLevel();
        var decl = Assert.IsType<LetDecl>(result);
        var pattern = Assert.IsType<VarPattern>(decl.Pattern);
        Assert.Equal("x", pattern.Name);
        Assert.NotNull(decl.Value);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_let_decl_with_type_annotation()
    {
        var ctx = MakeCtx("let", Ident("x"), ":", TypeId("Int"), "=", Num("42"), ";");
        var parser = new DeclParser(ctx);
        var result = parser.ParseTopLevel();
        var decl = Assert.IsType<LetDecl>(result);
        var pattern = Assert.IsType<VarPattern>(decl.Pattern);
        Assert.Equal("x", pattern.Name);
        Assert.NotNull(decl.TypeAnnotation);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_let_decl_with_package_qualified_type_annotation()
    {
        var ctx = MakeCtx(
            "let", Ident("x"), ":",
            Ident("crypto_a"), "::", TypeId("Hash"), "/", TypeId("Sha256"), "::", TypeId("Digest"),
            "=", TypeId("Digest"), "(", ")", ";");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var decl = Assert.IsType<LetDecl>(result);
        var typePath = Assert.IsType<TypePath>(decl.TypeAnnotation);
        Assert.Equal("crypto_a", typePath.PackageAlias);
        Assert.Equal(["Hash", "Sha256"], typePath.ModulePath);
        Assert.Equal("Digest", typePath.TypeName);
        Assert.Equal(["crypto_a", "Hash", "Sha256", "Digest"], typePath.ToQualifiedPathParts());
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_package_qualified_import_decl()
    {
        var ctx = MakeCtx("import", Ident("crypto_a"), "::", TypeId("Hash"), "/", TypeId("Sha256"), "as", TypeId("Sha"), ";");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var import = Assert.IsType<ImportDecl>(result);
        Assert.Equal("crypto_a", import.PackageAlias);
        Assert.Equal(["Hash", "Sha256"], import.ModulePath);
        Assert.Equal(["crypto_a", "Hash", "Sha256"], import.ToQualifiedModulePath());
        Assert.Equal("Sha", import.Alias);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_let_decl_with_option_suffix_type_annotation()
    {
        var ctx = MakeCtx("let", Ident("x"), ":", TypeId("Int"), "?", "=", TypeId("None"), "(", ")", ";");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var decl = Assert.IsType<LetDecl>(result);
        var optionType = Assert.IsType<TypePath>(decl.TypeAnnotation);
        Assert.Equal("Option", optionType.TypeName);
        Assert.Equal("Std", optionType.PackageAlias);
        Assert.Equal(["Option"], optionType.ModulePath);
        var innerType = Assert.IsType<TypePath>(Assert.Single(optionType.TypeArgs));
        Assert.Equal("Int", innerType.TypeName);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_let_decl_with_nested_option_suffix_type_argument()
    {
        var ctx = MakeCtx(
            "let", Ident("x"), ":",
            TypeId("Seq"), "[", TypeId("Int"), "?", "]", "?",
            "=", TypeId("None"), "(", ")", ";");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var decl = Assert.IsType<LetDecl>(result);
        var outerOption = Assert.IsType<TypePath>(decl.TypeAnnotation);
        Assert.Equal("Option", outerOption.TypeName);
        Assert.Equal("Std", outerOption.PackageAlias);
        Assert.Equal(["Option"], outerOption.ModulePath);

        var listType = Assert.IsType<TypePath>(Assert.Single(outerOption.TypeArgs));
        Assert.Equal("Seq", listType.TypeName);

        var innerOption = Assert.IsType<TypePath>(Assert.Single(listType.TypeArgs));
        Assert.Equal("Option", innerOption.TypeName);
        Assert.Equal("Std", innerOption.PackageAlias);
        Assert.Equal(["Option"], innerOption.ModulePath);
        var intType = Assert.IsType<TypePath>(Assert.Single(innerOption.TypeArgs));
        Assert.Equal("Int", intType.TypeName);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_export_let_decl()
    {
        var ctx = MakeCtx("export", "let", Ident("x"), "=", Num("1"), ";");
        var parser = new DeclParser(ctx);
        var result = parser.ParseTopLevel();
        var decl = Assert.IsType<LetDecl>(result);
        var pattern = Assert.IsType<VarPattern>(decl.Pattern);
        Assert.True(decl.IsExported);
        Assert.Equal("x", pattern.Name);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_let_mut_decl()
    {
        var ctx = MakeCtx("let", "mut", Ident("counter"), "=", Num("0"), ";");
        var parser = new DeclParser(ctx);
        var result = parser.ParseTopLevel();
        var decl = Assert.IsType<LetDecl>(result);
        var pattern = Assert.IsType<VarPattern>(decl.Pattern);
        Assert.True(decl.IsMutable);
        Assert.Equal("counter", pattern.Name);
        Assert.NotNull(decl.Value);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_let_decl()
    {
        var ctx = MakeCtx("let", Ident("a"), "=", Num("1"), ";");
        var parser = new DeclParser(ctx);
        var result = parser.ParseTopLevel();
        var decl = Assert.IsType<LetDecl>(result);
        Assert.NotNull(decl.Pattern);
        Assert.NotNull(decl.Value);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_let_decl_tuple_pattern()
    {
        var ctx = MakeCtx("let", "(", Ident("a"), ",", Ident("b"), ")", "=", "(", Num("1"), ",", Num("2"), ")", ";");
        var parser = new DeclParser(ctx);
        var result = parser.ParseTopLevel();
        var decl = Assert.IsType<LetDecl>(result);
        Assert.NotNull(decl.Pattern);
        Assert.IsType<TuplePattern>(decl.Pattern);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_func_def_no_body()
    {
        var ctx = MakeCtx("func", Ident("id"), "[", TypeId("T"), "]", ":", TypeId("T"), "->", TypeId("T"));
        var parser = new DeclParser(ctx);
        var result = parser.ParseTopLevel();
        var func = Assert.IsType<FuncDef>(result);
        Assert.Equal("id", func.Name);
        Assert.Empty(func.Body);
        Assert.NotEmpty(func.Signature);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_func_def_with_body()
    {
        var ctx = MakeCtx("func", Ident("double"), ":", TypeId("Int"), "->", TypeId("Int"), "{", Ident("x"), "=>", Ident("x"), "+", Num("1"), "}");
        var parser = new DeclParser(ctx);
        var result = parser.ParseTopLevel();
        var func = Assert.IsType<FuncDef>(result);
        Assert.Equal("double", func.Name);
        Assert.Single(func.Body);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_func_def_with_multiple_branches()
    {
        var ctx = MakeCtx("func", Ident("unwrap"), ":", TypeId("Int"), "->", TypeId("Int"),
            "{", TypeId("Some"), "(", Ident("v"), ")", "=>", Ident("v"), ",",
            TypeId("None"), "(", ")", "=>", Num("0"), "}");
        var parser = new DeclParser(ctx);
        var result = parser.ParseTopLevel();
        var func = Assert.IsType<FuncDef>(result);
        Assert.Equal("unwrap", func.Name);
        Assert.Equal(2, func.Body.Count);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_func_def_with_curried_lambda_body()
    {
        // eq_value :: T -> T -> Bool { left => right => left == right }
        var ctx = MakeCtx("func", Ident("eq_value"), "[", TypeId("T"), ":", TypeId("Eq"), "]",
            ":", TypeId("T"), "->", TypeId("T"), "->", TypeId("Bool"),
            "{", Ident("left"), "=>", Ident("right"), "=>", Ident("left"), "==", Ident("right"), "}");
        var parser = new DeclParser(ctx);
        var result = parser.ParseTopLevel();
        var func = Assert.IsType<FuncDef>(result);
        Assert.Equal("eq_value", func.Name);
        Assert.Single(func.Body);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_func_def_with_guard_and_tuple_pattern()
    {
        // (left, right) when left < right => Less(), (left, right) when left > right => Greater(), _ => Equal()
        var ctx = MakeCtx("func", Ident("compare"), "[", TypeId("T"), "]", ":", TypeId("T"), "->", TypeId("T"), "->", TypeId("Ordering"),
            "{",
            "(", Ident("left"), ",", Ident("right"), ")", "when", Ident("left"), "<", Ident("right"), "=>", TypeId("Less"), "(", ")", ",",
            "(", Ident("left"), ",", Ident("right"), ")", "when", Ident("left"), ">", Ident("right"), "=>", TypeId("Greater"), "(", ")", ",",
            "_", "=>", TypeId("Equal"), "(", ")",
            "}");
        var parser = new DeclParser(ctx);
        var result = parser.ParseTopLevel();
        var func = Assert.IsType<FuncDef>(result);
        Assert.Equal("compare", func.Name);
        Assert.Equal(3, func.Body.Count);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_type_alias()
    {
        var ctx = MakeCtx("type", TypeId("UserId"), "=", TypeId("Int"), ";");
        var parser = new DeclParser(ctx);
        var result = parser.ParseTopLevel();
        var adt = Assert.IsType<AdtDef>(result);
        Assert.Equal("UserId", adt.Name);
        Assert.True(adt.IsTypeAlias);
        Assert.NotNull(adt.AliasTarget);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_adt_with_legacy_pipe_constructors()
    {
        var ctx = MakeCtx("type", TypeId("Option"), "[", TypeId("T"), "]", "{",
            "|", TypeId("Some"), "(", TypeId("T"), ")",
            "|", TypeId("None"), "(", ")", "}");
        var parser = new DeclParser(ctx);
        var result = parser.ParseTopLevel();
        var adt = Assert.IsType<AdtDef>(result);
        Assert.Equal("Option", adt.Name);
        Assert.Equal(2, adt.Constructors.Count);
        Assert.False(adt.IsTypeAlias);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_adt_with_comma_constructors()
    {
        var ctx = MakeCtx("type", TypeId("Option"), "[", TypeId("T"), "]", "{",
            TypeId("Some"), "(", TypeId("T"), ")", ",",
            TypeId("None"), "(", ")", "}");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var adt = Assert.IsType<AdtDef>(result);
        Assert.Equal("Option", adt.Name);
        Assert.Equal(2, adt.Constructors.Count);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_gadt_with_comma_constructors()
    {
        var ctx = MakeNameFirstCtx(TypeId("Direction"), "[", TypeId("A"), "]", "::", "type", "{",
            TypeId("North"), "->", TypeId("Direction"), "[", TypeId("Vertical"), "]", ",",
            TypeId("East"), "->", TypeId("Direction"), "[", TypeId("Horizontal"), "]", "}");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var adt = Assert.IsType<AdtDef>(result);
        Assert.Equal(2, adt.Constructors.Count);
        Assert.All(adt.Constructors, ctor => Assert.NotNull(ctor.ReturnType));
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_adt_rejects_pipe_constructor_separator()
    {
        var ctx = MakeNameFirstCtx(TypeId("Direction"), "::", "type", "{",
            TypeId("North"), ",", TypeId("South"), "|", TypeId("East"), "}");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var adt = Assert.IsType<AdtDef>(result);
        Assert.Equal(3, adt.Constructors.Count);
        Assert.Contains(ctx.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("ADT constructors use ','", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_adt_requires_constructor_separator()
    {
        var ctx = MakeNameFirstCtx(TypeId("Direction"), "::", "type", "{",
            TypeId("North"), TypeId("South"), "}");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var adt = Assert.IsType<AdtDef>(result);
        Assert.Equal(2, adt.Constructors.Count);
        Assert.Contains(ctx.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("expected ',' between ADT constructors", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_adt_with_named_args()
    {
        var ctx = MakeCtx("type", TypeId("Point"), "{",
            TypeId("Point"), "{", Ident("x"), ":", TypeId("Int"), ",", Ident("y"), ":", TypeId("Int"), "}", "}");
        var parser = new DeclParser(ctx);
        var result = parser.ParseTopLevel();
        var adt = Assert.IsType<AdtDef>(result);
        Assert.Equal("Point", adt.Name);
        Assert.Single(adt.Constructors);
        Assert.Equal(2, adt.Constructors[0].NamedArgs.Count);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_adt_bare_product_fields_keyword_first()
    {
        // Bare product type: `type Point { x: Int, y: Int }` — no inner constructor.
        // The parser leaves Fields populated and Constructors empty; the default
        // constructor is synthesized later during name resolution.
        var ctx = MakeCtx("type", TypeId("Point"), "{",
            Ident("x"), ":", TypeId("Int"), ",", Ident("y"), ":", TypeId("Int"), "}");
        var parser = new DeclParser(ctx);
        var result = parser.ParseTopLevel();
        var adt = Assert.IsType<AdtDef>(result);
        Assert.Equal("Point", adt.Name);
        Assert.Empty(adt.Constructors);
        Assert.Equal(2, adt.Fields.Count);
        Assert.Equal("x", adt.Fields[0].Name);
        Assert.Equal("y", adt.Fields[1].Name);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_adt_bare_product_fields_name_first()
    {
        // Name-first bare product type: `Point :: type { x: Int, y: Int }`.
        var ctx = MakeNameFirstCtx(TypeId("Point"), "::", "type", "{",
            Ident("x"), ":", TypeId("Int"), ",", Ident("y"), ":", TypeId("Int"), "}");
        var parser = new DeclParser(ctx);
        var result = parser.ParseTopLevel();
        var adt = Assert.IsType<AdtDef>(result);
        Assert.Equal("Point", adt.Name);
        Assert.Empty(adt.Constructors);
        Assert.Equal(2, adt.Fields.Count);
        Assert.Equal("x", adt.Fields[0].Name);
        Assert.Equal("y", adt.Fields[1].Name);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_adt_constructor_constants_reports_removed_syntax()
    {
        var ctx = MakeCtx("type", TypeId("Direction"), "{",
            TypeId("North"), "{", Ident("dx"), "=", Num("0"), ",", Ident("dy"), "=", "-", Num("1"), "}", "|",
            TypeId("East"), "{", Ident("step"), ":", TypeId("Int"), ",", Ident("dx"), "=", Num("1"), "}", "}");
        var parser = new DeclParser(ctx);
        var result = parser.ParseTopLevel();
        var adt = Assert.IsType<AdtDef>(result);

        Assert.Equal(2, adt.Constructors.Count);
        Assert.Empty(adt.Constructors[0].NamedArgs);
        Assert.Single(adt.Constructors[1].NamedArgs);
        Assert.NotEmpty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_gadt_constructor_rejects_constants_and_keeps_return_type()
    {
        var ctx = MakeCtx("type", TypeId("Direction"), "[", TypeId("A"), "]", "{",
            TypeId("North"), "{", Ident("dx"), "=", Num("0"), "}", "->", TypeId("Direction"), "[", TypeId("Vertical"), "]", "}");
        var parser = new DeclParser(ctx);
        var result = parser.ParseTopLevel();
        var adt = Assert.IsType<AdtDef>(result);

        var ctor = Assert.Single(adt.Constructors);
        Assert.NotNull(ctor.ReturnType);
        Assert.NotEmpty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_constructor_local_type_params()
    {
        var ctx = MakeCtx("type", TypeId("Box"), "{",
            TypeId("Pack"), "[", TypeId("A"), "]", "(", TypeId("A"), ")", "->", TypeId("Box"), "}");
        var parser = new DeclParser(ctx);
        var result = parser.ParseTopLevel();
        var adt = Assert.IsType<AdtDef>(result);

        var ctor = Assert.Single(adt.Constructors);
        var typeParam = Assert.Single(ctor.TypeParams);
        Assert.Equal("A", typeParam.Name);
        Assert.Single(ctor.PositionalArgs);
        Assert.NotNull(ctor.ReturnType);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_constructor_local_type_params_with_constraints_and_kind()
    {
        var ctx = MakeCtx("type", TypeId("Box"), "{",
            TypeId("Pack"), "[", TypeId("A"), ":", TypeId("Show"), ",", TypeId("F"), ":", TypeId("kind2"), "]",
            "(", TypeId("F"), "[", TypeId("A"), "]", ")", "->", TypeId("Box"), "}");
        var parser = new DeclParser(ctx);
        var result = parser.ParseTopLevel();
        var adt = Assert.IsType<AdtDef>(result);

        var ctor = Assert.Single(adt.Constructors);
        Assert.Equal(2, ctor.TypeParams.Count);
        Assert.Equal("A", ctor.TypeParams[0].Name);
        Assert.Single(ctor.TypeParams[0].TraitConstraints);
        Assert.Equal("F", ctor.TypeParams[1].Name);
        Assert.NotNull(ctor.TypeParams[1].KindAnnotation);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_trait_def()
    {
        var ctx = MakeCtx("trait", TypeId("Show"), "{",
            "func", Ident("show"), ":", TypeId("Self"), "->", TypeId("String"), ";",
            "}");
        var parser = new DeclParser(ctx);
        var result = parser.ParseTopLevel();
        var trait = Assert.IsType<TraitDef>(result);
        Assert.Equal("Show", trait.Name);
        Assert.Single(trait.Methods);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_trait_def_with_default_impl()
    {
        // Eidos function body is match-arm syntax: { pattern => expr, ... }
        var ctx = MakeCtx("trait", TypeId("Eq"), "{",
            "func", Ident("eq"), ":", TypeId("Self"), "->", TypeId("Self"), "->", TypeId("Bool"), ";",
            "func", Ident("neq"), ":", TypeId("Self"), "->", TypeId("Self"), "->", TypeId("Bool"), "{",
            Ident("x"), "=>", TypeId("True"), "(", ")",
            "}",
            "}");
        var parser = new DeclParser(ctx);
        var result = parser.ParseTopLevel();
        var trait = Assert.IsType<TraitDef>(result);
        Assert.Equal("Eq", trait.Name);
        Assert.Equal(2, trait.Methods.Count);

        // eq — signature-only (no body)
        var eq = trait.Methods[0];
        Assert.Equal("eq", eq.Name);
        Assert.Empty(eq.Body);

        // neq — default implementation (has body)
        var neq = trait.Methods[1];
        Assert.Equal("neq", neq.Name);
        Assert.NotEmpty(neq.Body);

        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_import_module()
    {
        var ctx = MakeCtx("import", TypeId("Std"), "/", TypeId("Option"));
        var parser = new DeclParser(ctx);
        var result = parser.ParseTopLevel();
        var import = Assert.IsType<ImportDecl>(result);
        Assert.Equal(["Std", "Option"], import.ModulePath);
        Assert.Equal(ImportKind.Module, import.Kind);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_import_wildcard()
    {
        var ctx = MakeCtx("import", TypeId("Std"), "/", TypeId("Option"), "::", "*");
        var parser = new DeclParser(ctx);
        var result = parser.ParseTopLevel();
        var import = Assert.IsType<ImportDecl>(result);
        Assert.Equal(ImportKind.Wildcard, import.Kind);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_import_selective()
    {
        var ctx = MakeCtx("import", TypeId("Std"), "/", TypeId("Option"), "::", "{", TypeId("Some"), ",", TypeId("None"), "}");
        var parser = new DeclParser(ctx);
        var result = parser.ParseTopLevel();
        var import = Assert.IsType<ImportDecl>(result);
        Assert.Equal(ImportKind.Selective, import.Kind);
        Assert.Equal(2, import.SelectiveImports.Count);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_import_with_alias()
    {
        var ctx = MakeCtx("import", TypeId("Std"), "/", TypeId("Option"), "as", TypeId("Opt"));
        var parser = new DeclParser(ctx);
        var result = parser.ParseTopLevel();
        var import = Assert.IsType<ImportDecl>(result);
        Assert.Equal("Opt", import.Alias);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_module_decl()
    {
        var ctx = MakeCtx("module", TypeId("Std"), "/", TypeId("Option"),
            "{", "let", Ident("x"), "=", Num("1"), ";", "}");
        var parser = new DeclParser(ctx);
        var result = parser.ParseTopLevel();
        var module = Assert.IsType<ModuleDecl>(result);
        Assert.Equal(["Std", "Option"], module.Path);
        Assert.NotEmpty(module.Declarations);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_func_def()
    {
        var ctx = MakeNameFirstCtx(Ident("main"), "::", TypeId("Unit"), "->", TypeId("Int"),
            "{", "_", "=>", Num("0"), "}");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var func = Assert.IsType<FuncDef>(result);
        Assert.Equal("main", func.Name);
        Assert.Single(func.Body);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_func_decl()
    {
        var ctx = MakeNameFirstCtx(Ident("malloc"), "::", TypeId("Int"), "->", TypeId("RawPtr"), ";");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var func = Assert.IsType<FuncDecl>(result);
        Assert.Equal("malloc", func.Name);
        Assert.IsType<ArrowType>(Assert.Single(func.Signature));
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_func_def_with_parenthesized_function_type_parameter()
    {
        var ctx = MakeNameFirstCtx(Ident("apply"), "::",
            "(", TypeId("Int"), "->", TypeId("Int"), ")", "->", TypeId("Int"), "->", TypeId("Int"),
            "{", Ident("f"), "=>", Ident("x"), "=>", Ident("f"), "(", Ident("x"), ")", "}");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var func = Assert.IsType<FuncDef>(result);
        Assert.Equal("apply", func.Name);
        var signature = Assert.IsType<ArrowType>(Assert.Single(func.Signature));
        Assert.IsType<ArrowType>(signature.ParamType);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_inferred_static_binding_with_constructor_call()
    {
        var ctx = MakeNameFirstCtx(Ident("none_val"), "::", TypeId("None"), "(", ")", ";");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var binding = Assert.IsType<LetDecl>(result);
        var pattern = Assert.IsType<VarPattern>(binding.Pattern);
        Assert.Equal("none_val", pattern.Name);
        Assert.Null(binding.TypeAnnotation);
        Assert.NotNull(binding.Value);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_block_binding_and_assignment()
    {
        var ctx = MakeNameFirstCtx(Ident("main"), "::", TypeId("Unit"), "->", TypeId("Int"),
            "{", "_", "=>", "{",
            Ident("x"), ":=", Num("1"), ";",
            "mut", Ident("y"), ":", TypeId("Int"), ":=", Num("2"), ";",
            Ident("y"), "=", Ident("x"), ";",
            Ident("y"),
            "}", "}");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var func = Assert.IsType<FuncDef>(result);
        var body = Assert.Single(func.Body);
        var block = Assert.IsType<BlockExpr>(body.Expression);
        var first = Assert.IsType<LetDecl>(block.Statements[0]);
        Assert.False(first.IsMutable);
        var second = Assert.IsType<LetDecl>(block.Statements[1]);
        Assert.True(second.IsMutable);
        Assert.NotNull(second.TypeAnnotation);
        Assert.IsType<Assignment>(block.Statements[2]);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_function_prefix_where_need_clauses()
    {
        var ctx = MakeNameFirstCtx(Ident("showValue"), "[", TypeId("T"), "]", "::",
            TypeId("T"), "->", TypeId("String"),
            "where", TypeId("T"), ":", TypeId("Show"),
            "need", TypeId("Console"),
            "{", Ident("value"), "=>", Str("value"), "}");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var func = Assert.IsType<FuncDef>(result);
        Assert.Single(func.TypeParams);
        Assert.Single(func.TypeParams[0].TraitConstraints);
        Assert.Equal("Show", func.TypeParams[0].TraitConstraints[0].TraitName);
        Assert.Single(func.RequiredAbilities);
        Assert.Equal(["Console"], func.RequiredAbilities[0].Path);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_function_comptime_type_parameters()
    {
        var ctx = MakeNameFirstCtx(Ident("typeId"), "[",
            "comptime", TypeId("T"), ":", TypeId("Type"),
            ",",
            TypeId("U"), ":", "comptime", TypeId("Type"),
            "]", "::",
            TypeId("T"), "->", TypeId("T"),
            "{", Ident("value"), "=>", Ident("value"), "}");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var func = Assert.IsType<FuncDef>(result);
        Assert.Equal(2, func.TypeParams.Count);
        Assert.All(func.TypeParams, static typeParam => Assert.True(typeParam.IsComptime));
        Assert.All(func.TypeParams, static typeParam => Assert.Equal(Eidosc.Types.GenericParameterKind.Type, typeParam.ParameterKind));
        Assert.All(func.TypeParams, static typeParam => Assert.IsType<TypePath>(typeParam.ComptimeTypeAnnotation));
        Assert.Equal("Type", Assert.IsType<TypePath>(func.TypeParams[0].ComptimeTypeAnnotation).TypeName);
        Assert.Equal("Type", Assert.IsType<TypePath>(func.TypeParams[1].ComptimeTypeAnnotation).TypeName);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_value_and_effect_generic_parameter_kinds()
    {
        var ctx = MakeNameFirstCtx(Ident("use"), "[",
            "comptime", TypeId("N"), ":", TypeId("Int"),
            ",",
            TypeId("E"), ":", "effects",
            "]", "::",
            TypeId("Unit"), "->", TypeId("Unit"),
            "{", Ident("value"), "=>", "(", ")", "}");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var func = Assert.IsType<FuncDef>(result);
        Assert.Equal(Eidosc.Types.GenericParameterKind.Value, func.TypeParams[0].ParameterKind);
        Assert.Equal(Eidosc.Types.GenericParameterKind.EffectRow, func.TypeParams[1].ParameterKind);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_type_application_preserves_ordered_value_and_type_candidates()
    {
        var ctx = MakeNameFirstCtx(
            Ident("value"), "::",
            TypeId("Vector"), "[", Num("4"), ",", TypeId("Int"), "]",
            "=", Num("0"), ";");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var declaration = Assert.IsType<LetDecl>(result);
        var vector = Assert.IsType<TypePath>(declaration.TypeAnnotation);
        Assert.Equal(2, vector.GenericArguments.Count);
        Assert.IsType<LiteralExpr>(Assert.IsType<UnresolvedGenericArgumentNode>(vector.GenericArguments[0]).ValueCandidate);
        Assert.Equal("Int", Assert.IsType<TypePath>(Assert.IsType<UnresolvedGenericArgumentNode>(vector.GenericArguments[1]).TypeCandidate).TypeName);
        Assert.Single(vector.TypeArgs);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_function_suffix_where_need_clauses()
    {
        var ctx = MakeNameFirstCtx(Ident("showValue"), "[", TypeId("T"), "]", "::",
            TypeId("T"), "->", TypeId("String"),
            "{", Ident("value"), "=>", Str("value"), "}",
            "need", TypeId("Console"),
            "where", TypeId("T"), ":", TypeId("Show"));
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var func = Assert.IsType<FuncDef>(result);
        Assert.Single(func.TypeParams[0].TraitConstraints);
        Assert.Equal("Show", func.TypeParams[0].TraitConstraints[0].TraitName);
        Assert.Single(func.RequiredAbilities);
        Assert.Equal(["Console"], func.RequiredAbilities[0].Path);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_type_alias()
    {
        var ctx = MakeNameFirstCtx(TypeId("UserId"), "::", "type", "=", TypeId("Int"), ";");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var type = Assert.IsType<AdtDef>(result);
        Assert.Equal("UserId", type.Name);
        Assert.True(type.IsTypeAlias);
        Assert.NotNull(type.AliasTarget);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_typed_value_binding()
    {
        var ctx = MakeNameFirstCtx(Ident("answer"), "::", TypeId("Int"), "=", Num("42"), ";");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var decl = Assert.IsType<LetDecl>(result);
        var pattern = Assert.IsType<VarPattern>(decl.Pattern);
        Assert.Equal("answer", pattern.Name);
        Assert.Equal("Int", Assert.IsType<TypePath>(decl.TypeAnnotation).TypeName);
        Assert.Equal("42", Assert.IsType<LiteralExpr>(decl.Value).RawText);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_tuple_typed_value_binding()
    {
        var ctx = MakeNameFirstCtx(
            Ident("tuple"), "::",
            "(", TypeId("Int"), ",", TypeId("String"), ",", TypeId("Bool"), ")",
            "=",
            "(", Num("1"), ",", Str("\"hello\""), ",", "true", ")", ";");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var decl = Assert.IsType<LetDecl>(result);
        var pattern = Assert.IsType<VarPattern>(decl.Pattern);
        Assert.Equal("tuple", pattern.Name);
        Assert.IsType<TupleType>(decl.TypeAnnotation);
        Assert.IsType<TupleExpr>(decl.Value);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_inferred_value_binding()
    {
        var ctx = MakeNameFirstCtx(Ident("pi"), "::", Num("3.14"), ";");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var decl = Assert.IsType<LetDecl>(result);
        var pattern = Assert.IsType<VarPattern>(decl.Pattern);
        Assert.Equal("pi", pattern.Name);
        Assert.Null(decl.TypeAnnotation);
        Assert.Equal("3.14", Assert.IsType<LiteralExpr>(decl.Value).RawText);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_comptime_value_binding()
    {
        var ctx = MakeNameFirstCtx(TypeId("DefaultCapacity"), "::", "comptime", Num("64"), ";");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var decl = Assert.IsType<LetDecl>(result);
        var pattern = Assert.IsType<VarPattern>(decl.Pattern);
        Assert.True(decl.IsComptime);
        Assert.Equal("DefaultCapacity", pattern.Name);
        Assert.Equal("64", Assert.IsType<LiteralExpr>(decl.Value).RawText);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_comptime_function()
    {
        var ctx = MakeNameFirstCtx(Ident("fieldCount"), "::", "comptime", TypeId("Type"), "->", TypeId("Int"),
            "{", Ident("ty"), "=>", Num("1"), "}");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var func = Assert.IsType<FuncDef>(result);
        Assert.Equal("fieldCount", func.Name);
        Assert.True(func.IsComptime);
        Assert.IsType<ArrowType>(Assert.Single(func.Signature));
        Assert.Single(func.Body);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_trait_members()
    {
        var traitCtx = MakeNameFirstCtx(TypeId("Show"), "[", TypeId("T"), "]", "::", "trait",
            "{", Ident("show"), "::", TypeId("T"), "->", TypeId("String"), "}");
        var traitParser = new DeclParser(traitCtx);

        var traitResult = traitParser.ParseTopLevel();

        var trait = Assert.IsType<TraitDef>(traitResult);
        Assert.Equal("Show", trait.Name);
        Assert.Single(trait.Methods);
        Assert.Equal("show", trait.Methods[0].Name);
        Assert.Empty(traitCtx.Diagnostics);

    }

    [Fact]
    public void Parse_name_first_trait_where_clause_before_and_after_body()
    {
        var prefixCtx = MakeNameFirstCtx(TypeId("ShowBox"), "[", TypeId("T"), "]", "::", "trait",
            "where", TypeId("T"), ":", TypeId("Show"),
            "{", Ident("show"), "::", TypeId("T"), "->", TypeId("String"), "}");
        var prefixParser = new DeclParser(prefixCtx);

        var prefixResult = prefixParser.ParseTopLevel();

        var prefixTrait = Assert.IsType<TraitDef>(prefixResult);
        Assert.Single(prefixTrait.TypeParams[0].TraitConstraints);
        Assert.Equal("Show", prefixTrait.TypeParams[0].TraitConstraints[0].TraitName);
        Assert.Empty(prefixCtx.Diagnostics);

        var suffixCtx = MakeNameFirstCtx(TypeId("EqBox"), "[", TypeId("T"), "]", "::", "trait",
            "{", Ident("eq"), "::", TypeId("T"), "->", TypeId("T"), "->", TypeId("Bool"), "}",
            "where", TypeId("T"), ":", TypeId("Eq"));
        var suffixParser = new DeclParser(suffixCtx);

        var suffixResult = suffixParser.ParseTopLevel();

        var suffixTrait = Assert.IsType<TraitDef>(suffixResult);
        Assert.Single(suffixTrait.TypeParams[0].TraitConstraints);
        Assert.Equal("Eq", suffixTrait.TypeParams[0].TraitConstraints[0].TraitName);
        Assert.Empty(suffixCtx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_named_instance_decl()
    {
        var ctx = MakeNameFirstCtx(TypeId("EqInt"), "::", "instance", TypeId("Eq"), "[", TypeId("Int"), "]",
            "{",
            Ident("eq"), "::", TypeId("Int"), "->", TypeId("Int"), "->", TypeId("Bool"),
            "{", Ident("a"), Ident("b"), "=>", Ident("intEq"), "(", Ident("a"), ",", Ident("b"), ")", "}",
            "}");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var instance = Assert.IsType<InstanceDecl>(result);
        Assert.Equal("EqInt", instance.Name);
        Assert.NotNull(instance.Trait);
        Assert.Equal("Eq", instance.Trait.TraitName);
        Assert.Single(instance.Trait.TypeArgs);
        var method = Assert.Single(instance.Methods);
        Assert.Equal("eq", method.Name);
        Assert.Single(method.Body);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_constructor_bridge_instance_decl()
    {
        var ctx = MakeNameFirstCtx(
            TypeId("DirectionDirection"), "::", "instance", TypeId("Direction"), "for", TypeId("Direction"), ";");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var instance = Assert.IsType<InstanceDecl>(result);
        Assert.Equal("DirectionDirection", instance.Name);
        Assert.NotNull(instance.Trait);
        Assert.Equal("Direction", instance.Trait.TraitName);
        var targetType = Assert.IsType<TypePath>(instance.TargetType);
        Assert.Equal("Direction", targetType.TypeName);
        Assert.True(instance.UsesConstructorBridge);
        Assert.Empty(instance.Methods);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_constructor_bridge_instance_facts()
    {
        var ctx = MakeNameFirstCtx(
            TypeId("DirectionDirection"), "::", "instance", TypeId("Direction"), "for", TypeId("Direction"),
            "{",
            TypeId("North"), "=>", "{", Ident("opposite"), "=", TypeId("South"), "(", ")", "}", "|",
            TypeId("South"), "=>", "{", Ident("opposite"), "=", TypeId("North"), "(", ")", "}",
            "}");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var instance = Assert.IsType<InstanceDecl>(result);
        Assert.True(instance.UsesConstructorBridge);
        Assert.Equal(2, instance.ConstructorBridgeFacts.Count);
        Assert.Equal("North", instance.ConstructorBridgeFacts[0].ConstructorName);
        Assert.Equal("opposite", Assert.Single(instance.ConstructorBridgeFacts[0].Constants).Name);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_trait_associated_type_and_const()
    {
        var ctx = MakeNameFirstCtx(TypeId("Iterator"), "[", TypeId("I"), "]", "::", "trait",
            "{",
            TypeId("Item"), "::", "type",
            TypeId("Min"), "::", TypeId("I"),
            Ident("next"), "::", TypeId("I"), "->", TypeId("Option"), "[", TypeId("I"), "]",
            "}");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var trait = Assert.IsType<TraitDef>(result);
        Assert.Equal("Iterator", trait.Name);
        var associatedType = Assert.Single(trait.AssociatedTypes);
        Assert.Equal("Item", associatedType.Name);
        Assert.Null(associatedType.ValueType);
        var associatedConst = Assert.Single(trait.AssociatedConsts);
        Assert.Equal("Min", associatedConst.Name);
        Assert.NotNull(associatedConst.Type);
        Assert.Null(associatedConst.Value);
        Assert.Single(trait.Methods);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_instance_associated_type_and_const()
    {
        var ctx = MakeNameFirstCtx(TypeId("IteratorInt"), "::", "instance", TypeId("Iterator"), "[", TypeId("Int"), "]",
            "{",
            TypeId("Item"), "::", "type", "=", TypeId("Int"),
            TypeId("Min"), "::", TypeId("Int"), "=", Num("0"),
            "}");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var instance = Assert.IsType<InstanceDecl>(result);
        Assert.Equal("IteratorInt", instance.Name);
        var associatedType = Assert.Single(instance.AssociatedTypes);
        Assert.Equal("Item", associatedType.Name);
        Assert.NotNull(associatedType.ValueType);
        var associatedConst = Assert.Single(instance.AssociatedConsts);
        Assert.Equal("Min", associatedConst.Name);
        Assert.NotNull(associatedConst.Type);
        Assert.NotNull(associatedConst.Value);
        Assert.Empty(instance.Methods);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_associated_type_projection()
    {
        var ctx = MakeNameFirstCtx(TypeId("Iterator"), "[", TypeId("I"), "]", "::", "trait",
            "{",
            TypeId("Item"), "::", "type",
            Ident("head"), "::", TypeId("I"), "->", TypeId("Option"), "[", TypeId("Iterator"), "[", TypeId("I"), "]", ".", TypeId("Item"), "]",
            "}");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var trait = Assert.IsType<TraitDef>(result);
        var method = Assert.Single(trait.Methods);
        var signature = Assert.Single(method.Signature);
        var arrow = Assert.IsType<ArrowType>(signature);
        var option = Assert.IsType<TypePath>(arrow.ReturnType);
        var projection = Assert.IsType<AssociatedTypeProjection>(Assert.Single(option.TypeArgs));
        Assert.Equal("Item", projection.MemberName);
        var target = Assert.IsType<TypePath>(projection.Target);
        Assert.Equal("Iterator", target.TypeName);
        Assert.Single(target.TypeArgs);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_module_decl()
    {
        var ctx = MakeNameFirstCtx(TypeId("Demo"), ".", TypeId("Main"), "::", "module",
            "{", Ident("main"), "::", TypeId("Unit"), "->", TypeId("Int"), "{", "_", "=>", Num("0"), "}", "}");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var module = Assert.IsType<ModuleDecl>(result);
        Assert.Equal(["Demo", "Main"], module.Path);
        var func = Assert.IsType<FuncDef>(Assert.Single(module.Declarations));
        Assert.Equal("main", func.Name);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_import_package_qualified_dot_path()
    {
        var ctx = MakeNameFirstCtx(TypeId("Seq"), "::", "import", TypeId("Std"), ".", TypeId("Collections"), ".", TypeId("Seq"), ";");
        var parser = new DeclParser(ctx);

        var result = parser.ParseTopLevel();

        var import = Assert.IsType<ImportDecl>(result);
        Assert.Null(import.PackageAlias);
        Assert.Equal(["Std", "Collections", "Seq"], import.ModulePath);
        Assert.Equal("Seq", import.Alias);
        Assert.Equal(ImportKind.Module, import.Kind);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_import_selective_and_wildcard()
    {
        var selectiveCtx = MakeNameFirstCtx("import", TypeId("Std"), ".", TypeId("Seq"), ".",
            "{", Ident("map"), ",", Ident("filter"), "as", Ident("where"), "}", ";");
        var selectiveParser = new DeclParser(selectiveCtx);

        var selectiveResult = selectiveParser.ParseTopLevel();

        var selective = Assert.IsType<ImportDecl>(selectiveResult);
        Assert.Null(selective.PackageAlias);
        Assert.Equal(["Std", "Seq"], selective.ModulePath);
        Assert.Equal(ImportKind.Selective, selective.Kind);
        Assert.Equal(["map", "filter"], selective.SelectiveImports.Select(static item => item.Name).ToArray());
        Assert.Equal("where", selective.SelectiveImports[1].Alias);
        Assert.Empty(selectiveCtx.Diagnostics);

        var wildcardCtx = MakeNameFirstCtx("import", TypeId("Std"), ".", TypeId("Prelude"), ".", "*", ";");
        var wildcardParser = new DeclParser(wildcardCtx);

        var wildcardResult = wildcardParser.ParseTopLevel();

        var wildcard = Assert.IsType<ImportDecl>(wildcardResult);
        Assert.Null(wildcard.PackageAlias);
        Assert.Equal(["Std", "Prelude"], wildcard.ModulePath);
        Assert.Equal(ImportKind.Wildcard, wildcard.Kind);
        Assert.Empty(wildcardCtx.Diagnostics);
    }

    [Fact]
    public void Parse_attribute()
    {
        var ctx = MakeCtx("@", Ident("impl"), "(", TypeId("Show"), ")",
            "func", Ident("show"), ":", TypeId("Self"), "->", TypeId("String"));
        var parser = new DeclParser(ctx);
        var result = parser.ParseTopLevel();
        var func = Assert.IsType<FuncDef>(result);
        Assert.Single(func.Attributes);
        Assert.Equal("impl", func.Attributes[0].Name);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_program_multiple_decls()
    {
        var ctx = MakeCtx(
            "let", Ident("x"), "=", Num("1"), ";",
            "let", Ident("y"), "=", Num("2"), ";");
        var parser = new DeclParser(ctx);
        var nodes = parser.ParseProgram();
        Assert.Equal(2, nodes.Count);
        Assert.IsType<LetDecl>(nodes[0]);
        Assert.IsType<LetDecl>(nodes[1]);
        Assert.Empty(ctx.Diagnostics);
    }

    #region Helpers

    private static ParserContext MakeCtx(params object[] tokenSpecs)
    {
        return MakeCtxWithSyntax(EidosLanguageVersions.Legacy, tokenSpecs);
    }

    private static ParserContext MakeNameFirstCtx(params object[] tokenSpecs)
    {
        return MakeCtxWithSyntax(EidosLanguageVersions.Current, tokenSpecs);
    }

    private static ParserContext MakeCtxWithSyntax(string languageVersion, params object[] tokenSpecs)
    {
        var tokens = new List<Token>();
        foreach (var spec in tokenSpecs)
        {
            switch (spec)
            {
                case string s:
                    tokens.Add(new PlainToken(s));
                    break;
                case Token t:
                    tokens.Add(t);
                    break;
            }
        }
        tokens.Add(new EofToken(new SourceLocation(tokens.Count, 0, 0)));
        return new ParserContext(tokens, "test", languageVersion);
    }

    private static Token Ident(string name)
        => new DebugNameToken(name, "identifier");

    private static Token TypeId(string name)
        => new DebugNameToken(name, "typeIdentifier");

    private static Token Num(string text)
        => new DebugNameToken(text, "numberLiteral");

    private static Token Str(string text)
        => new DebugNameToken(text, "stringLiteral");

    private sealed class PlainToken(string text) : ContentToken(
        new SourceLocation(0, 0, 0),
        SyntaxKind.None,
        new Terminal(0, text, TerminalFlag.None),
        text.GetOrIntern(), text.Length, text);

    private static SyntaxKind DebugNameToKind(string debugName) => debugName switch
    {
        "identifier" => SyntaxKind.Identifier,
        "typeIdentifier" => SyntaxKind.TypeIdentifier,
        "operatorIdentifier" => SyntaxKind.OperatorIdentifier,
        "numberLiteral" => SyntaxKind.NumberLiteral,
        "stringLiteral" => SyntaxKind.StringLiteral,
        "charLiteral" => SyntaxKind.CharLiteral,
        "booleanLiteral" => SyntaxKind.BooleanLiteral,
        _ => SyntaxKindHelper.TryFromText(debugName, out var k) ? k : SyntaxKind.None
    };

    private sealed class DebugNameToken(string text, string debugName) : ContentToken(
        new SourceLocation(0, 0, 0),
        DebugNameToKind(debugName),
        new Terminal(0, debugName, TerminalFlag.None),
        text.GetOrIntern(), text.Length, text);

    #endregion
}
