using Eidosc.CodeFormatting;
using Eidosc.ProjectSystem;

namespace Eidosc.Tests.Unit.Formatting;

public sealed class EidosFormatterTests
{
    [Fact]
    public void Format_FunctionBranches_UsesBlockIndentation()
    {
        const string source = "inc :: Int->Int{x=>x+1,}";

        var result = EidosFormatter.Format(source, options: NoValidation());

        Assert.True(result.Success);
        Assert.Equal(
            """
            inc :: Int -> Int {
                x => x + 1,
            }

            """.ReplaceLineEndings("\n"),
            result.FormattedText.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Format_Pipeline_AddsOperatorSpacing()
    {
        const string source = "result :: items|>Seq.map({x=>x+1})|>Seq.count;";

        var result = EidosFormatter.Format(source, options: NoValidation());

        Assert.True(result.Success);
        Assert.Equal(
            """
            result::items |> Seq.map({
                x => x + 1
            }) |> Seq.count;

            """.ReplaceLineEndings("\n"),
            result.FormattedText.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Format_SimpleIfExpression_CompactsBranchesAndSpacesThenElse()
    {
        const string source = "next :: if new_dir==opposite(current)then {current}else {new_dir};";

        var result = EidosFormatter.Format(source, options: NoValidation());

        Assert.True(result.Success);
        Assert.Equal(
            """
            next::if new_dir == opposite(current) then current else new_dir;

            """.ReplaceLineEndings("\n"),
            result.FormattedText.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Format_OptionSugar_KeepsSuffixTightAndSpacesCoalesce()
    {
        const string source = "next: Int? := value??fallback;";

        var result = EidosFormatter.Format(source, options: NoValidation());

        Assert.True(result.Success);
        Assert.Equal(
            """
            next: Int? := value ?? fallback;

            """.ReplaceLineEndings("\n"),
            result.FormattedText.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Format_MutableLetAndAssignment_AddsExpectedSpacing()
    {
        const string source = "counter :: Int = 0;counter:=counter+1;";

        var result = EidosFormatter.Format(source, options: NoValidation());

        Assert.True(result.Success);
        Assert.Equal(
            """
            counter :: Int = 0;
            counter := counter + 1;

            """.ReplaceLineEndings("\n"),
            result.FormattedText.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Format_MultiStatementIfExpression_CompactsSimpleElseBranch()
    {
        const string source = "next :: if ok then {x := 1;x}else {fallback};";

        var result = EidosFormatter.Format(source, options: NoValidation());

        Assert.True(result.Success);
        Assert.Equal(
            """
            next::if ok
            then
            {
                x := 1;
                x
            }
            else fallback;

            """.ReplaceLineEndings("\n"),
            result.FormattedText.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Format_StandaloneThenBlock_ExpandsThenBraceAndCompactsSimpleElseBranch()
    {
        const string source =
            """
            next::if ok
            then
            {
                x := 1;
                x
            }
            else { fallback };
            """;

        var result = EidosFormatter.Format(source, options: NoValidation());

        Assert.True(result.Success);
        Assert.Equal(
            """
            next::if ok
            then
            {
                x := 1;
                x
            }
            else fallback;

            """.ReplaceLineEndings("\n"),
            result.FormattedText.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Format_MultiStatementElseBranch_CompactsSimpleThenBranch()
    {
        const string source = "next :: if ok then {ready}else {x := 1;x};";

        var result = EidosFormatter.Format(source, options: NoValidation());

        Assert.True(result.Success);
        Assert.Equal(
            """
            next::if ok then ready
            else
            {
                x := 1;
                x
            };

            """.ReplaceLineEndings("\n"),
            result.FormattedText.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Format_EmptyIfBranch_KeepsEmptyBlockCompact()
    {
        const string source = "n=>c=>if n<=0 then{}else{print_char(c);print_chars(n-1)(c)}";

        var result = EidosFormatter.Format(source, options: NoValidation());

        Assert.True(result.Success);
        Assert.Equal(
            """
            n => c => if n <= 0 then {}
            else
            {
                print_char(c);
                print_chars(n - 1)(c)
            }

            """.ReplaceLineEndings("\n"),
            result.FormattedText.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Format_Comments_PreservesLineComments()
    {
        const string source = "// title\nNumeric :: trait {add :: Int->Int->Int}";

        var result = EidosFormatter.Format(source, options: NoValidation());

        Assert.True(result.Success);
        Assert.Equal(
            """
            // title
            Numeric :: trait {
                add :: Int -> Int -> Int
            }

            """.ReplaceLineEndings("\n"),
            result.FormattedText.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Format_Imports_PreservesLineBoundaries()
    {
        const string source = "import Std.Option\nimport Std.Seq\nDirection :: type {North,South}";

        var result = EidosFormatter.Format(source, options: NoValidation());

        Assert.True(result.Success);
        Assert.Equal(
            """
            import Std.Option
            import Std.Seq
            Direction :: type {
                North,
                South
            }

            """.ReplaceLineEndings("\n"),
            result.FormattedText.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Format_CurriedConstructorPatternBranches_KeepsOutputParseable()
    {
        const string source =
            """
            Direction :: type { North, South }
            same :: Direction -> Direction -> Bool {
            North() => North() => true,
            South() => South() => true,
            _ => _ => false
            }
            """;

        var result = EidosFormatter.Format(source, options: DefaultValidation());

        Assert.True(result.Success);
        Assert.Contains("North() => North() => true", result.FormattedText);
        Assert.DoesNotContain("N North())North() = =>", result.FormattedText);
    }

    [Fact]
    public void Format_CurriedSimpleBranch_KeepsOutputParseable()
    {
        const string source =
            """
            Pos :: type { Pos { x: Int, y: Int } }
            make_pos :: Int -> Int -> Pos {
            x => y => Pos { x: x, y: y }
            }
            """;

        var result = EidosFormatter.Format(source, options: DefaultValidation());

        Assert.True(result.Success);
        Assert.Contains("x => y => Pos", result.FormattedText);
        Assert.DoesNotContain("x x x y = =>", result.FormattedText);
    }

    [Fact]
    public void Format_CurriedListPatternBranch_KeepsOutputParseable()
    {
        const string source =
            """
            draw_snake :: Seq[Int] -> Int -> Int -> Int -> Unit {
            [] => _ => _ => _ => (),
            [head, ..rest] => left => top => cell => {
            head.draw_cell(left, top, cell, 20, 135, 88);
            rest.draw_snake(left, top, cell)
            }
            }
            """;

        var result = EidosFormatter.Format(source, options: DefaultValidation());

        Assert.True(result.Success);
        Assert.Contains("[head, ..rest] => left => top => cell =>", result.FormattedText);
        Assert.DoesNotContain("[[head, ..rest]]left l top t cell = =>", result.FormattedText);
    }

    [Fact]
    public void Format_ValidatesSyntax_WhenEnabled()
    {
        const string source = "inc :: Int -> Int { x => x + 1 }";

        var result = EidosFormatter.Format(source);

        Assert.True(result.Success);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Format_NameFirstLanguageVersion_Validates2026Q31Source()
    {
        const string source = "Main :: module { main :: Unit -> Int { _ => 0 } }";

        var result = EidosFormatter.Format(source, options: NameFirstValidation());

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.Contains("Main :: module", result.FormattedText, StringComparison.Ordinal);
        Assert.Empty(result.Diagnostics);
    }

    private static EidosFormatterOptions NoValidation() => new()
    {
        ValidateSyntax = false,
        NewLine = "\n"
    };

    private static EidosFormatterOptions DefaultValidation() => new()
    {
        NewLine = "\n"
    };

    private static EidosFormatterOptions NameFirstValidation() => new()
    {
        LanguageVersion = EidosLanguageVersions.Current,
        NewLine = "\n"
    };
}
