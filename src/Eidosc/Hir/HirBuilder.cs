using Eidosc.Symbols;
using System.Collections.Frozen;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Mir.Closure;
using Eidosc.Diagnostic;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;

// Type alias to resolve ambiguity
using DrivenType = Eidosc.Types.Type;

namespace Eidosc.Hir;

/// <summary>
/// HIR 构建器 - 将 AST 转换为 HIR
/// </summary>
public sealed partial class HirBuilder
{
    private static readonly FrozenDictionary<Ast.BinaryOp, Hir.BinaryOp> AstToHirBinaryOp = new Dictionary<Ast.BinaryOp, Hir.BinaryOp>
    {
        [Ast.BinaryOp.Add] = Hir.BinaryOp.Add,
        [Ast.BinaryOp.Subtract] = Hir.BinaryOp.Sub,
        [Ast.BinaryOp.Multiply] = Hir.BinaryOp.Mul,
        [Ast.BinaryOp.Divide] = Hir.BinaryOp.Div,
        [Ast.BinaryOp.Modulo] = Hir.BinaryOp.Mod,
        [Ast.BinaryOp.Equal] = Hir.BinaryOp.Eq,
        [Ast.BinaryOp.NotEqual] = Hir.BinaryOp.Ne,
        [Ast.BinaryOp.Less] = Hir.BinaryOp.Lt,
        [Ast.BinaryOp.LessEqual] = Hir.BinaryOp.Le,
        [Ast.BinaryOp.Greater] = Hir.BinaryOp.Gt,
        [Ast.BinaryOp.GreaterEqual] = Hir.BinaryOp.Ge,
        [Ast.BinaryOp.And] = Hir.BinaryOp.And,
        [Ast.BinaryOp.Or] = Hir.BinaryOp.Or,
        [Ast.BinaryOp.Concat] = Hir.BinaryOp.Concat,
    }.ToFrozenDictionary();

    private sealed record StdlibDesugaring(string ModulePath, string FuncName, bool SwapArgs);

    private static readonly FrozenDictionary<Ast.BinaryOp, StdlibDesugaring> StdlibOperatorDesugaring = new Dictionary<Ast.BinaryOp, StdlibDesugaring>
    {
        [Ast.BinaryOp.Bind] = new("std.Monad.Monad", "bind", false),
        [Ast.BinaryOp.Coalesce] = new("std.Option", "unwrap_or", false),
        [Ast.BinaryOp.ComposeRight] = new("std.Functions", "compose", true),
        [Ast.BinaryOp.ComposeLeft] = new("std.Functions", "compose", false),
        [Ast.BinaryOp.Fmap] = new("std.Functor.Functor", "fmap", true),
        [Ast.BinaryOp.Ap] = new("std.Applicative.Applicative", "apply", false),
        [Ast.BinaryOp.Append] = new("std.Semigroup.Semigroup", "append", false),
        [Ast.BinaryOp.Prepend] = new("std.Seq", "cons", false),
    }.ToFrozenDictionary();

    private readonly SymbolTable _symbolTable;
    private readonly TypeInferer? _typeInferer;
    private readonly EffectInferer? _abilityInferer;
    private readonly FreeVariableAnalyzer _freeVariableAnalyzer = new();
    private readonly TypeIdRegistry _typeRegistry;
    private readonly CaptureCollector _captureCollector;
    private IReadOnlyDictionary<SymbolId, int> _activeValueGenericParameterIndices =
        new Dictionary<SymbolId, int>();

    public List<Diagnostic.Diagnostic> Diagnostics { get; } = [];
    public IReadOnlySet<TypeId> CopyLikeTypeIds => _typeRegistry.CopyLikeTypeIds;
    public IReadOnlyDictionary<TypeId, string> DynamicTypeKeys => _typeRegistry.DynamicTypeKeys;
    public IReadOnlyDictionary<int, List<ConstructorTypeLayout>> ConstructorLayouts => _typeRegistry.ConstructorLayouts;
    public string? EntryFunctionName { get; init; }
    public IReadOnlyDictionary<int, TypeDescriptor> TypeDescriptors => _typeRegistry.TypeDescriptors;

    public HirBuilder(SymbolTable symbolTable, TypeInferer? typeInferer = null, EffectInferer? abilityInferer = null)
    {
        _symbolTable = symbolTable;
        _typeInferer = typeInferer;
        _abilityInferer = abilityInferer;
        _typeRegistry = new TypeIdRegistry(symbolTable, typeInferer);
        _captureCollector = new CaptureCollector(symbolTable, _typeRegistry);
    }

    private TypeId GetOrCreateDynamicTypeId(string typeKey) => _typeRegistry.GetOrCreateDynamicTypeId(typeKey);
    private TypeId GetOrCreateDynamicTypeId(TypeDescriptor descriptor) => _typeRegistry.GetOrCreateDynamicTypeId(descriptor);

    /// <summary>
    /// 从 AST 模块构建 HIR 模块
    /// </summary>
    public HirModule Build(ModuleDecl moduleDecl, IReadOnlyList<string>? linkLibraries = null)
    {
        Diagnostics.Clear();
        _typeRegistry.Clear();

        var moduleName = moduleDecl.Path.Count > 0 ? moduleDecl.Path[0] : "Main";

        var hirModule = new HirModule
        {
            Name = moduleName,
            PackageAlias = moduleDecl.PackageAlias,
            PackageInstanceKey = moduleDecl.PackageInstanceKey,
            Path = moduleDecl.Path,
            Span = moduleDecl.Span,
            SymbolId = moduleDecl.SymbolId,
            LinkLibraries = linkLibraries?.ToList() ?? []
        };

        // 递归展平模块声明，确保导入/预编译模块中的函数定义可进入后续 MIR/LLVM 阶段。
        AppendModuleDeclarations(moduleDecl, hirModule.Declarations, isRootModule: true);

        // 从符号表收集 named instance 注册的 trait 实现，生成 HirImpl 节点。
        // impl 方法（FuncDef）已在 AppendModuleDeclarations 中作为 HirFunc 转换，
        // 这里根据 ImplSymbol 的语义信息将它们关联到对应的 HirImpl。
        CollectImplDeclarations(hirModule);

        HirGeneratedOriginPropagator.Propagate(moduleDecl, hirModule);

        return hirModule;
    }

    private void AppendModuleDeclarations(ModuleDecl moduleDecl, List<HirDecl> target, bool isRootModule)
    {
        foreach (var decl in moduleDecl.Declarations)
        {
            if (decl is ModuleDecl nestedModule)
            {
                AppendModuleDeclarations(nestedModule, target, isRootModule: false);
                continue;
            }

            if (decl is InstanceDecl instance)
            {
                AppendInstanceMethods(instance, target, moduleDecl.PackageAlias, moduleDecl.Path);
                continue;
            }

            var hirDecl = ConvertDeclaration(
                decl,
                moduleDecl.PackageAlias,
                moduleDecl.Path,
                qualifyFunctionName: (!isRootModule && moduleDecl.Path.Count > 0) ||
                                     !string.IsNullOrWhiteSpace(moduleDecl.PackageAlias));
            if (hirDecl != null)
            {
                target.Add(hirDecl);
            }
        }
    }

    private void AppendInstanceMethods(
        InstanceDecl instance,
        List<HirDecl> target,
        string? packageAlias,
        IReadOnlyList<string> modulePath)
    {
        foreach (var method in instance.Methods)
        {
            var loweredName = BuildModuleScopedFunctionName(
                BuildInstanceMethodName(instance.Name, method.Name),
                packageAlias,
                modulePath,
                qualifyFunctionName: modulePath.Count > 0 || !string.IsNullOrWhiteSpace(packageAlias));
            target.Add(ConvertFunc(method, loweredName, method.Name));
        }
    }

    private HirDecl? ConvertDeclaration(
        Declaration decl,
        string? packageAlias,
        IReadOnlyList<string> modulePath,
        bool qualifyFunctionName)
    {
        return decl switch
        {
            FuncDef func => ConvertFunc(func, BuildModuleScopedFunctionName(func.Name, packageAlias, modulePath, qualifyFunctionName)),
            FuncDecl funcDecl => ConvertFuncDecl(funcDecl, BuildModuleScopedFunctionName(funcDecl.Name, packageAlias, modulePath, qualifyFunctionName)),
            AdtDef adt => ConvertAdt(adt),
            EffectDef ability => ConvertEffect(ability),
            TraitDef trait => ConvertTrait(trait),
            LetDecl letDecl => ConvertLetDecl(letDecl),
            _ => null
        };
    }

    private static string BuildInstanceMethodName(string instanceName, string methodName)
    {
        var normalizedInstanceName = SanitizeNameSegment(instanceName);
        var normalizedMethodName = SanitizeNameSegment(methodName);
        return string.IsNullOrWhiteSpace(normalizedInstanceName)
            ? normalizedMethodName
            : $"{normalizedInstanceName}{WellKnownStrings.InternalNames.ModuleSeparator}{normalizedMethodName}";
    }

    /// <summary>
    /// 从符号表收集 ImplSymbol，为每个 trait 实现创建 HirImpl 节点。
    /// impl 方法的 HirFunc 已在 AppendModuleDeclarations 中通过 ConvertFunc 创建，
    /// 此方法根据 SymbolId 将它们关联到对应的 HirImpl。
    /// </summary>
    private void CollectImplDeclarations(HirModule hirModule)
    {
        if (_symbolTable == null)
        {
            return;
        }

        // 建立 SymbolId → HirFunc 查找表，用于快速关联 impl 方法
        var funcBySymbolId = new Dictionary<int, HirFunc>();
        foreach (var decl in hirModule.Declarations)
        {
            if (decl is HirFunc func && func.SymbolId.IsValid)
            {
                funcBySymbolId[func.SymbolId.Value] = func;
            }
        }

        foreach (var implSymbol in _symbolTable.Symbols.Values.OfType<ImplSymbol>())
        {
            if (!implSymbol.Trait.IsValid || !implSymbol.ImplementingType.IsValid)
            {
                continue;
            }

            var methods = new List<HirFunc>(implSymbol.Methods.Count);
            foreach (var methodSymbolId in implSymbol.Methods)
            {
                if (methodSymbolId.IsValid &&
                    funcBySymbolId.TryGetValue(methodSymbolId.Value, out var methodFunc))
                {
                    methods.Add(methodFunc);
                }
            }

            var hirImpl = new HirImpl
            {
                Name = implSymbol.Name,
                TraitId = implSymbol.Trait,
                ImplementingType = implSymbol.ImplementingType,
                Methods = methods,
                ImplMetadata = implSymbol,
                Span = implSymbol.Span,
                IsModuleLevel = true,
            };

            hirModule.Declarations.Add(hirImpl);
        }
    }

    private HirFunc ConvertFunc(FuncDef func, string loweredName, string? sourceName = null)
    {
        var (parameters, returnType) = BuildFuncSignature(func);

        // 从符号表获取 FFI 属性
        var funcSymbol = _symbolTable.GetSymbol<FuncSymbol>(func.SymbolId);
        var isExternal = funcSymbol?.IsExternal ?? false;
        var externalSymbolName = funcSymbol?.ExternalSymbolName;
        var externalLibrary = funcSymbol?.ExternalLibrary;
        var intrinsicName = funcSymbol?.IntrinsicName;
        var builtinIntrinsicRole = funcSymbol?.BuiltinIntrinsicRole ?? BuiltinIntrinsicRole.None;
        var typeParams = ConvertTypeParams(func.TypeParams, GetFunctionTypeParameters(func.SymbolId));

        // 无函数体的 FuncDef 视为声明（如 @ffi），不转换函数体
        HirNode? bodyNode = null;
        var previousValueGenericParameterIndices = _activeValueGenericParameterIndices;
        _activeValueGenericParameterIndices = func.TypeParams
            .Select(static (parameter, index) => (parameter, index))
            .Where(static entry =>
                entry.parameter.ParameterKind == GenericParameterKind.Value &&
                entry.parameter.SymbolId.IsValid)
            .ToDictionary(
                static entry => entry.parameter.SymbolId,
                static entry => entry.index);
        try
        {
            // Compile-time functions have already been evaluated by the Types phase and are
            // never lowered to MIR. Keeping their executable AST bodies here would leak
            // compile-time-only nodes such as quote expressions across the HIR boundary.
            if (!func.IsComptime && func.Body.Count > 0)
            {
                bodyNode = ConvertFunctionBody(func, parameters, returnType);
            }
        }
        finally
        {
            _activeValueGenericParameterIndices = previousValueGenericParameterIndices;
        }

        return new HirFunc
        {
            Name = loweredName,
            SourceName = sourceName ?? func.Name,
            Span = func.Span,
            SymbolId = func.SymbolId,
            IsModuleLevel = true,
            TypeParams = typeParams,
            Parameters = parameters,
            ReturnType = returnType,
            OwnershipContract = CreateOwnershipContract(
                func.SymbolId,
                sourceName ?? func.Name,
                parameters,
                returnType),
            Body = bodyNode,
            IsComptime = func.IsComptime,
            IsExternal = isExternal,
            ExternalSymbolName = externalSymbolName,
            ExternalLibrary = externalLibrary,
            IntrinsicName = intrinsicName,
            BuiltinIntrinsicRole = builtinIntrinsicRole,
            IsEntry = IsConfiguredEntryFunction(func.Name)
        };
    }

