using Eidosc.CodeGen;
using Eidosc.CodeGen.Llvm;
using Xunit;

namespace Eidosc.Tests.Unit.Llvm;

public sealed class LlvmFunctionFingerprintTests
{
    [Fact]
    public void Compute_IsStableForEquivalentFunction()
    {
        var first = LlvmFunctionFingerprintBuilder.Compute(CreateFunction("add"));
        var second = LlvmFunctionFingerprintBuilder.Compute(CreateFunction("add"));

        Assert.Equal(first.BodyHash, second.BodyHash);
        Assert.Equal(first.FunctionKey, second.FunctionKey);
    }

    [Fact]
    public void Compute_ChangesWhenInstructionChanges()
    {
        var first = LlvmFunctionFingerprintBuilder.Compute(CreateFunction("add"));
        var second = LlvmFunctionFingerprintBuilder.Compute(CreateFunction("sub"));

        Assert.NotEqual(first.BodyHash, second.BodyHash);
    }

    [Fact]
    public void ComputeModule_SortsByFunctionKey()
    {
        var module = new LlvmModule
        {
            Functions =
            [
                CreateFunction("add", "z"),
                CreateFunction("add", "a")
            ]
        };

        var fingerprints = LlvmFunctionFingerprintBuilder.ComputeModule(module);

        Assert.Equal(["name:a", "name:z"], fingerprints.Select(static fingerprint => fingerprint.FunctionKey));
    }

    [Fact]
    public void Snapshot_FromModule_HasStableModuleFingerprint()
    {
        var first = LlvmFunctionFingerprintSnapshot.FromModule(new LlvmModule
        {
            Functions = [CreateFunction("add")]
        });
        var second = LlvmFunctionFingerprintSnapshot.FromModule(new LlvmModule
        {
            Functions = [CreateFunction("add")]
        });

        Assert.Equal("llvm-function-fingerprint-snapshot-v1", first.SchemaVersion);
        Assert.Equal(first.ModuleFingerprint, second.ModuleFingerprint);
        Assert.NotEmpty(first.ModuleFingerprint);
    }

    [Fact]
    public void Snapshot_ModuleFingerprintChangesWhenFunctionChanges()
    {
        var first = LlvmFunctionFingerprintSnapshot.FromModule(new LlvmModule
        {
            Functions = [CreateFunction("add")]
        });
        var second = LlvmFunctionFingerprintSnapshot.FromModule(new LlvmModule
        {
            Functions = [CreateFunction("sub")]
        });

        Assert.NotEqual(first.ModuleFingerprint, second.ModuleFingerprint);
    }

    [Fact]
    public void FragmentSnapshot_FromModule_StoresFunctionIrFragments()
    {
        var snapshot = LlvmFunctionFragmentSnapshot.FromModule(new LlvmModule
        {
            Functions = [CreateFunction("add")]
        });

        var fragment = Assert.Single(snapshot.Functions);
        Assert.Equal("llvm-function-fragment-snapshot-v1", snapshot.SchemaVersion);
        Assert.Equal("name:main", fragment.FunctionKey);
        Assert.Contains("define", fragment.IrFragment, StringComparison.Ordinal);
        Assert.Equal("declare i64 @main(i64)", fragment.DeclarationIr);
        Assert.Contains("add", fragment.IrFragment, StringComparison.Ordinal);
        Assert.Equal(LlvmFunctionFingerprintBuilder.Compute(CreateFunction("add")).BodyHash, fragment.BodyHash);
        Assert.NotEmpty(snapshot.ModuleFingerprint);
    }

