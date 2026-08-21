using Eidosc.Diagnostic;
using Eidosc.Hir;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Mir;

/// <summary>
/// Module-level mutable variable registration and constant initializer lowering.
/// </summary>
public sealed partial class MirBuilder
{
    private void RegisterModuleVars(IReadOnlyList<HirVarDecl> moduleVars)
    {
        // 第一遍：注册全部模块变量。字面量/字符串常量之外的非静态初始化器
        // 登记为运行时初始化（合成 init 函数），不再直接报错。
        var runtimeCandidates = new List<HirVarDecl>();
        foreach (var varDecl in moduleVars)
        {
            var typeId = ResolveModuleVarType(varDecl);

            // extern(c) 变量：declaration-only 全局，不参与任何初始化路径。
            if (varDecl.IsExternal)
            {
                var externalVar = new MirModuleVar
                {
                    Name = string.IsNullOrWhiteSpace(varDecl.Name) ? "$var" : varDecl.Name,
                    SymbolId = varDecl.SymbolId,
                    TypeId = typeId,
                    IsMutable = true,
                    IsExternal = true,
                    ExternalName = varDecl.ExternalSymbolName,
                    Span = varDecl.Span
                };

                if (varDecl.SymbolId.IsValid)
                {
                    _moduleVarsBySymbol[varDecl.SymbolId] = externalVar;
                }

                if (!string.IsNullOrWhiteSpace(varDecl.Name))
                {
                    _moduleVarsByName[varDecl.Name] = externalVar;
                }

                continue;
            }

            var hasConstantInit = TryConvertModuleVarConstantInitializer(varDecl.Initializer, out var constant);
            var needsRuntimeInit = !hasConstantInit || constant is MirConstant
            {
                Value: MirConstantValue.StringValue or MirConstantValue.RawStringValue
            };

            var hasUnsupportedRuntimeInit = needsRuntimeInit && varDecl.Initializer == null;
            SourceSpan unsupportedSpan = default;
            if (needsRuntimeInit && !hasUnsupportedRuntimeInit)
            {
                hasUnsupportedRuntimeInit =
                    TryFindUnsupportedModuleVarRuntimeInitNode(varDecl.Initializer, out unsupportedSpan);
            }

            if (hasUnsupportedRuntimeInit)
            {
                // 控制流逃逸（return/break/loop/推导式）或缺失的初始化器
                // 不能作为 init 函数体，维持 E5312。
                var diagnostic = Diagnostic.Diagnostic.Error(
                    DiagnosticMessages.ModuleVariableInitializerNotConstant(varDecl.Name),
                    "E5312");
                if (HasSpan(varDecl.Span))
                {
                    diagnostic.WithLabel(varDecl.Span, DiagnosticMessages.ModuleVariableInitializerNotConstantLabel);
                }

                if (HasSpan(unsupportedSpan))
                {
                    diagnostic.WithLabel(unsupportedSpan, DiagnosticMessages.UnsupportedModuleInitializerLabel);
                }

                Diagnostics.Add(diagnostic);
                needsRuntimeInit = false;
            }

            if (needsRuntimeInit)
            {
                runtimeCandidates.Add(varDecl);
            }

            var moduleVar = new MirModuleVar
            {
                Name = string.IsNullOrWhiteSpace(varDecl.Name) ? "$var" : varDecl.Name,
                SymbolId = varDecl.SymbolId,
                TypeId = typeId,
                IsMutable = true,
                Initializer = hasConstantInit
                    ? constant
                    : CreatePoisonOperand(typeId, varDecl.Span, DiagnosticMessages.ModuleVariableInitializerNotConstantReason(varDecl.Name)),
                Span = varDecl.Span
            };

            if (varDecl.SymbolId.IsValid)
            {
                _moduleVarsBySymbol[varDecl.SymbolId] = moduleVar;
            }

            if (!string.IsNullOrWhiteSpace(varDecl.Name))
            {
                _moduleVarsByName[varDecl.Name] = moduleVar;
            }
        }

        // 第二遍：运行时初始化器之间的依赖拓扑排序，分配 init 函数名与顺序。
        AssignModuleVarRuntimeInitializers(moduleVars, runtimeCandidates);
    }

