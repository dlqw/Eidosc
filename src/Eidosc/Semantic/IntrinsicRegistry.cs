using Eidosc.Ast.Declarations;
using Eidosc.Ast.Types;
using Eidosc.Symbols;
using Eidosc.Utils;

namespace Eidosc.Semantic;

public sealed record IntrinsicDeclaration(
    string Name,
    string ModulePath,
    string FunctionName,
    TypeNode? Signature,
    string SignatureKey,
    string? LlvmAbi,
    IReadOnlyList<string> Effects,
    BuiltinIntrinsicRole Role);

public static class IntrinsicRegistry
{
    private static readonly Lazy<IntrinsicRegistryData> EmbeddedStdlibIntrinsics =
        new(LoadEmbeddedStdlibIntrinsics, isThreadSafe: true);

    public static IReadOnlyDictionary<string, IntrinsicDeclaration> EmbeddedStdlibDeclarations =>
        EmbeddedStdlibIntrinsics.Value.FirstByName;

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, IntrinsicDeclaration>> EmbeddedStdlibOverloadDeclarations =>
        EmbeddedStdlibIntrinsics.Value.ByNameAndSignature;

    public static bool IsKnownIntrinsicName(string? name)
    {
        return !string.IsNullOrWhiteSpace(name) &&
               (EmbeddedStdlibIntrinsics.Value.FirstByName.ContainsKey(name) ||
                IsMathIntrinsicName(name));
    }

    public static bool TryGet(string name, out IntrinsicDeclaration declaration)
    {
        return EmbeddedStdlibIntrinsics.Value.FirstByName.TryGetValue(name, out declaration!);
    }

    public static bool TryGet(string name, string signatureKey, out IntrinsicDeclaration declaration)
    {
        declaration = null!;
        return EmbeddedStdlibIntrinsics.Value.ByNameAndSignature.TryGetValue(name, out var overloads) &&
               overloads.TryGetValue(signatureKey, out declaration!);
    }

    public static IReadOnlyList<IntrinsicDeclaration> GetOverloads(string name)
    {
        return EmbeddedStdlibIntrinsics.Value.ByNameAndSignature.TryGetValue(name, out var overloads)
            ? overloads.Values.ToList()
            : [];
    }

    public static BuiltinIntrinsicRole GetRole(string intrinsicName)
    {
        return intrinsicName switch
        {
            WellKnownStrings.InternalNames.ValueBox => BuiltinIntrinsicRole.ValueBox,
            WellKnownStrings.InternalNames.ValueUnbox => BuiltinIntrinsicRole.ValueUnbox,
            WellKnownStrings.InternalNames.ValueBoxFree => BuiltinIntrinsicRole.ValueBoxFree,
            WellKnownStrings.InternalNames.SharedNew => BuiltinIntrinsicRole.SharedNew,
            WellKnownStrings.InternalNames.SharedBorrow => BuiltinIntrinsicRole.SharedBorrow,
            WellKnownStrings.InternalNames.SharedClone => BuiltinIntrinsicRole.SharedClone,
            WellKnownStrings.InternalNames.SharedPtrEq => BuiltinIntrinsicRole.SharedPtrEq,
            _ => BuiltinIntrinsicRole.None
        };
    }

    public static bool IsMathIntrinsicName(string name) => name.StartsWith("math_", StringComparison.Ordinal);

    public static bool IsPointerLoadIntrinsicName(string name) => name.StartsWith("ptr_load_", StringComparison.Ordinal);

    public static bool IsPointerStoreIntrinsicName(string name) => name.StartsWith("ptr_store_", StringComparison.Ordinal);