    private HirFunc ConvertFuncDecl(FuncDecl funcDecl, string loweredName)
    {
        var funcSymbol = _symbolTable.GetSymbol<FuncSymbol>(funcDecl.SymbolId);
        var isExternal = funcSymbol?.IsExternal ?? false;
        var externalSymbolName = funcSymbol?.ExternalSymbolName;
        var externalLibrary = funcSymbol?.ExternalLibrary;
        var intrinsicName = funcSymbol?.IntrinsicName;
        var builtinIntrinsicRole = funcSymbol?.BuiltinIntrinsicRole ?? BuiltinIntrinsicRole.None;

        var parameters = new List<HirParam>();
        var returnType = TypeId.None;

        // 从类型推断结果构建签名
        if (funcDecl.InferredType is TyFun funType)
        {
            var current = funType;
            while (true)
            {
                for (int i = 0; i < current.Params.Count; i++)
                {
                    parameters.Add(new HirParam
                    {
                        Name = $"_arg{i + 1}",
                        SymbolId = SymbolId.None,
                        TypeId = GetTypeTypeId(current.Params[i])
                    });
                }

                if (current.Result is TyFun next)
                {
                    current = next;
                    continue;
                }

                returnType = GetTypeTypeId(current.Result);
                break;
            }
        }
        else if (funcSymbol != null)
        {
            // 回退到符号表中的参数类型
            for (int i = 0; i < funcSymbol.ParamTypes.Count; i++)
            {
                parameters.Add(new HirParam
                {
                    Name = $"_arg{i + 1}",
                    SymbolId = SymbolId.None,
                    TypeId = funcSymbol.ParamTypes[i]
                });
            }

            returnType = funcSymbol.ReturnType;
        }

        return new HirFunc
        {
            Name = loweredName,
            IsComptime = funcDecl.IsComptime,
            Span = funcDecl.Span,
            SymbolId = funcDecl.SymbolId,
            IsModuleLevel = true,
            TypeParams = ConvertTypeParams(funcDecl.TypeParams, GetFunctionTypeParameters(funcDecl.SymbolId)),
            Parameters = parameters,
            ReturnType = returnType,
            OwnershipContract = CreateOwnershipContract(
                funcDecl.SymbolId,
                funcDecl.Name,
                parameters,
                returnType),
            Body = null,
            IsExternal = isExternal,
            ExternalSymbolName = externalSymbolName,
            ExternalLibrary = externalLibrary,
            IntrinsicName = intrinsicName,
            BuiltinIntrinsicRole = builtinIntrinsicRole,
            IsEntry = IsConfiguredEntryFunction(funcDecl.Name)
        };
    }

    private bool IsConfiguredEntryFunction(string functionName) =>
        !string.IsNullOrWhiteSpace(EntryFunctionName) &&
        string.Equals(functionName, EntryFunctionName, StringComparison.Ordinal);

    private OwnershipContract CreateOwnershipContract(
        SymbolId callableSymbol,
        string callableName,
        IReadOnlyList<HirParam> parameters,
        TypeId resultType) =>
        OwnershipContract.Create(
            callableSymbol,
            callableName,
            parameters.Select(static parameter => (parameter.Name, parameter.TypeId)).ToArray(),
            resultType,
            _typeRegistry.TypeDescriptors,
            _symbolTable);

    private static string BuildModuleScopedFunctionName(
        string functionName,
        string? packageAlias,
        IReadOnlyList<string> modulePath,
        bool qualifyFunctionName)
    {
        if (!qualifyFunctionName || modulePath.Count == 0)
        {
            return functionName;
        }

        var prefixSegments = new List<string>();
        if (!string.IsNullOrWhiteSpace(packageAlias))
        {
            prefixSegments.Add(packageAlias);
        }

        prefixSegments.AddRange(modulePath);
        var modulePrefix = string.Join(
            WellKnownStrings.InternalNames.ModuleSeparator,
            prefixSegments.Select(SanitizeNameSegment));
        return $"{modulePrefix}__{functionName}";
    }

    private static string SanitizeNameSegment(string segment)
    {
        var sanitized = segment
            .Replace('/', '_')
            .Replace('\\', '_')
            .Replace(':', '_')
            .Replace('.', '_');
        return string.IsNullOrWhiteSpace(sanitized) ? "_" : sanitized;
    }

    private HirNode? ConvertFunctionBody(FuncDef func, IReadOnlyList<HirParam> parameters, TypeId returnType)
    {
        if (func.Body.Count == 0)
        {
            return null;
        }

        var firstBranch = func.Body[0];
        if (func.Body.Count == 1 && IsDirectFunctionBranch(firstBranch))
        {
            return ConvertExprOrFallback(
                firstBranch.Expression,
                $"function '{func.Name}' body",
                func.Span);
        }

        var matchTypeId = returnType.IsValid
            ? returnType
            : GetTypeId(firstBranch.Expression);

        var hirMatch = new HirMatch
        {
            Scrutinee = BuildFunctionScrutinee(func, parameters),
            Span = func.Span,
            TypeId = matchTypeId,
            IsExhaustive = func.IsPatternBodyExhaustive
        };

        foreach (var branch in func.Body)
        {
            hirMatch.Branches.Add(new HirMatchBranch
            {
                Pattern = ConvertPattern(branch.Pattern),
                Guard = branch.Guard != null
                    ? ConvertExprOrFallback(branch.Guard, "function branch guard", branch.Span)
                    : null,
                Body = ConvertExprOrFallback(branch.Expression, "function branch body", branch.Span)
            });
        }

        return hirMatch;
    }

    private HirNode BuildFunctionScrutinee(FuncDef func, IReadOnlyList<HirParam> parameters)
    {
        if (parameters.Count == 0)
        {
            return new HirLiteral
            {
                LiteralKind = LiteralKind.Unit,
                Value = null,
                Span = func.Span,
                TypeId = new TypeId(BaseTypes.UnitId)
            };
        }

        if (parameters.Count == 1)
        {
            var parameter = parameters[0];
            return new HirVar
            {
                Name = parameter.Name,
                SymbolId = parameter.SymbolId,
                Span = func.Span,
                TypeId = parameter.TypeId
            };
        }

        var tupleTypeId = BuildParameterTupleTypeId(parameters);

        var tuple = new HirTuple
        {
            Span = func.Span,
            TypeId = tupleTypeId
        };

        foreach (var parameter in parameters)
        {
            tuple.Elements.Add(new HirVar
            {
                Name = parameter.Name,
                SymbolId = parameter.SymbolId,
                Span = func.Span,
                TypeId = parameter.TypeId
            });
        }

        return tuple;
    }

    private TypeId BuildParameterTupleTypeId(IReadOnlyList<HirParam> parameters)
    {
        // 注意：无效 TypeId 使用 "_" 占位符，与 TypeDescriptor.Tuple 语义不同
        // 暂时保留字符串 key 版本
        var elementKey = string.Join(
            ",",
            parameters.Select(parameter => parameter.TypeId.IsValid ? parameter.TypeId.ToString() : "_"));
        return GetOrCreateDynamicTypeId($"Tuple({elementKey})");
    }

    private static bool IsDirectFunctionBranch(PatternBranch branch)
    {
        if (branch.Guard != null || branch.Pattern == null)
        {
            return false;
        }

        return IsIrrefutablePattern(branch.Pattern);
    }

    private static bool IsIrrefutablePattern(Pattern pattern)
    {
        return pattern switch
        {
            ExpandPattern { ExpandedPattern: not null } expansion =>
                IsIrrefutablePattern(expansion.ExpandedPattern),
            VarPattern => true,
            WildcardPattern => true,
            AsPattern asPattern => !string.IsNullOrWhiteSpace(asPattern.BindingName) &&
                                   asPattern.InnerPattern != null &&
                                   IsIrrefutablePattern(asPattern.InnerPattern),
            _ => false
        };
    }

    private (List<HirParam> Parameters, TypeId ReturnType) BuildFuncSignature(FuncDef func)
    {
        var parameters = new List<HirParam>();
        var inferredParamTypes = new List<TypeId>();
        var returnType = TypeId.None;

        if (func.InferredType is TyFun funType)
        {
            var current = funType;
            while (true)
            {
                inferredParamTypes.AddRange(current.Params.Select(GetTypeTypeId));

                if (current.Result is TyFun next)
                {
                    current = next;
                    continue;
                }

                returnType = GetTypeTypeId(current.Result);
                break;
            }
        }

        if (_symbolTable.GetSymbol<FuncSymbol>(func.SymbolId) is { } funcSymbol)
        {
            for (var i = 0; i < inferredParamTypes.Count && i < funcSymbol.ParamTypes.Count; i++)
            {
                if (!inferredParamTypes[i].IsValid && funcSymbol.ParamTypes[i].IsValid)
                {
                    inferredParamTypes[i] = funcSymbol.ParamTypes[i];
                }
            }

            for (var i = inferredParamTypes.Count; i < funcSymbol.ParamTypes.Count; i++)
            {
                inferredParamTypes.Add(funcSymbol.ParamTypes[i]);
            }

            if (funcSymbol.ReturnType.IsValid)
            {
                returnType = funcSymbol.ReturnType;
            }
        }

        var paramPatterns = new List<Pattern>();
        if (func.Body.Count > 0 && func.Body[0].Pattern != null)
        {
            CollectFunctionParameterPatterns(
                func.Body[0].Pattern!,
                inferredParamTypes.Count,
                paramPatterns);
        }

        if (paramPatterns.Count > 0)
        {
            for (int i = 0; i < paramPatterns.Count; i++)
            {
                var paramType = i < inferredParamTypes.Count ? inferredParamTypes[i] : TypeId.None;
                parameters.Add(BuildHirParam(paramPatterns[i], paramType, i));
            }

            for (int i = paramPatterns.Count; i < inferredParamTypes.Count; i++)
            {
                parameters.Add(new HirParam
                {
                    Name = $"_arg{i + 1}",
                    SymbolId = SymbolId.None,
                    TypeId = inferredParamTypes[i]
                });
            }
        }
        else
        {
            for (int i = 0; i < inferredParamTypes.Count; i++)
            {
                parameters.Add(new HirParam
                {
                    Name = $"_arg{i + 1}",
                    SymbolId = SymbolId.None,
                    TypeId = inferredParamTypes[i]
                });
            }
        }

        return (parameters, returnType);
    }

    private static HirParam BuildHirParam(Pattern pattern, TypeId typeId, int index)
    {
        if (pattern is VarPattern varPattern &&
            !string.IsNullOrEmpty(varPattern.Name) &&
            varPattern.Name != "_")
        {
            return new HirParam
            {
                Name = varPattern.Name,
                SymbolId = varPattern.SymbolId,
                IsMutable = varPattern.IsMutableBinding,
                TypeId = typeId
            };
        }

        return new HirParam
        {
            Name = $"_arg{index + 1}",
            SymbolId = SymbolId.None,
            TypeId = typeId
        };
    }

    private static void CollectFunctionParameterPatterns(
        Pattern pattern,
        int expectedParameterCount,
        List<Pattern> parameters)
    {
        if (expectedParameterCount <= 1)
        {
            parameters.Add(pattern);
            return;
        }

        if (pattern is TuplePattern tuplePattern &&
            tuplePattern.Elements.Count == expectedParameterCount)
        {
            parameters.AddRange(tuplePattern.Elements);
            return;
        }

        CollectParameterPatterns(pattern, parameters);
    }

    private static void CollectParameterPatterns(Pattern pattern, List<Pattern> parameters)
    {
        if (pattern is TuplePattern tuplePattern)
        {
            foreach (var element in tuplePattern.Elements)
            {
                CollectParameterPatterns(element, parameters);
            }
            return;
        }

        parameters.Add(pattern);
    }

