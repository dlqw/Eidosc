using Eidosc.Ast.Patterns;
using Eidosc.Parsing.Handwritten;
using Eidosc.Utilities;
using Eidosc.Utils;

namespace Eidosc.Tests.Unit.Parsing.Handwritten;

public sealed class PatternParserTests
{
    [Fact]
    public void Parse_wildcard()
    {
        var ctx = MakeCtx("_");
        var parser = new PatternParser(ctx);
        var result = parser.ParsePattern();
        Assert.IsType<WildcardPattern>(result);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_var_binding()
    {
        var ctx = MakeCtx(Ident("x"));
        var parser = new PatternParser(ctx);
        var result = parser.ParsePattern();
        var varPat = Assert.IsType<VarPattern>(result);
        Assert.Equal("x", varPat.Name);
        Assert.Equal(PatternBindingMode.ByValue, varPat.BindingMode);
        Assert.False(varPat.IsMutableBinding);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_mut_var_binding_marks_by_value_binding_mutable()
    {
        var ctx = MakeCtx("mut", Ident("state"));
        var parser = new PatternParser(ctx);

        var result = parser.ParsePattern();

        var varPat = Assert.IsType<VarPattern>(result);
        Assert.Equal("state", varPat.Name);
        Assert.Equal(PatternBindingMode.ByValue, varPat.BindingMode);
        Assert.True(varPat.IsMutableBinding);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_mut_wildcard_binding_reports_error()
    {
        var ctx = MakeCtx("mut", "_");
        var parser = new PatternParser(ctx);

        var result = parser.ParsePattern();

        Assert.IsType<WildcardPattern>(result);
        Assert.Contains(
            ctx.Diagnostics,
            diagnostic => diagnostic.Message.Contains("mutable wildcard binding is not allowed", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_lowercase_call_like_pattern_reports_syntax_error()
    {
        var ctx = MakeCtx(Ident("view"), "(", Ident("x"), "->", Num("1"), ")", "=>");
        var parser = new PatternParser(ctx);

        var result = parser.ParsePattern();

        Assert.IsType<WildcardPattern>(result);
        Assert.Equal("=>", ctx.GetText());
        Assert.Contains(ctx.Diagnostics, diagnostic => diagnostic.Code == "E4001");
        Assert.DoesNotContain(
            ctx.Diagnostics.SelectMany(diagnostic => diagnostic.Helps),
            help => help.Contains("native view pattern", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_ref_binding()
    {
        var ctx = MakeCtx("ref", Ident("x"));
        var parser = new PatternParser(ctx);
        var result = parser.ParsePattern();
        var varPat = Assert.IsType<VarPattern>(result);
        Assert.Equal("x", varPat.Name);
        Assert.Equal(PatternBindingMode.SharedBorrow, varPat.BindingMode);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_mref_binding()
    {
        var ctx = MakeCtx("mref", Ident("v"));
        var parser = new PatternParser(ctx);
        var result = parser.ParsePattern();
        var varPat = Assert.IsType<VarPattern>(result);
        Assert.Equal("v", varPat.Name);
        Assert.Equal(PatternBindingMode.MutableBorrow, varPat.BindingMode);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_literal_int()
    {
        var ctx = MakeCtx(Num("42"));
        var parser = new PatternParser(ctx);
        var result = parser.ParsePattern();
        var lit = Assert.IsType<LiteralPattern>(result);
        Assert.Equal(42L, lit.Value);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_literal_string()
    {
        var ctx = MakeCtx(Str("\"hello\""));
        var parser = new PatternParser(ctx);
        var result = parser.ParsePattern();
        var lit = Assert.IsType<LiteralPattern>(result);
        Assert.Equal("hello", lit.Value);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_bool_true()
    {
        var ctx = MakeCtx("true");
        var parser = new PatternParser(ctx);
        var result = parser.ParsePattern();
        var lit = Assert.IsType<LiteralPattern>(result);
        Assert.Equal(true, lit.Value);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_tuple_pattern()
    {
        var ctx = MakeCtx("(", Ident("x"), ",", Ident("y"), ")");
        var parser = new PatternParser(ctx);
        var result = parser.ParsePattern();
        var tuple = Assert.IsType<TuplePattern>(result);
        Assert.Equal(2, tuple.Elements.Count);
        Assert.IsType<VarPattern>(tuple.Elements[0]);
        Assert.IsType<VarPattern>(tuple.Elements[1]);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_unit_tuple()
    {
        var ctx = MakeCtx("(", ")");
        var parser = new PatternParser(ctx);
        var result = parser.ParsePattern();
        var tuple = Assert.IsType<TuplePattern>(result);
        Assert.Empty(tuple.Elements);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_ctor_positional()
    {
        var ctx = MakeCtx(TypeId("Some"), "(", Ident("x"), ")");
        var parser = new PatternParser(ctx);
        var result = parser.ParsePattern();
        var ctor = Assert.IsType<CtorPattern>(result);
        Assert.Equal("Some", ctor.ConstructorName);
        Assert.Single(ctor.PositionalPatterns);
        Assert.Empty(ctor.NamedPatterns);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_ctor_named_fields()
    {
        var ctx = MakeCtx(TypeId("Person"), "{", Ident("name"), ":", Ident("n"), ",", Ident("age"), ":", "_", "}");
        var parser = new PatternParser(ctx);
        var result = parser.ParsePattern();
        var ctor = Assert.IsType<CtorPattern>(result);
        Assert.Equal("Person", ctor.ConstructorName);
        Assert.Equal(2, ctor.NamedPatterns.Count);
        Assert.Equal("name", ctor.NamedPatterns[0].FieldName);
        Assert.Equal("age", ctor.NamedPatterns[1].FieldName);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_ctor_named_shorthand()
    {
        var ctx = MakeCtx(TypeId("Pair"), "{", Ident("fst"), ",", Ident("snd"), "}");
        var parser = new PatternParser(ctx);
        var result = parser.ParsePattern();
        var ctor = Assert.IsType<CtorPattern>(result);
        Assert.Equal("Pair", ctor.ConstructorName);
        Assert.Equal(2, ctor.NamedPatterns.Count);
        Assert.True(ctor.NamedPatterns[0].IsShorthand);
        Assert.Equal("fst", ctor.NamedPatterns[0].FieldName);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_ctor_named_fields_with_record_rest()
    {
        var ctx = MakeCtx(TypeId("GameState"), "{", Ident("dir"), ":", Ident("d"), ",", "..", "}");
        var parser = new PatternParser(ctx);

        var result = parser.ParsePattern();

        var ctor = Assert.IsType<CtorPattern>(result);
        Assert.Equal("GameState", ctor.ConstructorName);
        Assert.True(ctor.HasRecordRest);
        Assert.Single(ctor.NamedPatterns);
        Assert.Equal("dir", ctor.NamedPatterns[0].FieldName);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_ctor_empty()
    {
        var ctx = MakeCtx(TypeId("None"), "(", ")");
        var parser = new PatternParser(ctx);
        var result = parser.ParsePattern();
        var ctor = Assert.IsType<CtorPattern>(result);
        Assert.Equal("None", ctor.ConstructorName);
        Assert.Empty(ctor.PositionalPatterns);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_ctor_module_path()
    {
        var ctx = MakeCtx(TypeId("Option"), "::", TypeId("Some"), "(", Ident("v"), ")");
        var parser = new PatternParser(ctx);
        var result = parser.ParsePattern();
        var ctor = Assert.IsType<CtorPattern>(result);
        Assert.Equal("Some", ctor.ConstructorName);
        Assert.Equal(["Option"], ctor.ModulePath);
        Assert.Single(ctor.PositionalPatterns);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_list_pattern()
    {
        var ctx = MakeCtx("[", Ident("x"), ",", Ident("y"), "]");
        var parser = new PatternParser(ctx);
        var result = parser.ParsePattern();
        var list = Assert.IsType<ListPattern>(result);
        Assert.Equal(2, list.Elements.Count);
        Assert.False(list.HasRestMarker);
        Assert.Null(list.RestPattern);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_list_pattern_with_rest()
    {
        var ctx = MakeCtx("[", Ident("head"), ",", "..", Ident("tail"), "]");
        var parser = new PatternParser(ctx);
        var result = parser.ParsePattern();
        var list = Assert.IsType<ListPattern>(result);
        Assert.Single(list.Elements);
        Assert.True(list.HasRestMarker);
        Assert.NotNull(list.RestPattern);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_list_pattern_with_middle_rest_and_suffix()
    {
        var ctx = MakeCtx("[", Ident("head"), ",", "..", Ident("middle"), ",", Ident("last"), "]");
        var parser = new PatternParser(ctx);
        var result = parser.ParsePattern();
        var list = Assert.IsType<ListPattern>(result);
        Assert.Single(list.Elements);
        Assert.True(list.HasRestMarker);
        var rest = Assert.IsType<VarPattern>(list.RestPattern);
        Assert.Equal("middle", rest.Name);
        var suffix = Assert.Single(list.SuffixElements);
        var last = Assert.IsType<VarPattern>(suffix);
        Assert.Equal("last", last.Name);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_list_pattern_with_bare_middle_rest_and_suffix()
    {
        var ctx = MakeCtx("[", Ident("first"), ",", "..", ",", Ident("last"), "]");
        var parser = new PatternParser(ctx);
        var result = parser.ParsePattern();
        var list = Assert.IsType<ListPattern>(result);
        Assert.Single(list.Elements);
        Assert.True(list.HasRestMarker);
        Assert.Null(list.RestPattern);
        Assert.Single(list.SuffixElements);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_list_pattern_with_duplicate_rest_reports_diagnostic()
    {
        var ctx = MakeCtx("[", Ident("first"), ",", "..", Ident("middle"), ",", "..", Ident("last"), "]");
        var parser = new PatternParser(ctx);
        _ = parser.ParsePattern();
        Assert.Contains(ctx.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Parse_list_pattern_with_complex_rest_reports_diagnostic()
    {
        var ctx = MakeCtx("[", "..", TypeId("Some"), "(", Ident("value"), ")", "]");
        var parser = new PatternParser(ctx);
        _ = parser.ParsePattern();
        Assert.Contains(ctx.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Parse_list_empty()
    {
        var ctx = MakeCtx("[", "]");
        var parser = new PatternParser(ctx);
        var result = parser.ParsePattern();
        var list = Assert.IsType<ListPattern>(result);
        Assert.Empty(list.Elements);
        Assert.False(list.HasRestMarker);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_or_pattern()
    {
        var ctx = MakeCtx(Ident("x"), "|", Ident("y"));
        var parser = new PatternParser(ctx);
        var result = parser.ParsePattern();
        var or = Assert.IsType<OrPattern>(result);
        Assert.Equal(2, or.Alternatives.Count);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_or_pattern_multiple()
    {
        var ctx = MakeCtx(Ident("a"), "|", Ident("b"), "|", Ident("c"));
        var parser = new PatternParser(ctx);
        var result = parser.ParsePattern();
        var or = Assert.IsType<OrPattern>(result);
        Assert.Equal(3, or.Alternatives.Count);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_or_pattern_missing_rhs_does_not_consume_branch_arrow()
    {
        var ctx = MakeCtx(Num("1"), "|", "=>");
        var parser = new PatternParser(ctx);

        var result = parser.ParsePattern();

        var or = Assert.IsType<OrPattern>(result);
        Assert.Single(or.Alternatives);
        Assert.Equal("=>", ctx.GetText());
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_and_pattern()
    {
        var ctx = MakeCtx(Ident("x"), "&", Ident("y"));
        var parser = new PatternParser(ctx);
        var result = parser.ParsePattern();
        var and = Assert.IsType<AndPattern>(result);
        Assert.Equal(2, and.Conjuncts.Count);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_and_pattern_missing_rhs_does_not_consume_branch_arrow()
    {
        var ctx = MakeCtx(Num("1"), "&", "=>");
        var parser = new PatternParser(ctx);

        var result = parser.ParsePattern();

        var and = Assert.IsType<AndPattern>(result);
        Assert.Single(and.Conjuncts);
        Assert.Equal("=>", ctx.GetText());
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_not_pattern()
    {
        var ctx = MakeCtx("!", TypeId("None"), "(", ")");
        var parser = new PatternParser(ctx);
        var result = parser.ParsePattern();
        var not = Assert.IsType<NotPattern>(result);
        Assert.NotNull(not.InnerPattern);
        Assert.IsType<CtorPattern>(not.InnerPattern);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_not_pattern_missing_inner_does_not_consume_branch_arrow()
    {
        var ctx = MakeCtx("!", "=>");
        var parser = new PatternParser(ctx);

        var result = parser.ParsePattern();

        var not = Assert.IsType<NotPattern>(result);
        Assert.Null(not.InnerPattern);
        Assert.Equal("=>", ctx.GetText());
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_as_pattern()
    {
        var ctx = MakeCtx("(", Ident("x"), ",", Ident("y"), ")", "as", Ident("point"));
        var parser = new PatternParser(ctx);
        var result = parser.ParsePattern();
        var asPat = Assert.IsType<AsPattern>(result);
        Assert.Equal("point", asPat.BindingName);
        Assert.Equal(PatternBindingMode.ByValue, asPat.BindingMode);
        Assert.NotNull(asPat.InnerPattern);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_as_pattern_missing_binding_name_does_not_consume_branch_arrow()
    {
        var ctx = MakeCtx(Num("1"), "as", "=>");
        var parser = new PatternParser(ctx);

        var result = parser.ParsePattern();

        var asPat = Assert.IsType<AsPattern>(result);
        Assert.Equal("", asPat.BindingName);
        Assert.NotNull(asPat.InnerPattern);
        Assert.Equal("=>", ctx.GetText());
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_as_pattern_ref()
    {
        var ctx = MakeCtx(Ident("x"), "as", "ref", Ident("r"));
        var parser = new PatternParser(ctx);
        var result = parser.ParsePattern();
        var asPat = Assert.IsType<AsPattern>(result);
        Assert.Equal("r", asPat.BindingName);
        Assert.Equal(PatternBindingMode.SharedBorrow, asPat.BindingMode);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_range_pattern()
    {
        var ctx = MakeCtx(Num("1"), "..", Num("10"));
        var parser = new PatternParser(ctx);
        var result = parser.ParsePattern();
        var range = Assert.IsType<RangePattern>(result);
        Assert.NotNull(range.Start);
        Assert.NotNull(range.End);
        Assert.Equal(1L, range.Start.Value);
        Assert.Equal(10L, range.End.Value);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_range_pattern_missing_end_does_not_consume_branch_arrow()
    {
        var ctx = MakeCtx(Num("1"), "..", "=>");
        var parser = new PatternParser(ctx);

        var result = parser.ParsePattern();

        var range = Assert.IsType<RangePattern>(result);
        Assert.NotNull(range.Start);
        Assert.Null(range.End);
        Assert.Equal("=>", ctx.GetText());
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_nested_ctor_in_tuple()
    {
        // (Some(x), None())
        var ctx = MakeCtx("(", TypeId("Some"), "(", Ident("x"), ")", ",", TypeId("None"), "(", ")", ")");
        var parser = new PatternParser(ctx);
        var result = parser.ParsePattern();
        var tuple = Assert.IsType<TuplePattern>(result);
        Assert.Equal(2, tuple.Elements.Count);
        Assert.IsType<CtorPattern>(tuple.Elements[0]);
        Assert.IsType<CtorPattern>(tuple.Elements[1]);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_complex_or_of_ctors()
    {
        // Some(x) | None()
        var ctx = MakeCtx(TypeId("Some"), "(", Ident("x"), ")", "|", TypeId("None"), "(", ")");
        var parser = new PatternParser(ctx);
        var result = parser.ParsePattern();
        var or = Assert.IsType<OrPattern>(result);
        Assert.Equal(2, or.Alternatives.Count);
        Assert.IsType<CtorPattern>(or.Alternatives[0]);
        Assert.IsType<CtorPattern>(or.Alternatives[1]);
        Assert.Empty(ctx.Diagnostics);
    }

    #region Helpers

    private static ParserContext MakeCtx(params object[] tokenSpecs)
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
        return new ParserContext(tokens, "test");
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
