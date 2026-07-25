using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Symbols;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class ModuleAccessibleBindingIndexTests
{
    [Fact]
    public void SelectiveImport_UsesAccessibleBindingNameIndex()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_accessible_binding_index_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "Lib"));
            var entryFile = Path.Combine(tempDir, "Main.eidos");
            var apiFile = Path.Combine(tempDir, "Lib", "Api.eidos");

            File.WriteAllText(apiFile, """
Lib.Api :: module {
    export public_id :: Int -> Int
    {
        x => x
    }
}
""");

            File.WriteAllText(entryFile, """
Main :: module {
    import Lib.Api.{public_id}

    run :: Int -> Int
    {
        x => public_id(x)
    }
}
""");

            var result = new CompilationPipeline(File.ReadAllText(entryFile), new CompilationOptions
            {
                InputFile = entryFile,
                LanguageVersion = EidosLanguageVersions.Current,
                StopAtPhase = CompilationPhase.Namer,
                ImportSearchRoots = [tempDir],
                NoImplicitPrelude = true,
                EnableDetailedProfiling = true,
                UseColors = false
            }).Run();

            Assert.True(result.Success, FormatDiagnostics(result));
            Assert.True(
                result.ProfilingCounters.TryGetValue(
                    "Namer.moduleRegistry.accessibleBindingNameIndex.hits",
                    out var hits),
                FormatCounters(result));
            Assert.True(hits > 0, FormatCounters(result));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ModuleQualifiedTraitMember_UsesAccessibleBindingNameIndexForOwnerLookup()
    {
        var source = """
seq :: module {
    export Seq :: trait {
        map :: Int -> Int
    }
}

Main :: module {
    use :: Int -> Int
    {
        x => seq.Seq.map(x)
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Namer,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            UseColors = false
        }).Run();

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.True(
            result.ProfilingCounters.TryGetValue(
                "Namer.moduleRegistry.accessibleBindingNameIndex.hits",
                out var hits),
            FormatCounters(result));
        Assert.True(hits > 0, FormatCounters(result));
    }

    [Fact]
    public void AddMemberToModule_IndexesOwningModuleWithoutDuplicatingMembers()
    {
        var symbolTable = new SymbolTable();
        var moduleId = symbolTable.DeclareModule(
            "Math",
            ["Math"],
            SourceSpan.Empty,
            packageAlias: "Pkg",
            packageInstanceKey: "pkg@1");
        var functionId = symbolTable.DeclareFunction("scale", SourceSpan.Empty);

        symbolTable.AddMemberToModule(moduleId, functionId);
        symbolTable.AddMemberToModule(moduleId, functionId);

        Assert.True(symbolTable.Modules.TryGetOwningModuleId(functionId, out var ownerModuleId));
        Assert.Equal(moduleId, ownerModuleId);
        Assert.True(symbolTable.Modules.TryGetOwningModule(functionId, out var ownerModule));
        Assert.Equal("Math", ownerModule.Name);
        Assert.Single(symbolTable.Modules.GetModuleMembers(moduleId));
    }

    [Fact]
    public void GetProfilingCounters_ReportsMemberOwnerIndexHitsAndMisses()
    {
        var symbolTable = new SymbolTable();
        var moduleId = symbolTable.DeclareModule("Math", ["Math"], SourceSpan.Empty);
        var functionId = symbolTable.DeclareFunction("scale", SourceSpan.Empty);
        symbolTable.AddMemberToModule(moduleId, functionId);

        Assert.True(symbolTable.Modules.TryGetOwningModuleId(functionId, out _));
        Assert.False(symbolTable.Modules.TryGetOwningModuleId(new SymbolId(999_999), out _));

        var counters = symbolTable.Modules.GetProfilingCounters();
        Assert.True(counters["Namer.moduleRegistry.memberOwnerIndex.entries"] >= 1);
        Assert.True(counters["Namer.moduleRegistry.memberOwnerIndex.hits"] >= 1);
        Assert.True(counters["Namer.moduleRegistry.memberOwnerIndex.misses"] >= 1);
    }

    [Fact]
    public void CompilerInternalBinding_IsVisibleOnlyBetweenVerifiedCompilerOwnedModules()
    {
        var symbolTable = new SymbolTable();
        var provider = symbolTable.DeclareModule(
            "Provider",
            ["Provider"],
            SourceSpan.Empty,
            allowsCompilerInternalAccess: true);
        var peer = symbolTable.DeclareModule(
            "Peer",
            ["Peer"],
            SourceSpan.Empty,
            allowsCompilerInternalAccess: true);
        var user = symbolTable.DeclareModule("User", ["User"], SourceSpan.Empty);
        var internalFunction = symbolTable.DeclareFunction(
            "internal_helper",
            SourceSpan.Empty,
            isPublic: false);
        var ordinaryPrivateFunction = symbolTable.DeclareFunction(
            "private_helper",
            SourceSpan.Empty,
            isPublic: false);

        symbolTable.UpdateSymbol(symbolTable.GetSymbol(internalFunction)! with
        {
            IsCompilerInternal = true,
            DefinitionModuleId = provider
        });
        symbolTable.UpdateSymbol(symbolTable.GetSymbol(ordinaryPrivateFunction)! with
        {
            DefinitionModuleId = provider
        });
        symbolTable.AddMemberToModule(provider, internalFunction);
        symbolTable.AddMemberToModule(provider, ordinaryPrivateFunction);

        Assert.True(symbolTable.Modules.TryLookupAccessibleBinding(
            provider,
            "internal_helper",
            peer,
            out var binding));
        Assert.Equal(internalFunction, binding.SymbolId);
        Assert.False(symbolTable.Modules.TryLookupAccessibleBinding(
            provider,
            "private_helper",
            peer,
            out _));
        Assert.False(symbolTable.Modules.TryLookupAccessibleBinding(
            provider,
            "internal_helper",
            user,
            out _));
        Assert.False(symbolTable.Modules.TryLookupAccessibleBinding(
            provider,
            "internal_helper",
            requesterModuleId: null,
            out _));
    }

    private static string FormatDiagnostics(CompilationResult result)
    {
        return string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(static diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}"));
    }

    private static string FormatCounters(CompilationResult result)
    {
        return string.Join(
            Environment.NewLine,
            result.ProfilingCounters
                .OrderBy(static counter => counter.Key, StringComparer.Ordinal)
                .Select(static counter => $"{counter.Key}={counter.Value}"));
    }
}