    [Fact]
    public void FragmentRestorePlan_ClassifiesRestoreRebuildAndRemove()
    {
        var previous = new LlvmFunctionFragmentSnapshot(
            "llvm-function-fragment-snapshot-v1",
            [
                CreateFragment("name:same", "same"),
                CreateFragment("name:changed", "old"),
                CreateFragment("name:removed", "gone")
            ]);
        var current = new LlvmFunctionFragmentSnapshot(
            "llvm-function-fragment-snapshot-v1",
            [
                CreateFragment("name:same", "same"),
                CreateFragment("name:changed", "new"),
                CreateFragment("name:added", "fresh")
            ]);

        var plan = LlvmFunctionFragmentRestorePlanSnapshot.Create(previous, current);

        Assert.Equal("llvm-function-fragment-restore-plan-snapshot-v1", plan.SchemaVersion);
        Assert.Equal(1, plan.Count(LlvmFunctionFragmentRestoreAction.Restore));
        Assert.Equal(2, plan.Count(LlvmFunctionFragmentRestoreAction.Rebuild));
        Assert.Equal(1, plan.Count(LlvmFunctionFragmentRestoreAction.Remove));
        Assert.Contains(plan.Functions, static entry =>
            entry.FunctionKey == "name:same" &&
            entry.Action == LlvmFunctionFragmentRestoreAction.Restore);
        Assert.Contains(plan.Functions, static entry =>
            entry.FunctionKey == "name:changed" &&
            entry.Action == LlvmFunctionFragmentRestoreAction.Rebuild);
        Assert.Contains(plan.Functions, static entry =>
            entry.FunctionKey == "name:removed" &&
            entry.Action == LlvmFunctionFragmentRestoreAction.Remove);
    }

    [Fact]
    public void FragmentRestoreExecutor_ReusesPreviousMatchingFragments()
    {
        var previous = new LlvmFunctionFragmentSnapshot(
            "llvm-function-fragment-snapshot-v1",
            [
                CreateFragmentWithIr("name:same", "same", "define i64 @same() { ret i64 1 }"),
                CreateFragmentWithIr("name:changed", "old", "define i64 @changed() { ret i64 2 }"),
                CreateFragmentWithIr("name:removed", "gone", "define i64 @removed() { ret i64 3 }")
            ]);
        var current = new LlvmFunctionFragmentSnapshot(
            "llvm-function-fragment-snapshot-v1",
            [
                CreateFragmentWithIr("name:same", "same", "define i64 @same() { ret i64 99 }"),
                CreateFragmentWithIr("name:changed", "new", "define i64 @changed() { ret i64 4 }"),
                CreateFragmentWithIr("name:added", "fresh", "define i64 @added() { ret i64 5 }")
            ]);
        var plan = LlvmFunctionFragmentRestorePlanSnapshot.Create(previous, current);

        var execution = LlvmFunctionFragmentRestoreExecutor.Execute(previous, current, plan);

        Assert.Equal("llvm-function-fragment-restore-result-snapshot-v1", execution.Result.SchemaVersion);
        Assert.Equal(1, execution.Result.RestoredFragments);
        Assert.Equal(2, execution.Result.RebuiltFragments);
        Assert.Equal(1, execution.Result.RemovedFragments);
        Assert.Equal(0, execution.Result.FallbackRebuildFragments);
        Assert.False(execution.Result.Applied);
        Assert.Contains(execution.Fragments.Functions, static fragment =>
            fragment.FunctionKey == "name:same" &&
            fragment.IrFragment.Contains("ret i64 1", StringComparison.Ordinal));
        Assert.Contains(execution.Fragments.Functions, static fragment =>
            fragment.FunctionKey == "name:changed" &&
            fragment.IrFragment.Contains("ret i64 4", StringComparison.Ordinal));
        Assert.DoesNotContain(execution.Fragments.Functions, static fragment => fragment.FunctionKey == "name:removed");
    }

