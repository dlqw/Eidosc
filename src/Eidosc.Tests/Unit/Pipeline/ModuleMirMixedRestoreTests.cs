using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Xunit;

namespace Eidosc.Tests.Unit.Pipeline;

public sealed class ModuleMirMixedRestoreTests
{
    [Fact]
    public void Run_LlvmTargetBodyChange_CompilesChangedMirAndRestoresUnaffectedModules()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_module_mir_mixed_{Guid.NewGuid():N}");
        try
        {
            var entryFile = WriteProject(tempDir);
            var source = File.ReadAllText(entryFile);
            var first = RunLlvm(entryFile, source, static _ => { });

            Assert.True(first.Success, FormatDiagnostics(first));
            var mirPayloads = Assert.IsAssignableFrom<IReadOnlyList<ModuleMirStateArtifactPayload>>(
                first.ModuleMirStatePayloads);
            var mirPayloadByModule = mirPayloads.ToDictionary(static payload => payload.ModuleKey, StringComparer.Ordinal);
            var typesPayloads = Assert.IsAssignableFrom<IReadOnlyList<ModuleTypesStatePayload>>(
                first.ModuleTypesStatePayloads);
            var typesPayloadByModule = typesPayloads.ToDictionary(static payload => payload.ModuleKey, StringComparer.Ordinal);
            File.WriteAllText(Path.Combine(tempDir, "LibA.eidos"), """
LibA :: module {
    Box :: type { Box(Int) }

    unbox :: Box -> Int
    {
        Box(value) => value + 2
    }
}
""");

            var expected = RunLlvm(entryFile, source, static _ => { });
            var second = RunLlvm(entryFile, source, options =>
            {
                options.MaxDegreeOfParallelism = 4;
                options.PreviousModuleSemanticSignatureSnapshot = first.ModuleSemanticSignatureSnapshot;
                options.PreviousModuleTypedSemanticSnapshot = first.ModuleTypedSemanticSnapshot;
                options.PreviousModuleMemberIndexSnapshot = first.ModuleMemberIndexSnapshot;
                options.PreviousModuleDependencySignatureSnapshot = first.ModuleDependencySignatureSnapshot;
                options.PreviousModuleNamerStatePayloads = first.ModuleNamerStatePayloads;
                options.PreviousModuleTypesStatePayloads = typesPayloads;
                options.PreviousModuleMirStatePayloads = mirPayloads;
                options.ModuleArtifactAvailability = (moduleKey, kind, _, _) => kind switch
                {
                    ProjectModuleArtifactKinds.SemanticSignature => true,
                    ProjectModuleArtifactKinds.TypedSemanticSignature => true,
                    ProjectModuleArtifactKinds.TypesStatePayload => typesPayloadByModule.ContainsKey(moduleKey),
                    ProjectModuleArtifactKinds.MirStatePayload => mirPayloadByModule.ContainsKey(moduleKey),
                    _ => false
                };
                options.ModuleTypesStatePayloadLoader = (moduleKey, kind, _, _) =>
                    kind == ProjectModuleArtifactKinds.TypesStatePayload &&
                    typesPayloadByModule.TryGetValue(moduleKey, out var payload)
                        ? payload
                        : null;
                options.ModuleMirStatePayloadLoader = (moduleKey, kind, _, _) =>
                    kind == ProjectModuleArtifactKinds.MirStatePayload &&
                    mirPayloadByModule.TryGetValue(moduleKey, out var payload)
                        ? payload
                        : null;
            });

            Assert.True(expected.Success, FormatDiagnostics(expected));
            Assert.True(second.Success, FormatFailure(second));
            Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault("Mir.moduleRestore.applied"));
            Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault("Mir.moduleRestore.fallbackBuildMir"));
            Assert.Equal(2, second.ProfilingCounters.GetValueOrDefault("Build.moduleStage.Mir.compiledModules"));
            Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault("Build.moduleStage.Mir.restoredModules"));
            Assert.True(second.ProfilingCounters.GetValueOrDefault("Build.moduleStage.Mir.maxObservedParallelism") > 1);
            Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault("Mir.build_mir.calls"));
            Assert.Equal(
                FormatConstructorLayouts(expected.MirModule!),
                FormatConstructorLayouts(second.MirModule!));
            Assert.True(
                string.Equals(expected.LlvmIrText, second.LlvmIrText, StringComparison.Ordinal),
                FormatTextDifference(expected.LlvmIrText, second.LlvmIrText));
            Assert.True(
                string.Equals(
                    Eidosc.Mir.MirFormatter.FormatMir(expected.MirModule!),
                    Eidosc.Mir.MirFormatter.FormatMir(second.MirModule!),
                    StringComparison.Ordinal),
                FormatTextDifference(
                    Eidosc.Mir.MirFormatter.FormatMir(expected.MirModule!),
                    Eidosc.Mir.MirFormatter.FormatMir(second.MirModule!)));
            Assert.Equal(
                FormatMirFunctionFingerprints(expected),
                FormatMirFunctionFingerprints(second));
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
        value => LibB.inc(LibA.unbox(LibA.Box(value)))
    }
}
""");
        File.WriteAllText(Path.Combine(tempDir, "LibA.eidos"), """
