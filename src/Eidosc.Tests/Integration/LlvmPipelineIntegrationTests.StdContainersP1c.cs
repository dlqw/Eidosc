using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void StdTreeMapTreeSet_IterateInKeyOrder()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
import std.Seq
import std.Option
import std.TreeMap
import std.TreeSet

check_map :: Unit -> Int
{
    _ => {
        map0 := TreeMap.from_seq[Int, Int]([(3, 30), (1, 10), (2, 20), (3, 300)])
        map1 := TreeMap.remove(map0)(1)
        map_for_keys := TreeMap.remove(
            TreeMap.from_seq[Int, Int]([(3, 30), (1, 10), (2, 20), (3, 300)]))(1)
        map_for_fold := TreeMap.remove(
            TreeMap.from_seq[Int, Int]([(3, 30), (1, 10), (2, 20), (3, 300)]))(1)
        keys := TreeMap.keys(map_for_keys)
        keys_ok := match keys
        {
            [first, second] => first == 2 && second == 3,
            _ => false
        }
        folded := TreeMap.fold(map_for_fold)(0)(acc => key => value => acc + key * 100 + value)
        if TreeMap.len(ref map1) == 2 &&
           TreeMap.get_or(map1)(3)(0) == 300 &&
           keys_ok &&
           folded == 820
        then { 20 }
        else { 1 }
    }
}

check_set :: Unit -> Int
{
    _ => {
        set0 := TreeSet.from_seq[Int]([4, 1, 3, 1, 2])
        set1 := TreeSet.remove(set0)(3)
        set_len := TreeSet.len(ref set1)
        contains_four := TreeSet.contains(
            TreeSet.remove(TreeSet.from_seq[Int]([4, 1, 3, 1, 2]))(3))(4)
        xs := TreeSet.to_seq(set1)
        ordered := match xs
        {
            [first, second, third] => first == 1 && second == 2 && third == 4,
            _ => false
        }
        if set_len == 3 && contains_four && ordered
        then { 22 }
        else { 2 }
    }
}

check_balancing :: Unit -> Int
{
    _ => {
        sorted := TreeMap.from_seq[Int, Int]([
            (1, 1), (2, 2), (3, 3), (4, 4), (5, 5),
            (6, 6), (7, 7), (8, 8), (9, 9), (10, 10),
            (11, 11), (12, 12), (13, 13), (14, 14), (15, 15)
        ])
        size := TreeMap.len(ref sorted);
        tree_height := TreeMap.height(ref sorted);
        keys := TreeMap.keys(sorted);
        keys_ok := match keys
        {
            [first, ..rest] => first == 1 && Seq.last_or(rest)(0) == 15,
            _ => false
        }
        if size == 15 && tree_height <= 5 && keys_ok
        then { 8 }
        else { 3 }
    }
}

main :: Unit -> Int
{
    _ => check_map(()) + check_set(()) + check_balancing(())
}
""";

        var execution = CompileAndRunSourceAtNativeWithContext(
            source,
            StdlibListImportInputFile(),
            "std_ordered_tree_p1");

        Assert.Equal(50, execution.ExitCode);
    }
}