    [Fact]
    public void ObjectGroupRestorePlan_RestoresOnlyGroupsWhoseMembersAreAllRestorable()
    {
        var groups = new[]
        {
            new LlvmCodegenUnitPlanObjectGroup(
                "same-group",
                "name:same",
                ["name:same"],
                [],
                [],
                10,
                1),
            new LlvmCodegenUnitPlanObjectGroup(
                "mixed-group",
                "name:mixed",
                ["name:same", "name:changed"],
                [],
                [],
                20,
                2)
        };
        var functionPlan = new LlvmFunctionFragmentRestorePlanSnapshot(
            "llvm-function-fragment-restore-plan-snapshot-v1",
            [
                new LlvmFunctionFragmentRestorePlanEntry(
                    "name:same",
                    LlvmFunctionFragmentRestoreAction.Restore,
                    "same",
                    "same",
                    10,
                    1,
                    1,
                    0),
                new LlvmFunctionFragmentRestorePlanEntry(
                    "name:changed",
                    LlvmFunctionFragmentRestoreAction.Rebuild,
                    "old",
                    "new",
                    10,
                    1,
                    1,
                    0)
            ]);

        var plan = LlvmObjectGroupRestorePlanSnapshot.Create(groups, functionPlan);

        Assert.Equal("llvm-object-group-restore-plan-snapshot-v1", plan.SchemaVersion);
        Assert.Equal(1, plan.Count(LlvmObjectGroupRestoreAction.Restore));
        Assert.Equal(1, plan.Count(LlvmObjectGroupRestoreAction.Rebuild));
        var restored = Assert.Single(plan.Groups, static group => group.Action == LlvmObjectGroupRestoreAction.Restore);
        Assert.Equal("same-group", restored.GroupKey);
        var rebuilt = Assert.Single(plan.Groups, static group => group.Action == LlvmObjectGroupRestoreAction.Rebuild);
        Assert.Equal("mixed-group", rebuilt.GroupKey);
        Assert.Equal(1, rebuilt.RestoreFunctions);
        Assert.Equal(1, rebuilt.RebuildFunctions);
    }

    [Fact]
    public void EnvelopeSnapshot_FromModule_StoresNonFunctionModuleFragments()
    {
        var module = new LlvmModule
        {
            Name = "env",
            Functions = [CreateFunction("add")],
            LinkLibraries = ["m"],
            NativeSources = ["native.c"],
            Declarations =
            [
                new LlvmDeclaration
                {
                    Name = "puts",
                    Type = new LlvmFunctionType
                    {
                        ReturnType = LlvmIntType.I32,
                        ParameterTypes = [LlvmPointerType.VoidPtr()]
                    }
                }
            ]
        };

        var snapshot = LlvmModuleEnvelopeSnapshot.FromModule(
            module,
            "layout",
            "triple");

        Assert.Equal("llvm-module-envelope-snapshot-v1", snapshot.SchemaVersion);
        Assert.Contains("target datalayout = \"layout\"", snapshot.HeaderIr);
        Assert.Contains("target triple = \"triple\"", snapshot.HeaderIr);
        Assert.Contains(snapshot.DeclarationIr, static line => line.Contains("@puts", StringComparison.Ordinal));
        Assert.Equal(["m"], snapshot.LinkLibraries);
        Assert.Equal(["native.c"], snapshot.NativeSources);
        Assert.NotEmpty(snapshot.EnvelopeFingerprint);
        Assert.True(snapshot.FragmentLineCount >= snapshot.HeaderIr.Count);
    }

    [Fact]
    public void RecomposeModule_FromEnvelopeAndFragments_MatchesEmitterForStableFunctionOrder()
    {
        var module = new LlvmModule
        {
            Name = "recompose",
            Functions = [CreateFunction("add")],
            Declarations =
            [
                new LlvmDeclaration
                {
                    Name = "puts",
                    Type = new LlvmFunctionType
                    {
                        ReturnType = LlvmIntType.I32,
                        ParameterTypes = [LlvmPointerType.VoidPtr()]
                    }
                }
            ]
        };
        var envelope = LlvmModuleEnvelopeSnapshot.FromModule(module, "layout", "triple");
        var fragments = LlvmFunctionFragmentSnapshot.FromModule(module);

        var recomposed = LlvmFunctionFingerprintBuilder.RecomposeModule(envelope, fragments);
        var emitted = new LlvmEmitter().Emit(module, "layout", "triple");

        Assert.Equal("llvm-recomposed-module-snapshot-v1", recomposed.SchemaVersion);
        Assert.Equal(envelope.EnvelopeFingerprint, recomposed.EnvelopeFingerprint);
        Assert.Equal(fragments.ModuleFingerprint, recomposed.FunctionFragmentFingerprint);
        Assert.Equal(1, recomposed.FunctionCount);
        Assert.Equal(emitted, recomposed.IrText);
    }

