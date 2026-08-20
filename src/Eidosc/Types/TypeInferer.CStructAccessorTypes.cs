using Eidosc.Symbols;

namespace Eidosc.Types;

public sealed partial class TypeInferer
{
    /// <summary>
    /// Cfn 字段的 @cstruct accessor 保留完整类型实参（Cfn[A..., R]）；
    /// FuncSymbol 的基础 TypeId 仍是 CfnId（C ABI 为指针）。
    /// </summary>
    private static bool TryCreateCStructAccessorFunctionType(
        FuncSymbol funcSymbol,
        out TyFun functionType)
    {
        functionType = default!;
        if (!funcSymbol.IsCStructAccessor ||
            funcSymbol.CStructFieldTypeId.Value != BaseTypes.CfnId ||
            funcSymbol.CStructFieldTypeArguments.Count == 0)
        {
            return false;
        }

        var cfnFieldType = new TyCon
        {
            Name = WellKnownStrings.BuiltinTypes.Cfn,
            Id = new TypeId(BaseTypes.CfnId),
            Args = funcSymbol.CStructFieldTypeArguments
                .Select(static argumentId => CreateMetadataType(new TypeId(argumentId)))
                .ToList()
        };

        functionType = funcSymbol.IsCStructGetter
            ? new TyFun
            {
                Params = [new TyCon
                {
                    Name = WellKnownStrings.BuiltinTypes.RawPtr,
                    Id = new TypeId(BaseTypes.RawPtrId)
                }],
                Result = cfnFieldType
            }
            : new TyFun
            {
                Params =
                [
                    new TyCon
                    {
                        Name = WellKnownStrings.BuiltinTypes.RawPtr,
                        Id = new TypeId(BaseTypes.RawPtrId)
                    },
                    cfnFieldType
                ],
                Result = BaseTypes.Unit
            };
        return true;
    }
}
