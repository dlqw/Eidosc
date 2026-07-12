using System.Diagnostics.CodeAnalysis;
using Eidosc.Diagnostic;
using Eidosc.Mir;
using Eidosc.Types;

namespace Eidosc.CodeGen.Llvm;

/// <summary>
/// 类型降低器 - 将 Eidos 类型转换为 LLVM 类型
/// </summary>
public sealed class TypeLowering
{
    private readonly TypeInferer? _typeInferer;
    private readonly Dictionary<TypeId, LlvmType> _cache = new();
    private readonly Dictionary<StorageTypeCacheKey, LlvmType> _storageCache = new();
    private readonly Dictionary<int, string> _dynamicTypeKeyById = new();

    /// <summary>
    /// 结构化类型描述表（替代字符串 key 查询）。
    /// </summary>
    private readonly Dictionary<int, TypeDescriptor> _typeDescriptorById = new();

    /// <summary>
    /// 缓存：TypeId → 具名 LlvmStructType（用于 GEP field access）。
    /// Lower() 仍返回 ptr（ADT 值是堆分配的引用计数对象）。
    /// </summary>
    private readonly Dictionary<int, LlvmStructType> _structTypeByTypeId = new();

    /// <summary>
    /// ADT 构造器布局数据。
    /// </summary>
    private Dictionary<int, List<ConstructorTypeLayout>> _constructorLayouts = [];

    /// <summary>
    /// Gets the dynamic type keys currently registered for lowering.
    /// </summary>
    public IReadOnlyDictionary<int, string> DynamicTypeKeys => _dynamicTypeKeyById;

    /// <summary>
    /// Gets the structured type descriptors currently registered for lowering.
    /// </summary>
    public IReadOnlyDictionary<int, TypeDescriptor> TypeDescriptors => _typeDescriptorById;

    public bool StrictUnresolvedTypeVariables { get; set; } = true;

    public TypeLowering() { }

    public TypeLowering(TypeInferer typeInferer)
    {
        _typeInferer = typeInferer;
    }

    public void SetDynamicTypeKeys(IReadOnlyDictionary<int, string>? dynamicTypeKeys)
    {
        _dynamicTypeKeyById.Clear();
        if (dynamicTypeKeys != null)
        {
            foreach (var (typeId, typeKey) in dynamicTypeKeys)
            {
                _dynamicTypeKeyById[typeId] = typeKey;
            }
        }

        _cache.Clear();
        _storageCache.Clear();
    }

    /// <summary>
    /// 设置结构化类型描述表（替代字符串 key 查询）。
    /// </summary>
    public void SetTypeDescriptors(IReadOnlyDictionary<int, TypeDescriptor>? typeDescriptors)
    {
        _typeDescriptorById.Clear();
        if (typeDescriptors != null)
        {
            foreach (var (typeIdValue, descriptor) in typeDescriptors)
            {
                _typeDescriptorById[typeIdValue] = descriptor;
            }
        }

        _cache.Clear();
        _storageCache.Clear();
    }

    public bool TryGetDynamicTypeKey(TypeId typeId, out string typeKey)
    {
        return _dynamicTypeKeyById.TryGetValue(typeId.Value, out typeKey!);
    }

    public bool HasKnownLoweringMetadata(TypeId typeId)
    {
        if (!typeId.IsValid)
        {
            return false;
        }

        if (IsBuiltinLoweringType(typeId))
        {
            return true;
        }

        return _dynamicTypeKeyById.ContainsKey(typeId.Value) ||
               _typeDescriptorById.ContainsKey(typeId.Value) ||
               _constructorLayouts.ContainsKey(typeId.Value);
    }

    public bool TryGetTyConTypeArguments(
        TypeId typeId,
        out string constructorDescriptor,
        out List<TypeId> typeArguments)
    {
        if (TryGetTyConTypeArgumentsFromDescriptor(typeId, out constructorDescriptor, out typeArguments))
        {
            return true;
        }

        constructorDescriptor = string.Empty;
        typeArguments = [];
        if (!_dynamicTypeKeyById.TryGetValue(typeId.Value, out var typeKey) ||
            !TypeKeyParsing.TryParseTyConTypeKey(typeKey, out var constructorKey, out typeArguments))
        {
            return false;
        }

        constructorDescriptor = constructorKey.ToDescriptorString();
        return true;
    }