    private void AssignModuleVarRuntimeInitializers(
        IReadOnlyList<HirVarDecl> moduleVars,
        IReadOnlyList<HirVarDecl> runtimeCandidates)
    {
        if (runtimeCandidates.Count == 0)
        {
            return;
        }

        var candidatesBySymbol = runtimeCandidates
            .Where(static varDecl => varDecl.SymbolId.IsValid)
            .ToDictionary(static varDecl => varDecl.SymbolId);
        var candidatesByName = new Dictionary<string, HirVarDecl>(StringComparer.Ordinal);
        foreach (var varDecl in runtimeCandidates)
        {
            if (!string.IsNullOrWhiteSpace(varDecl.Name) && !varDecl.SymbolId.IsValid)
            {
                candidatesByName[varDecl.Name] = varDecl;
            }
        }

        var dependencies = new Dictionary<HirVarDecl, List<HirVarDecl>>();
        foreach (var varDecl in runtimeCandidates)
        {
            var refs = new HashSet<HirVarDecl>();
            CollectModuleVarDependencies(varDecl.Initializer, candidatesBySymbol, candidatesByName, refs);
            dependencies[varDecl] = refs.ToList();
        }

        var blocked = new HashSet<HirVarDecl>();
        var visiting = new HashSet<HirVarDecl>();
        var visited = new HashSet<HirVarDecl>();
        var stack = new List<HirVarDecl>();
        var order = 0;

        void Visit(HirVarDecl varDecl)
        {
            if (visited.Contains(varDecl))
            {
                return;
            }

            if (!visiting.Add(varDecl))
            {
                var cycleStart = stack.FindIndex(candidate => ReferenceEquals(candidate, varDecl));
                if (cycleStart >= 0)
                {
                    var cycle = stack.Skip(cycleStart).Append(varDecl).ToList();
                    foreach (var member in cycle)
                    {
                        blocked.Add(member);
                    }

                    var cycleNames = cycle
                        .Select(static decl => string.IsNullOrWhiteSpace(decl.Name) ? "<unnamed>" : decl.Name)
                        .ToList();
                    var diagnostic = Diagnostic.Diagnostic.Error(
                        DiagnosticMessages.ModuleValueDependencyCycleDetected(string.Join(" -> ", cycleNames)),
                        "E5300");
                    if (HasSpan(cycle[0].Span))
                    {
                        diagnostic.WithLabel(cycle[0].Span, DiagnosticMessages.ModuleLevelValueCycleLabel);
                    }

                    Diagnostics.Add(diagnostic);
                }

                return;
            }

            stack.Add(varDecl);
            if (dependencies.TryGetValue(varDecl, out var directDependencies))
            {
                foreach (var dependency in directDependencies)
                {
                    Visit(dependency);
                }
            }

            stack.RemoveAt(stack.Count - 1);
            visiting.Remove(varDecl);
            visited.Add(varDecl);

            if (blocked.Contains(varDecl))
            {
                return;
            }

            // 后序访问：依赖的 init 先登记，保证初始化顺序。
            order++;
            var initName = $"{WellKnownStrings.InternalNames.ModuleVarInitializerPrefix}{NormalizeIdentifierSegment(varDecl.Name, "var")}";
            var updated = UpdateRegisteredModuleVar(varDecl, initName, order);
            if (updated != null)
            {
                _pendingModuleVarInitializers.Add((varDecl, initName, order));
            }
        }

        foreach (var varDecl in runtimeCandidates)
        {
            Visit(varDecl);
        }
    }

