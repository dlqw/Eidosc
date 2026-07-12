using Eidosc.Diagnostic;
using Eidosc.Hir;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Mir;

/// <summary>
/// Module value subsystem: registration, cycle detection, validation, and lowering.
/// </summary>
public sealed partial class MirBuilder
{
    private static bool IsModuleLambdaValue(HirVal value, out HirLambda lambda)
    {
        if (value.IsModuleLevel &&
            value.Initializer is HirLambda hirLambda &&
            !string.IsNullOrWhiteSpace(value.Name))
        {
            lambda = hirLambda;
            return true;
        }

        lambda = null!;
        return false;
    }

    private void RegisterModuleValueGetter(HirVal value)
    {
        if (!value.IsModuleLevel ||
            value.IsComptime ||
            value.Initializer is HirLambda ||
            string.IsNullOrWhiteSpace(value.Name) ||
            IsBlockedModuleValue(value))
        {
            return;
        }

        var getterName = $"{WellKnownStrings.InternalNames.ModuleValueGetterPrefix}{NormalizeIdentifierSegment(value.Name, "value")}";
        var getterFunctionId = BuildGeneratedFunctionId(
            value.SymbolId,
            getterName,
            ResolveSymbolKind(value.SymbolId),
            "module_value_getter");
        if (value.SymbolId.IsValid)
        {
            _moduleValueGetterBySymbol[value.SymbolId] = getterName;
            _moduleValueGetterFunctionIdBySymbol[value.SymbolId] = getterFunctionId;
        }

        _moduleValueGetterByName[value.Name] = getterName;
        _moduleValueGetterFunctionIdByName[value.Name] = getterFunctionId;

        var returnType = ResolveModuleValueRuntimeType(value);
        if (returnType.IsValid)
        {
            if (value.SymbolId.IsValid)
            {
                _moduleValueGetterReturnTypeBySymbol[value.SymbolId] = returnType;
            }

            _moduleValueGetterReturnTypeByName[value.Name] = returnType;
        }
    }

    private void ValidateModuleValueInitializers(IReadOnlyList<HirVal> moduleValues)
    {
        foreach (var value in moduleValues)
        {
            if (!TryFindUnsupportedModuleValueInitializerNode(value.Initializer, out var unsupportedSpan))
            {
                continue;
            }

            if (value.SymbolId.IsValid)
            {
                _blockedModuleValueSymbols.Add(value.SymbolId);
            }

            if (!string.IsNullOrWhiteSpace(value.Name))
            {
                _blockedModuleValueNames.Add(value.Name);
            }

            var diagnostic = Diagnostic.Diagnostic.Error(
                DiagnosticMessages.ModuleValueInitializerCannotCall(value.Name),
                "E5300");
            if (HasSpan(unsupportedSpan))
            {
                diagnostic.WithLabel(unsupportedSpan, DiagnosticMessages.UnsupportedModuleInitializerLabel);
            }

            Diagnostics.Add(diagnostic);
        }
    }

    private bool TryGetModuleValueGetterName(HirVal value, out string getterName)
    {
        if (value.SymbolId.IsValid &&
            _moduleValueGetterBySymbol.TryGetValue(value.SymbolId, out var getterBySymbol))
        {
            getterName = getterBySymbol;
            return true;
        }

        if (_moduleValueGetterByName.TryGetValue(value.Name, out var getterByName))
        {
            getterName = getterByName;
            return true;
        }

        getterName = string.Empty;
        return false;
    }

    private bool IsBlockedModuleValue(HirVal value)
    {
        return (value.SymbolId.IsValid && _blockedModuleValueSymbols.Contains(value.SymbolId)) ||
               _blockedModuleValueNames.Contains(value.Name);
    }