    public bool TryGetFunctionSignature(
        TypeId typeId,
        out List<TypeId> parameterTypes,
        out TypeId resultType)
    {
        if (TryGetFlattenedFunctionSignatureFromDescriptor(typeId, out parameterTypes, out resultType))
        {
            return true;
        }

        parameterTypes = [];
        resultType = TypeId.None;
        return _dynamicTypeKeyById.TryGetValue(typeId.Value, out var typeKey) &&
               TryResolveFlattenedFunctionType(typeKey, out parameterTypes, out resultType);
    }

    public bool TryGetDirectFunctionSignature(
        TypeId typeId,
        out List<TypeId> parameterTypes,
        out TypeId resultType)
    {
        if (TryGetFunctionSignatureFromDescriptor(typeId, out parameterTypes, out resultType))
        {
            return true;
        }

        parameterTypes = [];
        resultType = TypeId.None;

        return _dynamicTypeKeyById.TryGetValue(typeId.Value, out var typeKey) &&
               TypeKeyParsing.TryParseFunctionTypeKey(typeKey, out parameterTypes, out resultType);
    }

    /// <summary>
    /// 尝试通过 TypeDescriptor 获取 TypeId 的结构化类型描述。
    /// 优先使用 TypeDescriptor（O(1)），fallback 到字符串解析。
    /// </summary>
    public bool TryGetTypeDescriptor(TypeId typeId, [NotNullWhen(true)] out TypeDescriptor? descriptor)
    {
        return _typeDescriptorById.TryGetValue(typeId.Value, out descriptor);
    }

    /// <summary>
    /// 基于 TypeDescriptor 的 TyCon 参数查询（O(1)，无需字符串解析）。
    /// </summary>
    public bool TryGetTyConTypeArgumentsFromDescriptor(
        TypeId typeId,
        out string constructorDescriptor,
        out List<TypeId> typeArguments)
    {
        constructorDescriptor = string.Empty;
        typeArguments = [];

        if (!_typeDescriptorById.TryGetValue(typeId.Value, out var descriptor) ||
            descriptor is not TypeDescriptor.TyCon tyCon)
        {
            return false;
        }

        constructorDescriptor = tyCon.ConstructorDescriptor;
        typeArguments = [.. tyCon.TypeArgs];
        return true;
    }

    /// <summary>
    /// 基于 TypeDescriptor 的函数签名查询（O(1)，无需字符串解析）。
    /// </summary>
    public bool TryGetFunctionSignatureFromDescriptor(
        TypeId typeId,
        out List<TypeId> parameterTypes,
        out TypeId resultType)
    {
        parameterTypes = [];
        resultType = TypeId.None;

        if (!_typeDescriptorById.TryGetValue(typeId.Value, out var descriptor) ||
            descriptor is not TypeDescriptor.Function func)
        {
            return false;
        }

        parameterTypes = [.. func.ParamTypes];
        resultType = func.ReturnType;
        return true;
    }

    private bool TryGetFlattenedFunctionSignatureFromDescriptor(
        TypeId typeId,
        out List<TypeId> parameterTypes,
        out TypeId resultType)
    {
        return TryGetFlattenedFunctionSignatureFromDescriptor(
            typeId,
            [],
            out parameterTypes,
            out resultType);
    }

    private bool TryGetFlattenedFunctionSignatureFromDescriptor(
        TypeId typeId,
        HashSet<int> visitedNestedFunctionTypeIds,
        out List<TypeId> parameterTypes,
        out TypeId resultType)
    {
        parameterTypes = [];
        resultType = TypeId.None;

        if (!_typeDescriptorById.TryGetValue(typeId.Value, out var descriptor) ||
            descriptor is not TypeDescriptor.Function func)
        {
            return false;
        }

        parameterTypes = [.. func.ParamTypes];
        resultType = func.ReturnType;

        if (!_typeDescriptorById.TryGetValue(resultType.Value, out var resultDescriptor) ||
            resultDescriptor is not TypeDescriptor.Function)
        {
            return true;
        }

        if (!visitedNestedFunctionTypeIds.Add(resultType.Value))
        {
            parameterTypes = [];
            resultType = TypeId.None;
            return false;
        }

        if (!TryGetFlattenedFunctionSignatureFromDescriptor(
                resultType,
                visitedNestedFunctionTypeIds,
                out var nestedParameterTypes,
                out var nestedResultType))
        {
            return true;
        }

        parameterTypes.AddRange(nestedParameterTypes);
        resultType = nestedResultType;
        return true;
    }