    private HirAdt ConvertAdt(AdtDef adt)
    {
        var hirAdt = new HirAdt
        {
            Name = adt.Name,
            Span = adt.Span,
            SymbolId = adt.SymbolId,
            TypeId = ResolveDeclaredTypeId(adt.SymbolId),
            AliasTarget = adt.AliasTarget != null ? GetTypeId(adt.AliasTarget) : TypeId.None,
            IsModuleLevel = true,
            TypeParams = ConvertTypeParams(adt.TypeParams)
        };

        var constructors = new List<HirCtor>();
        foreach (var ctor in adt.Constructors)
        {
            var hirCtor = new HirCtor
            {
                Name = ctor.Name,
                SymbolId = ctor.SymbolId,
                Span = ctor.Span
            };

            for (var i = 0; i < ctor.PositionalArgs.Count; i++)
            {
                hirCtor.Fields.Add(new HirField
                {
                    Name = $"_{i}",
                    TypeId = GetTypeId(ctor.PositionalArgs[i])
                });
            }

            foreach (var namedField in ctor.NamedArgs)
            {
                hirCtor.Fields.Add(new HirField
                {
                    Name = namedField.Name,
                    TypeId = namedField.Type != null ? GetTypeId(namedField.Type) : TypeId.None
                });
            }

            constructors.Add(hirCtor);
        }

        foreach (var constructor in constructors)
        {
            hirAdt.Constructors.Add(constructor);
        }

        // Collect constructor layouts for LLVM struct type generation.
        if (adt.SymbolId.IsValid)
        {
            CollectConstructorLayoutsFromAdtDef(adt);
        }

        return hirAdt;
    }

    private HirEffect ConvertEffect(EffectDef ability)
    {
        return new HirEffect
        {
            Name = ability.Name,
            Span = ability.Span,
            SymbolId = ability.SymbolId,
            IsModuleLevel = true
        };
    }

    private HirTrait ConvertTrait(TraitDef trait)
    {
        // Resolve SuperTraits from TraitSymbol (populated by Namer)
        var superTraits = new List<SymbolId>();
        if (trait.SymbolId.IsValid && _symbolTable.GetSymbol(trait.SymbolId) is TraitSymbol traitSymbol)
        {
            superTraits = traitSymbol.ParentTraits;
        }

        // Populate Methods — include both signature-only and default implementations
        var methods = new List<HirFunc>();
        foreach (var method in trait.Methods)
        {
            methods.Add(ConvertFunc(method, method.Name));
        }

        return new HirTrait
        {
            Name = trait.Name,
            Span = trait.Span,
            SymbolId = trait.SymbolId,
            IsModuleLevel = true,
            TypeParams = ConvertTypeParams(trait.TypeParams),
            SuperTraits = superTraits,
            Methods = methods
        };
    }

    private IReadOnlyList<DrivenType>? GetFunctionTypeParameters(SymbolId functionId)
    {
        if (_typeInferer == null ||
            !functionId.IsValid ||
            !_typeInferer.FunctionTypeParametersBySymbol.TryGetValue(functionId, out var typeParameters))
        {
            return null;
        }

        return typeParameters;
    }

    private List<HirTypeParam> ConvertTypeParams(
        IReadOnlyList<Ast.Types.TypeParam> typeParams,
        IReadOnlyList<DrivenType>? inferredTypeParameters = null)
    {
        var result = new List<HirTypeParam>(typeParams.Count);
        for (var index = 0; index < typeParams.Count; index++)
        {
            var typeParam = typeParams[index];
            var constraints = ConvertTraitConstraints(typeParam.TraitConstraints);

            var kindAnnotation = typeParam.GetKindText();
            if (typeParam.SymbolId.IsValid &&
                _symbolTable.GetSymbol(typeParam.SymbolId) is TypeParamSymbol typeParamSymbol &&
                !string.IsNullOrWhiteSpace(typeParamSymbol.KindAnnotation))
            {
                kindAnnotation = typeParamSymbol.KindAnnotation;
            }

            var isComptime = typeParam.IsComptime;
            var parameterKind = typeParam.ParameterKind;
            var comptimeTypeAnnotation = FormatComptimeTypeAnnotation(typeParam.ComptimeTypeAnnotation);
            if (typeParam.SymbolId.IsValid &&
                _symbolTable.GetSymbol(typeParam.SymbolId) is TypeParamSymbol comptimeTypeParamSymbol)
            {
                isComptime = comptimeTypeParamSymbol.IsComptime;
                parameterKind = comptimeTypeParamSymbol.ParameterKind;
                comptimeTypeAnnotation = comptimeTypeParamSymbol.ComptimeTypeAnnotation ?? comptimeTypeAnnotation;
            }

            var typeId = inferredTypeParameters != null && index < inferredTypeParameters.Count
                ? GetTypeTypeId(inferredTypeParameters[index])
                : TypeId.None;
            _typeRegistry.RegisterTypeParameterTypeId(typeParam.SymbolId, typeId);

            result.Add(new HirTypeParam
            {
                Name = typeParam.Name,
                SymbolId = typeParam.SymbolId,
                TypeId = typeId,
                ParameterKind = parameterKind,
                KindAnnotation = kindAnnotation,
                IsComptime = isComptime,
                ComptimeTypeAnnotation = comptimeTypeAnnotation,
                Constraints = constraints
            });
        }

        return result;
    }

    private static string? FormatComptimeTypeAnnotation(Ast.Types.TypeNode? typeAnnotation)
    {
        return typeAnnotation switch
        {
            Ast.Types.TypePath path => string.Join(WellKnownStrings.Separators.Path, path.ToQualifiedPathParts()),
            null => null,
            _ => typeAnnotation.GetType().Name
        };
    }

    private List<HirTraitConstraint> ConvertTraitConstraints(IReadOnlyList<Ast.Types.TraitRef> traitRefs)
    {
        if (traitRefs.Count == 0)
        {
            return [];
        }

        var result = new List<HirTraitConstraint>(traitRefs.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var traitRef in traitRefs)
        {
            var constraint = ConvertTraitConstraint(traitRef);
            if (constraint == null)
            {
                continue;
            }

            var key = CreateHirTraitConstraintKey(constraint);
            if (!seen.Add(key))
            {
                continue;
            }

            result.Add(constraint);
        }

        return result;
    }

    private HirTraitConstraint? ConvertTraitConstraint(Ast.Types.TraitRef traitRef)
    {
        if (!traitRef.SymbolId.IsValid)
        {
            return null;
        }

        return new HirTraitConstraint
        {
            SymbolId = traitRef.SymbolId,
            Name = traitRef.TraitName,
            ModulePath = [.. traitRef.ModulePath],
            TypeArgs = ConvertTraitConstraintTypeArgs(traitRef.TypeArgs)
        };
    }

    private List<HirTypeArg> ConvertTraitConstraintTypeArgs(IReadOnlyList<Ast.Types.TypeNode> typeArgs)
    {
        if (typeArgs.Count == 0)
        {
            return [];
        }

        var result = new List<HirTypeArg>(typeArgs.Count);
        foreach (var typeArg in typeArgs)
        {
            result.Add(new HirTypeArg
            {
                TypeId = ResolveTypeArgTypeId(typeArg),
                DisplayText = GetTypeArgDisplayText(typeArg)
            });
        }

        return result;
    }

    private TypeId ResolveTypeArgTypeId(Ast.Types.TypeNode typeNode)
    {
        var typeId = GetTypeId(typeNode);
        if (typeId.IsValid)
        {
            return typeId;
        }

        if (typeNode is not Ast.Types.TypePath typePath)
        {
            return TypeId.None;
        }

        if (typePath.ModulePath.Count > 0)
        {
            var fullPath = new List<string>(typePath.ModulePath.Count + 1);
            fullPath.AddRange(typePath.ModulePath);
            fullPath.Add(typePath.TypeName);
            var resolved = _symbolTable.ResolvePath(fullPath);
            if (resolved is { } resolvedId &&
                _symbolTable.GetSymbol(resolvedId) is Symbol resolvedSymbol &&
                resolvedSymbol.TypeId.IsValid)
            {
                return resolvedSymbol.TypeId;
            }
        }
        else
        {
            var builtInTypeId = BaseTypes.GetBuiltInTypeId(typePath.TypeName);
            if (builtInTypeId.IsValid)
            {
                return builtInTypeId;
            }

            var symbolId = _symbolTable.LookupType(typePath.TypeName);
            if (symbolId is { } foundId &&
                _symbolTable.GetSymbol(foundId) is Symbol foundSymbol &&
                foundSymbol.TypeId.IsValid)
            {
                return foundSymbol.TypeId;
            }
        }

        return TypeId.None;
    }

    private static string CreateHirTraitConstraintKey(HirTraitConstraint constraint)
    {
        var modulePrefix = constraint.ModulePath.Count == 0
            ? ""
            : string.Join(WellKnownStrings.Separators.Path, constraint.ModulePath) + WellKnownStrings.Separators.Path;
        var typeArgKey = string.Join(
            ",",
            constraint.TypeArgs.Select(typeArg => $"{typeArg.TypeId.Value}:{typeArg.DisplayText}"));
        return $"{constraint.SymbolId.Value}:{modulePrefix}{constraint.Name}[{typeArgKey}]";
    }

    private static string GetTypeArgDisplayText(Ast.Types.TypeNode typeNode)
    {
        if (typeNode.InferredType != null)
        {
            return typeNode.InferredType.ToString() ?? typeNode.GetType().Name;
        }

        return typeNode switch
        {
            Ast.Types.TypePath typePath => FormatTypePath(typePath),
            Ast.Types.TupleType tupleType => $"({string.Join(", ", tupleType.Elements.Select(GetTypeArgDisplayText))})",
            Ast.Types.ArrowType arrowType => $"{GetTypeArgDisplayText(arrowType.ParamType)} -> {GetTypeArgDisplayText(arrowType.ReturnType)}",
            Ast.Types.WildcardType => "_",
            Ast.Types.EffectfulType effectfulType => FormatEffectfulType(effectfulType),
            _ => typeNode.GetType().Name
        };
    }

    private static string FormatTypePath(Ast.Types.TypePath typePath)
    {
        var pathPrefix = typePath.ModulePath.Count == 0
            ? ""
            : string.Join(WellKnownStrings.Separators.Path, typePath.ModulePath) + WellKnownStrings.Separators.Path;
        if (typePath.TypeArgs.Count == 0)
        {
            return $"{pathPrefix}{typePath.TypeName}";
        }

        return $"{pathPrefix}{typePath.TypeName}[{string.Join(", ", typePath.TypeArgs.Select(GetTypeArgDisplayText))}]";
    }

    private static string FormatEffectfulType(Ast.Types.EffectfulType effectfulType)
    {
        var input = effectfulType.InputType != null
            ? GetTypeArgDisplayText(effectfulType.InputType)
            : "_";
        var effects = effectfulType.EnumerateEffectPaths()
            .Select(path => string.Join(WellKnownStrings.Separators.Path, path))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();
        var effectsDisplay = string.Join(", ", effects);
        var output = effectfulType.OutputType != null
            ? GetTypeArgDisplayText(effectfulType.OutputType)
            : "";

        return string.IsNullOrWhiteSpace(output)
            ? $"{input} need {effectsDisplay}"
            : $"{input} -> {output} need {effectsDisplay}";
    }

    private HirDecl ConvertLetDecl(LetDecl letDecl)
    {
        return letDecl.IsMutable
            ? ConvertMutableLet(letDecl)
            : ConvertLet(letDecl);
    }

    private HirVal ConvertLet(LetDecl letDecl)
    {
        var (bindingName, bindingSymbol) = GetPrimaryPatternBinding(letDecl.Pattern);
        var resolvedName = string.IsNullOrWhiteSpace(bindingName)
            ? "$let"
            : bindingName!;

        return new HirVal
        {
            Name = resolvedName,
            Initializer = ConvertLetInitializer(letDecl, bindingSymbol),
            Span = letDecl.Span,
            SymbolId = bindingSymbol,
            IsModuleLevel = _symbolTable.GetSymbol(bindingSymbol) is VarSymbol { IsModuleLevel: true },
            IsComptime = letDecl.IsComptime,
            Pattern = ConvertPattern(letDecl.Pattern),
            TypeId = GetTypeId(letDecl)
        };
    }