    [Fact]
    public void CodegenUnitPlan_Create_SeparatesEnvelopeAndFunctionUnits()
    {
        var module = new LlvmModule
        {
            Name = "units",
            Functions = [CreateFunction("add")]
        };
        var envelope = LlvmModuleEnvelopeSnapshot.FromModule(module, "layout", "triple");
        var fragments = LlvmFunctionFragmentSnapshot.FromModule(module);
        var backendConfiguration = LlvmBackendConfiguration.Create(
            TargetInfo.X86_64Linux,
            optimizationLevel: 0,
            enableLto: false,
            NativeLinkMode.NonPieExecutable,
            extraCFlags: null,
            extraLinkFlags: null);

        var plan = LlvmCodegenUnitPlanSnapshot.Create(
            envelope,
            module,
            fragments,
            backendConfiguration);

        var unit = Assert.Single(plan.FunctionUnits);
        Assert.Equal("llvm-codegen-unit-plan-snapshot-v2", plan.SchemaVersion);
        Assert.Equal(envelope.EnvelopeFingerprint, plan.EnvelopeUnit.EnvelopeFingerprint);
        Assert.Equal("name:main", unit.FunctionKey);
        Assert.Equal(Assert.Single(fragments.Functions).BodyHash, unit.BodyHash);
        Assert.Equal(LlvmLinkage.External.ToString(), unit.Linkage);
        Assert.True(unit.IsObjectUnitEligible);
        Assert.Equal("", unit.ObjectUnitIneligibilityReason);
        Assert.NotEqual(plan.EnvelopeUnit.UnitCacheKey, unit.UnitCacheKey);
        Assert.NotEmpty(plan.PlanFingerprint);
    }

    [Fact]
    public void CodegenUnitPlan_KeepsObjectGroupKeyStableWhenUnreferencedFunctionIsAdded()
    {
        var firstModule = new LlvmModule
        {
            Name = "units",
            Functions = [CreateFunction("add", "main")]
        };
        var secondModule = new LlvmModule
        {
            Name = "units",
            Functions =
            [
                CreateFunction("add", "main"),
                CreateFunction("sub", "unused")
            ]
        };
        var backendConfiguration = LlvmBackendConfiguration.Create(
            TargetInfo.X86_64Linux,
            optimizationLevel: 0,
            enableLto: false,
            NativeLinkMode.NonPieExecutable,
            extraCFlags: null,
            extraLinkFlags: null);
        var firstPlan = LlvmCodegenUnitPlanSnapshot.Create(
            LlvmModuleEnvelopeSnapshot.FromModule(firstModule, "layout", "triple"),
            firstModule,
            LlvmFunctionFragmentSnapshot.FromModule(firstModule),
            backendConfiguration);
        var secondPlan = LlvmCodegenUnitPlanSnapshot.Create(
            LlvmModuleEnvelopeSnapshot.FromModule(secondModule, "layout", "triple"),
            secondModule,
            LlvmFunctionFragmentSnapshot.FromModule(secondModule),
            backendConfiguration);

        var firstGroup = Assert.Single(firstPlan.ObjectGroups, static group => group.RootFunctionKey == "name:main");
        var secondGroup = Assert.Single(secondPlan.ObjectGroups, static group => group.RootFunctionKey == "name:main");

        Assert.Equal(firstGroup.GroupKey, secondGroup.GroupKey);
    }