    private MirFunc? ConvertModuleLambdaValue(HirVal value, HirLambda lambda)
    {
        var flattenedLambda = FlattenCurriedLambdaBody(lambda);
        if (flattenedLambda.Captures.Count > 0)
        {
            var diagnostic = Diagnostic.Diagnostic.Error(
                DiagnosticMessages.ModuleLambdaCapturesState(value.Name),
                "E5300");
            if (HasSpan(value.Span))
            {
                diagnostic.WithLabel(value.Span, DiagnosticMessages.ModuleLambdaValueLabel);
            }

            Diagnostics.Add(diagnostic);
            return null;
        }

        var loweredLambda = flattenedLambda with
        {
            SymbolId = value.SymbolId,
            ReturnType = GetLambdaReturnType(flattenedLambda)
        };

        return ConvertLambdaToFunction(
            loweredLambda,
            value.Name,
            BuildGeneratedFunctionId(value.SymbolId, value.Name, ResolveSymbolKind(value.SymbolId), "module_lambda_value"));
    }

    private MirFunc? ConvertModuleValueGetter(HirVal value)
    {
        if (IsBlockedModuleValue(value))
        {
            return null;
        }

        if (value.IsComptime)
        {
            return null;
        }

        if (!TryGetModuleValueGetterName(value, out var getterName))
        {
            return null;
        }

        var getterReturnType = ResolveModuleValueRuntimeType(value);
        var getterLambda = new HirLambda
        {
            Parameters = [],
            ReturnType = getterReturnType,
            Body = value.Initializer,
            Captures = [],
            Span = value.Span,
            SymbolId = value.SymbolId,
            TypeId = getterReturnType
        };

        return ConvertLambdaToFunction(
            getterLambda,
            getterName,
            ResolveModuleValueGetterFunctionId(value, getterName));
    }

    private FunctionId ResolveModuleValueGetterFunctionId(HirVal value, string getterName)
    {
        if (value.SymbolId.IsValid &&
            _moduleValueGetterFunctionIdBySymbol.TryGetValue(value.SymbolId, out var bySymbol))
        {
            return bySymbol;
        }

        if (!string.IsNullOrWhiteSpace(value.Name) &&
            _moduleValueGetterFunctionIdByName.TryGetValue(value.Name, out var byName))
        {
            return byName;
        }

        return BuildGeneratedFunctionId(
            value.SymbolId,
            getterName,
            ResolveSymbolKind(value.SymbolId),
            "module_value_getter");
    }

    private static TypeId ResolveModuleValueRuntimeType(HirVal value)
    {
        if (value.TypeId.IsValid)
        {
            return value.TypeId;
        }

        return value.Initializer switch
        {
            { TypeId: { IsValid: true } typeId } => typeId,
            _ => TypeId.None
        };
    }

    private void DetectModuleValueCycles(IReadOnlyList<HirVal> moduleValues)
    {
        if (moduleValues.Count == 0)
        {
            return;
        }

        var valuesBySymbol = moduleValues
            .Where(static value => value.SymbolId.IsValid)
            .ToDictionary(value => value.SymbolId);
        var valuesByName = BuildUniqueModuleValueNameMap(moduleValues);
        var dependencies = new Dictionary<HirVal, List<HirVal>>();

        foreach (var value in moduleValues)
        {
            var refs = new HashSet<HirVal>();
            CollectModuleValueDependencies(value.Initializer, valuesBySymbol, valuesByName, refs);
            dependencies[value] = refs.ToList();
        }

        var visiting = new HashSet<HirVal>();
        var visited = new HashSet<HirVal>();
        var stack = new List<HirVal>();

        foreach (var value in moduleValues)
        {
            VisitModuleValue(value, dependencies, visiting, visited, stack);
        }
    }

    private void VisitModuleValue(
        HirVal value,
        IReadOnlyDictionary<HirVal, List<HirVal>> dependencies,
        HashSet<HirVal> visiting,
        HashSet<HirVal> visited,
        List<HirVal> stack)
    {
        if (visited.Contains(value))
        {
            return;
        }

        if (!visiting.Add(value))
        {
            var cycleStart = stack.FindIndex(candidate => ReferenceEquals(candidate, value));
            if (cycleStart >= 0)
            {
                ReportModuleValueCycle(stack.Skip(cycleStart).Append(value).ToList());
            }

            return;
        }

        stack.Add(value);
        if (dependencies.TryGetValue(value, out var directDependencies))
        {
            foreach (var dependency in directDependencies)
            {
                VisitModuleValue(dependency, dependencies, visiting, visited, stack);
            }
        }

        stack.RemoveAt(stack.Count - 1);
        visiting.Remove(value);
        visited.Add(value);
    }