    private HirNode ConvertLetInitializer(LetDecl letDecl, SymbolId bindingSymbol)
    {
        if (letDecl.IsComptime &&
            bindingSymbol.IsValid &&
            letDecl.Value != null &&
            _typeInferer?.ComptimeValues.TryGetValue(bindingSymbol, out var value) == true &&
            TryReifyComptimeValue(value, GetTypeId(letDecl.Value), letDecl.Value.Span, out var reified, out _))
        {
            return reified;
        }

        return ConvertExprOrFallback(
            letDecl.Value,
            "initializer of let pattern binding",
            letDecl.Span);
    }

    private HirVarDecl ConvertMutableLet(LetDecl letDecl)
    {
        var (bindingName, bindingSymbol) = GetPrimaryPatternBinding(letDecl.Pattern);
        var resolvedName = string.IsNullOrWhiteSpace(bindingName)
            ? "$let"
            : bindingName!;

        return new HirVarDecl
        {
            Name = resolvedName,
            Initializer = ConvertExprOrFallback(
                letDecl.Value,
                "initializer of mutable let pattern binding",
                letDecl.Span),
            Span = letDecl.Span,
            SymbolId = bindingSymbol,
            IsModuleLevel = _symbolTable.GetSymbol(bindingSymbol) is VarSymbol { IsModuleLevel: true },
            Pattern = ConvertPattern(letDecl.Pattern),
            TypeId = GetTypeId(letDecl)
        };
    }

    private static (string? Name, SymbolId SymbolId) GetPrimaryPatternBinding(Ast.Patterns.Pattern? pattern)
    {
        switch (pattern)
        {
            case Ast.Patterns.VarPattern varPattern when
                !string.IsNullOrWhiteSpace(varPattern.Name) &&
                varPattern.Name != "_":
                return (varPattern.Name, varPattern.SymbolId);

            case Ast.Patterns.AsPattern asPattern when
                !string.IsNullOrWhiteSpace(asPattern.BindingName):
                return (asPattern.BindingName, asPattern.SymbolId);

            case Ast.Patterns.TuplePattern tuplePattern:
                foreach (var element in tuplePattern.Elements)
                {
                    var candidate = GetPrimaryPatternBinding(element);
                    if (!string.IsNullOrWhiteSpace(candidate.Name))
                    {
                        return candidate;
                    }
                }
                break;

            case Ast.Patterns.ListPattern listPattern:
                foreach (var element in listPattern.Elements)
                {
                    var candidate = GetPrimaryPatternBinding(element);
                    if (!string.IsNullOrWhiteSpace(candidate.Name))
                    {
                        return candidate;
                    }
                }

                if (listPattern.RestPattern != null)
                {
                    var candidate = GetPrimaryPatternBinding(listPattern.RestPattern);
                    if (!string.IsNullOrWhiteSpace(candidate.Name))
                    {
                        return candidate;
                    }
                }

                foreach (var element in listPattern.SuffixElements)
                {
                    var candidate = GetPrimaryPatternBinding(element);
                    if (!string.IsNullOrWhiteSpace(candidate.Name))
                    {
                        return candidate;
                    }
                }
                break;

            case Ast.Patterns.CtorPattern ctorPattern:
                foreach (var positional in ctorPattern.PositionalPatterns)
                {
                    var candidate = GetPrimaryPatternBinding(positional);
                    if (!string.IsNullOrWhiteSpace(candidate.Name))
                    {
                        return candidate;
                    }
                }

                foreach (var named in ctorPattern.NamedPatterns)
                {
                    if (named.Pattern == null)
                    {
                        continue;
                    }

                    var candidate = GetPrimaryPatternBinding(named.Pattern);
                    if (!string.IsNullOrWhiteSpace(candidate.Name))
                    {
                        return candidate;
                    }
                }
                break;

            case Ast.Patterns.OrPattern orPattern:
                foreach (var alternative in orPattern.Alternatives)
                {
                    var candidate = GetPrimaryPatternBinding(alternative);
                    if (!string.IsNullOrWhiteSpace(candidate.Name))
                    {
                        return candidate;
                    }
                }
                break;

            case Ast.Patterns.AndPattern andPattern:
                foreach (var conjunct in andPattern.Conjuncts)
                {
                    var candidate = GetPrimaryPatternBinding(conjunct);
                    if (!string.IsNullOrWhiteSpace(candidate.Name))
                    {
                        return candidate;
                    }
                }
                break;

            case Ast.Patterns.NotPattern notPattern when notPattern.InnerPattern != null:
                return GetPrimaryPatternBinding(notPattern.InnerPattern);

            case Ast.Patterns.ViewPattern viewPattern when viewPattern.InnerPattern != null:
                return GetPrimaryPatternBinding(viewPattern.InnerPattern);
        }

        return (null, SymbolId.None);
    }

    private HirNode ConvertExpr(EidosAstNode node)
    {
        if (node.IsRecovered)
        {
            return CreateRecoveredHirError(node);
        }

        var converted = node switch
        {
            LiteralExpr lit => ConvertLiteral(lit),
            IdentifierExpr id => ConvertIdentifier(id),
            BinaryExpr bin => ConvertBinaryExpr(bin),
            UnaryExpr unary => new HirUnaryOp
            {
                Operator = unary.Operator switch
                {
                    Ast.UnaryOp.Negate => UnaryOp.Neg,
                    Ast.UnaryOp.Not => UnaryOp.Not,
                    Ast.UnaryOp.Deref => UnaryOp.Deref,
                    Ast.UnaryOp.AddressOf => UnaryOp.AddressOf,
                    Ast.UnaryOp.Ref => UnaryOp.Ref,
                    Ast.UnaryOp.MRef => UnaryOp.MRef,
                    _ => UnaryOp.Not
                },
                Operand = ConvertExprOrFallback(unary.Operand, "unary operand", unary.Span),
                Span = unary.Span,
                TypeId = GetTypeId(unary)
            },
            CallExpr call => ConvertCall(call),
            InfixCallExpr infixCall => ConvertInfixCall(infixCall),
            MethodCallExpr methodCall => TryConvertFieldAccess(methodCall, out var fieldAccess)
                ? fieldAccess
                : ConvertMethodCall(methodCall),
            IfExpr ifExpr => new HirIf
            {
                Condition = ConvertExprOrFallback(ifExpr.Condition, "if condition", ifExpr.Span),
                ThenBranch = ConvertExprOrFallback(ifExpr.ThenBranch, "if then branch", ifExpr.Span),
                ElseBranch = ifExpr.ElseBranch != null ? ConvertExpr(ifExpr.ElseBranch) : null,
                Span = ifExpr.Span,
                TypeId = GetTypeId(ifExpr)
            },
            IfLetExpr ifLetExpr => ConvertIfLet(ifLetExpr),
            WhileLetExpr whileLetExpr => ConvertWhileLet(whileLetExpr),
            LoopExpr loop => ConvertLoop(loop),
            ReturnExpr ret => ConvertReturn(ret),
            BreakExpr breakExpr => ConvertBreak(breakExpr),
            ContinueExpr continueExpr => ConvertContinue(continueExpr),
            UnreachableExpr unreachableExpr => ConvertUnreachable(unreachableExpr),
            MatchExpr match => ConvertMatch(match),
            SelectionExpr selection => ConvertSelection(selection),
            PatternGuardExpr patternGuard => ConvertPatternGuard(patternGuard),
            SequentialGuardExpr sequentialGuard => ConvertSequentialGuard(sequentialGuard),
            LambdaExpr lambda => ConvertLambda(lambda),
            BlockExpr block => ConvertBlock(block),
            DoExpr doExpr => ConvertDoExpr(doExpr),
            TupleExpr tuple => ConvertTuple(tuple),
            ListExpr list => ConvertList(list),
            ListComprehension listComprehension => ConvertListComprehension(listComprehension),
            CtorExpr ctor => ConvertCtor(ctor),
            RecordUpdateExpr recordUpdate when recordUpdate.DesugaredCtor != null => ConvertCtor(recordUpdate.DesugaredCtor),
            RecordUpdateExpr recordUpdate when recordUpdate.DesugaredMatch != null => ConvertExpr(recordUpdate.DesugaredMatch),
            ContextualRecordLiteralExpr contextualRecord when contextualRecord.DesugaredCtor != null => ConvertCtor(contextualRecord.DesugaredCtor),
            IndexExpr index => ConvertIndex(index),
            GivenExpr given => ConvertExprOrFallback(given.Target, "given target", given.Span),
            AssociatedConstExpr associatedConst => ConvertAssociatedConstExpr(associatedConst),
            PathExpr path => ConvertPath(path),
            QuoteExpr quote => ReportEscapedQuoteExpr(quote),
            ExpandExpr { ExpandedExpression: { } expanded } => ConvertExpr(expanded),
            ExpandExpr expansion => ReportAndCreateError(
                "expression expand crossed the HIR boundary without materialization",
                expansion.Span,
                "E5122",
                fallbackKind: "expression",
                reason: "expand-crossed-namer-types-boundary",
                context: "Namer-to-Types/HIR phase boundary",
                astNodeKind: nameof(ExpandExpr)),
            _ => ReportUnsupportedExpr(node)
        };

        if (_typeInferer != null &&
            _typeInferer.TryGetClosedCaseInjection(node.Span, out var injection))
        {
            var sourceTypeId = _typeRegistry.GetOrCreateClosedCaseLayoutTypeId(injection.SourceType);
            var targetTypeId = _typeRegistry.GetOrCreateClosedCaseLayoutTypeId(injection.TargetType);
            return new HirCaseInject
            {
                Operand = converted,
                SourceCase = injection.SourceCase,
                TargetAncestor = injection.TargetAncestor,
                SourceTypeId = sourceTypeId,
                TypeId = targetTypeId,
                Span = node.Span
            };
        }

        return converted;
    }

    private HirNode ReportEscapedQuoteExpr(QuoteExpr quote) => ReportAndCreateError(
        DiagnosticMessages.QuoteExpressionCrossedHirBoundary,
        quote.Span,
        "E5121",
        fallbackKind: "expression",
        reason: "quote-crossed-types-hir-boundary",
        context: "Types-to-HIR phase boundary",
        astNodeKind: nameof(QuoteExpr));

    private HirNode ConvertAssociatedConstExpr(AssociatedConstExpr associatedConst)
    {
        if (associatedConst.ImplementationValue != null)
        {
            var lowered = ConvertExprOrFallback(
                associatedConst.ImplementationValue,
                $"associated const '{associatedConst.MemberName}' value",
                associatedConst.Span);
            return lowered with { TypeId = GetTypeId(associatedConst) };
        }

        return ReportAndCreateError(
            $"Associated const '{associatedConst.MemberName}' has no resolved implementation value.",
            associatedConst.Span,
            "E5120",
            fallbackKind: "expression",
            reason: "missing-associated-const-implementation",
            context: "associated const projection",
            astNodeKind: nameof(AssociatedConstExpr));
    }

    private HirNode ConvertPath(PathExpr path)
    {
        if (TryConvertConstGenericValueReference(
                path.SymbolId,
                path.Name,
                path.Span,
                GetTypeId(path),
                out var constGenericValue))
        {
            return constGenericValue;
        }

        if (TryConvertComptimeValueReference(path.SymbolId, path.Span, GetTypeId(path), out var comptimeValue))
        {
            return comptimeValue;
        }

        if (path.ModulePath.Count == 0 &&
            string.IsNullOrWhiteSpace(path.PackageAlias) &&
            path.Name == WellKnownStrings.Keywords.ReflConstructor)
        {
            return new HirLiteral
            {
                LiteralKind = LiteralKind.Unit,
                Value = "()",
                Span = path.Span,
                TypeId = GetTypeId(path)
            };
        }

        return new HirVar
        {
            Name = string.Join(WellKnownStrings.Separators.Path, path.Path),
            SymbolId = path.SymbolId,
            Span = path.Span,
            TypeId = GetTypeId(path),
            TypeArgumentIds = ConvertExplicitTypeArguments(path.TypeArgs),
            ValueArguments = ConvertValueGenericArguments(path)
        };
    }

    private HirError CreateRecoveredHirError(EidosAstNode node)
    {
        return new HirError
        {
            Span = node.Span,
            TypeId = new TypeId(BaseTypes.UnitId),
            Reason = node.RecoveryReason ?? AstRecoveryReasons.ParserRecoveredLiteral,
            IsRecovered = true
        };
    }

