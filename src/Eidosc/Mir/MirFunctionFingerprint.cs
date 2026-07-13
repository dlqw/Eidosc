using System.Security.Cryptography;
using System.Text;
using Eidosc.Pipeline;

namespace Eidosc.Mir;

public sealed record MirFunctionFingerprint(
    string FunctionKey,
    string BodyHash,
    int BasicBlockCount,
    int InstructionCount,
    int LocalCount,
    int ParameterCount);

public sealed record MirFunctionFingerprintSnapshot(
    string SchemaVersion,
    IReadOnlyList<MirFunctionFingerprint> Functions)
{
    public const string CurrentSchemaVersion = "mir-function-fingerprint-snapshot-v1";

    public static MirFunctionFingerprintSnapshot FromModule(MirModule module) =>
        new(
            CurrentSchemaVersion,
            MirFunctionFingerprintBuilder.ComputeModule(module));

    public string ModuleFingerprint => ModuleArtifactHash.ComputeJsonHash(Functions);
}

public static class MirFunctionFingerprintBuilder
{
    private const string Schema = "mir-function-fingerprint-v1";

    public static MirFunctionFingerprint Compute(MirFunc function)
    {
        var writer = new HashWriter();
        writer.Add(Schema);
        writer.Add(MirFunctionIdentity.GetStableKey(function));
        writer.Add(function.ReturnType.Value);
        writer.Add(function.GenericParameterCount);
        writer.Add(function.IsRuntimeWordAbi);
        writer.Add(function.IsExternal);
        writer.Add(function.ExternalSymbolName ?? "");
        writer.Add(function.IntrinsicName ?? "");
        writer.Add((int)function.BuiltinIntrinsicRole);
        writer.Add(function.IsEntry);
        writer.Add(function.EntryBlockId.Value);

        writer.Add(function.GenericTypeParameterIds.Count);
        foreach (var typeParameterId in function.GenericTypeParameterIds)
        {
            writer.Add(typeParameterId.Value);
        }

        writer.Add(function.GenericParameters.Count);
        foreach (var parameter in function.GenericParameters)
        {
            writer.Add(parameter.ParameterIndex);
            writer.Add(parameter.SymbolId.Value);
            writer.Add(parameter.Name);
            writer.Add((int)parameter.ParameterKind);
            writer.Add(parameter.TypeId.Value);
        }

        var parameterCount = 0;
        writer.Add(function.Locals.Count);
        foreach (var local in function.Locals)
        {
            writer.Add(local.Id.Value);
            writer.Add(local.Name ?? "");
            writer.Add(local.TypeId.Value);
            writer.Add(local.IsParameter);
            writer.Add(local.IsMutable);
            writer.Add((int)local.BindingMode);
            if (local.IsParameter)
            {
                parameterCount++;
            }
        }

        var instructionCount = 0;
        writer.Add(function.BasicBlocks.Count);
        foreach (var block in function.BasicBlocks)
        {
            writer.Add(block.Id.Value);
            writer.Add(block.IsEntry);
            writer.Add(block.Instructions.Count);
            instructionCount += block.Instructions.Count;
            foreach (var instruction in block.Instructions)
            {
                AddInstruction(writer, instruction);
            }

            AddTerminator(writer, block.Terminator);
        }

        return new MirFunctionFingerprint(
            MirFunctionIdentity.GetStableKey(function),
            writer.ToHash(),
            function.BasicBlocks.Count,
            instructionCount,
            function.Locals.Count,
            parameterCount);
    }

