using Eidosc.CodeGen;
using Eidosc.CodeGen.Llvm;
using Xunit;

namespace Eidosc.Tests.Unit.Llvm;

public sealed class LlvmCompilerTests
{
    [Fact]
    public void CompileToIr_UsesConfiguredTargetInfoInHeader()
    {
        var module = new LlvmModule
        {
            Name = "Main"
        };

        var compiler = new LlvmCompiler(TargetInfo.Arm64Linux);
        var ir = compiler.CompileToIr(module);

        Assert.Contains($"target datalayout = \"{TargetInfo.Arm64Linux.DataLayout}\"", ir);
        Assert.Contains($"target triple = \"{TargetInfo.Arm64Linux.Triple}\"", ir);
    }

    [Fact]
    public void CompileToIr_WithProfile_RecordsEmitEvent()
    {
        var module = new LlvmModule
        {
            Name = "Main"
        };
        var profile = new CodeGenProfile();

        var compiler = new LlvmCompiler(TargetInfo.Default, profile: profile);
        compiler.CompileToIr(module);

        var profileEvent = Assert.Single(profile.Events, static profileEvent => profileEvent.Name == "emit_ir");
        Assert.Equal("llvm", profileEvent.Category);
        Assert.Equal("emit_ir", profileEvent.Name);
        Assert.True(profileEvent.Success);
    }

    [Fact]
    public void Constructor_WithProfile_RecordsCompilerConfiguration()
    {
        var profile = new CodeGenProfile();

        _ = new LlvmCompiler(
            TargetInfo.Default.WithNativeCpu(),
            optimizationLevel: 3,
            enableLto: true,
            linkMode: NativeLinkMode.PieExecutable,
            profile: profile,
            maxDegreeOfParallelism: 7);

        var profileEvent = Assert.Single(profile.Events, static profileEvent => profileEvent.Name == "llvm_compiler");
        Assert.Equal("config", profileEvent.Category);
        Assert.Equal("3", profileEvent.Metadata["optimizationLevel"]);
        Assert.Equal(bool.TrueString, profileEvent.Metadata["lto"]);
        Assert.Equal(bool.TrueString, profileEvent.Metadata["nativeCpu"]);
        Assert.Equal(NativeLinkMode.PieExecutable.ToString(), profileEvent.Metadata["linkMode"]);
        Assert.Equal("native", profileEvent.Metadata["targetCpu"]);
        Assert.Equal("7", profileEvent.Metadata["jobs"]);
        Assert.True(profileEvent.Metadata.ContainsKey("backendConfigHash"));
        Assert.NotEmpty(profileEvent.Metadata["backendConfigHash"]);
    }

    [Fact]
    public void BackendConfiguration_StableHash_ChangesWhenObjectCodeFlagsChange()
    {
        var first = LlvmBackendConfiguration.Create(
            TargetInfo.X86_64Linux,
            optimizationLevel: 0,
            enableLto: false,
            NativeLinkMode.NonPieExecutable,
            extraCFlags: null,
            extraLinkFlags: null);
        var second = LlvmBackendConfiguration.Create(
            TargetInfo.X86_64Linux,
            optimizationLevel: 2,
            enableLto: false,
            NativeLinkMode.NonPieExecutable,
            extraCFlags: null,
            extraLinkFlags: null);
        var third = LlvmBackendConfiguration.Create(
            TargetInfo.X86_64Linux,
            optimizationLevel: 0,
            enableLto: true,
            NativeLinkMode.NonPieExecutable,
            extraCFlags: null,
            extraLinkFlags: null);

        Assert.NotEqual(first.StableHash, second.StableHash);
        Assert.NotEqual(first.StableHash, third.StableHash);
        Assert.Contains("-relocation-model=static", first.LlcObjectFlags);
    }