    [Fact]
    public void CodegenUnitPlan_CreateFromSelectedPlan_PreservesRestoredGroupKeysAndRekeysRebuiltGroups()
    {
        var previousModule = new LlvmModule
        {
            Name = "units",
            Functions =
            [
                CreateFunction("add", "keep"),
                CreateFunction("add", "change")
            ]
        };
        var selectedModule = new LlvmModule
        {
            Name = "units",
            Functions = [CreateFunction("sub", "change")]
        };
        var previousEnvelope = LlvmModuleEnvelopeSnapshot.FromModule(previousModule, "layout", "triple");
        var selectedEnvelope = LlvmModuleEnvelopeSnapshot.FromModule(selectedModule, "layout", "triple");
        var previousFragments = LlvmFunctionFragmentSnapshot.FromModule(previousModule);
        var selectedFragments = LlvmFunctionFragmentSnapshot.FromModule(selectedModule);
        var currentFragments = new LlvmFunctionFragmentSnapshot(
            LlvmFunctionFragmentSnapshot.CurrentSchemaVersion,
            previousFragments.Functions
                .Where(static fragment => fragment.FunctionKey == "name:keep")
                .Concat(selectedFragments.Functions)
                .ToArray());
        var backendConfiguration = LlvmBackendConfiguration.Create(
            TargetInfo.X86_64Linux,
            optimizationLevel: 0,
            enableLto: false,
            NativeLinkMode.NonPieExecutable,
            extraCFlags: null,
            extraLinkFlags: null);
        var previousPlan = LlvmCodegenUnitPlanSnapshot.Create(
            previousEnvelope,
            previousModule,
            previousFragments,
            backendConfiguration);
        var selectedPlan = LlvmCodegenUnitPlanSnapshot.Create(
            selectedEnvelope,
            selectedModule,
            selectedFragments,
            backendConfiguration);
        var functionPlan = LlvmFunctionFragmentRestorePlanSnapshot.Create(previousFragments, currentFragments);

        var mixed = LlvmCodegenUnitPlanSnapshot.CreateFromSelectedPlan(
            previousPlan,
            selectedPlan,
            currentFragments,
            functionPlan);

        var previousKeep = Assert.Single(previousPlan.ObjectGroups, static group => group.RootFunctionKey == "name:keep");
        var mixedKeep = Assert.Single(mixed.ObjectGroups, static group => group.RootFunctionKey == "name:keep");
        var previousChange = Assert.Single(previousPlan.ObjectGroups, static group => group.RootFunctionKey == "name:change");
        var selectedChange = Assert.Single(selectedPlan.ObjectGroups, static group => group.RootFunctionKey == "name:change");
        var mixedChange = Assert.Single(mixed.ObjectGroups, static group => group.RootFunctionKey == "name:change");

        Assert.Equal(previousKeep.GroupKey, mixedKeep.GroupKey);
        Assert.NotEqual(previousChange.GroupKey, mixedChange.GroupKey);
        Assert.Equal(selectedChange.GroupKey, mixedChange.GroupKey);
        Assert.Equal(
            Assert.Single(selectedFragments.Functions).BodyHash,
            Assert.Single(mixed.FunctionUnits, static unit => unit.FunctionKey == "name:change").BodyHash);
    }

    [Fact]
    public void CodegenUnitPlan_MarksPrivateFunctionsObjectIneligible()
    {
        var module = new LlvmModule
        {
            Name = "units",
            Functions = [CreateFunction("add", "helper", LlvmLinkage.Private)]
        };
        var envelope = LlvmModuleEnvelopeSnapshot.FromModule(module, "layout", "triple");
        var fragments = LlvmFunctionFragmentSnapshot.FromModule(module);
        var backendConfiguration = LlvmBackendConfiguration.Create(
            TargetInfo.X86_64Linux,
            optimizationLevel: 0,
            enableLto: false,
            NativeLinkMode.NonPieExecutable,
            extraCFlags: null,
            extraLinkFlags: null);

        var plan = LlvmCodegenUnitPlanSnapshot.Create(
            envelope,
            module,
            fragments,
            backendConfiguration);

        var unit = Assert.Single(plan.FunctionUnits);
        Assert.Equal(LlvmLinkage.Private.ToString(), unit.Linkage);
        Assert.False(unit.IsObjectUnitEligible);
        Assert.Equal("private-linkage", unit.ObjectUnitIneligibilityReason);
    }