LibA :: module {
    Box :: type { Box(Int) }

    unbox :: Box -> Int
    {
        Box(value) => value
    }
}
""");
        File.WriteAllText(Path.Combine(tempDir, "LibB.eidos"), """
LibB :: module {
    inc :: Int -> Int
    {
        value => value + 1
    }
}
""");
        return entryFile;
    }

    private static CompilationResult RunLlvm(
        string entryFile,
        string source,
        Action<CompilationOptions> configure)
    {
        var options = new CompilationOptions
        {
            InputFile = entryFile,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Llvm,
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

    private static string[] FormatConstructorLayouts(Eidosc.Mir.MirModule module) =>
        module.ConstructorLayouts
            .OrderBy(static entry => entry.Key)
            .SelectMany(static entry => entry.Value
                .OrderBy(static layout => layout.ConstructorName, StringComparer.Ordinal)
                .Select(layout =>
                    $"{entry.Key}:{layout.TypeName}:{layout.ConstructorName}:{layout.RuntimeTypeId}"))
            .ToArray();

    private static string FormatTextDifference(string? expected, string? actual)
    {
        var expectedLines = (expected ?? "").Split(["\r\n", "\n"], StringSplitOptions.None);
        var actualLines = (actual ?? "").Split(["\r\n", "\n"], StringSplitOptions.None);
        var difference = Enumerable.Range(0, Math.Min(expectedLines.Length, actualLines.Length))
            .FirstOrDefault(index => !string.Equals(expectedLines[index], actualLines[index], StringComparison.Ordinal));
        var start = Math.Max(0, difference - 3);
        var count = Math.Min(8, Math.Max(expectedLines.Length, actualLines.Length) - start);
        return string.Join(
            Environment.NewLine,
            $"first LLVM difference at line {difference + 1}",
            "expected:",
            string.Join(Environment.NewLine, expectedLines.Skip(start).Take(count)),
            "actual:",
            string.Join(Environment.NewLine, actualLines.Skip(start).Take(count)));
    }

    private static string FormatFailure(CompilationResult result) =>
        string.Join(
            Environment.NewLine,
            FormatDiagnostics(result),
            string.Join(
                Environment.NewLine,
                result.MirModule?.Functions.Select(static function =>
                    $"mir-function: {function.Name} | {function.FunctionId}") ?? []),
            string.Join(
                Environment.NewLine,
                result.ProfilingCounters
                    .Where(static entry => entry.Key.Contains("moduleRestore", StringComparison.Ordinal) ||
                                           entry.Key.Contains("moduleStage.Mir", StringComparison.Ordinal))
                    .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                    .Select(static entry => $"{entry.Key}={entry.Value}")));

    private static string[] FormatMirFunctionFingerprints(CompilationResult result) =>
        result.MirFunctionFingerprints?.Functions
            .Select(static fingerprint => $"{fingerprint.FunctionKey}:{fingerprint.BodyHash}")
            .ToArray() ?? [];

}
