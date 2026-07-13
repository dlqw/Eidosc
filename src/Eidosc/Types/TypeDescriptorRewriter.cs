using Eidosc.Symbols;

namespace Eidosc.Types;

public static class TypeDescriptorRewriter
{
    public static TypeDescriptor RewriteTypeIds(
        TypeDescriptor descriptor,
        Func<TypeId, TypeId> rewriteTypeId)
    {
        return descriptor switch
        {
            TypeDescriptor.Function function => RewriteFunction(function, rewriteTypeId),
            TypeDescriptor.Tuple tuple => RewriteTuple(tuple, rewriteTypeId),
            TypeDescriptor.TyCon tyCon => RewriteTyCon(tyCon, rewriteTypeId),
            TypeDescriptor.Ref reference => RewriteRef(reference, rewriteTypeId),
            TypeDescriptor.MutRef reference => RewriteMutRef(reference, rewriteTypeId),
            _ => descriptor
        };
    }

    private static TypeDescriptor RewriteFunction(
        TypeDescriptor.Function function,
        Func<TypeId, TypeId> rewriteTypeId)
    {
        var parameters = RewriteTypeIds(function.ParamTypes, rewriteTypeId, out var parametersChanged);
        var returnType = rewriteTypeId(function.ReturnType);
        return parametersChanged || returnType != function.ReturnType
            ? new TypeDescriptor.Function(parameters, returnType, function.Effects)
            : function;
    }

    private static TypeDescriptor RewriteTuple(
        TypeDescriptor.Tuple tuple,
        Func<TypeId, TypeId> rewriteTypeId)
    {
        var fields = RewriteTypeIds(tuple.FieldTypes, rewriteTypeId, out var changed);
        return changed ? new TypeDescriptor.Tuple(fields) : tuple;
    }

    private static TypeDescriptor RewriteTyCon(
        TypeDescriptor.TyCon tyCon,
        Func<TypeId, TypeId> rewriteTypeId)
    {
        var typeArgs = RewriteTypeIds(tyCon.TypeArgs, rewriteTypeId, out var changed);
        var valueArgs = new GenericValueArgumentDescriptor[tyCon.ValueArgs.Length];
        for (var index = 0; index < tyCon.ValueArgs.Length; index++)
        {
            var valueArgument = tyCon.ValueArgs[index];
            var typeId = rewriteTypeId(valueArgument.TypeId);
            valueArgs[index] = valueArgument with { TypeId = typeId };
            changed |= typeId != valueArgument.TypeId;
        }

        return changed
            ? new TypeDescriptor.TyCon(tyCon.Constructor, typeArgs) { ValueArgs = valueArgs }
            : tyCon;
    }

    private static TypeDescriptor RewriteRef(
        TypeDescriptor.Ref reference,
        Func<TypeId, TypeId> rewriteTypeId)
    {
        var inner = rewriteTypeId(reference.Inner);
        return inner != reference.Inner ? new TypeDescriptor.Ref(inner) : reference;
    }

    private static TypeDescriptor RewriteMutRef(
        TypeDescriptor.MutRef reference,
        Func<TypeId, TypeId> rewriteTypeId)
    {
        var inner = rewriteTypeId(reference.Inner);
        return inner != reference.Inner ? new TypeDescriptor.MutRef(inner) : reference;
    }

    private static TypeId[] RewriteTypeIds(
        IReadOnlyList<TypeId> typeIds,
        Func<TypeId, TypeId> rewriteTypeId,
        out bool changed)
    {
        changed = false;
        var rewritten = new TypeId[typeIds.Count];
        for (var i = 0; i < typeIds.Count; i++)
        {
            var next = rewriteTypeId(typeIds[i]);
            rewritten[i] = next;
            changed |= next != typeIds[i];
        }

        return rewritten;
    }
}
