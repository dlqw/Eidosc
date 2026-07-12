using Eidosc.Hir;
using Eidosc.Mir;
using Eidosc.Types;

namespace Eidosc.Pipeline;

public sealed record ModuleHirStateArtifactPayload(
    string SchemaVersion,
    string ModuleKey,
    ProjectModuleTypedSemanticNode TypedSemantic,
    bool IsModuleLocal,
    int ModuleLocalDeclarationCount,
    ModuleHirStatePayload HirState,
    string PayloadHash)
{
    public const string CurrentSchemaVersion = "module-hir-state-artifact-payload-v1";

    public static ModuleHirStateArtifactPayload Create(
        string moduleKey,
        ProjectModuleTypedSemanticSnapshot typedSemanticSnapshot,
        HirModule? hirModule,
        ParameterEffectMap? parameterEffects,
        IReadOnlySet<TypeId>? copyLikeTypeIds,
        IReadOnlyDictionary<TypeId, string>? dynamicTypeKeys,
        IReadOnlyDictionary<int, TypeDescriptor>? typeDescriptors,
        IReadOnlyDictionary<int, List<ConstructorTypeLayout>>? constructorLayouts)
    {
        var typedSemantic = typedSemanticSnapshot.Nodes.FirstOrDefault(node =>
            string.Equals(node.ModuleKey, moduleKey, StringComparison.Ordinal));
        if (typedSemantic == null)
        {
            throw new ArgumentException($"Module '{moduleKey}' is missing from the typed semantic snapshot.", nameof(moduleKey));
        }

        var moduleLocal = TryCreateModuleLocalHirModule(
            hirModule,
            typedSemantic,
            out var moduleSlice);
        var hirState = ModuleHirStatePayload.Create(
            moduleSlice,
            parameterEffects,
            copyLikeTypeIds,
            dynamicTypeKeys,
            typeDescriptors,
            constructorLayouts);
        var payload = new ModuleHirStateArtifactPayload(
            CurrentSchemaVersion,
            moduleKey,
            typedSemantic,
            moduleLocal,
            moduleSlice?.Declarations.Count ?? 0,
            hirState,
            "");
        return payload with { PayloadHash = ComputeHash(payload) };
    }

    public bool HasValidPayloadHash() =>
        !string.IsNullOrWhiteSpace(PayloadHash) &&
        string.Equals(PayloadHash, ComputeHash(this), StringComparison.Ordinal) &&
        HirState.HasValidHash();

    private static string ComputeHash(ModuleHirStateArtifactPayload payload) =>
        ModuleArtifactHash.ComputeJsonHash(payload with { PayloadHash = "" });

    private static bool TryCreateModuleLocalHirModule(
        HirModule? module,
        ProjectModuleTypedSemanticNode typedSemantic,
        out HirModule? moduleSlice)
    {
        moduleSlice = module;
        if (module == null)
        {
            return false;
        }

        var keys = typedSemantic.Declarations
            .SelectMany(CreateDeclarationKeys)
            .ToHashSet(StringComparer.Ordinal);
        if (keys.Count == 0)
        {
            return false;
        }

        var declarations = module.Declarations
            .Where(declaration => CreateHirDeclarationKeys(declaration).Any(keys.Contains))
            .ToList();
        if (declarations.Count == 0)
        {
            return false;
        }

        var declarationSymbols = declarations
            .Select(static declaration => declaration.SymbolId)
            .Where(static id => id.IsValid)
            .Select(static id => id.Value)
            .ToHashSet();
        moduleSlice = module with
        {
            Declarations = declarations,
            Exports = module.Exports
                .Where(id => id.IsValid && declarationSymbols.Contains(id.Value))
                .ToList()
        };
        return true;
    }

    private static IEnumerable<string> CreateDeclarationKeys(ProjectModuleTypedSemanticDeclaration declaration)
    {
        yield return $"{declaration.Kind}:{declaration.Name}";
        if (TryGetCanonicalNameTail(declaration.CanonicalName, out var tail))
        {
            yield return tail;
        }
    }

    private static IEnumerable<string> CreateHirDeclarationKeys(HirDecl declaration)
    {
        var kind = declaration switch
        {
            HirFunc => "Function",
            HirVal => "Variable",
            HirVarDecl => "Variable",
            HirAdt => "ADT",
            HirEffect => "Effect",
            HirTrait => "Trait",
            HirImpl => "Impl",
            HirTypeAlias => "TypeAlias",
            _ => declaration.GetType().Name
        };

        yield return $"{kind}:{declaration.Name}";
        if (declaration is HirFunc { SourceName.Length: > 0 } function)
        {
            yield return $"{kind}:{function.SourceName}";
        }
    }

    private static bool TryGetCanonicalNameTail(string canonicalName, out string tail)
    {
        tail = "";
        var separatorIndex = canonicalName.LastIndexOf("::", StringComparison.Ordinal);
        if (separatorIndex < 0 || separatorIndex + 2 >= canonicalName.Length)
        {
            return false;
        }

        tail = canonicalName[(separatorIndex + 2)..];
        return tail.Contains(':', StringComparison.Ordinal);
    }
}