    private HirNode ConvertIdentifier(IdentifierExpr id)
    {
        if (TryConvertConstGenericValueReference(
                id.SymbolId,
                id.Name,
                id.Span,
                GetTypeId(id),
                out var constGenericValue))
        {
            return constGenericValue;
        }

        if (TryConvertComptimeValueReference(id.SymbolId, id.Span, GetTypeId(id), out var comptimeValue))
        {
            return comptimeValue;
        }

        if (id.Name == WellKnownStrings.Keywords.ReflConstructor)
        {
            return new HirLiteral
            {
                LiteralKind = LiteralKind.Unit,
                Value = "()",
                Span = id.Span,
                TypeId = GetTypeId(id)
            };
        }

        return new HirVar
        {
            Name = id.Name,
            SymbolId = id.SymbolId,
            Span = id.Span,
            TypeId = GetTypeId(id),
            ValueArguments = ConvertValueGenericArguments(id)
        };
    }

    private bool TryConvertConstGenericValueReference(
        SymbolId symbolId,
        string name,
        SourceSpan span,
        TypeId typeId,
        out HirNode node)
    {
        node = default!;
        if (!symbolId.IsValid ||
            !_activeValueGenericParameterIndices.TryGetValue(symbolId, out var parameterIndex) ||
            _symbolTable.GetSymbol<TypeParamSymbol>(symbolId) is not
                { ParameterKind: GenericParameterKind.Value })
        {
            return false;
        }

        node = new HirConstGenericValue
        {
            Name = name,
            SymbolId = symbolId,
            ParameterIndex = parameterIndex,
            Span = span,
            TypeId = typeId
        };
        return true;
    }

    private bool TryConvertComptimeValueReference(
        SymbolId symbolId,
        SourceSpan span,
        TypeId typeId,
        out HirNode node)
    {
        node = default!;
        if (_typeInferer == null ||
            !symbolId.IsValid ||
            !_typeInferer.ComptimeValues.TryGetValue(symbolId, out var value))
        {
            return false;
        }

        if (TryReifyComptimeValue(value, typeId, span, out node, out var reason))
        {
            return true;
        }

        node = ReportAndCreateError(
            $"Comptime value for symbol '{symbolId}' could not be reified into typed HIR: {reason}",
            span,
            "E5120",
            fallbackKind: "expression",
            reason: "comptime-value-reification-failed",
            context: "comptime value reference",
            astNodeKind: nameof(IdentifierExpr));
        return true;
    }

    private bool TryReifyComptimeValue(
        ComptimeValue value,
        TypeId fallbackTypeId,
        SourceSpan span,
        out HirNode node,
        out string reason)
    {
        var typeId = ResolveComptimeValueTypeId(value, fallbackTypeId);
        if (!typeId.IsValid)
        {
            node = default!;
            reason = $"value '{value.CanonicalHash}' has no concrete language type";
            return false;
        }

        switch (value)
        {
            case ComptimeUnitValue:
                node = CreateComptimeLiteral(LiteralKind.Unit, "()", typeId, span);
                reason = "";
                return true;
            case ComptimeBoolValue scalar:
                node = CreateComptimeLiteral(LiteralKind.Bool, scalar.Value, typeId, span);
                reason = "";
                return true;
            case ComptimeIntegerValue scalar:
                node = CreateComptimeLiteral(LiteralKind.Int, scalar.Value, typeId, span);
                reason = "";
                return true;
            case ComptimeFloatValue scalar:
                node = CreateComptimeLiteral(LiteralKind.Float, scalar.Value, typeId, span);
                reason = "";
                return true;
            case ComptimeStringValue scalar:
                node = CreateComptimeLiteral(LiteralKind.String, scalar.Value, typeId, span);
                reason = "";
                return true;
            case ComptimeCharValue scalar:
                node = CreateComptimeLiteral(LiteralKind.Char, scalar.Value, typeId, span);
                reason = "";
                return true;
            case ComptimeSequenceValue { Kind: ComptimeSequenceKind.Tuple } tuple:
                return TryReifyComptimeTuple(tuple, typeId, span, out node, out reason);
            case ComptimeSequenceValue { Kind: ComptimeSequenceKind.List } list:
                return TryReifyComptimeList(list, typeId, span, out node, out reason);
            case ComptimeAdtValue adt:
                return TryReifyComptimeAdt(adt, typeId, span, out node, out reason);
            default:
                node = default!;
                reason = $"value kind '{value.GetType().Name}' is not supported";
                return false;
        }
    }

    private bool TryReifyComptimeTuple(
        ComptimeSequenceValue tuple,
        TypeId typeId,
        SourceSpan span,
        out HirNode node,
        out string reason)
    {
        var hirTuple = new HirTuple { Span = span, TypeId = typeId };
        for (var i = 0; i < tuple.Elements.Count; i++)
        {
            if (!TryReifyComptimeValue(tuple.Elements[i], TypeId.None, span, out var element, out reason))
            {
                node = default!;
                reason = $"tuple element {i} could not be reified: {reason}";
                return false;
            }

            hirTuple.Elements.Add(element);
        }

        node = hirTuple;
        reason = "";
        return true;
    }

    private bool TryReifyComptimeList(
        ComptimeSequenceValue list,
        TypeId typeId,
        SourceSpan span,
        out HirNode node,
        out string reason)
    {
        var hirList = new HirList { Span = span, TypeId = typeId };
        for (var i = 0; i < list.Elements.Count; i++)
        {
            if (!TryReifyComptimeValue(list.Elements[i], TypeId.None, span, out var element, out reason))
            {
                node = default!;
                reason = $"list element {i} could not be reified: {reason}";
                return false;
            }

            hirList.Elements.Add(element);
        }

        node = hirList;
        reason = "";
        return true;
    }

    private bool TryReifyComptimeAdt(
        ComptimeAdtValue adt,
        TypeId typeId,
        SourceSpan span,
        out HirNode node,
        out string reason)
    {
        if (!adt.ConstructorId.IsValid ||
            _symbolTable.GetSymbol<CtorSymbol>(adt.ConstructorId) is not { } constructor)
        {
            node = default!;
            reason = $"constructor '{adt.ConstructorName}' is not present in the current symbol table";
            return false;
        }

        var hirCall = new HirCall
        {
            Function = new HirVar
            {
                Name = adt.ConstructorName,
                SymbolId = adt.ConstructorId,
                Span = span
            },
            Convention = CallConvention.Constructor,
            Span = span,
            TypeId = typeId
        };

        for (var i = 0; i < adt.PositionalValues.Count; i++)
        {
            if (!TryReifyComptimeValue(adt.PositionalValues[i], TypeId.None, span, out var argument, out reason))
            {
                node = default!;
                reason = $"constructor '{adt.ConstructorName}' positional field {i} could not be reified: {reason}";
                return false;
            }

            hirCall.Arguments.Add(argument);
        }

        if (!TryAppendReifiedNamedComptimeFields(adt, constructor, span, hirCall, out reason))
        {
            node = default!;
            return false;
        }

        node = hirCall;
        reason = "";
        return true;
    }

    private bool TryAppendReifiedNamedComptimeFields(
        ComptimeAdtValue adt,
        CtorSymbol constructor,
        SourceSpan span,
        HirCall hirCall,
        out string reason)
    {
        if (adt.NamedValues.Count == 0)
        {
            reason = "";
            return true;
        }

        var valuesByName = adt.NamedValues.ToDictionary(
            static field => field.Name,
            static field => field.Value,
            StringComparer.Ordinal);
        if (valuesByName.Count != adt.NamedValues.Count)
        {
            reason = $"constructor '{adt.ConstructorName}' contains duplicate named fields";
            return false;
        }

        var declaredFieldNames = GetConstructorNamedFieldOrder(constructor);
        if (declaredFieldNames.Count != valuesByName.Count)
        {
            reason = $"constructor '{adt.ConstructorName}' named field count does not match its declaration";
            return false;
        }

        foreach (var fieldName in declaredFieldNames)
        {
            if (!valuesByName.TryGetValue(fieldName, out var value))
            {
                reason = $"constructor '{adt.ConstructorName}' is missing declared field '{fieldName}'";
                return false;
            }

            if (!TryReifyComptimeValue(value, TypeId.None, span, out var argument, out reason))
            {
                reason = $"constructor '{adt.ConstructorName}' field '{fieldName}' could not be reified: {reason}";
                return false;
            }

            hirCall.Arguments.Add(argument);
        }

        reason = "";
        return true;
    }

    private IReadOnlyList<string> GetConstructorNamedFieldOrder(CtorSymbol constructor)
    {
        if (constructor.NamedFields.Count > 0)
        {
            var fieldNames = new List<string>(constructor.NamedFields.Count);
            foreach (var fieldSymbolId in constructor.NamedFields)
            {
                if (_symbolTable.GetSymbol<FieldSymbol>(fieldSymbolId) is { Name: { Length: > 0 } fieldName })
                {
                    fieldNames.Add(fieldName);
                }
            }

            if (fieldNames.Count == constructor.NamedFields.Count)
            {
                return fieldNames;
            }
        }

        return _typeInferer != null &&
               _typeInferer.TryGetConstructorNamedFieldOrder(constructor.Id, out var inferredFieldNames)
            ? inferredFieldNames
            : [];
    }

    private TypeId ResolveComptimeValueTypeId(ComptimeValue value, TypeId fallbackTypeId)
    {
        if (value.StaticType != null)
        {
            var staticTypeId = GetTypeTypeId(value.StaticType);
            if (staticTypeId.IsValid)
            {
                return staticTypeId;
            }
        }

        return fallbackTypeId;
    }

    private static HirLiteral CreateComptimeLiteral(
        LiteralKind literalKind,
        object value,
        TypeId typeId,
        SourceSpan span) =>
        new()
        {
            LiteralKind = literalKind,
            Value = value,
            Span = span,
            TypeId = typeId
        };

    private HirLiteral ConvertLiteral(LiteralExpr lit)
    {
        var hirKind = lit.Kind switch
        {
            Eidosc.Ast.Expressions.LiteralKind.Integer => LiteralKind.Int,
            Eidosc.Ast.Expressions.LiteralKind.Float => LiteralKind.Float,
            Eidosc.Ast.Expressions.LiteralKind.String => LiteralKind.String,
            Eidosc.Ast.Expressions.LiteralKind.Char => LiteralKind.Char,
            Eidosc.Ast.Expressions.LiteralKind.Boolean => LiteralKind.Bool,
            Eidosc.Ast.Expressions.LiteralKind.Unit => LiteralKind.Unit,
            _ => LiteralKind.Unit
        };

        return new HirLiteral
        {
            LiteralKind = hirKind,
            Value = lit.Value,
            Span = lit.Span,
            TypeId = GetTypeId(lit)
        };
    }

    private HirNode ConvertCall(CallExpr call, HirCallSurfaceSyntax surfaceSyntax = HirCallSurfaceSyntax.Direct)
    {
        var loweredTarget = StripTypeApplication(call.Function);
        var arguments = new List<HirNode>(call.PositionalArgs.Count);
        foreach (var arg in call.PositionalArgs)
        {
            arguments.Add(ConvertExpr(arg));
        }

        for (var i = 0; i < call.SynthesizedUnitArgumentCount; i++)
        {
            arguments.Add(CreateSyntheticUnitLiteral(call.Span));
        }

        var function = ConvertExprOrFallback(call.Function, "call function target", call.Span);
        HirNode computation = BuildCallableApplication(
            function,
            arguments,
            loweredTarget,
            call.Span,
            GetTypeId(call),
            surfaceSyntax);

        return computation;
    }

    private static HirLiteral CreateSyntheticUnitLiteral(SourceSpan span) =>
        new()
        {
            LiteralKind = LiteralKind.Unit,
            Value = "()",
            Span = span,
            TypeId = new TypeId(BaseTypes.UnitId)
        };

