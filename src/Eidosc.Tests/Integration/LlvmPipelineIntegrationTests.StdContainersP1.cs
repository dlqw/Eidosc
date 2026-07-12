using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void StdDequeQueueStack_PreserveEngineeringContainerOrder()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
import Std::Deque
import Std::Seq
import Std::Option
import Std::Queue
import Std::Stack

check_deque :: Unit -> Int
{
    _ => {
        d0 := Deque::empty[Int](())
        d1 := Deque::push_back(Deque::push_front(d0)(2))(3)
        d2 := Deque::push_front(d1)(1)
        listed := Deque::to_seq(d2)
        match Deque::pop_front(d2) {
            Some((front, withoutFront)) =>
                match Deque::pop_back(withoutFront) {
                    Some((back, middle)) =>
                        if front == 1 &&
                           back == 3 &&
                           Deque::len(middle) == 1 &&
                           Deque::get_or(middle)(0)(0) == 2 &&
                           Seq::sum(listed) == 6
                        then { 10 }
                        else { 1 },
                    None() => 2
                },
            None() => 3
        }
    }
}

check_queue :: Unit -> Int
{
    _ => {
        q0 := Queue::empty[Int](())
        q1 := Queue::enqueue(Queue::enqueue(q0)(10))(20)
        match Queue::dequeue(q1) {
            Some((first, rest)) =>
                match Queue::peek(rest) {
                    Some(second) =>
                        if first == 10 && second == 20 && Queue::len(rest) == 1 then { 20 } else { 4 },
                    None() => 5
                },
            None() => 6
        }
    }
}

check_stack :: Unit -> Int
{
    _ => {
        s0 := Stack::empty[Int](())
        s1 := Stack::push(Stack::push(s0)(4))(9)
        match Stack::pop(s1) {
            Some((top, rest)) =>
                match Stack::peek(rest) {
                    Some(next) =>
                        if top == 9 && next == 4 && Stack::len(rest) == 1 then { 12 } else { 7 },
                    None() => 8
                },
            None() => 9
        }
    }
}

main :: Unit -> Int
{
    _ => check_deque(()) + check_queue(()) + check_stack(())
}
""";

        var execution = CompileAndRunSourceAtNativeWithContext(
            source,
            StdlibListImportInputFile(),
            "std_basic_containers_p1");

        Assert.Equal(42, execution.ExitCode);
    }
}
