using Eidosc.Diagnostic;

namespace Eidosc.Symbols;

/// <summary>
/// 路径解析器 - 处理完整模块路径
/// </summary>
public sealed class PathResolver
{
    private readonly SymbolTable _symbolTable;
    private readonly ModuleRegistry _moduleRegistry;

    public PathResolver(SymbolTable symbolTable, ModuleRegistry moduleRegistry)
    {
        _symbolTable = symbolTable;
        _moduleRegistry = moduleRegistry;
    }

    /// <summary>
    /// 解析完整路径
    /// </summary>
    /// <param name="path">路径部分 (如 ["std", "collection", WellKnownStrings.BuiltinTypes.Seq])</param>
    /// <param name="context">当前模块上下文 (用于相对路径)</param>
    /// <returns>解析结果</returns>
    public PathResolutionResult Resolve(IReadOnlyList<string> path, SymbolId? context = null)
    {
        if (path.Count == 0)
            return PathResolutionResult.NotFound(DiagnosticMessages.EmptyPath);

        // 单一名称：在当前作用域查找
        if (path.Count == 1)
        {
            return ResolveSimpleName(path[0]);
        }

        // 限定路径：按模块层次解析
        return ResolveQualifiedPath(path, context);
    }

    /// <summary>
    /// 解析简单名称
    /// </summary>
    private PathResolutionResult ResolveSimpleName(string name)
    {
        // 1. 查找变量/函数
        var valueSymbol = _symbolTable.LookupValue(name);
        if (valueSymbol != null)
        {
            return PathResolutionResult.Found(valueSymbol.Value, ResolutionKind.Value);
        }

        // 2. 查找类型
        var typeSymbol = _symbolTable.LookupType(name);
        if (typeSymbol != null)
        {
            return PathResolutionResult.Found(typeSymbol.Value, ResolutionKind.Type);
        }

        // 3. 查找构造器
        var ctorSymbol = _symbolTable.LookupConstructor(name);
        if (ctorSymbol != null)
        {
            return PathResolutionResult.Found(ctorSymbol.Value, ResolutionKind.Constructor);
        }

        // 4. 查找模块
        var moduleSymbol = _moduleRegistry.LookupRootModule(name);
        if (moduleSymbol != null)
        {
            return PathResolutionResult.Found(moduleSymbol.Value, ResolutionKind.Module);
        }

        // 5. 查找能力
        var abilitySymbol = _symbolTable.LookupEffect(name);
        if (abilitySymbol != null)
        {
            return PathResolutionResult.Found(abilitySymbol.Value, ResolutionKind.Effect);
        }

        return PathResolutionResult.NotFound(DiagnosticMessages.UndefinedIdentifier(name));
    }

    /// <summary>
    /// 解析限定路径
    /// </summary>
    private PathResolutionResult ResolveQualifiedPath(IReadOnlyList<string> path, SymbolId? context)
    {
        var matches = new List<PathResolutionResult>();
        if (_symbolTable.LookupType(path[0]) is { } rootType)
        {
            var typeMember = ResolveMemberPath(rootType, path.Skip(1).ToList(), context);
            if (typeMember != null)
            {
                matches.Add(typeMember);
            }
        }

        for (int splitIndex = path.Count - 1; splitIndex >= 1; splitIndex--)
        {
            var modulePath = path.Take(splitIndex).ToList();
            var remainingSegments = path.Skip(splitIndex).ToList();

            foreach (var moduleId in LookupModulePathCandidates(modulePath, context))
            {
                var memberResult = ResolveWithinModule(moduleId, remainingSegments, context);
                if (memberResult != null)
                {
                    matches.Add(memberResult);
                }
            }
        }

        var distinctMatches = matches
            .DistinctBy(static result => (result.SymbolId, result.Kind))
            .ToList();
        if (distinctMatches.Count == 1)
        {
            return distinctMatches[0];
        }

        if (distinctMatches.Count > 1)
        {
            var displayPath = string.Join(WellKnownStrings.Separators.Path, path);
            var candidates = string.Join(", ", distinctMatches
                .Select(FormatResolvedSymbolName)
                .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static candidate => candidate, StringComparer.Ordinal));
            return PathResolutionResult.NotFound(DiagnosticMessages.AmbiguousPathWithCandidates(displayPath, candidates));
        }