    private MirModuleVar? UpdateRegisteredModuleVar(HirVarDecl varDecl, string initName, int order)
    {
        MirModuleVar? existing = null;
        if (varDecl.SymbolId.IsValid && _moduleVarsBySymbol.TryGetValue(varDecl.SymbolId, out var bySymbol))
        {
            existing = bySymbol;
        }
        else if (!string.IsNullOrWhiteSpace(varDecl.Name) && _moduleVarsByName.TryGetValue(varDecl.Name, out var byName))
        {
            existing = byName;
        }

        if (existing == null)
        {
            return null;
        }

        var updated = existing with { RuntimeInitializerName = initName, RuntimeInitOrder = order };
        if (varDecl.SymbolId.IsValid)
        {
            _moduleVarsBySymbol[varDecl.SymbolId] = updated;
        }

        if (!string.IsNullOrWhiteSpace(varDecl.Name))
        {
            _moduleVarsByName[varDecl.Name] = updated;
        }

        return updated;
    }

    private static void CollectModuleVarDependencies(
        HirNode? node,
        IReadOnlyDictionary<SymbolId, HirVarDecl> candidatesBySymbol,
        IReadOnlyDictionary<string, HirVarDecl> candidatesByName,
        HashSet<HirVarDecl> dependencies) =>
        TraverseHirNode(node, new ModuleVarDependencyCollector(candidatesBySymbol, candidatesByName, dependencies));

    private sealed class ModuleVarDependencyCollector(
        IReadOnlyDictionary<SymbolId, HirVarDecl> candidatesBySymbol,
        IReadOnlyDictionary<string, HirVarDecl> candidatesByName,
        HashSet<HirVarDecl> dependencies) : IHirNodeVisitor
    {
        public bool HasFound => false;

        public bool Visit(HirNode node)
        {
            if (node is HirVar variable)
            {
                if (variable.SymbolId.IsValid && candidatesBySymbol.TryGetValue(variable.SymbolId, out var bySymbol))
                {
                    dependencies.Add(bySymbol);
                }
                else if (!string.IsNullOrWhiteSpace(variable.Name) && candidatesByName.TryGetValue(variable.Name, out var byName))
                {
                    dependencies.Add(byName);
                }
            }

            return node is not HirLambda;
        }

        public bool VisitStatement(HirStatement stmt) => true;
    }

    /// <summary>
    /// 运行时初始化器经合成函数求值，允许调用与普通表达式；仅拒绝逃逸
    /// 表达式边界的控制流（return/break/loop/推导式）。
    /// </summary>
    private static bool TryFindUnsupportedModuleVarRuntimeInitNode(HirNode? node, out SourceSpan unsupportedSpan)
    {
        var finder = new UnsupportedRuntimeInitNodeFinder();
        TraverseHirNode(node, finder);
        unsupportedSpan = finder.FoundSpan;
        return finder.HasFound;
    }

    private sealed class UnsupportedRuntimeInitNodeFinder : IHirNodeVisitor
    {
        public SourceSpan FoundSpan { get; private set; }
        public bool HasFound { get; private set; }

        public bool Visit(HirNode node)
        {
            switch (node)
            {
                case HirReturn or HirBreak or HirLoop or HirListComprehension:
                    FoundSpan = node.Span;
                    HasFound = true;
                    return false;
                default:
                    return true;
            }
        }

        public bool VisitStatement(HirStatement stmt) => true;
    }

    /// <summary>
    /// 合成运行时初始化函数：无参、返回模块变量类型的求值函数，
    /// 在 <c>eidos_module_init</c> 中按拓扑序调用并存储到全局。
    /// </summary>
    private MirFunc? ConvertModuleVarRuntimeInitializer(HirVarDecl varDecl, string initName)
    {
        var typeId = ResolveModuleVarType(varDecl);
        if (!typeId.IsValid)
        {
            return null;
        }

        var initLambda = new HirLambda
        {
            Parameters = [],
            ReturnType = typeId,
            Body = varDecl.Initializer,
            Captures = [],
            Span = varDecl.Span,
            SymbolId = varDecl.SymbolId,
            TypeId = typeId
        };

        return ConvertLambdaToFunction(
            initLambda,
            initName,
            BuildGeneratedFunctionId(varDecl.SymbolId, initName, ResolveSymbolKind(varDecl.SymbolId), "module_var_init"));
    }

