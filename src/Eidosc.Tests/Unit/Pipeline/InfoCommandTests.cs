using Eidosc.Cli.Commands;

namespace Eidosc.Tests.Unit.Pipeline;

public class InfoCommandTests
{
    [Fact]
    public void RenderStdlibText_IncludesCategorySummariesAndRepresentativeApis()
    {
        var text = InfoCommand.RenderStdlibText();

        Assert.Contains("[函数式能力]", text, StringComparison.Ordinal);
        Assert.Contains("摘要     - 函数组合、可选/错误流水线、trait 与 HKT 抽象（Functor/Applicative/Foldable/Traversable/Monad）。", text, StringComparison.Ordinal);
        Assert.Contains("代表接口 - Fn::compose, Predicate::accept, Predicate::is, Option::map, Option::apply, Option::traverse, Seq::traverse, Result::and_then, Ordering::show", text, StringComparison.Ordinal);
        Assert.Contains("    Std::Predicate", text, StringComparison.Ordinal);
        Assert.Contains("    Std::Applicative", text, StringComparison.Ordinal);
        Assert.Contains("    Std::Foldable", text, StringComparison.Ordinal);
        Assert.Contains("    Std::Traversable", text, StringComparison.Ordinal);
        Assert.Contains("[数学能力]", text, StringComparison.Ordinal);
        Assert.Contains("摘要     - 标量数学、角度/插值辅助，以及面向网格与几何的游戏数学类型/运算。", text, StringComparison.Ordinal);
        Assert.Contains("代表接口 - Math::abs, Math::wrap, FloatMath::smoothstep, FloatMath::move_toward, GameMath::ivec2, GameMath::grid_cell_rect, GameMath::move_toward", text, StringComparison.Ordinal);
        Assert.Contains("    Std::FloatMath", text, StringComparison.Ordinal);
        Assert.Contains("    Std::GameMath", text, StringComparison.Ordinal);
        Assert.Contains("[容器能力]", text, StringComparison.Ordinal);
        Assert.Contains("摘要     - 序列、哈希集合/映射、工程队列/栈/双端队列、优先级堆和有序树容器。", text, StringComparison.Ordinal);
        Assert.Contains("代表接口 - Seq::head, SeqBuilder::filled, SeqBuilder::push, HashMap::insert, HashSet::contains, Deque::push_back, Queue::dequeue, Stack::pop, BinaryHeap::pop, PriorityQueue::min_enqueue, PriorityQueue::dequeue, TreeMap::keys, TreeSet::to_seq", text, StringComparison.Ordinal);
        Assert.Contains("    Std::BinaryHeap", text, StringComparison.Ordinal);
        Assert.Contains("    Std::Deque", text, StringComparison.Ordinal);
        Assert.Contains("    Std::PriorityQueue", text, StringComparison.Ordinal);
        Assert.Contains("    Std::Queue", text, StringComparison.Ordinal);
        Assert.Contains("    Std::Stack", text, StringComparison.Ordinal);
        Assert.Contains("    Std::TreeMap", text, StringComparison.Ordinal);
        Assert.Contains("    Std::TreeSet", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderStdlibText_GroupsRangeAndTextUnderOtherFoundation()
    {
        var text = InfoCommand.RenderStdlibText();

        Assert.Contains("[其他基础能力]", text, StringComparison.Ordinal);
        Assert.Contains("代表接口 - Text::from_int, Text::char_code_at_or, Text::char_at_or, Text::index_of_or, Range::make, Range::contains", text, StringComparison.Ordinal);
        Assert.Contains("    Std::Range", text, StringComparison.Ordinal);
        Assert.Contains("    Std::Text", text, StringComparison.Ordinal);
    }
}
