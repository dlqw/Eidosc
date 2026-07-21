using Eidosc.Symbols;
using System.Text;
using Eidosc.Borrow;
using Eidosc.Diagnostic;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.CodeGen.Llvm;

/// <summary>
/// MIR 到 LLVM IR 转换器
/// </summary>
public sealed partial class MirToLlvmConverter
{
    private const int UnknownGenericRemainingArity = -1;
    private readonly TypeLowering _typeLowering;
    private readonly NameMangler _nameMangler;
    private readonly LlvmSymbolNameAllocator _symbolNameAllocator;
    private readonly SymbolTable? _symbolTable;
    private readonly Func<string, IDisposable>? _measureSubphase;
    private readonly ConverterLocalManager _locals = new();
    private readonly List<LlvmInstruction> _postInstructionBuffer = [];
    private readonly Dictionary<BlockId, LlvmBasicBlock> _blockMap = new();
    private readonly ConverterFunctionCache _funcCache = new();
    private readonly Dictionary<string, ExternalFunctionDeclarationInfo> _externalFunctionDeclarations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MirSpecializationFailureInfo> _specializationFailureByTemplateKey = new(StringComparer.Ordinal);
    private readonly Dictionary<SymbolId, TypeId> _typeConstructorTypeIdBySymbol = [];
    private readonly Dictionary<string, TypeId> _typeConstructorTypeIdByName = new(StringComparer.Ordinal);

    // FFI 外部函数映射：Eidos 函数名 → C 符号名
    private readonly Dictionary<string, string> _ffiSymbolNameBySourceName = new(StringComparer.Ordinal);
    private readonly Dictionary<SymbolId, string> _ffiSymbolNameBySymbolId = [];
    private readonly HashSet<SymbolId> _genericFunctionSymbols = [];
    private readonly HashSet<string> _genericFunctionNames = new(StringComparer.Ordinal);
    private readonly Dictionary<LocalId, int> _genericFunctionLocals = [];
    private readonly Dictionary<BlockId, Dictionary<LocalId, int>> _incomingGenericFunctionLocalsByBlock = [];
    private readonly HashSet<string> _reportedGenericCallSites = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reportedUnresolvedTypeSites = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reportedUnresolvedFunctionSites = new(StringComparer.Ordinal);
    private readonly Dictionary<LocalId, PartialCallState> _partialCallStates = new();
    private readonly Dictionary<LocalId, List<MirInstruction>> _definitionInstructionsByLocal = new();
    private readonly Dictionary<LocalId, LocalDefinitionStats> _definitionStatsByLocal = new();
    private readonly Dictionary<LocalId, LlvmType> _inferredLocalTypeCache = new();
    private readonly HashSet<LocalId> _failedLocalTypeInferenceCache = [];
    private readonly HashSet<LocalId> _borrowedProjectionLocals = [];
    private readonly List<LlvmFunction> _synthesizedClosureHelpers = [];
    private readonly Dictionary<string, LlvmGlobal> _stringLiteralGlobals = new(StringComparer.Ordinal);
    private readonly Dictionary<SymbolId, PerceusHints> _perceusHintsByFunctionSymbol = [];
    private readonly Dictionary<string, PerceusHints> _perceusHintsByFunction = new(StringComparer.Ordinal);
    private readonly Dictionary<SymbolId, ReuseHints> _reuseHintsByFunctionSymbol = [];
    private readonly Dictionary<string, ReuseHints> _reuseHintsByFunction = new(StringComparer.Ordinal);
    private readonly Dictionary<int, LlvmAlloca> _reuseSlotAllocas = [];
    private readonly Dictionary<SymbolId, StackPromotionHints> _stackPromotionHintsByFunctionSymbol = [];
    private readonly Dictionary<string, StackPromotionHints> _stackPromotionHintsByFunction = new(StringComparer.Ordinal);
    private readonly Dictionary<int, TypeId> _valueBoxPayloadTypeByRuntimeTypeId = [];
    private readonly Dictionary<string, LlvmGlobal> _runtimeFunctionGlobalCache = new(StringComparer.Ordinal);
    private readonly Dictionary<TypeId, ArrayElementPolicy> _arrayElementPolicies = [];

    /// <summary>
    /// Per-function string constant pool: maps string value to the eidos_string_intern
    /// call result. Deduplicates repeated string literals within a function.
    /// </summary>
    private readonly Dictionary<string, LlvmValue> _stringLiteralPool = new(StringComparer.Ordinal);

    private LlvmFunction? _builtinShowBoolHelper;
    private LlvmFunction? _erasedShowHelper;
    private LlvmModule? _currentModule;
    private MirFunc? _currentMirFunction;
    private Dictionary<string, CStructAccessorInfo> _cstructAccessors = [];
    private int _stringLiteralCounter;
    private int _closureThunkCounter;
    private LlvmFunction? _currentFunction;
    private LlvmBasicBlock? _currentBlock;
    private BlockId? _currentBlockId;
    private int _currentInstructionIndex;
    private bool _currentFunctionAllowsOpenLocalTypes;
    private HashSet<(BlockId Block, int Index)>? _currentOmitDup;
    private HashSet<(BlockId Block, int Index)>? _currentOmitDrop;
    private ReuseHints? _currentReuseHints;
    private StackPromotionHints? _currentStackPromotionHints;
    private UnifiedStackPromotionHints? _currentUnifiedHints;
    public List<Diagnostic.Diagnostic> Diagnostics { get; } = [];

    private sealed record PartialCallState(
        LlvmValue Function,
        LlvmFunctionType Signature,
        List<LlvmValue> BoundArguments,
        List<bool> BoundArgumentManagedFlags,
        int CapturedArgumentCount,
        LlvmFunctionType? VisibleSignature);

    private sealed record ExternalFunctionDeclarationInfo(
        LlvmFunctionType FunctionType,
        LlvmDeclarationOrigin Origin);

    public MirToLlvmConverter()
        : this(null, null)
    {
    }

    public MirToLlvmConverter(SymbolTable? symbolTable, Func<string, IDisposable>? measureSubphase = null)
    {
        _typeLowering = new TypeLowering();
        _nameMangler = new NameMangler();
        _symbolNameAllocator = new LlvmSymbolNameAllocator(_nameMangler);
        _symbolTable = symbolTable;
        _measureSubphase = measureSubphase;
    }

    public MirToLlvmConverter(TypeLowering typeLowering, NameMangler nameMangler, Func<string, IDisposable>? measureSubphase = null)
    {
        _typeLowering = typeLowering;
        _nameMangler = nameMangler;
        _symbolNameAllocator = new LlvmSymbolNameAllocator(_nameMangler);
        _symbolTable = null;
        _measureSubphase = measureSubphase;
    }

    /// <summary>
    /// 设置借用检查阶段的 Perceus 分析提示，供 ConvertCopy/ConvertDrop 消费
    /// </summary>
    public void SetPerceusHints(ModuleBorrowCheckResult borrowResult)
    {
        _perceusHintsByFunctionSymbol.Clear();
        _perceusHintsByFunction.Clear();
        if (borrowResult == null) return;

        foreach (var funcResult in borrowResult.FunctionResults.Values)
        {
            var hints = funcResult.PerceusHints ?? funcResult.PerceusAnalyzer?.Hints;
            if (hints != null)
            {
                if (funcResult.FunctionSymbolId.IsValid)
                {
                    _perceusHintsByFunctionSymbol[funcResult.FunctionSymbolId] = hints;
                }
                else if (!string.IsNullOrEmpty(funcResult.FunctionName))
                {
                    _perceusHintsByFunction[funcResult.FunctionName] = hints;
                }
            }
        }
    }

    /// <summary>
    /// 设置 Reuse 分析提示（drop-then-alloc 内存复用）
    /// </summary>
    public void SetReuseHints(ModuleBorrowCheckResult borrowResult)
    {
        _reuseHintsByFunctionSymbol.Clear();
        _reuseHintsByFunction.Clear();
        if (borrowResult == null) return;

        foreach (var funcResult in borrowResult.FunctionResults.Values)
        {
            var hints = funcResult.ReuseHints ?? funcResult.ReuseAnalyzer?.Hints;
            if (hints != null)
            {
                if (funcResult.FunctionSymbolId.IsValid)
                {
                    _reuseHintsByFunctionSymbol[funcResult.FunctionSymbolId] = hints;
                }
                else if (!string.IsNullOrEmpty(funcResult.FunctionName))
                {
                    _reuseHintsByFunction[funcResult.FunctionName] = hints;
                }
            }
        }
    }

    /// <summary>
    /// 设置栈分配提升提示（heap-to-stack promotion）
    /// </summary>
    public void SetStackPromotionHints(ModuleBorrowCheckResult borrowResult)
    {
        _stackPromotionHintsByFunctionSymbol.Clear();
        _stackPromotionHintsByFunction.Clear();
        if (borrowResult == null) return;

        foreach (var funcResult in borrowResult.FunctionResults.Values)
        {
            var hints = funcResult.StackPromotionHints ?? funcResult.StackPromotionAnalyzer?.Hints;
            if (hints != null)
            {
                if (funcResult.FunctionSymbolId.IsValid)
                {
                    _stackPromotionHintsByFunctionSymbol[funcResult.FunctionSymbolId] = hints;
                }
                else if (!string.IsNullOrEmpty(funcResult.FunctionName))
                {
                    _stackPromotionHintsByFunction[funcResult.FunctionName] = hints;
                }
            }
        }
    }