    private HirNode BuildCallableApplication(
        HirNode function,
        List<HirNode> arguments,
        EidosAstNode? loweredTarget,
        SourceSpan span,
        TypeId resultType,
        HirCallSurfaceSyntax surfaceSyntax)
    {
        if (TryFlattenCurriedCallableApplication(function, arguments, out var flattenedFunction, out var flattenedArguments, out var flattenedConvention))
        {
            var inheritedOwnerSymbolId = function is HirCall flattenedSourceCall
                ? flattenedSourceCall.OwnerSymbolId
                : SymbolId.None;
            var inheritedOwnerPath = function is HirCall flattenedOwnerCall
                ? flattenedOwnerCall.OwnerPath
                : "";
            var inheritedHasExplicitOwner = function is HirCall flattenedExplicitOwnerCall &&
                                            flattenedExplicitOwnerCall.HasExplicitOwner;
            var (flattenedReceiverIndex, flattenedInjectedCount) = GetSurfaceArgumentMetadata(surfaceSyntax);
            return new HirCall
            {
                Function = flattenedFunction,
                Arguments = flattenedArguments,
                Convention = flattenedConvention,
                SurfaceSyntax = surfaceSyntax,
                OwnerSymbolId = inheritedOwnerSymbolId,
                OwnerPath = inheritedOwnerPath,
                HasExplicitOwner = inheritedHasExplicitOwner,
                ReceiverArgumentIndex = flattenedReceiverIndex,
                InjectedArgumentCount = flattenedInjectedCount,
                Span = span,
                TypeId = resultType
            };
        }

        var ownerSymbolId = ResolveCallableOwnerSymbolId(loweredTarget, function, out var ownerPath, out var hasExplicitOwner);
        var (receiverIndex, injectedCount) = GetSurfaceArgumentMetadata(surfaceSyntax);
        var convention = ResolveCallConvention(loweredTarget);

        return new HirCall
        {
            Function = function,
            Arguments = arguments,
            Convention = convention,
            SurfaceSyntax = surfaceSyntax,
            OwnerSymbolId = ownerSymbolId,
            OwnerPath = ownerPath,
            HasExplicitOwner = hasExplicitOwner,
            ReceiverArgumentIndex = receiverIndex,
            InjectedArgumentCount = injectedCount,
            Span = span,
            TypeId = resultType
        };
    }

    private static bool TryFlattenCurriedCallableApplication(
        HirNode function,
        List<HirNode> arguments,
        out HirNode flattenedFunction,
        out List<HirNode> flattenedArguments,
        out CallConvention convention)
    {
        flattenedFunction = function;
        flattenedArguments = arguments;
        convention = CallConvention.Normal;

        if (function is not HirCall
            {
                Convention: CallConvention.Normal or CallConvention.Constructor
            } innerCall)
        {
            return false;
        }

        flattenedFunction = innerCall.Function;
        flattenedArguments = [.. innerCall.Arguments, .. arguments];
        convention = innerCall.Convention;
        return true;
    }

    private static (int? ReceiverArgumentIndex, int InjectedArgumentCount) GetSurfaceArgumentMetadata(HirCallSurfaceSyntax surfaceSyntax)
    {
        return surfaceSyntax switch
        {
            HirCallSurfaceSyntax.Method => (0, 1),
            HirCallSurfaceSyntax.Pipe => (null, 1),
            _ => (null, 0)
        };
    }

    private SymbolId ResolveCallableOwnerSymbolId(
        EidosAstNode? loweredTarget,
        HirNode function,
        out string ownerPath,
        out bool hasExplicitOwner)
    {
        if (TryGetExplicitCallableOwner(loweredTarget, out var explicitOwnerId, out ownerPath))
        {
            hasExplicitOwner = true;
            return explicitOwnerId;
        }

        hasExplicitOwner = false;

        if (function is HirCall innerCall && innerCall.OwnerSymbolId.IsValid)
        {
            ownerPath = innerCall.OwnerPath;
            hasExplicitOwner = innerCall.HasExplicitOwner;
            return innerCall.OwnerSymbolId;
        }

        if (function is HirVar { SymbolId.IsValid: true } variable &&
            TryGetResolvedCallableOwner(variable.SymbolId, out var resolvedOwnerId, out ownerPath))
        {
            return resolvedOwnerId;
        }

        ownerPath = "";
        return SymbolId.None;
    }

    private bool TryGetExplicitCallableOwner(EidosAstNode? loweredTarget, out SymbolId ownerSymbolId, out string ownerPath)
    {
        ownerSymbolId = SymbolId.None;
        ownerPath = "";

        if (loweredTarget is not PathExpr path)
        {
            return false;
        }

        var ownerSegments = new List<string>();
        if (!string.IsNullOrWhiteSpace(path.PackageAlias))
        {
            ownerSegments.Add(path.PackageAlias);
        }

        ownerSegments.AddRange(path.ModulePath);
        if (ownerSegments.Count == 0)
        {
            return false;
        }

        ownerPath = string.Join(WellKnownStrings.Separators.Path, ownerSegments);
        var resolved = _symbolTable.ResolvePathWithResult(ownerSegments);
        ownerSymbolId = resolved.IsSuccess ? resolved.SymbolId : SymbolId.None;
        return true;
    }

    private bool TryGetResolvedCallableOwner(SymbolId functionSymbolId, out SymbolId ownerSymbolId, out string ownerPath)
    {
        ownerSymbolId = SymbolId.None;
        ownerPath = "";

        if (!functionSymbolId.IsValid ||
            _symbolTable.GetSymbol(functionSymbolId) is not FuncSymbol functionSymbol)
        {
            return false;
        }

        if (functionSymbol.OwnerTrait is { IsValid: true } ownerTraitId)
        {
            ownerSymbolId = ownerTraitId;
            ownerPath = FormatOwnerPath(ownerTraitId);
            return true;
        }

        if (TryGetOwningModuleId(functionSymbolId, out var moduleId))
        {
            ownerSymbolId = moduleId;
            ownerPath = _symbolTable.Modules.FormatModuleFullName(moduleId);
            return true;
        }

        return false;
    }

    private bool TryGetOwningModuleId(SymbolId memberId, out SymbolId moduleId)
    {
        return _symbolTable.Modules.TryGetOwningModuleId(memberId, out moduleId);
    }

    private string FormatOwnerPath(SymbolId ownerSymbolId)
    {
        if (!ownerSymbolId.IsValid ||
            _symbolTable.GetSymbol(ownerSymbolId) is not { } symbol)
        {
            return "";
        }

        if (symbol is ModuleSymbol)
        {
            return _symbolTable.Modules.FormatModuleFullName(ownerSymbolId);
        }

        if (TryGetOwningModuleId(ownerSymbolId, out var moduleId))
        {
            var moduleName = _symbolTable.Modules.FormatModuleFullName(moduleId);
            return string.IsNullOrWhiteSpace(moduleName)
                ? symbol.Name
                : moduleName + WellKnownStrings.Separators.Path + symbol.Name;
        }

        return symbol.Name;
    }

    private bool TryConvertFieldAccess(MethodCallExpr methodCall, out HirFieldAccess fieldAccess)
    {
        fieldAccess = null!;
        if (!methodCall.ResolvedAsFieldAccess || methodCall.Receiver == null)
        {
            return false;
        }

        fieldAccess = new HirFieldAccess
        {
            Target = ConvertExprOrFallback(methodCall.Receiver, "field access receiver", methodCall.Span),
            FieldName = methodCall.MethodName,
            FieldSymbolId = methodCall.FieldSymbolId,
            Span = methodCall.Span,
            TypeId = GetTypeId(methodCall)
        };
        return true;
    }

    private HirNode ConvertMethodCall(MethodCallExpr methodCall)
    {
        if (methodCall.ResolvedStaticExpression != null)
        {
            return ConvertExprOrFallback(
                methodCall.ResolvedStaticExpression,
                "static member projection",
                methodCall.Span);
        }

        var desugared = methodCall.ToDesugaredCall();
        return ConvertCall(desugared, HirCallSurfaceSyntax.Method);
    }

    private static EidosAstNode? StripTypeApplication(EidosAstNode? target)
    {
        while (target is IndexExpr { IsTypeApplication: true, Object: not null } typeApplication)
        {
            target = typeApplication.Object;
        }

        return target;
    }

    private List<TypeId> ConvertExplicitTypeArguments(IReadOnlyList<Ast.Types.TypeNode> typeArgs)
    {
        if (typeArgs.Count == 0)
        {
            return [];
        }

        var result = new List<TypeId>(typeArgs.Count);
        foreach (var typeArg in typeArgs)
        {
            result.Add(ResolveTypeArgTypeId(typeArg));
        }

        return result;
    }

    private HirNode AttachExplicitTypeArguments(
        HirNode target,
        IReadOnlyList<Ast.Types.TypeNode> typeArgs,
        EidosAstNode? application = null)
    {
        var valueArguments = application == null
            ? []
            : ConvertValueGenericArguments(application);
        if (typeArgs.Count == 0 && valueArguments.Count == 0)
        {
            return target;
        }

        var typeArgumentIds = ConvertExplicitTypeArguments(typeArgs);
        return target is HirVar variable
            ? variable with
            {
                TypeArgumentIds = typeArgumentIds,
                ValueArguments = valueArguments
            }
            : target;
    }

    private List<GenericValueArgumentDescriptor> ConvertValueGenericArguments(EidosAstNode application)
    {
        if (_typeInferer == null)
        {
            return [];
        }

        return _typeInferer.GetValueGenericArguments(application)
            .Select(static argument => new GenericValueArgumentDescriptor(
                argument.ParameterIndex,
                argument.CanonicalText,
                argument.CanonicalHash,
                argument.DisplayText,
                argument.TypeId,
                argument.ReferencedParameterIndex,
                argument.ValueVariableIndex))
            .ToList();
    }

    private HirMatch ConvertMatch(MatchExpr match)
    {
        var hirMatch = new HirMatch
        {
            Scrutinee = ConvertExprOrFallback(match.MatchedExpression, "match scrutinee", match.Span),
            Span = match.Span,
            TypeId = GetTypeId(match),
            IsExhaustive = match.IsPatternExhaustive
        };

        foreach (var branch in match.Branches)
        {
            hirMatch.Branches.Add(new HirMatchBranch
            {
                Pattern = ConvertPattern(branch.Pattern),
                Guard = branch.Guard != null
                    ? ConvertExprOrFallback(branch.Guard, "match branch guard", branch.Span)
                    : null,
                Body = ConvertExprOrFallback(branch.Expression, "match branch body", branch.Span)
            });
        }

        return hirMatch;
    }

    private HirMatch ConvertSelection(SelectionExpr selection)
    {
        var subjectNodes = selection.Subject is TupleExpr tuple
            ? tuple.Elements.Cast<EidosAstNode>().ToArray()
            : selection.Subject != null
                ? [selection.Subject]
                : [];
        var hirMatch = new HirMatch
        {
            Scrutinee = ConvertExprOrFallback(selection.Subject, "selection subject", selection.Span),
            Span = selection.Span,
            TypeId = GetTypeId(selection),
            IsExhaustive = true
        };

        var positivePattern = BuildSelectionPattern(selection, subjectNodes, positive: true);
        var negativePattern = selection.IsGroup
            ? CreateSelectionWildcard(selection.Span)
            : BuildSelectionPattern(selection, subjectNodes, positive: false);
        var unit = new HirLiteral
        {
            LiteralKind = LiteralKind.Unit,
            Value = null,
            Span = selection.Span,
            TypeId = new TypeId(BaseTypes.UnitId)
        };

        if (selection.ThenArm != null)
        {
            hirMatch.Branches.Add(new HirMatchBranch
            {
                Pattern = positivePattern,
                Body = ConvertExpr(selection.ThenArm)
            });
        }

        if (selection.ElseArm != null)
        {
            hirMatch.Branches.Add(new HirMatchBranch
            {
                Pattern = negativePattern,
                Body = ConvertExpr(selection.ElseArm)
            });
        }

        if (selection.ThenArm == null || selection.ElseArm == null)
        {
            hirMatch.Branches.Add(new HirMatchBranch
            {
                Pattern = CreateSelectionWildcard(selection.Span),
                Body = unit
            });
        }

        return hirMatch;
    }

    private HirPattern BuildSelectionPattern(
        SelectionExpr selection,
        IReadOnlyList<EidosAstNode> subjectNodes,
        bool positive)
    {
        var payloadIndex = 0;
        var elements = new List<HirPattern>(selection.Subjects.Count);
        for (var i = 0; i < selection.Subjects.Count; i++)
        {
            var subject = selection.Subjects[i];
            var node = i < subjectNodes.Count ? subjectNodes[i] : selection;
            elements.Add(BuildSelectionSubjectPattern(selection, subject, node, positive, ref payloadIndex));
        }

        if (!selection.IsGroup)
        {
            return elements.Count == 1 ? elements[0] : CreateSelectionWildcard(selection.Span);
        }

        return new HirTuplePattern
        {
            Elements = elements,
            Span = selection.Span,
            TypeId = GetTypeId(selection.Subject)
        };
    }

