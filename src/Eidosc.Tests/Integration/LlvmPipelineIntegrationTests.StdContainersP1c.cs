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
        keys := TreeMap.keys(map1)
        folded := TreeMap.fold(map1)(0)(acc => key => value => acc + key * 100 + value)
        if TreeMap.len(map1) == 2 &&
           TreeMap.get_or(map1)(3)(0) == 300 &&
           keys[0] == 2 &&
           keys[1] == 3 &&
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
        xs := TreeSet.to_seq(set1)
        if TreeSet.len(set1) == 3 &&
           TreeSet.contains(set1)(4) &&
           xs[0] == 1 &&
           xs[1] == 2 &&
           xs[2] == 4
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
        if TreeMap.len(sorted) == 15 &&
           TreeMap.height(sorted) <= 5 &&
           TreeMap.keys(sorted)[0] == 1 &&
           TreeMap.keys(sorted)[14] == 15
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