    private readonly Dictionary<SymbolId, UnifiedStackPromotionHints> _unifiedStackPromotionHintsByFunctionSymbol = [];
    private readonly Dictionary<string, UnifiedStackPromotionHints> _unifiedStackPromotionHintsByFunction = new(StringComparer.Ordinal);

    /// <summary>
    /// 设置统一栈提升提示（来自 UnifiedStackPromotionAnalyzer）。
    /// </summary>
    public void SetUnifiedStackPromotionHints(ModuleBorrowCheckResult result)
    {
        _unifiedStackPromotionHintsByFunctionSymbol.Clear();
        _unifiedStackPromotionHintsByFunction.Clear();
        if (result == null) return;

        foreach (var funcResult in result.FunctionResults.Values)
        {
            var hints = funcResult.UnifiedStackPromotionHints ?? funcResult.UnifiedStackPromotionAnalyzer?.Hints;
            if (hints != null)
            {
                if (funcResult.FunctionSymbolId.IsValid)
                {
                    _unifiedStackPromotionHintsByFunctionSymbol[funcResult.FunctionSymbolId] = hints;
                }
                else if (!string.IsNullOrEmpty(funcResult.FunctionName))
                {
                    _unifiedStackPromotionHintsByFunction[funcResult.FunctionName] = hints;
                }
            }
        }
    }

    /// <summary>
    /// 设置 @cstruct 字段访问器元数据（从管道传入，绕过 MIR 优化可能丢失数据的问题）
    /// </summary>
    public void SetCStructAccessors(Dictionary<string, CStructAccessorInfo> accessors)
    {
        _cstructAccessors = accessors ?? [];
    }

    /// <summary>
    /// 转换 MIR 模块为 LLVM 模块
    /// </summary>
    public LlvmModule Convert(MirModule module)
    {
        Diagnostics.Clear();
        ResetAndIndexModuleContext(module);

        using (MeasureConverterSubphase("register_function_types"))
        {
            foreach (var func in module.Functions)
            {
                RegisterFunctionType(func);
            }
        }

        LlvmModule llvmModule;
        using (MeasureConverterSubphase("create_module"))
        {
            llvmModule = new LlvmModule
            {
                Name = module.Name,
                Functions = new List<LlvmFunction>(module.Functions.Count),
                LinkLibraries = module.LinkLibraries.ToList()
            };
        }
        _currentModule = llvmModule;

        // Collect all named struct types from TypeLowering into the module
        using (MeasureConverterSubphase("collect_named_struct_types"))
        {
            CollectNamedStructTypes(llvmModule);
        }

        LlvmFunction? entryMainFunction = null;

        // 转换所有函数
        using (MeasureConverterSubphase("convert_functions"))
        {
            foreach (var func in module.Functions)
            {
                // FFI 外部函数：生成 declare 声明而非函数体
                if (func.IsExternal)
                {
                    AddExternalFfiDeclaration(func, llvmModule);
                    continue;
                }

                if (IsIntrinsicDeclaration(func))
                {
                    continue;
                }

                if (!func.IsRuntimeWordAbi && IsGenericSignature(func))
                {
                    continue;
                }

                var llvmFunc = ConvertFunctionCore(func);
                llvmModule.Functions.Add(llvmFunc);
                if (func.IsEntry ||
                    string.Equals(func.Name, WellKnownStrings.SpecialNames.Main, StringComparison.Ordinal))
                {
                    entryMainFunction = llvmFunc;
                }
            }
        }

        using (MeasureConverterSubphase("add_helpers_and_entry_wrapper"))
        {
            if (_synthesizedClosureHelpers.Count > 0)
            {
                llvmModule.Functions.AddRange(_synthesizedClosureHelpers);
            }

            AddMainEntryWrapperIfNeeded(llvmModule, entryMainFunction);
        }

        _currentModule = null;

        // 添加外部声明（运行时函数）
        using (MeasureConverterSubphase("add_declarations"))
        {
            AddRuntimeDeclarations(llvmModule);
            AddRecordedExternalDeclarations(llvmModule);
        }

        using (MeasureConverterSubphase("synthesize_constructor_stubs"))
        {
        }

        using (MeasureConverterSubphase("synthesize_destructors"))
        {
            SynthesizeAdtDestructors(module, llvmModule);
        }

        using (MeasureConverterSubphase("validate_output"))
        {
            ReportDuplicateGlobalDefinitions(llvmModule);
            ReportInvalidUnresolvedExternalDeclarations(llvmModule);
        }

        return llvmModule;
    }

    private IDisposable MeasureConverterSubphase(string name)
    {
        return _measureSubphase?.Invoke(name) ?? NullDisposable.Instance;
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        private NullDisposable()
        {
        }

        public void Dispose()
        {
        }
    }

    private static bool IsIntrinsicDeclaration(MirFunc function)
    {
        return function.IntrinsicName != null && function.BasicBlocks.Count == 0;
    }

    private void IndexSpecializationFailures(IEnumerable<MirSpecializationFailureInfo> failures)
    {
        foreach (var failure in failures)
        {
            if (string.IsNullOrWhiteSpace(failure.TemplateKey) ||
                _specializationFailureByTemplateKey.ContainsKey(failure.TemplateKey))
            {
                continue;
            }

            _specializationFailureByTemplateKey[failure.TemplateKey] = failure;
        }
    }

    private void IndexTypeConstructors(IEnumerable<MirTypeConstructorInfo> typeConstructors)
    {
        _typeConstructorTypeIdBySymbol.Clear();
        _typeConstructorTypeIdByName.Clear();

        foreach (var typeConstructor in typeConstructors)
        {
            if (!typeConstructor.TypeId.IsValid)
            {
                continue;
            }

            if (typeConstructor.SymbolId.IsValid)
            {
                _typeConstructorTypeIdBySymbol[typeConstructor.SymbolId] = typeConstructor.TypeId;
            }

            if (!string.IsNullOrWhiteSpace(typeConstructor.Name))
            {
                _typeConstructorTypeIdByName[typeConstructor.Name] = typeConstructor.TypeId;
            }
        }
    }

    /// <summary>
    /// 从 TypeLowering 收集所有已注册的具名结构体类型到 LlvmModule。
    /// 确保即使没有 GEP 指令引用，类型定义也会被输出到 IR。
    /// </summary>
    private void CollectNamedStructTypes(LlvmModule llvmModule)
    {
        foreach (var structType in _typeLowering.GetAllStructTypes())
        {
            if (!string.IsNullOrEmpty(structType.Name) && !structType.IsLiteral)
            {
                llvmModule.NamedStructTypes.Add(structType);
            }
        }
    }

    /// <summary>
    /// 转换单个 MIR 函数为 LLVM 函数
    /// </summary>
    public LlvmFunction ConvertFunction(MirFunc func)
    {
        Diagnostics.Clear();
        _typeLowering.SetDynamicTypeKeys(null);
        _typeLowering.SetTypeDescriptors(null);
        _funcCache.Clear();
        _externalFunctionDeclarations.Clear();
        _specializationFailureByTemplateKey.Clear();
        _typeConstructorTypeIdBySymbol.Clear();
        _typeConstructorTypeIdByName.Clear();
        _ffiSymbolNameBySourceName.Clear();
        _ffiSymbolNameBySymbolId.Clear();
        _genericFunctionSymbols.Clear();
        _genericFunctionNames.Clear();
        _reportedGenericCallSites.Clear();
        _stringLiteralGlobals.Clear();
        _runtimeFunctionGlobalCache.Clear();
        _stringLiteralCounter = 0;
        _closureThunkCounter = 0;
        _synthesizedClosureHelpers.Clear();
        _builtinShowBoolHelper = null;
        _erasedShowHelper = null;
        _currentModule = null;
        _currentMirFunction = null;
        return ConvertFunctionCore(func);
    }

