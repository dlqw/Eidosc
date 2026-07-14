using Eidosc.Diagnostic;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Symbols;

/// <summary>
/// 符号表 - 管理所有符号和作用域
/// </summary>
public sealed partial class SymbolTable
{
    private int _nextSymbolId = 1;
    private int _nextTypeId = 100;
    private int _nextEffectId = 1;

    private readonly Dictionary<SymbolId, Symbol> _symbols = new();
    private readonly List<Scope> _scopeStack = [];
    private readonly Dictionary<string, SymbolId> _globalTypes = new();
    private readonly Dictionary<string, SymbolId> _globalTraits = new();
    private readonly Dictionary<string, SymbolId> _globalConstructors = new();
    private readonly Dictionary<string, SymbolId> _globalAbilities = new();

    /// <summary>
    /// Trait 实现注册表
    /// Key: ImplLookupKey -> ImplSymbol list
    /// </summary>
    private readonly Dictionary<ImplLookupKey, List<ImplSymbol>> _impls = new();
    private readonly Dictionary<SymbolId, List<ImplSymbol>> _implsByTrait = new();

    /// <summary>
    /// TypeId → Symbol 的反向索引（懒构建，_symbolByTypeIdDirty 时重建）
    /// </summary>
    private Dictionary<TypeId, Symbol>? _symbolByTypeId;
    private bool _symbolByTypeIdDirty = true;

    /// <summary>
    /// 父 Trait → 子 Trait 列表的反向索引（懒构建）
    /// </summary>
    private Dictionary<SymbolId, List<SymbolId>>? _traitsByParent;
    private bool _traitsByParentDirty = true;

    /// <summary>
    /// 模块注册表
    /// </summary>
    private readonly ModuleRegistry _modules;

    /// <summary>
    /// 路径解析器
    /// </summary>
    private readonly PathResolver _pathResolver;

    /// <summary>
    /// 当前作用域
    /// </summary>
    public Scope? CurrentScope => _scopeStack.Count > 0 ? _scopeStack[^1] : null;

    /// <summary>
    /// 内置符号作用域
    /// </summary>
    public Scope? BuiltinScope => _scopeStack.Count > 0 ? _scopeStack[0] : null;

    /// <summary>
    /// 当前作用域深度
    /// </summary>
    public int CurrentDepth => CurrentScope?.Depth ?? -1;

    public int NextSymbolIdValue => _nextSymbolId;

    public int NextTypeIdValue => _nextTypeId;

    public int NextEffectIdValue => _nextEffectId;

    internal void EnsureIdCountersAtLeast(int nextSymbolId, int nextTypeId, int nextEffectId)
    {
        _nextSymbolId = Math.Max(_nextSymbolId, nextSymbolId);
        _nextTypeId = Math.Max(_nextTypeId, nextTypeId);
        _nextEffectId = Math.Max(_nextEffectId, nextEffectId);
    }

    public IReadOnlyList<Scope> ScopeStack => _scopeStack;

    public IReadOnlyDictionary<string, SymbolId> GlobalTypes => _globalTypes;

    public IReadOnlyDictionary<string, SymbolId> GlobalTraits => _globalTraits;

    public IReadOnlyDictionary<string, SymbolId> GlobalConstructors => _globalConstructors;

    public IReadOnlyDictionary<string, SymbolId> GlobalAbilities => _globalAbilities;

    private Scope GetRequiredCurrentScope()
    {
        return CurrentScope ?? throw new InvalidOperationException(DiagnosticMessages.CurrentScopeRequired);
    }

    /// <summary>
    /// 所有符号
    /// </summary>
    public IReadOnlyDictionary<SymbolId, Symbol> Symbols => _symbols;

    /// <summary>
    /// 模块注册表
    /// </summary>
    public ModuleRegistry Modules => _modules;

    /// <summary>
    /// 路径解析器
    /// </summary>
    public PathResolver PathResolver => _pathResolver;

    #region ID 生成

    /// <summary>
    /// 生成新的符号 ID
    /// </summary>
    public SymbolId NewSymbolId() => new(_nextSymbolId++);

    /// <summary>
    /// 生成新的类型 ID
    /// </summary>
    public TypeId NewTypeId() => new(_nextTypeId++);

    /// <summary>
    /// 生成新的效应 ID
    /// </summary>
    public EffectId NewEffectId() => new(_nextEffectId++);

    #endregion

    /// <summary>
    /// 默认构造函数
    /// </summary>
    public SymbolTable()
    {
        _modules = new ModuleRegistry(this);
        _pathResolver = new PathResolver(this, _modules);
        _scopeStack.Add(new Scope { Kind = ScopeKind.Module });

        // 注册内置类型
        RegisterBuiltinTypes();
        RegisterBuiltinFunctions();
    }

