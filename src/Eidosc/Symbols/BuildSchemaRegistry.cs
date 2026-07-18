using Eidosc.Types;
using Eidosc.Utils;
using EidosType = Eidosc.Types.Type;

namespace Eidosc.Symbols;

internal static class BuildSchemaRegistry
{
    public const string IntrinsicPrefix = "build.";

    private sealed record BuildTypeSpec(string Name, int TypeId);
    private sealed record BuildFunctionSpec(string Name, int Arity);

    private static readonly BuildTypeSpec[] s_types =
    [
        new(WellKnownStrings.Build.Types.Session, WellKnownTypeIds.BuildSessionId),
        new(WellKnownStrings.Build.Types.Fs, WellKnownTypeIds.BuildFsId),
        new(WellKnownStrings.Build.Types.Env, WellKnownTypeIds.BuildEnvId),
        new(WellKnownStrings.Build.Types.Process, WellKnownTypeIds.BuildProcessId),
        new(WellKnownStrings.Build.Types.Emit, WellKnownTypeIds.BuildEmitId),
        new(WellKnownStrings.Build.Types.Network, WellKnownTypeIds.BuildNetworkId),
        new(WellKnownStrings.Build.Types.Sha256, WellKnownTypeIds.BuildSha256Id),
        new(WellKnownStrings.Build.Types.Graph, WellKnownTypeIds.BuildGraphId),
        new(WellKnownStrings.Build.Types.Step, WellKnownTypeIds.BuildStepId),
        new(WellKnownStrings.Build.Types.Artifact, WellKnownTypeIds.BuildArtifactId)
    ];

    private static readonly BuildFunctionSpec[] s_functions =
    [
        new("session", 0),
        new("fs", 1),
        new("env", 1),
        new("process", 1),
        new("emit", 1),
        new("network", 1),
        new("host", 1),
        new("target", 1),
        new("read_text", 2),
        new("environment", 2),
        new("command", 7),
        new("generated_source", 4),
        new("generated_module", 4),
        new("artifact", 5),
        new("content_addressed_artifact", 6),
        new("fetch", 3),
        new("graph", 3)
    ];

    public static void Register(SymbolTable symbolTable)
    {
        if (symbolTable.Modules.LookupRootModule(WellKnownStrings.Build.Module) is { IsValid: true })
        {
            return;
        }

        var moduleId = symbolTable.DeclareModule(
            WellKnownStrings.Build.Module,
            [WellKnownStrings.Build.Module],
            SourceSpan.Empty,
            isPublic: true);

        var sha256TypeId = SymbolId.None;
        foreach (var typeSpec in s_types)
        {
            var typeId = symbolTable.RegisterSymbol(new AdtSymbol
            {
                Name = typeSpec.Name,
                Span = SourceSpan.Empty,
                IsModuleLevel = true,
                IsPublic = true,
                TypeId = new TypeId(typeSpec.TypeId)
            });
            symbolTable.AddMemberToModule(moduleId, typeId);
            if (string.Equals(typeSpec.Name, WellKnownStrings.Build.Types.Sha256, StringComparison.Ordinal))
            {
                sha256TypeId = typeId;
            }
        }

        if (sha256TypeId.IsValid)
        {
            var constructorId = symbolTable.RegisterSymbol(new CtorSymbol
            {
                Name = WellKnownStrings.Build.Types.Sha256,
                Span = SourceSpan.Empty,
                OwnerAdt = sha256TypeId,
                IsPublic = true
            });
            if (symbolTable.GetSymbol<CtorSymbol>(constructorId) is { } constructor)
            {
                constructor.PositionalArgs = [new TypeId(WellKnownTypeIds.StringId)];
                symbolTable.UpdateSymbol(constructor);
            }
            if (symbolTable.GetSymbol<AdtSymbol>(sha256TypeId) is { } sha256Type)
            {
                symbolTable.UpdateSymbol(sha256Type with
                {
                    CaseConstructor = constructorId,
                    Constructors = [constructorId]
                });
            }
            symbolTable.AddMemberToModule(moduleId, constructorId);
        }

        foreach (var functionSpec in s_functions)
        {
            var functionId = symbolTable.RegisterSymbol(new FuncSymbol
            {
                Name = functionSpec.Name,
                Span = SourceSpan.Empty,
                IsModuleLevel = true,
                IsPublic = true,
                IsComptime = true,
                HasBody = false,
                Parameters = Enumerable.Repeat(SymbolId.None, functionSpec.Arity).ToList(),
                IntrinsicName = IntrinsicPrefix + functionSpec.Name
            });
            symbolTable.AddMemberToModule(moduleId, functionId);
        }
    }

