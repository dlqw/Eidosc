using Eidosc.Symbols;
using Eidosc.Semantic;
using Eidosc.Utils;

namespace Eidosc.Mir;

internal static class MirBuiltinFunctions
{
    private const string BuiltinModule = "builtin";

    public static bool IsKnownIntrinsicName(string? name)
    {
        return IntrinsicRegistry.IsKnownIntrinsicName(name);
    }

    public static bool IsMathIntrinsicName(string name) => IntrinsicRegistry.IsMathIntrinsicName(name);

    public static bool IsPointerLoadIntrinsicName(string name) => IntrinsicRegistry.IsPointerLoadIntrinsicName(name);

    public static bool IsPointerStoreIntrinsicName(string name) => IntrinsicRegistry.IsPointerStoreIntrinsicName(name);

    public static FunctionId CreateFunctionId(SymbolId symbolId, string name)
    {
        return new FunctionId
        {
            SymbolId = symbolId,
            Kind = SymbolKind.Function,
            Name = name,
            Module = BuiltinModule,
            QualifiedName = BuildQualifiedName(name)
        };
    }

    public static FunctionId CreateIntrinsicFunctionId(SymbolId symbolId, string intrinsicName)
    {
        return new FunctionId
        {
            SymbolId = symbolId,
            Kind = SymbolKind.Function,
            Name = intrinsicName,
            Module = BuiltinModule,
            QualifiedName = BuildQualifiedName(intrinsicName)
        };
    }

    public static bool TryGetIntrinsicName(MirFunctionRef functionRef, out string name)
    {
        name = string.Empty;
        if (TryGetFunctionName(functionRef, out name))
        {
            if (IsKnownIntrinsicName(name))
            {
                return true;
            }

            if (TryNormalizeSpecializedIntrinsicName(name, out var normalized))
            {
                name = normalized;
                return true;
            }
        }

        if (TryNormalizeSpecializedIntrinsicName(functionRef.Name, out var fallback))
        {
            name = fallback;
            return true;
        }

        return false;
    }

    public static bool TryGetFunctionName(MirFunctionRef functionRef, out string name)
    {
        name = string.Empty;
        if (!HasBuiltinIdentity(functionRef.FunctionId))
        {
            return false;
        }

        name = functionRef.FunctionId.Name;
        return !string.IsNullOrWhiteSpace(name);
    }

    private static bool HasBuiltinIdentity(FunctionId? functionId)
    {
        return functionId is { SymbolId.IsValid: true } &&
               string.Equals(functionId.Module, BuiltinModule, StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(functionId.QualifiedName) &&
               functionId.QualifiedName.StartsWith($"{BuiltinModule}:", StringComparison.Ordinal);
    }

    private static string BuildQualifiedName(string name) => $"{BuiltinModule}:{name}";

    private static bool TryNormalizeSpecializedIntrinsicName(string name, out string intrinsicName)
    {
        intrinsicName = string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var specIndex = name.IndexOf("__spec", StringComparison.Ordinal);
        if (specIndex > 0)
        {
            var candidate = name[..specIndex];
            if (IsKnownIntrinsicName(candidate))
            {
                intrinsicName = candidate;
                return true;
            }
        }

        var monomorphicIndex = name.IndexOf("_i", StringComparison.Ordinal);
        if (monomorphicIndex > 0)
        {
            var candidate = name[..monomorphicIndex];
            if (IsKnownIntrinsicName(candidate))
            {
                intrinsicName = candidate;
                return true;
            }
        }

        return false;
    }
}
