using Eidosc.Hir;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Mir;

public sealed partial class MirBuilder
{
    private int GetRuntimeElementSize(TypeId typeId)
    {
        if (!typeId.IsValid)
        {
            return IntPtr.Size;
        }

        if (_dynamicTypeKeysById.TryGetValue(typeId.Value, out var dynamicTypeKey) &&
            TypeKeyParsing.TryParseTupleTypeKey(dynamicTypeKey, out var tupleElementTypes))
        {
            return tupleElementTypes.Count * IntPtr.Size;
        }

        return typeId.Value switch
        {
            BaseTypes.BoolId => 1,
            BaseTypes.CharId => 4,
            BaseTypes.UnitId => 0,
            BaseTypes.NeverId => 0,
            BaseTypes.IntId => sizeof(long),
            BaseTypes.FloatId => sizeof(double),
            _ => IntPtr.Size
        };
    }

    private static MirConstant CreateIntConstant(long value, SourceSpan span)
    {
        return new MirConstant
        {
            Value = new MirConstantValue.IntValue(value),
            TypeId = new TypeId(BaseTypes.IntId),
            Span = span
        };
    }

    private static MirConstant CreateStringConstant(string value, SourceSpan span)
    {
        return new MirConstant
        {
            Value = new MirConstantValue.StringValue(value ?? string.Empty),
            TypeId = new TypeId(BaseTypes.StringId),
            Span = span
        };
    }

    /// <summary>
    /// 创建裸 C 字符串常量（直接作为 const char* 传递给运行时，不包装为 EidosString）。
    /// 用于 handler 描述符槽位和 effect dispatch 的 op_name 参数。
    /// </summary>
    private static MirConstant CreateRawStringConstant(string value, SourceSpan span)
    {
        return new MirConstant
        {
            Value = new MirConstantValue.RawStringValue(value ?? string.Empty),
            TypeId = new TypeId(BaseTypes.StringId),
            Span = span
        };
    }

    private static MirConstant CreateBoolConstant(bool value, SourceSpan span)
    {
        return new MirConstant
        {
            Value = new MirConstantValue.BoolValue(value),
            TypeId = new TypeId(BaseTypes.BoolId),
            Span = span
        };
    }

    private static bool TryGetBooleanConstant(MirOperand operand, out bool value)
    {
        if (operand is MirConstant { Value: MirConstantValue.BoolValue boolValue })
        {
            value = boolValue.Value;
            return true;
        }

        value = false;
        return false;
    }

    private void RegisterKnownListLength(MirPlace place, int length)
    {
        if (place.Kind == PlaceKind.Local)
        {
            _knownListLengths[place.Local] = Math.Max(length, 0);
        }
    }

    private bool TryGetKnownListLength(MirPlace place, out int length)
    {
        if (place.Kind == PlaceKind.Local &&
            _knownListLengths.TryGetValue(place.Local, out length))
        {
            return true;
        }

        length = 0;
        return false;
    }

    private void PropagateKnownListLength(MirPlace target, MirPlace source)
    {
        if (target.Kind != PlaceKind.Local)
        {
            return;
        }

        if (source.Kind == PlaceKind.Local &&
            _knownListLengths.TryGetValue(source.Local, out var length))
        {
            _knownListLengths[target.Local] = length;
            return;
        }

        _knownListLengths.Remove(target.Local);
    }

    private void ClearKnownListLength(MirPlace place)
    {
        if (place.Kind == PlaceKind.Local)
        {
            _knownListLengths.Remove(place.Local);
        }
    }

    private void RegisterRuntimeArrayLocal(MirPlace place)
    {
        if (place.Kind == PlaceKind.Local)
        {
            _runtimeArrayLocals.Add(place.Local);
        }
    }

    private void ClearRuntimeArrayLocal(MirPlace place)
    {
        if (place.Kind == PlaceKind.Local)
        {
            _runtimeArrayLocals.Remove(place.Local);
        }
    }

    private void PropagateRuntimeArrayLocal(MirPlace target, MirPlace source)
    {
        if (target.Kind != PlaceKind.Local)
        {
            return;
        }

        if (source.Kind == PlaceKind.Local && _runtimeArrayLocals.Contains(source.Local))
        {
            _runtimeArrayLocals.Add(target.Local);
            return;
        }

        _runtimeArrayLocals.Remove(target.Local);
    }

    private MirIndexAccessKind ResolveIndexAccessKind(MirPlace basePlace, HirIndexAccessKind targetKind = HirIndexAccessKind.Unknown)
    {
        if (targetKind != HirIndexAccessKind.Unknown)
        {
            return targetKind switch
            {
                HirIndexAccessKind.Aggregate => MirIndexAccessKind.Aggregate,
                HirIndexAccessKind.RuntimeArray => MirIndexAccessKind.RuntimeArray,
                _ => MirIndexAccessKind.Aggregate
            };
        }

        return ResolveIndexAccessKind(basePlace);
    }

    private MirIndexAccessKind ResolveIndexAccessKind(MirPlace basePlace)
    {
        if (basePlace.Kind == PlaceKind.Local && _runtimeArrayLocals.Contains(basePlace.Local))
        {
            return MirIndexAccessKind.RuntimeArray;
        }

        if (basePlace.Kind == PlaceKind.Index && basePlace.IndexAccessKind == MirIndexAccessKind.RuntimeArray)
        {
            return MirIndexAccessKind.RuntimeArray;
        }

        return MirIndexAccessKind.Aggregate;
    }
}