    /// <summary>
    /// 降低类型
    /// </summary>
    public LlvmType Lower(TypeId typeId)
    {
        return Lower(typeId, allowOpenDynamicTypes: false);
    }

    public LlvmType LowerStorage(TypeId typeId)
    {
        return LowerStorage(typeId, allowOpenDynamicTypes: false);
    }

    public LlvmType LowerStorage(TypeId typeId, bool allowOpenDynamicTypes)
    {
        var cacheKey = new StorageTypeCacheKey(typeId, allowOpenDynamicTypes);
        if (_storageCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var lowered = NormalizeStorageType(Lower(typeId, allowOpenDynamicTypes));
        _storageCache[cacheKey] = lowered;
        return lowered;
    }

    public LlvmType Lower(TypeId typeId, bool allowOpenDynamicTypes)
    {
        if (_cache.TryGetValue(typeId, out var cached))
            return cached;

        if (!allowOpenDynamicTypes &&
            StrictUnresolvedTypeVariables &&
            _dynamicTypeKeyById.TryGetValue(typeId.Value, out var openCheckTypeKey) &&
            IsOpenDynamicTypeKey(typeId, openCheckTypeKey, []))
        {
            throw new TypeLoweringException(
                DiagnosticMessages.OpenDynamicTypeReachedLlvmLowering(openCheckTypeKey, typeId));
        }

        LlvmType result;
        if (_dynamicTypeKeyById.TryGetValue(typeId.Value, out var dynamicTypeKey) &&
            TryLowerDynamicType(dynamicTypeKey, allowOpenDynamicTypes, out var loweredDynamicType))
        {
            result = loweredDynamicType;
        }
        else if (_typeDescriptorById.TryGetValue(typeId.Value, out var descriptor) &&
                 TryLowerTypeDescriptor(typeId, descriptor, allowOpenDynamicTypes, out var loweredDescriptorType))
        {
            result = loweredDescriptorType;
        }
        else if (_constructorLayouts.ContainsKey(typeId.Value))
        {
            result = LlvmPointerType.VoidPtr();
        }
        else
        {
            result = typeId.Value switch
            {
                BaseTypes.IntId => LlvmIntType.I64,
                BaseTypes.FloatId => LlvmFloatType.Double,
                BaseTypes.BoolId => LlvmIntType.I1,
                BaseTypes.StringId => LlvmPointerType.VoidPtr(),
                BaseTypes.CharId => LlvmIntType.I32,
                BaseTypes.UnitId => LlvmVoidType.Instance,
                BaseTypes.TypeEqId => LlvmVoidType.Instance,
                BaseTypes.NeverId => LlvmVoidType.Instance,
                BaseTypes.ErasedCallableId => LlvmPointerType.VoidPtr(),
                BaseTypes.RawPtrId => LlvmPointerType.VoidPtr(),
                BaseTypes.CfnId => LlvmPointerType.VoidPtr(),
                _ when typeId == TypeId.None || !typeId.IsValid => LlvmPointerType.VoidPtr(),
                _ => LlvmPointerType.VoidPtr()
            };
        }

        _cache[typeId] = result;
        return result;
    }

    public bool IsOpenDynamicType(TypeId typeId)
    {
        return typeId.IsValid &&
               _dynamicTypeKeyById.TryGetValue(typeId.Value, out var typeKey) &&
               IsOpenDynamicTypeKey(typeId, typeKey, []);
    }

    private bool IsOpenDynamicTypeKey(TypeId typeId, string typeKey, HashSet<int> visitedTypeIds)
    {
        if (!typeId.IsValid || !visitedTypeIds.Add(typeId.Value))
        {
            return false;
        }

        if (typeKey.StartsWith("TyVar_", StringComparison.Ordinal))
        {
            return true;
        }

        if (TypeKeyParsing.TryParseTyConTypeKey(typeKey, out var constructorKey, out var typeArguments))
        {
            return constructorKey.Kind == TypeConstructorKeyKind.Variable ||
                   typeArguments.Any(IsOpenTypeId);
        }

        if (TryResolveFlattenedFunctionType(typeKey, out var parameterTypes, out var resultType))
        {
            return parameterTypes.Any(IsOpenTypeId) || IsOpenTypeId(resultType);
        }

        if (TypeKeyParsing.TryParseTupleTypeKey(typeKey, out var elementTypes))
        {
            return elementTypes.Any(IsOpenTypeId);
        }

        if (TypeKeyParsing.TryParseTypeDescriptor(typeKey, out var descriptor) &&
            descriptor is TypeDescriptor.Shared shared)
        {
            return IsOpenTypeId(shared.Inner);
        }

        return false;

        bool IsOpenTypeId(TypeId nestedTypeId)
        {
            return _dynamicTypeKeyById.TryGetValue(nestedTypeId.Value, out var nestedTypeKey) &&
                   IsOpenDynamicTypeKey(nestedTypeId, nestedTypeKey, visitedTypeIds);
        }
    }

    private bool TryLowerDynamicType(string typeKey, bool allowOpenDynamicTypes, out LlvmType loweredType)
    {
        loweredType = LlvmPointerType.VoidPtr();

        if (TryResolveFlattenedFunctionType(typeKey, out var parameterTypeIds, out var resultTypeId))
        {
            // Function-typed values are always represented as closure objects (opaque ptr).
            // The invoke function pointer is stored inside the closure struct, so a typed
            // function pointer is never used directly for function-typed parameters or locals.
            loweredType = LlvmPointerType.VoidPtr();
            return true;
        }

        if (TypeKeyParsing.TryParseTupleTypeKey(typeKey, out var elementTypes))
        {
            loweredType = new LlvmStructType
            {
                Fields = elementTypes
                    .Select(elementType => Lower(elementType, allowOpenDynamicTypes))
                    .ToList()
            };
            return true;
        }

        return false;
    }

    private bool TryLowerTypeDescriptor(
        TypeId typeId,
        TypeDescriptor descriptor,
        bool allowOpenDynamicTypes,
        out LlvmType loweredType)
    {
        loweredType = descriptor switch
        {
            TypeDescriptor.Builtin builtin => Lower(new TypeId(builtin.TypeIdValue), allowOpenDynamicTypes),
            TypeDescriptor.Function => LlvmPointerType.VoidPtr(),
            TypeDescriptor.Tuple tuple => new LlvmStructType
            {
                Fields = tuple.FieldTypes
                    .Select(fieldType => Lower(fieldType, allowOpenDynamicTypes))
                    .ToList()
            },
            TypeDescriptor.TyCon => LlvmPointerType.VoidPtr(),
            TypeDescriptor.Ref => LlvmPointerType.VoidPtr(),
            TypeDescriptor.MutRef => LlvmPointerType.VoidPtr(),
            TypeDescriptor.Shared => LlvmPointerType.VoidPtr(),
            TypeDescriptor.TypeVar typeVariable when allowOpenDynamicTypes => LlvmPointerType.VoidPtr(),
            TypeDescriptor.TypeVar typeVariable => throw new TypeLoweringException(
                DiagnosticMessages.UnresolvedTypeVariableReachedLlvm(typeVariable.Index)),
            _ => LlvmPointerType.VoidPtr()
        };

        return true;
    }

    /// <summary>
    /// 降低类型为 LLVM 类型
    /// </summary>
    public LlvmType LowerRaw(Eidosc.Types.Type? type)
    {
        return type switch
        {
            null => LlvmPointerType.VoidPtr(),
            TyVar var => LowerTypeVar(var),
            TyCon con => LowerConcrete(con),
            TyFun fun => LowerFunction(fun),
            TyTuple tuple => LowerTuple(tuple),
            TyRef or TyMutRef or TyShared => LlvmPointerType.VoidPtr(),
            TyReflProof => LlvmVoidType.Instance,
            EffectRow or EffectTag => LlvmPointerType.VoidPtr(),
            _ => throw new System.Diagnostics.UnreachableException()
        };
    }

    private LlvmType LowerTypeVar(TyVar var)
    {
        if (var.Instance != null)
        {
            return LowerRaw(var.Instance);
        }

        if (StrictUnresolvedTypeVariables)
        {
            throw new TypeLoweringException(
                DiagnosticMessages.UnresolvedTypeVariableReachedLlvm(var.Index));
        }

        return LlvmPointerType.VoidPtr();
    }

    private LlvmType LowerConcrete(TyCon con)
    {
        return con.Name switch
        {
            // 整数类型
            WellKnownStrings.BuiltinTypes.Int or WellKnownStrings.BuiltinTypes.Int64 => LlvmIntType.I64,
            WellKnownStrings.BuiltinTypes.Int32 => LlvmIntType.I32,
            WellKnownStrings.BuiltinTypes.Int16 => LlvmIntType.I16,
            WellKnownStrings.BuiltinTypes.Int8 => LlvmIntType.I8,

            // 浮点类型
            WellKnownStrings.BuiltinTypes.Float or WellKnownStrings.BuiltinTypes.Float64 => LlvmFloatType.Double,
            WellKnownStrings.BuiltinTypes.Float32 => LlvmFloatType.Float,
            WellKnownStrings.BuiltinTypes.Float16 => LlvmFloatType.Half,

            // 布尔类型
            WellKnownStrings.BuiltinTypes.Bool => LlvmIntType.I1,

            // Unit 类型
            WellKnownStrings.BuiltinTypes.Unit or WellKnownStrings.BuiltinTypes.UnitSyntax or WellKnownStrings.BuiltinTypes.TypeEq or WellKnownStrings.BuiltinTypes.Never => LlvmVoidType.Instance,

            // 字符串类型 (运行时表示为指针)
            WellKnownStrings.BuiltinTypes.String => LlvmPointerType.VoidPtr(),

            // FFI 裸指针类型 (不参与引用计数管理)
            WellKnownStrings.BuiltinTypes.RawPtr or WellKnownStrings.BuiltinTypes.Ptr => LlvmPointerType.VoidPtr(),

            // C 函数指针类型 (FFI 回调用)
            WellKnownStrings.BuiltinTypes.Cfn => LlvmPointerType.VoidPtr(),

            // 其他类型 (ADT, 能力等) 使用指针
            _ => LlvmPointerType.VoidPtr()
        };
    }

    private LlvmType LowerFunction(TyFun fun)
    {
        // 函数类型表示为闭包指针
        // 闭包包含：函数指针 + 捕获的环境
        return LlvmPointerType.VoidPtr();
    }

    private LlvmType LowerTuple(TyTuple tuple)
    {
        // 元组类型降低为结构体
        var fieldTypes = tuple.Elements.Select(e => NormalizeStorageType(LowerRaw(e))).ToList();
        return new LlvmStructType { Fields = fieldTypes };
    }

    public static LlvmType NormalizeStorageType(LlvmType type)
    {
        return type switch
        {
            LlvmVoidType => LlvmIntType.I1,
            LlvmArrayType arrayType => new LlvmArrayType
            {
                Element = NormalizeStorageType(arrayType.Element),
                Size = arrayType.Size
            },
            LlvmStructType structType => new LlvmStructType
            {
                Fields = structType.Fields.Select(NormalizeStorageType).ToList(),
                IsLiteral = structType.IsLiteral,
                Name = structType.Name
            },
            LlvmFunctionType functionType => new LlvmFunctionType
            {
                ReturnType = functionType.ReturnType,
                ParameterTypes = functionType.ParameterTypes
                    .Select(NormalizeFunctionParameterType)
                    .Select(NormalizeStorageType)
                    .ToList(),
                IsVarArg = functionType.IsVarArg
            },
            _ => type
        };
    }

    /// <summary>
    /// 设置 ADT 构造器类型布局数据。
    /// </summary>
    public void SetConstructorLayouts(IReadOnlyDictionary<int, List<ConstructorTypeLayout>>? layouts)
    {
        _constructorLayouts = layouts != null
            ? layouts.ToDictionary(kv => kv.Key, kv => kv.Value)
            : [];
        _structTypeByTypeId.Clear();
        _cache.Clear();
        _storageCache.Clear();
    }

    private readonly record struct StorageTypeCacheKey(TypeId TypeId, bool AllowOpenDynamicTypes);

    /// <summary>
    /// 获取 ADT 类型的具名 LLVM 结构体（用于 GEP field access）。
    /// Lower() 仍然返回 ptr（因为 ADT 值是堆分配的引用计数对象）。
    /// 返回 false 当没有可用的布局信息时（回退到 byte-offset GEP）。
    /// </summary>
    public bool TryGetStructType(TypeId typeId, [NotNullWhen(true)] out LlvmStructType? structType)
    {
        if (_structTypeByTypeId.TryGetValue(typeId.Value, out structType))
        {
            return structType != null;
        }

        structType = null;

        if (!_constructorLayouts.TryGetValue(typeId.Value, out var layouts) || layouts.Count == 0)
        {
            _structTypeByTypeId[typeId.Value] = null!;
            return false;
        }

        var isMultiCtor = layouts.Count > 1;

        // Build the struct name.
        var typeName = layouts[0].TypeName;
        if (string.IsNullOrEmpty(typeName))
        {
            typeName = $"adt_{typeId.Value}";
        }

        var structName = $"{WellKnownStrings.Mangling.Prefix}{NameMangler.SanitizeIdentifier(typeName)}";

        // Build fields: no tag field — tag is stored in EidosHeader, not in the payload area.
        // Use the precise storage type when every constructor that has a field at a
        // given ordinal agrees on that field type. Heterogeneous union slots fall
        // back to a machine word.
        var fields = new List<LlvmType>();

        // Use the largest constructor's field count, all padded to i64.
        var maxFieldCount = layouts.Max(l => l.FieldTypeIds.Count);

        // 如果所有构造器都没有字段，不需要 struct-typed GEP（空 struct 的 GEP index 无效）
        if (maxFieldCount == 0)
        {
            _structTypeByTypeId[typeId.Value] = null!;
            return false;
        }

        for (var i = 0; i < maxFieldCount; i++)
        {
            fields.Add(GetConstructorPayloadFieldStorageType(layouts, i));
        }

        structType = new LlvmStructType
        {
            Name = structName,
            IsLiteral = false,
            Fields = fields
        };

        _structTypeByTypeId[typeId.Value] = structType;
        return true;
    }

    private LlvmType GetConstructorPayloadFieldStorageType(
        IReadOnlyList<ConstructorTypeLayout> layouts,
        int fieldOrdinal)
    {
        LlvmType? commonType = null;
        foreach (var layout in layouts)
        {
            if (fieldOrdinal >= layout.FieldTypeIds.Count)
            {
                continue;
            }

            var fieldType = NormalizeStorageType(LowerStorage(layout.FieldTypeIds[fieldOrdinal], allowOpenDynamicTypes: true));
            if (commonType == null)
            {
                commonType = fieldType;
                continue;
            }

            if (!LlvmTypesEqual(commonType, fieldType))
            {
                return LlvmIntType.I64;
            }
        }

        return commonType ?? LlvmIntType.I64;
    }

    private static bool LlvmTypesEqual(LlvmType left, LlvmType right)
    {
        return left.ToIrString() == right.ToIrString();
    }

    /// <summary>
    /// 获取 ADT 类型的构造器布局（如果存在）。
    /// </summary>
    public bool TryGetConstructorLayouts(TypeId typeId, [NotNullWhen(true)] out List<ConstructorTypeLayout>? layouts)
    {
        return _constructorLayouts.TryGetValue(typeId.Value, out layouts);
    }

    /// <summary>
    /// 获取所有已注册的具名结构体类型。
    /// 主动从 _constructorLayouts 构建尚未缓存的结构体类型。
    /// </summary>
    public IEnumerable<LlvmStructType> GetAllStructTypes()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var typeId in _constructorLayouts.Keys)
        {
            // TryGetStructType lazily builds and caches the struct type
            if (TryGetStructType(new TypeId(typeId), out var structType) &&
                !string.IsNullOrEmpty(structType.Name) &&
                seen.Add(structType.Name))
            {
                yield return structType;
            }
        }
    }

    /// <summary>
    /// 通过 mangled 构造器名（如 "eidos_Some"）查找该构造器所属 ADT 的 struct type。
    /// </summary>
    public bool TryGetStructTypeByConstructorName(string constructorName, [NotNullWhen(true)] out LlvmStructType? structType)
    {
        structType = null;
        var rawName = constructorName.StartsWith(WellKnownStrings.Mangling.Prefix, StringComparison.Ordinal)
            ? constructorName[WellKnownStrings.Mangling.Prefix.Length..]
            : constructorName;

        foreach (var (_, layouts) in _constructorLayouts)
        {
            foreach (var layout in layouts)
            {
                if (string.Equals(layout.ConstructorName, rawName, StringComparison.Ordinal))
                {
                    // Found the right ADT. Now look up the struct type via any TypeId that has this layout.
                    // The layouts are shared across multiple TypeIds for the same ADT, so find one.
                    foreach (var (typeId, typeIdLayouts) in _constructorLayouts)
                    {
                        if (ReferenceEquals(typeIdLayouts, layouts))
                        {
                            return TryGetStructType(new TypeId(typeId), out structType);
                        }
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 获取 MirFunc 的 LLVM 函数类型
    /// </summary>
    public LlvmFunctionType GetFunctionType(MirFunc mirFunc)
    {
        var paramTypes = mirFunc.Locals
            .Where(l => l.IsParameter)
            .Select(p =>
            {
                var lowered = Lower(p.TypeId);
                return lowered is LlvmVoidType ? LlvmIntType.I1 : NormalizeStorageType(lowered);
            })
            .ToList();

        var loweredReturnType = Lower(mirFunc.ReturnType);
        var returnType = loweredReturnType is LlvmVoidType
            ? loweredReturnType
            : NormalizeStorageType(loweredReturnType);

        return new LlvmFunctionType
        {
            ReturnType = returnType,
            ParameterTypes = paramTypes
        };
    }

    /// <summary>
    /// 获取整数字面量的 LLVM 类型
    /// </summary>
    public static LlvmIntType GetIntTypeForLiteral(long value)
    {
        // 根据值范围选择合适的整数类型
        if (value >= int.MinValue && value <= int.MaxValue)
            return LlvmIntType.I32;
        return LlvmIntType.I64;
    }

    /// <summary>
    /// 获取浮点字面量的 LLVM 类型
    /// </summary>
    public static LlvmFloatType GetFloatTypeForLiteral(double value)
    {
        // 默认使用 double
        return LlvmFloatType.Double;
    }

    private static LlvmType NormalizeFunctionParameterType(LlvmType type)
    {
        return type is LlvmVoidType ? LlvmIntType.I1 : type;
    }

    private static bool IsBuiltinLoweringType(TypeId typeId)
    {
        return typeId.Value is
            BaseTypes.IntId or
            BaseTypes.FloatId or
            BaseTypes.BoolId or
            BaseTypes.StringId or
            BaseTypes.CharId or
            BaseTypes.UnitId or
            BaseTypes.TypeEqId or
            BaseTypes.NeverId or
            BaseTypes.ErasedCallableId or
            BaseTypes.RawPtrId or
            BaseTypes.CfnId;
    }

    private bool TryResolveFlattenedFunctionType(
        string typeKey,
        out List<TypeId> parameterTypes,
        out TypeId resultType)
    {
        return TryResolveFlattenedFunctionType(typeKey, [], out parameterTypes, out resultType);
    }

    private bool TryResolveFlattenedFunctionType(
        string typeKey,
        HashSet<int> visitedNestedFunctionTypeIds,
        out List<TypeId> parameterTypes,
        out TypeId resultType)
    {
        parameterTypes = [];
        resultType = TypeId.None;

        if (!TypeKeyParsing.TryParseFunctionTypeKey(typeKey, out var directParameterTypes, out var directResultType))
        {
            return false;
        }

        parameterTypes = new List<TypeId>(directParameterTypes);
        resultType = directResultType;

        if (!_dynamicTypeKeyById.TryGetValue(resultType.Value, out var nestedTypeKey))
        {
            return true;
        }

        if (!visitedNestedFunctionTypeIds.Add(resultType.Value))
        {
            parameterTypes = [];
            resultType = TypeId.None;
            return false;
        }

        if (!TryResolveFlattenedFunctionType(
                nestedTypeKey,
                visitedNestedFunctionTypeIds,
                out var nestedParameterTypes,
                out var nestedResultType))
        {
            return true;
        }

        parameterTypes.AddRange(nestedParameterTypes);
        resultType = nestedResultType;
        return true;
    }
}

public sealed class TypeLoweringException : Exception
{
    public TypeLoweringException(string message) : base(message)
    {
    }
}
