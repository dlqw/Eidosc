using Eidosc.Symbols;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Diagnostic;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Semantic;

/// <summary>
/// FFI 类型安全校验器。
/// 在类型推断完成后运行，校验 extern 函数的参数和返回值类型为 FFI 安全类型。
/// </summary>
public sealed class FfiTypeValidator
{
    private readonly List<Diagnostic.Diagnostic> _diagnostics = [];
    public List<Diagnostic.Diagnostic> Diagnostics => _diagnostics;

    /// <summary>
    /// FFI 安全的内置 TypeId 集合
    /// </summary>
    private static readonly HashSet<int> FfiSafeBaseTypeIds =
    [
        BaseTypes.IntId,       // Int / Int64 → i64
        BaseTypes.FloatId,     // Float / Float64 → f64
        BaseTypes.BoolId,      // Bool → i1
        BaseTypes.UnitId,      // Unit → void
        BaseTypes.RawPtrId     // RawPtr / Ptr[T] → ptr
    ];

    /// <summary>
    /// FFI 安全的 TyCon 名称集合
    /// </summary>
    private static readonly HashSet<string> FfiSafeTyConNames =
    [
        WellKnownStrings.BuiltinTypes.Int,      // Int → i64
        WellKnownStrings.BuiltinTypes.Float,    // Float → f64
        WellKnownStrings.BuiltinTypes.Bool,     // Bool → i1
        WellKnownStrings.BuiltinTypes.Unit,     // Unit → void
        WellKnownStrings.BuiltinTypes.Ptr,      // Ptr[T] → ptr（泛型指针）
        WellKnownStrings.BuiltinTypes.RawPtr,   // RawPtr → ptr
        "Int64",    // Int64 → i64
        "Int32",    // Int32 → i32
        "Int16",    // Int16 → i16
        "Int8",     // Int8 → i8
        "Float64",  // Float64 → f64
        "Float32",  // Float32 → f32
        "Float16",  // Float16 → f16
        WellKnownStrings.BuiltinTypes.Cfn       // Cfn[A..., Ret] → ptr（函数指针）
    ];

    /// <summary>
    /// 校验模块中所有 extern 函数的类型安全性和库引用合法性
    /// </summary>
    /// <param name=WellKnownStrings.Keywords.Module>AST 模块</param>
    /// <param name="linkLibraries">eidos.toml 配置的库名列表</param>
    /// <returns>true 如果没有错误</returns>
    public bool Validate(ModuleDecl module, IReadOnlyList<string>? linkLibraries = null)
    {
        var hasFfiFunctions = ValidateModule(module, linkLibraries, []);

        if (linkLibraries?.Count > 0 && !hasFfiFunctions)
        {
            foreach (var lib in linkLibraries)
            {
                AddLinkWithoutFfiWarning(lib);
            }
        }

        return !_diagnostics.Any(d => d.Level == Diagnostic.DiagnosticLevel.Error);
    }

    private bool ValidateModule(ModuleDecl module, IReadOnlyList<string>? linkLibraries, HashSet<string> seenBindings)
    {
        var hasFfiFunctions = false;

        foreach (var decl in module.Declarations)
        {
            if (decl is ModuleDecl nested)
            {
                if (ValidateModule(nested, linkLibraries, seenBindings))
                {
                    hasFfiFunctions = true;
                }

                continue;
            }

            if (!HasExternClause(decl))
            {
                continue;
            }

            hasFfiFunctions = true;

            // Validate the explicit extern linkage contract.
            if (decl is FuncDef or FuncDecl)
            {
                ValidateFfiLibraryReference(decl, linkLibraries);
                ValidateDuplicateFfiBinding(decl, seenBindings);
            }

            switch (decl)
            {
                case FuncDef func:
                    ValidateFfiFunction(func.InferredType, func.Name, func.Span);
                    break;
                case FuncDecl funcDecl:
                    ValidateFfiFunction(funcDecl.InferredType, funcDecl.Name, funcDecl.Span);
                    break;
            }
        }

        return hasFfiFunctions;
    }

    private void ValidateFfiLibraryReference(Declaration decl, IReadOnlyList<string>? availableLibraries)
    {
        var contract = ForeignContractIR.FromDeclaration(decl);
        var library = contract?.Library;
        if (string.IsNullOrWhiteSpace(library))
        {
            return;
        }

        var symbol = contract?.Name ?? GetDeclarationName(decl);
        if (availableLibraries == null || !availableLibraries.Contains(library))
        {
            _diagnostics.Add(new Diagnostic.Diagnostic(
                Diagnostic.DiagnosticLevel.Error,
                DiagnosticMessages.FfiUndeclaredLibraryReference(library, symbol),
                "E3052"));
        }
    }

    private void ValidateDuplicateFfiBinding(Declaration decl, HashSet<string> seenBindings)
    {
        if (!HasExternClause(decl))
        {
            return;
        }

        var contract = ForeignContractIR.FromDeclaration(decl);
        var library = contract?.Library ?? "<default>";
        var symbol = contract?.Name ?? GetDeclarationName(decl);
        var bindingKey = $"{library}/{symbol}";
        if (!seenBindings.Add(bindingKey))
        {
            AddDuplicateFfiBindingError(decl.Span, library, symbol);
        }
    }

    private static bool HasExternClause(Declaration decl) =>
        decl.Clauses.Any(static clause => clause.ClauseKind == DeclarationClauseKind.Extern);

