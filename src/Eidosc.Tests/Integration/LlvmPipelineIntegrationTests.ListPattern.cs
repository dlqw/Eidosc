using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    public void ListPattern_TupleElementsFromVecFreeze_NativeSmoke_IndexAndMatchStayStable()
    {
        const string source = """
import Std::SeqBuilder

make_edges :: Int -> Seq[(Int, Int)]
{
    from => {
        mut edges := SeqBuilder::with_capacity[(Int, Int)](4);
        mut offset := 1;
        loop {
            if offset > 4 then { break } else {
                edges := SeqBuilder::push(edges, (from + offset, offset * 10));
                offset := offset + 1
            }
        };
        SeqBuilder::freeze(edges)
    }
}

build_adjacency :: Unit -> SeqBuilder[Seq[(Int, Int)]]
{
    _ => {
        mut adjacency := SeqBuilder::with_capacity[Seq[(Int, Int)]](3);
        mut node := 0;
        loop {
            if node >= 3 then { break } else {
                adjacency := SeqBuilder::push(adjacency, make_edges(node));
                node := node + 1
            }
        };
        adjacency
    }
}

sum_by_destructure :: Seq[(Int, Int)] -> Int
{
    edges => {
        mut total := 0;
        mut index := 0;
        loop {
            if index >= Seq::len(edges) then { break } else {
                (to, weight) := edges[index];
                total := total + to + weight;
                index := index + 1
            }
        };
        total
    }
}

sum_by_match :: Seq[(Int, Int)] -> Int
{
    edges => {
        mut total := 0;
        mut index := 0;
        loop {
            if index >= Seq::len(edges) then { break } else {
                match edges[index] {
                    (to, weight) => {
                        total := total + to + weight;
                        index := index + 1
                    }
                }
            }
        };
        total
    }
}

main :: Unit -> Int
{
    _ => {
        adjacency := build_adjacency({});
        first := sum_by_destructure(SeqBuilder::get(adjacency, 0));
        second := sum_by_match(SeqBuilder::get(adjacency, 1));
        if first == 110 && second == 114 then { 0 } else { 99 }
    }
}
""";

        var execution = CompileAndRunSourceAtNative(
            source,
            "native_list_tuple_vec_freeze_index_match.eidos",
            "native_list_tuple_vec_freeze_index_match");

        Assert.Equal(0, execution.ExitCode);
    }

    [Fact]
    public void ListPattern_NestedAdtConstructor_NativeSmoke_MatchesElementFieldsAndRest()
    {
        const string source = """
Tok :: type {
    TkKeyword(String) | TkIdent(String) | TkEof
}

classify :: Seq[Tok] -> Int
{
    [TkKeyword("int"), TkIdent(name), ..rest] => 10 + Seq::len(rest),
    [TkKeyword("return"), ..rest] => 20 + Seq::len(rest),
    [TkIdent(name), ..rest] => 30 + Seq::len(rest),
    [TkEof(), ..rest] => 40 + Seq::len(rest),
    [] => 0
}

main :: Unit -> Int
{
    _ => {
        first := classify([TkKeyword("int"), TkIdent("x"), TkEof()]);
        second := classify([TkKeyword("return"), TkIdent("y")]);
        third := classify([TkIdent("z")]);
        if first == 11 && second == 21 && third == 30 then { 0 } else { 99 }
    }
}
""";

        var execution = CompileAndRunSourceAtNative(
            source,
            "native_nested_list_adt_constructor_pattern.eidos",
            "native_nested_list_adt_constructor_pattern");

        Assert.Equal(0, execution.ExitCode);
    }

    [Fact]
    public void ListPattern_NestedAdtConstructor_NativeSmoke_CanRebuildMatchedRestWithPrefix()
    {
        const string source = """
Tok :: type {
    TkKeyword(String) | TkIdent(String) | TkEof
}

reclassify :: Seq[Tok] -> Int
{
    [TkKeyword("int"), ..rest] => match [TkKeyword("int"), ..rest]
    {
        [TkKeyword("int"), TkIdent("x"), TkEof(), .._] => 0,
        [TkKeyword("int"), TkIdent(_), .._] => 11,
        _ => 12
    },
    _ => 13
}

main :: Unit -> Int
{
    _ => reclassify([TkKeyword("int"), TkIdent("x"), TkEof()])
}
""";

        var execution = CompileAndRunSourceAtNative(
            source,
            "native_nested_list_adt_constructor_rest_rebuild.eidos",
            "native_nested_list_adt_constructor_rest_rebuild");

        Assert.Equal(0, execution.ExitCode);
    }

    [Fact]
    public void ListPattern_MiddleRestAndSuffix_NativeSmoke_MatchesAndBindsMiddleSlice()
    {
        const string source = """
score :: Seq[Int] -> Int
{
    [head, ..middle, last] => head * 100 + Seq::len(middle) * 10 + last,
    _ => 0
}

score_init :: Seq[Int] -> Int
{
    [..init, last] => Seq::len(init) * 10 + last,
    _ => 0
}

score_bare :: Seq[Int] -> Int
{
    [first, .., last] => first * 10 + last,
    _ => 0
}

main :: Unit -> Int
{
    _ => {
        a := score([2, 4, 6, 8, 9]);
        b := score_init([1, 2, 3, 7]);
        c := score_bare([5, 6, 7, 8]);
        if a == 239 && b == 37 && c == 58 then { 0 } else { 99 }
    }
}
""";

        var execution = CompileAndRunSourceAtNative(
            source,
            "native_list_middle_rest_suffix_pattern.eidos",
            "native_list_middle_rest_suffix_pattern");

        Assert.Equal(0, execution.ExitCode);
    }
}