    private HirPattern BuildSelectionSubjectPattern(
        SelectionExpr selection,
        SelectionSubjectDesugaring subject,
        EidosAstNode subjectNode,
        bool positive,
        ref int payloadIndex)
    {
        if (subject.Kind == SelectionSubjectKind.Bool)
        {
            return new HirLiteralPattern
            {
                Value = positive,
                Span = subjectNode.Span,
                TypeId = new TypeId(BaseTypes.BoolId)
            };
        }

        var payloadTypes = positive ? subject.PositivePayloadTypes : subject.NegativePayloadTypes;
        var symbols = positive ? selection.ThenPlaceholderSymbols : selection.ElsePlaceholderSymbols;
        var fields = new List<HirFieldPattern>(payloadTypes.Count);
        for (var i = 0; i < payloadTypes.Count; i++)
        {
            var index = payloadIndex++;
            var payloadTypeId = payloadTypes[i] is DrivenType payloadType
                ? GetTypeTypeId(payloadType)
                : TypeId.None;
            var pattern = symbols.TryGetValue(index, out var symbolId)
                ? new HirVarPattern
                {
                    Name = $"_{index}",
                    SymbolId = symbolId,
                    Span = subjectNode.Span,
                    TypeId = payloadTypeId
                }
                : CreateSelectionWildcard(subjectNode.Span, payloadTypeId);
            fields.Add(new HirFieldPattern
            {
                FieldName = $"_{i}",
                Pattern = pattern
            });
        }

        var constructorName = subject.Kind switch
        {
            SelectionSubjectKind.Option => positive ? "Some" : "None",
            SelectionSubjectKind.Result => positive ? "Ok" : "Err",
            SelectionSubjectKind.Either => positive ? "Right" : "Left",
            _ => string.Empty
        };
        return new HirCtorPattern
        {
            ConstructorName = constructorName,
            ConstructorSymbolId = positive
                ? subject.PositiveConstructorSymbolId
                : subject.NegativeConstructorSymbolId,
            Fields = fields,
            Span = subjectNode.Span,
            TypeId = GetTypeId(subjectNode)
        };
    }

    private static HirVarPattern CreateSelectionWildcard(SourceSpan span, TypeId typeId = default) => new()
    {
        IsWildcard = true,
        Span = span,
        TypeId = typeId
    };

    private HirPatternGuard ConvertPatternGuard(PatternGuardExpr patternGuard)
    {
        return new HirPatternGuard
        {
            Pattern = ConvertPattern(patternGuard.Pattern),
            SourceExpression = ConvertExprOrFallback(patternGuard.SourceExpression, "pattern guard source", patternGuard.Span),
            Span = patternGuard.Span,
            TypeId = new TypeId(BaseTypes.BoolId)
        };
    }

    private HirSequentialGuard ConvertSequentialGuard(SequentialGuardExpr sequentialGuard)
    {
        var hirGuard = new HirSequentialGuard
        {
            Span = sequentialGuard.Span,
            TypeId = new TypeId(BaseTypes.BoolId)
        };

        foreach (var guard in sequentialGuard.Guards)
        {
            hirGuard.Guards.Add(ConvertExprOrFallback(guard, "sequential guard item", guard.Span));
        }

        return hirGuard;
    }

    private HirMatch ConvertIfLet(IfLetExpr ifLetExpr)
    {
        var hirMatch = new HirMatch
        {
            Scrutinee = ConvertExprOrFallback(ifLetExpr.MatchedExpression, "if let scrutinee", ifLetExpr.Span),
            Span = ifLetExpr.Span,
            TypeId = GetTypeId(ifLetExpr)
        };

        hirMatch.Branches.Add(new HirMatchBranch
        {
            Pattern = ConvertPattern(ifLetExpr.Pattern),
            Guard = null,
            Body = ConvertExprOrFallback(ifLetExpr.ThenBranch, "if let then branch", ifLetExpr.Span)
        });

        var elseBody = ifLetExpr.ElseBranch != null
            ? ConvertExpr(ifLetExpr.ElseBranch)
            : new HirLiteral
            {
                LiteralKind = LiteralKind.Unit,
                Value = null,
                Span = ifLetExpr.Span,
                TypeId = new TypeId(BaseTypes.UnitId)
            };

        hirMatch.Branches.Add(new HirMatchBranch
        {
            Pattern = new HirVarPattern
            {
                IsWildcard = true,
                Span = ifLetExpr.Span,
                TypeId = TypeId.None
            },
            Guard = null,
            Body = elseBody
        });

        return hirMatch;
    }

    private HirLoop ConvertWhileLet(WhileLetExpr whileLetExpr)
    {
        var unitType = new TypeId(BaseTypes.UnitId);
        var hirMatch = new HirMatch
        {
            Scrutinee = ConvertExprOrFallback(whileLetExpr.MatchedExpression, "while let scrutinee", whileLetExpr.Span),
            Span = whileLetExpr.Span,
            TypeId = unitType
        };

        var successPattern = whileLetExpr.Pattern != null
            ? ConvertPattern(whileLetExpr.Pattern)
            : new HirVarPattern
            {
                IsWildcard = true,
                Span = whileLetExpr.Span,
                TypeId = TypeId.None
            };

        hirMatch.Branches.Add(new HirMatchBranch
        {
            Pattern = successPattern,
            Guard = null,
            Body = BuildWhileLetSuccessBody(whileLetExpr, unitType)
        });

        hirMatch.Branches.Add(new HirMatchBranch
        {
            Pattern = new HirVarPattern
            {
                IsWildcard = true,
                Span = whileLetExpr.Span,
                TypeId = TypeId.None
            },
            Guard = null,
            Body = new HirBreak
            {
                Value = null,
                Span = whileLetExpr.Span,
                TypeId = unitType
            }
        });

        return new HirLoop
        {
            Body = hirMatch,
            Span = whileLetExpr.Span,
            TypeId = GetTypeId(whileLetExpr)
        };
    }

    private HirNode BuildWhileLetSuccessBody(WhileLetExpr whileLetExpr, TypeId unitType)
    {
        var bodyNode = ConvertExprOrFallback(whileLetExpr.Body, "while let body", whileLetExpr.Span);
        var unitLiteral = new HirLiteral
        {
            LiteralKind = LiteralKind.Unit,
            Value = null,
            Span = whileLetExpr.Span,
            TypeId = unitType
        };

        return new HirBlock
        {
            Span = whileLetExpr.Span,
            TypeId = unitType,
            Statements =
            [
                new HirExprStatement
                {
                    Expression = bodyNode,
                    Span = whileLetExpr.Span
                },
                new HirExprStatement
                {
                    Expression = unitLiteral,
                    Span = whileLetExpr.Span
                }
            ]
        };
    }

    private HirLoop ConvertLoop(LoopExpr loop)
    {
        return new HirLoop
        {
            Body = ConvertExprOrFallback(loop.Body, "loop body", loop.Span),
            Span = loop.Span,
            TypeId = GetTypeId(loop)
        };
    }

    private HirReturn ConvertReturn(ReturnExpr ret)
    {
        return new HirReturn
        {
            Value = ret.Value != null
                ? ConvertExprOrFallback(ret.Value, "return value", ret.Span)
                : null,
            Span = ret.Span,
            TypeId = GetTypeId(ret)
        };
    }

    private HirBreak ConvertBreak(BreakExpr breakExpr)
    {
        return new HirBreak
        {
            Value = breakExpr.Value != null
                ? ConvertExprOrFallback(breakExpr.Value, "break value", breakExpr.Span)
                : null,
            Span = breakExpr.Span,
            TypeId = GetTypeId(breakExpr)
        };
    }

    private HirContinue ConvertContinue(ContinueExpr continueExpr)
    {
        return new HirContinue
        {
            Span = continueExpr.Span,
            TypeId = GetTypeId(continueExpr)
        };
    }

    private HirUnreachable ConvertUnreachable(UnreachableExpr unreachableExpr)
    {
        return new HirUnreachable
        {
            Span = unreachableExpr.Span,
            TypeId = GetTypeId(unreachableExpr)
        };
    }

    private HirLambda ConvertLambda(LambdaExpr lambda)
    {
        var parameters = new List<HirParam>(lambda.Parameters.Count);
        for (int i = 0; i < lambda.Parameters.Count; i++)
        {
            var param = lambda.Parameters[i];
            parameters.Add(BuildHirParam(param, GetTypeId(param), i));
        }

        var captures = CollectLambdaCaptures(lambda);

        var hirLambda = new HirLambda
        {
            Body = BuildLambdaBody(lambda, parameters),
            Span = lambda.Span,
            TypeId = GetTypeId(lambda),
            Parameters = parameters,
            Captures = captures
        };

        return hirLambda;
    }

    private List<HirCapture> CollectLambdaCaptures(LambdaExpr lambda)
    {
        if (lambda.Body == null)
        {
            return [];
        }

        var freeVariables = _freeVariableAnalyzer.Analyze(lambda);
        return _captureCollector.Collect(lambda, freeVariables);
    }

    private HirNode BuildLambdaBody(LambdaExpr lambda, IReadOnlyList<HirParam> parameters)
    {
        var loweredBody = ConvertExprOrFallback(lambda.Body, "lambda body", lambda.Span);
        var bindingStatements = new List<HirStatement>();

        for (int i = 0; i < lambda.Parameters.Count; i++)
        {
            var pattern = lambda.Parameters[i];
            if (CanUseDirectLambdaParameterBinding(pattern))
            {
                continue;
            }

            bindingStatements.Add(new HirDeclStatement
            {
                Span = pattern.Span,
                Declaration = BuildLambdaPatternBinding(pattern, parameters[i])
            });
        }

        if (bindingStatements.Count == 0)
        {
            return loweredBody;
        }

        if (loweredBody is HirBlock existingBlock)
        {
            var mergedStatements = new List<HirStatement>(bindingStatements.Count + existingBlock.Statements.Count);
            mergedStatements.AddRange(bindingStatements);
            mergedStatements.AddRange(existingBlock.Statements);

            return existingBlock with
            {
                Statements = mergedStatements
            };
        }

        return new HirBlock
        {
            Statements = bindingStatements,
            Result = loweredBody,
            Span = lambda.Span,
            TypeId = loweredBody.TypeId
        };
    }

    private static bool CanUseDirectLambdaParameterBinding(Pattern pattern)
    {
        return pattern switch
        {
            VarPattern varPattern when
                !string.IsNullOrWhiteSpace(varPattern.Name) &&
                varPattern.Name != "_" => true,
            WildcardPattern => true,
            _ => false
        };
    }

    private HirVal BuildLambdaPatternBinding(Pattern pattern, HirParam parameter)
    {
        var (bindingName, bindingSymbol) = GetPrimaryPatternBinding(pattern);
        var resolvedName = string.IsNullOrWhiteSpace(bindingName)
            ? parameter.Name
            : bindingName!;

        return new HirVal
        {
            Name = resolvedName,
            SymbolId = bindingSymbol,
            Span = pattern.Span,
            IsModuleLevel = false,
            Pattern = ConvertPattern(pattern),
            TypeId = parameter.TypeId,
            Initializer = new HirVar
            {
                Name = parameter.Name,
                SymbolId = parameter.SymbolId,
                Span = pattern.Span,
                TypeId = parameter.TypeId
            }
        };
    }

    private HirBlock ConvertBlock(BlockExpr block)
    {
        return ConvertBlockFrom(block, 0);
    }

    private HirBlock ConvertBlockFrom(BlockExpr block, int startIndex)
    {
        var statements = new List<HirStatement>();
        HirNode? result = null;

        for (var i = startIndex; i < block.Statements.Count; i++)
        {
            var stmt = block.Statements[i];
            if (stmt is LetQuestionDecl letQuestionDecl)
            {
                result = ConvertLetQuestion(letQuestionDecl, block, i);
                break;
            }

            if (block.ResultExpression != null && ReferenceEquals(stmt, block.ResultExpression))
            {
                result = ConvertExprOrFallback(block.ResultExpression, "block result", block.ResultExpression.Span);
                break;
            }

            var hirStmt = ConvertStatement(stmt);
            if (hirStmt != null)
            {
                statements.Add(hirStmt);
            }
        }

        return new HirBlock
        {
            Span = block.Span,
            TypeId = GetTypeId(block),
            Statements = statements,
            Result = result
        };
    }

    private HirMatch ConvertLetQuestion(LetQuestionDecl letQuestionDecl, BlockExpr containingBlock, int statementIndex)
    {
        var matchTypeId = GetTypeId(containingBlock);
        var shortCircuitReturnTypeId = GetLetQuestionShortCircuitReturnTypeId(letQuestionDecl, matchTypeId);
        var hirMatch = new HirMatch
        {
            Scrutinee = ConvertExprOrFallback(letQuestionDecl.Value, "let? source", letQuestionDecl.Span),
            Span = letQuestionDecl.Span,
            TypeId = matchTypeId,
            IsExhaustive = true
        };

        hirMatch.Branches.Add(new HirMatchBranch
        {
            Pattern = BuildLetQuestionSuccessPattern(letQuestionDecl),
            Guard = null,
            Body = ConvertBlockFrom(containingBlock, statementIndex + 1)
        });

        hirMatch.Branches.Add(new HirMatchBranch
        {
            Pattern = BuildLetQuestionFailurePattern(letQuestionDecl),
            Guard = null,
            Body = new HirReturn
            {
                Value = BuildLetQuestionFailureValue(letQuestionDecl, shortCircuitReturnTypeId),
                Span = letQuestionDecl.Span,
                TypeId = shortCircuitReturnTypeId
            }
        });

        return hirMatch;
    }