    private LlvmFunction ConvertFunctionCore(MirFunc func)
    {
        using (MeasureConverterSubphase("function.ensure_registered"))
        {
            if (func.SymbolId.IsValid)
            {
                if (!_funcCache.FunctionTypeBySymbol.ContainsKey(func.SymbolId))
                {
                    RegisterFunctionType(func);
                }
            }
            else if (!string.IsNullOrEmpty(func.Name))
            {
                var llvmName = BuildFunctionLlvmName(func.Name, func.SymbolId);
                if (!_funcCache.FunctionTypeByName.ContainsKey(llvmName))
                {
                    RegisterFunctionType(func);
                }
            }
        }

        using (MeasureConverterSubphase("function.reset_state"))
        {
            // 清理之前的状态
            _locals.Clear();
            _genericFunctionLocals.Clear();
            _incomingGenericFunctionLocalsByBlock.Clear();
            _partialCallStates.Clear();
            _definitionInstructionsByLocal.Clear();
            _definitionStatsByLocal.Clear();
            _inferredLocalTypeCache.Clear();
            _failedLocalTypeInferenceCache.Clear();
            _borrowedProjectionLocals.Clear();
            _postInstructionBuffer.Clear();
            _blockMap.Clear();
            _nameMangler.ResetCounters();
            _stringLiteralPool.Clear();
            _currentMirFunction = func;
            _currentFunctionAllowsOpenLocalTypes = false;

            // 加载当前函数的 Perceus 优化提示
            _currentOmitDup = null;
            _currentOmitDrop = null;
            if (func.SymbolId.IsValid && _perceusHintsByFunctionSymbol.TryGetValue(func.SymbolId, out var symbolHints))
            {
                _currentOmitDup = new HashSet<(BlockId Block, int Index)>(symbolHints.OmitDup);
                _currentOmitDrop = new HashSet<(BlockId Block, int Index)>(symbolHints.OmitDrop);
            }
            else if (func.Name != null && _perceusHintsByFunction.TryGetValue(func.Name, out var hints))
            {
                _currentOmitDup = new HashSet<(BlockId Block, int Index)>(hints.OmitDup);
                _currentOmitDrop = new HashSet<(BlockId Block, int Index)>(hints.OmitDrop);
            }

            // 加载当前函数的 Reuse 分析提示
            _currentReuseHints = null;
            _reuseSlotAllocas.Clear();
            if (func.SymbolId.IsValid && _reuseHintsByFunctionSymbol.TryGetValue(func.SymbolId, out var symbolReuseHints))
            {
                _currentReuseHints = symbolReuseHints;
            }
            else if (func.Name != null && _reuseHintsByFunction.TryGetValue(func.Name, out var reuseHints))
            {
                _currentReuseHints = reuseHints;
            }

            // 加载当前函数的栈分配提升提示
            _currentStackPromotionHints = null;
            if (func.SymbolId.IsValid && _stackPromotionHintsByFunctionSymbol.TryGetValue(func.SymbolId, out var symbolStackPromoHints))
            {
                _currentStackPromotionHints = symbolStackPromoHints;
            }
            else if (func.Name != null && _stackPromotionHintsByFunction.TryGetValue(func.Name, out var stackPromoHints))
            {
                _currentStackPromotionHints = stackPromoHints;
            }

            // 加载统一栈提升提示（ADT + 闭包，字段级）
            _currentUnifiedHints = null;
            if (func.SymbolId.IsValid && _unifiedStackPromotionHintsByFunctionSymbol.TryGetValue(func.SymbolId, out var symbolUnifiedHints))
            {
                _currentUnifiedHints = symbolUnifiedHints;
            }
            else if (func.Name != null && _unifiedStackPromotionHintsByFunction.TryGetValue(func.Name, out var unifiedHints))
            {
                _currentUnifiedHints = unifiedHints;
            }
        }

        foreach (var local in func.Locals)
        {
            _locals.LocalTypeById[local.Id] = local.TypeId;
        }

        using (MeasureConverterSubphase("function.build_local_definition_index"))
        {
            BuildLocalDefinitionIndex(func);
        }

        using (MeasureConverterSubphase("function.analyze_generic_flow"))
        {
            AnalyzeGenericFunctionLocalFlow(func);
        }
        var allowUnresolvedSignatureTypes = IsGenericSignature(func);
        var allowOpenLocalTypes = ContainsOpenLocalTypes(func);
        _currentFunctionAllowsOpenLocalTypes = allowOpenLocalTypes;

        using (MeasureConverterSubphase("function.compute_slot_backed_locals"))
        {
            foreach (var slotBackedLocal in ComputeSlotBackedLocals(func, _definitionStatsByLocal))
            {
                if (!_locals.LocalTypeById.TryGetValue(slotBackedLocal, out var localType))
                {
                    continue;
                }

                if (LowerStorageTypeIdOrReport(localType, "slot-backed local", allowOpenLocalTypes) is LlvmVoidType)
                {
                    continue;
                }

                _locals.SlotBackedLocals.Add(slotBackedLocal);
            }
        }

        // 确定函数名
        var funcName = ResolveFunctionLlvmName(func);

        using (MeasureConverterSubphase("function.create_header"))
        {
            // 创建 LLVM 函数
            var isRuntimeWordAbi = func.IsRuntimeWordAbi;
            _currentFunction = new LlvmFunction
            {
                Name = funcName,
                ReturnType = isRuntimeWordAbi
                    ? LlvmIntType.I64
                    : LowerFunctionSignatureType(func.ReturnType, func, "return type", allowUnresolvedSignatureTypes),
                Linkage = LlvmLinkage.External,
                Parameters = new List<LlvmParameter>(CountParameters(func)),
                BasicBlocks = new List<LlvmBasicBlock>(Math.Max(1, func.BasicBlocks.Count))
            };

            // 添加参数
            foreach (var local in func.Locals)
            {
                if (!local.IsParameter)
                {
                    continue;
                }

                var loweredParameterType = isRuntimeWordAbi
                    ? (LlvmType)LlvmIntType.I64
                    : NormalizeParameterType(
                        LowerFunctionSignatureType(
                            local.TypeId,
                            func,
                            $"parameter '{local.Name}'",
                            allowUnresolvedSignatureTypes));
                var param = new LlvmParameter
                {
                    Name = local.Name ?? $"arg{local.Id.Value}",
                    Type = loweredParameterType
                };
                _currentFunction.Parameters.Add(param);

                // 建立映射
                var llvmLocal = new LlvmLocal
                {
                    Name = param.Name,
                    Type = loweredParameterType
                };
                _locals.LocalMap[local.Id] = llvmLocal;
            }
        }

        // 转换基本块
        using (MeasureConverterSubphase("function.convert_blocks"))
        {
            foreach (var block in GetBlockLoweringOrder(func))
            {
                var llvmBlock = ConvertBlock(block);
                _currentFunction.BasicBlocks.Add(llvmBlock);
            }
        }

        // 确保入口块存在
        if (_currentFunction.BasicBlocks.Count == 0)
        {
            _currentFunction.BasicBlocks.Add(new LlvmBasicBlock
            {
                Label = WellKnownStrings.InternalNames.Entry,
                Terminator = ReportMissingTerminatorFallback(null)
            });
        }

        return _currentFunction;
    }

    /// <summary>
    /// 转换 MIR 基本块为 LLVM 基本块
    /// </summary>
    private LlvmBasicBlock ConvertBlock(MirBasicBlock block)
    {
        var label = $"bb{block.Id.Value}";

        _currentBlock = new LlvmBasicBlock
        {
            Label = label,
            Instructions = new List<LlvmInstruction>(EstimateLlvmInstructionCapacity(block))
        };

        _blockMap[block.Id] = _currentBlock;
        _currentBlockId = block.Id;
        SeedGenericFunctionLocalsForBlock(block.Id);

        if (block.IsEntry)
        {
            EmitSlotLocalsForEntryBlock();
        }

        // 转换指令
        _currentInstructionIndex = 0;
        foreach (var instr in block.Instructions)
        {
            _postInstructionBuffer.Clear();
            var instruction = ConvertInstruction(instr);
            if (instruction != null)
            {
                _currentBlock.Instructions.Add(instruction);
            }
            _currentInstructionIndex++;

            if (_postInstructionBuffer.Count == 0)
            {
                continue;
            }

            _currentBlock.Instructions.AddRange(_postInstructionBuffer);
            _postInstructionBuffer.Clear();
        }

        // 转换终止指令
        if (block.Terminator != null)
        {
            _currentBlock.Terminator = ConvertTerminator(block.Terminator);
        }
        else
        {
            _currentBlock.Terminator = ReportMissingTerminatorFallback(block);
        }

        return _currentBlock;
    }

    private static int CountParameters(MirFunc function)
    {
        var count = 0;
        foreach (var local in function.Locals)
        {
            if (local.IsParameter)
            {
                count++;
            }
        }

        return count;
    }

    private static int EstimateLlvmInstructionCapacity(MirBasicBlock block)
    {
        var instructionCount = block.Instructions.Count;
        if (instructionCount == 0)
        {
            return 0;
        }

        return Math.Min(Math.Max(instructionCount + instructionCount / 2, instructionCount + 4), instructionCount * 4);
    }

    /// <summary>
    /// 转换 MIR 指令为 LLVM 指令
    /// </summary>
    private LlvmInstruction? ConvertInstruction(MirInstruction instr)
    {
        return instr switch
        {
            MirAssign assign => ConvertAssign(assign),
            MirCaseInject injection => ConvertCaseInject(injection),
            MirStore store => ConvertStore(store),
            MirCall call => ConvertCall(call),
            MirLoad load => ConvertLoad(load),
            MirBinOp binOp => ConvertBinOp(binOp),
            MirUnaryOp unaryOp => ConvertUnaryOp(unaryOp),
            MirAlloc alloc => ConvertAlloc(alloc),
            MirCopy copy => ConvertCopy(copy),
            MirMove move => ConvertMove(move),
            MirDrop drop => ConvertDrop(drop),
            _ => ReportUnsupportedInstruction(instr)
        };
    }

    /// <summary>
    /// 转换 MIR 终止指令为 LLVM 终止指令
    /// </summary>
    private LlvmTerminator ConvertTerminator(MirTerminator term)
    {
        return term switch
        {
            MirReturn ret => ConvertReturn(ret),
            MirGoto jump => ConvertGoto(jump),
            MirSwitch sw => ConvertSwitch(sw),
            MirUnreachable => LlvmUnreachable.Instance,
            _ => ReportUnsupportedTerminator(term)
        };
    }


