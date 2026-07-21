using Eidosc.Semantic;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void StdPersistentMapSet_NativeSmoke_PreserveVersionsAndOrdering()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
import std.Seq
import std.Option
import std.PersistentMap
import std.PersistentSet

check_map :: Unit -> Int
{
    _ => {
        base := PersistentMap.empty[Int, Int](())
        one := PersistentMap.insert(ref base)(2)(20)
        two := PersistentMap.insert(ref one)(1)(10)
        three := PersistentMap.insert(ref two)(3)(30)
        four := PersistentMap.insert(ref three)(4)(40)
        overwritten := PersistentMap.insert(ref four)(2)(22)
        removedLeaf := PersistentMap.remove(ref overwritten)(4)
        removedRoot := PersistentMap.remove(ref overwritten)(2)
        removedMissing := PersistentMap.remove(ref overwritten)(99)
        oldTwo := PersistentMap.get_or(ref two)(2)(0)
        oldOne := PersistentMap.get_or(ref two)(1)(0)
        newTwo := PersistentMap.get_or(ref overwritten)(2)(0)
        oldMissingFour := PersistentMap.contains_key(ref two)(4)
        missingLeaf := PersistentMap.contains_key(ref removedLeaf)(4)
        missingRoot := PersistentMap.contains_key(ref removedRoot)(2)
        ordered := PersistentMap.keys(ref overwritten)
        orderedForSum := Seq.clone(ref ordered)
        removedRootOrdered := PersistentMap.keys(ref removedRoot)
        unchangedByMissingRemove := PersistentMap.keys(ref removedMissing)
        orderOk := match ordered {
            [1, 2, 3, 4] => true,
            _ => false
        }
        rootRemoveOrderOk := match removedRootOrdered {
            [1, 3, 4] => true,
            _ => false
        }
        missingRemoveOrderOk := match unchangedByMissingRemove {
            [1, 2, 3, 4] => true,
            _ => false
        }
        if oldTwo == 20 &&
           oldOne == 10 &&
           newTwo == 22 &&
           !oldMissingFour &&
           !missingLeaf &&
           !missingRoot &&
           orderOk &&
           rootRemoveOrderOk &&
           missingRemoveOrderOk &&
           Seq.sum(orderedForSum) == 10
        then { 21 } else { 1 }
    }
}

check_set :: Unit -> Int
{
    _ => {
        s0 := PersistentSet.empty[Int](())
        withThree := PersistentSet.insert(ref s0)(3)
        s1 := PersistentSet.insert(ref withThree)(1)
        s2 := PersistentSet.remove(ref s1)(3)
        ordered := match PersistentSet.to_seq(ref s1) {
            [1, 3] => true,
            _ => false
        }
        if PersistentSet.contains(ref s1)(3) &&
           !PersistentSet.contains(ref s2)(3) &&
           PersistentSet.contains(ref s2)(1) &&
           ordered &&
           Seq.sum(PersistentSet.to_seq(ref s1)) == 4
        then { 21 } else { 2 }
    }
}

main :: Unit -> Int
{
    _ => check_map(()) + check_set(())
}
""";

        var execution = CompileAndRunSourceAtNativeWithContext(
            source,
            StdlibListImportInputFile(),
            "std_persistent_containers_native_smoke");

        Assert.Equal(42, execution.ExitCode);
    }

    [Fact]
    public void StdPersistentMap_Source_UsesSharedNodesWithoutTreeMapClone()
    {
        Assert.True(PrecompiledModuleRegistry.TryGetSource("std/PersistentMap", out var source));
        Assert.Contains("Shared.Shared[Node[K, V]]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("tree_map.clone", source, StringComparison.Ordinal);
    }
}
