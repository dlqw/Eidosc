using Eidosc.Symbols;

namespace Eidosc.Types;

/// <summary>
/// 类型种类 (Kind) - 表示类型的"类型"
///
/// Kind 系统用于区分不同层级的类型:
/// - kind1: 普通类型,如 Int, String
/// - kind2: 单参数类型构造器,如 Maybe, List
/// - kind3: 双参数类型构造器,如 Either, Map
/// - 任意高阶 Kind 通过嵌套 KArrow 表示
/// </summary>
public abstract record Kind
{
    /// <summary>
    /// Kind 名称 (用于调试和错误信息)
    /// </summary>
    public abstract string Name { get; }

    /// <summary>
    /// 是否是具体的 Kind (不包含变量)
    /// </summary>
    public abstract bool IsConcrete { get; }

    /// <summary>
    /// 获取 Kind 中的自由变量
    /// </summary>
    public abstract IEnumerable<int> FreeVariables();

    /// <summary>
    /// 获取 Kind 的阶数 (返回 0 表示 kind1, 1 表示 kind2, 2 表示 kind3, 以此类推)
    /// </summary>
    public abstract int Order { get; }

    /// <summary>
    /// 普通类型的 Kind
    /// </summary>
    public sealed record KStar : Kind
    {
        public override string Name => "kind1";
        public override bool IsConcrete => true;
        public override int Order => 0;
        public override IEnumerable<int> FreeVariables() => [];

        /// <summary>
        /// 单例实例
        /// </summary>
        public static KStar Instance { get; } = new();
    }

    /// <summary>
    /// 箭头类型 (k1 -> k2) - 类型构造器的 Kind
    /// 支持任意高阶类型构造器:
    /// - kind2: 单参数类型构造器 (如 Maybe, List)
    /// - kind3: 双参数类型构造器 (如 Either, Map)
    /// - kind2 -> kind1: 高阶类型构造器 (接受类型构造器作为参数)
    /// </summary>
    public sealed record KArrow : Kind
    {
        /// <summary>
        /// 参数 Kind
        /// </summary>
        public Kind Param { get; }

        /// <summary>
        /// 返回 Kind
        /// </summary>
        public Kind Result { get; }

        public KArrow(Kind param, Kind result)
        {
            Param = param;
            Result = result;
        }

        public override string Name => KindParser.ToKindText(this);

        public override bool IsConcrete => Param.IsConcrete && Result.IsConcrete;

        public override int Order => Result.Order + 1;

        public override IEnumerable<int> FreeVariables()
        {
            foreach (var v in Param.FreeVariables())
                yield return v;
            foreach (var v in Result.FreeVariables())
                yield return v;
        }

        /// <summary>
        /// 应用类型参数到 Kind，返回结果 Kind
        /// 例如: kind3 应用后得到 kind2
        /// </summary>
        public Kind Apply() => Result;
    }

    /// <summary>
    /// Kind 变量 - 用于 Kind 推断
    /// </summary>
    public sealed record KVar : Kind
    {
        /// <summary>
        /// 变量 ID
        /// </summary>
        public int Id { get; init; }

        /// <summary>
        /// 实例化后的 Kind（用于合一）
        /// </summary>
        public Kind? Instance { get; set; }

        public override string Name => $"k{Id}";
        public override bool IsConcrete => Instance?.IsConcrete ?? false;
        public override int Order => Instance?.Order ?? -1;

        public override IEnumerable<int> FreeVariables()
        {
            if (Instance != null)
            {
                foreach (var v in Instance.FreeVariables())
                    yield return v;
            }
            else
            {
                yield return Id;
            }
        }
    }

    /// <summary>
    /// Kind 行 (用于多参数类型构造器)
    /// </summary>
    public sealed record KRow : Kind
    {
        /// <summary>
        /// 字段列表
        /// </summary>
        public List<Kind> Fields { get; init; } = [];

        public override string Name => $"({string.Join(", ", Fields.Select(f => f.Name))})";
        public override bool IsConcrete => Fields.All(f => f.IsConcrete);
        public override int Order => Fields.Count > 0 ? Fields.Max(f => f.Order) : 0;

        public override IEnumerable<int> FreeVariables()
        {
            foreach (var field in Fields)
            {
                foreach (var v in field.FreeVariables())
                    yield return v;
            }
        }
    }

    /// <summary>
    /// 检查 Kind 是否兼容
    /// </summary>
    public static bool IsCompatible(Kind expected, Kind actual)
    {
        // 展开实例化的变量
        expected = expected is KVar ev && ev.Instance != null ? ev.Instance : expected;
        actual = actual is KVar av && av.Instance != null ? av.Instance : actual;

        if (expected is KStar)
            return actual is KStar;

        if (expected is KArrow expectedArrow && actual is KArrow actualArrow)
        {
            if (!IsCompatible(expectedArrow.Param, actualArrow.Param))
                return false;

            return IsCompatible(expectedArrow.Result, actualArrow.Result);
        }

        if (expected is KRow expectedRow && actual is KRow actualRow)
        {
            if (expectedRow.Fields.Count != actualRow.Fields.Count)
                return false;

            for (int i = 0; i < expectedRow.Fields.Count; i++)
            {
                if (!IsCompatible(expectedRow.Fields[i], actualRow.Fields[i]))
                    return false;
            }

            return true;
        }

        if (expected is KVar)
            return true; // 变量可以匹配任何 Kind

        if (actual is KVar)
            return true; // 变量可以匹配任何 Kind

        return false;
    }

    /// <summary>
    /// 构建指定参数数量的箭头 Kind
    /// 例如: BuildArrowKind(0) = kind1, BuildArrowKind(1) = kind2, BuildArrowKind(2) = kind3
    /// </summary>
    public static Kind BuildArrowKind(int paramCount)
    {
        if (paramCount <= 0)
            return KStar.Instance;

        Kind result = KStar.Instance;
        for (int i = 0; i < paramCount; i++)
        {
            result = new KArrow(KStar.Instance, result);
        }
        return result;
    }

    /// <summary>
    /// 应用 Kind 到类型构造器，返回部分应用后的 Kind
    /// 例如: ApplyKind(kind3, 1) = kind2
    /// </summary>
    public static Kind ApplyKind(Kind kind, int appliedArgs)
    {
        for (int i = 0; i < appliedArgs && kind is KArrow arrow; i++)
        {
            kind = arrow.Result;
        }
        return kind;
    }
}