    /// <summary>
    /// 注册内置类型
    /// </summary>
    private void RegisterBuiltinTypes()
    {
        // 基本类型及其 TypeId 映射
        var builtinTypes = new (string Name, TypeId TypeId)[]
        {
            (WellKnownStrings.BuiltinTypes.Int, new TypeId(WellKnownTypeIds.IntId)),
            (WellKnownStrings.BuiltinTypes.Float, new TypeId(WellKnownTypeIds.FloatId)),
            (WellKnownStrings.BuiltinTypes.Bool, new TypeId(WellKnownTypeIds.BoolId)),
            (WellKnownStrings.BuiltinTypes.String, new TypeId(WellKnownTypeIds.StringId)),
            (WellKnownStrings.BuiltinTypes.Char, new TypeId(WellKnownTypeIds.CharId)),
            (WellKnownStrings.BuiltinTypes.Unit, new TypeId(WellKnownTypeIds.UnitId)),
            ("ErasedCallable", new TypeId(WellKnownTypeIds.ErasedCallableId)),
            (WellKnownStrings.BuiltinTypes.TypeEq, new TypeId(WellKnownTypeIds.TypeEqId)),
            (WellKnownStrings.BuiltinTypes.Never, new TypeId(WellKnownTypeIds.NeverId)),
            (WellKnownStrings.BuiltinTypes.Type, new TypeId(WellKnownTypeIds.TypeId))
        };

        foreach (var (typeName, typeId) in builtinTypes)
        {
            var symbol = new AdtSymbol
            {
                Id = NewSymbolId(),
                Name = typeName,
                IsModuleLevel = true,
                TypeId = typeId
            };
            var id = RegisterSymbol(symbol);
            CurrentScope!.BindType(typeName, id);
            _globalTypes[typeName] = id;
        }

        var listSymbol = new AdtSymbol
        {
            Id = NewSymbolId(),
            Name = WellKnownStrings.BuiltinTypes.Seq,
            IsModuleLevel = true,
            TypeId = NewTypeId(),
            TypeParams = [SymbolId.None]
        };
        var listId = RegisterSymbol(listSymbol);
        CurrentScope!.BindType(WellKnownStrings.BuiltinTypes.Seq, listId);
        _globalTypes[WellKnownStrings.BuiltinTypes.Seq] = listId;

        var refSymbol = new AdtSymbol
        {
            Id = NewSymbolId(),
            Name = WellKnownStrings.BuiltinTypes.Ref,
            IsModuleLevel = true,
            TypeId = NewTypeId(),
            TypeParams = [SymbolId.None]
        };
        var refId = RegisterSymbol(refSymbol);
        CurrentScope!.BindType(WellKnownStrings.BuiltinTypes.Ref, refId);
        _globalTypes[WellKnownStrings.BuiltinTypes.Ref] = refId;

        var mutRefSymbol = new AdtSymbol
        {
            Id = NewSymbolId(),
            Name = WellKnownStrings.BuiltinTypes.MRef,
            IsModuleLevel = true,
            TypeId = NewTypeId(),
            TypeParams = [SymbolId.None]
        };
        var mutRefId = RegisterSymbol(mutRefSymbol);
        CurrentScope!.BindType(WellKnownStrings.BuiltinTypes.MRef, mutRefId);
        _globalTypes[WellKnownStrings.BuiltinTypes.MRef] = mutRefId;
        CurrentScope!.BindType(WellKnownStrings.BuiltinTypes.MutRef, mutRefId);
        _globalTypes[WellKnownStrings.BuiltinTypes.MutRef] = mutRefId;

        // FFI 裸指针类型（不参与引用计数）
        var rawPtrSymbol = new AdtSymbol
        {
            Id = NewSymbolId(),
            Name = WellKnownStrings.BuiltinTypes.RawPtr,
            IsModuleLevel = true,
            TypeId = new TypeId(WellKnownTypeIds.RawPtrId)
        };
        var rawPtrId = RegisterSymbol(rawPtrSymbol);
        CurrentScope!.BindType(WellKnownStrings.BuiltinTypes.RawPtr, rawPtrId);
        _globalTypes[WellKnownStrings.BuiltinTypes.RawPtr] = rawPtrId;

        // FFI 泛型指针类型 Ptr[T]
        var ptrSymbol = new AdtSymbol
        {
            Id = NewSymbolId(),
            Name = WellKnownStrings.BuiltinTypes.Ptr,
            IsModuleLevel = true,
            TypeId = new TypeId(WellKnownTypeIds.RawPtrId),
            TypeParams = [SymbolId.None]
        };
        var ptrId = RegisterSymbol(ptrSymbol);
        CurrentScope!.BindType(WellKnownStrings.BuiltinTypes.Ptr, ptrId);
        _globalTypes[WellKnownStrings.BuiltinTypes.Ptr] = ptrId;

        // FFI C 函数指针类型 Cfn[A..., Ret]
        var cfnSymbol = new AdtSymbol
        {
            Id = NewSymbolId(),
            Name = WellKnownStrings.BuiltinTypes.Cfn,
            IsModuleLevel = true,
            TypeId = new TypeId(WellKnownTypeIds.CfnId),
            TypeParams = [SymbolId.None]
        };
        var cfnId = RegisterSymbol(cfnSymbol);
        CurrentScope!.BindType(WellKnownStrings.BuiltinTypes.Cfn, cfnId);
        _globalTypes[WellKnownStrings.BuiltinTypes.Cfn] = cfnId;

        // 保持作用域开启，让后续声明在同一个模块作用域中

        // 内建能力符号（无需用户声明即可使用）
        RegisterBuiltinEffect(WellKnownStrings.BuiltinAbilities.FFI);
        RegisterBuiltinEffect(WellKnownStrings.BuiltinAbilities.IO);

        MetaSchemaRegistry.Register(this);
    }

    private void RegisterBuiltinEffect(string name)
    {
        var symbol = new EffectSymbol
        {
            Id = NewSymbolId(),
            Name = name,
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            IsPublic = true,
            TypeId = NewTypeId()
        };
        var id = RegisterSymbol(symbol);
        CurrentScope!.BindEffect(name, id);
        _globalAbilities[name] = id;
    }

    /// <summary>
    /// 注册内置函数
    /// </summary>
    // ── 数据驱动的内建函数表 ──

    private static readonly TypeId T_Int = new(WellKnownTypeIds.IntId);
    private static readonly TypeId T_Float = new(WellKnownTypeIds.FloatId);
    private static readonly TypeId T_Bool = new(WellKnownTypeIds.BoolId);
    private static readonly TypeId T_String = new(WellKnownTypeIds.StringId);
    private static readonly TypeId T_Char = new(WellKnownTypeIds.CharId);
    private static readonly TypeId T_Unit = new(WellKnownTypeIds.UnitId);
    private static readonly TypeId T_RawPtr = new(WellKnownTypeIds.RawPtrId);

    private static readonly string[] A_IO = [WellKnownStrings.BuiltinAbilities.IO];
    private static readonly string[] A_FFI = [WellKnownStrings.BuiltinAbilities.FFI];

    /// <summary>
    /// 内建函数规格：名称、返回类型、参数类型、隐式 abilities、intrinsic role。
    /// </summary>
    private sealed record BuiltinSpec(
        string Name,
        TypeId ReturnType,
        TypeId[] Params,
        string[]? Effects = null,
        BuiltinIntrinsicRole Role = BuiltinIntrinsicRole.None);

