using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Eidosc.Utils;

public class StringInterner
{
    private readonly ConcurrentDictionary<string, StringId> _stringToIdMap;
    private readonly ConcurrentDictionary<string, StringId>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    private readonly List<string> _idToStringList;
    private readonly Lock _writeLock = new();

    public StringInterner()
    {
        int concurrencyLevel = Environment.ProcessorCount * 2;
        int initialCapacity = 8192;

        _stringToIdMap = new ConcurrentDictionary<string, StringId>(
            concurrencyLevel,
            initialCapacity,
            StringComparer.Ordinal
        );

        _lookup = _stringToIdMap.GetAlternateLookup<ReadOnlySpan<char>>();
        _idToStringList = new List<string>(initialCapacity);

        // 预留 ID 0 为空字符串
        GetOrIntern("");
    }

    /// <summary>
    /// [Hot Path] 针对 Span 的零分配查找
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StringId GetOrIntern(ReadOnlySpan<char> span)
    {
        if (_lookup.TryGetValue(span, out StringId existingId))
        {
            return existingId;
        }

        return GetOrInternSlow(span.ToString());
    }

    /// <summary>
    /// 针对普通 string 的重载
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StringId GetOrIntern(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (_stringToIdMap.TryGetValue(text, out StringId existingId))
        {
            return existingId;
        }

        return GetOrInternSlow(text);
    }

    // 慢速路径 (写入)
    private StringId GetOrInternSlow(string text)
    {
        lock (_writeLock)
        {
            // Double-Check
            if (_stringToIdMap.TryGetValue(text, out var id))
            {
                return id;
            }

            int newIdValue = _idToStringList.Count;

            // --- 修复与优化 ---
            // 1. 安全获取首字符
            char firstChar = text.Length > 0 ? text[0] : '\0';

            // 2. 安全处理长度溢出 (ushort max = 65535)
            // 如果超过 ushort，存 MaxValue，表示"很长，请去 Resolve 查看详情"
            ushort len = text.Length > ushort.MaxValue
                ? ushort.MaxValue
                : (ushort)text.Length;

            StringId newId = new StringId(newIdValue, len, firstChar);

            _idToStringList.Add(text);
            _stringToIdMap[text] = newId;

            return newId;
        }
    }

    /// <summary>
    /// 通过 ID 获取原始字符串
    /// </summary>
    public string Resolve(StringId id)
    {
        // 快速范围检查 (利用 List.Count 读取的原子性进行预判)
        if (id.Id < 0) return string.Empty;

        // 这里必须加锁，因为 List 在 Add 扩容时不是线程安全的 (虽然几率很小)
        // 如果想要极致性能的 Resolve，需要换成 SegmentedList (分块数组)
        lock (_writeLock)
        {
            if (id.Id < _idToStringList.Count)
            {
                return _idToStringList[id.Id];
            }
        }

        return string.Empty;
    }
}