    [Fact]
    public void CodegenUnitPlan_MarksExternalFunctionCallingPrivateHelperObjectIneligible()
    {
        var module = new LlvmModule
        {
            Name = "units",
            Functions =
            [
                CreateCallingFunction("main", "helper"),
                CreateFunction("add", "helper", LlvmLinkage.Private)
            ]
        };
        var envelope = LlvmModuleEnvelopeSnapshot.FromModule(module, "layout", "triple");
        var fragments = LlvmFunctionFragmentSnapshot.FromModule(module);
        var backendConfiguration = LlvmBackendConfiguration.Create(
            TargetInfo.X86_64Linux,
            optimizationLevel: 0,
            enableLto: false,
            NativeLinkMode.NonPieExecutable,
            extraCFlags: null,
            extraLinkFlags: null);

        var plan = LlvmCodegenUnitPlanSnapshot.Create(
            envelope,
            module,
            fragments,
            backendConfiguration);

        var main = Assert.Single(plan.FunctionUnits, static unit => unit.FunctionKey == "name:main");
        Assert.False(main.IsObjectUnitEligible);
        Assert.Equal("depends-on-non-object-unit:helper", main.ObjectUnitIneligibilityReason);
        Assert.Equal(["helper"], main.DirectCallees);

        var group = Assert.Single(plan.ObjectGroups, static unit => unit.RootFunctionKey == "name:main");
        Assert.Equal(["name:helper", "name:main"], group.MemberFunctionKeys);
        Assert.Equal(2, group.FunctionCount);
        Assert.True(group.TotalIrBytes > 0);
        Assert.NotEmpty(group.GroupKey);

        var recomposed = LlvmFunctionFingerprintBuilder.RecomposeObjectGroup(envelope, fragments, group);
        Assert.Equal("llvm-recomposed-object-group-snapshot-v1", recomposed.SchemaVersion);
        Assert.Equal(group.GroupKey, recomposed.GroupKey);
        Assert.Contains("@main", recomposed.IrText, StringComparison.Ordinal);
        Assert.Contains("@helper", recomposed.IrText, StringComparison.Ordinal);
        Assert.Equal(2, recomposed.FunctionCount);
        Assert.True(recomposed.IrBytes > group.TotalIrBytes);
    }

    [Fact]
    public void RecomposeObjectGroup_DeclaresNonMemberModuleFunctions()
    {
        var module = new LlvmModule
        {
            Name = "units",
            Functions =
            [
                CreateCallingFunction("main", "other"),
                CreateFunction("add", "other")
            ]
        };
        var envelope = LlvmModuleEnvelopeSnapshot.FromModule(module, "layout", "triple");
        var fragments = LlvmFunctionFragmentSnapshot.FromModule(module);
        var backendConfiguration = LlvmBackendConfiguration.Create(
            TargetInfo.X86_64Linux,
            optimizationLevel: 0,
            enableLto: false,
            NativeLinkMode.NonPieExecutable,
            extraCFlags: null,
            extraLinkFlags: null);
        var plan = LlvmCodegenUnitPlanSnapshot.Create(
            envelope,
            module,
            fragments,
            backendConfiguration);
        var group = Assert.Single(plan.ObjectGroups, static unit => unit.RootFunctionKey == "name:main");

        var recomposed = LlvmFunctionFingerprintBuilder.RecomposeObjectGroup(envelope, fragments, group);

        Assert.Contains("declare i64 @other(i64)", recomposed.IrText, StringComparison.Ordinal);
        Assert.DoesNotContain("define external i64 @other", recomposed.IrText, StringComparison.Ordinal);
    }

    [Fact]
    public void CodegenUnitPlan_TracksPrivateHelperReferencesPassedAsCallArguments()
    {
        var module = new LlvmModule
        {
            Name = "units",
            Functions =
            [
                CreateCallingFunctionWithFunctionArgument("main", "apply", "helper"),
                CreateFunction("add", "apply"),
                CreateFunction("add", "helper", LlvmLinkage.Private),
                CreateFunction("add", "unused", LlvmLinkage.Private)
            ]
        };
        var envelope = LlvmModuleEnvelopeSnapshot.FromModule(module, "layout", "triple");
        var fragments = LlvmFunctionFragmentSnapshot.FromModule(module);
        var backendConfiguration = LlvmBackendConfiguration.Create(
            TargetInfo.X86_64Linux,
            optimizationLevel: 0,
            enableLto: false,
            NativeLinkMode.NonPieExecutable,
            extraCFlags: null,
            extraLinkFlags: null);

        var plan = LlvmCodegenUnitPlanSnapshot.Create(
            envelope,
            module,
            fragments,
            backendConfiguration);

        var main = Assert.Single(plan.FunctionUnits, static unit => unit.FunctionKey == "name:main");
        Assert.Equal(["apply", "helper"], main.DirectCallees);

        var group = Assert.Single(plan.ObjectGroups, static unit => unit.RootFunctionKey == "name:main");
        Assert.Equal(["name:helper", "name:main"], group.MemberFunctionKeys);

        var recomposed = LlvmFunctionFingerprintBuilder.RecomposeObjectGroup(envelope, fragments, group);
        Assert.Contains("define private i64 @helper", recomposed.IrText, StringComparison.Ordinal);
        Assert.Contains("declare i64 @apply", recomposed.IrText, StringComparison.Ordinal);
        Assert.DoesNotContain("define private i64 @unused", recomposed.IrText, StringComparison.Ordinal);
    }