    private LlvmInstruction? ConvertAssign(MirAssign assign)
    {
        if (assign.Target.Kind == PlaceKind.Local)
        {
            UpdateGenericFunctionLocalBinding(assign.Target.Local, assign.Source);
        }

        // When assigning a function reference to a local whose TypeId is a known function type,
        // store a typed function pointer instead of materializing a closure object.  This allows
        // subsequent indirect calls through the local to emit a single direct call rather than
        // going through the closure-invoke protocol.
        if (assign.Source is MirFunctionRef funcRef &&
            assign.Target is { Kind: PlaceKind.Local } targetPlace &&
            TryResolveSourceVisibleSignature(targetPlace.TypeId, out var targetSig))
        {
            var funcType = TryResolveFunctionTypeByTypeId(funcRef.Name, funcRef.TypeId, out var specializedType)
                ? specializedType
                : targetSig;
            var funcName = ResolveFunctionLlvmName(funcRef, funcType);
            var typedLocal = new LlvmLocal
            {
                Name = $"l{targetPlace.Local.Value}",
                Type = new LlvmPointerType { ElementType = funcType }
            };
            _locals.LocalMap[targetPlace.Local] = typedLocal;
            _locals.RuntimeWordLocals.Remove(targetPlace.Local);
            return null;
        }

        var sourceValue = assign.Source switch
        {
            MirFunctionRef functionRef => MaterializeFunctionReference(functionRef, assign.Target.TypeId),
            _ => ConvertOperand(assign.Source)
        };
        if (assign.Target.Kind == PlaceKind.Local && IsSlotBackedLocal(assign.Target.Local))
        {
            UpdateRuntimeWordLocalBinding(assign.Target.Local, assign.Source, sourceValue);
            _locals.LocalMap[assign.Target.Local] = new LlvmLocal
            {
                Name = GetAliasName(sourceValue),
                Type = sourceValue.Type
            };
            return CreateStoreToLocalSlot(assign.Target.Local, sourceValue);
        }

        var materialized = TryMaterializeImmediateAssignment(assign.Target, sourceValue);
        if (materialized != null)
        {
            return materialized;
        }

        _locals.LocalMap[GetLocalId(assign.Target)] = new LlvmLocal
        {
            Name = GetAliasName(sourceValue),
            Type = sourceValue.Type
        };
        if (assign.Target.Kind == PlaceKind.Local)
        {
            if (assign.Source is MirPlace { Kind: PlaceKind.Local } sourcePlace &&
                _locals.RuntimeWordLocals.Contains(sourcePlace.Local))
            {
                _locals.RuntimeWordLocals.Add(assign.Target.Local);
            }
            else
            {
                _locals.RuntimeWordLocals.Remove(assign.Target.Local);
            }
        }

        return null;
    }

    private LlvmInstruction? ConvertCaseInject(MirCaseInject injection)
    {
        if (injection.Target is not MirPlace target)
        {
            return ReportUnsupportedInstruction(injection);
        }

        return ConvertAssign(new MirAssign
        {
            Target = target,
            Source = injection.Operand,
            Span = injection.Span
        });
    }

    private LlvmInstruction? ConvertStore(MirStore store)
    {
        if (store.Target.Kind == PlaceKind.Local)
        {
            UpdateGenericFunctionLocalBinding(store.Target.Local, store.Value);
            var value = store.Value switch
            {
                MirFunctionRef functionRef => MaterializeFunctionReference(functionRef, store.Target.TypeId),
                _ => ConvertOperand(store.Value)
            };
            if (IsSlotBackedLocal(store.Target.Local))
            {
                UpdateRuntimeWordLocalBinding(store.Target.Local, store.Value, value);
                _locals.LocalMap[store.Target.Local] = new LlvmLocal
                {
                    Name = GetAliasName(value),
                    Type = value.Type
                };
                return CreateStoreToLocalSlot(store.Target.Local, value);
            }

            var materialized = TryMaterializeImmediateAssignment(store.Target, value);
            if (materialized != null)
            {
                return materialized;
            }

            _locals.LocalMap[store.Target.Local] = new LlvmLocal
            {
                Name = GetAliasName(value),
                Type = value.Type
            };
            if (store.Value is MirPlace { Kind: PlaceKind.Local } sourcePlace &&
                _locals.RuntimeWordLocals.Contains(sourcePlace.Local))
            {
                _locals.RuntimeWordLocals.Add(store.Target.Local);
            }
            else
            {
                _locals.RuntimeWordLocals.Remove(store.Target.Local);
            }

            return null;
        }

        if (store.Target.Kind == PlaceKind.Index &&
            store.Target.IndexAccessKind == MirIndexAccessKind.RuntimeArray &&
            store.Target.Base != null &&
            store.Target.Index != null)
        {
            return ConvertIndexedStore(store);
        }

        return new LlvmStore
        {
            Value = store.Value switch
            {
                MirFunctionRef functionRef => MaterializeFunctionReference(functionRef, store.Target.TypeId),
                _ => ConvertOperand(store.Value)
            },
            Pointer = ResolveStoreTargetPointer(store.Target),
            IsVolatile = false
        };
    }

    private LlvmValue ResolveStoreTargetPointer(MirPlace target)
    {
        if (target.Kind == PlaceKind.Deref && target.Base != null)
        {
            return CoerceToPointer(ConvertPlace(target.Base));
        }

        return ConvertPlace(target);
    }

    private LlvmInstruction? ReportUnsupportedInstruction(MirInstruction instruction)
    {
        var diag = Diagnostic.Diagnostic.Error(
            DiagnosticMessages.UnsupportedMirInstructionDuringLlvmLowering(instruction.GetType().Name),
            "E5200");
        if (HasSpan(instruction.Span))
        {
            diag.WithLabel(instruction.Span, DiagnosticMessages.UnsupportedInstructionLabel);
        }

        if (_currentFunction != null)
        {
            diag.WithNote(DiagnosticMessages.FunctionNote(_currentFunction.Name));
        }

        Diagnostics.Add(diag);
        return null;
    }

    private void UpdateRuntimeWordLocalBinding(LocalId targetLocal, MirOperand sourceOperand, LlvmValue sourceValue)
    {
        if (sourceOperand is MirPlace { Kind: PlaceKind.Local } sourcePlace &&
            _locals.RuntimeWordLocals.Contains(sourcePlace.Local))
        {
            _locals.RuntimeWordLocals.Add(targetLocal);
            return;
        }

        if (sourceValue.Type is LlvmIntType { Bits: 64 })
        {
            _locals.RuntimeWordLocals.Add(targetLocal);
            return;
        }

        _locals.RuntimeWordLocals.Remove(targetLocal);
    }

    private LlvmTerminator ReportUnsupportedTerminator(MirTerminator terminator)
    {
        var diag = Diagnostic.Diagnostic.Error(
            DiagnosticMessages.UnsupportedMirTerminatorDuringLlvmLowering(terminator.GetType().Name),
            "E5201");
        if (HasSpan(terminator.Span))
        {
            diag.WithLabel(terminator.Span, DiagnosticMessages.UnsupportedTerminatorLabel);
        }

        if (_currentFunction != null)
        {
            diag.WithNote(DiagnosticMessages.FunctionNote(_currentFunction.Name));
        }

        Diagnostics.Add(diag);
        return LlvmUnreachable.Instance;
    }

    private LlvmTerminator ReportMissingTerminatorFallback(MirBasicBlock? block)
    {
        var diag = Diagnostic.Diagnostic.Error(
            DiagnosticMessages.MissingTerminatorDuringLlvmLowering,
            "E5202");
        if (block != null && HasSpan(block.Span))
        {
            diag.WithLabel(block.Span, DiagnosticMessages.UnsupportedTerminatorLabel);
        }

        if (_currentFunction != null)
        {
            diag.WithNote(DiagnosticMessages.FunctionNote(_currentFunction.Name));
        }

        diag.WithHelp(DiagnosticMessages.LlvmFallbackLoweredToUnreachableHelp);
        Diagnostics.Add(diag);
        return LlvmUnreachable.Instance;
    }

    private static bool HasSpan(SourceSpan span)
    {
        return span.Length > 0 ||
               span.Location.Position > 0 ||
               span.Location.Line > 0 ||
               span.Location.Column > 0;
    }