    private static readonly BuiltinSpec[] s_builtinFunctions =
    [
        // IO: 打印
        new("print_int", T_Unit, [T_Int], A_IO),
        new("print_float", T_Unit, [T_Float], A_IO),
        new("print_string", T_Unit, [T_String], A_IO),
        new("print_newline", T_Unit, [], A_IO),
        new("print_char", T_Unit, [T_Int], A_IO),

        // 纯函数: 字符串操作
        new("string_length", T_Int, [T_String]),
        new("string_char_at", T_Int, [T_String, T_Int]),
        new("string_slice", T_String, [T_String, T_Int, T_Int]),
        new("string_equals", T_Bool, [T_String, T_String]),
        new("string_from_char", T_String, [T_Int]),
        new("char_from_code", T_Char, [T_Int]),
        new("char_to_code", T_Int, [T_Char]),

        // 纯函数: 类型转换
        new("int_to_string", T_String, [T_Int]),
        new("int_to_float", T_Float, [T_Int]),
        new("string_to_float", T_Float, [T_String]),

        // IO: 读取
        new("read_line", T_String, [], A_IO),
        new("read_char", T_Int, [], A_IO),

        // IO: 终端
        new("terminal_set_raw", T_Unit, [], A_IO),
        new("terminal_restore", T_Unit, [], A_IO),

        // IO: 睡眠
        new("sleep_ms", T_Unit, [T_Int], A_IO),

        // IO: 错误状态查询
        new("io_last_success", T_Bool, [], A_IO),
        new("io_last_error", T_String, [], A_IO),

        // IO: 文件操作
        new("file_exists", T_Bool, [T_String], A_IO),
        new("file_read_all_text", T_String, [T_String], A_IO),
        new("file_write_all_text", T_Bool, [T_String, T_String], A_IO),

        // IO: HTTP 操作
        new("http_get_text", T_String, [T_String], A_IO),
        new("http_request_text", T_String, [T_String, T_String, T_String, T_String], A_IO),
        new("http_request_text_with_headers", T_String, [T_String, T_String, T_String, T_String, T_String], A_IO),
        new("http_request_text_with_options", T_String, [T_String, T_String, T_String, T_String, T_String, T_Int, T_Int], A_IO),
        new("http_request_body_hex_with_options", T_String, [T_String, T_String, T_String, T_String, T_String, T_Int, T_Int], A_IO),
        new("http_request_text_with_binary_body_options", T_String, [T_String, T_String, T_String, T_String, T_String, T_Int, T_Int], A_IO),
        new("http_request_body_hex_with_binary_body_options", T_String, [T_String, T_String, T_String, T_String, T_String, T_Int, T_Int], A_IO),
        new("http_last_status_code", T_Int, [], A_IO),
        new("http_last_effective_url", T_String, [], A_IO),
        new("http_last_content_type", T_String, [], A_IO),
        new("http_last_headers", T_String, [], A_IO),

        // 纯函数: 浮点数学 intrinsic
        new("math_sin", T_Float, [T_Float]),
        new("math_cos", T_Float, [T_Float]),
        new("math_tan", T_Float, [T_Float]),
        new("math_asin", T_Float, [T_Float]),
        new("math_acos", T_Float, [T_Float]),
        new("math_atan", T_Float, [T_Float]),
        new("math_atan2", T_Float, [T_Float, T_Float]),
        new("math_sqrt", T_Float, [T_Float]),
        new("math_exp", T_Float, [T_Float]),
        new("math_log", T_Float, [T_Float]),
        new("math_log2", T_Float, [T_Float]),
        new("math_log10", T_Float, [T_Float]),
        new("math_pow", T_Float, [T_Float, T_Float]),
        new("math_fabs", T_Float, [T_Float]),
        new("math_ceil", T_Float, [T_Float]),
        new("math_floor", T_Float, [T_Float]),
        new("math_round", T_Float, [T_Float]),
        new("math_trunc", T_Float, [T_Float]),
        new("math_fma", T_Float, [T_Float, T_Float, T_Float]),
        new("math_copysign", T_Float, [T_Float, T_Float]),
        new("math_fmin", T_Float, [T_Float, T_Float]),
        new("math_fmax", T_Float, [T_Float, T_Float]),

        // IO: 时间操作 intrinsic
        new("time_now", T_Int, [], A_IO),
        new("time_now_ms", T_Int, [], A_IO),
        new("time_format", T_Int, [T_Int, T_RawPtr, T_Int, T_RawPtr], A_IO),
        new("time_year", T_Int, [T_Int]),
        new("time_month", T_Int, [T_Int]),
        new("time_day", T_Int, [T_Int]),
        new("time_hour", T_Int, [T_Int]),
        new("time_minute", T_Int, [T_Int]),
        new("time_second", T_Int, [T_Int]),

        // FFI: 正则操作 intrinsic
        new("regex_compile", T_RawPtr, [T_RawPtr, T_Int], A_FFI),
        new("regex_free", T_Unit, [T_RawPtr], A_FFI),
        new("regex_is_match", T_Int, [T_RawPtr, T_RawPtr], A_FFI),
        new("regex_find", T_Int, [T_RawPtr, T_RawPtr, T_RawPtr, T_Int], A_FFI),
        new("regex_find_string", T_String, [T_RawPtr, T_RawPtr], A_FFI),

        // FFI: 辅助函数
        new("string_to_cstr", T_RawPtr, [T_String], A_FFI),
        new("cstr_to_string", T_String, [T_RawPtr], A_FFI),

        // FFI: 指针基础
        new(WellKnownStrings.InternalNames.PtrNull, T_RawPtr, []),
        new(WellKnownStrings.InternalNames.PtrIsNull, T_Bool, [T_RawPtr]),
        new(WellKnownStrings.InternalNames.PtrEquals, T_Bool, [T_RawPtr, T_RawPtr]),

        // FFI: 指针算术与内存操作
        new("ptr_add", T_RawPtr, [T_RawPtr, T_Int], A_FFI),

        // FFI: 类型化指针读取
        new(WellKnownStrings.InternalNames.PtrLoadInt, T_Int, [T_RawPtr], A_FFI),
        new(WellKnownStrings.InternalNames.PtrStoreInt, T_Unit, [T_RawPtr, T_Int], A_FFI),
        new(WellKnownStrings.InternalNames.PtrLoadFloat, T_Float, [T_RawPtr], A_FFI),
        new(WellKnownStrings.InternalNames.PtrLoadPtr, T_RawPtr, [T_RawPtr], A_FFI),
        new(WellKnownStrings.InternalNames.PtrLoadI32, T_Int, [T_RawPtr], A_FFI),
        new(WellKnownStrings.InternalNames.PtrLoadI8, T_Int, [T_RawPtr], A_FFI),
        new(WellKnownStrings.InternalNames.PtrLoadBool, T_Bool, [T_RawPtr], A_FFI),

        // FFI: 类型化指针写入
        new(WellKnownStrings.InternalNames.PtrStoreFloat, T_Unit, [T_RawPtr, T_Float], A_FFI),
        new(WellKnownStrings.InternalNames.PtrStorePtr, T_Unit, [T_RawPtr, T_RawPtr], A_FFI),
        new(WellKnownStrings.InternalNames.PtrStoreI32, T_Unit, [T_RawPtr, T_Int], A_FFI),
        new(WellKnownStrings.InternalNames.PtrStoreI8, T_Unit, [T_RawPtr, T_Int], A_FFI),
        new(WellKnownStrings.InternalNames.PtrStoreBool, T_Unit, [T_RawPtr, T_Bool], A_FFI),

        // FFI: 回调函数指针
        new("cfn_from", T_RawPtr, [T_RawPtr], A_FFI),
        new("cfn_call", TypeId.None, [TypeId.None], A_FFI),

        // FFI: generic value boxing intrinsics
        new(WellKnownStrings.InternalNames.ValueBox, T_RawPtr, [TypeId.None], A_FFI, BuiltinIntrinsicRole.ValueBox),
        new(WellKnownStrings.InternalNames.ValueUnbox, TypeId.None, [T_RawPtr], A_FFI, BuiltinIntrinsicRole.ValueUnbox),
        new(WellKnownStrings.InternalNames.ValueBoxFree, T_Unit, [T_RawPtr], A_FFI, BuiltinIntrinsicRole.ValueBoxFree),
    ];

    private void RegisterBuiltinFunctions()
    {
        foreach (var spec in s_builtinFunctions)
        {
            RegisterBuiltinFunction(spec.Name, spec.ReturnType, spec.Params, spec.Effects, spec.Role);
        }
    }