    private static IntrinsicRegistryData LoadEmbeddedStdlibIntrinsics()
    {
        var firstByName = new Dictionary<string, IntrinsicDeclaration>(StringComparer.Ordinal);
        var byNameAndSignature = new Dictionary<string, Dictionary<string, IntrinsicDeclaration>>(StringComparer.Ordinal);
        foreach (var modulePath in PrecompiledModuleRegistry.GetAvailableModulePaths())
        {
            if (!PrecompiledModuleRegistry.TryGetSource(modulePath, out var source) ||
                !PrecompiledModuleRegistry.TryParseModuleDeclForTest(source, modulePath, out var moduleDecl) ||
                moduleDecl == null)
            {
                continue;
            }

            CollectModuleIntrinsics(moduleDecl, modulePath, firstByName, byNameAndSignature);
        }

        return new IntrinsicRegistryData(
            firstByName,
            byNameAndSignature.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyDictionary<string, IntrinsicDeclaration>)pair.Value,
                StringComparer.Ordinal));
    }

    private static void CollectModuleIntrinsics(
        ModuleDecl module,
        string modulePath,
        Dictionary<string, IntrinsicDeclaration> firstByName,
        Dictionary<string, Dictionary<string, IntrinsicDeclaration>> byNameAndSignature)
    {
        foreach (var declaration in module.Declarations)
        {
            switch (declaration)
            {
                case ModuleDecl nested:
                    CollectModuleIntrinsics(nested, modulePath, firstByName, byNameAndSignature);
                    break;
                case FuncDecl funcDecl:
                    AddIntrinsic(funcDecl.Clauses, modulePath, funcDecl.Name, funcDecl.Signature.FirstOrDefault(), funcDecl.TypeParams, firstByName, byNameAndSignature);
                    break;
                case FuncDef funcDef:
                    AddIntrinsic(funcDef.Clauses, modulePath, funcDef.Name, funcDef.Signature.FirstOrDefault(), funcDef.TypeParams, firstByName, byNameAndSignature);
                    break;
            }
        }
    }

    private static void AddIntrinsic(
        IReadOnlyList<DeclarationClause> clauses,
        string modulePath,
        string functionName,
        TypeNode? signature,
        IReadOnlyList<TypeParam> typeParams,
        Dictionary<string, IntrinsicDeclaration> firstByName,
        Dictionary<string, Dictionary<string, IntrinsicDeclaration>> byNameAndSignature)
    {
        var intrinsicName = GetClauseArgument(clauses, DeclarationClauseKind.Intrinsic) ?? functionName;
        if (!clauses.Any(static clause => clause.ClauseKind == DeclarationClauseKind.Intrinsic) ||
            string.IsNullOrWhiteSpace(intrinsicName))
        {
            return;
        }

        var effects = clauses
            .Where(static clause => clause.ClauseKind == DeclarationClauseKind.Need)
            .SelectMany(static clause => clause.ArgumentTokens)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var llvmAbi = GetClauseArgument(clauses, DeclarationClauseKind.LlvmAbi);
        var declaration = new IntrinsicDeclaration(
            intrinsicName,
            modulePath,
            functionName,
            signature,
            BuildSignatureKey(signature, typeParams),
            llvmAbi,
            effects,
            GetRole(intrinsicName));

        firstByName.TryAdd(intrinsicName, declaration);

        if (!byNameAndSignature.TryGetValue(intrinsicName, out var overloads))
        {
            overloads = new Dictionary<string, IntrinsicDeclaration>(StringComparer.Ordinal);
            byNameAndSignature[intrinsicName] = overloads;
        }

        overloads.TryAdd(declaration.SignatureKey, declaration);
    }

    private static string BuildSignatureKey(TypeNode? signature, IReadOnlyList<TypeParam> typeParams)
    {
        var arity = typeParams.Count;
        var signatureText = signature == null ? "<unknown>" : RenderTypeNode(signature);
        return $"arity={arity};{signatureText}";
    }

    private static string RenderTypeNode(TypeNode node)
    {
        return node switch
        {
            ArrowType arrow => $"{RenderTypeNode(arrow.ParamType)}->{RenderTypeNode(arrow.ReturnType)}",
            TupleType tuple => $"({string.Join(",", tuple.Elements.Select(RenderTypeNode))})",
            TypePath path => RenderTypePath(path),
            EffectfulType effectful => RenderEffectfulType(effectful),
            AssociatedTypeProjection projection => $"{(projection.Target == null ? "<unknown>" : RenderTypeNode(projection.Target))}.{projection.MemberName}",
            WildcardType => "_",
            _ => node.GetType().Name
        };
    }

    private static string RenderTypePath(TypePath path)
    {
        var parts = path.ToQualifiedPathParts();
        var name = parts.Count == 0
            ? path.TypeName
            : string.Join(".", parts);
        if (path.TypeArgs.Count == 0)
        {
            return name;
        }

        return $"{name}[{string.Join(",", path.TypeArgs.Select(RenderTypeNode))}]";
    }

    private static string RenderEffectfulType(EffectfulType effectful)
    {
        var input = RenderTypeNode(effectful.InputType);
        var effects = string.Join(
            "|",
            effectful.EnumerateEffectPaths()
                .Select(path => string.Join(".", path)));
        var output = effectful.OutputType == null ? "" : $"->{RenderTypeNode(effectful.OutputType)}";
        return $"{input}->{{{effects}}}{output}";
    }

    private static string? GetClauseArgument(IReadOnlyList<DeclarationClause> clauses, DeclarationClauseKind kind)
    {
        foreach (var clause in clauses)
        {
            if (clause.ClauseKind == kind)
            {
                return NormalizeClauseArgumentText(clause.ArgumentTokens.FirstOrDefault());
            }
        }

        return null;
    }

    private static string? NormalizeClauseArgumentText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (trimmed.Length >= 2 &&
            ((trimmed[0] == '"' && trimmed[^1] == '"') ||
             (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            return trimmed[1..^1];
        }

        return trimmed;
    }

    private sealed record IntrinsicRegistryData(
        IReadOnlyDictionary<string, IntrinsicDeclaration> FirstByName,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IntrinsicDeclaration>> ByNameAndSignature);
}