    private LlvmInstruction? ConvertLoad(MirLoad load)
    {
        if (load.Source is MirPlace { Kind: PlaceKind.Local } sourceLocal)
        {
            if (_partialCallStates.TryGetValue(sourceLocal.Local, out var partial))
            {
                _partialCallStates[load.Target.Local] = partial;
                _locals.LocalMap.Remove(load.Target.Local);
                _locals.RuntimeWordLocals.Remove(load.Target.Local);
                CopyGenericLocal(load.Target.Local, sourceLocal.Local);
                return null;
            }

            var value = ConvertPlace(sourceLocal);

            if (IsSlotBackedLocal(load.Target.Local))
            {
                return CreateStoreToLocalSlot(load.Target.Local, value);
            }

            // local->local load 不需要额外 emit 指令，直接保持 source alias。
            AssignPlaceFromValue(load.Target, value);
            if (_locals.RuntimeWordLocals.Contains(sourceLocal.Local))
            {
                _locals.RuntimeWordLocals.Add(load.Target.Local);
            }
            else
            {
                _locals.RuntimeWordLocals.Remove(load.Target.Local);
            }
            CopyGenericLocal(load.Target.Local, sourceLocal.Local);
            PropagateBorrowedProjectionLocal(load.Target.Local, sourceLocal.Local);

            return null;
        }

        ClearGenericLocal(load.Target.Local);

        if (load.Source is MirPlace { Kind: PlaceKind.Deref } derefSource &&
            ResolveDerefValueType(derefSource) is LlvmPointerType)
        {
            AssignPlaceFromValue(load.Target, ConvertPlace(derefSource));
            return null;
        }

        if (load.Source is MirPlace { Kind: PlaceKind.Index, IndexAccessKind: MirIndexAccessKind.RuntimeArray } indexSource &&
            indexSource.Base != null &&
            indexSource.Index != null)
        {
            return ConvertIndexedLoad(load, indexSource);
        }

        var target = load.Target;
        var resultName = target != null
            ? GetOrCreateLocal(target).Name
            : _nameMangler.NewTempName("load");
        var loadType = LowerStorageTypeIdOrReport(
            target != null && target.TypeId.IsValid ? target.TypeId : load.Source.TypeId,
            "load result");

        var sourcePointer = load.Source is MirPlace sourcePlace
            ? ResolveStoreTargetPointer(sourcePlace)
            : ConvertOperand(load.Source);

        if (!IsSlotBackedLocal(target!.Local))
        {
            SetBorrowedProjectionLocal(target.Local, IsBorrowedProjectionLoad(load));
            _locals.RuntimeWordLocals.Remove(target.Local);
            _locals.LocalMap[target.Local] = new LlvmLocal
            {
                Name = resultName,
                Type = loadType
            };
        }

        return new LlvmLoad
        {
            Pointer = sourcePointer,
            LoadType = loadType,
            IsVolatile = false,
            ResultName = resultName
        };
    }

    private bool IsBorrowedProjectionLoad(MirLoad load)
    {
        return load.CreatesBorrowAlias &&
               load.Source is MirPlace { Kind: not PlaceKind.Local } &&
               load.Target is { TypeId.IsValid: true } &&
               !IsFfiNonRcPointerType(load.Target.TypeId) &&
               LowerStorageTypeIdOrReport(load.Target.TypeId, "borrowed projection load") is LlvmPointerType;
    }

    private bool IsBorrowedProjectionOperand(MirOperand operand)
    {
        return operand is MirPlace { Kind: PlaceKind.Local, Local: var local } &&
               _borrowedProjectionLocals.Contains(local);
    }

    private void SetBorrowedProjectionLocal(LocalId local, bool isBorrowedProjection)
    {
        if (isBorrowedProjection)
        {
            _borrowedProjectionLocals.Add(local);
        }
        else
        {
            _borrowedProjectionLocals.Remove(local);
        }
    }

    private void PropagateBorrowedProjectionLocal(LocalId target, LocalId source)
    {
        SetBorrowedProjectionLocal(target, _borrowedProjectionLocals.Contains(source));
    }

    private void RetainBorrowedProjectionConstructorField(
        MirOperand operand,
        LlvmValue value,
        bool retainBorrowedProjectionFields)
    {
        if (!retainBorrowedProjectionFields ||
            (!IsBorrowedProjectionOperand(operand) && !IsBorrowedProjectionConstructorArgument(operand)) ||
            value.Type is not LlvmPointerType)
        {
            return;
        }

        _currentBlock!.Instructions.Add(CreateRuntimeRcCall(WellKnownStrings.Runtime.IncRefLocal, value));
    }

    private bool IsBorrowedProjectionConstructorArgument(MirOperand operand)
    {
        if (operand is not MirPlace { Kind: PlaceKind.Local, Local: var local } ||
            !_definitionInstructionsByLocal.TryGetValue(local, out var definitions) ||
            definitions.Count == 0)
        {
            return false;
        }

        foreach (var definition in definitions)
        {
            if (definition is not MirLoad load ||
                load.Source is not MirPlace { Kind: not PlaceKind.Local } ||
                !load.Target.TypeId.IsValid ||
                IsFfiNonRcPointerType(load.Target.TypeId) ||
                LowerStorageTypeIdOrReport(load.Target.TypeId, "constructor borrowed projection field") is not LlvmPointerType)
            {
                return false;
            }
        }

        return true;
    }

    private LlvmInstruction? ConvertIndexedStore(MirStore store)
    {
        if (store.Target.Base == null || store.Target.Index == null)
        {
            return null;
        }

        var arrayValue = ResolveRuntimeArrayBasePointer(store.Target.Base);
        var indexValue = CoerceToI64(ConvertOperand(store.Target.Index));
        var value = ConvertOperand(store.Value);
        var elementType = LowerStorageTypeIdOrReport(store.Target.TypeId, "indexed store element");
        var valuePointer = CreateAddressableValuePointer(value, elementType);

        return new LlvmCall
        {
            Function = CreateRuntimeFunctionGlobal(
                WellKnownStrings.Runtime.ArraySet,
                LlvmVoidType.Instance,
                [LlvmPointerType.VoidPtr(), LlvmIntType.I64, LlvmPointerType.VoidPtr(), LlvmIntType.I64]),
            Arguments =
            [
                arrayValue,
                indexValue,
                valuePointer,
                new LlvmConstant
                {
                    Value = GetRuntimeElementSize(store.Target.TypeId),
                    Type = LlvmIntType.I64
                }
            ],
            ReturnType = LlvmVoidType.Instance
        };
    }

    private LlvmInstruction? ConvertIndexedLoad(MirLoad load, MirPlace indexSource)
    {
        var arrayValue = ResolveRuntimeArrayBasePointer(indexSource.Base!);
        var indexValue = CoerceToI64(ConvertOperand(indexSource.Index!));
        var elementType = ResolveRuntimeArrayElementType(indexSource);
        var targetLocalId = load.Target.Local;
        var targetUsesSlot = IsSlotBackedLocal(targetLocalId);

        if (IsOpaqueRuntimeWordType(elementType) &&
            TryInferRuntimeArrayElementTypeFromLocalUses(targetLocalId, out var inferredElementType))
        {
            elementType = inferredElementType;
        }

        var getCall = new LlvmCall
        {
            Function = CreateRuntimeFunctionGlobal(
                WellKnownStrings.Runtime.ArrayGet,
                LlvmPointerType.VoidPtr(),
                [LlvmPointerType.VoidPtr(), LlvmIntType.I64]),
            Arguments = [arrayValue, indexValue],
            ReturnType = LlvmPointerType.VoidPtr(),
            ResultName = _nameMangler.NewTempName("array_get")
        };
        _currentBlock?.Instructions.Add(getCall);

        if (elementType is LlvmVoidType)
        {
            if (load.Target is MirPlace { Kind: PlaceKind.Local } unitTarget)
            {
                if (!IsSlotBackedLocal(unitTarget.Local))
                {
                    var targetLocal = GetOrCreateLocal(unitTarget);
                    _locals.LocalMap[unitTarget.Local] = new LlvmLocal
                    {
                        Name = targetLocal.Name,
                        Type = LlvmVoidType.Instance
                    };
                }
            }

            return null;
        }

        var resultName = targetUsesSlot
            ? _nameMangler.NewTempName($"l{targetLocalId.Value}_idx")
            : GetOrCreateLocal(load.Target).Name;
        var typedLoad = new LlvmLoad
        {
            Pointer = new LlvmInstructionRef
            {
                Instruction = getCall,
                Type = LlvmPointerType.VoidPtr()
            },
            LoadType = elementType,
            IsVolatile = false,
            ResultName = resultName
        };

        if (targetUsesSlot)
        {
            _locals.RuntimeWordLocals.Remove(targetLocalId);
            QueueStoreToLocalSlot(targetLocalId, new LlvmInstructionRef
            {
                Instruction = typedLoad,
                Type = elementType
            });
        }
        else
        {
            _locals.RuntimeWordLocals.Remove(targetLocalId);
            _locals.LocalMap[targetLocalId] = new LlvmLocal
            {
                Name = resultName,
                Type = elementType
            };
        }

        return typedLoad;
    }