    private HirPattern BuildLetQuestionSuccessPattern(LetQuestionDecl letQuestionDecl)
    {
        var constructorName = letQuestionDecl.BindingKind == LetQuestionBindingKind.Result ? "Ok" : "Some";
        return new HirCtorPattern
        {
            ConstructorName = constructorName,
            ConstructorSymbolId = letQuestionDecl.SuccessConstructorSymbolId,
            Span = letQuestionDecl.Span,
            TypeId = GetTypeId(letQuestionDecl.Value),
            Fields =
            [
                new HirFieldPattern
                {
                    FieldName = "_0",
                    Pattern = ConvertPattern(letQuestionDecl.Pattern)
                }
            ]
        };
    }

    private HirPattern BuildLetQuestionFailurePattern(LetQuestionDecl letQuestionDecl)
    {
        if (letQuestionDecl.BindingKind == LetQuestionBindingKind.Result)
        {
            var errorTypeId = GetLetQuestionFailurePayloadTypeId(letQuestionDecl);
            return new HirCtorPattern
            {
                ConstructorName = "Err",
                ConstructorSymbolId = letQuestionDecl.FailureConstructorSymbolId,
                Span = letQuestionDecl.Span,
                TypeId = GetTypeId(letQuestionDecl.Value),
                Fields =
                [
                    new HirFieldPattern
                    {
                        FieldName = "_0",
                        Pattern = new HirVarPattern
                        {
                            Name = GetLetQuestionFailureBindingName(letQuestionDecl),
                            SymbolId = letQuestionDecl.FailureBindingSymbolId,
                            Span = letQuestionDecl.Span,
                            TypeId = errorTypeId
                        }
                    }
                ]
            };
        }

        return new HirCtorPattern
        {
            ConstructorName = "None",
            ConstructorSymbolId = letQuestionDecl.FailureConstructorSymbolId,
            Span = letQuestionDecl.Span,
            TypeId = GetTypeId(letQuestionDecl.Value)
        };
    }

    private HirNode BuildLetQuestionFailureValue(LetQuestionDecl letQuestionDecl, TypeId returnTypeId)
    {
        HirCall failureCall;
        if (letQuestionDecl.BindingKind == LetQuestionBindingKind.Result)
        {
            failureCall = BuildLetQuestionConstructorCall(
                "Err",
                letQuestionDecl.FailureConstructorSymbolId,
                [
                    new HirVar
                    {
                        Name = GetLetQuestionFailureBindingName(letQuestionDecl),
                        SymbolId = letQuestionDecl.FailureBindingSymbolId,
                        Span = letQuestionDecl.Span,
                        TypeId = GetLetQuestionFailurePayloadTypeId(letQuestionDecl)
                    }
                ],
                letQuestionDecl.Span,
                returnTypeId);
        }
        else
        {
            failureCall = BuildLetQuestionConstructorCall(
                "None",
                letQuestionDecl.FailureConstructorSymbolId,
                [],
                letQuestionDecl.Span,
                returnTypeId);
        }

        if (letQuestionDecl.ShortCircuitReturnType is not DrivenType returnType ||
            _typeInferer?.Substitution.Apply(returnType) is not TyCon targetType ||
            _symbolTable.GetSymbol<CtorSymbol>(letQuestionDecl.FailureConstructorSymbolId) is not { } constructor ||
            _symbolTable.GetSymbol<AdtSymbol>(constructor.OwnerAdt) is not { IsCaseType: true } sourceCase)
        {
            return failureCall;
        }

        var sourceType = targetType with
        {
            Name = $"{targetType.Name}.{sourceCase.Name}",
            Symbol = sourceCase.Id,
            Id = sourceCase.TypeId
        };
        var sourceTypeId = _typeRegistry.GetOrCreateClosedCaseLayoutTypeId(sourceType);
        failureCall = failureCall with { TypeId = sourceTypeId };
        return new HirCaseInject
        {
            Operand = failureCall,
            SourceCase = sourceCase.Id,
            TargetAncestor = targetType.Symbol,
            SourceTypeId = sourceTypeId,
            TypeId = returnTypeId,
            Span = letQuestionDecl.Span
        };
    }

    private HirCall BuildLetQuestionConstructorCall(
        string constructorName,
        SymbolId constructorSymbolId,
        IReadOnlyList<HirNode> arguments,
        SourceSpan span,
        TypeId typeId)
    {
        var call = new HirCall
        {
            Function = new HirVar
            {
                Name = constructorName,
                SymbolId = constructorSymbolId,
                Span = span
            },
            Convention = CallConvention.Constructor,
            Span = span,
            TypeId = typeId
        };

        call.Arguments.AddRange(arguments);
        return call;
    }

    private TypeId GetLetQuestionFailurePayloadTypeId(LetQuestionDecl letQuestionDecl)
    {
        return letQuestionDecl.FailurePayloadType is DrivenType failurePayloadType
            ? GetTypeTypeId(failurePayloadType)
            : TypeId.None;
    }

    private TypeId GetLetQuestionShortCircuitReturnTypeId(LetQuestionDecl letQuestionDecl, TypeId fallbackTypeId)
    {
        return letQuestionDecl.ShortCircuitReturnType is DrivenType returnType
            ? GetTypeTypeId(returnType)
            : fallbackTypeId;
    }

    private string GetLetQuestionFailureBindingName(LetQuestionDecl letQuestionDecl)
    {
        if (letQuestionDecl.FailureBindingSymbolId.IsValid &&
            _symbolTable.GetSymbol<VarSymbol>(letQuestionDecl.FailureBindingSymbolId) is { Name.Length: > 0 } symbol)
        {
            return symbol.Name;
        }

        return $"{WellKnownStrings.InternalNames.LetQuestionErrorPrefix}{letQuestionDecl.Span.Position}";
    }

    private HirStatement? ConvertStatement(EidosAstNode stmt)
    {
        return stmt switch
        {
            LetDecl letDecl => new HirDeclStatement { Declaration = ConvertLetDecl(letDecl), Span = letDecl.Span },
            LetQuestionDecl => null,
            Assignment assign => new HirAssignStatement
            {
                Target = ConvertAssignmentTarget(assign),
                Value = ConvertExprOrFallback(assign.Value, "assignment value", assign.Span),
                Span = assign.Span
            },
            _ => new HirExprStatement { Expression = ConvertExpr(stmt), Span = stmt.Span }
        };
    }

    private HirNode ConvertAssignmentTarget(Assignment assign)
    {
        if (assign.TargetExpression != null)
        {
            return ConvertExpr(assign.TargetExpression);
        }

        return new HirVar
        {
            Name = assign.Target,
            SymbolId = assign.TargetSymbolId,
            Span = assign.Span,
            TypeId = ResolveDeclaredTypeId(assign.TargetSymbolId)
        };
    }

    private HirTuple ConvertTuple(TupleExpr tuple)
    {
        var hirTuple = new HirTuple { Span = tuple.Span, TypeId = GetTypeId(tuple) };
        foreach (var elem in tuple.Elements)
        {
            hirTuple.Elements.Add(ConvertExpr(elem));
        }
        return hirTuple;
    }

    private HirList ConvertList(ListExpr list)
    {
        var hirList = new HirList { Span = list.Span, TypeId = GetTypeId(list), HasRest = list.HasRest };
        foreach (var elem in list.Elements)
        {
            hirList.Elements.Add(ConvertExpr(elem));
        }
        return hirList;
    }

    private HirNode ConvertListComprehension(ListComprehension listComprehension)
    {
        if (listComprehension.Output == null)
        {
            return ReportAndCreateError(
                DiagnosticMessages.ListComprehensionRequiresOutputExpression,
                listComprehension.Span,
                "E5120",
                fallbackKind: "expression",
                reason: "missing-list-comprehension-output",
                context: "Seq comprehension output",
                astNodeKind: nameof(ListComprehension));
        }

        var hirComprehension = new HirListComprehension
        {
            Span = listComprehension.Span,
            TypeId = GetTypeId(listComprehension),
            Output = ConvertExpr(listComprehension.Output)
        };

        foreach (var qualifier in listComprehension.Qualifiers)
        {
            if (qualifier.Kind == QualifierKind.Generator)
            {
                if (qualifier.GeneratorPattern == null)
                {
                    Diagnostics.Add(CreateDiagnostic(
                        DiagnosticMessages.ListComprehensionGeneratorRequiresPattern,
                        qualifier.Span,
                        "E5120",
                        DiagnosticMessages.ListComprehensionMissingGeneratorPatternLabel));
                    continue;
                }

                if (qualifier.GeneratorExpression == null)
                {
                    Diagnostics.Add(CreateDiagnostic(
                        DiagnosticMessages.ListComprehensionGeneratorRequiresSourceExpression,
                        qualifier.Span,
                        "E5120",
                        DiagnosticMessages.ListComprehensionMissingGeneratorSourceLabel));
                    continue;
                }

                hirComprehension.Qualifiers.Add(new HirQualifier
                {
                    Kind = HirQualifierKind.Generator,
                    GeneratorPattern = ConvertPattern(qualifier.GeneratorPattern),
                    GeneratorSource = ConvertExpr(qualifier.GeneratorExpression),
                    Span = qualifier.Span
                });
                continue;
            }

            if (qualifier.GuardExpression == null)
            {
                Diagnostics.Add(CreateDiagnostic(
                    DiagnosticMessages.ListComprehensionGuardRequiresExpression,
                    qualifier.Span,
                    "E5120",
                    DiagnosticMessages.ListComprehensionMissingGuardExpressionLabel));
                continue;
            }

            hirComprehension.Qualifiers.Add(new HirQualifier
            {
                Kind = HirQualifierKind.Guard,
                GuardExpression = ConvertExpr(qualifier.GuardExpression),
                Span = qualifier.Span
            });
        }

        return hirComprehension;
    }

    private HirCall ConvertCtor(CtorExpr ctor)
    {
        var hirCall = new HirCall
        {
            Function = new HirVar { Name = ctor.ConstructorName, SymbolId = ctor.SymbolId, Span = ctor.Span },
            Convention = CallConvention.Constructor,
            Span = ctor.Span,
            TypeId = GetTypeId(ctor)
        };

        foreach (var arg in ctor.PositionalArgs)
        {
            hirCall.Arguments.Add(ConvertExpr(arg));
        }

        if (ctor.NamedArgs.Count > 0)
        {
            if (_symbolTable.GetSymbol<CtorSymbol>(ctor.SymbolId) is { } ctorSymbol &&
                ctorSymbol.NamedFields.Count > 0)
            {
                var namedArgsByField = ctor.NamedArgs
                    .Where(arg => !string.IsNullOrWhiteSpace(arg.FieldName) && arg.Value != null)
                    .ToDictionary(arg => arg.FieldName, StringComparer.Ordinal);

                foreach (var fieldSymbolId in ctorSymbol.NamedFields)
                {
                    if (_symbolTable.GetSymbol<FieldSymbol>(fieldSymbolId) is not { Name: { Length: > 0 } fieldName })
                    {
                        continue;
                    }

                    if (!namedArgsByField.TryGetValue(fieldName, out var fieldInit) || fieldInit.Value == null)
                    {
                        continue;
                    }

                    hirCall.Arguments.Add(ConvertExpr(fieldInit.Value));
                }
            }
            else
            {
                foreach (var fieldInit in ctor.NamedArgs)
                {
                    if (fieldInit.Value != null)
                    {
                        hirCall.Arguments.Add(ConvertExpr(fieldInit.Value));
                    }
                }
            }
        }

        return hirCall;
    }

    private CallConvention ResolveCallConvention(EidosAstNode? callTarget)
    {
        var symbolId = callTarget switch
        {
            IdentifierExpr identifier => identifier.SymbolId,
            PathExpr path => path.SymbolId,
            _ => SymbolId.None
        };

        if (!symbolId.IsValid)
        {
            return CallConvention.Normal;
        }

        return _symbolTable.GetSymbol<CtorSymbol>(symbolId) != null
            ? CallConvention.Constructor
            : CallConvention.Normal;
    }
}
