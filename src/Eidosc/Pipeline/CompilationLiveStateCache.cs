using Eidosc.Hir;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Symbols;
using Eidosc.Types;
using Eidosc.Ast.Declarations;

namespace Eidosc.Pipeline;

internal sealed record CompilationLiveStateCacheKey(
    string SourceHash,
    string InputFile,
    string LanguageVersion,
    string FlagsHash,
    CompilationPhase Phase);

internal sealed record CompilationLiveStateSnapshot(
    ModuleDecl? Ast,
    SymbolTable? SymbolTable,
    NameResolver? NameResolver,
    TypeInferer? TypeInferer,
    EffectInferer? EffectInferer,
    HirModule? HirModule,
    MirModule? MirModule,
    MirModule? BorrowMirModule,
    Mir.ParameterEffectMap? HirParameterEffects,
    IReadOnlySet<TypeId> HirCopyLikeTypeIds,
    IReadOnlyDictionary<TypeId, string> HirDynamicTypeKeys,
    IReadOnlyDictionary<int, TypeDescriptor> HirTypeDescriptors,
    IReadOnlyDictionary<int, List<ConstructorTypeLayout>> HirConstructorLayouts,
    ProjectModuleMemberIndexSnapshot? ModuleMemberIndexSnapshot,
    ImplOverlapCheckSnapshot? ImplOverlapCheckSnapshot,
    TypeDirectedCallableResolutionSnapshot? TypeDirectedCallableResolutionSnapshot,
    AssociatedTypeProjectionSnapshot? AssociatedTypeProjectionSnapshot,
    AssociatedConstProjectionSnapshot? AssociatedConstProjectionSnapshot,
    TraitCheckSnapshot? TraitCheckSnapshot,
    MirFunctionFingerprintSnapshot? MirFunctionFingerprints,
    ProjectModuleMirArtifactSnapshot? ModuleMirArtifactSnapshot);

internal static class CompilationLiveStateCache
{
    private const int MaxEntries = 16;
    private static readonly object Lock = new();
    private static readonly Dictionary<CompilationLiveStateCacheKey, CompilationLiveStateSnapshot> Entries = new();
    private static readonly Queue<CompilationLiveStateCacheKey> Order = new();

    public static bool TryGet(
        CompilationLiveStateCacheKey key,
        out CompilationLiveStateSnapshot snapshot)
    {
        lock (Lock)
        {
            return Entries.TryGetValue(key, out snapshot!);
        }
    }

    public static void Store(
        CompilationLiveStateCacheKey key,
        CompilationLiveStateSnapshot snapshot)
    {
        lock (Lock)
        {
            if (!Entries.ContainsKey(key))
            {
                Order.Enqueue(key);
            }

            Entries[key] = snapshot;
            while (Entries.Count > MaxEntries && Order.TryDequeue(out var oldest))
            {
                Entries.Remove(oldest);
            }
        }
    }
}