    private LlvmInstruction ConvertBinOp(MirBinOp binOp)
    {
        var left = ConvertOperand(binOp.Left);
        var right = ConvertOperand(binOp.Right);
        var targetLocalId = TryGetTargetLocal(binOp.Target);
        var targetUsesSlot = targetLocalId is { } id && IsSlotBackedLocal(id);

        var resultName = binOp.Target != null
            ? targetUsesSlot
                ? _nameMangler.NewTempName($"l{targetLocalId!.Value}_binop")
                : GetOrCreateLocalFromOperand(binOp.Target, "binary operation target").Name
            : _nameMangler.NewTempName("binop");

        if (binOp.Operator is BinaryOp.Eq or BinaryOp.Ne or BinaryOp.Lt or BinaryOp.Le or BinaryOp.Gt or BinaryOp.Ge)
        {
            if (left.Type is LlvmFloatType || right.Type is LlvmFloatType)
            {
                var fcmp = new LlvmFcmp
                {
                    Predicate = binOp.Operator switch
                    {
                        BinaryOp.Eq => "oeq",
                        BinaryOp.Ne => "one",
                        BinaryOp.Lt => "olt",
                        BinaryOp.Le => "ole",
                        BinaryOp.Gt => "ogt",
                        BinaryOp.Ge => "oge",
                        _ => "oeq"
                    },
                    Left = left,
                    Right = right,
                    ResultName = resultName
                };

                if (targetUsesSlot)
                {
                    QueueStoreToLocalSlot(targetLocalId!.Value, new LlvmInstructionRef
                    {
                        Instruction = fcmp,
                        Type = LlvmIntType.I1
                    });
                }

                return fcmp;
            }

            (left, right) = NormalizeComparisonOperands(left, right, binOp.Operator);

            var icmp = new LlvmIcmp
            {
                Predicate = binOp.Operator switch
                {
                    BinaryOp.Eq => "eq",
                    BinaryOp.Ne => "ne",
                    BinaryOp.Lt => "slt",
                    BinaryOp.Le => "sle",
                    BinaryOp.Gt => "sgt",
                    BinaryOp.Ge => "sge",
                    _ => "eq"
                },
                Left = left,
                Right = right,
                ResultName = resultName
            };

            if (targetUsesSlot)
            {
                QueueStoreToLocalSlot(targetLocalId!.Value, new LlvmInstructionRef
                {
                    Instruction = icmp,
                    Type = LlvmIntType.I1
                });
            }

            return icmp;
        }

        if (binOp.Operator == BinaryOp.Concat)
        {
            var concatCall = new LlvmCall
            {
                Function = CreateRuntimeFunctionGlobal(
                    WellKnownStrings.Runtime.StringConcat,
                    LlvmPointerType.VoidPtr(),
                    [LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr()]),
                Arguments = [CoerceToPointer(left), CoerceToPointer(right)],
                ReturnType = LlvmPointerType.VoidPtr(),
                ResultName = resultName
            };

            if (targetUsesSlot)
            {
                QueueStoreToLocalSlot(targetLocalId!.Value, new LlvmInstructionRef
                {
                    Instruction = concatCall,
                    Type = LlvmPointerType.VoidPtr()
                });
            }

            return concatCall;
        }

        var resultType = LowerTypeIdOrReport(binOp.Left.TypeId, "binary operation operand");
        var op = binOp.Operator switch
        {
            BinaryOp.Add => left.Type is LlvmFloatType ? "fadd" : "add",
            BinaryOp.Sub => left.Type is LlvmFloatType ? "fsub" : "sub",
            BinaryOp.Mul => left.Type is LlvmFloatType ? "fmul" : "mul",
            BinaryOp.Div => left.Type is LlvmFloatType ? "fdiv" : "sdiv",
            BinaryOp.Mod => left.Type is LlvmFloatType ? "frem" : "srem",
            BinaryOp.And => "and",
            BinaryOp.Or => "or",
            _ => "add"
        };

        if (binOp.Operator is BinaryOp.Add or BinaryOp.Sub or BinaryOp.Mul or BinaryOp.Div or BinaryOp.Mod)
        {
            if (left.Type is LlvmFloatType || right.Type is LlvmFloatType)
            {
                resultType = LlvmFloatType.Double;
                left = CoerceValueToType(left, resultType, "bin_l");
                right = CoerceValueToType(right, resultType, "bin_r");
            }
            else
            {
                resultType = LlvmIntType.I64;
                left = CoerceToI64(left);
                right = CoerceToI64(right);
            }
        }
        else if (binOp.Operator is BinaryOp.And or BinaryOp.Or)
        {
            resultType = LlvmIntType.I1;
            left = CoerceIntegerToWidth(left, 1, "bin_l");
            right = CoerceIntegerToWidth(right, 1, "bin_r");
        }

        var llvmBinOp = new LlvmBinOp
        {
            Op = op,
            Left = left,
            Right = right,
            ResultType = resultType,
            ResultName = resultName
        };

        if (targetUsesSlot)
        {
            QueueStoreToLocalSlot(targetLocalId!.Value, new LlvmInstructionRef
            {
                Instruction = llvmBinOp,
                Type = resultType
            });
        }
        else if (targetLocalId is { } localId)
        {
            _locals.RuntimeWordLocals.Remove(localId);
            _locals.LocalMap[localId] = new LlvmLocal
            {
                Name = resultName,
                Type = resultType
            };
        }

        return llvmBinOp;
    }

    private (LlvmValue Left, LlvmValue Right) NormalizeComparisonOperands(
        LlvmValue left,
        LlvmValue right,
        BinaryOp comparisonOp)
    {
        if (left.Type is LlvmPointerType && right.Type is LlvmPointerType)
        {
            return comparisonOp is BinaryOp.Eq or BinaryOp.Ne
                ? (left, right)
                : (CoerceToI64(left), CoerceToI64(right));
        }

        if (left.Type is LlvmPointerType || right.Type is LlvmPointerType)
        {
            return (CoerceToI64(left), CoerceToI64(right));
        }

        if (left.Type is LlvmIntType leftInt && right.Type is LlvmIntType rightInt && leftInt.Bits != rightInt.Bits)
        {
            return (CoerceToI64(left), CoerceToI64(right));
        }

        return (left, right);
    }

    private LlvmInstruction ConvertUnaryOp(MirUnaryOp unaryOp)
    {
        var operand = ConvertOperand(unaryOp.Operand);
        var resultType = LowerTypeIdOrReport(unaryOp.Operand.TypeId, "unary operation operand");
        var targetLocalId = TryGetTargetLocal(unaryOp.Target);
        var targetUsesSlot = targetLocalId is { } id && IsSlotBackedLocal(id);

        var resultName = unaryOp.Target != null
            ? targetUsesSlot
                ? _nameMangler.NewTempName($"l{targetLocalId!.Value}_unary")
                : GetOrCreateLocalFromOperand(unaryOp.Target, "unary operation target").Name
            : _nameMangler.NewTempName("unary");

        LlvmInstruction llvmUnaryOp = unaryOp.Operator switch
        {
            UnaryOp.Not => new LlvmBinOp
            {
                Op = "xor",
                Left = CoerceIntegerToWidth(operand, 1, "not_l"),
                Right = new LlvmConstant
                {
                    Value = true,
                    Type = LlvmIntType.I1
                },
                ResultType = LlvmIntType.I1,
                ResultName = resultName
            },
            UnaryOp.Neg when resultType is LlvmFloatType => new LlvmUnaryOp
            {
                Op = "fneg",
                Operand = operand,
                ResultType = resultType,
                ResultName = resultName
            },
            UnaryOp.Neg => new LlvmBinOp
            {
                Op = "sub",
                Left = new LlvmConstant
                {
                    Value = 0L,
                    Type = LlvmIntType.I64
                },
                Right = CoerceToI64(operand),
                ResultType = LlvmIntType.I64,
                ResultName = resultName
            },
            _ => new LlvmUnaryOp
            {
                Op = "fneg",
                Operand = operand,
                ResultType = resultType,
                ResultName = resultName
            }
        };

        if (targetUsesSlot)
        {
            QueueStoreToLocalSlot(targetLocalId!.Value, new LlvmInstructionRef
            {
                Instruction = llvmUnaryOp,
                Type = resultType
            });
        }
        else if (targetLocalId is { } localId)
        {
            _locals.RuntimeWordLocals.Remove(localId);
            _locals.LocalMap[localId] = new LlvmLocal
            {
                Name = resultName,
                Type = resultType
            };
        }

        return llvmUnaryOp;
    }

    private LlvmInstruction? ConvertAlloc(MirAlloc alloc)
    {
        var targetLocalId = alloc.Target.Local;
        var targetUsesSlot = IsSlotBackedLocal(targetLocalId);
        ClearGenericLocal(targetLocalId);
        var resultName = targetUsesSlot
            ? _nameMangler.NewTempName($"l{targetLocalId.Value}_alloc")
            : GetOrCreateLocal(alloc.Target).Name;

        var alloca = new LlvmAlloca
        {
            AllocatedType = GetAggregateStorageType(targetLocalId, alloc.TypeId),
            Alignment = 8, // 默认 8 字节对齐
            ResultName = resultName
        };

        // Stack allocation must be static (one slot per function); hoist to the entry
        // block so an alloc inside a loop body does not grow the stack each iteration.
        EmitAllocaInEntryBlock(alloca);

        if (targetUsesSlot)
        {
            // The store of the alloca address into the entry-block slot is a store
            // (not an alloca), so it stays in the current block via the post-buffer.
            QueueStoreToLocalSlot(targetLocalId, new LlvmInstructionRef
            {
                Instruction = alloca,
                Type = LlvmPointerType.VoidPtr()
            });
        }
        else
        {
            _locals.RuntimeWordLocals.Remove(targetLocalId);
            _locals.LocalMap[targetLocalId] = new LlvmLocal
            {
                Name = resultName,
                Type = LlvmPointerType.VoidPtr()
            };
        }

        // Already placed in the entry block above; return null so the dispatch loop
        // does not also append it to the (possibly loop-body) current block.
        return null;
    }