    private void ReportModuleValueCycle(IReadOnlyList<HirVal> cycle)
    {
        if (cycle.Count == 0)
        {
            return;
        }

        foreach (var value in cycle)
        {
            if (value.SymbolId.IsValid)
            {
                _blockedModuleValueSymbols.Add(value.SymbolId);
            }

            if (!string.IsNullOrWhiteSpace(value.Name))
            {
                _blockedModuleValueNames.Add(value.Name);
            }
        }

        var cycleNames = cycle
            .Select(value => string.IsNullOrWhiteSpace(value.Name) ? "<unnamed>" : value.Name)
            .ToList();
        var diagnostic = Diagnostic.Diagnostic.Error(
            DiagnosticMessages.ModuleValueDependencyCycleDetected(string.Join(" -> ", cycleNames)),
            "E5300");
        var first = cycle[0];
        if (HasSpan(first.Span))
        {
            diagnostic.WithLabel(first.Span, DiagnosticMessages.ModuleLevelValueCycleLabel);
        }

        Diagnostics.Add(diagnostic);
    }

    private static Dictionary<string, HirVal> BuildUniqueModuleValueNameMap(IReadOnlyList<HirVal> moduleValues)
    {
        var valuesByName = new Dictionary<string, HirVal>(StringComparer.Ordinal);
        var ambiguousNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var value in moduleValues)
        {
            if (string.IsNullOrWhiteSpace(value.Name) || value.SymbolId.IsValid)
            {
                continue;
            }

            if (ambiguousNames.Contains(value.Name))
            {
                continue;
            }

            if (valuesByName.TryGetValue(value.Name, out var existing) &&
                !ReferenceEquals(existing, value))
            {
                valuesByName.Remove(value.Name);
                ambiguousNames.Add(value.Name);
                continue;
            }

            valuesByName[value.Name] = value;
        }

