using Eidosc.Hir;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Xunit;

namespace Eidosc.Tests.Unit.Pipeline;

public sealed class ModuleHirMixedRestoreTests
{
    [Fact]
    public void Run_BodyOnlySourceChange_CompilesChangedHirAndRestoresUnaffectedModules()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_module_hir_mixed_{Guid.NewGuid():N}");
        try
        {
            var entryFile = WriteProject(tempDir);
            var source = File.ReadAllText(entryFile);
            var first = RunHir(entryFile, source, static _ => { });

            Assert.True(first.Success, FormatDiagnostics(first));
            var hirPayloads = Assert.IsAssignableFrom<IReadOnlyList<ModuleHirStateArtifactPayload>>(
                first.ModuleHirStatePayloads);
            var hirPayloadByModule = hirPayloads.ToDictionary(static payload => payload.ModuleKey, StringComparer.Ordinal);
            var typesPayloads = Assert.IsAssignableFrom<IReadOnlyList<ModuleTypesStatePayload>>(
                first.ModuleTypesStatePayloads);
            var typesPayloadByModule = typesPayloads.ToDictionary(static payload => payload.ModuleKey, StringComparer.Ordinal);
            File.WriteAllText(Path.Combine(tempDir, "lib_a.eidos"), """
LibA :: module {
    id :: Int -> Int
    {
        value => value + 2
    }
}
""");

            var expected = RunHir(entryFile, source, static _ => { });
            var second = RunHir(entryFile, source, options =>
            {
                options.MaxDegreeOfParallelism = 4;
                options.PreviousModuleSemanticSignatureSnapshot = first.ModuleSemanticSignatureSnapshot;
                options.PreviousModuleTypedSemanticSnapshot = first.ModuleTypedSemanticSnapshot;
                options.PreviousModuleMemberIndexSnapshot = first.ModuleMemberIndexSnapshot;
                options.PreviousModuleDependencySignatureSnapshot = first.ModuleDependencySignatureSnapshot;
                options.PreviousModuleNamerStatePayloads = first.ModuleNamerStatePayloads;
                options.PreviousModuleTypesStatePayloads = typesPayloads;
                options.PreviousModuleHirStatePayloads = hirPayloads;
                options.ModuleArtifactAvailability = (moduleKey, kind, _, _) => kind switch
                {
                    ProjectModuleArtifactKinds.SemanticSignature => true,
                    ProjectModuleArtifactKinds.TypedSemanticSignature => true,
                    ProjectModuleArtifactKinds.TypesStatePayload => typesPayloadByModule.ContainsKey(moduleKey),
                    ProjectModuleArtifactKinds.HirStatePayload => hirPayloadByModule.ContainsKey(moduleKey),
                    _ => false
                };
                options.ModuleTypesStatePayloadLoader = (moduleKey, kind, _, _) =>
                    kind == ProjectModuleArtifactKinds.TypesStatePayload &&
                    typesPayloadByModule.TryGetValue(moduleKey, out var payload)
                        ? payload
                        : null;
                options.ModuleHirStatePayloadLoader = (moduleKey, kind, _, _) =>
                    kind == ProjectModuleArtifactKinds.HirStatePayload &&
                    hirPayloadByModule.TryGetValue(moduleKey, out var payload)
                        ? payload
                        : null;
            });

            Assert.True(expected.Success, FormatDiagnostics(expected));
            Assert.True(second.Success, FormatDiagnostics(second));
            Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault("Hir.moduleRestore.applied"));
            Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault("Hir.moduleRestore.fallbackBuildHir"));
            Assert.Equal(2, second.ProfilingCounters.GetValueOrDefault("Build.moduleStage.Hir.compiledModules"));
            Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault("Build.moduleStage.Hir.restoredModules"));
            Assert.True(second.ProfilingCounters.GetValueOrDefault("Build.moduleStage.Hir.maxObservedParallelism") > 1);
            Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault("Hir.build_hir.calls"));
            Assert.Equal(
                HirFormatter.FormatHir(Assert.IsType<HirModule>(expected.HirModule)),
                HirFormatter.FormatHir(Assert.IsType<HirModule>(second.HirModule)));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static string WriteProject(string tempDir)
    {
        Directory.CreateDirectory(tempDir);
        var entryFile = Path.Combine(tempDir, "Main.eidos");
        File.WriteAllText(entryFile, """
Main :: module {
    import LibA
    import LibB

    main :: Int -> Int
    {
        value => LibB.inc(LibA.id(value))
    }
}
""");
        File.WriteAllText(Path.Combine(tempDir, "lib_a.eidos"), """
LibA :: module {
    id :: Int -> Int
    {
        value => value
    }
}
""");
        File.WriteAllText(Path.Combine(tempDir, "lib_b.eidos"), """
LibB :: module {
    inc :: Int -> Int
    {
        value => value + 1
    }
}
""");
        return entryFile;
    }

    private static CompilationResult RunHir(
        string entryFile,
        string source,
        Action<CompilationOptions> configure)
    {
        var options = new CompilationOptions
        {
            InputFile = entryFile,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Hir,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            EnableIncrementalCompilation = true,
            UseColors = false
        };
        configure(options);
        return new CompilationPipeline(source, options).Run();
    }

    private static string FormatDiagnostics(CompilationResult result) =>
        string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
}
