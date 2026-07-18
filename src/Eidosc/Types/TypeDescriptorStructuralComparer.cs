using Eidosc.Symbols;

namespace Eidosc.Types;

/// <summary>
/// Compares <see cref="TypeDescriptor" /> values by structural content.
/// </summary>
public sealed class TypeDescriptorStructuralComparer : IEqualityComparer<TypeDescriptor>
{
    /// <summary>
    /// Gets the shared comparer instance.
    /// </summary>
    public static TypeDescriptorStructuralComparer Instance { get; } = new();

    private TypeDescriptorStructuralComparer()
    {
    }

    /// <inheritdoc />
    public bool Equals(TypeDescriptor? x, TypeDescriptor? y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (x is null || y is null || x.GetType() != y.GetType())
        {
            return false;
        }

        return x switch
        {
            TypeDescriptor.Builtin left when y is TypeDescriptor.Builtin right =>
                left.TypeIdValue == right.TypeIdValue,
            TypeDescriptor.Function left when y is TypeDescriptor.Function right =>
                string.Equals(left.Effects, right.Effects, StringComparison.Ordinal) &&
                left.ReturnType == right.ReturnType &&
                left.ParamTypes.SequenceEqual(right.ParamTypes),
            TypeDescriptor.Tuple left when y is TypeDescriptor.Tuple right =>
                left.FieldTypes.SequenceEqual(right.FieldTypes),
            TypeDescriptor.TyCon left when y is TypeDescriptor.TyCon right =>
                left.Constructor == right.Constructor &&
                left.TypeArgs.SequenceEqual(right.TypeArgs) &&
                left.ValueArgs.SequenceEqual(right.ValueArgs) &&
                left.EffectArgs.SequenceEqual(right.EffectArgs),
            TypeDescriptor.Ref left when y is TypeDescriptor.Ref right =>
                left.Inner == right.Inner,
            TypeDescriptor.MutRef left when y is TypeDescriptor.MutRef right =>
                left.Inner == right.Inner,
            TypeDescriptor.Shared left when y is TypeDescriptor.Shared right =>
                left.Inner == right.Inner,
            TypeDescriptor.TypeVar left when y is TypeDescriptor.TypeVar right =>
                left.Index == right.Index,
            _ => false
        };
    }

    /// <inheritdoc />
    public int GetHashCode(TypeDescriptor obj)
    {
        var hash = new HashCode();
        hash.Add(obj.GetType());
        switch (obj)
        {
            case TypeDescriptor.Builtin builtin:
                hash.Add(builtin.TypeIdValue);
                break;
            case TypeDescriptor.Function function:
                AddTypeIds(ref hash, function.ParamTypes);
                hash.Add(function.ReturnType);
                hash.Add(function.Effects, StringComparer.Ordinal);
                break;
            case TypeDescriptor.Tuple tuple:
                AddTypeIds(ref hash, tuple.FieldTypes);
                break;
            case TypeDescriptor.TyCon tyCon:
                hash.Add(tyCon.Constructor);
                AddTypeIds(ref hash, tyCon.TypeArgs);
                foreach (var valueArgument in tyCon.ValueArgs)
                {
                    hash.Add(valueArgument);
                }
                foreach (var effectArgument in tyCon.EffectArgs)
                {
                    hash.Add(effectArgument);
                }
                break;
            case TypeDescriptor.Ref reference:
                hash.Add(reference.Inner);
                break;
            case TypeDescriptor.MutRef reference:
                hash.Add(reference.Inner);
                break;
            case TypeDescriptor.Shared shared:
                hash.Add(shared.Inner);
                break;
            case TypeDescriptor.TypeVar typeVar:
                hash.Add(typeVar.Index);
                break;
        }

        return hash.ToHashCode();
    }

    private static void AddTypeIds(ref HashCode hash, IEnumerable<TypeId> typeIds)
    {
        foreach (var typeId in typeIds)
        {
            hash.Add(typeId);
        }
    }
}
