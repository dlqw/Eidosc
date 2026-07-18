using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void StdBinaryHeapPriorityQueue_PopInPriorityOrder()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
import std.BinaryHeap
import std.Seq
import std.Option
import std.PriorityQueue

check_heap :: Unit -> Int
{
    _ => {
        heap := BinaryHeap.from_seq[Int]([3, 10, 4, 7, 1])
        match BinaryHeap.pop(heap) {
            Some((first, rest1)) =>
                match BinaryHeap.pop(rest1) {
                    Some((second, rest2)) =>
                        match BinaryHeap.peek(rest2) {
                            Some(third) =>
                                if first == 10 && second == 7 && third == 4 then { 20 } else { 1 },
                            None() => 2
                        },
                    None() => 3
                },
            None() => 4
        }
    }
}

check_priority_queue :: Unit -> Int
{
    _ => {
        q0 := PriorityQueue.empty[Int](())
        q1 := PriorityQueue.enqueue(PriorityQueue.enqueue(PriorityQueue.enqueue(q0)(5))(12))(9)
        match PriorityQueue.dequeue(q1) {
            Some((first, rest1)) =>
                match PriorityQueue.dequeue(rest1) {
                    Some((second, rest2)) =>
                        match PriorityQueue.peek(rest2) {
                            Some(third) =>
                                if first == 12 && second == 9 && third == 5 && PriorityQueue.len(rest2) == 1 then { 22 } else { 5 },
                            None() => 6
                        },
                    None() => 7
                },
            None() => 8
        }
    }
}

main :: Unit -> Int
{
    _ => check_heap(()) + check_priority_queue(())
}
""";

        var execution = CompileAndRunSourceAtNativeWithContext(
            source,
            StdlibListImportInputFile(),
            "std_priority_heap_p1");

        Assert.Equal(42, execution.ExitCode);
    }
}