    private void RegisterBuiltinFunction(
        string name,
        TypeId returnType,
        IReadOnlyList<TypeId> parameterTypes,
        string[]? implicitAbilities = null,
        BuiltinIntrinsicRole builtinIntrinsicRole = BuiltinIntrinsicRole.None)
    {
        var parameters = new List<SymbolId>(parameterTypes.Count);
        for (var i = 0; i < parameterTypes.Count; i++)
        {
            parameters.Add(SymbolId.None);
        }

        var symbol = new FuncSymbol
        {
            Id = NewSymbolId(),
            Name = name,
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = false,
            Parameters = parameters,
            ParamTypes = parameterTypes.ToList(),
            ReturnType = returnType,
            ImplicitAbilities = implicitAbilities?.ToList() ?? [],
            BuiltinIntrinsicRole = builtinIntrinsicRole
        };

        var id = RegisterSymbol(symbol);
        CurrentScope!.BindFunction(name, id);
    }

    /// <summary>
    /// 注册 @cstruct 字段访问器内置函数
    /// </summary>
    public void RegisterCStructAccessor(
        string name,
        SourceSpan span,
        int fieldOffset,
        TypeId fieldTypeId,
        bool isGetter)
    {
        var returnType = isGetter ? fieldTypeId : new TypeId(WellKnownTypeIds.UnitId);
        List<TypeId> paramTypes = isGetter
            ? [new TypeId(WellKnownTypeIds.RawPtrId)]
            : [new TypeId(WellKnownTypeIds.RawPtrId), fieldTypeId];

        var symbol = new FuncSymbol
        {
            Id = NewSymbolId(),
            Name = name,
            Span = span,
            IsModuleLevel = true,
            HasBody = false,
            Parameters = new List<SymbolId>(new SymbolId[paramTypes.Count]),
            ParamTypes = paramTypes,
            ReturnType = returnType,
            IsCStructAccessor = true,
            CStructFieldOffset = fieldOffset,
            CStructFieldTypeId = fieldTypeId,
            IsCStructGetter = isGetter
        };

        var id = RegisterSymbol(symbol);
        CurrentScope!.BindFunction(name, id);
    }

    #region 作用域管理

    /// <summary>
    /// 进入新作用域
    /// </summary>
    public void PushScope(ScopeKind kind = ScopeKind.Block)
    {
        var parent = _scopeStack.Count > 0 ? CurrentScope : null;
        var scope = new Scope(parent) { Kind = kind };
        _scopeStack.Add(scope);
    }

    public ScopeGuard PushScopeGuard(ScopeKind kind = ScopeKind.Block)
    {
        PushScope(kind);
        return new ScopeGuard(this);
    }

    /// <summary>
    /// 使用指定父作用域进入新作用域
    /// </summary>
    public Scope PushScopeWithParent(ScopeKind kind, Scope? parent)
    {
        var scope = new Scope(parent) { Kind = kind };
        _scopeStack.Add(scope);
        return scope;
    }

    /// <summary>
    /// 重新进入已存在的作用域
    /// </summary>
    public void PushScope(Scope scope)
    {
        _scopeStack.Add(scope);
    }

    public ScopeGuard PushScopeGuard(Scope scope)
    {
        PushScope(scope);
        return new ScopeGuard(this);
    }

    /// <summary>
    /// 退出当前作用域
    /// </summary>
    public void PopScope()
    {
        if (_scopeStack.Count > 1)
        {
            _scopeStack.RemoveAt(_scopeStack.Count - 1);
        }
    }

    public readonly struct ScopeGuard(SymbolTable? symbolTable) : IDisposable
    {
        private readonly SymbolTable? _symbolTable = symbolTable;

        public void Dispose()
        {
            _symbolTable?.PopScope();
        }
    }

    /// <summary>
    /// 初始化全局作用域
    /// </summary>
    public void InitializeGlobalScope()
    {
        if (_scopeStack.Count == 0)
        {
            _scopeStack.Add(new Scope { Kind = ScopeKind.Module });
        }

        if (_scopeStack.Count == 1)
        {
            PushScopeWithParent(ScopeKind.Module, BuiltinScope);
        }
    }

    #endregion

    #region 符号注册

    /// <summary>
    /// 注册符号
    /// </summary>
    public SymbolId RegisterSymbol(Symbol symbol)
    {
        var id = symbol.Id.IsValid ? symbol.Id : NewSymbolId();
        var replacesExistingSymbol = _symbols.ContainsKey(id);
        var symbolWithId = symbol with { Id = id };
        _symbols[id] = symbolWithId;
        if (replacesExistingSymbol)
        {
            _modules.InvalidateAccessibleBindingCache();
        }
        InvalidateIndices();
        return id;
    }

    /// <summary>
    /// 注册变量符号并绑定到当前作用域
    /// </summary>
    public SymbolId DeclareVariable(
        string name,
        SourceSpan span,
        bool isMutable = false,
        bool isParameter = false,
        bool isPatternBound = false,
        PatternBindingMode bindingMode = PatternBindingMode.ByValue,
        bool isComptime = false,
        bool isPublic = true)
    {
        var symbol = new VarSymbol
        {
            Name = name,
            Span = span,
            IsMutable = isMutable,
            IsComptime = isComptime,
            IsParameter = isParameter,
            IsPatternBound = isPatternBound,
            BindingMode = bindingMode,
            IsModuleLevel = CurrentScope?.Kind == ScopeKind.Module,
            IsPublic = isPublic
        };

        var id = RegisterSymbol(symbol);

        if (CurrentScope == null)
        {
            // 没有作用域，无法声明变量
            return SymbolId.None;
        }

        if (!CurrentScope.BindValue(name, id))
        {
            // 重复定义错误，但仍注册符号
        }

        return id;
    }

    /// <summary>
    /// 注册函数符号
    /// </summary>
    public SymbolId DeclareFunction(string name, SourceSpan span, bool hasBody = true, bool isPublic = true, bool isComptime = false)
    {
        var symbol = new FuncSymbol
        {
            Name = name,
            Span = span,
            HasBody = hasBody,
            IsComptime = isComptime,
            IsModuleLevel = CurrentScope?.Kind == ScopeKind.Module,
            IsPublic = isPublic
        };

        var id = RegisterSymbol(symbol);
        CurrentScope?.BindFunction(name, id);
        return id;
    }

