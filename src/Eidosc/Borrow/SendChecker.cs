using Eidosc.Diagnostic;
using Eidosc.Mir;
using Eidosc.Types;

namespace Eidosc.Borrow;

/// <summary>
/// Send 检查器 —— 验证 spawn 操作的闭包捕获值满足 Send trait。
///
/// Send trait 表示类型可以安全地跨线程传递。
/// 基础类型（Int, Float, Bool, Char, Unit, String）自动满足 Send。
/// 包含非 Send 字段的类型（如 Ref&lt;T&gt;）不满足 Send。
///
/// 当前实现检查 spawn 的直接参数；当参数是本函数内可追踪的 partial application 闭包时，
/// 会展开其捕获参数并逐项检查 Send。无法唯一追踪的函数/闭包值仍按非 Send 保守处理。
/// </summary>
public sealed class SendChecker
{
    private readonly MirFunc _function;
    private readonly MirModule _module;
    private Dictionary<LocalId, MirCall?>? _definingCallsByLocal;
    private Dictionary<LocalId, LocalId?>? _localAliasesByLocal;

    /// <summary>
    /// 检测到的 Send 错误
    /// </summary>
    public List<SendCheckError> Errors { get; } = [];

    public SendChecker(MirFunc function, MirModule module)
    {
        _function = function;
        _module = module;
    }

    /// <summary>
    /// 执行 Send 检查
    /// </summary>
    public void Check()
    {
        Errors.Clear();

        foreach (var block in _function.BasicBlocks)
        {
            for (int i = 0; i < block.Instructions.Count; i++)
            {
                if (block.Instructions[i] is MirCall call && IsSpawnCall(call))
                {
                    CheckSpawnCaptures(call.Arguments, block.Id, i);
                }
            }
        }
    }

    private static bool IsSpawnCall(MirCall call)
    {
        return call.Function is MirFunctionRef function &&
               function.FunctionId.Name is "spawn" or "spawn_raw" or
                   "spawn_closure_raw_runtime" or "spawn_closure_value_runtime";
    }

    private void CheckSpawnCaptures(IReadOnlyList<MirOperand> arguments, BlockId block, int index)
    {
        foreach (var arg in arguments)
        {
            if (TryGetClosureCaptureTypes(arg, out var captureTypes))
            {
                foreach (var captureType in captureTypes)
                {
                    if (!IsSendType(captureType))
                    {
                        Errors.Add(new SendCheckError(
                            DiagnosticMessages.SpawnArgumentTypeMustImplementSend(captureType),
                            block,
                            index));
                    }
                }

                continue;
            }

            if (!IsSendType(arg.TypeId))
            {
                Errors.Add(new SendCheckError(
                    DiagnosticMessages.SpawnArgumentTypeMustImplementSend(arg.TypeId),
                    block,
                    index));
            }
        }
    }

    /// <summary>
    /// 判断 TypeId 是否表示满足 Send trait 的类型。
    ///
    /// Send 类型判定规则：
    /// - 基础值类型（Int, Float, Bool, Char, Unit）：天然 Send
    /// - String：不可变，线程安全
    /// - Tuple(A, B)：Send iff 所有元素 Send
    /// - TyCon（ADT）：Send iff 所有类型参数 Send
    /// - Function（闭包）：Send iff 所有捕获类型 Send（保守为非 Send）
    /// - Ref/MRef：非 Send（共享可变状态）
    /// - TyVar：Send（泛型参数，延迟检查）
    /// </summary>
    private bool IsSendType(TypeId typeId, HashSet<int>? visited = null)
    {
        if (!typeId.IsValid)
        {
            return true;
        }

        var id = typeId.Value;

        if (id == BaseTypes.IntId ||
            id == BaseTypes.FloatId ||
            id == BaseTypes.BoolId ||
            id == BaseTypes.CharId ||
            id == BaseTypes.UnitId)
        {
            return true;
        }

        if (id == BaseTypes.StringId)
        {
            return true;
        }

        visited ??= [];
        if (!visited.Add(id))
        {
            return true;
        }

        if (!_module.TypeDescriptors.TryGetValue(id, out var descriptor))
        {
            return true;
        }

        return descriptor switch
        {
            TypeDescriptor.Ref or TypeDescriptor.MutRef => false,

            TypeDescriptor.Tuple tuple => tuple.FieldTypes.All(ft => IsSendType(ft, visited)),

            TypeDescriptor.TyCon tyCon => tyCon.TypeArgs.All(ta => IsSendType(ta, visited)),

            // Function values are Send only when TryGetClosureCaptureTypes can recover
            // the concrete closure captures from the defining partial application.
            TypeDescriptor.Function => false,

            TypeDescriptor.TypeVar => true,

            TypeDescriptor.Builtin => true,

            _ => false
        };
    }

