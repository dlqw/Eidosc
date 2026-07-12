using Eidosc.Symbols;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Mir;

internal static class MirRuntimeFunctions
{
    private const string RuntimeModule = "runtime";

    public static FunctionId CreateFunctionId(string name)
    {
        return new FunctionId
        {
            SymbolId = SymbolId.None,
            Kind = SymbolKind.Function,
            Name = name,
            Module = RuntimeModule,
            QualifiedName = BuildQualifiedName(name)
        };
    }

    public static MirFunctionRef CreateFunctionRef(string name, TypeId typeId, SourceSpan span)
    {
        return new MirFunctionRef
        {
            Name = name,
            SymbolId = SymbolId.None,
            FunctionId = CreateFunctionId(name),
            TypeId = typeId,
            Span = span
        };
    }

    public static bool HasIdentity(MirFunctionRef functionRef, string name)
    {
        return HasIdentity(functionRef.FunctionId, name);
    }

    public static bool TryGetFunctionName(MirFunctionRef functionRef, out string name)
    {
        name = string.Empty;
        if (!HasRuntimeIdentity(functionRef.FunctionId))
        {
            return false;
        }

        name = functionRef.FunctionId.Name;
        return !string.IsNullOrWhiteSpace(name);
    }

    public static bool HasIdentity(FunctionId? functionId, string name)
    {
        return functionId is { SymbolId.IsValid: false } &&
               string.Equals(functionId.QualifiedName, BuildQualifiedName(name), StringComparison.Ordinal);
    }

    public static bool HasRuntimeIdentity(FunctionId? functionId)
    {
        return functionId is { SymbolId.IsValid: false } &&
               string.Equals(functionId.Module, RuntimeModule, StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(functionId.QualifiedName) &&
               functionId.QualifiedName.StartsWith($"{RuntimeModule}:", StringComparison.Ordinal);
    }

    private static string BuildQualifiedName(string name) => $"{RuntimeModule}:{name}";
}