        return PathResolutionResult.NotFound(DiagnosticMessages.CannotResolvePath(
            string.Join(WellKnownStrings.Separators.Path, path)));
    }

    private PathResolutionResult? ResolveWithinModule(
        SymbolId moduleId,
        IReadOnlyList<string> remainingSegments,
        SymbolId? requesterModuleId)
    {
        if (!moduleId.IsValid || remainingSegments.Count == 0)
        {
            return null;
        }

        if (remainingSegments.Count == 1 &&
            LookupTraitMemberInModule(moduleId, remainingSegments[0], requesterModuleId) is { } traitMember)
        {
            return traitMember;
        }

        if (LookupModuleMember(moduleId, remainingSegments[0], requesterModuleId) is { } memberResult)
        {
            if (remainingSegments.Count == 1)
            {
                return memberResult;
            }

            return ResolveMemberPath(memberResult.SymbolId, remainingSegments.Skip(1).ToList(), requesterModuleId);
        }

        if (remainingSegments.Count == 1)
        {
            return LookupSameNamedEffectMember(moduleId, remainingSegments[0], requesterModuleId);
        }

        return null;
    }

    private PathResolutionResult? ResolveMemberPath(
        SymbolId symbolId,
        IReadOnlyList<string> remainingSegments,
        SymbolId? requesterModuleId)
    {
        if (!symbolId.IsValid || remainingSegments.Count == 0)
        {
            return null;
        }

        var symbol = _symbolTable.GetSymbol(symbolId);
        if (symbol == null)
        {
            return null;
        }

        return symbol switch
        {
            ModuleSymbol => ResolveWithinModule(symbolId, remainingSegments, requesterModuleId),
            TraitSymbol trait when remainingSegments.Count == 1
                => LookupTraitMethod(trait, remainingSegments[0]),
            AdtSymbol when remainingSegments.Count == 1
                => LookupTypeMember(symbolId, remainingSegments[0]),
            AdtSymbol => ResolveAdtMemberPath(symbolId, remainingSegments, requesterModuleId),
            _ => null
        };
    }

    private PathResolutionResult? ResolveAdtMemberPath(
        SymbolId ownerId,
        IReadOnlyList<string> remainingSegments,
        SymbolId? requesterModuleId)
    {
        _ = requesterModuleId;
        if (remainingSegments.Count == 0)
        {
            return null;
        }

        var directCase = _symbolTable.LookupDirectCase(ownerId, remainingSegments[0]);
        if (!directCase.HasValue)
        {
            return null;
        }

        return remainingSegments.Count == 1
            ? PathResolutionResult.Found(directCase.Value, ResolutionKind.Type)
            : ResolveMemberPath(directCase.Value, remainingSegments.Skip(1).ToList(), requesterModuleId);
    }

    private PathResolutionResult? LookupTypeMember(SymbolId typeId, string memberName)
    {
        if (_symbolTable.LookupDirectCase(typeId, memberName) is { } caseType)
        {
            return PathResolutionResult.Found(caseType, ResolutionKind.Type);
        }

        return LookupTypeConstructor(typeId, memberName);
    }

    private IEnumerable<SymbolId> LookupModulePathCandidates(
        IReadOnlyList<string> relativeOrAbsolutePath,
        SymbolId? context)
    {
        var seen = new HashSet<SymbolId>();

        foreach (var moduleId in _moduleRegistry.LookupModuleCandidatesByPath(relativeOrAbsolutePath))
        {
            if (seen.Add(moduleId))
            {
                yield return moduleId;
            }
        }

        if (!context.HasValue || !context.Value.IsValid)
        {
            yield break;
        }

        var contextModule = _moduleRegistry.GetModule(context.Value);
        if (contextModule == null || contextModule.Path.Count == 0)
        {
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(contextModule.PackageAlias) &&
            TryLookupModuleCandidate(contextModule.PackageAlias, relativeOrAbsolutePath, seen, out var packageDirectModuleId))
        {
            yield return packageDirectModuleId;
        }

        for (var prefixLength = contextModule.Path.Count; prefixLength >= 1; prefixLength--)
        {
            var candidatePath = contextModule.Path
                .Take(prefixLength)
                .Concat(relativeOrAbsolutePath)
                .ToList();

            if (TryLookupModuleCandidate(contextModule.PackageAlias, candidatePath, seen, out var packageRelativeModuleId))
            {
                yield return packageRelativeModuleId;
            }

            if (TryLookupModuleCandidate(candidatePath, seen, out var relativeModuleId))
            {
                yield return relativeModuleId;
            }
        }
    }

    private bool TryLookupModuleCandidate(
        string? packageAlias,
        IReadOnlyList<string> path,
        ISet<SymbolId> seen,
        out SymbolId moduleId)
    {
        moduleId = SymbolId.None;

        if (string.IsNullOrWhiteSpace(packageAlias))
        {
            return false;
        }

        var candidateId = _moduleRegistry.LookupModuleByPath(packageAlias, path);
        if (!candidateId.HasValue || !candidateId.Value.IsValid || !seen.Add(candidateId.Value))
        {
            return false;
        }

        moduleId = candidateId.Value;
        return true;
    }

    private bool TryLookupModuleCandidate(
        IReadOnlyList<string> path,
        ISet<SymbolId> seen,
        out SymbolId moduleId)
    {
        moduleId = SymbolId.None;

        var candidateId = _moduleRegistry.LookupModuleByPath(path);
        if (!candidateId.HasValue || !candidateId.Value.IsValid || !seen.Add(candidateId.Value))
        {
            return false;
        }

        moduleId = candidateId.Value;
        return true;
    }

    private string FormatResolvedSymbolName(PathResolutionResult result)
    {
        if (!result.SymbolId.IsValid || _symbolTable.GetSymbol(result.SymbolId) is not { } symbol)
        {
            return string.Empty;
        }

        if (_moduleRegistry.TryGetOwningModule(result.SymbolId, out var module))
        {
            return $"{ModuleRegistry.FormatModuleFullName(module)}{WellKnownStrings.Separators.Path}{symbol.Name}";
        }

        return symbol.Name;
    }

    /// <summary>
    /// 在模块中查找成员
    /// </summary>
    private PathResolutionResult? LookupModuleMember(
        SymbolId moduleId,
        string memberName,
        SymbolId? requesterModuleId)
    {
        if (_moduleRegistry.TryLookupAccessibleBinding(moduleId, memberName, requesterModuleId, out var binding))
        {
            return PathResolutionResult.Found(binding.SymbolId, binding.Kind);
        }

        return null;
    }

    /// <summary>
    /// 查找类型的构造器
    /// </summary>
    private PathResolutionResult? LookupTypeConstructor(SymbolId typeId, string ctorName)
    {
        var adt = _symbolTable.GetSymbol<AdtSymbol>(typeId);
        if (adt == null)
            return null;

        foreach (var ctorId in adt.Constructors)
        {
            var ctor = _symbolTable.GetSymbol<CtorSymbol>(ctorId);
            if (ctor?.Name == ctorName)
            {
                return PathResolutionResult.Found(ctorId, ResolutionKind.Constructor);
            }
        }

        return null;
    }

    private PathResolutionResult? LookupTraitMethod(TraitSymbol trait, string methodName)
    {
        foreach (var methodId in trait.Methods)
        {
            if (_symbolTable.GetSymbol(methodId) is FuncSymbol method &&
                string.Equals(method.Name, methodName, StringComparison.Ordinal))
            {
                return PathResolutionResult.Found(methodId, ResolutionKind.Value);
            }
        }

        return null;
    }

    private PathResolutionResult? LookupTraitMemberInModule(
        SymbolId moduleId,
        string memberName,
        SymbolId? requesterModuleId)
    {
        var matches = new List<PathResolutionResult>();
        foreach (var binding in _moduleRegistry.GetAccessibleBindings(moduleId, requesterModuleId))
        {
            if (_symbolTable.GetSymbol(binding.SymbolId) is not TraitSymbol trait ||
                LookupTraitMethod(trait, memberName) is not { IsSuccess: true } candidate)
            {
                continue;
            }

            if (!matches.Any(match => match.SymbolId == candidate.SymbolId))
            {
                matches.Add(candidate);
            }
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    private PathResolutionResult? LookupSameNamedEffectMember(
        SymbolId moduleId,
        string memberName,
        SymbolId? requesterModuleId)
    {
        var module = _moduleRegistry.GetModule(moduleId);
        if (module == null || module.Path.Count == 0)
        {
            return null;
        }

        var ownerName = module.Path[^1];
        foreach (var binding in _moduleRegistry.GetAccessibleBindingsByName(
                     moduleId,
                     ownerName,
                     requesterModuleId,
                     EffectOwnerResolutionKinds))
        {
            _ = _symbolTable.GetSymbol(binding.SymbolId);
        }

        return null;
    }

    /// <summary>
    /// 根据符号类型获取解析类型
    /// </summary>
    private static ResolutionKind GetResolutionKind(Symbol symbol)
    {
        return symbol switch
        {
            FuncSymbol => ResolutionKind.Value,
            VarSymbol => ResolutionKind.Value,
            AdtSymbol => ResolutionKind.Type,
            CtorSymbol => ResolutionKind.Constructor,
            TraitSymbol => ResolutionKind.Type,
            AssociatedTypeSymbol => ResolutionKind.Type,
            AssociatedConstSymbol => ResolutionKind.Value,
            EffectSymbol => ResolutionKind.Effect,
            ModuleSymbol => ResolutionKind.Module,
            // ProofSymbol removed
            _ => ResolutionKind.Value
        };
    }

    private static readonly HashSet<ResolutionKind> EffectOwnerResolutionKinds =
    [
        ResolutionKind.Effect
    ];
}