    /// <summary>
    /// 注册类型参数
    /// </summary>
    public SymbolId DeclareTypeParameter(
        string name,
        SourceSpan span,
        string kindAnnotation = "kind1",
        bool isComptime = false,
        string? comptimeTypeAnnotation = null,
        GenericParameterKind parameterKind = GenericParameterKind.Type)
    {
        var currentScope = GetRequiredCurrentScope();
        var symbol = new TypeParamSymbol
        {
            Name = name,
            Span = span,
            KindAnnotation = string.IsNullOrWhiteSpace(kindAnnotation) ? "kind1" : kindAnnotation,
            ParameterKind = parameterKind,
            IsComptime = isComptime,
            ComptimeTypeAnnotation = string.IsNullOrWhiteSpace(comptimeTypeAnnotation) ? null : comptimeTypeAnnotation
        };

        var id = RegisterSymbol(symbol);
        if (parameterKind == GenericParameterKind.Value)
        {
            currentScope.BindValue(name, id);
        }
        else
        {
            currentScope.BindType(name, id);
        }
        return id;
    }

    /// <summary>
    /// 注册 ADT 类型
    /// </summary>
    public SymbolId DeclareAdt(
        string name,
        SourceSpan span,
        IReadOnlyList<SymbolId>? typeParams = null,
        bool isPublic = true)
    {
        var currentScope = GetRequiredCurrentScope();
        var symbol = new AdtSymbol
        {
            Name = name,
            Span = span,
            IsModuleLevel = CurrentScope?.Kind == ScopeKind.Module,
            IsPublic = isPublic,
            TypeId = NewTypeId(),
            TypeParams = typeParams?.ToList() ?? []
        };

        var id = RegisterSymbol(symbol);
        currentScope.BindType(name, id);
        _globalTypes[name] = id;
        return id;
    }

    /// <summary>
    /// 注册构造器
    /// </summary>
    public SymbolId DeclareConstructor(string name, SourceSpan span, SymbolId ownerAdt, bool isPublic = true)
    {
        var currentScope = GetRequiredCurrentScope();
        var symbol = new CtorSymbol
        {
            Name = name,
            Span = span,
            OwnerAdt = ownerAdt,
            IsPublic = isPublic
        };

        var id = RegisterSymbol(symbol);
        currentScope.BindConstructor(name, id);
        _globalConstructors[name] = id;
        return id;
    }

    /// <summary>
    /// 注册能力 (Unison-style)
    /// </summary>
    public SymbolId DeclareEffect(string name, SourceSpan span, bool isPublic = true)
    {
        var currentScope = GetRequiredCurrentScope();
        var symbol = new EffectSymbol
        {
            Name = name,
            Span = span,
            IsModuleLevel = CurrentScope?.Kind == ScopeKind.Module,
            IsPublic = isPublic,
            TypeId = NewTypeId()
        };

        var id = RegisterSymbol(symbol);
        currentScope.BindEffect(name, id);
        _globalAbilities[name] = id;
        return id;
    }

    /// <summary>
    /// 注册 Trait
    /// </summary>
    public SymbolId DeclareTrait(
        string name,
        SourceSpan span,
        IReadOnlyList<SymbolId>? typeParams = null,
        bool isPublic = true)
    {
        var currentScope = GetRequiredCurrentScope();
        var symbol = new TraitSymbol
        {
            Name = name,
            Span = span,
            IsModuleLevel = CurrentScope?.Kind == ScopeKind.Module,
            IsPublic = isPublic,
            TypeId = NewTypeId(),
            TypeParams = typeParams?.ToList() ?? []
        };

        var id = RegisterSymbol(symbol);
        currentScope.BindTrait(name, id);
        _globalTraits[name] = id;
        return id;
    }

    /// <summary>
    /// 注册模块
    /// </summary>
    public SymbolId DeclareModule(
        string name,
        List<string> path,
        SourceSpan span,
        bool isPublic = true,
        bool usesExplicitExports = false,
        string? packageAlias = null,
        string? packageInstanceKey = null)
    {
        var symbol = new ModuleSymbol
        {
            Name = name,
            PackageAlias = packageAlias,
            PackageInstanceKey = packageInstanceKey,
            Path = path,
            Span = span,
            IsPublic = isPublic,
            UsesExplicitExports = usesExplicitExports
        };

        var id = RegisterSymbol(symbol);

        // 注册到模块注册表
        _modules.RegisterModule(symbol with { Id = id }, id);

        return id;
    }

    /// <summary>
    /// 添加成员到模块
    /// </summary>
    public void AddMemberToModule(SymbolId moduleId, SymbolId memberId)
    {
        _modules.AddMemberToModule(moduleId, memberId);
    }

    /// <summary>
    /// 查找模块
    /// </summary>
    public SymbolId? LookupModule(string name)
    {
        return _modules.LookupRootModule(name);
    }

    #endregion

    #region 符号查找

    /// <summary>
    /// 获取符号
    /// </summary>
    public Symbol? GetSymbol(SymbolId id)
    {
        return _symbols.TryGetValue(id, out var symbol) ? symbol : null;
    }

    /// <summary>
    /// 获取类型化符号
    /// </summary>
    public T? GetSymbol<T>(SymbolId id) where T : Symbol
    {
        return GetSymbol(id) as T;
    }

    /// <summary>
    /// 查找变量/函数
    /// </summary>
    public SymbolId? LookupValue(string name)
    {
        return CurrentScope?.LookupValue(name);
    }

    public IReadOnlyList<SymbolId> LookupValueCandidates(string name)
    {
        return CurrentScope?.LookupValueCandidates(name) ?? [];
    }

    public IReadOnlyList<SymbolId> LookupLocalValueCandidates(string name)
    {
        return CurrentScope?.LookupLocalValueCandidates(name) ?? [];
    }

    /// <summary>
    /// 查找类型
    /// </summary>
    public SymbolId? LookupType(string name)
    {
        var currentScope = CurrentScope;
        if (currentScope == null)
        {
            return _globalTypes.TryGetValue(name, out var globalIdWithoutScope) ? globalIdWithoutScope : null;
        }

        // 先查找当前作用域链
        var result = currentScope.LookupType(name);
        if (result != null)
            return result;

        var traitResult = currentScope.LookupTrait(name);
        if (traitResult != null && !_globalTypes.ContainsKey(name))
            return traitResult;

        // 再查找全局类型
        if (_globalTypes.TryGetValue(name, out var globalId))
            return globalId;

        return _globalTraits.TryGetValue(name, out var globalTraitId) ? globalTraitId : null;
    }

    /// <summary>
    /// 查找 trait
    /// </summary>
    public SymbolId? LookupTrait(string name)
    {
        var currentScope = CurrentScope;
        if (currentScope == null)
        {
            return _globalTraits.TryGetValue(name, out var globalIdWithoutScope) ? globalIdWithoutScope : null;
        }

        var result = currentScope.LookupTrait(name);
        if (result != null)
            return result;

        return _globalTraits.TryGetValue(name, out var globalId) ? globalId : null;
    }

    /// <summary>
    /// 查找能力
    /// </summary>
    public SymbolId? LookupEffect(string name)
    {
        return CurrentScope?.LookupEffect(name);
    }

    public SymbolId? LookupBuiltinEffect(string name)
    {
        return name is WellKnownStrings.BuiltinAbilities.FFI or WellKnownStrings.BuiltinAbilities.IO &&
               _globalAbilities.TryGetValue(name, out var symbolId)
            ? symbolId
            : null;
    }

