using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

public sealed partial class MirGenericSpecializer
{
    private void PopulateSpecializedConstructorLayouts(
        MirModule module,
        Dictionary<int, List<ConstructorTypeLayout>> outputLayouts)
    {
        CreateConstructorLayoutSpecializer().Populate(module, outputLayouts);
    }

    private MirConstructorLayoutSpecializer CreateConstructorLayoutSpecializer()
    {
        return new MirConstructorLayoutSpecializer(
            _dynamicTypes.DescriptorByIdDict,
            _typeConstructorInfoByTypeId,
            CreateConstructorKeyMatcher(),
            TryGetTypeDescriptor,
            GetOrCreateDynamicTypeId,
            EnumerateTypeAliasInfos,
            IsMirGenericTypeParameter);
    }
}