    private LlvmInstruction? ConvertCopy(MirCopy copy)
    {
        var sourceIsBorrowedProjection = _borrowedProjectionLocals.Contains(copy.Source.Local);

        if (copy.Source.Kind == PlaceKind.Local &&
            _partialCallStates.TryGetValue(copy.Source.Local, out var partial))
        {
            _partialCallStates[copy.Target.Local] = partial;
            _locals.LocalMap.Remove(copy.Target.Local);
            _locals.RuntimeWordLocals.Remove(copy.Target.Local);
            CopyGenericLocal(copy.Target.Local, copy.Source.Local);
            return null;
        }

        var sourceValue = ConvertPlace(copy.Source);
        AssignPlaceFromValue(copy.Target, sourceValue);
        if (_locals.RuntimeWordLocals.Contains(copy.Source.Local))
        {
            _locals.RuntimeWordLocals.Add(copy.Target.Local);
        }
        else
        {
            _locals.RuntimeWordLocals.Remove(copy.Target.Local);
        }
        CopyGenericLocal(copy.Target.Local, copy.Source.Local);
        SetBorrowedProjectionLocal(copy.Target.Local, false);

        if (_locals.RuntimeWordLocals.Contains(copy.Source.Local))
        {
            return null;
        }

        if (TryInferRuntimeArrayElementTypeFromLocalUses(copy.Target.Local, out var inferredCopiedType) &&
            !IsOpaqueRuntimeWordType(inferredCopiedType))
        {
            _locals.RuntimeWordLocals.Add(copy.Source.Local);
            _locals.RuntimeWordLocals.Add(copy.Target.Local);
            return null;
        }

        if (!IsManagedRcType(copy.Source.TypeId))
        {
            return null;
        }

        // Stack Promotion 优化：栈分配的值无需 RC incref
        if (copy.Source.Kind == PlaceKind.Local &&
            _currentStackPromotionHints?.PromotedLocals.Contains(copy.Source.Local) == true)
        {
            return null;
        }

        // Perceus 优化：如果此位置的 dup 可以省略，跳过 incref
        if (!sourceIsBorrowedProjection &&
            _currentOmitDup != null && _currentBlockId.HasValue &&
            _currentOmitDup.Contains((_currentBlockId.Value, _currentInstructionIndex)))
        {
            return null;
        }

        // 对托管类型 copy 需要增加引用计数
        return CreateRuntimeRcCall(WellKnownStrings.Runtime.IncRefLocal, sourceValue);
    }

    private LlvmInstruction? ConvertMove(MirMove move)
    {
        if (move.Source.Kind == PlaceKind.Local &&
            _partialCallStates.TryGetValue(move.Source.Local, out var partial))
        {
            _partialCallStates[move.Target.Local] = partial;
            _locals.LocalMap.Remove(move.Target.Local);
            _locals.RuntimeWordLocals.Remove(move.Target.Local);
            CopyGenericLocal(move.Target.Local, move.Source.Local);
            _partialCallStates.Remove(move.Source.Local);
            ClearGenericLocal(move.Source.Local);

            if (move.Source.Kind == PlaceKind.Local)
            {
                _locals.LocalMap.Remove(move.Source.Local);
                _locals.RuntimeWordLocals.Remove(move.Source.Local);
            }

            return null;
        }

        var sourceValue = ConvertPlace(move.Source);
        AssignPlaceFromValue(move.Target, sourceValue);
        if (_locals.RuntimeWordLocals.Contains(move.Source.Local))
        {
            _locals.RuntimeWordLocals.Add(move.Target.Local);
        }
        else
        {
            _locals.RuntimeWordLocals.Remove(move.Target.Local);
        }
        CopyGenericLocal(move.Target.Local, move.Source.Local);
        PropagateBorrowedProjectionLocal(move.Target.Local, move.Source.Local);

        if (move.Source.Kind == PlaceKind.Local)
        {
            // move 后 source 视为失效。这里保留一个“已定义”的别名占位，
            // 避免线性 lowering 跨块时生成未定义 SSA 名。
            InvalidateLocalAlias(move.Source.Local, move.Source.TypeId);
            _borrowedProjectionLocals.Remove(move.Source.Local);
        }

        return null;
    }

    private LlvmInstruction? ConvertDrop(MirDrop drop)
    {
        var value = ConvertOperand(drop.Value);
        var isBorrowedProjectionDrop = drop.Value is MirPlace { Kind: PlaceKind.Local } borrowedDropLocal &&
                                       _borrowedProjectionLocals.Contains(borrowedDropLocal.Local);

        if (drop.Value is MirPlace { Kind: PlaceKind.Local } localPlace)
        {
            InvalidateLocalAlias(localPlace.Local, localPlace.TypeId);
            _borrowedProjectionLocals.Remove(localPlace.Local);
            _partialCallStates.Remove(localPlace.Local);
            ClearGenericLocal(localPlace.Local);
        }

        if (!IsManagedRcType(drop.Value.TypeId))
        {
            return null;
        }

        if (isBorrowedProjectionDrop)
        {
            return null;
        }

        // Stack Promotion 优化：栈分配的值无需整体 RC decref，也无需逐字段 decref。
        // EmitInlineConstructorFieldStores 对栈提升值不生成 incref，因此对应地也不需要 decref。
        if (drop.Value is MirPlace { Kind: PlaceKind.Local } dropLocal &&
            (_currentStackPromotionHints?.PromotedLocals.Contains(dropLocal.Local) == true ||
             _currentUnifiedHints?.PromotedLocals.Contains(dropLocal.Local) == true))
        {
            return null;
        }

        // Perceus 优化：如果此位置的 drop 可以省略（值已被移动），跳过 decref
        if (_currentOmitDrop != null && _currentBlockId.HasValue &&
            _currentOmitDrop.Contains((_currentBlockId.Value, _currentInstructionIndex)))
        {
            return null;
        }

        // Reuse 优化：如果此位置的 drop 可以参与内存复用，发 eidos_drop_reuse
        if (_currentReuseHints != null && _currentBlockId.HasValue &&
            _currentReuseHints.DropReuseSites.TryGetValue(
                (_currentBlockId.Value, _currentInstructionIndex), out var reuseSlot))
        {
            var reuseSlotPtr = GetOrCreateReuseSlotAlloca(reuseSlot);
            return new LlvmCall
            {
                Function = new LlvmGlobal
                {
                    Name = WellKnownStrings.Runtime.DropReuse,
                    Type = new LlvmFunctionType
                    {
                        ReturnType = LlvmVoidType.Instance,
                        ParameterTypes = [LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr()]
                    }
                },
                Arguments = [CoerceToPointer(value), reuseSlotPtr],
                ReturnType = LlvmVoidType.Instance,
                ResultName = _nameMangler.NewTempName("drop_reuse")
            };
        }

        // 对托管类型 drop 需要减少引用计数。
        return CreateRuntimeRcCall(WellKnownStrings.Runtime.DecRefLocal, value);
    }


    private void AssignPlaceFromValue(MirPlace target, LlvmValue sourceValue)
    {
        if (target.Kind != PlaceKind.Local)
        {
            return;
        }

        _borrowedProjectionLocals.Remove(target.Local);
        _partialCallStates.Remove(target.Local);

        if (IsSlotBackedLocal(target.Local))
        {
            _locals.LocalMap[target.Local] = new LlvmLocal
            {
                Name = GetAliasName(sourceValue),
                Type = sourceValue.Type
            };
            QueueStoreToLocalSlot(target.Local, sourceValue);
            return;
        }

        var materialized = TryMaterializeImmediateAssignment(target, sourceValue);
        if (materialized != null)
        {
            _currentBlock?.Instructions.Add(materialized);
            return;
        }

        var targetLocal = GetOrCreateLocal(target);
        var aliasName = targetLocal.Name;

        if (sourceValue is LlvmLocal sourceLocal)
        {
            aliasName = sourceLocal.Name;
        }
        else if (sourceValue is LlvmInstructionRef { Instruction: { ResultName: { Length: > 0 } resultName } })
        {
            aliasName = resultName;
        }

        _locals.LocalMap[target.Local] = new LlvmLocal
        {
            Name = aliasName,
            Type = sourceValue.Type
        };
    }

    private LlvmCall CreateRuntimeRcCall(string functionName, LlvmValue value)
    {
        return new LlvmCall
        {
            Function = new LlvmGlobal
            {
                Name = functionName,
                Type = new LlvmFunctionType
                {
                    ReturnType = LlvmVoidType.Instance,
                    ParameterTypes = [LlvmPointerType.VoidPtr()]
                }
            },
            Arguments = [CoerceToPointer(value)],
            ReturnType = LlvmVoidType.Instance,
            ResultName = _nameMangler.NewTempName(functionName.Contains(WellKnownStrings.Runtime.IncRefShort, StringComparison.Ordinal) ? WellKnownStrings.Runtime.IncRefShort : WellKnownStrings.Runtime.DecRefShort)
        };
    }

    /// <summary>
    /// EidosReuse struct 的 LLVM 类型: { ptr, i64, i64 }（对齐后）
    /// </summary>
    private static readonly LlvmStructType EidosReuseType = new()
    {
        Fields = [LlvmPointerType.VoidPtr(), LlvmIntType.I64, LlvmIntType.I64],
        IsLiteral = true
    };

    /// <summary>
    /// 获取或创建复用槽的 alloca 指令。
    /// <summary>
    /// Resolves the block where stack <c>alloca</c>s must be inserted: the function
    /// entry block. Stack allocation must be static (one allocation per function),
    /// so an <c>alloca</c> placed in a loop body would grow the stack every iteration
    /// and eventually overflow. Falls back to <c>_currentBlock</c> while the entry
    /// block itself is still being converted (BasicBlocks is empty until it returns).
    /// </summary>
    private LlvmBasicBlock GetAllocaInsertionBlock() =>
        _currentFunction != null && _currentFunction.BasicBlocks.Count > 0
            ? _currentFunction.BasicBlocks[0]
            : _currentBlock!;

    /// <summary>
    /// Inserts an <c>alloca</c> at the front of the entry block so the slot is
    /// allocated once and dominates all uses. Use this for every stack allocation.
    /// </summary>
    private void EmitAllocaInEntryBlock(LlvmAlloca alloca) =>
        GetAllocaInsertionBlock().Instructions.Insert(0, alloca);