    /// <summary>
    /// 查找构造器
    /// </summary>
    public SymbolId? LookupConstructor(string name)
    {
        var currentScope = CurrentScope;
        if (currentScope == null)
        {
            return _globalConstructors.TryGetValue(name, out var globalIdWithoutScope) ? globalIdWithoutScope : null;
        }

        // 先查找当前作用域链
        var result = currentScope.LookupConstructor(name);
        if (result != null)
            return result;

        // 再查找全局构造器
        return _globalConstructors.TryGetValue(name, out var globalId) ? globalId : null;
    }

    /// <summary>
    /// 解析路径（如 std::collection::List）
    /// </summary>
    public SymbolId? ResolvePath(IReadOnlyList<string> path, SymbolId? context = null)
    {
        var result = _pathResolver.Resolve(path, context);
        return result.IsSuccess ? result.SymbolId : null;
    }

    /// <summary>
    /// 使用 PathResolver 解析路径并返回完整结果
    /// </summary>
    public PathResolutionResult ResolvePathWithResult(IReadOnlyList<string> path, SymbolId? context = null)
    {
        return _pathResolver.Resolve(path, context);
    }

    #endregion

    #region 懒构建索引

    /// <summary>
    /// 标记索引为脏，下次访问时重建。
    /// </summary>
    private void InvalidateIndices()
    {
        _symbolByTypeIdDirty = true;
        _traitsByParentDirty = true;
    }

    /// <summary>
    /// 获取或构建 TypeId → Symbol 反向索引。
    /// </summary>
    private Dictionary<TypeId, Symbol> GetSymbolByTypeIdIndex()
    {
        if (!_symbolByTypeIdDirty && _symbolByTypeId is not null)
        {
            return _symbolByTypeId;
        }

        _symbolByTypeId = new Dictionary<TypeId, Symbol>();
        foreach (var (_, symbol) in _symbols)
        {
            if (symbol.TypeId.IsValid && !_symbolByTypeId.ContainsKey(symbol.TypeId))
            {
                _symbolByTypeId[symbol.TypeId] = symbol;
            }
        }
        _symbolByTypeIdDirty = false;
        return _symbolByTypeId;
    }

    /// <summary>
    /// 通过 TypeId 查找对应的 Symbol（使用反向索引，O(1)）。
    /// </summary>
    public Symbol? GetSymbolByTypeId(TypeId typeId)
    {
        if (!typeId.IsValid)
        {
            return null;
        }

        return GetSymbolByTypeIdIndex().TryGetValue(typeId, out var symbol) ? symbol : null;
    }

    /// <summary>
    /// 获取或构建父 Trait → 子 Trait 列表反向索引。
    /// 遍历所有 TraitSymbol 的 ParentTraits，将子 trait 注册到每个祖先 trait 下。
    /// </summary>
    private Dictionary<SymbolId, List<SymbolId>> GetTraitsByParentIndex()
    {
        if (!_traitsByParentDirty && _traitsByParent is not null)
        {
            return _traitsByParent;
        }

        _traitsByParent = new Dictionary<SymbolId, List<SymbolId>>();
        foreach (var (symbolId, symbol) in _symbols)
        {
            if (symbol is TraitSymbol trait && trait.ParentTraits.Count > 0)
            {
                var visited = new HashSet<SymbolId>();
                var stack = new Stack<SymbolId>(trait.ParentTraits);
                while (stack.Count > 0)
                {
                    var current = stack.Pop();
                    if (!visited.Add(current))
                    {
                        continue;
                    }

                    if (!_traitsByParent.TryGetValue(current, out var children))
                    {
                        children = [];
                        _traitsByParent[current] = children;
                    }
                    children.Add(symbolId);

                    if (_symbols.TryGetValue(current, out var parentSymbol) &&
                        parentSymbol is TraitSymbol parentTrait)
                    {
                        foreach (var grandparent in parentTrait.ParentTraits)
                        {
                            stack.Push(grandparent);
                        }
                    }
                }
            }
        }
        _traitsByParentDirty = false;
        return _traitsByParent;
    }

    /// <summary>
    /// 获取直接或间接继承自指定父 trait 的所有子 trait SymbolId（使用反向索引，O(1)）。
    /// </summary>
    public IReadOnlyList<SymbolId> GetChildTraits(SymbolId parentTraitId)
    {
        return GetTraitsByParentIndex().TryGetValue(parentTraitId, out var children)
            ? children
            : Array.Empty<SymbolId>();
    }

    #endregion

    #region 符号更新

    /// <summary>
    /// 更新符号
    /// </summary>
    public void UpdateSymbol(Symbol symbol)
    {
        if (symbol.Id.IsValid)
        {
            _symbols[symbol.Id] = symbol;
            _modules.InvalidateAccessibleBindingCache();
            InvalidateIndices();
        }
    }

