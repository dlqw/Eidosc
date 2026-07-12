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
import Std::Seq
import Std::Option
import Std::PersistentMap
import Std::PersistentSet

check_map :: Unit -> Int
{
    _ => {
        base := PersistentMap::empty[Int, Int](())
        one := PersistentMap::insert(base)(2)(20)
        two := PersistentMap::insert(one)(1)(10)
        three := PersistentMap::insert(two)(3)(30)
        four := PersistentMap::insert(three)(4)(40)
        overwritten := PersistentMap::insert(four)(2)(22)
        removedLeaf := PersistentMap::remove(overwritten)(4)
        removedRoot := PersistentMap::remove(overwritten)(2)
        removedMissing := PersistentMap::remove(overwritten)(99)
        oldTwo := PersistentMap::get_or(two)(2)(0)
        oldOne := PersistentMap::get_or(two)(1)(0)
        newTwo := PersistentMap::get_or(overwritten)(2)(0)
        oldMissingFour := PersistentMap::contains_key(two)(4)
        missingLeaf := PersistentMap::contains_key(removedLeaf)(4)
        missingRoot := PersistentMap::contains_key(removedRoot)(2)
        ordered := PersistentMap::keys(overwritten)
        removedRootOrdered := PersistentMap::keys(removedRoot)
        unchangedByMissingRemove := PersistentMap::keys(removedMissing)
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
           Seq::sum(ordered) == 10
        then { 21 } else { 1 }
    }
}

check_set :: Unit -> Int
{
    _ => {
        s0 := PersistentSet::empty[Int](())
        s1 := PersistentSet::insert(PersistentSet::insert(s0)(3))(1)
        s2 := PersistentSet::remove(s1)(3)
        ordered := match PersistentSet::to_seq(s1) {
            [1, 3] => true,
            _ => false
        }
        if PersistentSet::contains(s1)(3) &&
           !PersistentSet::contains(s2)(3) &&
           PersistentSet::contains(s2)(1) &&
           ordered &&
           Seq::sum(PersistentSet::to_seq(s1)) == 4
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
        Assert.True(PrecompiledModuleRegistry.TryGetSource("Std/PersistentMap", out var source));
        Assert.Contains("Shared::Shared[Node[K, V]]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TreeMap::clone", source, StringComparison.Ordinal);
    }
}