    /// <summary>
    /// 惰性分配：首次使用时在当前块的入口处插入 alloca。
    /// </summary>
    private LlvmValue GetOrCreateReuseSlotAlloca(int slotNumber)
    {
        if (_reuseSlotAllocas.TryGetValue(slotNumber, out var existing))
        {
            return new LlvmInstructionRef
            {
                Instruction = existing,
                Type = new LlvmPointerType { ElementType = EidosReuseType }
            };
        }

        var alloca = new LlvmAlloca
        {
            AllocatedType = EidosReuseType,
            Alignment = 8,
            ResultName = _nameMangler.NewTempName($"reuse_slot_{slotNumber}")
        };

        _reuseSlotAllocas[slotNumber] = alloca;
        _currentFunction!.BasicBlocks[0].Instructions.Insert(0, alloca);

        return new LlvmInstructionRef
        {
            Instruction = alloca,
            Type = new LlvmPointerType { ElementType = EidosReuseType }
        };
    }

    private void InvalidateLocalAlias(LocalId localId, TypeId typeId)
    {
        // Mark the local as consumed by clearing derived state only.
        // Do NOT emit an SSA alias seed (bitcast/add) into _currentBlock — doing so
        // inside a branch arm creates a definition that does not dominate the merge
        // point, causing LLVM verifier errors.
        _partialCallStates.Remove(localId);
        _locals.RuntimeWordLocals.Remove(localId);
        ClearGenericLocal(localId);
    }


    private LlvmValue CoerceToPointer(LlvmValue value)
    {
        if (value.Type is LlvmPointerType)
        {
            return value;
        }

        if (value.Type is LlvmVoidType)
        {
            return LlvmNullPointer.Instance;
        }

        if (value.Type is LlvmStructType or LlvmArrayType)
        {
            return CreateAddressableValuePointer(value, value.Type);
        }

        var cast = new LlvmCast
        {
            Op = "inttoptr",
            Value = value,
            TargetType = LlvmPointerType.VoidPtr(),
            ResultName = _nameMangler.NewTempName("coerce_inttoptr")
        };
        _currentBlock?.Instructions.Add(cast);
        return new LlvmInstructionRef
        {
            Instruction = cast,
            Type = LlvmPointerType.VoidPtr()
        };
    }

    private LlvmValue CoerceToI64(LlvmValue value)
    {
        if (value is LlvmConstant { Type: LlvmPointerType } pointerConstant &&
            TryGetIntegerLikeConstantValue(pointerConstant.Value, out var pointerWord))
        {
            return new LlvmConstant
            {
                Value = pointerWord,
                Type = LlvmIntType.I64
            };
        }

        if (value.Type is LlvmIntType intType)
        {
            if (intType.Bits == 64)
            {
                return value;
            }

            if (intType.Bits < 64)
            {
                var zext = new LlvmZext
                {
                    Value = value,
                    TargetType = LlvmIntType.I64,
                    ResultName = _nameMangler.NewTempName("idx_zext")
                };
                _currentBlock?.Instructions.Add(zext);
                return new LlvmInstructionRef
                {
                    Instruction = zext,
                    Type = LlvmIntType.I64
                };
            }

            var trunc = new LlvmTrunc
            {
                Value = value,
                TargetType = LlvmIntType.I64,
                ResultName = _nameMangler.NewTempName("idx_trunc")
            };
            _currentBlock?.Instructions.Add(trunc);
            return new LlvmInstructionRef
            {
                Instruction = trunc,
                Type = LlvmIntType.I64
            };
        }

        if (value.Type is LlvmPointerType)
        {
            var cast = new LlvmCast
            {
                Op = "ptrtoint",
                Value = value,
                TargetType = LlvmIntType.I64,
                ResultName = _nameMangler.NewTempName("coerce_ptrtoint")
            };
            _currentBlock?.Instructions.Add(cast);
            return new LlvmInstructionRef
            {
                Instruction = cast,
                Type = LlvmIntType.I64
            };
        }

        return new LlvmConstant
        {
            Value = 0L,
            Type = LlvmIntType.I64
        };
    }

    private LlvmValue CoerceDispatchWordForCurrentBlock(LlvmValue value, LlvmType targetType, string tempPrefix)
    {
        return CoerceDispatchWordToType(
            value,
            targetType,
            tempPrefix,
            instruction =>
            {
                _currentBlock?.Instructions.Add(instruction);
                return _currentBlock != null;
            });
    }

    private LlvmValue CoerceDispatchWordForPostBuffer(LlvmValue value, LlvmType targetType, string tempPrefix)
    {
        return CoerceDispatchWordToType(
            value,
            targetType,
            tempPrefix,
            instruction =>
            {
                _postInstructionBuffer.Add(instruction);
                return true;
            });
    }

    private LlvmValue CoerceDispatchWordToType(
        LlvmValue value,
        LlvmType targetType,
        string tempPrefix,
        Func<LlvmInstruction, bool> appendInstruction)
    {
        if (targetType is LlvmVoidType)
        {
            return LlvmVoid.Instance;
        }

        if (targetType == value.Type)
        {
            return value;
        }

        if (targetType is LlvmPointerType pointerType &&
            value.Type is LlvmIntType intSource)
        {
            var machineWord = intSource.Bits == 64
                ? value
                : CoerceDispatchWordToType(value, LlvmIntType.I64, $"{tempPrefix}_i64", appendInstruction);

            var cast = new LlvmCast
            {
                Op = "inttoptr",
                Value = machineWord,
                TargetType = pointerType,
                ResultName = _nameMangler.NewTempName($"{tempPrefix}_inttoptr")
            };

            if (!appendInstruction(cast))
            {
                return value;
            }

            return new LlvmInstructionRef
            {
                Instruction = cast,
                Type = pointerType
            };
        }

        if (targetType is LlvmIntType intTarget &&
            value.Type is LlvmPointerType)
        {
            if (value is LlvmConstant pointerConstant &&
                TryGetIntegerLikeConstantValue(pointerConstant.Value, out var pointerWord))
            {
                var constantWord = new LlvmConstant
                {
                    Value = pointerWord,
                    Type = LlvmIntType.I64
                };

                if (intTarget.Bits == 64)
                {
                    return constantWord;
                }

                return CoerceDispatchWordToType(constantWord, intTarget, $"{tempPrefix}_int", appendInstruction);
            }

            var ptrToInt = new LlvmCast
            {
                Op = "ptrtoint",
                Value = value,
                TargetType = LlvmIntType.I64,
                ResultName = _nameMangler.NewTempName($"{tempPrefix}_ptrtoint")
            };

            if (!appendInstruction(ptrToInt))
            {
                return value;
            }

            var ptrWord = new LlvmInstructionRef
            {
                Instruction = ptrToInt,
                Type = LlvmIntType.I64
            };

            if (intTarget.Bits == 64)
            {
                return ptrWord;
            }

            return CoerceDispatchWordToType(ptrWord, intTarget, $"{tempPrefix}_int", appendInstruction);
        }

        if (targetType is LlvmIntType intTargetType &&
            value.Type is LlvmIntType intValueType)
        {
            if (intValueType.Bits == intTargetType.Bits)
            {
                return value;
            }

            if (intValueType.Bits < intTargetType.Bits)
            {
                var zext = new LlvmZext
                {
                    Value = value,
                    TargetType = intTargetType,
                    ResultName = _nameMangler.NewTempName($"{tempPrefix}_zext")
                };

                if (!appendInstruction(zext))
                {
                    return value;
                }

                return new LlvmInstructionRef
                {
                    Instruction = zext,
                    Type = intTargetType
                };
            }

            var trunc = new LlvmTrunc
            {
                Value = value,
                TargetType = intTargetType,
                ResultName = _nameMangler.NewTempName($"{tempPrefix}_trunc")
            };

            if (!appendInstruction(trunc))
            {
                return value;
            }

            return new LlvmInstructionRef
            {
                Instruction = trunc,
                Type = intTargetType
            };
        }

        return value;
    }

    private LlvmValue CreateAddressableValuePointer(LlvmValue value, LlvmType valueType)
    {
        valueType = TypeLowering.NormalizeStorageType(valueType);

        if (valueType is LlvmVoidType || value.Type is LlvmVoidType)
        {
            return LlvmNullPointer.Instance;
        }

        if (value is LlvmInstructionRef { Instruction: LlvmAlloca allocaInstruction } &&
            valueType is LlvmStructType or LlvmArrayType)
        {
            return new LlvmInstructionRef
            {
                Instruction = allocaInstruction,
                Type = LlvmPointerType.VoidPtr()
            };
        }

        if ((valueType is LlvmStructType or LlvmArrayType) && value.Type is LlvmPointerType)
        {
            return CoerceToPointer(value);
        }

        var slot = new LlvmAlloca
        {
            AllocatedType = valueType,
            Alignment = 8,
            ResultName = _nameMangler.NewTempName("arr_slot")
        };
        EmitAllocaInEntryBlock(slot);

        var slotRef = new LlvmInstructionRef
        {
            Instruction = slot,
            Type = LlvmPointerType.VoidPtr()
        };
        _currentBlock?.Instructions.Add(new LlvmStore
        {
            Value = value,
            Pointer = slotRef,
            IsVolatile = false
        });

        return slotRef;
    }
}
