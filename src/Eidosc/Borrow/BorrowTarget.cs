using Eidosc.Mir;

namespace Eidosc.Borrow;

/// <summary>
/// 借用目标（基础局部变量 + 投影路径）
/// </summary>
public readonly record struct BorrowTarget
{
    private const string RootSegment = "root";
    private const string Separator = WellKnownStrings.Operators.Divide;
    private const string FieldPrefix = "field:";
    private const string IndexPrefix = "idx:";
    private const string WildcardIndexSegment = "idx:*";
    private const string LocalIndexPrefix = "idx:local:";
    private const string TempIndexPrefix = "idx:temp:";
    private const string DerefSegment = "deref";

    public LocalId BaseLocal { get; init; }
    public string PathKey { get; init; }

    public static BorrowTarget None => new()
    {
        BaseLocal = LocalId.None,
        PathKey = RootSegment
    };

    public static BorrowTarget ForLocal(LocalId localId) => new()
    {
        BaseLocal = localId,
        PathKey = RootSegment
    };

    public bool IsValid => BaseLocal.IsValid;

    public string StableKey => $"{BaseLocal.Value}:{NormalizePath(PathKey)}";

    public bool OverlapsWith(BorrowTarget other)
    {
        if (!IsValid || !other.IsValid || !BaseLocal.Equals(other.BaseLocal))
        {
            return false;
        }

        var leftSegments = GetPathSegments(PathKey);
        var rightSegments = GetPathSegments(other.PathKey);
        var commonLength = Math.Min(leftSegments.Length, rightSegments.Length);

        for (var i = 0; i < commonLength; i++)
        {
            if (SegmentsOverlap(leftSegments[i], rightSegments[i]))
            {
                continue;
            }

            return false;
        }

        if (leftSegments.Length == rightSegments.Length)
        {
            return true;
        }

        // 前缀关系默认视为重叠；但 base 与 pointee（deref）是不同内存域。
        var longer = leftSegments.Length > rightSegments.Length
            ? leftSegments
            : rightSegments;
        var nextSegment = longer[commonLength];
        return !IsDerefSegment(nextSegment);
    }

    public static bool TryResolve(MirOperand operand, out BorrowTarget target)
    {
        if (operand is MirPlace place)
        {
            return TryResolve(place, out target);
        }

        target = None;
        return false;
    }

    public static bool TryResolve(MirPlace place, out BorrowTarget target)
    {
        switch (place.Kind)
        {
            case PlaceKind.Local:
                target = ForLocal(place.Local);
                return target.IsValid;

            case PlaceKind.Field when place.Base != null &&
                                      TryResolve(place.Base, out var fieldBase):
                target = fieldBase.AppendField(place.FieldName);
                return target.IsValid;

            case PlaceKind.Index when place.Base != null &&
                                      TryResolve(place.Base, out var indexBase):
                target = indexBase.AppendIndex(place.Index);
                return target.IsValid;

            case PlaceKind.Deref when place.Base != null &&
                                      TryResolve(place.Base, out var derefBase):
                target = derefBase.AppendDeref();
                return target.IsValid;
        }

        target = None;
        return false;
    }

    private BorrowTarget AppendField(string? fieldName)
    {
        return AppendSegment($"{FieldPrefix}{EscapeSegment(fieldName ?? "_")}");
    }

    private BorrowTarget AppendIndex(MirOperand? index)
    {
        return AppendSegment(BuildIndexSegment(index));
    }

    private BorrowTarget AppendDeref()
    {
        return AppendSegment(DerefSegment);
    }

    private BorrowTarget AppendSegment(string segment)
    {
        if (!IsValid)
        {
            return None;
        }

        return new BorrowTarget
        {
            BaseLocal = BaseLocal,
            PathKey = $"{NormalizePath(PathKey)}{Separator}{segment}"
        };
    }

    private static string BuildIndexSegment(MirOperand? index)
    {
        if (index is MirConstant { Value: MirConstantValue.IntValue(var intValue) })
        {
            return $"{IndexPrefix}int:{intValue}";
        }

        if (index is MirConstant { Value: MirConstantValue.CharValue(var charValue) })
        {
            return $"{IndexPrefix}char:{(int)charValue}";
        }

        if (index is MirConstant { Value: MirConstantValue.BoolValue(var boolValue) })
        {
            return $"{IndexPrefix}bool:{(boolValue ? 1 : 0)}";
        }

        if (index is MirPlace { Kind: PlaceKind.Local, Local: var localIndex } && localIndex.IsValid)
        {
            return $"{LocalIndexPrefix}{localIndex.Value}";
        }

        if (index is MirTemp { Id: var tempIndex } && tempIndex.IsValid)
        {
            return $"{TempIndexPrefix}{tempIndex.Value}";
        }

        // 非常量下标使用通配符：与任意下标保守冲突。
        return WildcardIndexSegment;
    }

    private static bool SegmentsOverlap(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        if (IsWildcardIndexSegment(left) && IsIndexSegment(right))
        {
            return true;
        }

        if (IsWildcardIndexSegment(right) && IsIndexSegment(left))
        {
            return true;
        }

        if (IsSymbolicIndexSegment(left) || IsSymbolicIndexSegment(right))
        {
            return IsIndexSegment(left) && IsIndexSegment(right);
        }

        return false;
    }

    private static bool IsDerefSegment(string segment)
    {
        return string.Equals(segment, DerefSegment, StringComparison.Ordinal);
    }

    private static bool IsIndexSegment(string segment)
    {
        return segment.StartsWith(IndexPrefix, StringComparison.Ordinal);
    }

    private static bool IsWildcardIndexSegment(string segment)
    {
        return string.Equals(segment, WildcardIndexSegment, StringComparison.Ordinal);
    }

    private static bool IsSymbolicIndexSegment(string segment)
    {
        return segment.StartsWith(LocalIndexPrefix, StringComparison.Ordinal) ||
               segment.StartsWith(TempIndexPrefix, StringComparison.Ordinal);
    }

    private static string[] GetPathSegments(string pathKey)
    {
        var normalized = NormalizePath(pathKey);
        return normalized.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
    }

    private static string NormalizePath(string? pathKey)
    {
        return string.IsNullOrWhiteSpace(pathKey)
            ? RootSegment
            : pathKey!;
    }

    private static string EscapeSegment(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(WellKnownStrings.Operators.Divide, "\\/", StringComparison.Ordinal);
    }
}