    private static string GetDeclarationName(Declaration declaration) => declaration switch
    {
        FuncDef function => function.Name,
        FuncDecl function => function.Name,
        _ => "<unknown>"
    };

    private void ValidateFfiFunction(object? inferredType, string funcName, SourceSpan span)
    {
        if (inferredType is not TyFun funcType)
        {
            return;
        }

        // 遍历函数类型的参数链
        var current = funcType;
        var paramIndex = 0;
        while (true)
        {
            foreach (var paramType in current.Params)
            {
                if (!IsFfiSafeParameterType(paramType))
                {
                    var typeName = FormatTypeName(paramType);
                    AddFfiTypeError(span, typeName, DiagnosticMessages.FfiParameterRole, paramIndex + 1);
                }

                paramIndex++;
            }

            if (current.Result is TyFun next)
            {
                current = next;
                continue;
            }

            // 最终返回类型
            if (!IsFfiSafeReturnType(current.Result))
            {
                var typeName = FormatTypeName(current.Result);
                AddFfiTypeError(span, typeName, DiagnosticMessages.FfiReturnRole, -1);
            }

            break;
        }
    }

    /// <summary>
    /// 检查类型是否为 FFI 安全类型
    /// </summary>
    private static bool IsFfiSafeParameterType(Eidosc.Types.Type type)
    {
        return type switch
        {
            TyFun => true,
            TyVar or TyCon or TyTuple or TyRef or TyMutRef or TyShared or EffectRow or EffectTag => IsFfiSafeValueType(type),
            _ => throw new System.Diagnostics.UnreachableException()
        };
    }

    private static bool IsFfiSafeReturnType(Eidosc.Types.Type type)
    {
        return type switch
        {
            TyVar or TyCon or TyFun or TyTuple or TyRef or TyMutRef or TyShared or EffectRow or EffectTag => IsFfiSafeValueType(type),
            _ => throw new System.Diagnostics.UnreachableException()
        };
    }

    private static bool IsFfiSafeValueType(Eidosc.Types.Type type)
    {
        return type switch
        {
            TyCon tyCon => IsFfiSafeTyCon(tyCon),
            TyVar => false,       // 未解析的类型变量不安全
            TyFun => false,       // FFI 返回值不能直接构造 Eidos closure
            TyTuple => false,     // 元组不是 FFI 安全的
            TyRef => false,       // 引用类型不是 FFI 安全的
            TyMutRef => false,    // 可变引用不是 FFI 安全的
            TyShared => false,
            EffectRow => false,
            EffectTag => false,
            _ => throw new System.Diagnostics.UnreachableException()
        };
    }

    private static bool IsFfiSafeTyCon(TyCon tyCon)
    {
        // 检查基础 TypeId
        if (tyCon.Id.IsValid && FfiSafeBaseTypeIds.Contains(tyCon.Id.Value))
        {
            return true;
        }

        // 检查类型构造器名称
        return FfiSafeTyConNames.Contains(tyCon.Name);
    }

    private static string FormatTypeName(Eidosc.Types.Type type)
    {
        return type switch
        {
            TyCon tyCon => string.IsNullOrEmpty(tyCon.Name) ? $"TypeId({tyCon.Id.Value})" : tyCon.Name,
            TyVar tyVar => $"?{tyVar.Id}",
            TyFun => DiagnosticMessages.FfiTypeDisplayFunction,
            TyTuple => DiagnosticMessages.FfiTypeDisplayTuple,
            TyRef => DiagnosticMessages.FfiTypeDisplayReference,
            TyMutRef => DiagnosticMessages.FfiTypeDisplayMutableReference,
            TyShared => "shared handle",
            EffectRow => DiagnosticMessages.FfiTypeDisplayUnknown,
            EffectTag => DiagnosticMessages.FfiTypeDisplayUnknown,
            _ => throw new System.Diagnostics.UnreachableException()
        };
    }

    private void AddFfiTypeError(SourceSpan span, string typeName, string role, int paramIndex)
    {
        var location = paramIndex >= 0 ? DiagnosticMessages.FfiParameterLocation(paramIndex, role) : role;
        var message = DiagnosticMessages.FfiUnsafeTypeForFunctionLocation(typeName, location);

        var diag = new Diagnostic.Diagnostic(
            Diagnostic.DiagnosticLevel.Error,
            message,
            "E3051");

        diag.WithLabel(span, message);

        // 为 String 类型添加特别提示
        if (typeName == WellKnownStrings.BuiltinTypes.String)
        {
            diag = diag.WithHelp(DiagnosticMessages.FfiStringRequiresCstrConversionHelp);
        }

        _diagnostics.Add(diag);
    }

    private void AddLinkWithoutFfiWarning(string libraryName)
    {
        var message = DiagnosticMessages.FfiLinkWithoutFunction(libraryName);
        var diag = new Diagnostic.Diagnostic(
            Diagnostic.DiagnosticLevel.Warning,
            message,
            "W3050");

        _diagnostics.Add(diag);
    }

    private void AddDuplicateFfiBindingError(SourceSpan span, string library, string symbol)
    {
        var message = DiagnosticMessages.FfiDuplicateBinding(library, symbol);
        var diag = new Diagnostic.Diagnostic(
            Diagnostic.DiagnosticLevel.Error,
            message,
            "E3054");

        diag.WithLabel(span, message);

        _diagnostics.Add(diag);
    }
}
