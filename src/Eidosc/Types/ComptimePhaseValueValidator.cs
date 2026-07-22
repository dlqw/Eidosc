namespace Eidosc.Types;

internal static class ComptimePhaseValueValidator
{
    public static bool TryValidate(ComptimeValue value, out string reason)
    {
        if (value is ComptimeControlValue)
        {
            reason = "control-flow state cannot escape a comptime evaluation frame";
            return false;
        }

        if (value is ComptimeLambdaValue or ComptimeFunctionValue)
        {
            reason = "partially applied function values cannot cross the comptime phase boundary";
            return false;
        }

        if (!TryValidateType(value.StaticType, value is ComptimeTypeValue, out reason))
        {
            return false;
        }

        switch (value)
        {
            case ComptimeSequenceValue sequence:
                foreach (var element in sequence.Elements)
                {
                    if (!TryValidate(element, out reason))
                    {
                        return false;
                    }
                }
                break;

            case ComptimeMapValue map:
                foreach (var entry in map.Entries)
                {
                    if (!TryValidate(entry.Key, out reason) ||
                        !TryValidate(entry.Value, out reason))
                    {
                        return false;
                    }
                }
                break;

            case ComptimeSetValue set:
                foreach (var element in set.Elements)
                {
                    if (!TryValidate(element, out reason))
                    {
                        return false;
                    }
                }
                break;

            case ComptimeAdtValue adt:
                foreach (var element in adt.PositionalValues)
                {
                    if (!TryValidate(element, out reason))
                    {
                        return false;
                    }
                }
                foreach (var field in adt.NamedValues)
                {
                    if (!TryValidate(field.Value, out reason))
                    {
                        return false;
                    }
                }
                break;

            case ComptimeMetaObjectValue reflected:
                foreach (var property in reflected.Properties)
                {
                    if (!TryValidate(property.Value, out reason))
                    {
                        return false;
                    }
                }
                break;
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryValidateType(Type? type, bool isReflectedTypeValue, out string reason)
    {
        if (type is TyVar { Instance: { } instance })
        {
            return TryValidateType(instance, isReflectedTypeValue, out reason);
        }

        if (isReflectedTypeValue || type == null)
        {
            reason = string.Empty;
            return true;
        }

        switch (type)
        {
            case TyRef:
                reason = "runtime Ref values cannot cross the comptime phase boundary";
                return false;
            case TyMutRef:
                reason = "runtime MRef values cannot cross the comptime phase boundary";
                return false;
            case TyShared:
                reason = "runtime Shared values cannot cross the comptime phase boundary";
                return false;
            case TyFun:
                reason = "runtime function or closure values cannot cross the comptime phase boundary";
                return false;
            case TyCon constructor when
                constructor.Id.Value == BaseTypes.RawPtrId ||
                string.Equals(constructor.Name, WellKnownStrings.BuiltinTypes.RawPtr, StringComparison.Ordinal):
                reason = "RawPtr values cannot cross the comptime phase boundary";
                return false;
            case TyCon constructor:
                foreach (var argument in constructor.Args)
                {
                    if (!TryValidateType(argument, isReflectedTypeValue: false, out reason))
                    {
                        return false;
                    }
                }
                break;
        }

        reason = string.Empty;
        return true;
    }
}