    private static LlvmFunction CreateFunction(string op, string name = "main", LlvmLinkage linkage = LlvmLinkage.External)
    {
        var intType = LlvmIntType.I64;
        var function = new LlvmFunction
        {
            Name = name,
            Linkage = linkage,
            ReturnType = intType,
            Parameters =
            [
                new LlvmParameter { Name = "x", Type = intType }
            ],
            BasicBlocks =
            [
                new LlvmBasicBlock
                {
                    Label = "entry",
                    Instructions =
                    [
                        new LlvmBinOp
                        {
                            ResultName = "tmp",
                            Op = op,
                            Left = new LlvmLocal { Name = "x", Type = intType },
                            Right = new LlvmConstant { Value = 1, Type = intType },
                            ResultType = intType
                        }
                    ],
                    Terminator = new LlvmRet
                    {
                        Value = new LlvmInstructionRef
                        {
                            Instruction = new LlvmBinOp { ResultName = "tmp", ResultType = intType },
                            Type = intType
                        }
                    }
                }
            ]
        };
        return function;
    }

    private static LlvmFunctionFragment CreateFragment(string functionKey, string bodyHash) =>
        new(
            functionKey,
            bodyHash,
            $"define i64 @{functionKey.Replace("name:", "", StringComparison.Ordinal)}() {{ ret i64 0 }}",
            $"declare i64 @{functionKey.Replace("name:", "", StringComparison.Ordinal)}()",
            LlvmLinkage.External.ToString(),
            1,
            1,
            0);

    private static LlvmFunctionFragment CreateFragmentWithIr(string functionKey, string bodyHash, string ir) =>
        new(
            functionKey,
            bodyHash,
            ir,
            $"declare i64 @{functionKey.Replace("name:", "", StringComparison.Ordinal)}()",
            LlvmLinkage.External.ToString(),
            1,
            1,
            0);

    private static LlvmFunction CreateCallingFunction(string name, string calleeName)
    {
        var intType = LlvmIntType.I64;
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
                    Instructions =
                    [
                        new LlvmCall
                        {
                            ResultName = "called",
                            Function = new LlvmGlobal
                            {
                                Name = calleeName,
                                Type = new LlvmFunctionType
                                {
                                    ReturnType = intType,
                                    ParameterTypes = []
                                }
                            },
                            Arguments = [],
                            ReturnType = intType
                        }
                    ],
                    Terminator = new LlvmRet
                    {
                        Value = new LlvmInstructionRef
                        {
                            Instruction = new LlvmCall { ResultName = "called", ReturnType = intType },
                            Type = intType
                        }
                    }
                }
            ]
        };
    }

    private static LlvmFunction CreateCallingFunctionWithFunctionArgument(string name, string calleeName, string argumentFunctionName)
    {
        var intType = LlvmIntType.I64;
        var functionType = new LlvmFunctionType
        {
            ReturnType = intType,
            ParameterTypes = [intType]
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
                    Instructions =
                    [
                        new LlvmCall
                        {
                            ResultName = "called",
                            Function = new LlvmGlobal
                            {
                                Name = calleeName,
                                Type = new LlvmFunctionType
                                {
                                    ReturnType = intType,
                                    ParameterTypes = [LlvmPointerType.VoidPtr()]
                                }
                            },
                            Arguments =
                            [
                                new LlvmGlobal
                                {
                                    Name = argumentFunctionName,
                                    Type = functionType
                                }
                            ],
                            ReturnType = intType
                        }
                    ],
                    Terminator = new LlvmRet
                    {
                        Value = new LlvmInstructionRef
                        {
                            Instruction = new LlvmCall { ResultName = "called", ReturnType = intType },
                            Type = intType
                        }
                    }
                }
            ]
        };
    }
}