    private List<MirModuleVar> CreateModuleVarList(IReadOnlyList<HirVarDecl> moduleVars)
    {
        var result = new List<MirModuleVar>(moduleVars.Count);
        foreach (var varDecl in moduleVars)
        {
            var moduleVar = ResolveRegisteredModuleVar(varDecl);
            if (moduleVar == null)
            {
                continue;
            }

            result.Add(moduleVar);
        }

        return result;
    }

    private MirModuleVar? ResolveRegisteredModuleVar(HirVarDecl varDecl)
    {
        if (varDecl.SymbolId.IsValid &&
            _moduleVarsBySymbol.TryGetValue(varDecl.SymbolId, out var bySymbol))
        {
            return bySymbol;
        }

        if (!string.IsNullOrWhiteSpace(varDecl.Name) &&
            _moduleVarsByName.TryGetValue(varDecl.Name, out var byName))
        {
            return byName;
        }

        return null;
    }

    private bool TryResolveModuleVarPlace(HirVar variable, out MirPlace place)
    {
        if (variable.SymbolId.IsValid &&
            _moduleVarsBySymbol.TryGetValue(variable.SymbolId, out var bySymbol))
        {
            place = CreateModuleVarPlace(variable, bySymbol);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(variable.Name) &&
            _moduleVarsByName.TryGetValue(variable.Name, out var byName))
        {
            place = CreateModuleVarPlace(variable, byName);
            return true;
        }

        place = null!;
        return false;
    }

    private static MirPlace CreateModuleVarPlace(HirVar variable, MirModuleVar moduleVar)
    {
        return new MirPlace
        {
            Kind = PlaceKind.ModuleVar,
            ModuleVarName = moduleVar.Name,
            ModuleVarSymbol = moduleVar.SymbolId,
            TypeId = variable.TypeId.IsValid ? variable.TypeId : moduleVar.TypeId,
            Span = variable.Span
        };
    }

    private static TypeId ResolveModuleVarType(HirVarDecl varDecl)
    {
        if (varDecl.TypeId.IsValid)
        {
            return varDecl.TypeId;
        }

        return varDecl.Initializer?.TypeId ?? TypeId.None;
    }

    private bool TryConvertModuleVarConstantInitializer(HirNode? node, out MirOperand constant)
    {
        switch (node)
        {
            case HirLiteral literal:
                constant = ConvertLiteral(literal);
                return true;

            case HirUnaryOp
            {
                Operator: Eidosc.Hir.UnaryOp.Neg,
                Operand: HirLiteral negatedLiteral
            } unaryOp:
            {
                var operand = ConvertLiteral(negatedLiteral);
                if (operand is not MirConstant { Value: var value } baseConstant)
                {
                    constant = null!;
                    return false;
                }

                constant = value switch
                {
                    MirConstantValue.IntValue intValue =>
                        new MirConstant
                        {
                            Value = new MirConstantValue.IntValue(unchecked(-intValue.Value)),
                            TypeId = unaryOp.TypeId.IsValid ? unaryOp.TypeId : baseConstant.TypeId,
                            Span = unaryOp.Span
                        },
                    MirConstantValue.BigIntValue bigIntValue =>
                        new MirConstant
                        {
                            Value = new MirConstantValue.BigIntValue(-bigIntValue.Value),
                            TypeId = unaryOp.TypeId.IsValid ? unaryOp.TypeId : baseConstant.TypeId,
                            Span = unaryOp.Span
                        },
                    MirConstantValue.FloatValue floatValue =>
                        new MirConstant
                        {
                            Value = new MirConstantValue.FloatValue(-floatValue.Value),
                            TypeId = unaryOp.TypeId.IsValid ? unaryOp.TypeId : baseConstant.TypeId,
                            Span = unaryOp.Span
                        },
                    _ => null!
                };
                return constant != null;
            }

            default:
                constant = null!;
                return false;
        }
    }
}
