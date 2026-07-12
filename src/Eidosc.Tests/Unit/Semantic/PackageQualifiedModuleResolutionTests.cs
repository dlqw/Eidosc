using Eidosc.Symbols;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Mir;
using Eidosc.Semantic;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class PackageQualifiedModuleResolutionTests
{
    [Fact]
    public void CompilationPipeline_PackageQualifiedImports_RegisterDistinctSamePathModules()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_pkg_modules_{Guid.NewGuid():N}");
        var appRoot = Path.Combine(tempDir, "app");
        var aRoot = Path.Combine(tempDir, "a", "src");
        var bRoot = Path.Combine(tempDir, "b", "src");

        try
        {
            Directory.CreateDirectory(appRoot);
            Directory.CreateDirectory(Path.Combine(aRoot, "Common"));
            Directory.CreateDirectory(Path.Combine(bRoot, "Common"));

            var entryFile = Path.Combine(appRoot, "Main.eidos");
            File.WriteAllText(entryFile, """
                Main :: module {
                    AResult :: import a::Common.Result;
                    BResult :: import b::Common.Result;

                    keepA :: AResult::Error -> AResult::Error { x => x }
                    keepB :: BResult::Error -> BResult::Error { x => x }
                }
                """);

            File.WriteAllText(Path.Combine(aRoot, "Common", "Result.eidos"), """
                Common.Result :: module {
                    export Error :: type { AErr }
                }
                """);

            File.WriteAllText(Path.Combine(bRoot, "Common", "Result.eidos"), """
                Common.Result :: module {
                    export Error :: type { BErr }
                }
                """);

            var result = new CompilationPipeline(File.ReadAllText(entryFile), new CompilationOptions
            {
                InputFile = entryFile,
                LanguageVersion = EidosLanguageVersions.Current,
                StopAtPhase = CompilationPhase.Namer,
                NoImplicitPrelude = true,
                UseColors = false,
                PackageImportRoots = new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["a"] = [aRoot],
                    ["b"] = [bRoot]
                }
            }).Run();

            Assert.True(result.Success, FormatDiagnostics(result));
            Assert.NotNull(result.SymbolTable);
            Assert.True(result.SymbolTable.Modules.ModulePaths.ContainsKey("a::Common/Result"));
            Assert.True(result.SymbolTable.Modules.ModulePaths.ContainsKey("b::Common/Result"));
            Assert.NotEqual(
                result.SymbolTable.Modules.ModulePaths["a::Common/Result"],
                result.SymbolTable.Modules.ModulePaths["b::Common/Result"]);

            var aIdentityKey = ModuleRegistry.ToModuleIdentityKey(
                "a",
                NormalizePackageInstanceRoot(aRoot),
                ["Common", "Result"]);
            var bIdentityKey = ModuleRegistry.ToModuleIdentityKey(
                "b",
                NormalizePackageInstanceRoot(bRoot),
                ["Common", "Result"]);
            Assert.True(result.SymbolTable.Modules.ModuleIdentityKeys.ContainsKey(aIdentityKey));
            Assert.True(result.SymbolTable.Modules.ModuleIdentityKeys.ContainsKey(bIdentityKey));
            Assert.NotEqual(
                result.SymbolTable.Modules.ModuleIdentityKeys[aIdentityKey],
                result.SymbolTable.Modules.ModuleIdentityKeys[bIdentityKey]);
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
    public void CompilationPipeline_PackageImport_InternalStdImportKeepsStdModulePath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_pkg_std_{Guid.NewGuid():N}");
        var appRoot = Path.Combine(tempDir, "app");
        var packageRoot = Path.Combine(tempDir, "pkg", "src");

        try
        {
            Directory.CreateDirectory(appRoot);
            Directory.CreateDirectory(packageRoot);

            var entryFile = Path.Combine(appRoot, "Main.eidos");
            File.WriteAllText(entryFile, """
                Main :: module {
                    import pkg::Feature
                }
                """);

            File.WriteAllText(Path.Combine(packageRoot, "Feature.eidos"), """
                Feature :: module {
                    import Std::Seq

                    export Marker :: type { Marker }
                }
                """);

            var result = new CompilationPipeline(File.ReadAllText(entryFile), new CompilationOptions
            {
                InputFile = entryFile,
                LanguageVersion = EidosLanguageVersions.Current,
                StopAtPhase = CompilationPhase.Namer,
                NoImplicitPrelude = true,
                UseColors = false,
                PackageImportRoots = new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["pkg"] = [packageRoot]
                }
            }).Run();

            Assert.True(result.Success, FormatDiagnostics(result));
            Assert.NotNull(result.SymbolTable);
            Assert.True(result.SymbolTable.Modules.ModulePaths.ContainsKey("pkg::Feature"));
            Assert.True(result.SymbolTable.Modules.ModulePaths.ContainsKey("Std::Seq"));
            Assert.False(result.SymbolTable.Modules.ModulePaths.ContainsKey("pkg::Std.Seq"));
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
    public void CompilationPipeline_PackageQualifiedImportedFunctions_CarryModuleIdentityInMir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_pkg_function_identity_{Guid.NewGuid():N}");
        var appRoot = Path.Combine(tempDir, "app");
        var aRoot = Path.Combine(tempDir, "a", "src");
        var bRoot = Path.Combine(tempDir, "b", "src");

        try
        {
            Directory.CreateDirectory(appRoot);
            Directory.CreateDirectory(Path.Combine(aRoot, "Common"));
            Directory.CreateDirectory(Path.Combine(bRoot, "Common"));

            var entryFile = Path.Combine(appRoot, "Main.eidos");
            File.WriteAllText(entryFile, """
                Main :: module {
                    import a::Common.Tools
                    import b::Common.Tools

                    main :: Unit -> Int { _ => 0 }
                }
                """);

            File.WriteAllText(Path.Combine(aRoot, "Common", "Tools.eidos"), """
                Common.Tools :: module {
                    export pick :: Int -> Int { x => x }
                }
                """);

            File.WriteAllText(Path.Combine(bRoot, "Common", "Tools.eidos"), """
                Common.Tools :: module {
                    export pick :: Int -> Int { x => x }
                }
                """);

            var result = new CompilationPipeline(File.ReadAllText(entryFile), new CompilationOptions
            {
                InputFile = entryFile,
                LanguageVersion = EidosLanguageVersions.Current,
                StopAtPhase = CompilationPhase.Mir,
                NoImplicitPrelude = true,
                UseColors = false,
                PackageImportRoots = new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["a"] = [aRoot],
                    ["b"] = [bRoot]
                }
            }).Run();

            Assert.True(result.Success, FormatDiagnostics(result));
            var mirModule = Assert.IsType<MirModule>(result.MirModule);
            var packageFunctions = mirModule.Functions
                .Where(function => function.FunctionId.Module.EndsWith("Common/Tools", StringComparison.Ordinal))
                .ToList();

            Assert.Equal(2, packageFunctions.Count);
            Assert.Contains(packageFunctions, function =>
                string.Equals(function.FunctionId.Module, "a::Common/Tools", StringComparison.Ordinal) &&
                function.FunctionId.ModuleIdentityKey.Contains(NormalizePackageInstanceRoot(aRoot), StringComparison.Ordinal));
            Assert.Contains(packageFunctions, function =>
                string.Equals(function.FunctionId.Module, "b::Common/Tools", StringComparison.Ordinal) &&
                function.FunctionId.ModuleIdentityKey.Contains(NormalizePackageInstanceRoot(bRoot), StringComparison.Ordinal));
            Assert.NotEqual(packageFunctions[0].FunctionId.ModuleIdentityKey, packageFunctions[1].FunctionId.ModuleIdentityKey);
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
    public void CompilationPipeline_PackageQualifiedSameNamedConstructors_CarryDistinctRuntimeTypeIdsInMir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_pkg_ctor_identity_{Guid.NewGuid():N}");
        var appRoot = Path.Combine(tempDir, "app");
        var aRoot = Path.Combine(tempDir, "a", "src");
        var bRoot = Path.Combine(tempDir, "b", "src");

        try
        {
            Directory.CreateDirectory(appRoot);
            Directory.CreateDirectory(Path.Combine(aRoot, "Common"));
            Directory.CreateDirectory(Path.Combine(bRoot, "Common"));

            var entryFile = Path.Combine(appRoot, "Main.eidos");
            File.WriteAllText(entryFile, """
                Main :: module {
                    import a::Common.Box
                    import b::Common.Box

                    main :: Unit -> Int { _ => 0 }
                }
                """);

            File.WriteAllText(Path.Combine(aRoot, "Common", "Box.eidos"), """
                Common.Box :: module {
                    export Box :: type { Same(Int) }
                }
                """);

            File.WriteAllText(Path.Combine(bRoot, "Common", "Box.eidos"), """
                Common.Box :: module {
                    export Box :: type { Same(Int) }
                }
                """);

            var result = new CompilationPipeline(File.ReadAllText(entryFile), new CompilationOptions
            {
                InputFile = entryFile,
                LanguageVersion = EidosLanguageVersions.Current,
                StopAtPhase = CompilationPhase.Mir,
                NoImplicitPrelude = true,
                UseColors = false,
                PackageImportRoots = new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["a"] = [aRoot],
                    ["b"] = [bRoot]
                }
            }).Run();

            Assert.True(result.Success, FormatDiagnostics(result));
            var mirModule = Assert.IsType<MirModule>(result.MirModule);
            var sameConstructorRuntimeTypeIds = mirModule.ConstructorLayouts.Values
                .SelectMany(static layouts => layouts)
                .Where(static layout => string.Equals(layout.ConstructorName, "Same", StringComparison.Ordinal))
                .Select(static layout => layout.RuntimeTypeId)
                .Where(static runtimeTypeId => runtimeTypeId != 0)
                .Distinct()
                .ToList();

            Assert.Equal(2, sameConstructorRuntimeTypeIds.Count);
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
    public void CompilationPipeline_StdPackageQualifiedImport_ResolvesAsStdlibPackageAlias()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_pkg_std_alias_{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(tempDir);
            var entryFile = Path.Combine(tempDir, "Main.eidos");
            File.WriteAllText(entryFile, """
                Main :: module {
                    import Std::Seq
                }
                """);

            var result = new CompilationPipeline(File.ReadAllText(entryFile), new CompilationOptions
            {
                InputFile = entryFile,
                LanguageVersion = EidosLanguageVersions.Current,
                StopAtPhase = CompilationPhase.Namer,
                NoImplicitPrelude = true,
                UseColors = false
            }).Run();

            Assert.True(result.Success, FormatDiagnostics(result));
            Assert.NotNull(result.SymbolTable);
            Assert.True(result.SymbolTable.Modules.ModulePaths.ContainsKey("Std::Seq"));
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
    public void CompilationPipeline_ProjectWithoutStdlibImportRoot_ResolvesPrecompiledStdPackage()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_pkg_implicit_std_{Guid.NewGuid():N}");
        var projectDir = Path.Combine(tempDir, "app");
        var srcDir = Path.Combine(projectDir, "src");

        try
        {
            Directory.CreateDirectory(srcDir);
            var entryFile = Path.Combine(srcDir, "Main.eidos");
            File.WriteAllText(entryFile, """
                Main :: module {
                    import Std::Seq

                    keep :: Std::Seq::Seq[Int] -> Std::Seq::Seq[Int] { xs => xs }
                }
                """);
            File.WriteAllText(Path.Combine(projectDir, "eidos.toml"), """
                sourceRoots = ["src"]
                defaultTarget = "main"

                [[targets]]
                name = "main"
                entry = "src/Main.eidos"
                kind = "executable"
                """);

            var result = new CompilationPipeline(File.ReadAllText(entryFile), new CompilationOptions
            {
                InputFile = entryFile,
                LanguageVersion = EidosLanguageVersions.Current,
                StopAtPhase = CompilationPhase.Namer,
                NoImplicitPrelude = true,
                UseColors = false
            }).Run();

            Assert.True(result.Success, FormatDiagnostics(result));
            Assert.NotNull(result.SymbolTable);
            Assert.True(result.SymbolTable.Modules.ModulePaths.ContainsKey("Std::Seq"));
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
    public void CompilationPipeline_PackageQualifiedStdConstructor_Resolves()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_pkg_std_ctor_{Guid.NewGuid():N}");
        var projectDir = Path.Combine(tempDir, "app");
        var srcDir = Path.Combine(projectDir, "src");

        try
        {
            Directory.CreateDirectory(srcDir);
            var entryFile = Path.Combine(srcDir, "Main.eidos");
            File.WriteAllText(entryFile, """
                Main :: module {
                    import Std::Ordering

                    keep :: Unit -> Std::Ordering::Ordering
                    {
                        _ => Std::Ordering::Equal()
                    }
                }
                """);
            File.WriteAllText(Path.Combine(projectDir, "eidos.toml"), """
                sourceRoots = ["src"]
                defaultTarget = "main"

                [[targets]]
                name = "main"
                entry = "src/Main.eidos"
                kind = "executable"
                """);

            var result = new CompilationPipeline(File.ReadAllText(entryFile), new CompilationOptions
            {
                InputFile = entryFile,
                LanguageVersion = EidosLanguageVersions.Current,
                StopAtPhase = CompilationPhase.Types,
                NoImplicitPrelude = true,
                UseColors = false
            }).Run();

            Assert.True(result.Success, FormatDiagnostics(result));
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
    public void CompilationPipeline_UnqualifiedStdImport_ResolvesUniqueStdModuleCandidate()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_pkg_std_short_{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(tempDir);
            var entryFile = Path.Combine(tempDir, "Main.eidos");
            File.WriteAllText(entryFile, """
                Main :: module {
                    import Seq

                    keep :: Seq::Seq[Int] -> Seq::Seq[Int] { xs => xs }
                }
                """);

            var result = new CompilationPipeline(File.ReadAllText(entryFile), new CompilationOptions
            {
                InputFile = entryFile,
                LanguageVersion = EidosLanguageVersions.Current,
                StopAtPhase = CompilationPhase.Namer,
                NoImplicitPrelude = true,
                UseColors = false
            }).Run();

            Assert.True(result.Success, FormatDiagnostics(result));
            Assert.NotNull(result.SymbolTable);
            Assert.True(result.SymbolTable.Modules.ModulePaths.ContainsKey("Std::Seq"));
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
    public void CompilationPipeline_UnqualifiedImportWithPackageCollision_ReportsAmbiguousModulePath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_pkg_module_collision_{Guid.NewGuid():N}");
        var appRoot = Path.Combine(tempDir, "app");
        var pkgRoot = Path.Combine(tempDir, "pkg", "src");

        try
        {
            Directory.CreateDirectory(appRoot);
            Directory.CreateDirectory(pkgRoot);

            var entryFile = Path.Combine(appRoot, "Main.eidos");
            File.WriteAllText(entryFile, """
                Main :: module {
                    import Seq
                }
                """);

            File.WriteAllText(Path.Combine(pkgRoot, "Seq.eidos"), """
                Seq :: module {
                    export Seq :: type { CustomSeq }
                }
                """);

            var result = new CompilationPipeline(File.ReadAllText(entryFile), new CompilationOptions
            {
                InputFile = entryFile,
                LanguageVersion = EidosLanguageVersions.Current,
                StopAtPhase = CompilationPhase.Namer,
                NoImplicitPrelude = true,
                UseColors = false,
                PackageImportRoots = new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["pkg"] = [pkgRoot]
                }
            }).Run();

            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, diagnostic =>
                diagnostic.Message.Contains("Ambiguous module path 'Seq'", StringComparison.Ordinal) &&
                diagnostic.Message.Contains("Std::Seq", StringComparison.Ordinal) &&
                diagnostic.Message.Contains("pkg::Seq", StringComparison.Ordinal));
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
    public void CompilationPipeline_PrecompiledStdlibRootInput_UsesStdPackageIdentityForShortImports()
    {
        var result = RunStdlibRootInput("Math.eidos", CompilationPhase.Namer);

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.NotNull(result.SymbolTable);
        Assert.True(result.SymbolTable.Modules.ModulePaths.ContainsKey("Std::Math"));
        Assert.True(result.SymbolTable.Modules.ModulePaths.ContainsKey("Std::FloatMath"));
        Assert.False(result.SymbolTable.Modules.ModulePaths.ContainsKey("Math"));
        Assert.False(result.SymbolTable.Modules.ModulePaths.ContainsKey("FloatMath"));
    }

    [Fact]
    public void CompilationPipeline_PrecompiledStdlibRootInput_TypesDoesNotUseImportedSignatureOnlyShortcut()
    {
        var result = RunStdlibRootInput("GameMath.eidos", CompilationPhase.Types);

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.DoesNotContain("Namer.precompiledImportSignatureOnly.functions", result.ProfilingCounters.Keys);
        Assert.DoesNotContain("Types.precompiledImportSignatureOnly.functions", result.ProfilingCounters.Keys);
    }

    [Fact]
    public void CompilationPipeline_TypesWithImportedPrecompiledStd_UsesImportedSignatureOnlyShortcut()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_std_signature_only_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var entryFile = Path.Combine(tempDir, "Main.eidos");
            File.WriteAllText(entryFile, """
                import Std::GameMath

                main :: Unit -> Int
                {
                    _ => GameMath::scale_i(GameMath::east_i, 4).x
                }
                """);

            var result = new CompilationPipeline(File.ReadAllText(entryFile), new CompilationOptions
            {
                InputFile = entryFile,
                LanguageVersion = EidosLanguageVersions.Current,
                StopAtPhase = CompilationPhase.Types,
                NoImplicitPrelude = true,
                EnableDetailedProfiling = true,
                UseColors = false
            }).Run();

            Assert.True(result.Success, FormatDiagnostics(result));
            Assert.True(
                result.ProfilingCounters.TryGetValue("Namer.precompiledImportSignatureOnly.functions", out var namerSkipped),
                FormatCounters(result));
            Assert.True(namerSkipped > 0, FormatCounters(result));
            Assert.True(
                result.ProfilingCounters.TryGetValue("Types.precompiledImportSignatureOnly.functions", out var typesSkipped),
                FormatCounters(result));
            Assert.True(typesSkipped > 0, FormatCounters(result));
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
    public void CompilationPipeline_MissingPackageQualifiedImport_ReportsPackageSearchRoots()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_pkg_missing_module_{Guid.NewGuid():N}");
        var appRoot = Path.Combine(tempDir, "app");
        var pkgRoot = Path.Combine(tempDir, "pkg", "src");

        try
        {
            Directory.CreateDirectory(appRoot);
            Directory.CreateDirectory(pkgRoot);

            var entryFile = Path.Combine(appRoot, "Main.eidos");
            File.WriteAllText(entryFile, """
                Main :: module {
                    import pkg::Feature.Missing
                }
                """);

            var result = new CompilationPipeline(File.ReadAllText(entryFile), new CompilationOptions
            {
                InputFile = entryFile,
                LanguageVersion = EidosLanguageVersions.Current,
                StopAtPhase = CompilationPhase.Namer,
                NoImplicitPrelude = true,
                UseColors = false,
                PackageImportRoots = new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["pkg"] = [pkgRoot]
                }
            }).Run();

            Assert.False(result.Success);
            var diagnostic = Assert.Single(
                result.Diagnostics,
                diagnostic => diagnostic.Code == "E3000" &&
                              diagnostic.Message.Contains(
                                  "Unable to resolve imported module 'pkg::Feature::Missing'",
                                  StringComparison.Ordinal));
            Assert.Contains(diagnostic.Notes, note => note == $"entry file: {entryFile}");
            Assert.Contains(diagnostic.Notes, note => note == $"searched root: {pkgRoot}");
            Assert.DoesNotContain(diagnostic.Notes, note => note == $"searched root: {appRoot}");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static string FormatDiagnostics(CompilationResult result)
    {
        return string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}"));
    }

    private static string FormatCounters(CompilationResult result)
    {
        return string.Join(
            Environment.NewLine,
            result.ProfilingCounters
                .OrderBy(counter => counter.Key, StringComparer.Ordinal)
                .Select(counter => $"{counter.Key}={counter.Value}"));
    }

    private static string NormalizePackageInstanceRoot(string rootDirectory)
    {
        return Path.GetFullPath(rootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace('\\', '/');
    }

    private static CompilationResult RunStdlibRootInput(string fileName, CompilationPhase phase)
    {
        var stdlibFile = FindWorkspaceFile("src", "Eidosc", "Stdlib", "Precompiled", "Std", fileName);
        return new CompilationPipeline(File.ReadAllText(stdlibFile), new CompilationOptions
        {
            InputFile = stdlibFile,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = phase,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();
    }

    private static string FindWorkspaceFile(params string[] segments)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine([dir, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var parent = Directory.GetParent(dir);
            if (parent == null)
            {
                break;
            }

            dir = parent.FullName;
        }

        throw new FileNotFoundException($"Unable to locate workspace file: {Path.Combine(segments)}");
    }
}