    public void RestoreNamerBindings(
        IReadOnlyList<RestoredScopeBinding> scopes,
        IReadOnlyDictionary<string, SymbolId> globalTypes,
        IReadOnlyDictionary<string, SymbolId> globalTraits,
        IReadOnlyDictionary<string, SymbolId> globalConstructors,
        IReadOnlyDictionary<string, SymbolId> globalAbilities)
    {
        var builtinScope = BuiltinScope;
        var builtinGlobalTypes = new Dictionary<string, SymbolId>(_globalTypes, StringComparer.Ordinal);
        var builtinGlobalTraits = new Dictionary<string, SymbolId>(_globalTraits, StringComparer.Ordinal);
        var builtinGlobalConstructors = new Dictionary<string, SymbolId>(_globalConstructors, StringComparer.Ordinal);
        var builtinGlobalAbilities = new Dictionary<string, SymbolId>(_globalAbilities, StringComparer.Ordinal);

        _scopeStack.Clear();
        if (builtinScope != null)
        {
            _scopeStack.Add(builtinScope);
        }

        foreach (var restored in scopes.OrderBy(static scope => scope.Index))
        {
            var parent = restored.ParentIndex >= 0 && restored.ParentIndex < _scopeStack.Count
                ? _scopeStack[restored.ParentIndex]
                : builtinScope;
            var scope = new Scope(parent) { Kind = restored.Kind };
            foreach (var (name, overloads) in restored.FunctionOverloads.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
            {
                foreach (var symbolId in overloads)
                {
                    scope.BindFunction(name, symbolId);
                }
            }

            foreach (var (name, symbolId) in restored.Bindings.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
            {
                if (!restored.FunctionOverloads.ContainsKey(name))
                {
                    scope.BindValue(name, symbolId);
                }
            }

            foreach (var (name, symbolId) in restored.Types.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
            {
                scope.BindType(name, symbolId);
            }

            foreach (var (name, symbolId) in restored.Traits.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
            {
                scope.BindTrait(name, symbolId);
            }

            foreach (var (name, symbolId) in restored.Effects.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
            {
                scope.BindEffect(name, symbolId);
            }

            foreach (var (name, symbolId) in restored.Constructors.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
            {
                scope.BindConstructor(name, symbolId);
            }

            _scopeStack.Add(scope);
        }

        if (_scopeStack.Count == 0)
        {
            _scopeStack.Add(new Scope { Kind = ScopeKind.Module });
        }

        _globalTypes.Clear();
        CopySymbolMap(builtinGlobalTypes, _globalTypes);
        CopySymbolMap(globalTypes, _globalTypes);
        _globalTraits.Clear();
        CopySymbolMap(builtinGlobalTraits, _globalTraits);
        CopySymbolMap(globalTraits, _globalTraits);
        _globalConstructors.Clear();
        CopySymbolMap(builtinGlobalConstructors, _globalConstructors);
        CopySymbolMap(globalConstructors, _globalConstructors);
        _globalAbilities.Clear();
        CopySymbolMap(builtinGlobalAbilities, _globalAbilities);
        CopySymbolMap(globalAbilities, _globalAbilities);

        RebuildImplIndexes();
        _modules.InvalidateAccessibleBindingCache();
        InvalidateIndices();
        AdvanceCountersPastRegisteredSymbols();
    }

    // After restoring namer bindings the fresh-id counters must skip every
    // pre-allocated SymbolId/TypeId that was re-registered from the persisted
    // payload, otherwise subsequent NewSymbolId()/NewTypeId() calls during
    // restored type inference would collide with the restored ids. This is the
    // restore-path counterpart of the per-symbol counter bump; it must NOT run
    // in the regular RegisterSymbol path, where re-registering a symbol with a
    // caller-supplied id (e.g. MirGenericSpecializer unit tests) would silently
    // shift downstream id allocation.
    private void AdvanceCountersPastRegisteredSymbols()
    {
        foreach (var symbol in _symbols.Values)
        {
            if (symbol.Id.IsValid && symbol.Id.Value >= _nextSymbolId)
            {
                _nextSymbolId = symbol.Id.Value + 1;
            }

            if (symbol.TypeId.IsValid && symbol.TypeId.Value >= _nextTypeId)
            {
                _nextTypeId = symbol.TypeId.Value + 1;
            }
        }
    }

    private static void CopySymbolMap(
        IReadOnlyDictionary<string, SymbolId> source,
        Dictionary<string, SymbolId> target)
    {
        foreach (var (name, symbolId) in source.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            if (symbolId.IsValid)
            {
                target[name] = symbolId;
            }
        }
    }

    private void RebuildImplIndexes()
    {
        _impls.Clear();
        _implsByTrait.Clear();
        foreach (var symbol in _symbols.Values.OfType<ImplSymbol>().OrderBy(static symbol => symbol.Id.Value))
        {
            if (!symbol.Trait.IsValid || !symbol.ImplementingType.IsValid)
            {
                continue;
            }

            var key = new ImplLookupKey(
                symbol.Trait,
                symbol.ImplementingType,
                NormalizeTraitTypeArgKeys(symbol.TraitTypeArgKeys, symbol.TraitTypeArgs));
            if (!_impls.TryGetValue(key, out var impls))
            {
                impls = [];
                _impls[key] = impls;
            }

            impls.Add(symbol);
            AddImplToTraitIndex(symbol);
        }
    }

    /// <summary>
    /// 添加构造器到 ADT
    /// </summary>
    public void AddConstructorToAdt(SymbolId adtId, SymbolId ctorId)
    {
        if (_symbols.TryGetValue(adtId, out var symbol) && symbol is AdtSymbol adt)
        {
            var ctors = new List<SymbolId>(adt.Constructors) { ctorId };
            _symbols[adtId] = adt with { Constructors = ctors };
            _modules.InvalidateAccessibleBindingCache();
            InvalidateIndices();
        }
    }

    /// <summary>
    /// 注册 Trait 实现
    /// </summary>
    public SymbolId DeclareImpl(
        SymbolId trait,
        TypeId implementingType,
        SourceSpan span,
        IReadOnlyList<string>? traitTypeArgs = null,
        string? implementingTypeDisplay = null,
        string? canonicalImplementingType = null,
        IReadOnlyList<string>? canonicalTraitTypeArgs = null,
        IReadOnlyList<ImplTypeRefKey>? traitTypeArgKeys = null,
        IReadOnlyList<ImplTypeRefKey>? canonicalTraitTypeArgKeys = null,
        IReadOnlyList<ImplTypeArgTraitRequirement>? implementingTypeRequirements = null,
        ImplHeadShape? implHeadShape = null,
        ImplTypeRefKey? implementingTypeKey = null)
    {
        if (!trait.IsValid || !implementingType.IsValid)
        {
            return SymbolId.None;
        }

        var normalizedTraitTypeArgs = NormalizeAndValidateTraitTypeArgs(traitTypeArgs);
        var normalizedCanonicalTraitTypeArgs = NormalizeAndValidateTraitTypeArgs(canonicalTraitTypeArgs);
        if (normalizedCanonicalTraitTypeArgs.Length == 0 && normalizedTraitTypeArgs.Length > 0)
        {
            normalizedCanonicalTraitTypeArgs = normalizedTraitTypeArgs.ToArray();
        }
        var normalizedTraitTypeArgKeys = NormalizeTraitTypeArgKeys(traitTypeArgKeys, normalizedTraitTypeArgs);
        var normalizedCanonicalTraitTypeArgKeys = NormalizeTraitTypeArgKeys(
            canonicalTraitTypeArgKeys,
            normalizedCanonicalTraitTypeArgs);
        IReadOnlyList<ImplTypeRefKey> explicitCanonicalTraitTypeArgKeys = canonicalTraitTypeArgKeys is { Count: > 0 }
            ? normalizedCanonicalTraitTypeArgKeys
            : Array.Empty<ImplTypeRefKey>();
        var normalizedImplementingTypeDisplay = implementingTypeDisplay?.Trim() ?? "";
        var normalizedCanonicalImplementingType = canonicalImplementingType?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(normalizedCanonicalImplementingType))
        {
            normalizedCanonicalImplementingType = InferCanonicalImplementingType(implementingType);
        }
        var normalizedImplementingTypeKey = implementingTypeKey is { IsEmpty: false } providedImplementingTypeKey
            ? providedImplementingTypeKey
            : ShouldBuildDefaultImplementingTypeKey(
                normalizedCanonicalImplementingType,
                normalizedImplementingTypeDisplay)
                ? BuildDefaultImplementingTypeKey(implementingType)
                : ImplTypeRefKey.Empty;

        var key = new ImplLookupKey(trait, implementingType, normalizedTraitTypeArgKeys);
        if (_impls.TryGetValue(key, out var existingImpls))
        {
            var normalizedRequirements = NormalizeImplementingTypeRequirements(implementingTypeRequirements);
            var existingImpl = existingImpls.FirstOrDefault(impl =>
                string.Equals(impl.ImplementingTypeDisplay, normalizedImplementingTypeDisplay, StringComparison.Ordinal) &&
                string.Equals(impl.CanonicalImplementingType, normalizedCanonicalImplementingType, StringComparison.Ordinal) &&
                impl.ImplementingTypeKey.Equals(normalizedImplementingTypeKey) &&
                impl.TraitTypeArgs.SequenceEqual(normalizedTraitTypeArgs) &&
                impl.TraitTypeArgKeys.SequenceEqual(normalizedTraitTypeArgKeys) &&
                impl.CanonicalTraitTypeArgs.SequenceEqual(normalizedCanonicalTraitTypeArgs) &&
                impl.CanonicalTraitTypeArgKeys.SequenceEqual(normalizedCanonicalTraitTypeArgKeys) &&
                impl.ImplementingTypeRequirements.SequenceEqual(normalizedRequirements));
            if (existingImpl != null)
            {
                return existingImpl.Id;
            }
        }

        var traitName = GetSymbol(trait)?.Name ?? $"trait#{trait.Value}";
        var typeName = GetTypeName(implementingType);
        var traitArgsDisplay = normalizedTraitTypeArgs.Length == 0
            ? ""
            : $"[{string.Join(", ", normalizedTraitTypeArgs)}]";
        var implSymbol = new ImplSymbol
        {
            Name = $"impl {traitName}{traitArgsDisplay} for {typeName}",
            Span = span,
            IsModuleLevel = CurrentScope?.Kind == ScopeKind.Module,
            Trait = trait,
            ImplementingType = implementingType,
            ImplementingTypeDisplay = normalizedImplementingTypeDisplay,
            CanonicalImplementingType = normalizedCanonicalImplementingType,
            ImplementingTypeKey = normalizedImplementingTypeKey,
            TraitTypeArgs = normalizedTraitTypeArgs.ToList(),
            TraitTypeArgKeys = normalizedTraitTypeArgKeys.ToList(),
            CanonicalTraitTypeArgs = normalizedCanonicalTraitTypeArgs.ToList(),
            CanonicalTraitTypeArgKeys = normalizedCanonicalTraitTypeArgKeys.ToList(),
            TraitTypeArgShapes = implHeadShape?.TraitArgs.ToList() ??
                                 BuildTraitArgShapesFromKeysOrText(
                                     normalizedTraitTypeArgKeys,
                                     explicitCanonicalTraitTypeArgKeys,
                                     normalizedCanonicalTraitTypeArgs),
            ImplementingTypeShape = implHeadShape?.ImplementingType ??
                                    BuildImplementingTypeShapeFromKeyOrText(
                                        normalizedImplementingTypeKey,
                                        normalizedCanonicalImplementingType,
                                        implementingType),
            ImplementingTypeRequirements = NormalizeImplementingTypeRequirements(implementingTypeRequirements)
        };

        var implId = RegisterSymbol(implSymbol);
        if (!_impls.TryGetValue(key, out var impls))
        {
            impls = [];
            _impls[key] = impls;
        }

        impls.Add(implSymbol with { Id = implId });
        AddImplToTraitIndex(implSymbol with { Id = implId });
        return implId;
    }

    public IReadOnlyList<ImplSymbol> GetImplsForTrait(SymbolId traitId)
    {
        if (!traitId.IsValid ||
            !_implsByTrait.TryGetValue(traitId, out var impls))
        {
            return [];
        }

        return impls;
    }

    /// <summary>
    /// 将方法符号关联到 Trait 实现。
    /// </summary>
    public void AddMethodToImpl(SymbolId implId, SymbolId methodId)
    {
        if (!_symbols.TryGetValue(implId, out var symbol) ||
            symbol is not ImplSymbol impl ||
            !methodId.IsValid)
        {
            return;
        }

        if (impl.Methods.Contains(methodId))
        {
            return;
        }

        var updatedImpl = impl with { Methods = [.. impl.Methods, methodId] };
        UpdateImplSymbol(updatedImpl);
    }

    /// <summary>
    /// 将方法符号关联到 Trait 实现，并记录对应的 trait 方法身份。
    /// </summary>
    public void AddMethodToImpl(SymbolId implId, SymbolId methodId, SymbolId traitMethodId)
    {
        if (!_symbols.TryGetValue(implId, out var symbol) ||
            symbol is not ImplSymbol impl ||
            !methodId.IsValid)
        {
            return;
        }

        var traitMethodImplementations = new Dictionary<SymbolId, SymbolId>(impl.TraitMethodImplementations);
        if (traitMethodId.IsValid)
        {
            traitMethodImplementations[traitMethodId] = methodId;
        }

        if (impl.Methods.Contains(methodId) &&
            (!traitMethodId.IsValid ||
             (impl.TraitMethodImplementations.TryGetValue(traitMethodId, out var existingMethodId) &&
              existingMethodId == methodId)))
        {
            return;
        }

        var updatedImpl = impl with
        {
            Methods = impl.Methods.Contains(methodId)
                ? new List<SymbolId>(impl.Methods)
                : [.. impl.Methods, methodId],
            TraitMethodImplementations = traitMethodImplementations
        };
        UpdateImplSymbol(updatedImpl);
    }

    private void UpdateImplSymbol(ImplSymbol updatedImpl)
    {
        _symbols[updatedImpl.Id] = updatedImpl;
        _modules.InvalidateAccessibleBindingCache();
        InvalidateIndices();

        var implKey = new ImplLookupKey(
            updatedImpl.Trait,
            updatedImpl.ImplementingType,
            NormalizeTraitTypeArgKeys(updatedImpl.TraitTypeArgKeys, updatedImpl.TraitTypeArgs));
        if (!_impls.TryGetValue(implKey, out var impls))
        {
            return;
        }

        for (var i = 0; i < impls.Count; i++)
        {
            if (impls[i].Id == updatedImpl.Id)
            {
                impls[i] = updatedImpl;
                break;
            }
        }

        UpdateImplTraitIndex(updatedImpl);
    }

    private void AddImplToTraitIndex(ImplSymbol impl)
    {
        if (!_implsByTrait.TryGetValue(impl.Trait, out var impls))
        {
            impls = [];
            _implsByTrait[impl.Trait] = impls;
        }

        impls.Add(impl);
    }

    private void UpdateImplTraitIndex(ImplSymbol updatedImpl)
    {
        if (!_implsByTrait.TryGetValue(updatedImpl.Trait, out var impls))
        {
            AddImplToTraitIndex(updatedImpl);
            return;
        }

        for (var i = 0; i < impls.Count; i++)
        {
            if (impls[i].Id == updatedImpl.Id)
            {
                impls[i] = updatedImpl;
                return;
            }
        }

        impls.Add(updatedImpl);
    }

    #endregion
}