    public static IReadOnlyList<MirFunctionFingerprint> ComputeModule(MirModule module)
    {
        return module.Functions
            .Select(Compute)
            .OrderBy(static fingerprint => fingerprint.FunctionKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddInstruction(HashWriter writer, MirInstruction instruction)
    {
        writer.Add(instruction.GetType().Name);
        switch (instruction)
        {
            case MirAssign assign:
                AddPlace(writer, assign.Target);
                AddOperand(writer, assign.Source);
                break;
            case MirCall call:
                AddPlace(writer, call.Target);
                AddOperand(writer, call.Function);
                AddOperands(writer, call.Arguments);
                writer.Add(call.IsTailCall);
                break;
            case MirBinOp binOp:
                AddOperand(writer, binOp.Target);
                writer.Add((int)binOp.Operator);
                AddOperand(writer, binOp.Left);
                AddOperand(writer, binOp.Right);
                break;
            case MirUnaryOp unaryOp:
                AddOperand(writer, unaryOp.Target);
                writer.Add((int)unaryOp.Operator);
                AddOperand(writer, unaryOp.Operand);
                break;
            case MirLoad load:
                AddPlace(writer, load.Target);
                AddOperand(writer, load.Source);
                writer.Add(load.IsMutableBorrow);
                writer.Add(load.CreatesBorrowAlias);
                break;
            case MirStore store:
                AddPlace(writer, store.Target);
                AddOperand(writer, store.Value);
                break;
            case MirDrop drop:
                AddOperand(writer, drop.Value);
                break;
            case MirCopy copy:
                AddPlace(writer, copy.Target);
                AddPlace(writer, copy.Source);
                break;
            case MirMove move:
                AddPlace(writer, move.Target);
                AddPlace(writer, move.Source);
                break;
            case MirAlloc alloc:
                AddPlace(writer, alloc.Target);
                writer.Add(alloc.TypeId.Value);
                break;
        }
    }

    private static void AddTerminator(HashWriter writer, MirTerminator? terminator)
    {
        writer.Add(terminator?.GetType().Name ?? "<null>");
        switch (terminator)
        {
            case null:
                break;
            case MirReturn ret:
                AddOperand(writer, ret.Value);
                break;
            case MirGoto goTo:
                writer.Add(goTo.Target.Value);
                break;
            case MirSwitch @switch:
                AddOperand(writer, @switch.Discriminant);
                writer.Add(@switch.Branches.Count);
                foreach (var branch in @switch.Branches)
                {
                    AddOperand(writer, branch.Value);
                    writer.Add(branch.Target.Value);
                    writer.Add(branch.BoundVariable?.Value ?? 0);
                }

                writer.Add(@switch.DefaultTarget?.Value ?? 0);
                break;
            case MirUnreachable:
                break;
        }
    }

    private static void AddOperands(HashWriter writer, IReadOnlyList<MirOperand> operands)
    {
        writer.Add(operands.Count);
        foreach (var operand in operands)
        {
            AddOperand(writer, operand);
        }
    }

    private static void AddOperand(HashWriter writer, MirOperand? operand)
    {
        writer.Add(operand?.GetType().Name ?? "<null>");
        if (operand == null)
        {
            return;
        }

        writer.Add(operand.TypeId.Value);
        switch (operand)
        {
            case MirPoison poison:
                writer.Add(poison.Reason);
                break;
            case MirConstant constant:
                AddConstant(writer, constant.Value);
                break;
            case MirConstGenericValue constGeneric:
                writer.Add(constGeneric.SymbolId.Value);
                writer.Add(constGeneric.Name);
                writer.Add(constGeneric.ParameterIndex);
                break;
            case MirFunctionRef functionRef:
                writer.Add(MirFunctionIdentity.GetStableKey(functionRef));
                writer.Add(functionRef.Name);
                writer.Add((int)functionRef.SymbolKind);
                writer.Add(functionRef.SignatureTypeId.Value);
                writer.Add(functionRef.TypeArgumentIds.Count);
                foreach (var typeArgumentId in functionRef.TypeArgumentIds)
                {
                    writer.Add(typeArgumentId.Value);
                }

                writer.Add(functionRef.ValueArguments.Count);
                foreach (var argument in functionRef.ValueArguments)
                {
                    writer.Add(argument.ParameterIndex);
                    writer.Add(argument.CanonicalText);
                    writer.Add(argument.CanonicalHash);
                    writer.Add(argument.DisplayText);
                    writer.Add(argument.TypeId.Value);
                    writer.Add(argument.ReferencedParameterIndex);
                    writer.Add(argument.ValueVariableIndex);
                }

                writer.Add(functionRef.TraitOwnerId.Value);
                writer.Add((int)functionRef.TraitSelfPosition);
                writer.Add(functionRef.TraitSelfParameterIndices.Count);
                foreach (var index in functionRef.TraitSelfParameterIndices)
                {
                    writer.Add(index);
                }

                writer.Add(functionRef.TraitSelfInResult);
                writer.Add((int)functionRef.TraitMethodRole);
                break;
            case MirPlace place:
                AddPlaceBody(writer, place);
                break;
            case MirTemp temp:
                writer.Add(temp.Id.Value);
                break;
        }
    }

    private static void AddPlace(HashWriter writer, MirPlace? place)
    {
        AddOperand(writer, place);
    }

    private static void AddPlaceBody(HashWriter writer, MirPlace place)
    {
        writer.Add((int)place.Kind);
        writer.Add(place.Local.Value);
        AddPlace(writer, place.Base);
        writer.Add(place.FieldName ?? "");
        AddOperand(writer, place.Index);
        writer.Add((int)place.IndexAccessKind);
    }

    private static void AddConstant(HashWriter writer, MirConstantValue? constant)
    {
        writer.Add(constant?.GetType().Name ?? "<null>");
        switch (constant)
        {
            case null:
                break;
            case MirConstantValue.IntValue value:
                writer.Add(value.Value);
                break;
            case MirConstantValue.FloatValue value:
                writer.Add(BitConverter.DoubleToInt64Bits(value.Value));
                break;
            case MirConstantValue.StringValue value:
                writer.Add(value.Value);
                break;
            case MirConstantValue.RawStringValue value:
                writer.Add(value.Value);
                break;
            case MirConstantValue.CharValue value:
                writer.Add((int)value.Value);
                break;
            case MirConstantValue.BoolValue value:
                writer.Add(value.Value);
                break;
            case MirConstantValue.UnitValue:
                break;
        }
    }

    private sealed class HashWriter
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public void Add(string value)
        {
            _hash.AppendData(Encoding.UTF8.GetBytes(value));
            _hash.AppendData([0]);
        }

        public void Add(int value) => Add(value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        public void Add(long value) => Add(value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        public void Add(bool value) => Add(value ? "1" : "0");

        public string ToHash() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
    }
}