        return valuesByName;
    }

    private static void CollectModuleValueDependencies(
        HirNode? node,
        IReadOnlyDictionary<SymbolId, HirVal> valuesBySymbol,
        IReadOnlyDictionary<string, HirVal> valuesByName,
        HashSet<HirVal> dependencies)
    {
        TraverseHirNode(node, new ModuleValueDependencyCollector(valuesBySymbol, valuesByName, dependencies));
    }

    private static bool TryFindUnsupportedModuleValueInitializerNode(HirNode? node, out SourceSpan unsupportedSpan)
    {
        var finder = new UnsupportedNodeFinder();
        TraverseHirNode(node, finder);
        unsupportedSpan = finder.FoundSpan;
        return finder.HasFound;
    }

    /// <summary>
    /// Shared HIR tree traversal. Visits each node once, delegating to the visitor
    /// for per-node classification. Returns false from Visit to prune children.
    /// </summary>
    private static void TraverseHirNode(HirNode? node, IHirNodeVisitor visitor)
    {
        if (node == null || visitor.HasFound)
        {
            return;
        }

        if (!visitor.Visit(node))
        {
            return;
        }

        foreach (var child in EnumerateHirChildren(node, visitor))
        {
            TraverseHirNode(child, visitor);
            if (visitor.HasFound)
            {
                return;
            }
        }
    }

    private static IEnumerable<HirNode?> EnumerateHirChildren(HirNode node, IHirNodeVisitor visitor)
    {
        switch (node)
        {
            case HirTuple tuple:
                foreach (var e in tuple.Elements) yield return e;
                break;
            case HirList list:
                foreach (var e in list.Elements) yield return e;
                break;
            case HirCall call:
                yield return call.Function;
                foreach (var a in call.Arguments) yield return a;
                break;
            case HirBinOp binOp:
                yield return binOp.Left;
                yield return binOp.Right;
                break;
            case HirUnaryOp unaryOp:
                yield return unaryOp.Operand;
                break;
            case HirBlock block:
                foreach (var stmt in block.Statements)
                {
                    if (!visitor.VisitStatement(stmt))
                    {
                        yield break;
                    }

                    switch (stmt)
                    {
                        case HirExprStatement exprStmt: yield return exprStmt.Expression; break;
                        case HirAssignStatement assignStmt:
                            yield return assignStmt.Target;
                            yield return assignStmt.Value;
                            break;
                        case HirDeclStatement declStmt when declStmt.Declaration is HirVal valDecl:
                            yield return valDecl.Initializer;
                            break;
                        case HirDeclStatement declStmt when declStmt.Declaration is HirVarDecl varDecl:
                            yield return varDecl.Initializer;
                            break;
                    }
                }
                yield return block.Result;
                break;
            case HirIf ifExpr:
                yield return ifExpr.Condition;
                yield return ifExpr.ThenBranch;
                yield return ifExpr.ElseBranch;
                break;
            case HirMatch match:
                yield return match.Scrutinee;
                foreach (var b in match.Branches)
                {
                    yield return b.Guard;
                    yield return b.Body;
                }
                break;
            case HirReturn returnExpr:
                yield return returnExpr.Value;
                break;
            case HirBreak breakExpr:
                yield return breakExpr.Value;
                break;
            case HirFieldAccess fieldAccess:
                yield return fieldAccess.Target;
                break;
            case HirIndexAccess indexAccess:
                yield return indexAccess.Target;
                yield return indexAccess.Index;
                break;
            case HirListComprehension comprehension:
                foreach (var q in comprehension.Qualifiers)
                {
                    yield return q.GeneratorSource;
                    yield return q.GuardExpression;
                }
                yield return comprehension.Output;
                break;
        }
    }

    private interface IHirNodeVisitor
    {
        /// <returns>false to skip children, true to continue traversal.</returns>
        bool Visit(HirNode node);
        /// <returns>false to stop traversal (found unsupported), true to continue.</returns>
        bool VisitStatement(HirStatement stmt);
        bool HasFound { get; }
    }

    private sealed class ModuleValueDependencyCollector(
        IReadOnlyDictionary<SymbolId, HirVal> valuesBySymbol,
        IReadOnlyDictionary<string, HirVal> valuesByName,
        HashSet<HirVal> dependencies) : IHirNodeVisitor
    {
        public bool HasFound => false;

        public bool Visit(HirNode node)
        {
            if (node is HirVar variable)
            {
                if (variable.SymbolId.IsValid && valuesBySymbol.TryGetValue(variable.SymbolId, out var bySymbol))
                {
                    dependencies.Add(bySymbol);
                }
                else if (!string.IsNullOrWhiteSpace(variable.Name) && valuesByName.TryGetValue(variable.Name, out var byName))
                {
                    dependencies.Add(byName);
                }
            }

            return node is not (HirLambda or HirLiteral);
        }

        public bool VisitStatement(HirStatement stmt) => true;
    }

    private sealed class UnsupportedNodeFinder : IHirNodeVisitor
    {
        public SourceSpan FoundSpan { get; private set; }
        public bool HasFound { get; private set; }

        public bool Visit(HirNode node)
        {
            switch (node)
            {
                // Always unsupported
                case HirCall:
                case HirListComprehension or HirLoop or HirReturn or HirBreak or HirContinue:
                    FoundSpan = node.Span;
                    HasFound = true;
                    return false;
                // Leaf — safe, no children to traverse
                case HirLiteral or HirVar or HirLambda:
                    return false;
                default:
                    return true;
            }
        }

        public bool VisitStatement(HirStatement stmt)
        {
            if (stmt is HirDeclStatement decl)
            {
                FoundSpan = decl.Span;
                HasFound = true;
                return false;
            }

            return true;
        }
    }
}
