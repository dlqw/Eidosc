namespace Eidosc.CodeGen.Llvm;

internal sealed class LlvmAttributeGroupRegistry
{
    private readonly LlvmModule _module;

    public LlvmAttributeGroupRegistry(LlvmModule module)
    {
        _module = module;
    }

    public int GetOrAdd(params string[] attributes)
    {
        var normalized = Normalize(attributes);
        foreach (var group in _module.AttributeGroups)
        {
            if (Normalize(group.Attributes).SequenceEqual(normalized, StringComparer.Ordinal))
            {
                return group.Id;
            }
        }

        var id = _module.AttributeGroups
            .Select(static group => group.Id)
            .DefaultIfEmpty(-1)
            .Max() + 1;
        _module.AttributeGroups.Add(new LlvmAttributeGroup
        {
            Id = id,
            Attributes = [.. normalized]
        });
        return id;
    }

    private static string[] Normalize(IEnumerable<string> attributes) =>
        attributes
            .Where(static attribute => !string.IsNullOrWhiteSpace(attribute))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static attribute => attribute, StringComparer.Ordinal)
            .ToArray();
}

internal static class LlvmAttributeFormatter
{
    public static string FormatFunctionReferences(IReadOnlyList<int> attributeIds) =>
        attributeIds.Count == 0
            ? ""
            : string.Concat(attributeIds.Select(static id => $" #{id}"));

    public static string FormatParameter(LlvmParameter parameter, bool includeName)
    {
        var type = parameter.Type.ToIrString();
        var attributes = parameter.Attributes.Count == 0
            ? ""
            : $" {string.Join(' ', parameter.Attributes.Select(FormatParameterAttribute))}";
        var name = includeName && !string.IsNullOrEmpty(parameter.Name)
            ? $" %{parameter.Name}"
            : "";
        return $"{type}{attributes}{name}";
    }

    private static string FormatParameterAttribute(LlvmParameterAttribute attribute) =>
        attribute switch
        {
            LlvmParameterAttribute.Noundef => "noundef",
            LlvmParameterAttribute.NoAlias => "noalias",
            LlvmParameterAttribute.NoCapture => "nocapture",
            LlvmParameterAttribute.NoFree => "nofree",
            LlvmParameterAttribute.NonNull => "nonnull",
            LlvmParameterAttribute.ReadOnly => "readonly",
            LlvmParameterAttribute.WriteOnly => "writeonly",
            LlvmParameterAttribute.ImmArg => "immarg",
            LlvmParameterAttribute.Returned => "returned",
            LlvmParameterAttribute.SignExt => "signext",
            LlvmParameterAttribute.ZeroExt => "zeroext",
            LlvmParameterAttribute.InReg => "inreg",
            LlvmParameterAttribute.ByVal => "byval",
            LlvmParameterAttribute.InAlloca => "inalloca",
            LlvmParameterAttribute.SRet => "sret",
            LlvmParameterAttribute.Nest => "nest",
            _ => attribute.ToString().ToLowerInvariant()
        };
}
