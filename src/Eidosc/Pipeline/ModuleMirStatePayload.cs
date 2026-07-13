using Eidosc.Mir;
using Eidosc.Symbols;
using Eidosc.Types;

namespace Eidosc.Pipeline;

public sealed record ModuleMirStateArtifactPayload(
    string SchemaVersion,
    string ModuleKey,
    ProjectModuleTypedSemanticNode TypedSemantic,
    bool IsModuleLocal,
    int ModuleLocalFunctionCount,
    ModuleMirStatePayload MirState,
    string PayloadHash)
{
    public const string CurrentSchemaVersion = "module-mir-state-artifact-payload-v7";

    public static ModuleMirStateArtifactPayload Create(
        string moduleKey,
        ProjectModuleTypedSemanticSnapshot typedSemanticSnapshot,
        MirModule mirModule)
    {
        var typedSemantic = typedSemanticSnapshot.Nodes.FirstOrDefault(node =>
            string.Equals(node.ModuleKey, moduleKey, StringComparison.Ordinal));
        if (typedSemantic == null)
        {
            throw new ArgumentException($"Module '{moduleKey}' is missing from the typed semantic snapshot.", nameof(moduleKey));
        }

        var moduleLocal = TryCreateModuleLocalMirModule(
            mirModule,
            typedSemantic,
            out var moduleSlice);
        var mirState = ModuleMirStatePayload.Create(moduleLocal ? moduleSlice : null);
        var payload = new ModuleMirStateArtifactPayload(
            CurrentSchemaVersion,
            moduleKey,
            typedSemantic,
            moduleLocal,
            moduleLocal ? moduleSlice.Functions.Count : 0,
            mirState,
            "");
        return payload with { PayloadHash = ComputeHash(payload) };
    }

    public bool HasValidPayloadHash() =>
        !string.IsNullOrWhiteSpace(PayloadHash) &&
        string.Equals(PayloadHash, ComputeHash(this), StringComparison.Ordinal) &&
        MirState.HasValidHash();

    private static string ComputeHash(ModuleMirStateArtifactPayload payload) =>
        ModuleArtifactHash.ComputeJsonHash(payload with { PayloadHash = "" });

    private static bool TryCreateModuleLocalMirModule(
        MirModule module,
        ProjectModuleTypedSemanticNode typedSemantic,
        out MirModule moduleSlice)
    {
        moduleSlice = module;
        var moduleIdentityKeys = CreateModuleIdentityKeys(typedSemantic.ModuleKey)
            .Select(ParseModuleIdentityKey)
            .ToArray();
        var declarationSymbolIds = typedSemantic.Declarations
            .Select(static declaration => declaration.SymbolId)
            .Where(static id => id > 0)
            .ToHashSet();

        var functions = module.Functions
            .Where(function => BelongsToModule(function, typedSemantic.ModuleKey, moduleIdentityKeys, declarationSymbolIds))
            .ToList();
        if (functions.Count == 0)
        {
            return false;
        }

        moduleSlice = new MirModule
        {
            Name = module.Name,
            PackageAlias = module.PackageAlias,
            PackageInstanceKey = module.PackageInstanceKey,
            Path = module.Path.ToList(),
            Span = module.Span,
            Functions = functions,
            DynamicTypeKeys = new Dictionary<int, string>(module.DynamicTypeKeys),
            TypeDescriptors = new Dictionary<int, TypeDescriptor>(module.TypeDescriptors),
            LinkLibraries = module.LinkLibraries.ToList(),
            CStructAccessors = new Dictionary<string, CStructAccessorInfo>(module.CStructAccessors),
            ConstructorLayouts = module.ConstructorLayouts.ToDictionary(
                static entry => entry.Key,
                static entry => entry.Value.ToList()),
            TraitImpls = module.TraitImpls.ToList(),
            TraitInfos = module.TraitInfos.ToList(),
            TypeAliases = module.TypeAliases.ToList(),
            TypeConstructors = module.TypeConstructors.ToList(),
            SpecializationFailures = module.SpecializationFailures.ToList()
        };
        return true;
    }

    private static bool BelongsToModule(
        MirFunc function,
        string moduleKey,
        IReadOnlyList<(string PackageInstanceKey, string ModulePath)> moduleIdentityKeys,
        IReadOnlySet<int> declarationSymbolIds)
    {
        if (!string.IsNullOrWhiteSpace(function.FunctionId.Module))
        {
            return ModulePathsEqual(function.FunctionId.Module, moduleKey);
        }

        if (!string.IsNullOrWhiteSpace(function.FunctionId.ModuleIdentityKey))
        {
            var functionModule = ParseModuleIdentityKey(function.FunctionId.ModuleIdentityKey);
            if (moduleIdentityKeys.Any(expected =>
                    ModulePathsEqual(functionModule.ModulePath, expected.ModulePath)))
            {
                return true;
            }

            return false;
        }

        return function.SymbolId.IsValid &&
               declarationSymbolIds.Contains(function.SymbolId.Value);
    }

    private static IEnumerable<string> CreateModuleIdentityKeys(string moduleKey)
    {
        var packageAlias = default(string?);
        var modulePathText = moduleKey;
        var packageSeparatorIndex = moduleKey.IndexOf(WellKnownStrings.Separators.Path, StringComparison.Ordinal);
        if (packageSeparatorIndex >= 0)
        {
            packageAlias = moduleKey[..packageSeparatorIndex];
            modulePathText = moduleKey[(packageSeparatorIndex + WellKnownStrings.Separators.Path.Length)..];
        }

        var modulePath = modulePathText
            .Split([WellKnownStrings.Operators.Divide], StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(static segment => segment.Split(['.'], StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
        yield return ModuleRegistry.ToModuleIdentityKey(packageAlias, null, modulePath);

        if (packageAlias != null)
        {
            yield return ModuleRegistry.ToModuleIdentityKey(null, null, modulePath);
        }
    }

    private static string NormalizeModulePath(string modulePath)
    {
        if (string.IsNullOrWhiteSpace(modulePath))
        {
            return "";
        }

        var normalized = modulePath
            .Replace('\\', '/')
            .Replace(WellKnownStrings.Separators.Path, WellKnownStrings.Operators.Divide, StringComparison.Ordinal);
        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", WellKnownStrings.Operators.Divide, StringComparison.Ordinal);
        }

        return normalized.Trim('/');
    }

    private static bool ModulePathsEqual(string left, string right) =>
        string.Equals(NormalizeModulePath(left), NormalizeModulePath(right), StringComparison.OrdinalIgnoreCase);

    private static (string PackageInstanceKey, string ModulePath) ParseModuleIdentityKey(string moduleIdentityKey)
    {
        var pathSeparator = moduleIdentityKey.IndexOf(WellKnownStrings.Separators.Path, StringComparison.Ordinal);
        if (pathSeparator < 0)
        {
            return (ModuleIdentity.CurrentPackageInstanceKey, moduleIdentityKey);
        }

        var packagePart = moduleIdentityKey[..pathSeparator];
        var instanceSeparator = packagePart.IndexOf('@', StringComparison.Ordinal);
        var packageInstanceKey = instanceSeparator >= 0
            ? packagePart[(instanceSeparator + 1)..]
            : ModuleIdentity.CurrentPackageInstanceKey;
        var modulePath = moduleIdentityKey[(pathSeparator + WellKnownStrings.Separators.Path.Length)..];
        return (
            string.IsNullOrWhiteSpace(packageInstanceKey) ? ModuleIdentity.CurrentPackageInstanceKey : packageInstanceKey,
            modulePath);
    }
}

public sealed record ModuleMirStatePayload(
    string SchemaVersion,
    MirStateModulePayload? Module,
    int UnsupportedNodeCount,
    IReadOnlyList<string> UnsupportedNodeKinds,
    string ModuleFingerprint,
    IReadOnlyList<MirFunctionFingerprint> FunctionFingerprints,
    string Hash)
{
    public const string CurrentSchemaVersion = "module-mir-state-payload-v7";

    public bool IsRestorable => Module != null &&
                                UnsupportedNodeCount == 0 &&
                                UnsupportedNodeKinds.Count == 0 &&
                                HasValidHash();

    public static ModuleMirStatePayload Create(MirModule? module)
    {
        if (module == null)
        {
            var empty = new ModuleMirStatePayload(CurrentSchemaVersion, null, 0, [], "", [], "");
            return empty with { Hash = ComputeHash(empty) };
        }

        var context = new MirStatePayloadCreateContext();
        var modulePayload = MirStateModulePayload.Create(module, context);
        var fingerprintSnapshot = MirFunctionFingerprintSnapshot.FromModule(module);
        var payload = new ModuleMirStatePayload(
            CurrentSchemaVersion,
            modulePayload,
            context.UnsupportedNodeCount,
            context.UnsupportedNodeKinds.Order(StringComparer.Ordinal).ToArray(),
            CompilationPipeline.CreateMirModuleFingerprint(module),
            fingerprintSnapshot.Functions,
            "");

        return payload with { Hash = ComputeHash(payload) };
    }

    public bool HasValidHash() =>
        !string.IsNullOrWhiteSpace(Hash) &&
        string.Equals(Hash, ComputeHash(this), StringComparison.Ordinal);

    public bool TryRestore(out MirModule module)
    {
        module = new MirModule();
        if (SchemaVersion != CurrentSchemaVersion ||
            Module == null ||
            UnsupportedNodeCount != 0 ||
            UnsupportedNodeKinds.Count != 0 ||
            !HasValidHash())
        {
            return false;
        }

        if (!Module.TryRestore(out module))
        {
            return false;
        }

        return string.Equals(ModuleFingerprint, CompilationPipeline.CreateMirModuleFingerprint(module), StringComparison.Ordinal);
    }

    private static string ComputeHash(ModuleMirStatePayload payload) =>
        ModuleArtifactHash.ComputeJsonHash(payload with { Hash = "" });
}

public sealed record MirStateModulePayload(
    string Name,
    string? PackageAlias,
    string? PackageInstanceKey,
    IReadOnlyList<string> Path,
    SourceSpanPayload Span,
    IReadOnlyList<MirStateFunctionPayload> Functions,
    IReadOnlyList<DynamicTypeKeyPayload> DynamicTypeKeys,
    IReadOnlyList<TypeDescriptorEntryPayload> TypeDescriptors,
    IReadOnlyList<string> LinkLibraries,
    IReadOnlyList<CStructAccessorEntryPayload> CStructAccessors,
    IReadOnlyList<ConstructorLayoutGroupPayload> ConstructorLayouts,
    IReadOnlyList<MirStateImplSymbolPayload> TraitImpls,
    IReadOnlyList<MirStateTraitInfoPayload> TraitInfos,
    IReadOnlyList<MirStateTypeAliasInfoPayload> TypeAliases,
    IReadOnlyList<MirStateTypeConstructorInfoPayload> TypeConstructors,
    IReadOnlyList<MirStateSpecializationFailurePayload> SpecializationFailures)
{
    public static MirStateModulePayload Create(MirModule module, MirStatePayloadCreateContext context) =>
        new(
            module.Name,
            module.PackageAlias,
            module.PackageInstanceKey,
            module.Path.ToArray(),
            SourceSpanPayload.Create(module.Span),
            module.Functions.Select(function => MirStateFunctionPayload.Create(function, context)).ToArray(),
            module.DynamicTypeKeys
                .OrderBy(static entry => entry.Key)
                .Select(static entry => new DynamicTypeKeyPayload(entry.Key, entry.Value))
                .ToArray(),
            module.TypeDescriptors
                .OrderBy(static entry => entry.Key)
                .Select(static entry => new TypeDescriptorEntryPayload(entry.Key, TypeDescriptorPayload.Create(entry.Value)))
                .ToArray(),
            module.LinkLibraries.ToArray(),
            module.CStructAccessors
                .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                .Select(static entry => new CStructAccessorEntryPayload(entry.Key, CStructAccessorPayload.Create(entry.Value)))
                .ToArray(),
            module.ConstructorLayouts
                .OrderBy(static entry => entry.Key)
                .Select(static entry => new ConstructorLayoutGroupPayload(
                    entry.Key,
                    entry.Value
                        .OrderBy(static layout => layout.ConstructorName, StringComparer.Ordinal)
                        .ThenBy(static layout => layout.TagValue)
                        .Select(ConstructorTypeLayoutPayload.Create)
                        .ToArray()))
                .ToArray(),
            module.TraitImpls.Select(MirStateImplSymbolPayload.Create).ToArray(),
            module.TraitInfos.Select(MirStateTraitInfoPayload.Create).ToArray(),
            module.TypeAliases.Select(MirStateTypeAliasInfoPayload.Create).ToArray(),
            module.TypeConstructors.Select(MirStateTypeConstructorInfoPayload.Create).ToArray(),
            module.SpecializationFailures.Select(MirStateSpecializationFailurePayload.Create).ToArray());

    public bool TryRestore(out MirModule module)
    {
        module = new MirModule
        {
            Name = Name,
            PackageAlias = PackageAlias,
            PackageInstanceKey = PackageInstanceKey,
            Path = Path.ToList(),
            Span = Span.ToSourceSpan(),
            Functions = Functions.Select(static function => function.Restore()).ToList(),
            DynamicTypeKeys = DynamicTypeKeys.ToDictionary(static entry => entry.TypeId, static entry => entry.TypeKey),
            LinkLibraries = LinkLibraries.ToList(),
            CStructAccessors = CStructAccessors.ToDictionary(static entry => entry.Name, static entry => entry.Accessor.Restore()),
            ConstructorLayouts = ConstructorLayouts.ToDictionary(
                static group => group.TypeId,
                static group => group.Layouts.Select(static layout => layout.Restore()).ToList()),
            TraitInfos = TraitInfos.Select(static trait => trait.Restore()).ToList(),
            TypeAliases = TypeAliases.Select(static alias => alias.Restore()).ToList(),
            TypeConstructors = TypeConstructors.Select(static constructor => constructor.Restore()).ToList(),
            SpecializationFailures = SpecializationFailures.Select(static failure => failure.Restore()).ToList()
        };

        var typeDescriptors = new Dictionary<int, TypeDescriptor>();
        foreach (var entry in TypeDescriptors)
        {
            if (!entry.Descriptor.TryRestore(out var descriptor))
            {
                return false;
            }

            typeDescriptors[entry.TypeId] = descriptor;
        }

        var traitImpls = new List<ImplSymbol>(TraitImpls.Count);
        foreach (var impl in TraitImpls)
        {
            if (!impl.TryRestore(out var restored))
            {
                return false;
            }

            traitImpls.Add(restored);
        }

        module.TypeDescriptors.Clear();
        foreach (var (typeId, descriptor) in typeDescriptors)
        {
            module.TypeDescriptors[typeId] = descriptor;
        }

        module.TraitImpls.Clear();
        module.TraitImpls.AddRange(traitImpls);
        return true;
    }
}

public sealed record MirStateFunctionPayload(
    string Name,
    string SourceName,
    IReadOnlyList<MirStateLocalPayload> Locals,
    IReadOnlyList<MirStateBlockPayload> BasicBlocks,
    int EntryBlockId,
    int ReturnType,
    int GenericParameterCount,
    IReadOnlyList<int> GenericTypeParameterIds,
    bool IsRuntimeWordAbi,
    bool IsExternal,
    string? ExternalSymbolName,
    string? ExternalLibrary,
    string? IntrinsicName,
    string BuiltinIntrinsicRole,
    SourceSpanPayload Span,
    int SymbolId,
    MirStateFunctionIdPayload FunctionId,
    bool IsEntry,
    string TraitInvokeHelper,
    int TraitInvokeHelperTraitId)
{
    public static MirStateFunctionPayload Create(MirFunc function, MirStatePayloadCreateContext context) =>
        new(
            function.Name,
            function.SourceName,
            function.Locals.Select(MirStateLocalPayload.Create).ToArray(),
            function.BasicBlocks.Select(block => MirStateBlockPayload.Create(block, context)).ToArray(),
            function.EntryBlockId.Value,
            function.ReturnType.Value,
            function.GenericParameterCount,
            function.GenericTypeParameterIds.Select(static id => id.Value).ToArray(),
            function.IsRuntimeWordAbi,
            function.IsExternal,
            function.ExternalSymbolName,
            function.ExternalLibrary,
            function.IntrinsicName,
            function.BuiltinIntrinsicRole.ToString(),
            SourceSpanPayload.Create(function.Span),
            function.SymbolId.Value,
            MirStateFunctionIdPayload.Create(function.FunctionId),
            function.IsEntry,
            function.TraitInvokeHelper.ToString(),
            function.TraitInvokeHelperTraitId.Value)
        {
            GenericParameters = function.GenericParameters
                .Select(MirStateGenericParameterPayload.Create)
                .ToArray()
        };

    public IReadOnlyList<MirStateGenericParameterPayload> GenericParameters { get; init; } = [];

    public MirFunc Restore() =>
        new()
        {
            Name = Name,
            SourceName = SourceName,
            Locals = Locals.Select(static local => local.Restore()).ToList(),
            BasicBlocks = BasicBlocks.Select(static block => block.Restore()).ToList(),
            EntryBlockId = new BlockId { Value = EntryBlockId },
            ReturnType = new TypeId(ReturnType),
            GenericParameterCount = GenericParameterCount,
            GenericParameters = GenericParameters.Select(static parameter => parameter.Restore()).ToList(),
            GenericTypeParameterIds = GenericTypeParameterIds.Select(static id => new TypeId(id)).ToList(),
            IsRuntimeWordAbi = IsRuntimeWordAbi,
            IsExternal = IsExternal,
            ExternalSymbolName = ExternalSymbolName,
            ExternalLibrary = ExternalLibrary,
            IntrinsicName = IntrinsicName,
            BuiltinIntrinsicRole = Enum.Parse<BuiltinIntrinsicRole>(BuiltinIntrinsicRole),
            Span = Span.ToSourceSpan(),
            SymbolId = new SymbolId(SymbolId),
            FunctionId = FunctionId.Restore(),
            IsEntry = IsEntry,
            TraitInvokeHelper = Enum.Parse<TraitInvokeHelperKind>(TraitInvokeHelper),
            TraitInvokeHelperTraitId = new SymbolId(TraitInvokeHelperTraitId)
        };
}

public sealed record MirStateGenericParameterPayload(
    int ParameterIndex,
    int SymbolId,
    string Name,
    string ParameterKind,
    int TypeId)
{
    public static MirStateGenericParameterPayload Create(MirGenericParameter parameter) =>
        new(
            parameter.ParameterIndex,
            parameter.SymbolId.Value,
            parameter.Name,
            parameter.ParameterKind.ToString(),
            parameter.TypeId.Value);

    public MirGenericParameter Restore() =>
        new()
        {
            ParameterIndex = ParameterIndex,
            SymbolId = new SymbolId(SymbolId),
            Name = Name,
            ParameterKind = Enum.Parse<GenericParameterKind>(ParameterKind),
            TypeId = new TypeId(TypeId)
        };
}

public sealed record MirStateLocalPayload(
    int Id,
    string Name,
    int TypeId,
    bool IsMutable,
    bool IsParameter,
    string BindingMode,
    SourceSpanPayload Span)
{
    public static MirStateLocalPayload Create(MirLocal local) =>
        new(
            local.Id.Value,
            local.Name,
            local.TypeId.Value,
            local.IsMutable,
            local.IsParameter,
            local.BindingMode.ToString(),
            SourceSpanPayload.Create(local.Span));

    public MirLocal Restore() =>
        new()
        {
            Id = new LocalId { Value = Id },
            Name = Name,
            TypeId = new TypeId(TypeId),
            IsMutable = IsMutable,
            IsParameter = IsParameter,
            BindingMode = Enum.Parse<PatternBindingMode>(BindingMode),
            Span = Span.ToSourceSpan()
        };
}

public sealed record MirStateBlockPayload(
    int Id,
    IReadOnlyList<MirStateInstructionPayload> Instructions,
    MirStateTerminatorPayload? Terminator,
    SourceSpanPayload Span,
    bool IsEntry)
{
    public static MirStateBlockPayload Create(MirBasicBlock block, MirStatePayloadCreateContext context) =>
        new(
            block.Id.Value,
            block.Instructions.Select(instruction => MirStateInstructionPayload.Create(instruction, context)).ToArray(),
            block.Terminator == null ? null : MirStateTerminatorPayload.Create(block.Terminator, context),
            SourceSpanPayload.Create(block.Span),
            block.IsEntry);

    public MirBasicBlock Restore() =>
        new()
        {
            Id = new BlockId { Value = Id },
            Instructions = Instructions.Select(static instruction => instruction.Restore()).ToList(),
            Terminator = Terminator?.Restore(),
            Span = Span.ToSourceSpan(),
            IsEntry = IsEntry
        };
}

public sealed record MirStateInstructionPayload(
    string Kind,
    SourceSpanPayload Span,
    MirStateOperandPayload? Target = null,
    MirStateOperandPayload? Source = null,
    MirStateOperandPayload? Function = null,
    IReadOnlyList<MirStateOperandPayload>? Arguments = null,
    bool IsTailCall = false,
    string? Operator = null,
    MirStateOperandPayload? Left = null,
    MirStateOperandPayload? Right = null,
    MirStateOperandPayload? Operand = null,
    bool IsMutableBorrow = false,
    bool CreatesBorrowAlias = true,
    MirStateOperandPayload? Value = null,
    int TypeId = 0)
{
    public static MirStateInstructionPayload Create(MirInstruction instruction, MirStatePayloadCreateContext context)
    {
        context.ObserveInstruction(instruction);
        var span = SourceSpanPayload.Create(instruction.Span);
        return instruction switch
        {
            MirAssign assign => new MirStateInstructionPayload(
                nameof(MirAssign),
                span,
                Target: MirStateOperandPayload.Create(assign.Target, context),
                Source: MirStateOperandPayload.Create(assign.Source, context)),
            MirCall call => new MirStateInstructionPayload(
                nameof(MirCall),
                span,
                Target: call.Target == null ? null : MirStateOperandPayload.Create(call.Target, context),
                Function: MirStateOperandPayload.Create(call.Function, context),
                Arguments: call.Arguments.Select(argument => MirStateOperandPayload.Create(argument, context)).ToArray(),
                IsTailCall: call.IsTailCall),
            MirBinOp binOp => new MirStateInstructionPayload(
                nameof(MirBinOp),
                span,
                Target: MirStateOperandPayload.Create(binOp.Target, context),
                Operator: binOp.Operator.ToString(),
                Left: MirStateOperandPayload.Create(binOp.Left, context),
                Right: MirStateOperandPayload.Create(binOp.Right, context)),
            MirUnaryOp unaryOp => new MirStateInstructionPayload(
                nameof(MirUnaryOp),
                span,
                Target: MirStateOperandPayload.Create(unaryOp.Target, context),
                Operator: unaryOp.Operator.ToString(),
                Operand: MirStateOperandPayload.Create(unaryOp.Operand, context)),
            MirLoad load => new MirStateInstructionPayload(
                nameof(MirLoad),
                span,
                Target: MirStateOperandPayload.Create(load.Target, context),
                Source: MirStateOperandPayload.Create(load.Source, context),
                IsMutableBorrow: load.IsMutableBorrow,
                CreatesBorrowAlias: load.CreatesBorrowAlias),
            MirStore store => new MirStateInstructionPayload(
                nameof(MirStore),
                span,
                Target: MirStateOperandPayload.Create(store.Target, context),
                Value: MirStateOperandPayload.Create(store.Value, context)),
            MirDrop drop => new MirStateInstructionPayload(
                nameof(MirDrop),
                span,
                Value: MirStateOperandPayload.Create(drop.Value, context)),
            MirCopy copy => new MirStateInstructionPayload(
                nameof(MirCopy),
                span,
                Target: MirStateOperandPayload.Create(copy.Target, context),
                Source: MirStateOperandPayload.Create(copy.Source, context)),
            MirMove move => new MirStateInstructionPayload(
                nameof(MirMove),
                span,
                Target: MirStateOperandPayload.Create(move.Target, context),
                Source: MirStateOperandPayload.Create(move.Source, context)),
            MirAlloc alloc => new MirStateInstructionPayload(
                nameof(MirAlloc),
                span,
                Target: MirStateOperandPayload.Create(alloc.Target, context),
                TypeId: alloc.TypeId.Value),
            _ => UnsupportedInstruction(instruction, context)
        };
    }

    public MirInstruction Restore() =>
        Kind switch
        {
            nameof(MirAssign) => new MirAssign { Span = Span.ToSourceSpan(), Target = RestorePlace(Target), Source = RestoreOperand(Source) },
            nameof(MirCall) => new MirCall { Span = Span.ToSourceSpan(), Target = Target == null ? null : RestorePlace(Target), Function = RestoreOperand(Function), Arguments = RestoreOperands(Arguments), IsTailCall = IsTailCall },
            nameof(MirBinOp) => new MirBinOp { Span = Span.ToSourceSpan(), Target = RestoreOperand(Target), Operator = Enum.Parse<BinaryOp>(Operator ?? ""), Left = RestoreOperand(Left), Right = RestoreOperand(Right) },
            nameof(MirUnaryOp) => new MirUnaryOp { Span = Span.ToSourceSpan(), Target = RestoreOperand(Target), Operator = Enum.Parse<UnaryOp>(Operator ?? ""), Operand = RestoreOperand(Operand) },
            nameof(MirLoad) => new MirLoad { Span = Span.ToSourceSpan(), Target = RestorePlace(Target), Source = RestoreOperand(Source), IsMutableBorrow = IsMutableBorrow, CreatesBorrowAlias = CreatesBorrowAlias },
            nameof(MirStore) => new MirStore { Span = Span.ToSourceSpan(), Target = RestorePlace(Target), Value = RestoreOperand(Value) },
            nameof(MirDrop) => new MirDrop { Span = Span.ToSourceSpan(), Value = RestoreOperand(Value) },
            nameof(MirCopy) => new MirCopy { Span = Span.ToSourceSpan(), Target = RestorePlace(Target), Source = RestorePlace(Source) },
            nameof(MirMove) => new MirMove { Span = Span.ToSourceSpan(), Target = RestorePlace(Target), Source = RestorePlace(Source) },
            nameof(MirAlloc) => new MirAlloc { Span = Span.ToSourceSpan(), Target = RestorePlace(Target), TypeId = new TypeId(TypeId) },
            _ => throw new InvalidOperationException($"Unsupported MIR instruction payload '{Kind}'.")
        };

    private static MirStateInstructionPayload UnsupportedInstruction(MirInstruction instruction, MirStatePayloadCreateContext context)
    {
        context.AddUnsupported(instruction);
        return new MirStateInstructionPayload(instruction.GetType().Name, SourceSpanPayload.Create(instruction.Span));
    }

    private static MirOperand RestoreOperand(MirStateOperandPayload? operand) =>
        operand?.Restore() ?? throw new InvalidOperationException("Missing MIR operand payload.");

    private static MirPlace RestorePlace(MirStateOperandPayload? operand) =>
        operand?.Restore() as MirPlace ?? throw new InvalidOperationException("Missing MIR place payload.");

    private static List<MirOperand> RestoreOperands(IReadOnlyList<MirStateOperandPayload>? operands) =>
        (operands ?? []).Select(static operand => operand.Restore()).ToList();
}

public sealed record MirStateTerminatorPayload(
    string Kind,
    SourceSpanPayload Span,
    MirStateOperandPayload? Value = null,
    int Target = 0,
    MirStateOperandPayload? Discriminant = null,
    IReadOnlyList<MirStateSwitchBranchPayload>? Branches = null,
    int? DefaultTarget = null)
{
    public static MirStateTerminatorPayload Create(MirTerminator terminator, MirStatePayloadCreateContext context)
    {
        context.ObserveTerminator(terminator);
        var span = SourceSpanPayload.Create(terminator.Span);
        return terminator switch
        {
            MirReturn ret => new MirStateTerminatorPayload(
                nameof(MirReturn),
                span,
                Value: ret.Value == null ? null : MirStateOperandPayload.Create(ret.Value, context)),
            MirGoto goTo => new MirStateTerminatorPayload(nameof(MirGoto), span, Target: goTo.Target.Value),
            MirSwitch @switch => new MirStateTerminatorPayload(
                nameof(MirSwitch),
                span,
                Discriminant: MirStateOperandPayload.Create(@switch.Discriminant, context),
                Branches: @switch.Branches.Select(branch => MirStateSwitchBranchPayload.Create(branch, context)).ToArray(),
                DefaultTarget: @switch.DefaultTarget?.Value),
            MirUnreachable => new MirStateTerminatorPayload(nameof(MirUnreachable), span),
            _ => UnsupportedTerminator(terminator, context)
        };
    }

    public MirTerminator Restore() =>
        Kind switch
        {
            nameof(MirReturn) => new MirReturn { Span = Span.ToSourceSpan(), Value = Value?.Restore() },
            nameof(MirGoto) => new MirGoto { Span = Span.ToSourceSpan(), Target = new BlockId { Value = Target } },
            nameof(MirSwitch) => new MirSwitch { Span = Span.ToSourceSpan(), Discriminant = RestoreOperand(Discriminant), Branches = (Branches ?? []).Select(static branch => branch.Restore()).ToList(), DefaultTarget = DefaultTarget.HasValue ? new BlockId { Value = DefaultTarget.Value } : null },
            nameof(MirUnreachable) => new MirUnreachable { Span = Span.ToSourceSpan() },
            _ => throw new InvalidOperationException($"Unsupported MIR terminator payload '{Kind}'.")
        };

    private static MirStateTerminatorPayload UnsupportedTerminator(MirTerminator terminator, MirStatePayloadCreateContext context)
    {
        context.AddUnsupported(terminator);
        return new MirStateTerminatorPayload(terminator.GetType().Name, SourceSpanPayload.Create(terminator.Span));
    }

    private static MirOperand RestoreOperand(MirStateOperandPayload? operand) =>
        operand?.Restore() ?? throw new InvalidOperationException("Missing MIR operand payload.");
}

public sealed record MirStateSwitchBranchPayload(
    MirStateOperandPayload Value,
    int Target,
    int? BoundVariable)
{
    public static MirStateSwitchBranchPayload Create(MirSwitchBranch branch, MirStatePayloadCreateContext context) =>
        new(MirStateOperandPayload.Create(branch.Value, context), branch.Target.Value, branch.BoundVariable?.Value);

    public MirSwitchBranch Restore() =>
        new()
        {
            Value = RestoreConstant(Value),
            Target = new BlockId { Value = Target },
            BoundVariable = BoundVariable.HasValue ? new LocalId { Value = BoundVariable.Value } : null
        };

    private static MirConstant RestoreConstant(MirStateOperandPayload payload) =>
        payload.Restore() as MirConstant ?? throw new InvalidOperationException("Switch branch payload must restore to a MIR constant.");
}

public sealed record MirStateOperandPayload(
    string Kind,
    SourceSpanPayload Span,
    int TypeId,
    string? Reason = null,
    MirStateConstantValuePayload? ConstantValue = null,
    int SymbolId = 0,
    string? Name = null,
    string? SymbolKind = null,
    MirStateFunctionIdPayload? FunctionId = null,
    int SignatureTypeId = 0,
    IReadOnlyList<int>? TypeArgumentIds = null,
    IReadOnlyList<GenericValueArgumentDescriptorPayload>? ValueArguments = null,
    int TraitOwnerId = 0,
    string? TraitSelfPosition = null,
    IReadOnlyList<int>? TraitSelfParameterIndices = null,
    bool TraitSelfInResult = false,
    string? TraitMethodRole = null,
    string? PlaceKind = null,
    int Local = 0,
    MirStateOperandPayload? Base = null,
    string? FieldName = null,
    MirStateOperandPayload? Index = null,
    string? IndexAccessKind = null,
    int TempId = 0)
{
    public static MirStateOperandPayload Create(MirOperand operand, MirStatePayloadCreateContext context)
    {
        context.ObserveOperand(operand);
        var span = SourceSpanPayload.Create(operand.Span);
        return operand switch
        {
            MirPoison poison => new MirStateOperandPayload(
                nameof(MirPoison),
                span,
                poison.TypeId.Value,
                Reason: poison.Reason),
            MirConstant constant => new MirStateOperandPayload(
                nameof(MirConstant),
                span,
                constant.TypeId.Value,
                ConstantValue: MirStateConstantValuePayload.Create(constant.Value, context)),
            MirConstGenericValue constGeneric => new MirStateOperandPayload(
                nameof(MirConstGenericValue),
                span,
                constGeneric.TypeId.Value,
                SymbolId: constGeneric.SymbolId.Value,
                Name: constGeneric.Name)
            {
                ParameterIndex = constGeneric.ParameterIndex
            },
            MirFunctionRef functionRef => new MirStateOperandPayload(
                nameof(MirFunctionRef),
                span,
                functionRef.TypeId.Value,
                SymbolId: functionRef.SymbolId.Value,
                Name: functionRef.Name,
                SymbolKind: functionRef.SymbolKind.ToString(),
                FunctionId: MirStateFunctionIdPayload.Create(functionRef.FunctionId),
                SignatureTypeId: functionRef.SignatureTypeId.Value,
                TypeArgumentIds: functionRef.TypeArgumentIds.Select(static id => id.Value).ToArray(),
                ValueArguments: functionRef.ValueArguments
                    .Select(GenericValueArgumentDescriptorPayload.Create)
                    .ToArray(),
                TraitOwnerId: functionRef.TraitOwnerId.Value,
                TraitSelfPosition: functionRef.TraitSelfPosition.ToString(),
                TraitSelfParameterIndices: functionRef.TraitSelfParameterIndices.ToArray(),
                TraitSelfInResult: functionRef.TraitSelfInResult,
                TraitMethodRole: functionRef.TraitMethodRole.ToString()),
            MirPlace place => new MirStateOperandPayload(
                nameof(MirPlace),
                span,
                place.TypeId.Value,
                PlaceKind: place.Kind.ToString(),
                Local: place.Local.Value,
                Base: place.Base == null ? null : Create(place.Base, context),
                FieldName: place.FieldName,
                Index: place.Index == null ? null : Create(place.Index, context),
                IndexAccessKind: place.IndexAccessKind.ToString()),
            MirTemp temp => new MirStateOperandPayload(
                nameof(MirTemp),
                span,
                temp.TypeId.Value,
                TempId: temp.Id.Value),
            _ => UnsupportedOperand(operand, context)
        };
    }

    public MirOperand Restore() =>
        Kind switch
        {
            nameof(MirPoison) => new MirPoison { Span = Span.ToSourceSpan(), TypeId = new TypeId(TypeId), Reason = Reason ?? "" },
            nameof(MirConstant) => new MirConstant { Span = Span.ToSourceSpan(), TypeId = new TypeId(TypeId), Value = RestoreConstantValue(ConstantValue) },
            nameof(MirConstGenericValue) => new MirConstGenericValue { Span = Span.ToSourceSpan(), TypeId = new TypeId(TypeId), SymbolId = new SymbolId(SymbolId), Name = Name ?? "", ParameterIndex = ParameterIndex },
            nameof(MirFunctionRef) => new MirFunctionRef { Span = Span.ToSourceSpan(), TypeId = new TypeId(TypeId), SymbolId = new SymbolId(SymbolId), Name = Name ?? "", SymbolKind = Enum.Parse<SymbolKind>(SymbolKind ?? ""), FunctionId = FunctionId?.Restore() ?? new FunctionId(), SignatureTypeId = new TypeId(SignatureTypeId), TypeArgumentIds = (TypeArgumentIds ?? []).Select(static id => new TypeId(id)).ToArray(), ValueArguments = (ValueArguments ?? []).Select(static argument => argument.Restore()).ToArray(), TraitOwnerId = new SymbolId(TraitOwnerId), TraitSelfPosition = Enum.Parse<SelfPosition>(TraitSelfPosition ?? ""), TraitSelfParameterIndices = (TraitSelfParameterIndices ?? []).ToArray(), TraitSelfInResult = TraitSelfInResult, TraitMethodRole = Enum.Parse<TraitMethodRole>(TraitMethodRole ?? "") },
            nameof(MirPlace) => new MirPlace { Span = Span.ToSourceSpan(), TypeId = new TypeId(TypeId), Kind = Enum.Parse<PlaceKind>(PlaceKind ?? ""), Local = new LocalId { Value = Local }, Base = Base?.Restore() as MirPlace, FieldName = FieldName, Index = Index?.Restore(), IndexAccessKind = Enum.Parse<MirIndexAccessKind>(IndexAccessKind ?? "") },
            nameof(MirTemp) => new MirTemp { Span = Span.ToSourceSpan(), TypeId = new TypeId(TypeId), Id = new TempId { Value = TempId } },
            _ => throw new InvalidOperationException($"Unsupported MIR operand payload '{Kind}'.")
        };

    public int ParameterIndex { get; init; } = -1;

    private static MirStateOperandPayload UnsupportedOperand(MirOperand operand, MirStatePayloadCreateContext context)
    {
        context.AddUnsupported(operand);
        return new MirStateOperandPayload(operand.GetType().Name, SourceSpanPayload.Create(operand.Span), operand.TypeId.Value);
    }

    private static MirConstantValue RestoreConstantValue(MirStateConstantValuePayload? payload) =>
        payload?.Restore() ?? throw new InvalidOperationException("Missing MIR constant value payload.");
}

public sealed record MirStateConstantValuePayload(
    string Kind,
    long IntValue = 0,
    double FloatValue = 0,
    string? StringValue = null,
    int CharValue = 0,
    bool BoolValue = false)
{
    public static MirStateConstantValuePayload Create(MirConstantValue value, MirStatePayloadCreateContext context)
    {
        context.ObserveConstantValue(value);
        return value switch
        {
            MirConstantValue.IntValue intValue => new MirStateConstantValuePayload(nameof(MirConstantValue.IntValue), IntValue: intValue.Value),
            MirConstantValue.FloatValue floatValue => new MirStateConstantValuePayload(nameof(MirConstantValue.FloatValue), FloatValue: floatValue.Value),
            MirConstantValue.StringValue stringValue => new MirStateConstantValuePayload(nameof(MirConstantValue.StringValue), StringValue: stringValue.Value),
            MirConstantValue.RawStringValue rawStringValue => new MirStateConstantValuePayload(nameof(MirConstantValue.RawStringValue), StringValue: rawStringValue.Value),
            MirConstantValue.CharValue charValue => new MirStateConstantValuePayload(nameof(MirConstantValue.CharValue), CharValue: charValue.Value),
            MirConstantValue.BoolValue boolValue => new MirStateConstantValuePayload(nameof(MirConstantValue.BoolValue), BoolValue: boolValue.Value),
            MirConstantValue.UnitValue => new MirStateConstantValuePayload(nameof(MirConstantValue.UnitValue)),
            _ => UnsupportedConstantValue(value, context)
        };
    }

    public MirConstantValue Restore() =>
        Kind switch
        {
            nameof(MirConstantValue.IntValue) => new MirConstantValue.IntValue(IntValue),
            nameof(MirConstantValue.FloatValue) => new MirConstantValue.FloatValue(FloatValue),
            nameof(MirConstantValue.StringValue) => new MirConstantValue.StringValue(StringValue ?? ""),
            nameof(MirConstantValue.RawStringValue) => new MirConstantValue.RawStringValue(StringValue ?? ""),
            nameof(MirConstantValue.CharValue) => new MirConstantValue.CharValue((char)CharValue),
            nameof(MirConstantValue.BoolValue) => new MirConstantValue.BoolValue(BoolValue),
            nameof(MirConstantValue.UnitValue) => new MirConstantValue.UnitValue(),
            _ => throw new InvalidOperationException($"Unsupported MIR constant payload '{Kind}'.")
        };

    private static MirStateConstantValuePayload UnsupportedConstantValue(
        MirConstantValue value,
        MirStatePayloadCreateContext context)
    {
        context.AddUnsupported(value);
        return new MirStateConstantValuePayload(value.GetType().Name);
    }
}

public sealed record MirStateFunctionIdPayload(
    int SymbolId,
    string Kind,
    string Module,
    string ModuleIdentityKey,
    string StableIdentityKey,
    string Name,
    string QualifiedName,
    string MangledName)
{
    public static MirStateFunctionIdPayload Create(FunctionId functionId) =>
        new(
            functionId.SymbolId.Value,
            functionId.Kind.ToString(),
            functionId.Module,
            functionId.ModuleIdentityKey,
            functionId.StableIdentityKey,
            functionId.Name,
            functionId.QualifiedName,
            functionId.MangledName);

    public FunctionId Restore() =>
        new()
        {
            SymbolId = new SymbolId(SymbolId),
            Kind = Enum.Parse<SymbolKind>(Kind),
            Module = Module,
            ModuleIdentityKey = ModuleIdentityKey,
            StableIdentityKey = StableIdentityKey,
            Name = Name,
            QualifiedName = QualifiedName,
            MangledName = MangledName
        };
}

public sealed record CStructAccessorEntryPayload(string Name, CStructAccessorPayload Accessor);

public sealed record CStructAccessorPayload(int FieldOffset, int FieldTypeId, bool IsGetter)
{
    public static CStructAccessorPayload Create(CStructAccessorInfo accessor) =>
        new(accessor.FieldOffset, accessor.FieldTypeId, accessor.IsGetter);

    public CStructAccessorInfo Restore() =>
        new()
        {
            FieldOffset = FieldOffset,
            FieldTypeId = FieldTypeId,
            IsGetter = IsGetter
        };
}

public sealed record MirStateImplSymbolPayload(
    int Id,
    string Name,
    SourceSpanPayload Span,
    bool IsTypeResolved,
    bool IsModuleLevel,
    bool IsPublic,
    int TypeId,
    int Trait,
    int ImplementingType,
    string CanonicalImplementingType,
    string ImplementingTypeDisplay,
    ImplTypeRefKeyPayload ImplementingTypeKey,
    IReadOnlyList<int> Methods,
    IReadOnlyList<SymbolMapEntryPayload> TraitMethodImplementations,
    IReadOnlyList<string> TraitTypeArgs,
    IReadOnlyList<ImplTypeRefKeyPayload> TraitTypeArgKeys,
    IReadOnlyList<string> CanonicalTraitTypeArgs,
    IReadOnlyList<ImplTypeRefKeyPayload> CanonicalTraitTypeArgKeys,
    IReadOnlyList<MirStateImplTypeShapePayload> TraitTypeArgShapes,
    MirStateImplTypeShapePayload? ImplementingTypeShape,
    IReadOnlyList<TypeMapEntryPayload> TypeArguments,
    IReadOnlyList<MirStateImplTypeArgTraitRequirementPayload> ImplementingTypeRequirements,
    bool IsAutoDerived)
{
    public static MirStateImplSymbolPayload Create(ImplSymbol impl) =>
        new(
            impl.Id.Value,
            impl.Name,
            SourceSpanPayload.Create(impl.Span),
            impl.IsTypeResolved,
            impl.IsModuleLevel,
            impl.IsPublic,
            impl.TypeId.Value,
            impl.Trait.Value,
            impl.ImplementingType.Value,
            impl.CanonicalImplementingType,
            impl.ImplementingTypeDisplay,
            ImplTypeRefKeyPayload.Create(impl.ImplementingTypeKey),
            impl.Methods.Select(static id => id.Value).ToArray(),
            impl.TraitMethodImplementations
                .OrderBy(static entry => entry.Key.Value)
                .Select(static entry => new SymbolMapEntryPayload(entry.Key.Value, entry.Value.Value))
                .ToArray(),
            impl.TraitTypeArgs.ToArray(),
            impl.TraitTypeArgKeys.Select(ImplTypeRefKeyPayload.Create).ToArray(),
            impl.CanonicalTraitTypeArgs.ToArray(),
            impl.CanonicalTraitTypeArgKeys.Select(ImplTypeRefKeyPayload.Create).ToArray(),
            impl.TraitTypeArgShapes.Select(MirStateImplTypeShapePayload.Create).ToArray(),
            impl.ImplementingTypeShape == null ? null : MirStateImplTypeShapePayload.Create(impl.ImplementingTypeShape),
            impl.TypeArguments
                .OrderBy(static entry => entry.Key.Value)
                .Select(static entry => new TypeMapEntryPayload(entry.Key.Value, entry.Value.Value))
                .ToArray(),
            impl.ImplementingTypeRequirements.Select(MirStateImplTypeArgTraitRequirementPayload.Create).ToArray(),
            impl.IsAutoDerived);

    public bool TryRestore(out ImplSymbol impl)
    {
        if (!ImplementingTypeKey.TryRestore(out var implementingTypeKey) ||
            !TryRestoreImplTypeRefKeys(TraitTypeArgKeys, out var traitTypeArgKeys) ||
            !TryRestoreImplTypeRefKeys(CanonicalTraitTypeArgKeys, out var canonicalTraitTypeArgKeys))
        {
            impl = new ImplSymbol { Name = Name };
            return false;
        }

        var requirements = new List<ImplTypeArgTraitRequirement>(ImplementingTypeRequirements.Count);
        foreach (var requirement in ImplementingTypeRequirements)
        {
            if (!requirement.TryRestore(out var restored))
            {
                impl = new ImplSymbol { Name = Name };
                return false;
            }

            requirements.Add(restored);
        }

        impl = new ImplSymbol
        {
            Id = new SymbolId(Id),
            Name = Name,
            Span = Span.ToSourceSpan(),
            IsTypeResolved = IsTypeResolved,
            IsModuleLevel = IsModuleLevel,
            IsPublic = IsPublic,
            TypeId = new TypeId(TypeId),
            Trait = new SymbolId(Trait),
            ImplementingType = new TypeId(ImplementingType),
            CanonicalImplementingType = CanonicalImplementingType,
            ImplementingTypeDisplay = ImplementingTypeDisplay,
            ImplementingTypeKey = implementingTypeKey,
            Methods = Methods.Select(static id => new SymbolId(id)).ToList(),
            TraitMethodImplementations = TraitMethodImplementations.ToDictionary(static entry => new SymbolId(entry.Key), static entry => new SymbolId(entry.Value)),
            TraitTypeArgs = TraitTypeArgs.ToList(),
            TraitTypeArgKeys = traitTypeArgKeys,
            CanonicalTraitTypeArgs = CanonicalTraitTypeArgs.ToList(),
            CanonicalTraitTypeArgKeys = canonicalTraitTypeArgKeys,
            TraitTypeArgShapes = TraitTypeArgShapes.Select(static shape => shape.Restore()).ToList(),
            ImplementingTypeShape = ImplementingTypeShape?.Restore(),
            TypeArguments = TypeArguments.ToDictionary(static entry => new TypeId(entry.Key), static entry => new TypeId(entry.Value)),
            ImplementingTypeRequirements = requirements,
            IsAutoDerived = IsAutoDerived
        };
        return true;
    }

    private static bool TryRestoreImplTypeRefKeys(
        IReadOnlyList<ImplTypeRefKeyPayload> payloads,
        out List<ImplTypeRefKey> keys)
    {
        keys = new List<ImplTypeRefKey>(payloads.Count);
        foreach (var payload in payloads)
        {
            if (!payload.TryRestore(out var key))
            {
                return false;
            }

            keys.Add(key);
        }

        return true;
    }
}

public sealed record MirStateImplTypeArgTraitRequirementPayload(
    int TypeArgIndex,
    int Trait,
    string TraitName,
    IReadOnlyList<string> TraitTypeArgs,
    IReadOnlyList<ImplTypeRefKeyPayload> TraitTypeArgKeys)
{
    public static MirStateImplTypeArgTraitRequirementPayload Create(ImplTypeArgTraitRequirement requirement) =>
        new(
            requirement.TypeArgIndex,
            requirement.Trait.Value,
            requirement.TraitName,
            requirement.TraitTypeArgs.ToArray(),
            requirement.TraitTypeArgKeys.Select(ImplTypeRefKeyPayload.Create).ToArray());

    public bool TryRestore(out ImplTypeArgTraitRequirement requirement)
    {
        var keys = new List<ImplTypeRefKey>(TraitTypeArgKeys.Count);
        foreach (var payload in TraitTypeArgKeys)
        {
            if (!payload.TryRestore(out var key))
            {
                requirement = new ImplTypeArgTraitRequirement();
                return false;
            }

            keys.Add(key);
        }

        requirement = new ImplTypeArgTraitRequirement
        {
            TypeArgIndex = TypeArgIndex,
            Trait = new SymbolId(Trait),
            TraitName = TraitName,
            TraitTypeArgs = TraitTypeArgs.ToList(),
            TraitTypeArgKeys = keys
        };
        return true;
    }
}

public sealed record MirStateImplTypeShapePayload(
    string Kind,
    string? Name = null,
    int SymbolId = 0,
    int TypeId = 0,
    IReadOnlyList<MirStateImplTypeShapePayload>? Children = null,
    MirStateImplTypeShapePayload? ParamType = null,
    MirStateImplTypeShapePayload? ReturnType = null,
    MirStateImplTypeShapePayload? InputType = null,
    IReadOnlyList<string>? EffectPaths = null,
    MirStateImplTypeShapePayload? OutputType = null)
{
    public static MirStateImplTypeShapePayload Create(ImplTypeShapeNode shape) =>
        shape switch
        {
            ImplWildcardShapeNode => new MirStateImplTypeShapePayload(nameof(ImplWildcardShapeNode)),
            ImplVariableShapeNode variable => new MirStateImplTypeShapePayload(nameof(ImplVariableShapeNode), Name: variable.Name),
            ImplValueVariableShapeNode variable => new MirStateImplTypeShapePayload(
                nameof(ImplValueVariableShapeNode),
                Name: variable.Name,
                TypeId: variable.TypeId.Value),
            ImplConcreteValueShapeNode value => new MirStateImplTypeShapePayload(
                nameof(ImplConcreteValueShapeNode),
                Name: value.CanonicalPayload,
                TypeId: value.TypeId.Value),
            ImplConstructorShapeNode constructor => new MirStateImplTypeShapePayload(
                nameof(ImplConstructorShapeNode),
                Name: constructor.Name,
                SymbolId: constructor.SymbolId.Value,
                TypeId: constructor.TypeId.Value,
                Children: constructor.Args.Select(Create).ToArray()),
            ImplTupleShapeNode tuple => new MirStateImplTypeShapePayload(
                nameof(ImplTupleShapeNode),
                Children: tuple.Elements.Select(Create).ToArray()),
            ImplArrowShapeNode arrow => new MirStateImplTypeShapePayload(
                nameof(ImplArrowShapeNode),
                ParamType: Create(arrow.ParamType),
                ReturnType: Create(arrow.ReturnType)),
            ImplEffectfulShapeNode effectful => new MirStateImplTypeShapePayload(
                nameof(ImplEffectfulShapeNode),
                InputType: Create(effectful.InputType),
                EffectPaths: effectful.EffectPaths.ToArray(),
                OutputType: effectful.OutputType == null ? null : Create(effectful.OutputType)),
            _ => throw new InvalidOperationException($"Unsupported impl type shape '{shape.GetType().Name}'.")
        };

    public ImplTypeShapeNode Restore() =>
        Kind switch
        {
            nameof(ImplWildcardShapeNode) => ImplWildcardShapeNode.Instance,
            nameof(ImplVariableShapeNode) => new ImplVariableShapeNode(Name ?? ""),
            nameof(ImplValueVariableShapeNode) => new ImplValueVariableShapeNode(Name ?? "", new TypeId(TypeId)),
            nameof(ImplConcreteValueShapeNode) => new ImplConcreteValueShapeNode(Name ?? "", new TypeId(TypeId)),
            nameof(ImplConstructorShapeNode) => new ImplConstructorShapeNode(Name ?? "", (Children ?? []).Select(static child => child.Restore()).ToArray()) { SymbolId = new SymbolId(SymbolId), TypeId = new TypeId(TypeId) },
            nameof(ImplTupleShapeNode) => new ImplTupleShapeNode((Children ?? []).Select(static child => child.Restore()).ToArray()),
            nameof(ImplArrowShapeNode) => new ImplArrowShapeNode(RestoreRequired(ParamType), RestoreRequired(ReturnType)),
            nameof(ImplEffectfulShapeNode) => new ImplEffectfulShapeNode(RestoreRequired(InputType), EffectPaths ?? [], OutputType?.Restore()),
            _ => throw new InvalidOperationException($"Unsupported impl type shape payload '{Kind}'.")
        };

    private static ImplTypeShapeNode RestoreRequired(MirStateImplTypeShapePayload? payload) =>
        payload?.Restore() ?? throw new InvalidOperationException("Missing impl type shape child payload.");
}

public sealed record MirStateTraitInfoPayload(
    int TraitId,
    int TypeParameterCount,
    IReadOnlyList<int> TypeParameterIds,
    string SelfPosition,
    bool HasMethodDispatchMetadata,
    IReadOnlyList<MirStateTraitMethodInfoPayload> Methods,
    IReadOnlyList<int> ParentTraits)
{
    public static MirStateTraitInfoPayload Create(MirTraitInfo trait) =>
        new(
            trait.TraitId.Value,
            trait.TypeParameterCount,
            trait.TypeParameterIds.Select(static id => id.Value).ToArray(),
            trait.SelfPosition.ToString(),
            trait.HasMethodDispatchMetadata,
            trait.Methods.Select(MirStateTraitMethodInfoPayload.Create).ToArray(),
            trait.ParentTraits.Select(static id => id.Value).ToArray());

    public MirTraitInfo Restore() =>
        new()
        {
            TraitId = new SymbolId(TraitId),
            TypeParameterCount = TypeParameterCount,
            TypeParameterIds = TypeParameterIds.Select(static id => new SymbolId(id)).ToList(),
            SelfPosition = Enum.Parse<SelfPosition>(SelfPosition),
            HasMethodDispatchMetadata = HasMethodDispatchMetadata,
            Methods = Methods.Select(static method => method.Restore()).ToList(),
            ParentTraits = ParentTraits.Select(static id => new SymbolId(id)).ToList()
        };
}

public sealed record MirStateTraitMethodInfoPayload(
    int TraitId,
    int MethodId,
    string Name,
    string SelfPosition,
    IReadOnlyList<int> SelfParameterIndices,
    bool SelfInResult,
    string MethodRole,
    bool HasDefaultImplementation)
{
    public static MirStateTraitMethodInfoPayload Create(MirTraitMethodInfo method) =>
        new(
            method.TraitId.Value,
            method.MethodId.Value,
            method.Name,
            method.SelfPosition.ToString(),
            method.SelfParameterIndices.ToArray(),
            method.SelfInResult,
            method.MethodRole.ToString(),
            method.HasDefaultImplementation);

    public MirTraitMethodInfo Restore() =>
        new()
        {
            TraitId = new SymbolId(TraitId),
            MethodId = new SymbolId(MethodId),
            Name = Name,
            SelfPosition = Enum.Parse<SelfPosition>(SelfPosition),
            SelfParameterIndices = SelfParameterIndices.ToList(),
            SelfInResult = SelfInResult,
            MethodRole = Enum.Parse<TraitMethodRole>(MethodRole),
            HasDefaultImplementation = HasDefaultImplementation
        };
}

public sealed record MirStateTypeAliasInfoPayload(
    int AliasId,
    string Name,
    int TypeId,
    int AliasTarget,
    IReadOnlyList<int> TypeParameterIds)
{
    public static MirStateTypeAliasInfoPayload Create(MirTypeAliasInfo alias) =>
        new(
            alias.AliasId.Value,
            alias.Name,
            alias.TypeId.Value,
            alias.AliasTarget.Value,
            alias.TypeParameterIds.Select(static id => id.Value).ToArray());

    public MirTypeAliasInfo Restore() =>
        new()
        {
            AliasId = new SymbolId(AliasId),
            Name = Name,
            TypeId = new TypeId(TypeId),
            AliasTarget = new TypeId(AliasTarget),
            TypeParameterIds = TypeParameterIds.Select(static id => new SymbolId(id)).ToList()
        };
}

public sealed record MirStateTypeConstructorInfoPayload(
    int SymbolId,
    string Name,
    int TypeId,
    IReadOnlyList<int> TypeParameterIds)
{
    public static MirStateTypeConstructorInfoPayload Create(MirTypeConstructorInfo constructor) =>
        new(
            constructor.SymbolId.Value,
            constructor.Name,
            constructor.TypeId.Value,
            constructor.TypeParameterIds.Select(static id => id.Value).ToArray());

    public MirTypeConstructorInfo Restore() =>
        new()
        {
            SymbolId = new SymbolId(SymbolId),
            Name = Name,
            TypeId = new TypeId(TypeId),
            TypeParameterIds = TypeParameterIds.Select(static id => new SymbolId(id)).ToList()
        };
}

public sealed record MirStateSpecializationFailurePayload(
    string Reason,
    string TemplateKey,
    string TemplateName,
    string SignatureKey,
    string SignatureDisplay,
    string PreviewName)
{
    public static MirStateSpecializationFailurePayload Create(MirSpecializationFailureInfo failure) =>
        new(
            failure.Reason,
            failure.TemplateKey,
            failure.TemplateName,
            failure.SignatureKey,
            failure.SignatureDisplay,
            failure.PreviewName);

    public MirSpecializationFailureInfo Restore() =>
        new()
        {
            Reason = Reason,
            TemplateKey = TemplateKey,
            TemplateName = TemplateName,
            SignatureKey = SignatureKey,
            SignatureDisplay = SignatureDisplay,
            PreviewName = PreviewName
        };
}

public sealed record SymbolMapEntryPayload(int Key, int Value);

public sealed record TypeMapEntryPayload(int Key, int Value);

public sealed class MirStatePayloadCreateContext
{
    public int UnsupportedNodeCount { get; private set; }

    public HashSet<string> UnsupportedNodeKinds { get; } = new(StringComparer.Ordinal);

    public void ObserveInstruction(MirInstruction instruction)
    {
        if (instruction is not (MirAssign or MirCall or MirBinOp or MirUnaryOp or MirLoad or MirStore or MirDrop or
            MirCopy or MirMove or MirAlloc))
        {
            AddUnsupported(instruction);
        }
    }

    public void ObserveTerminator(MirTerminator terminator)
    {
        if (terminator is not (MirReturn or MirGoto or MirSwitch or MirUnreachable))
        {
            AddUnsupported(terminator);
        }
    }

    public void ObserveOperand(MirOperand operand)
    {
        if (operand is not (MirPoison or MirConstant or MirConstGenericValue or MirFunctionRef or MirPlace or MirTemp))
        {
            AddUnsupported(operand);
        }
    }

    public void ObserveConstantValue(MirConstantValue value)
    {
        if (value is not (MirConstantValue.IntValue or MirConstantValue.FloatValue or MirConstantValue.StringValue or
            MirConstantValue.RawStringValue or MirConstantValue.CharValue or MirConstantValue.BoolValue or
            MirConstantValue.UnitValue))
        {
            AddUnsupported(value);
        }
    }

    public void AddUnsupported(object node)
    {
        UnsupportedNodeCount++;
        UnsupportedNodeKinds.Add(node.GetType().Name);
    }
}