    public static bool IsBuildIntrinsic(FuncSymbol symbol, out string name)
    {
        name = string.Empty;
        if (string.IsNullOrWhiteSpace(symbol.IntrinsicName) ||
            !symbol.IntrinsicName.StartsWith(IntrinsicPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        name = symbol.IntrinsicName[IntrinsicPrefix.Length..];
        return true;
    }

    public static EidosType CreateFunctionType(
        FuncSymbol symbol,
        Substitution substitution,
        SymbolTable symbolTable)
    {
        if (!IsBuildIntrinsic(symbol, out var name))
        {
            throw new ArgumentException("Function is not a Build intrinsic.", nameof(symbol));
        }

        var session = BuildType(WellKnownStrings.Build.Types.Session, WellKnownTypeIds.BuildSessionId);
        var fs = BuildType(WellKnownStrings.Build.Types.Fs, WellKnownTypeIds.BuildFsId);
        var env = BuildType(WellKnownStrings.Build.Types.Env, WellKnownTypeIds.BuildEnvId);
        var process = BuildType(WellKnownStrings.Build.Types.Process, WellKnownTypeIds.BuildProcessId);
        var emit = BuildType(WellKnownStrings.Build.Types.Emit, WellKnownTypeIds.BuildEmitId);
        var network = BuildType(WellKnownStrings.Build.Types.Network, WellKnownTypeIds.BuildNetworkId);
        var sha256 = BuildType(WellKnownStrings.Build.Types.Sha256, WellKnownTypeIds.BuildSha256Id);
        var graph = BuildType(WellKnownStrings.Build.Types.Graph, WellKnownTypeIds.BuildGraphId);
        var step = BuildType(WellKnownStrings.Build.Types.Step, WellKnownTypeIds.BuildStepId);
        var artifact = BuildType(WellKnownStrings.Build.Types.Artifact, WellKnownTypeIds.BuildArtifactId);
        var strings = ListOf(symbolTable, BaseTypes.String);
        var itemSyntax = MetaSchemaRegistry.MetaType(
            WellKnownStrings.Meta.Types.Syntax,
            WellKnownTypeIds.MetaSyntaxId) with
        {
            Args =
            [
                MetaSchemaRegistry.MetaType(
                    WellKnownStrings.Meta.Types.Item,
                    WellKnownTypeIds.MetaItemId)
            ]
        };

        var parameters = name switch
        {
            "session" => [],
            "fs" or "env" or "process" or "emit" or "network" or "host" or "target" => [session],
            "read_text" => [fs, BaseTypes.String],
            "environment" => [env, BaseTypes.String],
            "command" => [process, BaseTypes.String, BaseTypes.String, strings, strings, strings, strings],
            "generated_source" => [emit, BaseTypes.String, BaseTypes.String, BaseTypes.String],
            "generated_module" => [emit, BaseTypes.String, ListOf(symbolTable, itemSyntax), BaseTypes.String],
            "artifact" => [emit, BaseTypes.String, BaseTypes.String, BaseTypes.String, BaseTypes.String],
            "content_addressed_artifact" =>
                [emit, BaseTypes.String, BaseTypes.String, BaseTypes.String, BaseTypes.String, sha256],
            "fetch" => [network, BaseTypes.String, sha256],
            "graph" => [emit, ListOf(symbolTable, step), ListOf(symbolTable, artifact)],
            _ => Enumerable.Repeat<EidosType>(substitution.FreshTypeVariable(), symbol.Parameters.Count).ToList()
        };

        EidosType result = name switch
        {
            "session" => session,
            "fs" => fs,
            "env" => env,
            "process" => process,
            "emit" => emit,
            "network" => network,
            "host" or "target" or "read_text" or "environment" => BaseTypes.String,
            "command" => step,
            "generated_source" or "generated_module" or "artifact" or
                "content_addressed_artifact" or "fetch" => artifact,
            "graph" => graph,
            _ => substitution.FreshTypeVariable()
        };

        return new TyFun { Params = parameters, Result = result };
    }

    public static TyCon BuildType(string name, int typeId) => new()
    {
        Name = name,
        Id = new TypeId(typeId)
    };

    private static TyCon ListOf(SymbolTable symbolTable, EidosType elementType)
    {
        var symbol = symbolTable.LookupType(WellKnownStrings.BuiltinTypes.Seq) ?? SymbolId.None;
        return new TyCon
        {
            Name = WellKnownStrings.BuiltinTypes.Seq,
            Symbol = symbol,
            Args = [elementType]
        };
    }
}