    private bool TryGetClosureCaptureTypes(MirOperand operand, out IReadOnlyList<TypeId> captureTypes)
    {
        captureTypes = [];

        if (operand is not MirPlace { Kind: PlaceKind.Local, Local: var local })
        {
            return false;
        }

        if (!TryResolveLocalAlias(local, out var resolvedLocal))
        {
            return false;
        }

        var definitions = GetDefiningCallsByLocal();
        if (!definitions.TryGetValue(resolvedLocal, out var definingCall) ||
            definingCall == null ||
            definingCall.Function is not MirFunctionRef functionRef)
        {
            return false;
        }

        var calleeKey = MirFunctionIdentity.GetStableKey(functionRef);
        var callee = _module.Functions.FirstOrDefault(function => MirFunctionIdentity.GetStableKey(function) == calleeKey);
        if (callee == null)
        {
            return false;
        }

        var parameterCount = callee.Locals.Count(static local => local.IsParameter);
        if (definingCall.Arguments.Count >= parameterCount)
        {
            return false;
        }

        captureTypes = definingCall.Arguments.Select(static argument => argument.TypeId).ToArray();
        return true;
    }

    private Dictionary<LocalId, MirCall?> GetDefiningCallsByLocal()
    {
        if (_definingCallsByLocal != null)
        {
            return _definingCallsByLocal;
        }

        var definitions = new Dictionary<LocalId, MirCall?>();
        foreach (var block in _function.BasicBlocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (instruction is not MirCall { Target: MirPlace { Kind: PlaceKind.Local, Local: var local } } call)
                {
                    continue;
                }

                definitions[local] = definitions.ContainsKey(local) ? null : call;
            }
        }

        _definingCallsByLocal = definitions;
        return definitions;
    }

    private bool TryResolveLocalAlias(LocalId local, out LocalId resolvedLocal)
    {
        resolvedLocal = local;
        var aliases = GetLocalAliasesByLocal();
        var definitions = GetDefiningCallsByLocal();
        var visited = new HashSet<int>();

        while (aliases.TryGetValue(resolvedLocal, out var nextLocal))
        {
            if (definitions.ContainsKey(resolvedLocal))
            {
                return false;
            }

            if (nextLocal == null ||
                !visited.Add(resolvedLocal.Value))
            {
                return false;
            }

            resolvedLocal = nextLocal.Value;
        }

        return true;
    }

    private Dictionary<LocalId, LocalId?> GetLocalAliasesByLocal()
    {
        if (_localAliasesByLocal != null)
        {
            return _localAliasesByLocal;
        }

        var aliases = new Dictionary<LocalId, LocalId?>();
        foreach (var block in _function.BasicBlocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (instruction is not MirAssign
                    {
                        Target: MirPlace { Kind: PlaceKind.Local, Local: var targetLocal },
                        Source: MirPlace { Kind: PlaceKind.Local, Local: var sourceLocal }
                    })
                {
                    continue;
                }

                aliases[targetLocal] = aliases.ContainsKey(targetLocal) ? null : sourceLocal;
            }
        }

        _localAliasesByLocal = aliases;
        return aliases;
    }
}

/// <summary>
/// Send 检查错误
/// </summary>
public sealed record SendCheckError(
    string Message,
    BlockId Block,
    int InstructionIndex);