    [Fact]
    public void CompileToObject_WithAvailableLlvmOrClangToolchain_ProducesObjectFile()
    {
        if (!ToolExists("llc") && !ToolExists("clang"))
        {
            return;
        }

        var module = new LlvmModule
        {
            Name = "Main"
        };

        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_llvm_compiler_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var objectPath = Path.Combine(tempDir, OperatingSystem.IsWindows() ? "compiler_test.obj" : "compiler_test.o");

        try
        {
            var profile = new CodeGenProfile();
            var compiler = new LlvmCompiler(TargetInfo.Default, temporaryDirectory: tempDir, profile: profile);
            var ir = compiler.CompileToIr(module);
            var result = compiler.CompileToObject(ir, objectPath);

            Assert.True(result.Success, result.ErrorMessage ?? "CompileToObject failed.");
            Assert.True(File.Exists(objectPath));
            Assert.Contains(profile.Events, profileEvent => profileEvent.Category == "object");
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures on CI/Windows file lock races.
            }
        }
    }

    [Fact]
    public void CompileObjectGroup_WithAvailableLlvmOrClangToolchain_ProducesCachedObjectFile()
    {
        if (!ToolExists("llc") && !ToolExists("clang"))
        {
            return;
        }

        var targetInfo = TargetInfo.X86_64Linux;
        var module = new LlvmModule
        {
            Name = "object_group",
            Functions = [CreateReturnConstantFunction("group_main", 42)]
        };
        var envelope = LlvmModuleEnvelopeSnapshot.FromModule(
            module,
            targetInfo.DataLayout,
            targetInfo.Triple);
        var fragments = LlvmFunctionFragmentSnapshot.FromModule(module);
        var plan = LlvmCodegenUnitPlanSnapshot.Create(
            envelope,
            module,
            fragments,
            LlvmBackendConfiguration.Create(
                targetInfo,
                optimizationLevel: 0,
                enableLto: false,
                NativeLinkMode.NonPieExecutable,
                extraCFlags: null,
                extraLinkFlags: null));
        var group = Assert.Single(plan.ObjectGroups);
        var recomposedGroup = LlvmFunctionFingerprintBuilder.RecomposeObjectGroup(envelope, fragments, group);

        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_llvm_group_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var firstObjectPath = Path.Combine(tempDir, $"first{targetInfo.ObjectExtension}");
        var secondObjectPath = Path.Combine(tempDir, $"second{targetInfo.ObjectExtension}");

        try
        {
            var profile = new CodeGenProfile();
            var compiler = new LlvmCompiler(
                targetInfo,
                optimizationLevel: 0,
                temporaryDirectory: tempDir,
                profile: profile);

            var first = compiler.CompileObjectGroup(recomposedGroup, firstObjectPath);
            var second = compiler.CompileObjectGroup(recomposedGroup, secondObjectPath);

            Assert.True(first.Success, first.ErrorMessage ?? "CompileObjectGroup first pass failed.");
            Assert.True(second.Success, second.ErrorMessage ?? "CompileObjectGroup second pass failed.");
            Assert.True(File.Exists(firstObjectPath));
            Assert.True(File.Exists(secondObjectPath));
            Assert.Contains(
                profile.Events,
                profileEvent => profileEvent.Name == "object_cache.llvm_object_group" &&
                    profileEvent.CacheHit);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures on CI/Windows file lock races.
            }
        }
    }

    [Fact]
    public void CompileObjectGroups_WithAvailableLlvmOrClangToolchain_PreservesStableResultOrder()
    {
        if (!ToolExists("llc") && !ToolExists("clang"))
        {
            return;
        }

        var targetInfo = TargetInfo.X86_64Linux;
        var module = new LlvmModule
        {
            Name = "object_groups",
            Functions =
            [
                CreateReturnConstantFunction("group_a", 1),
                CreateReturnConstantFunction("group_b", 2)
            ]
        };
        var envelope = LlvmModuleEnvelopeSnapshot.FromModule(
            module,
            targetInfo.DataLayout,
            targetInfo.Triple);
        var fragments = LlvmFunctionFragmentSnapshot.FromModule(module);
        var plan = LlvmCodegenUnitPlanSnapshot.Create(
            envelope,
            module,
            fragments,
            LlvmBackendConfiguration.Create(
                targetInfo,
                optimizationLevel: 0,
                enableLto: false,
                NativeLinkMode.NonPieExecutable,
                extraCFlags: null,
                extraLinkFlags: null));
        var recomposedGroups = plan.ObjectGroups
            .Select(group => LlvmFunctionFingerprintBuilder.RecomposeObjectGroup(envelope, fragments, group))
            .ToArray();

        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_llvm_groups_tests_{Guid.NewGuid():N}");
        var objectDir = Path.Combine(tempDir, "objects");

        try
        {
            var profile = new CodeGenProfile();
            var compiler = new LlvmCompiler(
                targetInfo,
                optimizationLevel: 0,
                temporaryDirectory: tempDir,
                profile: profile);

            var first = compiler.CompileObjectGroups(recomposedGroups, objectDir);
            var second = compiler.CompileObjectGroups(recomposedGroups, objectDir);

            Assert.Equal(recomposedGroups.Length, first.Count);
            Assert.Equal(recomposedGroups.Select(static group => group.GroupKey), first.Select(static result => result.GroupKey));
            Assert.All(first, result =>
            {
                Assert.True(result.Result.Success, result.Result.ErrorMessage ?? "CompileObjectGroups failed.");
                Assert.True(File.Exists(result.ObjectPath));
            });
            Assert.All(second, result => Assert.True(result.Result.Success, result.Result.ErrorMessage));
            Assert.True(
                profile.Events.Count(profileEvent => profileEvent.Name == "object_cache.llvm_object_group" &&
                    profileEvent.CacheHit) >= recomposedGroups.Length);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures on CI/Windows file lock races.
            }
        }
    }

    [Fact]
    public void CompileObjectGroups_WithRestorePlan_UsesStableCacheForRestorableGroups()
    {
        if (!ToolExists("llc") && !ToolExists("clang"))
        {
            return;
        }

        var targetInfo = TargetInfo.X86_64Linux;
        var module = new LlvmModule
        {
            Name = "object_groups_stable_restore",
            Functions =
            [
                CreateReturnConstantFunction("restore_a", 1),
                CreateReturnConstantFunction("rebuild_a", 2)
            ]
        };
        var envelope = LlvmModuleEnvelopeSnapshot.FromModule(
            module,
            targetInfo.DataLayout,
            targetInfo.Triple);
        var fragments = LlvmFunctionFragmentSnapshot.FromModule(module);
        var plan = LlvmCodegenUnitPlanSnapshot.Create(
            envelope,
            module,
            fragments,
            LlvmBackendConfiguration.Create(
                targetInfo,
                optimizationLevel: 0,
                enableLto: false,
                NativeLinkMode.NonPieExecutable,
                extraCFlags: null,
                extraLinkFlags: null));
        var restoreGroup = Assert.Single(plan.ObjectGroups, group => group.RootFunctionKey == "name:restore_a");
        var rebuildGroup = Assert.Single(plan.ObjectGroups, group => group.RootFunctionKey == "name:rebuild_a");
        var restorePlan = new LlvmObjectGroupRestorePlanSnapshot(
            "llvm-object-group-restore-plan-snapshot-v1",
            [
                new LlvmObjectGroupRestorePlanEntry(
                    restoreGroup.GroupKey,
                    restoreGroup.RootFunctionKey,
                    LlvmObjectGroupRestoreAction.Restore,
                    restoreGroup.MemberFunctionKeys,
                    RestoreFunctions: restoreGroup.FunctionCount,
                    RebuildFunctions: 0,
                    restoreGroup.TotalIrBytes),
                new LlvmObjectGroupRestorePlanEntry(
                    rebuildGroup.GroupKey,
                    rebuildGroup.RootFunctionKey,
                    LlvmObjectGroupRestoreAction.Rebuild,
                    rebuildGroup.MemberFunctionKeys,
                    RestoreFunctions: 0,
                    RebuildFunctions: rebuildGroup.FunctionCount,
                    rebuildGroup.TotalIrBytes)
            ]);

        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_llvm_groups_stable_restore_{Guid.NewGuid():N}");
        var objectDir = Path.Combine(tempDir, "objects");

        try
        {
            var profile = new CodeGenProfile();
            var compiler = new LlvmCompiler(
                targetInfo,
                optimizationLevel: 0,
                temporaryDirectory: tempDir,
                profile: profile);
            var coldGroups = plan.ObjectGroups
                .Select(group => LlvmFunctionFingerprintBuilder.RecomposeObjectGroup(envelope, fragments, group))
                .ToArray();
            var cold = compiler.CompileObjectGroups(coldGroups, objectDir);

            Assert.All(cold, result => Assert.True(result.Result.Success, result.Result.ErrorMessage));

            var restored = compiler.CompileObjectGroups(
                envelope,
                fragments,
                plan.ObjectGroups,
                objectDir,
                restorePlan);
            var restoredGroup = Assert.Single(restored, result => result.GroupKey == restoreGroup.GroupKey);
            var rebuiltGroup = Assert.Single(restored, result => result.GroupKey == rebuildGroup.GroupKey);

            Assert.True(restoredGroup.Result.Success, restoredGroup.Result.ErrorMessage);
            Assert.True(restoredGroup.StableCacheHit);
            Assert.False(restoredGroup.Recomposed);
            Assert.True(restoredGroup.Result.CacheHit);
            Assert.True(File.Exists(restoredGroup.ObjectPath));
            Assert.True(rebuiltGroup.Result.Success, rebuiltGroup.Result.ErrorMessage);
            Assert.False(rebuiltGroup.StableCacheHit);
            Assert.True(rebuiltGroup.Recomposed);
            Assert.Contains(
                profile.Events,
                profileEvent => profileEvent.Name == "object_cache.llvm_object_group_stable_restore" &&
                    profileEvent.CacheHit);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures on CI/Windows file lock races.
            }
        }
    }

    [Fact]
    public void CompileModuleEnvelopeObject_WithVisibleGlobal_AllowsObjectGroupsToLinkTogether()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        var targetInfo = TargetInfo.X86_64Linux;
        var module = new LlvmModule
        {
            Name = "object_group_link",
            Globals =
            [
                new LlvmGlobal
                {
                    Name = "shared_counter",
                    Type = LlvmIntType.I64,
                    Initializer = new LlvmConstant { Value = 7L, Type = LlvmIntType.I64 },
                    Linkage = LlvmLinkage.External,
                    IsConstant = false
                }
            ],
            Functions =
            [
                CreateLoadGlobalFunction("read_a", "shared_counter"),
                CreateLoadGlobalFunction("read_b", "shared_counter")
            ]
        };
        var envelope = LlvmModuleEnvelopeSnapshot.FromModule(
            module,
            targetInfo.DataLayout,
            targetInfo.Triple);
        var fragments = LlvmFunctionFragmentSnapshot.FromModule(module);
        var plan = LlvmCodegenUnitPlanSnapshot.Create(
            envelope,
            module,
            fragments,
            LlvmBackendConfiguration.Create(
                targetInfo,
                optimizationLevel: 0,
                enableLto: false,
                NativeLinkMode.NonPieExecutable,
                extraCFlags: null,
                extraLinkFlags: null));
        var groups = plan.ObjectGroups
            .Select(group => LlvmFunctionFingerprintBuilder.RecomposeObjectGroup(envelope, fragments, group))
            .ToArray();

        Assert.All(groups, group => Assert.Contains(SplitLines(group.IrText), static line => line == "@shared_counter = external global i64"));
        Assert.Contains(
            SplitLines(LlvmFunctionFingerprintBuilder.RecomposeModule(envelope, fragments).IrText),
            static line => line == "@shared_counter = global i64 7");

        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_llvm_group_link_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var envelopeObjectPath = Path.Combine(tempDir, $"envelope{targetInfo.ObjectExtension}");
        var outputPath = Path.Combine(tempDir, $"linked{targetInfo.ObjectExtension}");

        try
        {
            var compiler = new LlvmCompiler(
                targetInfo,
                optimizationLevel: 0,
                temporaryDirectory: tempDir);
            var envelopeResult = compiler.CompileModuleEnvelopeObject(envelope, envelopeObjectPath);
            var groupResults = compiler.CompileObjectGroups(groups, Path.Combine(tempDir, "groups"));

            Assert.True(envelopeResult.Success, envelopeResult.ErrorMessage ?? "CompileModuleEnvelopeObject failed.");
            Assert.All(groupResults, result => Assert.True(result.Result.Success, result.Result.ErrorMessage ?? "CompileObjectGroups failed."));

            var linkResult = compiler.LinkRelocatableObject(
                [envelopeObjectPath, .. groupResults.Select(static result => result.ObjectPath)],
                outputPath);

            Assert.True(linkResult.Success, linkResult.ErrorMessage ?? "Relocatable split-object link failed.");
            Assert.True(File.Exists(outputPath));
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures on CI/Windows file lock races.
            }
        }
    }

    [Fact]
    public void CompileToExecutableWithObjectGroups_WithAvailableToolchain_ProducesExecutable()
    {
        if (!ToolExists("llc") && !ToolExists("clang"))
        {
            return;
        }

        if (!ToolExists("clang"))
        {
            return;
        }

        var targetInfo = TargetInfo.Default;
        var module = new LlvmModule
        {
            Name = "split_executable",
            Functions = [CreateReturnConstantFunction("eidos_main", 0)]
        };
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_split_exe_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var executablePath = Path.Combine(tempDir, OperatingSystem.IsWindows() ? "split_executable.exe" : "split_executable");

        try
        {
            var compiler = new LlvmCompiler(
                targetInfo,
                optimizationLevel: 0,
                temporaryDirectory: tempDir);
            var result = compiler.CompileToExecutableWithObjectGroups(module, executablePath);

            Assert.True(result.Success, result.ErrorMessage ?? "CompileToExecutableWithObjectGroups failed.");
            Assert.True(File.Exists(executablePath));
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures on CI/Windows file lock races.
            }
        }
    }

    [Fact]
    public void CompileRestoredFragmentsToExecutableWithObjectGroups_WithAvailableToolchain_ProducesExecutable()
    {
        if (!ToolExists("llc") && !ToolExists("clang"))
        {
            return;
        }

        if (!ToolExists("clang"))
        {
            return;
        }

        var targetInfo = TargetInfo.Default;
        var module = new LlvmModule
        {
            Name = "restored_split_executable",
            Functions = [CreateReturnConstantFunction("eidos_main", 0)]
        };
        var envelope = LlvmModuleEnvelopeSnapshot.FromModule(
            module,
            targetInfo.DataLayout,
            targetInfo.Triple);
        var fragments = LlvmFunctionFragmentSnapshot.FromModule(module);
        var plan = LlvmCodegenUnitPlanSnapshot.Create(
            envelope,
            module,
            fragments,
            LlvmBackendConfiguration.Create(
                targetInfo,
                optimizationLevel: 0,
                enableLto: false,
                NativeLinkMode.NonPieExecutable,
                extraCFlags: null,
                extraLinkFlags: null));
        var restorePlan = LlvmObjectGroupRestorePlanSnapshot.Create(
            plan.ObjectGroups,
            LlvmFunctionFragmentRestorePlanSnapshot.Create(fragments, fragments));
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_restored_split_exe_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var executablePath = Path.Combine(tempDir, OperatingSystem.IsWindows() ? "restored_split_executable.exe" : "restored_split_executable");

        try
        {
            var profile = new CodeGenProfile();
            var compiler = new LlvmCompiler(
                targetInfo,
                optimizationLevel: 0,
                temporaryDirectory: tempDir,
                profile: profile);
            var result = compiler.CompileRestoredFragmentsToExecutableWithObjectGroups(
                envelope,
                fragments,
                plan,
                executablePath,
                restorePlan: restorePlan);

            Assert.True(result.Success, result.ErrorMessage ?? "CompileRestoredFragmentsToExecutableWithObjectGroups failed.");
            Assert.True(File.Exists(executablePath));
            Assert.Contains(
                profile.Events,
                profileEvent => profileEvent.Name == "native_object_groups_restore_from_previous_fragments" &&
                    profileEvent.CacheHit);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures on CI/Windows file lock races.
            }
        }
    }

    [Fact]
    public void CompileRestoredFragmentsToExecutable_WithAvailableToolchain_ProducesExecutable()
    {
        if (!ToolExists("llc") && !ToolExists("clang"))
        {
            return;
        }

        if (!ToolExists("clang"))
        {
            return;
        }

        var targetInfo = TargetInfo.Default;
        var module = new LlvmModule
        {
            Name = "restored_full_executable",
            Functions = [CreateReturnConstantFunction("eidos_main", 0)]
        };
        var envelope = LlvmModuleEnvelopeSnapshot.FromModule(
            module,
            targetInfo.DataLayout,
            targetInfo.Triple);
        var fragments = LlvmFunctionFragmentSnapshot.FromModule(module);
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_restored_full_exe_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var executablePath = Path.Combine(tempDir, OperatingSystem.IsWindows() ? "restored_full_executable.exe" : "restored_full_executable");

        try
        {
            var profile = new CodeGenProfile();
            var compiler = new LlvmCompiler(
                targetInfo,
                optimizationLevel: 0,
                temporaryDirectory: tempDir,
                profile: profile);
            var result = compiler.CompileRestoredFragmentsToExecutable(
                envelope,
                fragments,
                executablePath);

            Assert.True(result.Success, result.ErrorMessage ?? "CompileRestoredFragmentsToExecutable failed.");
            Assert.True(File.Exists(executablePath));
            Assert.Contains(
                profile.Events,
                profileEvent => profileEvent.Name == "native_full_module_restore_from_previous_fragments" &&
                    profileEvent.CacheHit);
            Assert.Contains(
                profile.Events,
                profileEvent => profileEvent.Name == "recompose_restored_full_module_ir" &&
                    profileEvent.CacheHit);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures on CI/Windows file lock races.
            }
        }
    }

    [Fact]
    public void ObjectGroupRestoreSummary_RecordsMixedRestoreMetadata()
    {
        var profile = new CodeGenProfile();
        var compiler = new LlvmCompiler(TargetInfo.Default, profile: profile);
        var restorePlan = new LlvmObjectGroupRestorePlanSnapshot(
            "llvm-object-group-restore-plan-snapshot-v1",
            [
                new LlvmObjectGroupRestorePlanEntry(
                    "restore-group",
                    "name:keep",
                    LlvmObjectGroupRestoreAction.Restore,
                    ["name:keep"],
                    RestoreFunctions: 1,
                    RebuildFunctions: 0,
                    TotalIrBytes: 100),
                new LlvmObjectGroupRestorePlanEntry(
                    "rebuild-group",
                    "name:changed",
                    LlvmObjectGroupRestoreAction.Rebuild,
                    ["name:changed"],
                    RestoreFunctions: 0,
                    RebuildFunctions: 1,
                    TotalIrBytes: 200)
            ]);
        var results = new[]
        {
            new LlvmObjectGroupCompileResult(
                "restore-group",
                "name:keep",
                "restore.o",
                new CodeGenResult { Success = true, CacheHit = true },
                FunctionCount: 1,
                IrBytes: 100,
                Recomposed: false,
                StableCacheHit: true),
            new LlvmObjectGroupCompileResult(
                "rebuild-group",
                "name:changed",
                "rebuild.o",
                new CodeGenResult { Success = true, CacheHit = false },
                FunctionCount: 1,
                IrBytes: 200,
                Recomposed: true,
                StableCacheHit: false)
        };

        compiler.RecordObjectGroupRestoreSummaryForTesting(restorePlan, results);

        var profileEvent = Assert.Single(profile.Events, static profileEvent =>
            profileEvent.Name == "llvm_object_group_restore_summary");
        Assert.Equal("True", profileEvent.Metadata["mixed"]);
        Assert.Equal("1", profileEvent.Metadata["restorableGroups"]);
        Assert.Equal("1", profileEvent.Metadata["rebuildGroups"]);
        Assert.Equal("1", profileEvent.Metadata["restoredObjectCacheHits"]);
        Assert.Equal("0", profileEvent.Metadata["rebuiltObjectCacheHits"]);
        Assert.Equal("1", profileEvent.Metadata["restoreFunctions"]);
        Assert.Equal("1", profileEvent.Metadata["rebuildFunctions"]);
        Assert.Equal("100", profileEvent.Metadata["restorableIrBytes"]);
        Assert.Equal("200", profileEvent.Metadata["rebuildIrBytes"]);
        Assert.Equal("True", profileEvent.Metadata["allRestorableGroupsHitObjectCache"]);
    }

    [Fact]
    public void ObjectGroupSummary_RecordsStableCacheAndRecompositionMetadata()
    {
        var profile = new CodeGenProfile();
        var compiler = new LlvmCompiler(TargetInfo.Default, profile: profile);
        var results = new[]
        {
            new LlvmObjectGroupCompileResult(
                "restore-group",
                "name:keep",
                "restore.o",
                new CodeGenResult { Success = true, CacheHit = true },
                FunctionCount: 1,
                IrBytes: 100,
                Recomposed: false,
                StableCacheHit: true),
            new LlvmObjectGroupCompileResult(
                "rebuild-group",
                "name:changed",
                "rebuild.o",
                new CodeGenResult { Success = true, CacheHit = false },
                FunctionCount: 1,
                IrBytes: 200,
                Recomposed: true,
                StableCacheHit: false)
        };

        compiler.RecordObjectGroupSummaryForTesting(results, TimeSpan.FromMilliseconds(3), maxObjectGroups: 2);

        var profileEvent = Assert.Single(profile.Events, static profileEvent =>
            profileEvent.Name == "llvm_object_group_summary");
        Assert.Equal("2", profileEvent.Metadata["groups"]);
        Assert.Equal("2", profileEvent.Metadata["functions"]);
        Assert.Equal("300", profileEvent.Metadata["irBytes"]);
        Assert.Equal("1", profileEvent.Metadata["cacheHits"]);
        Assert.Equal("1", profileEvent.Metadata["stableCacheHits"]);
        Assert.Equal("1", profileEvent.Metadata["recomposedGroups"]);
    }

    [Fact]
    public void CoalesceObjectGroups_WithRestorePlan_PreservesRestoreAndRebuildPartitions()
    {
        var groups = new[]
        {
            CreateObjectGroup("g0", "name:restore_a", ["name:restore_a"]),
            CreateObjectGroup("g1", "name:rebuild_a", ["name:rebuild_a"]),
            CreateObjectGroup("g2", "name:restore_b", ["name:restore_b"]),
            CreateObjectGroup("g3", "name:rebuild_b", ["name:rebuild_b"])
        };
        var functionPlan = new LlvmFunctionFragmentRestorePlanSnapshot(
            "llvm-function-fragment-restore-plan-snapshot-v1",
            [
                CreateFunctionRestorePlan("name:restore_a", LlvmFunctionFragmentRestoreAction.Restore),
                CreateFunctionRestorePlan("name:rebuild_a", LlvmFunctionFragmentRestoreAction.Rebuild),
                CreateFunctionRestorePlan("name:restore_b", LlvmFunctionFragmentRestoreAction.Restore),
                CreateFunctionRestorePlan("name:rebuild_b", LlvmFunctionFragmentRestoreAction.Rebuild)
            ]);
        var restorePlan = LlvmObjectGroupRestorePlanSnapshot.Create(groups, functionPlan);

        var coalesced = LlvmCompiler.CoalesceObjectGroupRestorePlanForTesting(
            groups,
            maxObjectGroups: 2,
            restorePlan);

        Assert.Equal(2, coalesced.Groups.Count);
        Assert.Equal(1, coalesced.Count(LlvmObjectGroupRestoreAction.Restore));
        Assert.Equal(1, coalesced.Count(LlvmObjectGroupRestoreAction.Rebuild));
        var restored = Assert.Single(coalesced.Groups, static group => group.Action == LlvmObjectGroupRestoreAction.Restore);
        var rebuilt = Assert.Single(coalesced.Groups, static group => group.Action == LlvmObjectGroupRestoreAction.Rebuild);
        Assert.Equal(2, restored.RestoreFunctions);
        Assert.Equal(0, restored.RebuildFunctions);
        Assert.Equal(0, rebuilt.RestoreFunctions);
        Assert.Equal(2, rebuilt.RebuildFunctions);
    }

    [Fact]
    public void GetDefaultExecutableLinkerFlags_LinuxElf_DisablesPieByDefault()
    {
        var flags = LlvmCompiler.GetDefaultExecutableLinkerFlags(
            TargetInfo.X86_64Linux,
            NativeLinkMode.NonPieExecutable,
            linkerFlags: null);

        Assert.Equal(["-no-pie"], flags);
    }

    [Fact]
    public void GetDefaultExecutableLinkerFlags_LinuxElf_CanUsePlatformDefault()
    {
        var flags = LlvmCompiler.GetDefaultExecutableLinkerFlags(
            TargetInfo.X86_64Linux,
            NativeLinkMode.PlatformDefault,
            linkerFlags: null);

        Assert.Empty(flags);
    }

    [Fact]
    public void GetDefaultExecutableLinkerFlags_LinuxElf_CanRequestPie()
    {
        var flags = LlvmCompiler.GetDefaultExecutableLinkerFlags(
            TargetInfo.X86_64Linux,
            NativeLinkMode.PieExecutable,
            linkerFlags: null);

        Assert.Equal(["-pie"], flags);
    }

    [Theory]
    [InlineData("-pie")]
    [InlineData("-shared")]
    [InlineData("-static-pie")]
    [InlineData("-no-pie")]
    public void GetDefaultExecutableLinkerFlags_LinuxElf_RespectsExplicitImageMode(string linkerFlag)
    {
        var flags = LlvmCompiler.GetDefaultExecutableLinkerFlags(
            TargetInfo.X86_64Linux,
            NativeLinkMode.NonPieExecutable,
            [linkerFlag]);

        Assert.Empty(flags);
    }

    [Fact]
    public void GetDefaultExecutableLinkerFlags_WindowsCoff_DoesNotAddElfFlags()
    {
        var flags = LlvmCompiler.GetDefaultExecutableLinkerFlags(
            TargetInfo.X86_64Windows,
            NativeLinkMode.NonPieExecutable,
            linkerFlags: null);

        Assert.Empty(flags);
    }

    [Fact]
    public void GetDefaultObjectRelocationFlags_LinuxPie_UsesPositionIndependentCode()
    {
        var flags = LlvmCompiler.GetDefaultObjectRelocationFlags(
            TargetInfo.X86_64Linux,
            NativeLinkMode.PieExecutable);

        Assert.Equal(["-relocation-model=pic"], flags.LlcFlags);
        Assert.Equal(["-fPIE"], flags.ClangFlags);
    }

    [Fact]
    public void GetDefaultObjectRelocationFlags_LinuxNonPie_UsesStaticRelocationModel()
    {
        var flags = LlvmCompiler.GetDefaultObjectRelocationFlags(
            TargetInfo.X86_64Linux,
            NativeLinkMode.NonPieExecutable);

        Assert.Equal(["-relocation-model=static"], flags.LlcFlags);
        Assert.Empty(flags.ClangFlags);
    }

    [Fact]
    public void GetDefaultObjectRelocationFlags_PlatformDefault_DoesNotOverrideToolchain()
    {
        var flags = LlvmCompiler.GetDefaultObjectRelocationFlags(
            TargetInfo.X86_64Linux,
            NativeLinkMode.PlatformDefault);

        Assert.Empty(flags.LlcFlags);
        Assert.Empty(flags.ClangFlags);
    }

    private static bool ToolExists(string toolName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVar))
        {
            return false;
        }

        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            var candidate = Path.Combine(dir, toolName);
            if (File.Exists(candidate))
            {
                return true;
            }

            if (OperatingSystem.IsWindows() &&
                File.Exists(Path.Combine(dir, $"{toolName}.exe")))
            {
                return true;
            }
        }

        return false;
    }

    private static LlvmFunction CreateReturnConstantFunction(string name, long value)
    {
        return new LlvmFunction
        {
            Name = name,
            ReturnType = LlvmIntType.I64,
            Parameters = [],
            BasicBlocks =
            [
                new LlvmBasicBlock
                {
                    Label = "entry",
                    Instructions = [],
                    Terminator = new LlvmRet
                    {
                        Value = new LlvmConstant
                        {
                            Value = value,
                            Type = LlvmIntType.I64
                        }
                    }
                }
            ]
        };
    }

    private static LlvmFunction CreateLoadGlobalFunction(string name, string globalName)
    {
        var intType = LlvmIntType.I64;
        var load = new LlvmLoad
        {
            ResultName = "loaded",
            Pointer = new LlvmGlobal
            {
                Name = globalName,
                Type = LlvmPointerType.VoidPtr()
            },
            LoadType = intType
        };
        return new LlvmFunction
        {
            Name = name,
            ReturnType = intType,
            Parameters = [],
            BasicBlocks =
            [
                new LlvmBasicBlock
                {
                    Label = "entry",
                    Instructions = [load],
                    Terminator = new LlvmRet
                    {
                        Value = new LlvmInstructionRef
                        {
                            Instruction = load,
                            Type = intType
                        }
                    }
                }
            ]
        };
    }

    private static LlvmCodegenUnitPlanObjectGroup CreateObjectGroup(
        string groupKey,
        string rootFunctionKey,
        IReadOnlyList<string> memberFunctionKeys)
    {
        return new LlvmCodegenUnitPlanObjectGroup(
            groupKey,
            rootFunctionKey,
            memberFunctionKeys,
            ReferencedSymbols: [],
            ReferencedTypeNames: [],
            TotalIrBytes: memberFunctionKeys.Count * 100,
            FunctionCount: memberFunctionKeys.Count);
    }

    private static LlvmFunctionFragmentRestorePlanEntry CreateFunctionRestorePlan(
        string functionKey,
        LlvmFunctionFragmentRestoreAction action)
    {
        return new LlvmFunctionFragmentRestorePlanEntry(
            functionKey,
            action,
            PreviousBodyHash: action == LlvmFunctionFragmentRestoreAction.Restore ? "same" : "old",
            CurrentBodyHash: action == LlvmFunctionFragmentRestoreAction.Restore ? "same" : "new",
            IrBytes: 100,
            BasicBlockCount: 1,
            InstructionCount: 1,
            ParameterCount: 0);
    }

    private static string[] SplitLines(string text) =>
        text.Split(["\r\n", "\n"], StringSplitOptions.None);
}
